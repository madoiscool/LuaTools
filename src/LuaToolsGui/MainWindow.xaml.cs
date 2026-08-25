using System.Windows;
using LuaToolsGui.Services;
using LuaToolsGui.ViewModels;
using LuaToolsGui.Views;

namespace LuaToolsGui;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly SettingsService _settings;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private bool _reallyExiting; // true once the user picks tray "Exit". Lets the close go through

    public MainWindow(MainViewModel viewModel, IServiceProvider services, SettingsService settings)
    {
        _settings = settings;
        InitializeComponent();
        DataContext = viewModel;

        // NavigationView resolves page instances (DownloadView/SettingsView) from DI.
        RootNavigation.SetServiceProvider(services);

        InitializeTrayIcon();
        Closing += OnWindowClosing;

        Loaded += async (_, _) =>
        {
            RootNavigation.Navigate(typeof(HomeView));
            try { await viewModel.InitializeAsync(); }
            catch { /* auth restore failed (e.g. offline). UI still loads as guest */ }
        };
    }

    // ── System tray ─────────────────────────────────────────────────
    private void InitializeTrayIcon()
    {
        _trayIcon = new System.Windows.Forms.NotifyIcon { Text = "LuaTools", Visible = false };
        try
        {
            using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/icon.ico"))?.Stream;
            if (stream is not null) _trayIcon.Icon = new System.Drawing.Icon(stream);
        }
        catch { /* fall back to no icon. Tray menu still works */ }

        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(LuaToolsGui.Resources.Strings.Tray_Open, null, (_, _) => RestoreFromTray());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(LuaToolsGui.Resources.Strings.Tray_Exit, null, (_, _) => ExitApp());
        _trayIcon.ContextMenuStrip = menu;
    }

    /// <summary>Clicking the window's close (X) button hides to the tray instead of quitting, when the
    /// MinimizeToTray setting is on OR the app was launched with --tray-locked (the loader passes this to
    /// keep the backend alive. Session-only, doesn't touch the saved setting). Either way, only the tray
    /// "Exit" item (which sets _reallyExiting) actually closes the app.</summary>
    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_reallyExiting && (_settings.MinimizeToTray || Program.SessionTrayLock))
        {
            e.Cancel = true;
            Hide();
            if (_trayIcon is not null) _trayIcon.Visible = true;
            return;
        }
        _trayIcon?.Dispose();

        // ShutdownMode is OnExplicitShutdown (a silent/--minimized launch never shows a window, so
        // OnLastWindowClose would otherwise tear the app down before it does anything), so a real
        // window close has to ask for shutdown itself instead of relying on the window count.
        Application.Current.Shutdown();
    }

    /// <summary>Quit for real from the tray "Exit" item (bypasses close-to-tray).</summary>
    private void ExitApp()
    {
        _reallyExiting = true;
        if (_trayIcon is not null) _trayIcon.Visible = false;
        Close();
    }

    /// <summary>Bring the window back from the tray (tray double-click, "Open" menu, or Minimize-to-tray
    /// being turned off in Settings).</summary>
    public void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        if (_trayIcon is not null) _trayIcon.Visible = false;

        // Activate() alone often loses to Windows' foreground rules when the request comes from another
        // process (a relaunch). Bouncing Topmost reliably pulls the window to the front.
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    /// <summary>Launch tray-only for a headless install (luatools://install/silent/&lt;id&gt;): show the tray
    /// icon but never surface the window. The app sits in the background and reports via a balloon tip.</summary>
    public void StartSilent()
    {
        if (_trayIcon is not null) _trayIcon.Visible = true;
    }

    /// <summary>Pop a Windows balloon from the tray icon. Reports a silent install's outcome.</summary>
    public void ShowInstallNotification(string message, bool error)
    {
        if (_trayIcon is null) return;
        _trayIcon.Visible = true;
        _trayIcon.ShowBalloonTip(
            5000,
            "LuaTools",
            message,
            error ? System.Windows.Forms.ToolTipIcon.Error : System.Windows.Forms.ToolTipIcon.Info);
    }

    /// <summary>Switch to the Add page (used by the Manage page's "Update" action).</summary>
    public void NavigateToAdd() => RootNavigation.Navigate(typeof(DownloadView));

    /// <summary>Switch to Manage (used by Home's "recently added" cards). Caller opens the detail.</summary>
    public void NavigateToManage() => RootNavigation.Navigate(typeof(ManageView));

    /// <summary>Switch to Builds (used by the Manage flyout's "Manage Build"). Caller selects the game.</summary>
    public void NavigateToBuilds() => RootNavigation.Navigate(typeof(BuildsView));

    /// <summary>Switch to Achievements (Steam Achievement Manager).</summary>
    public void NavigateToAchievements() => RootNavigation.Navigate(typeof(AchievementsView));

    /// <summary>Switch to Settings (used when a guest hits a protected action).</summary>
    public void NavigateToSettings() => RootNavigation.Navigate(typeof(SettingsView));

    /// <summary>Switch to Fixes (used by the protocol handler to open a specific game's fixes).</summary>
    public void NavigateToFixes() => RootNavigation.Navigate(typeof(FixesView));

    /// <summary>Switch to Plugin (used by Home's "Plugin Status" tile).</summary>
    public void NavigateToPlugin() => RootNavigation.Navigate(typeof(PluginView));

    /// <summary>Switch to Mode (used by Home's mode status row).</summary>
    public void NavigateToMode() => RootNavigation.Navigate(typeof(ModeView));

    // "Restart Steam" is an action, not a page: run the command, don't leave it selected.
    private void RestartSteam_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.RestartSteamCommand.Execute(null);
    }
}
