using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using LuaToolsGui.Models;

namespace LuaToolsGui.Services;

/// <summary>
/// Manages the mutually-exclusive Steam unlockers (OpenSteamTools / BetterSteamTools / Custom). Only
/// one is active at a time. Each managed mode resolves its own build, verifies files by sha256, and
/// installs into the Steam root; Custom downloads and verifies nothing, since the user owns those
/// files. Switching overwrites shared files but doesn't delete the previous mode's leftovers. The
/// active mode persists in settings.
/// </summary>
public class UnlockerService(SteamService steam, SettingsService settings, CacheService cache, GithubProxy gh, BackupService backup)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // Per-mode cache of the GitHub release so re-opening the page doesn't hammer the API
    // (unauthenticated GitHub allows only 60 req/hr per IP). The "Check for updates" button forces a
    // fresh fetch (30s cooldown) for anyone who wants certainty sooner.
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private readonly Dictionary<UnlockerMode, (GithubRelease release, DateTime fetchedAt)> _releaseCache = new();
    private readonly Dictionary<UnlockerMode, (UpdateManifest manifest, DateTime fetchedAt)> _manifestCache = new();

    /// <summary>BetterSteamTools publishes its version + payload hash here instead of via the releases
    /// API. See <see cref="FetchUpdateManifestAsync"/>.</summary>
    private const string BstManifestUrl =
        "https://raw.githubusercontent.com/madoiscool/BetterSteamTools/refs/heads/updates/opensteamtool/latest.toml";

    public IReadOnlyList<ModeDefinition> Modes { get; } =
    [
        // The nightly channel of upstream OpenSteamTool, built from main into our own OST-Nightly repo.
        // Carries native CloudRedirect support (see the add-on below).
        new(UnlockerMode.Ost, "OpenSteamTools",
            Description: Resources.Strings.Mode_Desc_Ost,
            Kind: ModeKind.Zip,
            Owner: "madoiscool", Repo: "OST-Nightly",
            FixedTag: null,
            PlaceFiles: ["dwmapi.dll", "xinput1_4.dll", "OpenSteamTool.dll"],
            ZipAssetPattern: "OpenSteamTool-{version}-Release.zip"),

        // Our fork of OpenSteamTool. The dll/zip identifiers stay the upstream "OpenSteamTool" names.
        // They're real download and file targets inherited from the fork, and renaming them breaks
        // install. Only the mode's DisplayName is the new brand.
        new(UnlockerMode.Bst, "BetterSteamTools",
            Description: Resources.Strings.Mode_Desc_Bst,
            Kind: ModeKind.Zip,
            Owner: "madoiscool", Repo: "BetterSteamTools",
            FixedTag: null,
            PlaceFiles: ["dwmapi.dll", "xinput1_4.dll", "OpenSteamTool.dll"],
            ZipAssetPattern: "OpenSteamTool-{version}-Release.zip",
            UpdateManifestUrl: BstManifestUrl),

        // Opt-out: the user installs and updates their own unlocker, and we place/verify nothing.
        new(UnlockerMode.Custom, Resources.Strings.Mode_Name_Custom,
            Description: Resources.Strings.Mode_Desc_Custom,
            Kind: ModeKind.Manual,
            Owner: "", Repo: "",
            FixedTag: null,
            PlaceFiles: [],
            ZipAssetPattern: null),
    ];

    private ModeDefinition Def(UnlockerMode mode) => Modes.First(m => m.Mode == mode);

    /// <summary>The currently-active mode (the last one installed/selected), or null if none yet.</summary>
    public UnlockerMode? SelectedMode =>
        Enum.TryParse(settings.SelectedMode, out UnlockerMode m) ? m : null;

    /// <summary>Short display name of the active mode for status UI; null if none selected/detected yet.</summary>
    public string? SelectedModeDisplayName =>
        SelectedMode is { } m ? Def(m).DisplayName : null;

    /// <summary>
    /// Make sure the active OST/BST install is watching <c>config/stplug-in</c>, so luas written there
    /// hot-reload instead of needing a Steam restart.
    /// </summary>
    /// <remarks>
    /// The app no longer tells users to restart Steam after a lua change, because OST/BST re-read any
    /// directory listed in <c>opensteamtool.toml</c>'s <c>[lua] paths</c>. That makes this registration
    /// load-bearing rather than a nicety: previously it ran only inside <see cref="InstallAsync"/>, so a
    /// user who set their unlocker up outside this app got neither hot-reload nor restart advice.
    ///
    /// Safe to call repeatedly — the underlying edit is targeted, comment-preserving and append-only, and
    /// no-ops when the path is already present. Skipped for <c>Custom</c>, whose unlocker we know nothing
    /// about, and when no mode is selected.
    /// </remarks>
    public void EnsureLuaPathRegistered()
    {
        if (SelectedMode is not (UnlockerMode.Ost or UnlockerMode.Bst)) return;
        if (steam.EffectivePath is not { } root) return;
        try { EnsureOpenSteamToolLuaPath(root); } catch { /* config tweak is best-effort */ }
    }

    // ── State query ─────────────────────────────────────────────────

    /// <summary>Query GitHub + local files → this mode's status. Returns Unknown on any failure/offline.
    /// Cached briefly unless <paramref name="forceRefresh"/>.</summary>
    public async Task<ModeState> GetStateAsync(UnlockerMode mode, bool forceRefresh = false, CancellationToken ct = default)
    {
        var def = Def(mode);
        bool active = SelectedMode == mode;

        // Custom: the user owns their files. Nothing to fetch, nothing to compare.
        if (def.Kind == ModeKind.Manual)
            return new ModeState(mode, ModeStatus.UserManaged, active, null);

        string? root = steam.EffectivePath;
        if (root is null || !steam.IsValid)
            return new ModeState(mode, ModeStatus.Unknown, active, null);

        // BST publishes version + payload hash in a raw-hosted manifest, no releases API call.
        if (def.UpdateManifestUrl is not null)
        {
            var manifest = await FetchUpdateManifestAsync(def, forceRefresh, ct);
            if (manifest is null) return new ModeState(mode, ModeStatus.Unknown, active, null);
            return new ModeState(mode, ManifestStatus(manifest, root), active, manifest.Version);
        }

        // OST: recognise BOTH channels. An exact match against the nightly release means up to date;
        // an exact match against the stable "ost-" mirror means the user is on stable OST, which is
        // still OST, but this mode ships nightly, so offer them the move.
        if (mode == UnlockerMode.Ost)
        {
            var (ostStatus, latestTag) = await OstStatusAsync(def, root, forceRefresh, ct);
            return new ModeState(mode, ostStatus, active, latestTag);
        }

        return new ModeState(mode, ModeStatus.Unknown, active, null);
    }

    /// <summary>
    /// Status for a manifest-backed mode: the manifest names one payload file and its hash (for BST,
    /// OpenSteamTool.dll: the real change indicator; dwmapi/xinput are loaders that rarely move, so
    /// they're placed but not compared).
    /// </summary>
    private static ModeStatus ManifestStatus(UpdateManifest manifest, string root)
    {
        string local = Path.Combine(root, manifest.File);
        if (!File.Exists(local)) return ModeStatus.NotInstalled;
        return AssetHash.OfFile(local).Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase)
            ? ModeStatus.UpToDate
            : ModeStatus.UpdateAvailable;
    }

    /// <summary>
    /// OST status across both channels. Nightly builds differ per build in OpenSteamTool.dll, so that's
    /// the nightly indicator; stable OST is detected via the mendy-tools "ost-" mirror's per-DLL hashes
    /// (upstream only publishes a zip digest, not per-file ones).
    /// </summary>
    private async Task<(ModeStatus status, string? latestTag)> OstStatusAsync(
        ModeDefinition def, string root, bool forceRefresh, CancellationToken ct)
    {
        var nightly = await FetchReleaseAsync(def, forceRefresh, ct);
        string ostDll = Path.Combine(root, "OpenSteamTool.dll");

        if (nightly is not null && File.Exists(ostDll)
            && AssetDigest(nightly, "OpenSteamTool.dll") == AssetHash.OfFile(ostDll))
            return (ModeStatus.UpToDate, nightly.TagName);

        // Not the current nightly. Fall back to the stable mirror to tell "on stable OST" apart from
        // "nothing installed": both end up as UpdateAvailable, but only the former is really OST.
        var (mirrorStatus, mirrorTag) = await OstMirrorStatusAsync(root, ct);
        string? tag = nightly?.TagName ?? mirrorTag;

        if (mirrorStatus == ModeStatus.NotInstalled && !File.Exists(ostDll))
            return (ModeStatus.NotInstalled, tag);
        if (nightly is null && mirrorStatus == ModeStatus.Unknown)
            return (ModeStatus.Unknown, tag);
        return (ModeStatus.UpdateAvailable, tag);
    }

    private const string MirrorRepoOwner = "mendy-tools";
    private const string MirrorRepo = "verynotsusdllsthataredefnotstrelated";

    /// <summary>
    /// OpenSteamTools status via the mendy-tools "ost-" mirror (real per-DLL hashes). Hash the on-disk
    /// dwmapi.dll against the mirror: matches the LATEST ost- release (by published_at) → UpToDate;
    /// matches an older ost- release (or files present but no match) → UpdateAvailable; absent → NotInstalled.
    /// Returns the latest ost- tag for display.
    /// </summary>
    private async Task<(ModeStatus status, string? latestTag)> OstMirrorStatusAsync(string root, CancellationToken ct)
    {
        string dwmapi = Path.Combine(root, "dwmapi.dll");
        if (!File.Exists(dwmapi)) return (ModeStatus.NotInstalled, null);

        var releases = await FetchAllReleasesAsync(MirrorRepoOwner, MirrorRepo, null, ct);
        if (releases is null) return (ModeStatus.Unknown, null);

        var ost = releases.Where(r => r.TagName.StartsWith("ost-", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.PublishedAt ?? DateTimeOffset.MinValue).ToList();
        if (ost.Count == 0) return (ModeStatus.Unknown, null);

        var latest = ost[0];
        string dwmHash = AssetHash.OfFile(dwmapi);

        if (AssetDigest(latest, "dwmapi.dll") == dwmHash) return (ModeStatus.UpToDate, latest.TagName);
        // Matches an older ost- release, or is present but unrecognized → an update exists.
        return (ModeStatus.UpdateAvailable, latest.TagName);
    }

    // ── Revert to vanilla ───────────────────────────────────────────

    /// <summary>True when a revert has something to undo: any mode-managed artifact is present in
    /// the Steam root. Works with OR without a backup — without one (installs made before backups
    /// existed), the mode-placed files are removed instead of restored. Cheap, disk-only.</summary>
    public bool CanRevert => ModeArtifactsPresent().Count > 0;

    /// <summary>Human-readable backup summary for the Mode page. With a backup: when it was taken +
    /// how many files. Without one: a note that the files will be removed rather than restored.</summary>
    public string? BackupSummary
    {
        get
        {
            var info = backup.GetInfo();
            return info.HasBackup
                ? $"{Resources.Strings.Mode_Backup_Summary} · {info.CreatedText} · {info.FileCount}"
                : Resources.Strings.Mode_Backup_Summary_None;
        }
    }

    /// <summary>Mode-managed artifacts currently present in the Steam root: the loader DLLs, the
    /// payload DLL, CloudRedirect and opensteamtool.toml. Used to decide if a revert has work to do.</summary>
    private List<string> ModeArtifactsPresent()
    {
        var present = new List<string>();
        string? root = steam.EffectivePath;
        if (root is null) return present;

        foreach (string f in new[]
                 {
                     BackupService.OpenSteamToolDll,
                     BackupService.CloudRedirectDll,
                     "dwmapi.dll", "xinput1_4.dll", "opensteamtool.toml",
                 })
            if (File.Exists(Path.Combine(root, f))) present.Add(f);
        return present;
    }

    /// <summary>
    /// Revert Steam to its pre-LuaTools state: Steam must already be stopped (the caller owns the
    /// close/relaunch choreography). Restores every backed-up file (verified), removes mode payload
    /// DLLs and opensteamtool.toml (restored by the backup when it existed before us), clears the
    /// selected mode, and optionally cleans game luas + pinned manifests.
    /// </summary>
    /// <param name="cleanLua">Also delete &lt;Steam&gt;/config/stplug-in/*.lua (game unlocks).</param>
    /// <param name="cleanManifests">Also delete &lt;Steam&gt;/config/depotcache/*.manifest (pins).</param>
    public ModeInstallResult Revert(bool cleanLua = false, bool cleanManifests = false)
    {
        string? root = steam.EffectivePath;
        if (root is null || !steam.IsValid)
            return ModeInstallResult.Fail(Resources.Strings.Err_SteamNotFound);

        var failed = new List<string>();

        // 1. Restore backed-up files (loader DLLs + opensteamtool.toml as they were before us).
        //    No backup (e.g. the mode was installed before backups existed) → the loader DLLs were
        //    placed by the mode, so deleting them IS the revert: absent proxies, steam.exe loads the
        //    real system DLLs from System32. (opensteamtool.toml is handled by step 3 below.)
        if (backup.HasBackup)
            failed.AddRange(backup.RestoreBackedUpFiles());
        else
            foreach (string f in new[] { "dwmapi.dll", "xinput1_4.dll" })
            {
                try { string p = Path.Combine(root, f); if (File.Exists(p)) File.Delete(p); }
                catch { failed.Add(f); }
            }

        // 2. Remove mode payload files that have no backup to restore over them.
        foreach (string f in new[] { BackupService.OpenSteamToolDll, BackupService.CloudRedirectDll })
        {
            try
            {
                string p = Path.Combine(root, f);
                // Only delete when it's NOT covered by the backup (the restore already overwrote it).
                if (backup.BackedUpFiles().Any(b => b.Equals(f, StringComparison.OrdinalIgnoreCase))) continue;
                if (File.Exists(p)) File.Delete(p);
            }
            catch { failed.Add(f); }
        }

        // 3. opensteamtool.toml: if the backup has no copy, the file was created by us → delete it.
        try
        {
            string toml = Path.Combine(root, "opensteamtool.toml");
            if (!backup.BackedUpFiles().Any(b => b.Equals("opensteamtool.toml", StringComparison.OrdinalIgnoreCase))
                && File.Exists(toml))
                File.Delete(toml);
        }
        catch { failed.Add("opensteamtool.toml"); }

        // 4. Optional: remove game luas (the user's unlocks).
        if (cleanLua)
        {
            try
            {
                string? dir = steam.StPlugInDir;
                if (dir is not null && Directory.Exists(dir))
                    foreach (string f in Directory.GetFiles(dir, "*.lua"))
                        File.Delete(f);
            }
            catch { failed.Add("*.lua"); }
        }

        // 5. Optional: remove pinned manifests.
        if (cleanManifests)
        {
            try
            {
                string? dir = steam.DepotCacheDir;
                if (dir is not null && Directory.Exists(dir))
                    foreach (string f in Directory.GetFiles(dir, "*.manifest"))
                        File.Delete(f);
            }
            catch { failed.Add("*.manifest"); }
        }

        // 6. Clear the active mode so the app reflects vanilla state (onboarding re-opens).
        settings.SelectedMode = null;
        cache.OpenSteamToolsInstalledVersion = null;
        cache.OpenSteamToolsInstalledZipDigest = null;

        return failed.Count > 0
            ? new ModeInstallResult(false,
                string.Format(Resources.Strings.Err_WriteFailedCount, failed.Count), failed)
            : ModeInstallResult.Ok();
    }

    /// <summary>Delete stale lua files from stplug-in. Returns count removed (for UI), -1 on failure.
    /// Used by the revert flow when the user opts into lua cleanup.</summary>
    public int CleanLuaFiles()
    {
        try
        {
            string? dir = steam.StPlugInDir;
            if (dir is null || !Directory.Exists(dir)) return 0;
            int n = 0;
            foreach (string f in Directory.GetFiles(dir, "*.lua")) { File.Delete(f); n++; }
            return n;
        }
        catch { return -1; }
    }

    // ── Install / switch ─────────────────────────────────────────────

    /// <summary>Download + verify a mode's files, place them in the Steam root, remove the other mode's
    /// unique files, and persist the selection. Best-effort per file (locked files land in Failed).</summary>
    public async Task<ModeInstallResult> InstallAsync(
        UnlockerMode mode, IProgress<double?>? progress = null, CancellationToken ct = default)
    {
        var def = Def(mode);

        // Custom: selecting it is the whole operation. Nothing is downloaded, nothing is written to
        // the Steam root, and whatever the user has installed is left exactly as it is.
        if (def.Kind == ModeKind.Manual)
        {
            settings.SelectedMode = mode.ToString();
            return ModeInstallResult.Ok();
        }

        string? root = steam.EffectivePath;
        if (root is null || !steam.IsValid)
            return ModeInstallResult.Fail(Resources.Strings.Err_SteamNotFound);

        // Resolve the build to install: manifest-backed modes (BST) name their own version and payload
        // hash; the rest use the same (cached) release the card's status was based on, so what installs
        // matches what was shown.
        GithubRelease? release = null;
        UpdateManifest? manifest = null;
        string? version;
        if (def.UpdateManifestUrl is not null)
        {
            manifest = await FetchUpdateManifestAsync(def, forceRefresh: false, ct);
            if (manifest is null)
                return ModeInstallResult.Fail(Resources.Strings.Err_UpdateServerUnreachable);
            version = manifest.Version;
        }
        else
        {
            release = await FetchReleaseAsync(def, forceRefresh: false, ct);
            if (release is null)
                return ModeInstallResult.Fail(Resources.Strings.Err_GithubUnreachable);
            version = release.TagName;
        }

        string staging = Path.Combine(Path.GetTempPath(), "LuaToolsGui", "mode", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);

            // 1. Stage + verify into temp.
            Dictionary<string, string> staged; // filename → staged path
            string? zipDigest = null;
            {
                // Manifest modes build the asset URL from the reported version; release modes read it
                // off the release. Either way we land on the same "<name>-<version>-Release.zip" shape.
                string zipUrl, zipName;
                string? wantedZipDigest = null;
                if (manifest is not null)
                {
                    zipName = (def.ZipAssetPattern ?? "").Replace("{version}", manifest.Version);
                    zipUrl = $"https://github.com/{def.Owner}/{def.Repo}/releases/download/{manifest.Version}/{zipName}";
                }
                else
                {
                    var asset = FindZipAsset(def, release!);
                    if (asset is null) return ModeInstallResult.Fail(Resources.Strings.Err_ReleaseMissingDownload);
                    zipName = asset.Name;
                    zipUrl = asset.DownloadUrl;
                    wantedZipDigest = AssetHash.ParseDigest(asset.Digest);
                }

                string zipPath = Path.Combine(staging, zipName);
                await DownloadToFileAsync(zipUrl, zipPath, progress, ct);

                zipDigest = AssetHash.OfFile(zipPath);
                if (wantedZipDigest is { } want && !zipDigest.Equals(want, StringComparison.OrdinalIgnoreCase))
                    return ModeInstallResult.Fail(Resources.Strings.Err_VerifyFailed);

                staged = ExtractWanted(zipPath, def.PlaceFiles, staging);
                var missing = def.PlaceFiles.Where(f => !staged.ContainsKey(f)).ToList();
                if (missing.Count > 0)
                    return ModeInstallResult.Fail(string.Format(Resources.Strings.Err_DownloadMissingFiles, string.Join(", ", missing)));

                // Manifest modes don't publish a zip digest, so verify the payload file the manifest
                // DOES vouch for, once it's out of the archive.
                if (manifest is not null && staged.TryGetValue(manifest.File, out string? payload)
                    && !AssetHash.OfFile(payload).Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                    return ModeInstallResult.Fail(string.Format(Resources.Strings.Err_VerifyFailedFile, manifest.File));
            }

            // 2. SAFETY BACKUP (one-time): before the first managed install ever writes to the Steam
            //    root, snapshot the current loader DLLs + opensteamtool.toml so a later revert can
            //    restore this exact state. Never blocks install on failure.
            try { backup.BackupIfNeeded(); } catch { /* best-effort safety net */ }

            // 3. Copy verified files into the Steam root (overwrite). Locked files → Failed (Steam running).
            var failed = new List<string>();
            foreach (string file in def.PlaceFiles)
            {
                try
                {
                    string dest = Path.Combine(root, file);
                    File.Copy(staged[file], dest, overwrite: true);
                    StampNow(dest);
                }
                catch
                {
                    failed.Add(file);
                }
            }

            // 4. This mode is now the active one. (No cleanup of other modes' files. Just overwrite.)
            settings.SelectedMode = mode.ToString();

            // Record the installed zip digest/version for reference (the up-to-date check uses per-DLL
            // hashes, not this). Both remaining install modes are OpenSteamTool-derived, so both want
            // their config pointed at stplug-in.
            cache.OpenSteamToolsInstalledZipDigest = zipDigest;
            cache.OpenSteamToolsInstalledVersion = version;
            try { EnsureOpenSteamToolLuaPath(root); } catch { /* config tweak is best-effort */ }

            return failed.Count > 0
                ? new ModeInstallResult(false, string.Format(Resources.Strings.Err_WriteFailedCount, failed.Count), failed)
                : ModeInstallResult.Ok();
        }
        catch (OperationCanceledException)
        {
            return ModeInstallResult.Fail(Resources.Strings.Err_Cancelled);
        }
        catch (Exception ex)
        {
            return ModeInstallResult.Fail(ex.Message);
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch { /* best effort */ }
        }
    }

    // ── First-run auto-detect ────────────────────────────────────────

    /// <summary>
    /// One-time detection of an already-installed mode when none is selected yet. Hashes the on-disk
    /// DLLs against published digests, in priority order:
    ///   1. Bst: OpenSteamTool.dll vs the BST update manifest's sha256.
    ///   2. Ost (nightly): OpenSteamTool.dll vs any madoiscool/OST-Nightly release asset.
    ///   3. Ost (stable): dwmapi.dll AND xinput1_4.dll vs mendy-tools tag "ost-" (loose-DLL mirror;
    ///      OST ships a zip whose API digest isn't per-DLL, so we mirror the DLLs for hash-matching).
    ///      Still OST, just the other channel: GetStateAsync will offer the move to nightly.
    ///
    /// EVERY branch requires an EXACT hash match. Do not relax this to "the file exists": SteamTools
    /// shipped the same dwmapi.dll / xinput1_4.dll filenames, so a presence check would silently claim
    /// ex-SteamTools users as OST. Exactly the users ModeMigration deliberately routes to onboarding.
    /// Never auto-selects <see cref="UnlockerMode.Custom"/>; that's an explicit user choice.
    ///
    /// Persists the match as the active mode. Returns the detected mode, or null if nothing matched.
    /// </summary>
    public async Task<UnlockerMode?> DetectActiveModeAsync(CancellationToken ct = default)
    {
        string? root = steam.EffectivePath;
        if (root is null || !steam.IsValid) return null;

        UnlockerMode? detected = null;

        // The loader DLLs (dwmapi/xinput) are often byte-identical across builds, so the payload
        // OpenSteamTool.dll is what actually distinguishes BST from OST-nightly.
        string ostDll = Path.Combine(root, "OpenSteamTool.dll");
        if (File.Exists(ostDll))
        {
            string ostHash = AssetHash.OfFile(ostDll);

            var bstManifest = await FetchUpdateManifestAsync(Def(UnlockerMode.Bst), forceRefresh: false, ct);
            if (bstManifest is not null
                && bstManifest.File.Equals("OpenSteamTool.dll", StringComparison.OrdinalIgnoreCase)
                && ostHash.Equals(bstManifest.Sha256, StringComparison.OrdinalIgnoreCase))
                detected = UnlockerMode.Bst;

            if (detected is null)
            {
                var nightly = await FetchAllReleasesAsync("madoiscool", "OST-Nightly", null, ct);
                if (nightly is not null && nightly.Any(r => AssetDigest(r, "OpenSteamTool.dll") == ostHash))
                    detected = UnlockerMode.Ost;
            }
        }

        // Stable OST via the loose-DLL mirror. Both DLLs must be present and each must hash-match SOME
        // ost- release. The two ship in SEPARATE releases, so they're matched independently.
        if (detected is null)
        {
            string dwmapi = Path.Combine(root, "dwmapi.dll");
            string xinput = Path.Combine(root, "xinput1_4.dll");
            if (File.Exists(dwmapi) && File.Exists(xinput))
            {
                var mirror = await FetchAllReleasesAsync(MirrorRepoOwner, MirrorRepo, null, ct);
                var tagged = mirror?
                    .Where(r => r.TagName.StartsWith("ost-", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (tagged is { Count: > 0 })
                {
                    string dwmHash = AssetHash.OfFile(dwmapi);
                    string xinHash = AssetHash.OfFile(xinput);
                    if (tagged.Any(r => AssetDigest(r, "dwmapi.dll") == dwmHash)
                        && tagged.Any(r => AssetDigest(r, "xinput1_4.dll") == xinHash))
                        detected = UnlockerMode.Ost;
                }
            }
        }

        if (detected is { } m) settings.SelectedMode = m.ToString();
        return detected;
    }

    /// <summary>Digest (hex, no prefix) of a release's same-named asset, or null if absent.</summary>
    private static string? AssetDigest(GithubRelease r, string assetName) =>
        AssetHash.ParseDigest(r.Assets.FirstOrDefault(a => a.Name.Equals(assetName, StringComparison.OrdinalIgnoreCase))?.Digest);

    /// <summary>The same-named asset, or null if this release doesn't have it.</summary>
    private static GithubAsset? FindAsset(GithubRelease r, string assetName) =>
        r.Assets.FirstOrDefault(a => a.Name.Equals(assetName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Fetch every release for a repo (per_page=100). If <paramref name="tag"/> is set, only
    /// that one release (wrapped in a list). Null on failure/offline.</summary>
    private async Task<List<GithubRelease>?> FetchAllReleasesAsync(string owner, string repo, string? tag, CancellationToken ct)
    {
        string url = tag is not null
            ? $"https://api.github.com/repos/{owner}/{repo}/releases/tags/{tag}"
            : $"https://api.github.com/repos/{owner}/{repo}/releases?per_page=100";
        try
        {
            // Routed via GithubProxy: direct, then mirrors (for blocked/throttled regions).
            using var res = await gh.SendAsync(url, ct);
            if (res is null || !res.IsSuccessStatusCode) return null;
            string body = await res.Content.ReadAsStringAsync(ct);
            if (tag is not null)
            {
                var one = JsonSerializer.Deserialize<GithubRelease>(body, JsonOpts);
                return one is null ? null : [one];
            }
            return JsonSerializer.Deserialize<List<GithubRelease>>(body, JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    // ── OpenSteamTool config ─────────────────────────────────────────

    private const string OstLuaPath = "config/stplug-in";

    /// <summary>
    /// Ensure &lt;Steam&gt;/opensteamtool.toml's [lua] paths array contains "config/stplug-in" so our luas
    /// are loaded. Creates the file/section/array if missing; appends without removing existing paths.
    /// Targeted text edit (preserves comments and other sections). Commented-out lines are ignored.
    /// </summary>
    private static void EnsureOpenSteamToolLuaPath(string steamRoot)
    {
        string tomlPath = Path.Combine(steamRoot, "opensteamtool.toml");

        // No file → create a minimal one.
        if (!File.Exists(tomlPath))
        {
            File.WriteAllText(tomlPath, $"[lua]\npaths = [\"{OstLuaPath}\"]\n");
            return;
        }

        var lines = File.ReadAllLines(tomlPath).ToList();

        // Find the active (uncommented) [lua] section header and the bounds of that section.
        int luaHeader = lines.FindIndex(l => IsActiveTableHeader(l, "lua"));
        if (luaHeader < 0)
        {
            // No active [lua] section → append one.
            if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
            lines.Add("[lua]");
            lines.Add($"paths = [\"{OstLuaPath}\"]");
            File.WriteAllLines(tomlPath, lines);
            return;
        }

        // Section runs until the next active table header (or EOF).
        int sectionEnd = lines.FindIndex(luaHeader + 1, IsActiveAnyTableHeader);
        if (sectionEnd < 0) sectionEnd = lines.Count;

        // Look for an active `paths` key within the section. The array may span multiple lines.
        int pathsStart = -1;
        for (int i = luaHeader + 1; i < sectionEnd; i++)
        {
            string t = lines[i].TrimStart();
            if (t.StartsWith('#')) continue;                       // commented → ignore
            if (Regex.IsMatch(t, @"^paths\s*=")) { pathsStart = i; break; }
        }

        if (pathsStart < 0)
        {
            // [lua] exists but no active paths key → insert one right under the header.
            lines.Insert(luaHeader + 1, $"paths = [\"{OstLuaPath}\"]");
            File.WriteAllLines(tomlPath, lines);
            return;
        }

        // Find where the array closes (']'), scanning from pathsStart (handles multi-line arrays).
        int pathsEnd = pathsStart;
        while (pathsEnd < sectionEnd && !lines[pathsEnd].Contains(']')) pathsEnd++;
        if (pathsEnd >= sectionEnd) pathsEnd = sectionEnd - 1; // malformed/unclosed. Best effort

        string block = string.Join("\n", lines.GetRange(pathsStart, pathsEnd - pathsStart + 1));

        // Already present (compare the path token, slashes normalized)? Nothing to do.
        if (Regex.IsMatch(block, @"[""']\s*" + Regex.Escape(OstLuaPath).Replace("/", @"[/\\]+") + @"\s*[""']",
                RegexOptions.IgnoreCase))
            return;

        // Insert our entry just before the closing ']' on the line that has it.
        int closeLine = pathsEnd;
        string line = lines[closeLine];
        int bracket = line.LastIndexOf(']');

        // Insert our entry just before the ']'. Add a comma after existing content unless the array
        // is empty (text before ']' ends right after the opening '[').
        string before = line[..bracket].TrimEnd();
        bool arrayEmpty = Regex.IsMatch(before, @"\[\s*$");
        string newBefore = arrayEmpty
            ? before + $" \"{OstLuaPath}\""
            : before + $", \"{OstLuaPath}\"";
        lines[closeLine] = newBefore + line[bracket..];

        File.WriteAllLines(tomlPath, lines);
    }

    /// <summary>True if the line is an active (uncommented) [name] table header.</summary>
    private static bool IsActiveTableHeader(string line, string name)
    {
        string t = line.TrimStart();
        return !t.StartsWith('#') && Regex.IsMatch(t, $@"^\[\s*{Regex.Escape(name)}\s*\]");
    }

    /// <summary>True if the line is any active (uncommented) [..] table header.</summary>
    private static bool IsActiveAnyTableHeader(string line)
    {
        string t = line.TrimStart();
        return !t.StartsWith('#') && Regex.IsMatch(t, @"^\[[^\[].*\]");
    }

    // ── CloudRedirect add-on (a feature of the OpenSteamTool Nightly build) ──────────
    // Not a mutually-exclusive mode: it drops cloud_redirect.dll into the Steam root and toggles
    // [cloud] enabled in opensteamtool.toml (parallel to how BST install writes [lua] paths). Only
    // meaningful when the Nightly BST mode is active.

    private const string CloudRedirectDll = "cloud_redirect.dll";
    private (GithubRelease release, DateTime fetchedAt)? _crReleaseCache;

    private async Task<GithubRelease?> FetchCloudRedirectReleaseAsync(bool forceRefresh, CancellationToken ct)
    {
        if (!forceRefresh && _crReleaseCache is { } c && DateTime.UtcNow - c.fetchedAt < CacheTtl)
            return c.release;

        string url = $"https://api.github.com/repos/{AppConfig.CloudRedirectRepo}/releases/latest";
        try
        {
            using var res = await gh.SendAsync(url, ct);
            if (res is null || !res.IsSuccessStatusCode) return null;
            var release = JsonSerializer.Deserialize<GithubRelease>(await res.Content.ReadAsStringAsync(ct), JsonOpts);
            if (release is not null) _crReleaseCache = (release, DateTime.UtcNow);
            return release;
        }
        catch { return null; }
    }

    /// <summary>Add-on state from disk (dll present + [cloud] enabled) plus, when <paramref name="checkUpdate"/>
    /// and installed, whether a newer cloud_redirect.dll is published.</summary>
    public async Task<CloudRedirectAddonState> GetCloudRedirectStateAsync(
        bool checkUpdate, bool forceRefresh = false, CancellationToken ct = default)
    {
        string? root = steam.EffectivePath;
        if (root is null || !steam.IsValid)
            return new CloudRedirectAddonState(false, false, false, null);

        string dll = Path.Combine(root, CloudRedirectDll);
        bool installed = File.Exists(dll);
        bool enabled = ReadOpenSteamToolCloudEnabled(root);

        bool updateAvailable = false;
        string? latest = null;
        if (checkUpdate && installed)
        {
            var release = await FetchCloudRedirectReleaseAsync(forceRefresh, ct);
            if (release is not null)
            {
                latest = release.TagName;
                string? wanted = AssetDigest(release, CloudRedirectDll);
                if (wanted is not null && !AssetHash.OfFile(dll).Equals(wanted, StringComparison.OrdinalIgnoreCase))
                    updateAvailable = true;
            }
        }
        return new CloudRedirectAddonState(installed, enabled, updateAvailable, latest);
    }

    /// <summary>Enable: download cloud_redirect.dll if missing (verified), then set [cloud] enabled = true.
    /// Takes effect on the next Steam launch.</summary>
    public async Task<ModeInstallResult> EnableCloudRedirectAsync(IProgress<double?>? progress = null, CancellationToken ct = default)
    {
        string? root = steam.EffectivePath;
        if (root is null || !steam.IsValid)
            return ModeInstallResult.Fail(Resources.Strings.Err_SteamNotFound);

        if (!File.Exists(Path.Combine(root, CloudRedirectDll)))
        {
            var dl = await DownloadCloudRedirectDllAsync(root, progress, ct);
            if (!dl.Success) return dl;
        }
        try { SetOpenSteamToolCloudEnabled(root, true); }
        catch (Exception ex) { return ModeInstallResult.Fail(ex.Message); }
        return ModeInstallResult.Ok();
    }

    /// <summary>Disable: flip [cloud] enabled = false (keeps the dll on disk).</summary>
    public ModeInstallResult DisableCloudRedirect()
    {
        string? root = steam.EffectivePath;
        if (root is null || !steam.IsValid)
            return ModeInstallResult.Fail(Resources.Strings.Err_SteamNotFound);
        try { SetOpenSteamToolCloudEnabled(root, false); return ModeInstallResult.Ok(); }
        catch (Exception ex) { return ModeInstallResult.Fail(ex.Message); }
    }

    /// <summary>Update: replace cloud_redirect.dll with the latest (verified). Fails with a "close Steam"
    /// message if Steam holds the existing dll open.</summary>
    public async Task<ModeInstallResult> UpdateCloudRedirectAsync(IProgress<double?>? progress = null, CancellationToken ct = default)
    {
        string? root = steam.EffectivePath;
        if (root is null || !steam.IsValid)
            return ModeInstallResult.Fail(Resources.Strings.Err_SteamNotFound);
        return await DownloadCloudRedirectDllAsync(root, progress, ct);
    }

    private async Task<ModeInstallResult> DownloadCloudRedirectDllAsync(string root, IProgress<double?>? progress, CancellationToken ct)
    {
        var release = await FetchCloudRedirectReleaseAsync(forceRefresh: true, ct);
        if (release is null) return ModeInstallResult.Fail(Resources.Strings.Err_GithubUnreachable);
        var asset = FindAsset(release, CloudRedirectDll);
        if (asset is null) return ModeInstallResult.Fail(string.Format(Resources.Strings.Err_ReleaseMissingFile, CloudRedirectDll));

        string staging = Path.Combine(Path.GetTempPath(), "LuaToolsGui", "cloud", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            string tmp = Path.Combine(staging, CloudRedirectDll);
            await DownloadToFileAsync(asset.DownloadUrl, tmp, progress, ct);
            if (AssetHash.ParseDigest(asset.Digest) is { } want && !AssetHash.OfFile(tmp).Equals(want, StringComparison.OrdinalIgnoreCase))
                return ModeInstallResult.Fail(string.Format(Resources.Strings.Err_VerifyFailedFile, CloudRedirectDll));

            try
            {
                string dest = Path.Combine(root, CloudRedirectDll);
                File.Copy(tmp, dest, overwrite: true);
                StampNow(dest);
            }
            catch
            {
                // Steam has the loaded dll locked: surface a close-Steam message (same as mode install).
                return ModeInstallResult.Fail(string.Format(Resources.Strings.Err_WriteFailedFile, CloudRedirectDll));
            }
            return ModeInstallResult.Ok();
        }
        catch (OperationCanceledException) { return ModeInstallResult.Fail(Resources.Strings.Err_Cancelled); }
        catch (Exception ex) { return ModeInstallResult.Fail(ex.Message); }
        finally { try { Directory.Delete(staging, recursive: true); } catch { /* best effort */ } }
    }

    /// <summary>Ensure opensteamtool.toml has an active [cloud] section with enabled = true|false. Mirrors
    /// EnsureOpenSteamToolLuaPath's targeted, comment-preserving editing.</summary>
    private static void SetOpenSteamToolCloudEnabled(string steamRoot, bool enabled)
    {
        string tomlPath = Path.Combine(steamRoot, "opensteamtool.toml");
        string val = enabled ? "true" : "false";

        if (!File.Exists(tomlPath))
        {
            File.WriteAllText(tomlPath, $"[cloud]\nenabled = {val}\n");
            return;
        }

        var lines = File.ReadAllLines(tomlPath).ToList();

        int header = lines.FindIndex(l => IsActiveTableHeader(l, "cloud"));
        if (header < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
            lines.Add("[cloud]");
            lines.Add($"enabled = {val}");
            File.WriteAllLines(tomlPath, lines);
            return;
        }

        int sectionEnd = lines.FindIndex(header + 1, IsActiveAnyTableHeader);
        if (sectionEnd < 0) sectionEnd = lines.Count;

        for (int i = header + 1; i < sectionEnd; i++)
        {
            string t = lines[i].TrimStart();
            if (t.StartsWith('#')) continue;                       // commented → ignore
            if (Regex.IsMatch(t, @"^enabled\s*="))
            {
                string indent = lines[i][..(lines[i].Length - lines[i].TrimStart().Length)];
                lines[i] = $"{indent}enabled = {val}";
                File.WriteAllLines(tomlPath, lines);
                return;
            }
        }

        // [cloud] exists but no active enabled key → insert one under the header.
        lines.Insert(header + 1, $"enabled = {val}");
        File.WriteAllLines(tomlPath, lines);
    }

    /// <summary>Read opensteamtool.toml's active [cloud] enabled value (false if the file/section/key is
    /// absent).</summary>
    private static bool ReadOpenSteamToolCloudEnabled(string steamRoot)
    {
        string tomlPath = Path.Combine(steamRoot, "opensteamtool.toml");
        if (!File.Exists(tomlPath)) return false;

        var lines = File.ReadAllLines(tomlPath);
        int header = Array.FindIndex(lines, l => IsActiveTableHeader(l, "cloud"));
        if (header < 0) return false;

        for (int i = header + 1; i < lines.Length; i++)
        {
            if (IsActiveAnyTableHeader(lines[i])) break;            // next section → done
            string t = lines[i].TrimStart();
            if (t.StartsWith('#')) continue;                       // commented → ignore
            var m = Regex.Match(t, @"^enabled\s*=\s*(\w+)");
            if (m.Success) return m.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private async Task<GithubRelease?> FetchReleaseAsync(ModeDefinition def, bool forceRefresh, CancellationToken ct)
    {
        // Serve from cache within the TTL unless a forced refresh is requested.
        if (!forceRefresh
            && _releaseCache.TryGetValue(def.Mode, out var cached)
            && DateTime.UtcNow - cached.fetchedAt < CacheTtl)
            return cached.release;

        string url = def.FixedTag is not null
            ? $"https://api.github.com/repos/{def.Owner}/{def.Repo}/releases/tags/{def.FixedTag}"
            : $"https://api.github.com/repos/{def.Owner}/{def.Repo}/releases/latest";
        try
        {
            // Routed via GithubProxy: direct, then mirrors (for blocked/throttled regions).
            using var res = await gh.SendAsync(url, ct);
            if (res is null || !res.IsSuccessStatusCode) return null;
            var release = JsonSerializer.Deserialize<GithubRelease>(await res.Content.ReadAsStringAsync(ct), JsonOpts);
            if (release is not null) _releaseCache[def.Mode] = (release, DateTime.UtcNow);
            return release;
        }
        catch
        {
            return null; // offline / rate-limited / parse error → caller maps to Unknown
        }
    }

    /// <summary>
    /// Fetch a mode's <c>latest.toml</c> update manifest. Version + payload filename + sha256.
    ///
    /// This is deliberately NOT an api.github.com call: it's a raw-hosted file, so a manifest-backed
    /// mode never spends any of the 60 req/hr unauthenticated GitHub API budget. It also gives a real
    /// per-file hash, which is the problem the "ost-" mirror repo exists to work around for the
    /// release-API modes. Routed through GithubProxy all the same. IsGithub covers
    /// raw.githubusercontent.com, so blocked regions still fall through to the mirrors.
    ///
    /// Shares the release cache's TTL, keyed by mode.
    /// </summary>
    private async Task<UpdateManifest?> FetchUpdateManifestAsync(ModeDefinition def, bool forceRefresh, CancellationToken ct)
    {
        if (def.UpdateManifestUrl is null) return null;
        if (!forceRefresh
            && _manifestCache.TryGetValue(def.Mode, out var cached)
            && DateTime.UtcNow - cached.fetchedAt < CacheTtl)
            return cached.manifest;

        try
        {
            using var res = await gh.SendAsync(def.UpdateManifestUrl, ct);
            if (res is null || !res.IsSuccessStatusCode) return null;
            var manifest = ParseUpdateManifest(await res.Content.ReadAsStringAsync(ct));
            if (manifest is not null) _manifestCache[def.Mode] = (manifest, DateTime.UtcNow);
            return manifest;
        }
        catch
        {
            return null; // offline / parse error → caller maps to Unknown
        }
    }

    /// <summary>
    /// Read the three keys we care about out of a flat <c>key = "value"</c> TOML. Hand-rolled on
    /// purpose. The file has no tables, arrays or nesting, so a TOML package would be a dependency
    /// bought for three lines of parsing.
    /// </summary>
    private static UpdateManifest? ParseUpdateManifest(string toml)
    {
        string? version = null, path = null, sha = null;
        foreach (string raw in toml.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            int eq = line.IndexOf('=');
            if (eq < 0) continue;

            string key = line[..eq].Trim();
            string value = line[(eq + 1)..].Trim().Trim('"');
            switch (key)
            {
                case "version": version = value; break;
                case "path": path = value; break;
                case "sha256": sha = value; break;
            }
        }

        if (version is null or "" || path is null or "" || sha is null or "") return null;
        // `path` is repo-relative ("opensteamtool/v1.0.0/OpenSteamTool.dll"); only the filename matters
        // to us, since that's what gets compared in the Steam root.
        return new UpdateManifest(version, Path.GetFileName(path), sha.ToLowerInvariant());
    }

    /// <summary>Find the small Release zip (matches the pattern, excludes any Debug build).</summary>
    private static GithubAsset? FindZipAsset(ModeDefinition def, GithubRelease release)
    {
        string wanted = (def.ZipAssetPattern ?? "").Replace("{version}", release.TagName);
        return release.Assets.FirstOrDefault(a =>
                   a.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase) &&
                   !a.Name.Contains("Debug", StringComparison.OrdinalIgnoreCase))
               ?? release.Assets.FirstOrDefault(a =>
                   a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                   a.Name.Contains("Release", StringComparison.OrdinalIgnoreCase) &&
                   !a.Name.Contains("Debug", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Extract just the wanted files from a zip into <paramref name="destDir"/> (flattened).</summary>
    private static Dictionary<string, string> ExtractWanted(string zipPath, string[] wanted, string destDir)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // directory entry
            string? match = wanted.FirstOrDefault(w => w.Equals(entry.Name, StringComparison.OrdinalIgnoreCase));
            if (match is null || result.ContainsKey(match)) continue;
            string dest = Path.Combine(destDir, match);
            entry.ExtractToFile(dest, overwrite: true);
            result[match] = dest;
        }
        return result;
    }

    // Asset download routed via GithubProxy: direct, then mirrors (for blocked/throttled regions).
    private Task DownloadToFileAsync(string url, string destPath, IProgress<double?>? progress, CancellationToken ct) =>
        gh.DownloadAsync(url, destPath, progress, ct);

    private static void StampNow(string path)
    {
        try
        {
            var now = DateTime.Now;
            File.SetCreationTime(path, now);
            File.SetLastWriteTime(path, now);
        }
        catch { /* cosmetic */ }
    }
}
