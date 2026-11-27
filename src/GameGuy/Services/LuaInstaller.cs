using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace GameGuy.Services;

/// <summary>Outcome of installing a downloaded artifact into Steam.</summary>
public record InstallResult(bool LuaInstalled, int ManifestCount, IReadOnlyList<string> Failed, string? Error)
{
    public bool AnyFailed => Failed.Count > 0 || Error is not null;
    public static InstallResult Fail(string error) => new(false, 0, [], error);
}

/// <summary>
/// Installs downloaded lua/manifest files into Steam: &lt;appid&gt;.lua → config\stplug-in,
/// *.manifest → depotcache. Best-effort per file (a locked file doesn't abort the rest).
///
/// Note the asymmetry, it is not a typo: stplug-in is under config (it is SteamTools'), depotcache is
/// NOT (it is Steam's own, a sibling of steamapps). See <c>SteamService.DepotCacheDir</c>.
///
/// When the "Auto Update Apps" setting is on (default), setManifestid() lines in the lua are
/// commented out so the app isn't pinned to a version and Steam keeps it updated. When off, the
/// lua is installed as-is (manifest pins intact). Manifest files are copied either way.
/// </summary>
public partial class LuaInstaller(SteamService steam, SettingsService settings, CacheService cache, LuaVault vault)
{
    private bool AutoUpdate => settings.AutoUpdateApps;

    /// <summary>Raised (with the appid) whenever a lua is successfully installed, via any path (plugin,
    /// drag-drop, Add page, Fixes). The UI subscribes to refresh the library live. May fire on a
    /// background thread, so handlers must marshal to the UI thread.</summary>
    public event Action<long>? Installed;

    // "386940_18234567.lua" → "18234567". A plain "386940.lua" has no build id → null.
    [GeneratedRegex(@"^\s*\d+_(\d+)\s*$")]
    private static partial Regex BuildIdRegex();

