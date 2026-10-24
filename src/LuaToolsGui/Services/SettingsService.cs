using System.IO;
using System.Reflection;
using System.Text.Json;

namespace LuaToolsGui.Services;

public class AppSettings
{
    public string? SteamPathOverride { get; set; }

    // ── Unlocker mode (Mode page). User's chosen backend ────────────
    // "Ost" | "Bst" | "Custom". Older builds wrote "SteamTools" | "OpenSteamTools" |
    // "OpenSteamToolsNightly" | "CloudRedirect"; see ModeMigration, which rewrites those on startup.
    public string? SelectedMode { get; set; }

    // ── Install behavior ─────────────────────────────────────────────
    // When true (default), installs comment out setManifestid() lines and skip copying .manifest
    // files, so games aren't pinned to a version and Steam keeps them updated. Nullable so we can
    // tell "never set" (→ default ON) from an explicit user choice.
    public bool? AutoUpdateApps { get; set; }

    // When true (default), donate spare Steam decryption keys to the community pool. Nullable so
    // "never set" (→ default ON) is distinguishable from an explicit user choice.
    public bool? DonateKeys { get; set; }

    // Manage-page results-per-page. 0 = "All" (single infinite scroll). Nullable so "never set"
    // (→ default 24) is distinguishable from an explicit choice.
    public int? ManagePageSize { get; set; }

    // Fixes-page results-per-page. 0 = "All" (single infinite scroll). Nullable so "never set"
    // (→ default 24) is distinguishable from an explicit choice.
    public int? FixesPageSize { get; set; }

    // Builds-page game-list results-per-page. 0 = "All". Kept separate from ManagePageSize. The Builds
    // list is a narrow sidebar, so a size that suits the Manage grid rarely suits both.
    public int? BuildsPageSize { get; set; }

    // UI language as a BCP-47 tag ("en", "zh-Hans"). Null = follow the Windows display language.
    public string? Language { get; set; }

    // The user's own Hubcap (hubcapmanifest.com) API key ("smm_…"). Null = not configured; key-gated
    // sources stay locked until set. Stored locally. The app calls Hubcap directly with it.
    public string? HubcapApiKey { get; set; }

    // When true, register the app to launch on Windows sign-in (HKCU …\Run). Nullable so "never set"
    // (→ default OFF) is distinguishable from an explicit choice.
    public bool? StartWithWindows { get; set; }

    // When true, minimizing hides the window to the system tray instead of the taskbar. Nullable so
    // "never set" (→ default OFF) is distinguishable from an explicit choice.
    public bool? MinimizeToTray { get; set; }

    // When true, FastFetch auto-picks the first available source and downloads immediately.
    // Nullable so "never set" (→ default OFF) is distinguishable from an explicit choice.
    public bool? FastFetch { get; set; }

    // How host names are resolved: "Auto" (default), "Always" or "Never". Stored in English because
    // it is matched in code; the Settings page localizes the display only. Null = never set → "Auto".
    // See AppHttp for what each mode does.
    public string? DnsMode { get; set; }
}

