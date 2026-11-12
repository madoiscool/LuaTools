using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace LuaToolsGui.Services;

/// <summary>
/// Resolves the Steam install location: auto-detected from the registry, or a user override.
/// Detection confirms the folder actually contains steam.exe.
/// </summary>
public class SteamService(SettingsService settings)
{
    // Known 64-bit Steam registry locations, in priority order.
    private static readonly (RegistryHive Hive, RegistryView View, string SubKey, string Value)[] RegistryLocations =
    [
        (RegistryHive.CurrentUser, RegistryView.Registry64, @"SOFTWARE\Valve\Steam", "SteamPath"),
        (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
        (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Valve\Steam", "InstallPath"),
    ];

    /// <summary>Steam path detected from the registry (confirmed via steam.exe), or null.</summary>
    public string? AutoDetectedPath => DetectFromRegistry();

    /// <summary>The effective path: user override if set, otherwise the auto-detected one.</summary>
    public string? EffectivePath
    {
        get
        {
            string? overridePath = settings.SteamPathOverride;
            return !string.IsNullOrWhiteSpace(overridePath) ? Normalize(overridePath) : AutoDetectedPath;
        }
    }

    public bool IsOverridden => !string.IsNullOrWhiteSpace(settings.SteamPathOverride);

    /// <summary>
    /// The Steam client's UI language ("english", "schinese", "brazilian", ...), or null if unreadable.
    /// </summary>
    /// <remarks>
    /// This is the same vocabulary a depot's <c>config.language</c> uses — verified against steamcmd's
    /// app info, where the registry's "english" matches the depot value verbatim, and against the
    /// <c>baselanguages</c> list ("english,german,french,..."). So it can be compared to depot languages
    /// directly, with no mapping table.
    ///
    /// Read fresh each time rather than cached: a user can change Steam's language without restarting
    /// this app, and the read is a single registry lookup.
    ///
    /// Lowercased on the way out — the depot values are lowercase, and relying on every future call site
    /// to remember OrdinalIgnoreCase is the kind of thing that breaks once and is never noticed.
    /// </remarks>
    public static string? SteamLanguage
    {
        get
        {
            try
            {
                using var key = RegistryKey
                    .OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64)
                    .OpenSubKey(@"SOFTWARE\Valve\Steam");
                return key?.GetValue("Language") is string s && s.Length > 0
                    ? s.Trim().ToLowerInvariant()
                    : null;
            }
            catch { return null; } // no Steam, or the value is missing/unreadable
        }
    }

    /// <summary>True when the effective path exists and contains steam.exe.</summary>
    public bool IsValid => EffectivePath is not null && File.Exists(SteamExePathFor(EffectivePath));

    public static string SteamExePathFor(string steamPath) => Path.Combine(steamPath, "steam.exe");

    /// <summary>Full path to config\stplug-in, or null if Steam isn't located.</summary>
    public string? StPlugInDir =>
        EffectivePath is { } p ? Path.Combine(p, "config", "stplug-in") : null;

    /// <summary>
    /// Full path to depotcache (where .manifest files go), or null if Steam isn't located.
    /// </summary>
    /// <remarks>
    /// This is <c>&lt;Steam&gt;\depotcache</c>, a SIBLING of steamapps — NOT <c>config\depotcache</c>.
    /// Only stplug-in lives under config; that folder is SteamTools'. depotcache is Steam's own, and
    /// Steam builds the path as <c>%s/depotcache/%d_%llu.manifest</c> off the install root
    /// (the literal is in steamclient64.dll, listed beside /common, /downloading, /temp and /workshop).
    ///
    /// This was wrong until 2026-09-09 and it silently broke downloads: a manifest written to
    /// config\depotcache is invisible to Steam, so a pinned depot resolves to nothing and the download
    /// never starts, with no error anywhere. Measured on a real install — of 2,314 files in
    /// config\depotcache, 2,313 were byte-identical copies of the real depotcache with mtimes preserved
    /// (a stale one-off mirror), and the single file that was ONLY there was the manifest for the one
    /// app that would not download. Meanwhile 394 manifests Steam had fetched itself existed only in
    /// the real folder. So the read side was as wrong as the write side: cache hits on files Steam
    /// cannot see, cache misses on files it already has.
    /// </remarks>
    public string? DepotCacheDir =>
        EffectivePath is { } p ? Path.Combine(p, "depotcache") : null;

    /// <summary>
    /// The old, wrong location. Read-only fallback so manifests already sitting there still count as
    /// cached instead of every install re-fetching its whole depot list once. Never written to.
    /// </summary>
    public string? LegacyDepotCacheDir =>
        EffectivePath is { } p ? Path.Combine(p, "config", "depotcache") : null;

    /// <summary>
    /// Full path to appcache\appinfo.vdf, or null if Steam isn't located. Every app's install metadata
    /// lives here, including <c>config.installdir</c> and the launch executables.
    /// </summary>
    public string? AppInfoPath =>
        EffectivePath is { } p ? Path.Combine(p, "appcache", "appinfo.vdf") : null;

    /// <summary>Open a store/steam URL or file path with the shell (browser, Steam client, Explorer).</summary>
    public static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    /// <summary>Open Explorer with the given file selected.</summary>
    public static void RevealInExplorer(string filePath) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"") { UseShellExecute = true });

    /// <summary>
    /// Show a path in Explorer, picking the right gesture for what it is: a file gets selected inside
    /// its folder, a folder is opened. Returns false when the path is missing or Explorer refuses.
    /// </summary>
    /// <remarks>
    /// <see cref="RevealInExplorer"/> always passes <c>/select</c>, which for a directory highlights it
    /// in its PARENT rather than opening it — wrong for the depot output folder and a game's install
    /// directory, which are the two most common targets. Callers hand over whichever they have and let
    /// this sort it out, so no call site has to probe the filesystem itself.
    /// </remarks>
    public static bool ShowInExplorer(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            if (File.Exists(path)) { RevealInExplorer(path); return true; }
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return true;
            }
            return false; // deleted since the row was created
        }
        catch { return false; } // no shell association, or Explorer is wedged
    }

    /// <summary>Put text on the clipboard. Returns false instead of throwing when it can't.</summary>
    /// <remarks>
    /// <c>Clipboard.SetText</c> throws <c>CLIPBRD_E_CANT_OPEN</c> when another process is holding the
    /// clipboard open — common with remote-desktop and clipboard-manager tools, and entirely outside our
    /// control. Copying an app id is never worth an unhandled exception, so failure is reported, not thrown.
    /// </remarks>
    public static bool CopyToClipboard(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        try { System.Windows.Clipboard.SetText(text); return true; }
        catch { return false; }
    }

    /// <summary>Kill any running steam.exe (and its tree) and wait for it to exit. Safe to call when
    /// Steam isn't running. Use before changing Steam's files so they aren't locked.</summary>
    /// <summary>True while a Steam client process is running. Appinfo.vdf can't be edited under it.</summary>
    public static bool IsSteamRunning()
    {
        var procs = Process.GetProcessesByName("steam");
        try { return procs.Length > 0; }
        finally { foreach (var p in procs) p.Dispose(); }
    }

    public void StopSteam()
    {
        foreach (var proc in Process.GetProcessesByName("steam"))
        {
            try { proc.Kill(entireProcessTree: true); proc.WaitForExit(8000); }
            catch { /* already gone / access denied */ }
            finally { proc.Dispose(); }
        }
    }

    /// <summary>Launch Steam from the effective path. Returns false if it can't be located/launched.</summary>
    public bool StartSteam()
    {
        string? path = EffectivePath;
        if (path is null) return false;
        string exe = SteamExePathFor(path);
        if (!File.Exists(exe)) return false;

        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Kill any running steam.exe and relaunch it from the effective path. lua changes only take
    /// effect after a Steam restart. Returns false if Steam can't be located/launched.
    /// </summary>
    public bool RestartSteam()
    {
        StopSteam();
        return StartSteam();
    }

    private static string? DetectFromRegistry()
    {
        foreach (var (hive, view, subKey, value) in RegistryLocations)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.OpenSubKey(subKey);
                if (key?.GetValue(value) is not string raw || string.IsNullOrWhiteSpace(raw)) continue;

                string path = Normalize(raw);
                if (File.Exists(SteamExePathFor(path))) return path;
            }
            catch
            {
                // Inaccessible key: try the next one
            }
        }
        return null;
    }

    /// <summary>Registry values vary (forward vs back slashes, casing). Canonicalize to a Windows path.</summary>
    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path.Trim().Replace('/', '\\')); }
        catch { return path.Trim().Replace('/', '\\'); }
    }
}
