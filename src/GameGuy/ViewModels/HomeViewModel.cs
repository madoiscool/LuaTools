using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameGuy.Services;

namespace GameGuy.ViewModels;

/// <summary>
/// Home dashboard: library stats, a "recently added" cover strip, and Steam/account status.
/// Reuses the same stplug-in scan + name/cover caches as the Manage page.
/// </summary>
public partial class HomeViewModel : ObservableObject
{
    /// <summary>Set by App: navigate to Manage and open this appid's detail (Home "recently added" click).</summary>
    public Action<long>? NavigateToGame { get; set; }

    // Section-navigation hooks wired by App (each → MainWindow.NavigateToXxx). Fire the matching command
    // from the clickable dashboard cells.
    public Action? NavigateToPlugin { get; set; }
    public Action? NavigateToManage { get; set; }
    public Action? NavigateToSettings { get; set; }
    public Action? NavigateToMode { get; set; }

    private readonly SteamService _steam;
    private readonly AuthService _auth;
    private readonly SteamAppListCache _appList;
    private readonly SteamAppInfoCache _appInfo;
    private readonly CoverCache _covers;
    private readonly UnlockerService _unlocker;
    private readonly PluginInstallerService _plugin;
    private readonly ToastService _toast;

    /// <summary>Drag-and-drop installer shown on the page; refreshes the library after a drop.</summary>
    public DropInstallViewModel Drop { get; }

    // ── Library stats ───────────────────────────────────────────────
    [ObservableProperty] private int _gameCount;

    // ── Store-page plugin status (at-a-glance on the dashboard) ─────
    [ObservableProperty] private string _pluginStatusText = Resources.Strings.Plugin_Checking;
    [ObservableProperty] private string _pluginStatusColor = "#9ca3af";
    /// <summary>Not installed → show the tile's inline Install button.</summary>
    [ObservableProperty] private bool _showPluginInstall;
    /// <summary>Install in progress → disable the button (via <see cref="NotInstallingPlugin"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotInstallingPlugin))]
    private bool _isInstallingPlugin;
    public bool NotInstallingPlugin => !IsInstallingPlugin;

    // ── Recently added strip ────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecent))]
    private ObservableCollection<LuaTileViewModel> _recent = [];

    public bool HasRecent => Recent.Count > 0;

    // ── Steam + account status ──────────────────────────────────────
    [ObservableProperty] private bool _steamFound;
    [ObservableProperty] private string _steamStatus = Resources.Strings.Home_CheckingSteam;

    /// <summary>True when an authentication key is stored (drives <see cref="AccountStatus"/>).</summary>
    [ObservableProperty] private bool _isSignedIn;

    [ObservableProperty] private string _accountStatus = Resources.Strings.Home_NoAuthKey;

    // ── Active unlocker mode ────────────────────────────────────────
    [ObservableProperty] private string _modeStatus = Resources.Strings.Home_NoModeSelected;

    public HomeViewModel(SteamService steam, AuthService auth,
        SteamAppListCache appList, SteamAppInfoCache appInfo, CoverCache covers, DropInstallViewModel drop,
        UnlockerService unlocker, PluginInstallerService plugin, ToastService toast)
    {
        _steam = steam;
        _auth = auth;
        _appList = appList;
        _appInfo = appInfo;
        _covers = covers;
        _unlocker = unlocker;
        _plugin = plugin;
        _toast = toast;
        Drop = drop;
        _auth.AuthStateChanged += RefreshAccount;
        // Library refresh on any install (drag-drop, plugin, Add page, Fixes) is driven by
        // LuaInstaller.Installed, wired in App → RefreshLibraryAsync.
    }

    /// <summary>Open a recently-added game in the Manage detail view.</summary>
    [RelayCommand]
    private void OpenGame(LuaTileViewModel tile) => NavigateToGame?.Invoke(tile.AppId);

    // Clickable dashboard cells → section navigation.
    [RelayCommand] private void OpenPlugin() => NavigateToPlugin?.Invoke();
    [RelayCommand] private void OpenManage() => NavigateToManage?.Invoke();
    [RelayCommand] private void OpenSettings() => NavigateToSettings?.Invoke();
    [RelayCommand] private void OpenMode() => NavigateToMode?.Invoke();