public class SettingsService
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LuaToolsGui");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    private AppSettings _settings = new();

    public SettingsService() => Load();

    /// <summary>User-chosen Steam folder. Null = auto-detect from registry. Persisted only when set.</summary>
    public string? SteamPathOverride
    {
        get => _settings.SteamPathOverride;
        set
        {
            _settings.SteamPathOverride = string.IsNullOrWhiteSpace(value) ? null : value;
            Save();
        }
    }

    /// <summary>Selected unlocker backend ("SteamTools" | "OpenSteamTools"), or null if never chosen.</summary>
    public string? SelectedMode
    {
        get => _settings.SelectedMode;
        set { _settings.SelectedMode = string.IsNullOrWhiteSpace(value) ? null : value; Save(); }
    }

    /// <summary>When true (default), installs don't lock manifests so apps keep auto-updating.</summary>
    public bool AutoUpdateApps
    {
        get => _settings.AutoUpdateApps ?? true; // default ON
        set { _settings.AutoUpdateApps = value; Save(); }
    }

    /// <summary>When true (default), donate spare Steam decryption keys to the community pool.</summary>
    public bool DonateKeys
    {
        get => _settings.DonateKeys ?? true; // default ON
        set { _settings.DonateKeys = value; Save(); }
    }

    /// <summary>Manage-page results-per-page (default 24). 0 = "All" (single infinite scroll).</summary>
    public int ManagePageSize
    {
        get => _settings.ManagePageSize ?? 24; // default 24
        set { _settings.ManagePageSize = value; Save(); }
    }

    /// <summary>Fixes-page results-per-page (default 24). 0 = "All" (single infinite scroll).</summary>
    public int FixesPageSize
    {
        get => _settings.FixesPageSize ?? 24; // default 24
        set { _settings.FixesPageSize = value; Save(); }
    }

    /// <summary>Builds-page game-list results-per-page (default 10). 0 = "All". Smaller than the other
    /// pages' 24. This list is a narrow sidebar next to the build detail, not a full-width grid.</summary>
    public int BuildsPageSize
    {
        get => _settings.BuildsPageSize ?? 10; // default 10
        set { _settings.BuildsPageSize = value; Save(); }
    }

    /// <summary>UI language tag ("en" | "zh-Hans"), or null to follow the Windows display language.</summary>
    public string? Language
    {
        get => _settings.Language;
        set { _settings.Language = string.IsNullOrWhiteSpace(value) ? null : value; Save(); }
    }

    /// <summary>The user's Hubcap API key ("smm_…"), or null if not configured.</summary>
    public string? HubcapApiKey
    {
        get => _settings.HubcapApiKey;
        set { _settings.HubcapApiKey = string.IsNullOrWhiteSpace(value) ? null : value; Save(); }
    }

    /// <summary>When true, the app is registered to launch on Windows sign-in (default OFF).</summary>
    public bool StartWithWindows
    {
        get => _settings.StartWithWindows ?? false; // default OFF
        set { _settings.StartWithWindows = value; Save(); }
    }

    /// <summary>When true, minimizing hides the window to the system tray (default OFF).</summary>
    public bool MinimizeToTray
    {
        get => _settings.MinimizeToTray ?? false; // default OFF
        set { _settings.MinimizeToTray = value; Save(); }
    }

    /// <summary>When true, FastFetch auto-picks the first available source and downloads immediately (default OFF).</summary>
    public bool FastFetch
    {
        get => _settings.FastFetch ?? false; // default OFF
        set { _settings.FastFetch = value; Save(); }
    }

    /// <summary>
    /// How host names are resolved: "Auto" (default), "Always" or "Never". See <see cref="AppHttp"/>.
    /// </summary>
    /// <remarks>
    /// Defaults to Auto rather than off on purpose. A user whose ISP DNS-blocks lua.tools cannot reach
    /// anything in the app to discover that a setting would fix it, so an opt-in toggle would be found
    /// by everyone except the people who need it. Auto costs unaffected users nothing: the system
    /// resolver is still tried first and DoH only engages once it has actually failed.
    /// </remarks>
    public string DnsMode
    {
        get => _settings.DnsMode is "Always" or "Never" ? _settings.DnsMode : "Auto";
        set { _settings.DnsMode = value; Save(); }
    }

    private static readonly string TmpPath = FilePath + ".tmp";
    private static readonly string BakPath = FilePath + ".bak";

    private void Load()
    {
        // Prefer the primary file; fall back to the last-good .bak. Crucially, NEVER silently reset a
        // corrupt-but-present file to defaults (a later Save would then overwrite it and lose real data).
        // Move it aside to .corrupt so it's preserved and can't be clobbered.
        if (TryLoad(FilePath)) return;
        PreserveCorrupt(FilePath);
        if (TryLoad(BakPath)) return;
        _settings = new AppSettings();
    }

    private bool TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            if (JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) is { } loaded)
            {
                _settings = loaded;
                return true;
            }
        }
        catch { /* missing / truncated / invalid JSON → caller falls through */ }
        return false;
    }

    /// <summary>A present-but-unparseable settings file is moved aside (not deleted) so its contents survive
    /// for manual recovery and a subsequent Save can't overwrite it.</summary>
    private static void PreserveCorrupt(string path)
    {
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length > 0)
                File.Move(path, path + ".corrupt", overwrite: true);
        }
        catch { /* best effort */ }
    }

    private static readonly PropertyInfo[] PersistedProperties =
        typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance);

    /// <summary>
    /// True when no setting has been set. Derived from every public property rather than a hand-kept
    /// list: a field missing from such a list made a user whose ONLY change was that setting lose it on
    /// the next save (FixesPageSize, then DnsMode).
    /// </summary>
    internal static bool IsEmpty(AppSettings settings) =>
        PersistedProperties.All(p => p.GetValue(settings) is null);

    // Every setter saves, and setters run on the UI thread and on background services alike. All of them
    // share one .tmp path, so two unsynchronized saves race on the final File.Move.
    private readonly object _saveGate = new();

    private void Save()
    {
        lock (_saveGate)
        {
            try { SaveCore(); }
            // A failed write must not throw out of a property setter into whatever UI changed it. The
            // in-memory value still applies for this session, and the .bak/.tmp scheme keeps the
            // previous file intact.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                PluginLog.Log($"Settings save failed: {ex.Message}");
            }
        }
    }

    private void SaveCore()
    {
        // Nothing worth persisting → don't leave a settings file behind.
        if (IsEmpty(_settings))
        {
            foreach (var p in new[] { FilePath, BakPath, TmpPath })
                try { if (File.Exists(p)) File.Delete(p); } catch { /* best effort */ }
            return;
        }

        Directory.CreateDirectory(Dir);
        string json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });

        // Atomic write: fill a temp file, then rename it over the target. A crash/kill mid-write can only
        // ever truncate the .tmp. The live settings.json is replaced by an atomic move (same-volume rename)
        // and is therefore never left half-written. (This class of loss is exactly what a forced kill during
        // a plain WriteAllText caused.) A .bak of the last good file is kept as a second recovery source.
        File.WriteAllText(TmpPath, json);
        try { if (File.Exists(FilePath)) File.Copy(FilePath, BakPath, overwrite: true); } catch { /* best effort */ }
        File.Move(TmpPath, FilePath, overwrite: true);
    }
}