    /// <summary>
    /// The Steam build id a lua file name declares (&lt;appid&gt;_&lt;buildid&gt;.lua), or null for a plain
    /// &lt;appid&gt;.lua. This is the ONLY place build identity comes from. The file name, whether that's a
    /// staged download (which keeps the server's Content-Disposition name) or an entry inside a zip.
    /// </summary>
    public static string? BuildIdFromFileName(string path)
    {
        var m = BuildIdRegex().Match(Path.GetFileNameWithoutExtension(path));
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// A lua named &lt;appid&gt;_&lt;buildid&gt;.lua must keep its manifest pins, even with "Auto Update Apps"
    /// on. Those pins ARE the build. Installing it de-pinned would hand the user whatever version Steam
    /// ships today while calling it the build they picked. Same reasoning as a Denuvo fix's forceLocked.
    /// </summary>
    private static bool KeepPinsFor(string? buildId, bool forceLocked) => forceLocked || buildId is not null;

    /// <summary>
    /// Store what was just written to stplug-in. Called with the INSTALLED file (not the staged source) so
    /// the stored bytes are exactly what Steam reads, which is what makes this variant resolve as the
    /// active one.
    ///
    /// <para>
    /// A build lua (<c>&lt;appid&gt;_&lt;buildid&gt;.lua</c>) is captured as its own variant. Those
    /// accumulate, that's the point of the switcher. A plain lua instead re-points the single Default slot
    /// via <see cref="LuaVault.SyncDefaultFromLive"/>: capturing it would append a second "default" row
    /// every time a game was re-added from a different generator, which is exactly the bug this replaced.
    /// </para>
    /// </summary>
    private void CaptureInstalled(long appId, string installedPath, string? buildId, string? source)
    {
        try
        {
            if (buildId is not null)
                vault.Capture(appId, installedPath, LuaVariantKind.Build, buildId, source);
            else
                vault.SyncDefaultFromLive(appId);
        }
        catch { /* the vault is a convenience. A hiccup here must never fail an install */ }
    }

    /// <summary>Record a just-installed appid in the "recently added" list (cache.json's LoadedAppIds), so
    /// the store-page plugin can surface it in the "games added since last Steam restart" popup. Best-effort.
    /// A caching hiccup must never fail an install. Also fires <see cref="Installed"/> so the app's own
    /// Home/Manage library views refresh (this is the single chokepoint every lua install passes through).</summary>
    private void RecordLoaded(long appId)
    {
        try { cache.SaveLoadedAppIds(cache.GetLoadedAppIds().Append(appId)); }
        catch { /* the notification is cosmetic */ }
        try { Installed?.Invoke(appId); }
        catch { /* a subscriber blowing up must never fail an install */ }
    }

    // setManifestid(depot, "manifestid", ...). Pins a depot to a fixed version. Commenting it out
    // (and skipping the .manifest) lets Steam fetch the latest, so the app auto-updates.
    [GeneratedRegex(@"^(\s*)(setManifestid\s*\()", RegexOptions.IgnoreCase)]
    private static partial Regex SetManifestLineRegex();

    /// <summary>Comment out every (uncommented) setManifestid line so the app isn't version-pinned.</summary>
    private static string CommentOutManifestPins(string lua)
    {
        var lines = lua.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("--")) continue; // already commented
            // Preserve the original leading whitespace, insert "-- " before setManifestid.
            lines[i] = SetManifestLineRegex().Replace(lines[i], "$1-- $2");
        }
        return string.Join('\n', lines);
    }

    /// <summary>
    /// Write a lua to <paramref name="dest"/>. Comments out manifest pins when AutoUpdate is on, UNLESS
    /// <paramref name="forceLocked"/> is set (Denuvo fixes must stay version-pinned → keep setManifestid).
    /// </summary>
    private void WriteLua(string sourceLuaPath, string dest, bool forceLocked = false)
    {
        if (AutoUpdate && !forceLocked)
        {
            string lua = File.ReadAllText(sourceLuaPath);
            File.WriteAllText(dest, CommentOutManifestPins(lua));
        }
        else
        {
            File.Copy(sourceLuaPath, dest, overwrite: true);
        }
        StampNow(dest);
    }

    /// <summary>Path of the already-installed &lt;appid&gt;.lua in stplug-in, or null.</summary>
    public string? ReadInstalledLua(long appId)
    {
        string? dir = steam.StPlugInDir;
        if (dir is null) return null;
        string path = Path.Combine(dir, $"{appId}.lua");
        return File.Exists(path) ? path : null;
    }

    /// <summary>Copy a bare &lt;appid&gt;.lua into stplug-in (overwrites). Used for DLC unlocks.
    /// <paramref name="forceLocked"/> keeps manifest pins (for Denuvo fixes). The build id is read from
    /// <paramref name="luaPath"/>'s own name, so a &lt;appid&gt;_&lt;buildid&gt;.lua is vaulted as that build.</summary>
    public InstallResult InstallLua(string luaPath, long appId, bool forceLocked = false, string? source = null)
    {
        string? dir = steam.StPlugInDir;
        if (dir is null) return InstallResult.Fail(Resources.Strings.Err_SteamNotFound);

        try
        {
            Directory.CreateDirectory(dir);
            string dest = Path.Combine(dir, $"{appId}.lua");
            string? buildId = BuildIdFromFileName(luaPath);

            WriteLua(luaPath, dest, KeepPinsFor(buildId, forceLocked));
            CaptureInstalled(appId, dest, buildId, source); // exactly what Steam now reads → the active build
            RecordLoaded(appId);
            return new InstallResult(LuaInstalled: true, ManifestCount: 0, Failed: [], Error: null);
        }
        catch (Exception ex)
        {
            return InstallResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Stamp a freshly-installed file's timestamps to "now". File.Copy/extract carries the source's
    /// timestamps (and NTFS tunneling can preserve an old CreationTime when overwriting), so without
    /// this a just-added lua can show a months-old "Added" date. Best-effort.
    /// </summary>
    private static void StampNow(string path)
    {
        try
        {
            var now = DateTime.Now;
            File.SetCreationTime(path, now);
            File.SetLastWriteTime(path, now);
        }
        catch { /* timestamp is cosmetic, never fail an install over it */ }
    }

    /// <summary>
    /// Every installed game lua in stplug-in, as (appid, full path). Skips non-numeric names
    /// (Steamtools.lua etc.). Shared by the Manage and Builds pages so the scan rule lives in one place.
    /// </summary>
    public static IEnumerable<(long AppId, string Path)> EnumerateInstalled(string stPlugInDir)
    {
        if (!Directory.Exists(stPlugInDir)) yield break;
        foreach (string path in Directory.EnumerateFiles(stPlugInDir, "*.lua"))
            if (long.TryParse(Path.GetFileNameWithoutExtension(path), out long appId))
                yield return (appId, path);
    }

    /// <summary>True if the path is a file type we know how to install (.lua, .manifest, .zip).</summary>
    public static bool IsInstallable(string path)
    {
        string ext = Path.GetExtension(path);
        return ext.Equals(".lua", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".manifest", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".zip", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The appid a dropped .lua/.zip represents, taken from the leading digits of its file name.
    /// Tolerates the browser's duplicate suffix and similar, e.g. "3768760 (1).lua" → 3768760.
    /// </summary>
    public static long? AppIdFromFileName(string path)
    {
        string stem = Path.GetFileNameWithoutExtension(path);
        var m = Regex.Match(stem, @"^\s*(\d+)");
        return m.Success && long.TryParse(m.Groups[1].Value, out long id) ? id : null;
    }

    /// <summary>
    /// Resolve the appid for a dropped .zip: the zip's own filename if numeric, else the numeric name
    /// of the .lua entry inside it (e.g. a zip containing 480.lua). Null if neither yields an appid.
    /// </summary>
    public static long? AppIdForZip(string zipPath)
    {
        if (AppIdFromFileName(zipPath) is { } fromName) return fromName;
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
                // AppIdFromFileName (leading digits) rather than a whole-name parse, so a build-named
                // entry like "386940_18234567.lua" still resolves to 386940.
                if (entry.Name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase) &&
                    AppIdFromFileName(entry.Name) is { } id)
                    return id;
        }
        catch { /* unreadable zip. Caller handles null */ }
        return null;
    }

    /// <summary>
    /// Install a loose .lua file by its filename appid (&lt;appid&gt;.lua → stplug-in). The .lua at
    /// <paramref name="luaPath"/> may have any name; it's copied as &lt;appid&gt;.lua.
    /// </summary>
    public InstallResult InstallLuaFile(string luaPath, long appId, bool forceLocked = false, string? source = null) =>
        InstallLua(luaPath, appId, forceLocked, source);

    /// <summary>Copy a loose .manifest into depotcache, keeping its (depot_manifest).manifest name.</summary>
    public InstallResult InstallManifestFile(string manifestPath)
    {
        string? dir = steam.DepotCacheDir;
        if (dir is null) return InstallResult.Fail(Resources.Strings.Err_SteamNotFound);
        try
        {
            Directory.CreateDirectory(dir);
            string dest = Path.Combine(dir, Path.GetFileName(manifestPath));
            // Content-addressed name → identical bytes if it already exists. Skip (counts as installed)
            // rather than overwrite, which would needlessly fail when Steam has the file open.
            if (!File.Exists(dest))
            {
                File.Copy(manifestPath, dest, overwrite: false);
                StampNow(dest);
            }
            return new InstallResult(LuaInstalled: false, ManifestCount: 1, Failed: [], Error: null);
        }
        catch (Exception ex)
        {
            return InstallResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Extract a manifest zip into Steam: the .lua → stplug-in (renamed to &lt;appid&gt;.lua),
    /// every *.manifest → depotcache. Zips may carry no manifests. That's fine. Best-effort.
    /// <paramref name="forceLocked"/> keeps manifest pins (for Denuvo fixes).
    /// </summary>
    public InstallResult InstallZip(string zipPath, long appId, bool forceLocked = false, string? source = null)
    {
        string? plugDir = steam.StPlugInDir;
        string? depotDir = steam.DepotCacheDir;
        if (plugDir is null || depotDir is null)
            return InstallResult.Fail(Resources.Strings.Err_SteamNotFound);

        ZipArchive archive;
        try { archive = ZipFile.OpenRead(zipPath); }
        catch (Exception ex) { return InstallResult.Fail(string.Format(Resources.Strings.Err_OpenDownloadFailed, ex.Message)); }

        bool luaInstalled = false;
        int manifestCount = 0;
        var failed = new List<string>();

        using (archive)
        {
            try { Directory.CreateDirectory(plugDir); } catch { /* reported per-file below */ }
            try { Directory.CreateDirectory(depotDir); } catch { }

            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue; // skip directory entries

                string name = entry.Name;
                bool isLua = name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase);
                bool isManifest = name.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase);
                if (!isLua && !isManifest) continue; // ignore anything else

                // The lua is forced to <appid>.lua; manifests keep their (depot_manifest) name.
                string dest = isLua
                    ? Path.Combine(plugDir, $"{appId}.lua")
                    : Path.Combine(depotDir, name);

                // Manifest filenames are content-addressed (the id is a hash of the content), so an
                // existing one is byte-identical. Skip it. Avoids needless work and, importantly, the
                // "file in use" failure when Steam is running and already has that manifest open.
                if (isManifest && File.Exists(dest))
                {
                    manifestCount++;
                    continue;
                }

                try
                {
                    if (isLua)
                    {
                        // Extract to a temp file, then write the (possibly manifest-stripped) lua.
                        string tmp = Path.Combine(Path.GetTempPath(), $"gameguy_{Guid.NewGuid():N}.lua");
                        try
                        {
                            entry.ExtractToFile(tmp, overwrite: true);
                            // The ENTRY name is what carries the build id ("386940_18234567.lua"); `dest`
                            // has already been flattened to <appid>.lua above, so it can't be read from there.
                            string? buildId = BuildIdFromFileName(name);

                            WriteLua(tmp, dest, KeepPinsFor(buildId, forceLocked));
                            CaptureInstalled(appId, dest, buildId, source);
                            luaInstalled = true;
                            RecordLoaded(appId);
                        }
                        finally { try { File.Delete(tmp); } catch { /* best effort */ } }
                    }
                    else
                    {
                        entry.ExtractToFile(dest, overwrite: true);
                        StampNow(dest);
                        manifestCount++;
                    }
                }
                catch
                {
                    failed.Add(name); // e.g. file locked because Steam is running
                }
            }
        }

        return new InstallResult(luaInstalled, manifestCount, failed, Error: null);
    }
}
