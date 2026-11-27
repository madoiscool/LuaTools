using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameGuy.Services;
using GameGuy.Models;
using Microsoft.Win32;

namespace GameGuy.ViewModels;

/// <summary>A selectable UI language. <see cref="Tag"/> is the BCP-47 tag ("en", "zh-Hans") or null for
/// "follow the system display language".</summary>
public record LanguageOption(string Display, string? Tag);

/// <summary>A DNS resolution mode. <see cref="Tag"/> is the stored value and stays English ("Auto",
/// "Always", "Never") because it is matched in code; only <see cref="Display"/> is localized.</summary>
public record DnsModeOption(string Display, string Tag);

public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly AuthService _auth;
    private readonly SteamService _steam;
    private readonly HubcapService _hubcap;

    /// <summary>True when no authentication key is stored.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAuthKey))]
    private bool _isGuest = true;

    public bool HasAuthKey => !IsGuest;

    // ── Key-required redirect banner ───────────────────────────────
    /// <summary>Set by App when navigating here from a gated action. Null = banner hidden.</summary>
    [ObservableProperty] private string? _authKeyRequiredMessage;

    [RelayCommand]
    private void DismissAuthKeyRequired() => AuthKeyRequiredMessage = null;

    // ── Steam location ──────────────────────────────────────────────
    [ObservableProperty] private string _steamPath = "";
    [ObservableProperty] private bool _isSteamOverridden;
    [ObservableProperty] private string _steamSource = "";
    [ObservableProperty] private string? _steamWarning;

    // ── Install behavior ────────────────────────────────────────────
    /// <summary>Auto Update Apps (Don't Lock Manifests). Persisted via SettingsService.</summary>
    [ObservableProperty] private bool _autoUpdateApps;

    partial void OnAutoUpdateAppsChanged(bool value) => _settings.AutoUpdateApps = value;

    /// <summary>FastFetch: auto-download from the first available source. Same persisted setting the Add
    /// screen's toggle drives (both read/write SettingsService.FastFetch).</summary>
    [ObservableProperty] private bool _fastFetch;

    partial void OnFastFetchChanged(bool value) => _settings.FastFetch = value;

    /// <summary>Donate spare Steam decryption keys to the community pool. Persisted via SettingsService.</summary>
    [ObservableProperty] private bool _donateKeys;

    partial void OnDonateKeysChanged(bool value) => _settings.DonateKeys = value;

    // ── Startup behavior ────────────────────────────────────────────
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "GameGuy";

    /// <summary>Launch the app on Windows sign-in (writes HKCU …\Run). Persisted via SettingsService.</summary>
    [ObservableProperty] private bool _startWithWindows;

    partial void OnStartWithWindowsChanged(bool value)
    {
        _settings.StartWithWindows = value;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null) return;
            if (value)
                // --minimized: start silently in the tray on sign-in so it just serves the
                // local backend (127.0.0.1:6767) for the Steam plugin without stealing focus.
                key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\" --minimized");
            else
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
        catch { /* registry write blocked. Setting is still saved, just not applied this run */ }
    }

    /// <summary>Minimize to the system tray instead of the taskbar. Persisted via SettingsService.</summary>
    [ObservableProperty] private bool _minimizeToTray;

    partial void OnMinimizeToTrayChanged(bool value)
    {
        _settings.MinimizeToTray = value;
        if (!value) RequestShowWindow?.Invoke(); // turning it off restores the window if it's hidden in the tray
    }

    /// <summary>Set by App: restore the main window from the tray (used when Minimize-to-tray is turned off).</summary>
    public Action? RequestShowWindow { get; set; }

    // ── Language ────────────────────────────────────────────────────
    /// <summary>Available UI languages (native endonyms, matching Steam's list). "System default"
    /// (null tag) follows Windows. Languages whose .resx isn't present fall back to English.</summary>
    public ObservableCollection<LanguageOption> LanguageOptions { get; } =
    [
        new(Resources.Strings.Settings_Language_SystemDefault, null),
        new("English", "en"),
        new("简体中文", "zh-Hans"),
        new("繁體中文", "zh-Hant"),
        new("日本語", "ja"),
        new("한국어", "ko"),
        new("Español (España)", "es"),
        new("Español (Latinoamérica)", "es-419"),
        new("Português (Brasil)", "pt-BR"),
        new("Português (Portugal)", "pt-PT"),
        new("Français", "fr"),
        new("Deutsch", "de"),
        new("Italiano", "it"),
        new("Nederlands", "nl"),
        new("Polski", "pl"),
        new("Русский", "ru"),
        new("Українська", "uk"),
        new("العربية", "ar"),
        new("Čeština", "cs"),
        new("Magyar", "hu"),
        new("Română", "ro"),
        new("Türkçe", "tr"),
        new("Ελληνικά", "el"),
        new("Български", "bg"),
        new("ไทย", "th"),
        new("Tiếng Việt", "vi"),
        new("Bahasa Indonesia", "id"),
        new("Dansk", "da"),
        new("Suomi", "fi"),
        new("Norsk", "nb"),
        new("Svenska", "sv"),
    ];

    [ObservableProperty] private LanguageOption _selectedLanguage = null!;

    // ── DNS ─────────────────────────────────────────────────────────
    /// <summary>How host names are resolved. See <see cref="Services.AppHttp"/>.</summary>
    public ObservableCollection<DnsModeOption> DnsModeOptions { get; } =
    [
        new(Resources.Strings.Settings_Dns_Auto, "Auto"),
        new(Resources.Strings.Settings_Dns_Always, "Always"),
        new(Resources.Strings.Settings_Dns_Never, "Never"),
    ];

    [ObservableProperty] private DnsModeOption _selectedDnsMode = null!;

    // Takes effect on new connections; pooled ones turn over within AppHttp's connection lifetime, so
    // there is no restart prompt here (unlike the language switch, which is baked in at parse time).
    partial void OnSelectedDnsModeChanged(DnsModeOption value)
    {
        if (value is not null) _settings.DnsMode = value.Tag;
    }

    private bool _suppressLanguagePrompt; // true during ctor init so we don't prompt on first bind

    partial void OnSelectedLanguageChanged(LanguageOption value)
    {
        if (value is null || _suppressLanguagePrompt) return;
        _settings.Language = value.Tag; // null = follow system
        // The whole UI is built with parse-time x:Static resources, so a relaunch is needed to re-read it.
        RequestRestartPrompt?.Invoke();
    }

    // ── Hubcap API key ──────────────────────────────────────────────
    /// <summary>The key the user is typing/pasting. Starts blank. The saved key is never shown back.</summary>
    [ObservableProperty] private string _hubcapKeyInput = "";

    /// <summary>Status line under the key box (usage/expiry on success, or an error). Null = hidden.</summary>
    [ObservableProperty] private string? _hubcapKeyStatus;

    /// <summary>Color for the status line: red on error, green on success.</summary>
    [ObservableProperty] private string _hubcapKeyStatusColor = "#22c55e";
    /// <summary>True when a key is saved in settings (drives the "Clear" button + "configured" label).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHubcapStats))]
    [NotifyPropertyChangedFor(nameof(HubcapStatsPending))]
    [NotifyPropertyChangedFor(nameof(HubcapStatsText))]
    private bool _hubcapIsKeyConfigured;

    /// <summary>True while validating (disables the buttons via <see cref="CanEditHubcapKey"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditHubcapKey))]
    private bool _isValidatingHubcapKey;

    public bool CanEditHubcapKey => !IsValidatingHubcapKey;

    [ObservableProperty] private bool _isRefreshingHubcapStats;

    /// <summary>Latest stats for the saved key. Null = not loaded / no key / error.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HubcapStatsDisplay))]
    [NotifyPropertyChangedFor(nameof(HubcapStatsText))]
    [NotifyPropertyChangedFor(nameof(HubcapStatsPending))]
    [NotifyPropertyChangedFor(nameof(HubcapUsagePercent))]
    [NotifyPropertyChangedFor(nameof(ShowHubcapStats))]
    private HubcapStats? _hubcapStats;

    /// <summary>"12 / 25 requests today" or "12 / 25 requests today · expires 2026-08-15".</summary>
    public string? HubcapStatsDisplay =>
        HubcapStats is not null ? FormatHubcapStats(HubcapStats) : null;

    /// <summary>Progress bar fill 0.0–1.0.</summary>
    public double HubcapUsagePercent =>
        HubcapStats is { DailyLimit: > 0 } ? (double)HubcapStats.DailyUsage / HubcapStats.DailyLimit : 0;

    /// <summary>Show the usage card whenever a key is configured. It renders a loading state until stats
    /// arrive (never blank), so the section can't look broken while the fetch is in flight or after a
    /// transient failure (the key input is collapsed once configured, so this card is the only content).</summary>
    public bool ShowHubcapStats => HubcapIsKeyConfigured;

    /// <summary>Stats not yet loaded for a configured key → show the "Loading…" placeholder + indeterminate bar.</summary>
    public bool HubcapStatsPending => HubcapIsKeyConfigured && HubcapStats is null;

    /// <summary>Placeholder text shown until real stats load.</summary>
    public string HubcapStatsText => HubcapStatsDisplay ?? Resources.Strings.Common_Loading;

    /// <summary>Set by App: actually relaunch the app (used after a language change).</summary>
    public Action? RequestRestart { get; set; }

    /// <summary>Show the "language changed. Restart now?" toast. Wired here so the VM stays UI-agnostic;
    /// App provides the toast + restart action.</summary>
    public Action? RequestRestartPrompt { get; set; }

    public SettingsViewModel(SettingsService settings, AuthService auth, SteamService steam,
        HubcapService hubcap)
    {
        _settings = settings;
        _auth = auth;
        _steam = steam;
        _hubcap = hubcap;
        _auth.AuthStateChanged += RefreshAccount;
        RefreshAccount();
        RefreshSteam();
        _autoUpdateApps = settings.AutoUpdateApps; // init from saved value (default ON) without triggering Save
        _fastFetch = settings.FastFetch;
        _donateKeys = settings.DonateKeys;
        _startWithWindows = settings.StartWithWindows; // default OFF. Init without triggering the registry write
        _minimizeToTray = settings.MinimizeToTray;
        _hubcapIsKeyConfigured = !string.IsNullOrEmpty(settings.HubcapApiKey);
        _authKeyIsConfigured = !string.IsNullOrEmpty(settings.AuthKey);

        // Select the saved language (or "System default") without firing the restart prompt.
        _suppressLanguagePrompt = true;
        _selectedLanguage = LanguageOptions.FirstOrDefault(o => o.Tag == settings.Language) ?? LanguageOptions[0];
        _suppressLanguagePrompt = false;

        // Assign the backing field, not the property: going through the setter would fire the change
        // handler and write the default straight back to disk on first open.
        _selectedDnsMode = DnsModeOptions.FirstOrDefault(o => o.Tag == settings.DnsMode) ?? DnsModeOptions[0];
    }

    private void RefreshSteam()
    {
        string? path = _steam.EffectivePath;
        IsSteamOverridden = _steam.IsOverridden;
        SteamPath = path ?? Resources.Strings.Settings_SteamNotFound;
        SteamSource = IsSteamOverridden ? Resources.Strings.Settings_SteamSource_Custom
            : path is null ? Resources.Strings.Settings_SteamSource_NotFound
            : Resources.Strings.Settings_SteamSource_Auto;
        SteamWarning = path is not null && !_steam.IsValid ? Resources.Strings.Settings_SteamWarning_NoExe : null;
    }

    private void RefreshAccount()
    {
        IsGuest = _auth.IsGuest;
        AuthKeyIsConfigured = !IsGuest;
        MaskedAuthKey = MaskKey(_auth.TrimmedKey);
        if (!IsGuest) AuthKeyRequiredMessage = null;
    }

    /// <summary>Never render the key itself: first four characters plus its length is enough to tell
    /// one key from another when something is wrong, and useless to anyone reading over a shoulder.</summary>
    private static string? MaskKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        string head = key.Length <= 8 ? key[..2] : key[..4];
        return $"{head}{new string('•', 12)}  ({key.Length})";
    }

    // ── Authentication key ──────────────────────────────────────────

    /// <summary>The key the user is typing/pasting. Starts blank. The saved key is never shown back.</summary>
    [ObservableProperty] private string _authKeyInput = "";

    /// <summary>Status/error line under the key box. Null = hidden.</summary>
    [ObservableProperty] private string? _authKeyStatus;

    /// <summary>True when a key is saved (drives the "Key set" row + Clear button).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AuthKeyStatusText))]
    private bool _authKeyIsConfigured;

    /// <summary>Masked display form of the saved key (e.g. "abcd••••••••••••  (43)"), or null when none
    /// is saved. The raw key is never bound into the view.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAuthKeyMask))]
    private string? _maskedAuthKey;

    public bool HasAuthKeyMask => MaskedAuthKey is not null;

    /// <summary>"Key set" / "No key set", shown as the status row in the key section.</summary>
    public string AuthKeyStatusText =>
        AuthKeyIsConfigured ? Resources.Strings.Settings_AuthKeyActive : Resources.Strings.Settings_AuthKeyMissing;

    /// <summary>Save the pasted key. Nothing to verify client-side, so this is stored as typed.</summary>
    [RelayCommand]
    private void SaveAuthKey()
    {
        string key = AuthKeyInput.Trim();
        if (key.Length == 0)
        {
            AuthKeyStatus = Resources.Strings.Settings_AuthKeyEmptyError;
            return;
        }

        _auth.SetKey(key);
        AuthKeyInput = "";
        AuthKeyStatus = null;
        RefreshAccount();
    }

    /// <summary>Set by App: re-open the key prompt when the key is cleared, since the app requires one.</summary>
    public Action? RequestAuthKeyGate { get; set; }

    /// <summary>Forget the saved key. A key is required, so this also re-opens the key prompt.</summary>
    [RelayCommand]
    private void ClearAuthKey()
    {
        _auth.ClearKey();
        RequestAuthKeyGate?.Invoke();
        AuthKeyInput = "";
        AuthKeyStatus = null;
        RefreshAccount();
    }

    [RelayCommand]
    private void OverrideSteamFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = Resources.Strings.Settings_ChooseSteamFolder,
            InitialDirectory = _steam.EffectivePath ?? "",
        };
        if (dialog.ShowDialog() == true)
        {
            _settings.SteamPathOverride = dialog.FolderName;
            RefreshSteam();
        }
    }

    [RelayCommand]
    private void ClearSteamOverride()
    {
        _settings.SteamPathOverride = null;
        RefreshSteam();
    }

    [RelayCommand]
    private void OpenSteamFolder()
    {
        string? path = _steam.EffectivePath;
        if (path is not null && System.IO.Directory.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    [RelayCommand]
    private void OpenWebsite() =>
        Process.Start(new ProcessStartInfo(AppConfig.ApiBaseUrl) { UseShellExecute = true });

    [RelayCommand]
    private void OpenHubcap() =>
        Process.Start(new ProcessStartInfo(AppConfig.HubcapBaseUrl) { UseShellExecute = true });

    // ── Hubcap key management ───────────────────────────────────────

    /// <summary>Called by the View when loaded. Auto-refreshes stats if a key is saved.</summary>
    public void OnViewLoaded()
    {
        if (HubcapIsKeyConfigured)
            RefreshHubcapStatsCommand.Execute(null);
        // Re-sync FastFetch in case the Add screen's toggle changed it this session (both are singletons
        // that only read the setting at construction). No-op if unchanged; a real change writes back the
        // same value, so no feedback loop.
        FastFetch = _settings.FastFetch;
    }

    /// <summary>Re-fetch usage stats for the saved key. Silent no-op if no key is saved.</summary>
    [RelayCommand]
    private async Task RefreshHubcapStatsAsync()
    {
        string? key = _settings.HubcapApiKey;
        if (string.IsNullOrEmpty(key)) return;

        IsRefreshingHubcapStats = true;
        try
        {
            HubcapStats = await _hubcap.GetStatsAsync(key);
        }
        catch
        {
            HubcapStats = null;
        }
        finally
        {
            IsRefreshingHubcapStats = false;
        }
    }

    /// <summary>Validate the typed key (format first, then a live stats call) and, if good, save it.</summary>
    [RelayCommand]
    private async Task ValidateAndSaveHubcapKeyAsync()
    {
        string key = HubcapKeyInput.Trim();

        if (!HubcapService.IsValidKeyFormat(key))
        {
            ShowHubcapStatus(Resources.Strings.Settings_HubcapKeyBad, isError: true);
            return;
        }

        IsValidatingHubcapKey = true;
        try
        {
            var stats = await _hubcap.GetStatsAsync(key);
            if (stats is null)
            {
                // Could be a bad/expired key (401), or a network problem. Both surface as null.
                ShowHubcapStatus(Resources.Strings.Settings_HubcapKeyError, isError: true);
                return;
            }

            _settings.HubcapApiKey = key;
            HubcapIsKeyConfigured = true;
            HubcapKeyInput = "";
            HubcapKeyStatus = null;
            HubcapStats = stats;
        }
        finally
        {
            IsValidatingHubcapKey = false;
        }
    }

    /// <summary>Forget the saved key and reset the status line.</summary>
    [RelayCommand]
    private void ClearHubcapKey()
    {
        _settings.HubcapApiKey = null;
        HubcapIsKeyConfigured = false;
        HubcapKeyInput = "";
        HubcapKeyStatus = null;
        HubcapStats = null;
    }

    private void ShowHubcapStatus(string text, bool isError)
    {
        HubcapKeyStatus = text;
        HubcapKeyStatusColor = isError ? "#f87171" : "#22c55e";
    }

    /// <summary>"4/10 today". Appends "· expires yyyy-MM-dd" when the key has an expiry.</summary>
    private static string FormatHubcapStats(HubcapStats stats)
    {
        string usage = string.Format(Resources.Strings.Settings_HubcapKeyOk, stats.DailyUsage, stats.DailyLimit);
        if (DateTimeOffset.TryParse(stats.ApiKeyExpiresAt, out var expiry))
            return string.Format(Resources.Strings.Settings_HubcapKeyOkExpiry, stats.DailyUsage, stats.DailyLimit,
                expiry.ToString("yyyy-MM-dd"));
        return usage;
    }
}
