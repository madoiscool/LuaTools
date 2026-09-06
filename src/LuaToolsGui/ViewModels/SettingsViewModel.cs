using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaToolsGui.Services;
using LuaToolsGui.Models;
using Microsoft.Win32;

namespace LuaToolsGui.ViewModels;

/// <summary>A selectable UI language. <see cref="Tag"/> is the BCP-47 tag ("en", "zh-Hans") or null for
/// "follow the system display language".</summary>
public record LanguageOption(string Display, string? Tag);

public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly AuthService _auth;
    private readonly SteamService _steam;
    private readonly HubcapService _hubcap;
    private readonly Services.Sources.SourcePackRegistry _packs;

    [ObservableProperty] private string? _displayName;
    [ObservableProperty] private string? _email;
    [ObservableProperty] private string? _avatarUrl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRealUser))]
    private bool _isGuest = true;

    public bool IsRealUser => !IsGuest;

    // ── Login-required redirect banner ─────────────────────────────
    /// <summary>Set by App when navigating here from a protected action. Null = banner hidden.</summary>
    [ObservableProperty] private string? _loginRequiredMessage;

    [RelayCommand]
    private void DismissLoginRequired() => LoginRequiredMessage = null;

    // ── Bot-provisioned account re-link banner ──────────────────────
    /// <summary>True when the signed-in session is a Discord bot placeholder (@bot.lua.tools).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBotLinkBanner))]
    private bool _isBotProvisioned;

    /// <summary>Session-only. Resets next launch so the banner re-checks on every startup.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBotLinkBanner))]
    private bool _botBannerDismissed;

    public bool ShowBotLinkBanner => false;

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
    private const string RunValueName = "LuaTools";

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

    /// <summary>Set by App so the guest "Sign in" button can run the Discord flow.</summary>
    public Func<Task>? RequestSignIn { get; set; }

    /// <summary>Set by App: actually relaunch the app (used after a language change).</summary>
    public Action? RequestRestart { get; set; }

    /// <summary>Show the "language changed. Restart now?" toast. Wired here so the VM stays UI-agnostic;
    /// App provides the toast + restart action.</summary>
    public Action? RequestRestartPrompt { get; set; }

    public SettingsViewModel(SettingsService settings, AuthService auth, SteamService steam,
        HubcapService hubcap, Services.Sources.SourcePackRegistry packs)
    {
        _packs = packs;
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

        // Select the saved language (or "System default") without firing the restart prompt.
        _suppressLanguagePrompt = true;
        _selectedLanguage = LanguageOptions.FirstOrDefault(o => o.Tag == settings.Language) ?? LanguageOptions[0];
        _suppressLanguagePrompt = false;
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

    public void RefreshAccount()
    {
        IsGuest = _auth.IsGuest;
        DisplayName = _auth.DisplayName;
        Email = _auth.Email;
        AvatarUrl = _auth.AvatarUrl;
        IsBotProvisioned = _auth.IsBotProvisioned;
        if (!IsGuest) LoginRequiredMessage = null;
    }

    /// <summary>Hide the re-link banner for this session (returns next launch if still a bot account).</summary>
    [RelayCommand]
    private void DismissBotBanner() => BotBannerDismissed = true;

    [RelayCommand]
    private async Task SignInAsync()
    {
        if (RequestSignIn is not null) await RequestSignIn();
    }

    // ── Discord bot code sign-in ────────────────────────────────────

    /// <summary>The 6-char code the user typed from the Discord <c>/login</c> DM.</summary>
    [ObservableProperty] private string _codeInput = "";

    /// <summary>True while redeeming. Disables the Redeem button via <see cref="CanRedeemCode"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRedeemCode))]
    private bool _isRedeemingCode;

    /// <summary>Error shown under the code box (expired/invalid/server). Null = hidden.</summary>
    [ObservableProperty] private string? _codeError;

    public bool CanRedeemCode => !IsRedeemingCode;

    /// <summary>Redeem the Discord bot code for a session (no browser needed).</summary>
    [RelayCommand]
    private async Task SignInWithCodeAsync()
    {
        string code = CodeInput.Trim();
        if (code.Length != 6) return;

        IsRedeemingCode = true;
        CodeError = null;
        try
        {
            await _auth.SignInWithCodeAsync(code);
            CodeInput = "";
        }
        catch (Exception ex)
        {
            CodeError = ex.Message;
        }
        finally
        {
            IsRedeemingCode = false;
        }
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

    [RelayCommand]
    private void SignOut() => _auth.SignOut();

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

        // Re-read the source-pack folder every time the page is shown: dropping a file in and coming
        // back here to check it was accepted is the natural gesture. Cheap - a handful of small files.
        RefreshSourcePacks();
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
    // ── Manifest source packs ────────────────────────────────────────

    /// <summary>One line per .json file found: what it contributed, or why it was refused.</summary>
    public System.Collections.ObjectModel.ObservableCollection<string> SourcePacks { get; } = [];

    public bool HasSourcePacks => SourcePacks.Count > 0;

    /// <summary>
    /// Re-read the source-pack folder. Called when the page is shown, so dropping a file in and coming
    /// back here is enough to see whether it was accepted - no restart, since a pack is only ever data.
    /// </summary>
    public void RefreshSourcePacks()
    {
        _packs.Reload(Models.SourceMeta.All.Keys.ToList());

        SourcePacks.Clear();
        foreach (var pack in _packs.Packs)
        {
            string detail = pack.Error is not null
                ? pack.Error
                : string.Format(Resources.Strings.Settings_SourcePacks_Count, pack.Sources.Count);
            SourcePacks.Add($"{pack.DisplayName} — {detail}");
        }
        OnPropertyChanged(nameof(HasSourcePacks));
    }

    [RelayCommand]
    private void OpenSourcesFolder()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Services.Sources.SourcePackRegistry.Root);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                Services.Sources.SourcePackRegistry.Root) { UseShellExecute = true });
        }
        catch { /* opening a folder is never worth an error dialog */ }
    }
}
