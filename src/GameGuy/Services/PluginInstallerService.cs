using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using GameGuy.Models;

namespace GameGuy.Services;

/// <summary>Queried state of the installed plugin vs. the latest GitHub release.</summary>
public sealed record PluginStatus(
    bool FrontendInstalled,
    bool DllInstalled,
    bool DllMatches,       // every loader slot's sha256 == its latest release asset digest
    string? InstalledTag,  // from the on-disk manifest (null if never installed / no manifest)
    string? LatestTag,
    bool UpdateAvailable,
    bool MillenniumPresent,
    bool Offline,          // couldn't reach GitHub
    bool Port8080Busy);    // something other than Steam's own CDP server is listening on CDP's fixed port. Warn only, see IsPort8080BusyAsync

/// <summary>
/// Installs / updates / removes the GameGuy store-page plugin from GitHub releases (the app is the plugin
/// MANAGER: it doesn't bundle the frontend). Each release of <c>madoiscool/LTSP</c> carries
/// <c>plugin.zip</c> (the frontend, extracted to %AppData%\GameGuy\plugin, where CefInjectorService
/// reads it) plus one loader DLL per <see cref="Slots"/> entry, dropped into the Steam install root
/// (steam.exe loads it). Modeled on <see cref="UnlockerService"/>: fetch release JSON, download assets
/// via <see cref="GithubProxy"/> (mirror fallback), verify each by its sha256 digest, then place. The DLL
/// is locked while Steam runs, so DLL install/uninstall stops Steam first (via <see cref="SteamService"/>)
/// and relaunches it if it was up.
///
/// CDP itself (the debug bridge <see cref="CefInjectorService"/> connects through) is NOT opened by the
/// DLL anymore: install/uninstall also manages a `.cef-enable-remote-debugging` NTFS junction next to
/// Steam's exe, which makes Steam self-enable CDP on its own (fixed port 8080, confirmed not
/// configurable; verified on both Windows 10 and 11). That leaves the DLL with one remaining job:
/// "launch GameGuy.exe when Steam opens", with no CDP hook, no load-timing race, and no dual-slot
/// redundancy needed anymore.
/// </summary>
public class PluginInstallerService(SteamService steam, GithubProxy gh, CefInjectorService injector)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private const string PluginZipAsset = "plugin.zip";

    /// <summary>The one DLL-proxy slot the loader ships as. <c>winmm.dll</c> is loaded dynamically (audio)
    /// by steam.exe, is never a KnownDLL on Win10 or Win11, and isn't claimed by Millennium (wsock32/
    /// version), or OpenSteamTool (dwmapi/xinput). Its old weakness (load timing isn't guaranteed relative
    /// to steamwebhelper's launch) no longer matters now that CDP is opened by the junction instead of a
    /// hook this DLL installs: there's no launch to catch a deadline for anymore, just "eventually load
    /// while Steam is running." Other slots were each dead ends: bcrypt is KnownDLLs-forced on Win10,
    /// and psapi and dbghelp don't load reliably enough.</summary>
    private sealed record LoaderSlot(string DllAsset, string RealName, string SystemSourceName)
    {
        public string SystemSourcePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), SystemSourceName);
    }

    private static readonly LoaderSlot[] Slots =
    {
        new("winmm.dll", "winmm_real.dll", "winmm.dll"),
    };

    // Old slots to clean up on install/update: if left in the Steam root they'd load and run the loader
    // payload an extra time (double GameGuy launch), and bcrypt/dbghelp specifically would still install
    // their own now-obsolete CreateProcessInternalW hook from the old shipped binary. Covers the shipped
    // bcrypt dual-slot build and the psapi/dbghelp test builds some users received during this
    // investigation. *_real are their forwarding companions.
    private static readonly string[] LegacyDllNames =
        { "bcrypt.dll", "bcrypt_real.dll", "psapi.dll", "dbghelp.dll", "dbghelp_real.dll" };

    // ── CDP marker junction ──────────────────────────────────────────────────
    // `.cef-enable-remote-debugging` next to Steam's exe, created as an NTFS JUNCTION (not a plain file)
    // pointing at a target that's deliberately chosen to never exist. Steam's own internal logic treats
    // its mere presence as "self-enable CDP debugging" (fixed port 8080) without needing to resolve the
    // target, but Millennium's cleanup code (health_check.cc) calls `std::filesystem::exists()` first,
    // which DOES try to resolve the junction, fails since the target is nonexistent, and reports false,
    // so Millennium's removal code never even runs. A plain file does not survive that cleanup; the
    // junction does. Verified live this session on both Windows 10 and 11, including with a real
    // Millennium instance loaded. `mklink /j` is shelled out to because directory junctions (unlike
    // symlinks) have no native .NET creation API; `rmdir` (not Directory.Delete) is used to remove it,
    // avoiding historical .NET/junction-deletion quirks that can recurse into a junction's target instead
    // of just severing the link.
    private const string CdpMarkerName = ".cef-enable-remote-debugging";
    private const string CdpMarkerJunctionTarget = @"C:\fuckass\folder\that\shall\never\exist\die\millennium";
    private const int CdpPort = 8080; // Steam's own fixed default: confirmed not configurable via the marker's content.
    private string? CdpMarkerPath => SteamDir is { } s ? Path.Combine(s, CdpMarkerName) : null;

    /// <summary>True only if <paramref name="path"/> is already a real reparse point (junction/symlink).
    /// NOT just "something exists there". A bare Exists check used to treat a stale plain file (from before
    /// this session's junction fix) or a leftover plain directory (e.g. a partially-failed rmdir) as
    /// "already present" and permanently skip creating a real junction, silently leaving CDP broken for
    /// that install forever (nothing else ever re-touches the marker once the plugin's up to date; see
    /// CreateCdpMarkerJunction's own doc comment).</summary>
    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (Directory.Exists(path) || File.Exists(path))
                && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch { return false; }
    }

    /// <summary>Idempotent, self-healing: safe to call on every status check, not just on install. If a
    /// REAL junction is already there, no-op. If something else is there (a stale plain marker file from
    /// before this session's junction fix, or any other leftover cruft that isn't actually a reparse
    /// point), it's cleared first, since a plain file at this path doesn't survive Millennium's cleanup and
    /// silently breaks CDP forever otherwise.</summary>
    private static void CreateCdpMarkerJunction(string path)
    {
        if (IsReparsePoint(path)) return;
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch { /* best effort; mklink below just no-ops/fails harmlessly if this didn't clear it */ }

        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /j \"{path}\" \"{CdpMarkerJunctionTarget}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = System.Diagnostics.Process.Start(psi);
        p?.WaitForExit(5000);
    }

    private static void RemoveCdpMarkerJunction(string path)
    {
        if (!Directory.Exists(path)) return;
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c rmdir \"{path}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = System.Diagnostics.Process.Start(psi);
        p?.WaitForExit(5000);
    }

    private static readonly HttpClient PortProbeHttp = AppHttp.Create(TimeSpan.FromMilliseconds(800));

    /// <summary>Best-effort check for whether CDP's fixed port is occupied by something OTHER than Steam's
    /// own CDP server. A bare bind-test can't make that distinction: once the junction is doing its job and
    /// CDP is actually up, Steam itself is the one holding the port, so a bind fails for the exact same
    /// reason a hostile squatter would cause it to fail. The two are indistinguishable to
    /// <see cref="System.Net.Sockets.TcpListener"/>. This produced a live false positive (warning fired
    /// against Steam's own working CDP server). Fixed by, on a bind failure, asking whatever's on the port
    /// for <c>/json</c>. CDP answers with a JSON array of debug targets; only treat it as "busy" in the
    /// user-facing sense if that probe does NOT look like CDP. The port can't be changed regardless
    /// (confirmed: file content has no effect on it), so this stays detection-only. A false
    /// positive/negative here never blocks install/uninstall.</summary>
    private static async Task<bool> IsPort8080BusyAsync()
    {
        try
        {
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, CdpPort);
            listener.Start();
            listener.Stop();
            return false; // nothing bound there
        }
        catch (System.Net.Sockets.SocketException)
        {
            try
            {
                var body = await PortProbeHttp.GetStringAsync($"http://127.0.0.1:{CdpPort}/json");
                return !body.TrimStart().StartsWith('['); // CDP's /json answers with a JSON array of targets
            }
            catch { return true; } // bound, but not answering like CDP -> genuinely something else
        }
        catch { return false; }
    }

    private static string FrontendDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppConfig.DataDirName, "plugin");
    private static string LuatoolsJsPath => Path.Combine(FrontendDir, "public", "luatools.js");
    private static string ManifestPath => Path.Combine(FrontendDir, "installed.json");

    private string? SteamDir => steam.EffectivePath;
    private string? SlotPath(LoaderSlot slot) => SteamDir is { } s ? Path.Combine(s, slot.DllAsset) : null;
    private string? SlotRealPath(LoaderSlot slot) => SteamDir is { } s ? Path.Combine(s, slot.RealName) : null;
    private IEnumerable<string> LegacyDllPaths =>
        SteamDir is { } s ? LegacyDllNames.Select(n => Path.Combine(s, n)) : Enumerable.Empty<string>();

    // ── DLL-update testing switch ──
    // Drop `.gameguy-dll-update-disabled` in the Steam root to stop the app from replacing any installed
    // loader slot (auto OR manual), so hand-placed test builds survive updates. Delete the file to resume
    // normal DLL updates. Frontend updates are unaffected. Re-read on every update, so toggling needs no
    // restart. Mirrors the DLL's own `.gameguy-cdp-hook-disabled` marker idiom.
    private const string DllUpdateDisabledMarker = ".gameguy-dll-update-disabled";
    private bool DllUpdateDisabled =>
        SteamDir is { } s && File.Exists(Path.Combine(s, DllUpdateDisabledMarker));

    private GithubRelease? _cachedLatest;

    public bool MillenniumPresent =>
        SteamDir is { } s && File.Exists(Path.Combine(s, "millennium", "lib", "millennium.dll"));

    // ── manifest (records the installed tag + asset hashes, for status/update detection) ──
    private sealed class Manifest
    {
        public string? Tag { get; set; }
        public Dictionary<string, string>? DllShas { get; set; } // DllAsset name -> sha256, one per slot
        public string? ZipSha { get; set; }
        /// <summary>Millennium config path → the exact enabledPlugins entries we removed there on install,
        /// so uninstall can restore Millennium's gameguy plugin verbatim.</summary>
        public Dictionary<string, List<string>>? DisabledMillenniumEntries { get; set; }
    }

    private static Manifest? ReadManifest()
    {
        try
        {
            return File.Exists(ManifestPath)
                ? JsonSerializer.Deserialize<Manifest>(File.ReadAllText(ManifestPath), JsonOpts)
                : null;
        }
        catch { return null; }
    }

    private static void WriteManifest(Manifest m)
    {
        Directory.CreateDirectory(FrontendDir);
        File.WriteAllText(ManifestPath, JsonSerializer.Serialize(m));
    }

    // ── GitHub ──
    public async Task<GithubRelease?> FetchLatestAsync(bool force, CancellationToken ct = default)
    {
        if (!force && _cachedLatest is not null) return _cachedLatest;
        string url = $"https://api.github.com/repos/{AppConfig.PluginReleasesOwner}/{AppConfig.PluginReleasesRepo}/releases/latest";
        try
        {
            using var res = await gh.SendAsync(url, ct);
            if (res is null || !res.IsSuccessStatusCode) return null;
            var rel = JsonSerializer.Deserialize<GithubRelease>(await res.Content.ReadAsStringAsync(ct), JsonOpts);
            if (rel is not null) _cachedLatest = rel;
            return rel;
        }
        catch { return null; }
    }

    /// <summary>Fast, network-free check: the plugin frontend + a loader slot are both present. Used by the
    /// first-run onboarding gate (no GitHub round-trip, unlike <see cref="GetStatusAsync"/>).</summary>
    public bool IsInstalledLocally() =>
        File.Exists(LuatoolsJsPath)
        && (Slots.Any(s => SlotPath(s) is { } p && File.Exists(p)) || LegacyDllPaths.Any(File.Exists));

    public async Task<PluginStatus> GetStatusAsync(bool force = false, CancellationToken ct = default)
    {
        bool frontend = File.Exists(LuatoolsJsPath);
        // "installed" if AT LEAST ONE slot's proxy is present. A partial/mid-migration state still counts
        // as installed and eligible for auto-update, rather than showing "not installed". An OLD loader
        // (psapi/dbghelp) also still counts. Otherwise a user who hasn't migrated shows "not installed"
        // and the auto-update gate (UpdateAvailable) never fires, stranding them on the dead loader.
        bool anySlotPresent = Slots.Any(slot => SlotPath(slot) is { } p && File.Exists(p));
        bool legacy = LegacyDllPaths.Any(File.Exists);
        bool loader = anySlotPresent || legacy;

        // Self-heal the CDP junction on every status check, not just when InstallAsync happens to run.
        // InstallAsync only fires on a fresh install or when a version bump makes UpdateAvailable true. Once
        // the plugin is fully up to date, nothing else ever re-touches the marker. If it's ever removed after
        // that point (inconsistent Millennium-version cleanup, AV quarantining the reparse point, a Steam
        // repair, manual deletion), CDP silently stops working and the ONLY thing that used to fix it was a
        // full uninstall+reinstall (forces InstallAsync unconditionally). Exactly the workaround users have
        // been reporting. GetStatusAsync runs on every Steam-open poke, so checking here closes that gap
        // continuously instead of only at version-bump time. Cheap when already correct (single attribute
        // check, no shellout) and only touches the loader DLL is actually installed.
        if (loader && CdpMarkerPath is { } liveMarkerPath)
            CreateCdpMarkerJunction(liveMarkerPath);

        bool port8080Busy = await IsPort8080BusyAsync();
        var manifest = ReadManifest();
        var latest = await FetchLatestAsync(force, ct);

        if (latest is null)
            return new PluginStatus(frontend, loader, false, manifest?.Tag, null, UpdateAvailable: false,
                MillenniumPresent, Offline: true, port8080Busy);

        // dllMatches = true only when EVERY slot's proxy is present and matches its release asset digest.
        bool dllMatches = Slots.All(slot =>
            SlotPath(slot) is { } p && File.Exists(p) &&
            AssetDigest(latest, slot.DllAsset) is { } digest &&
            AssetHash.OfFile(p) == digest);
        bool installed = frontend && loader;
        // `|| legacy` keeps a leftover/locked legacy dll getting swept on subsequent auto-updates until gone.
        bool updateAvailable = installed && (manifest?.Tag != latest.TagName || !dllMatches || legacy);

        return new PluginStatus(frontend, loader, dllMatches, manifest?.Tag, latest.TagName, updateAvailable,
            MillenniumPresent, Offline: false, port8080Busy);
    }

    // ── Install / update ──
    public async Task<(bool ok, string? error)> InstallAsync(IProgress<double?>? progress, CancellationToken ct = default)
    {
        if (SteamDir is not { } steamDir) return (false, Resources.Strings.Plugin_Err_SteamNotFound);

        var latest = await FetchLatestAsync(force: true, ct);
        if (latest is null) return (false, Resources.Strings.Plugin_Err_GithubUnreachable);

        var zipAsset = FindAsset(latest, PluginZipAsset);
        if (zipAsset is null)
            return (false, string.Format(Resources.Strings.Plugin_Err_MissingAssets, latest.TagName, PluginZipAsset, Slots[0].DllAsset));
        var slotAssets = new Dictionary<LoaderSlot, GithubAsset>();
        foreach (var slot in Slots)
        {
            if (FindAsset(latest, slot.DllAsset) is not { } asset)
                return (false, string.Format(Resources.Strings.Plugin_Err_MissingAssets, latest.TagName, PluginZipAsset, slot.DllAsset));
            slotAssets[slot] = asset;
        }

        string tmp = Path.Combine(Path.GetTempPath(), "gameguy-plugin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        Dictionary<string, List<string>>? disabledMillenniumEntries = null;
        try
        {
            string zipPath = Path.Combine(tmp, PluginZipAsset);
            await gh.DownloadAsync(zipAsset.DownloadUrl, zipPath, progress, ct);
            var slotDlPaths = new Dictionary<LoaderSlot, string>();
            foreach (var (slot, asset) in slotAssets)
            {
                string p = Path.Combine(tmp, slot.DllAsset);
                await gh.DownloadAsync(asset.DownloadUrl, p, progress, ct);
                slotDlPaths[slot] = p;
            }

            // Verify each against its release asset digest before touching anything on disk.
            string zipSha = AssetHash.OfFile(zipPath);
            if (AssetDigest(latest, PluginZipAsset) is { } zd && zipSha != zd)
                return (false, string.Format(Resources.Strings.Plugin_Err_VerifyFailed, PluginZipAsset));
            var slotShas = new Dictionary<LoaderSlot, string>();
            foreach (var (slot, p) in slotDlPaths)
            {
                string sha = AssetHash.OfFile(p);
                slotShas[slot] = sha;
                if (AssetDigest(latest, slot.DllAsset) is { } dd && sha != dd)
                    return (false, string.Format(Resources.Strings.Plugin_Err_VerifyFailed, slot.DllAsset));
            }

            // 1) Frontend → %AppData%\GameGuy\plugin (fresh).
            if (Directory.Exists(FrontendDir)) Directory.Delete(FrontendDir, recursive: true);
            Directory.CreateDirectory(FrontendDir);
            ZipFile.ExtractToDirectory(zipPath, FrontendDir);
            NormalizeFrontendLayout();
            if (!File.Exists(LuatoolsJsPath))
                return (false, Resources.Strings.Plugin_Err_NoLuatoolsJs);

            // Get the frontend live in THIS running process immediately. Don't wait on the Steam restart
            // below. A relaunched GameGuy.exe would hit the single-instance mutex against this very
            // process (the one the user is using right now to click Install) and exit quietly without ever
            // taking over, so nothing would otherwise pick up the new file until a manual app restart.
            await injector.ReloadPluginFilesAsync();

            // 2) Loader DLLs → Steam root, but ONLY when at least one slot actually changed, OR a legacy
            //    slot is still present and must be removed. Both slots are always installed/updated
            //    together (never partially out of date relative to each other). The DLLs are locked while
            //    Steam runs, so either condition means stopping+restarting Steam; a frontend-only update
            //    (the common case) skips all of that and applies with zero Steam disruption.
            // Testing switch: when `.gameguy-dll-update-disabled` is present, never touch any on-disk DLL
            // (so hand-placed test builds aren't clobbered), and thus never stop/restart Steam for it either.
            bool legacyPresent = LegacyDllPaths.Any(File.Exists);
            bool anySlotNeedsUpdate = Slots.Any(slot =>
                SlotPath(slot) is not { } cur || !File.Exists(cur) || AssetHash.OfFile(cur) != slotShas[slot]);
            bool dllNeedsUpdate = !DllUpdateDisabled && (anySlotNeedsUpdate || legacyPresent);
            if (dllNeedsUpdate)
            {
                bool wasRunning = Process.GetProcessesByName("steam").Length > 0;
                steam.StopSteam();
                await Task.Delay(1200, ct); // let file handles on the loader DLLs release after the kill

                foreach (var slot in Slots)
                {
                    File.Copy(slotDlPaths[slot], Path.Combine(steamDir, slot.DllAsset), overwrite: true);
                    // Each proxy forwards to <name>_real.dll. A copy of the machine's own matching
                    // System32 file. Refresh it on every DLL update so it always matches the current OS build.
                    if (SlotRealPath(slot) is { } real && File.Exists(slot.SystemSourcePath))
                        File.Copy(slot.SystemSourcePath, real, overwrite: true);
                }
                // Remove any legacy slot (psapi/dbghelp + their _real). Leaving one would run the loader
                // payload an extra time (double GameGuy launch / CDP hook).
                foreach (var legacy in LegacyDllPaths)
                    if (File.Exists(legacy)) { try { File.Delete(legacy); } catch { /* locked/again next time */ } }

                // 3) A live Millennium gameguy plugin would inject the frontend redundantly. Disable it in
                //    Millennium's config (reversibly) so LuaLoader is the sole injector (leaves the Millennium
                //    mod itself alone). Steam is stopped here, so the edit can't be clobbered and takes effect
                //    on the restart below. Also migrate away any leftover folder-rename from older builds.
                if (MillenniumPresent)
                {
                    RestoreMillenniumPluginFolder(steamDir);
                    disabledMillenniumEntries = SetMillenniumLuatoolsEnabled(enable: false);
                }

                if (wasRunning) steam.StartSteam();
            }

            // Ensure the CDP marker junction exists: independent of whether the DLL itself changed (a
            // filesystem-only op, doesn't need Steam stopped). Needed here for the very first install (before
            // any GetStatusAsync call would see `loader` true); GetStatusAsync's own check is what keeps this
            // self-healing on every subsequent Steam-open, not just at install/update time. See its
            // declaration above for why this MUST be a junction, not a file.
            if (CdpMarkerPath is { } markerPath)
                CreateCdpMarkerJunction(markerPath);

            WriteManifest(new Manifest
            {
                Tag = latest.TagName,
                DllShas = slotShas.ToDictionary(kv => kv.Key.DllAsset, kv => kv.Value),
                ZipSha = zipSha,
                DisabledMillenniumEntries = disabledMillenniumEntries,
            });
            return (true, null);
        }
        catch (Exception ex) { return (false, ex.Message); }
        finally { try { Directory.Delete(tmp, recursive: true); } catch { /* temp cleanup */ } }
    }

    /// <summary>Silent auto-update: if an update is available for an already-installed plugin, apply it
    /// (frontend silently; DLL change stops/swaps/restarts Steam). Returns true if an update was applied.
    /// Fire-and-forget safe: swallows offline/errors. Called from the app's Steam-open update flow.</summary>
    public async Task<bool> AutoUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            var st = await GetStatusAsync(force: true, ct);
            if (!st.UpdateAvailable) return false; // not installed, offline, or already current
            var (ok, _) = await InstallAsync(progress: null, ct);
            return ok;
        }
        catch { return false; }
    }

    // ── Uninstall ──
    public Task<(bool ok, string? error)> UninstallAsync(CancellationToken ct = default)
    {
        return Task.Run(async () =>
        {
            try
            {
                // Read the manifest BEFORE the FrontendDir delete below wipes it. It holds the exact
                // enabledPlugins entries we stripped from Millennium's config at install time.
                var manifest = ReadManifest();

                bool wasRunning = Process.GetProcessesByName("steam").Length > 0;
                steam.StopSteam();
                await Task.Delay(1200, ct);

                foreach (var slot in Slots)
                {
                    if (SlotPath(slot) is { } bp && File.Exists(bp)) File.Delete(bp);
                    if (SlotRealPath(slot) is { } br && File.Exists(br)) File.Delete(br);
                }
                foreach (var legacy in LegacyDllPaths)
                    if (File.Exists(legacy)) File.Delete(legacy);
                if (CdpMarkerPath is { } markerPath) RemoveCdpMarkerJunction(markerPath);
                if (Directory.Exists(FrontendDir)) Directory.Delete(FrontendDir, recursive: true);

                // Give Millennium its gameguy plugin back: we're the ones who disabled it. Steam is
                // stopped here, so the edit sticks and applies on the restart below.
                if (MillenniumPresent)
                    SetMillenniumLuatoolsEnabled(enable: true, restore: manifest?.DisabledMillenniumEntries);

                // Stop injecting the now-deleted content immediately, same reasoning as InstallAsync.
                await injector.ReloadPluginFilesAsync();

                if (wasRunning) steam.StartSteam();
                return (true, (string?)null);
            }
            catch (Exception ex) { return (false, (string?)ex.Message); }
        }, ct);
    }

    // ── Millennium coexistence: disable its gameguy plugin via config (reversible), not folder-rename ──

    private const string MillenniumPluginName = "gameguy";
    private const string MillenniumDisabledName = MillenniumPluginName + ".disabled-by-gameguy";

    /// <summary>Options for rewriting Millennium's config. The <see cref="DefaultJsonTypeInfoResolver"/> is
    /// REQUIRED, not cosmetic: re-enabling adds CLR-backed <see cref="JsonValue"/> nodes (from plain strings),
    /// and serializing those through custom options without a resolver throws
    /// "JsonSerializerOptions instance must specify a TypeInfoResolver". Removal-only writes happen to work
    /// without it (all surviving nodes are JsonElement-backed from Parse), so the failure would only ever
    /// surface on uninstall. Silently, inside the best-effort catch.</summary>
    private static readonly JsonSerializerOptions ConfigWriteOpts = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    /// <summary>Every place Millennium might keep its config.json, newest first. Legacy Millennium used
    /// <c>&lt;steam&gt;\ext\config.json</c>; current uses <c>&lt;steam&gt;\millennium\config\config.json</c>;
    /// an intermediate build used <c>&lt;steam&gt;\millennium\config.json</c>. A <c>MILLENNIUM__CONFIG_PATH</c>
    /// env var overrides the directory. Both real files can coexist, so callers edit *every* one present.</summary>
    private IEnumerable<string> MillenniumConfigPaths()
    {
        if (SteamDir is not { } s) yield break;
        yield return Path.Combine(s, "millennium", "config", "config.json");
        yield return Path.Combine(s, "millennium", "config.json");
        yield return Path.Combine(s, "ext", "config.json");
        var env = Environment.GetEnvironmentVariable("MILLENNIUM__CONFIG_PATH");
        if (!string.IsNullOrWhiteSpace(env))
            yield return Path.Combine(env, "config.json");
    }

    /// <summary>True if an enabledPlugins entry refers to our plugin. The entry's leading space-delimited
    /// token is the plugin id; Millennium versions store it as <c>gameguy</c>, <c>gameguy LUA</c>
    /// (name + backend), or (from the old rename) <c>gameguy.disabled-by-gameguy[ LUA]</c>.</summary>
    private static bool IsLuatoolsEntry(string entry)
    {
        string token = entry.Split(' ', 2)[0];
        return token.Equals(MillenniumPluginName, StringComparison.OrdinalIgnoreCase)
            || token.Equals(MillenniumDisabledName, StringComparison.OrdinalIgnoreCase);
    }

    private static JsonArray? NestedEnabled(JsonObject root) =>
        root["plugins"] is JsonObject p ? p["enabledPlugins"] as JsonArray : null;
    private static JsonArray? FlatEnabled(JsonObject root) =>
        root["plugins.enabledPlugins"] as JsonArray; // stray dotted key some Millennium builds also write

    /// <summary>Remove (enable=false) or restore (enable=true) our plugin from <c>plugins.enabledPlugins</c>
    /// in every Millennium config that exists. On disable returns configPath → the exact entry strings
    /// removed, so uninstall can restore them verbatim (passed back in as <paramref name="restore"/>).
    /// Best-effort per file; edits a JSON DOM so all unrelated keys/plugins are preserved.</summary>
    private Dictionary<string, List<string>> SetMillenniumLuatoolsEnabled(
        bool enable, IReadOnlyDictionary<string, List<string>>? restore = null)
    {
        var removedMap = new Dictionary<string, List<string>>();
        foreach (string path in MillenniumConfigPaths())
        {
            try
            {
                if (!File.Exists(path)) continue;
                if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root) continue;
                bool changed = false;

                if (!enable)
                {
                    // Record only the nested (real) array's removals for restore; the flat dotted key is a
                    // stray duplicate some builds write. Scrub it, but never restore into it.
                    var removed = new List<string>();
                    if (NestedEnabled(root) is { } nested)
                        for (int i = nested.Count - 1; i >= 0; i--)
                            if (nested[i]?.GetValue<string>() is { } v && IsLuatoolsEntry(v))
                            {
                                removed.Add(v);
                                nested.RemoveAt(i);
                                changed = true;
                            }
                    if (FlatEnabled(root) is { } flat)
                        for (int i = flat.Count - 1; i >= 0; i--)
                            if (flat[i]?.GetValue<string>() is { } v && IsLuatoolsEntry(v))
                            {
                                flat.RemoveAt(i);
                                changed = true;
                            }
                    if (removed.Count > 0)
                    {
                        removed.Reverse(); // preserve original order
                        removedMap[path] = removed;
                    }
                }
                else
                {
                    var arr = NestedEnabled(root); // restore only into the real nested array
                    if (arr is not null && !arr.Any(n => n?.GetValue<string>() is { } v && IsLuatoolsEntry(v)))
                    {
                        var toAdd = restore is not null && restore.TryGetValue(path, out var r) && r.Count > 0
                            ? r
                            : new List<string> { MillenniumPluginName };
                        foreach (string e in toAdd) arr.Add(e);
                        changed = true;
                    }
                }

                if (changed) File.WriteAllText(path, root.ToJsonString(ConfigWriteOpts));
            }
            catch { /* best effort per file. A locked/invalid config must not fail install/uninstall */ }
        }
        return removedMap;
    }

    /// <summary>Undo the previous folder-rename approach: bring <c>plugins\gameguy</c> back to a normal
    /// state so only config governs enablement. If both the real and the renamed-aside folder exist
    /// (Millennium recreated it), drop the stale aside copy; otherwise rename the aside copy back.</summary>
    private static void RestoreMillenniumPluginFolder(string steamDir)
    {
        try
        {
            string dir = Path.Combine(steamDir, "millennium", "plugins");
            string real = Path.Combine(dir, MillenniumPluginName);
            string aside = Path.Combine(dir, MillenniumDisabledName);
            if (!Directory.Exists(aside)) return;
            if (Directory.Exists(real)) Directory.Delete(aside, recursive: true);
            else Directory.Move(aside, real);
        }
        catch { /* best effort */ }
    }

    /// <summary>If plugin.zip wrapped everything in a single top-level folder, hoist that folder's contents
    /// up so <c>public/luatools.js</c> sits directly under FrontendDir.</summary>
    private static void NormalizeFrontendLayout()
    {
        if (Directory.Exists(Path.Combine(FrontendDir, "public"))) return;
        foreach (var sub in Directory.GetDirectories(FrontendDir))
        {
            if (!Directory.Exists(Path.Combine(sub, "public"))) continue;
            foreach (var entry in Directory.GetFileSystemEntries(sub))
            {
                string dest = Path.Combine(FrontendDir, Path.GetFileName(entry));
                if (Directory.Exists(entry)) Directory.Move(entry, dest);
                else File.Move(entry, dest, overwrite: true);
            }
            try { Directory.Delete(sub, recursive: true); } catch { /* leftover wrapper */ }
            return;
        }
    }

    // ── Helpers (same shape as UnlockerService's) ──
    private static string? AssetDigest(GithubRelease r, string name) =>
        AssetHash.ParseDigest(r.Assets.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Digest);

    private static GithubAsset? FindAsset(GithubRelease r, string name) =>
        r.Assets.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

}