    /// <summary>Inline install of the store-page plugin from the Home tile (mirrors PluginViewModel.Install):
    /// confirm the Steam restart, install, toast the outcome, then refresh the tile.</summary>
    [RelayCommand]
    private async Task InstallPlugin()
    {
        if (IsInstallingPlugin) return;
        var confirm = System.Windows.MessageBox.Show(
            Resources.Strings.Plugin_Confirm_RestartBody,
            Resources.Strings.Plugin_Confirm_RestartCaption,
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.OK) return;

        IsInstallingPlugin = true;
        PluginStatusText = Resources.Strings.Plugin_Checking;
        PluginStatusColor = "#9ca3af";
        try
        {
            var (ok, error) = await _plugin.InstallAsync(progress: null);
            _toast.Show(Resources.Strings.Plugin_Toast_Title, ok
                ? Resources.Strings.Plugin_Toast_Installed
                : string.Format(Resources.Strings.Plugin_Toast_InstallFailed, error), error: !ok);
        }
        finally
        {
            IsInstallingPlugin = false;
            await RefreshPluginStatusAsync();
        }
    }

    /// <summary>Called when the page is shown. Refresh everything.</summary>
    public async Task LoadAsync()
    {
        RefreshSteam();
        RefreshAccount();
        RefreshMode();
        _ = RefreshPluginStatusAsync(); // fire-and-forget: may hit GitHub, must not delay the page
        await RefreshLibraryAsync();
    }

    /// <summary>Populate the "Plugin Status" dashboard tile from the same source the Plugin page uses.</summary>
    private async Task RefreshPluginStatusAsync()
    {
        try
        {
            var st = await _plugin.GetStatusAsync(force: false);
            bool installed = st.FrontendInstalled && st.DllInstalled;
            ShowPluginInstall = !installed;
            (PluginStatusText, PluginStatusColor) =
                !installed         ? (Resources.Strings.Plugin_Status_NotInstalled,   "#9ca3af")
                : st.UpdateAvailable ? (Resources.Strings.Plugin_Badge_UpdateAvailable, "#fbbf24")
                : (st.InstalledTag is { } tag
                      ? $"{Resources.Strings.Plugin_Status_Installed} · {tag}"
                      : Resources.Strings.Plugin_Status_Installed, "#34d399");
        }
        catch { /* leave the prior value (e.g. "Checking…") on any failure */ }
    }

    private void RefreshMode() =>
        ModeStatus = _unlocker.SelectedModeDisplayName is { } name
            ? string.Format(Resources.Strings.Home_ModeIs, name)
            : Resources.Strings.Home_NoModeSelected;

    private void RefreshSteam()
    {
        SteamFound = _steam.IsValid;
        SteamStatus = SteamFound
            ? string.Format(Resources.Strings.Home_SteamDetected, _steam.EffectivePath)
            : Resources.Strings.Home_SteamNotFound;
    }

    /// <summary>Rebuild the library count + "Recently added" strip (and warm the recent covers). Public
    /// so App can call it from LuaInstaller.Installed to refresh live after any add.</summary>
    public async Task RefreshLibraryAsync()
    {
        string? dir = _steam.StPlugInDir;
        if (dir is null || !Directory.Exists(dir))
        {
            GameCount = 0;
            Recent = [];
            return;
        }

        await _appList.EnsureLoadedAsync();

        var tiles = await Task.Run(() =>
            Directory.EnumerateFiles(dir, "*.lua")
                .Select(path => (path, name: Path.GetFileNameWithoutExtension(path)))
                .Where(f => long.TryParse(f.name, out _))
                .Select(f =>
                {
                    long appid = long.Parse(f.name);
                    var info = new FileInfo(f.path);
                    string? name = _appList.GetName(appid) ?? _appInfo.GetCached(appid)?.Name;
                    // Base = when added to the folder; if edited since (LastWrite later), use that. Newer is more relevant.
                    var added = info.LastWriteTime > info.CreationTime ? info.LastWriteTime : info.CreationTime;
                    return new LuaTileViewModel(appid, f.path, added, name ?? string.Format(Resources.Strings.Common_AppFallback, appid), name is null);
                })
                .OrderByDescending(t => t.AddedAt)
                .ToList());

        GameCount = tiles.Count;

        var recent = tiles.Take(4).ToList();
        Recent = new ObservableCollection<LuaTileViewModel>(recent);
        foreach (var t in recent) _ = t.EnsureResolvedAsync(_appInfo, _covers); // warm covers
    }

    private void RefreshAccount()
    {
        IsSignedIn = _auth.IsSignedIn;
        // No account identity exists any more: the status line is key state only.
        AccountStatus = IsSignedIn ? Resources.Strings.Settings_AuthKeyActive : Resources.Strings.Home_NoAuthKey;
    }
}
