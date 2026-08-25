using System.Windows;
using System.Windows.Threading;
using LuaToolsGui.Models;
using LuaToolsGui.Services;
using LuaToolsGui.ViewModels;
using LuaToolsGui.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LuaToolsGui;

public partial class App : Application
{
    private readonly IHost _host;
    // True when the app was cold-started solely to run a silent install AND MinimizeToTray is off,
    // which means we auto-exit after the balloon so we don't leave a ghost tray icon behind.
    private bool _exitAfterSilentInstall;

    public App()
    {
        // Subscribe to unhandled exceptions for diagnostic purposes
        this.DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (s, ev) =>
        {
            var ex = ev.ExceptionObject as Exception;
            System.IO.File.AppendAllText("crash.log", $"[AppDomain Unhandled] {ex}\n");
            MessageBox.Show(ex?.ToString() ?? "Unknown AppDomain error", "AppDomain Crash", MessageBoxButton.OK, MessageBoxImage.Error);
        };
        TaskScheduler.UnobservedTaskException += (s, ev) =>
        {
            System.IO.File.AppendAllText("crash.log", $"[TaskScheduler Unobserved] {ev.Exception}\n");
            MessageBox.Show(ev.Exception.ToString(), "TaskScheduler Crash", MessageBoxButton.OK, MessageBoxImage.Error);
        };

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                // Enable WPF data binding error tracing (Critical and Error levels)
                System.Diagnostics.PresentationTraceSources.DataBindingSource.Switch.Level = System.Diagnostics.SourceLevels.Critical | System.Diagnostics.SourceLevels.Error;
                services.AddSingleton<SettingsService>();
                services.AddSingleton<CacheService>();
                services.AddSingleton<SteamService>();
                services.AddSingleton<SteamAppListCache>();
                services.AddSingleton<SteamAppInfoCache>();
                services.AddSingleton<CoverCache>();
                services.AddSingleton<ToastService>();
                services.AddSingleton<SteamDepotInfo>();
                services.AddSingleton<LuaVault>();
                services.AddSingleton<Services.AppInfo.LaunchModStore>();
                services.AddSingleton<Services.AppInfo.LaunchOptionsService>();
                services.AddSingleton<LuaInstaller>();
                services.AddSingleton<SteamLibraryService>();
                services.AddSingleton<DonateKeysService>();
                services.AddSingleton<AnalyticsService>();
                services.AddSingleton<GithubProxy>();
                services.AddSingleton<HardwareAppIdService>();
                services.AddSingleton<SteamlessService>();
                services.AddSingleton<CloudRedirectService>();
                services.AddSingleton<UnlockerService>();
                services.AddSingleton<PluginInstallerService>();
                services.AddSingleton<Services.SAM.SamService>();
                services.AddTransient<DropInstallViewModel>(); // one per page (Home, Add)
                services.AddSingleton<AuthService>();
                services.AddSingleton<LuaToolsApiClient>();
                services.AddSingleton<HubcapService>();
                services.AddSingleton<UpdateService>();
                // Hook loader infrastructure
                services.AddSingleton<PluginAddService>();
                services.AddSingleton<HttpServerService>();
                services.AddHostedService(sp => sp.GetRequiredService<HttpServerService>());
                // Also resolvable as a plain singleton (not just IHostedService) so PluginInstallerService
                // can call ReloadPluginFilesAsync() after install/uninstall. Same pattern as HttpServerService.
                services.AddSingleton<CefInjectorService>();
                services.AddHostedService(sp => sp.GetRequiredService<CefInjectorService>());
                services.AddSingleton<DownloadViewModel>();
                services.AddSingleton<SettingsViewModel>();
                services.AddSingleton<ManageViewModel>();
                services.AddSingleton<BuildsViewModel>();
                services.AddSingleton<AchievementsViewModel>();
                services.AddTransient<LaunchOptionsViewModel>(); // one per dialog
                services.AddSingleton<HomeViewModel>();
                services.AddSingleton<ModeViewModel>();
                services.AddSingleton<FixesViewModel>();
                services.AddSingleton<PluginViewModel>();
                services.AddSingleton<OnboardingViewModel>();
                services.AddSingleton<MainViewModel>();
                // Pages resolved by NavigationView via the DI service provider.
                services.AddSingleton<HomeView>();
                services.AddSingleton<DownloadView>();
                services.AddSingleton<ManageView>();
                services.AddSingleton<BuildsView>();
                services.AddSingleton<AchievementsView>();
                services.AddSingleton<ModeView>();
                services.AddSingleton<FixesView>();
                services.AddSingleton<PluginView>();
                services.AddSingleton<SettingsView>();
                services.AddSingleton<MainWindow>();
            })
            .Build();
    }

    private UpdateService Updates => _host.Services.GetRequiredService<UpdateService>();

    // Guards RunUpdateFlowAsync so overlapping triggers (startup + the re-poke a DLL/Steam restart causes)
    // never run it concurrently. A second caller drops out immediately.
    private readonly System.Threading.SemaphoreSlim _updateFlowGate = new(1, 1);

    private async Task CheckLaunchOptionDriftAsync()
    {
        try
        {
            var launch = _host.Services.GetRequiredService<Services.AppInfo.LaunchOptionsService>();
            if (launch.Store.IsEmpty) return;
            var drifted = await Task.Run(launch.FindDrifted);
            if (drifted.Count == 0) return;
            var toast = _host.Services.GetRequiredService<ToastService>();
            Dispatcher.Invoke(() => toast.ShowAction(
                LuaToolsGui.Resources.Strings.Launch_Drift_Title,
                string.Format(LuaToolsGui.Resources.Strings.Launch_Drift_Body, drifted.Count),
                LuaToolsGui.Resources.Strings.Launch_Drift_Action,
                () => _ = ReapplyDriftedAsync(launch, drifted, toast)));
        }
        catch
        {
            // Cache locked/unreadable: nothing actionable, and this must never block startup.
        }
    }

    private static async Task ReapplyDriftedAsync(
        Services.AppInfo.LaunchOptionsService launch, IReadOnlyList<int> drifted, ToastService toast)
    {
        if (MessageBox.Show(
                LuaToolsGui.Resources.Strings.Launch_ApplyNow_Body,
                LuaToolsGui.Resources.Strings.Launch_ApplyNow_Title,
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        var result = await Task.Run(() => launch.Reapply(drifted));
        if (result.Ok)
            toast.Show(LuaToolsGui.Resources.Strings.Launch_Title,
                result.SteamWasRunning
                    ? LuaToolsGui.Resources.Strings.Launch_Applied_Restarted
                    : LuaToolsGui.Resources.Strings.Launch_Applied);
        else
            toast.Show(LuaToolsGui.Resources.Strings.Launch_Title,
                string.Format(LuaToolsGui.Resources.Strings.Launch_ApplyFailed, result.Error), error: true);
    }

    internal static Func<Task>? RunUpdateFlow;

    private async Task RunUpdateFlowAsync()
    {
        if (!_updateFlowGate.Wait(0)) return; // another run already in progress
        try
        {
            try { await Updates.CheckAndStageAsync(); } catch { /* offline / not installed */ }
            if (Updates.HasStagedUpdate)
            {
                Dispatcher.Invoke(() => Updates.ApplyAndRestart(new[] { "--minimized", "--tray-locked" }));
                return;
            }
            var installer = _host.Services.GetRequiredService<PluginInstallerService>();
            var st = await installer.GetStatusAsync(force: true);
            if (st.UpdateAvailable)
            {
                if (!st.DllMatches)
                {
                    var t = _host.Services.GetRequiredService<ToastService>();
                    Dispatcher.Invoke(() => t.Show("LuaTools", "Updating plugin. Steam will restart."));
                }
                await installer.InstallAsync(progress: null);
            }
        }
        finally { _updateFlowGate.Release(); }
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Legacy cleanup: older builds staged downloads in ~/Downloads/LuaTools (they now stage in %TEMP% and self-delete).
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                string legacy = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "LuaTools");
                if (System.IO.Directory.Exists(legacy)) System.IO.Directory.Delete(legacy, recursive: true);
            }
            catch { /* best effort, never block startup on cleanup */ }
        });
        await _host.StartAsync();
        // Rewrite any pre-3-mode SelectedMode BEFORE anything reads it.
        if (ModeMigration.Apply(_host.Services.GetRequiredService<SettingsService>()))
            _host.Services.GetRequiredService<CacheService>().OnboardingComplete = false;
        var main = _host.Services.GetRequiredService<MainViewModel>();
        var settingsVm = _host.Services.GetRequiredService<SettingsViewModel>();
        settingsVm.RequestRestart = RelaunchApp;
        var window = _host.Services.GetRequiredService<MainWindow>();
        settingsVm.RequestShowWindow = () => Dispatcher.Invoke(window.RestoreFromTray);
        if (Program.ShowWindowSignal is not null)
            System.Threading.ThreadPool.RegisterWaitForSingleObject(
                Program.ShowWindowSignal,
                (_, _) => Dispatcher.Invoke(() =>
                {
                    string? pending = ProtocolService.TryReadPending();
                    bool silent = pending is not null && ProtocolService.Parse(pending).Silent;
                    if (!silent) window.RestoreFromTray();
                    if (pending is not null) HandleProtocolUrl(pending);
                }),
                null, System.Threading.Timeout.Infinite, executeOnlyOnce: false);
        if (Program.EnableTrayLockSignal is not null)
            System.Threading.ThreadPool.RegisterWaitForSingleObject(
                Program.EnableTrayLockSignal,
                (_, _) => Program.SessionTrayLock = true,
                null, System.Threading.Timeout.Infinite, executeOnlyOnce: false);
        if (Program.RecheckUpdatesSignal is not null)
            System.Threading.ThreadPool.RegisterWaitForSingleObject(
                Program.RecheckUpdatesSignal,
                (_, _) => _ = RunUpdateFlowAsync(),
                null, System.Threading.Timeout.Infinite, executeOnlyOnce: false);
        RunUpdateFlow = RunUpdateFlowAsync;
        settingsVm.RequestSignIn = () => main.SignInCommand.ExecuteAsync(null);
        var toast = _host.Services.GetRequiredService<ToastService>();
        toast.Attach(window.RootSnackbar);
        settingsVm.RequestRestartPrompt = () => Dispatcher.Invoke(() =>
            toast.ShowAction(
                LuaToolsGui.Resources.Strings.Lang_Changed_Title,
                LuaToolsGui.Resources.Strings.Lang_Changed_Body,
                LuaToolsGui.Resources.Strings.Lang_Changed_Restart,
                () => settingsVm.RequestRestart?.Invoke()));
        var download = _host.Services.GetRequiredService<DownloadViewModel>();
        var manage = _host.Services.GetRequiredService<ManageViewModel>();
        var builds = _host.Services.GetRequiredService<BuildsViewModel>();
        var achievements = _host.Services.GetRequiredService<AchievementsViewModel>();
        // Manage page hooks
        manage.NavigateToAdd = appId => Dispatcher.Invoke(() => { window.NavigateToAdd(); download.SeedSearch(appId); });
        manage.NavigateToBuilds = appId => Dispatcher.Invoke(() => { window.NavigateToBuilds(); _ = builds.SelectAppAsync(appId); });
        manage.NavigateToAchievements = appId => Dispatcher.Invoke(() =>
        {
            window.NavigateToAchievements();
            _ = achievements.SelectGameAsync(new Models.SamGameInfo
            {
                Id = (uint)appId,
                Name = $"App {appId}",
                Type = "normal",
                DisplayCoverUrl = _host.Services.GetRequiredService<CoverCache>().GetCoverPathOrUrl(appId),
            });
        });
        manage.OpenLaunchOptions = (appId, name) => Dispatcher.Invoke(() =>
        {
            var dialog = new LaunchOptionsDialog(
                _host.Services.GetRequiredService<LaunchOptionsViewModel>(), appId, name)
            { Owner = window };
            dialog.ShowDialog();
        });
        _ = CheckLaunchOptionDriftAsync();
        // Home navigation
        var home = _host.Services.GetRequiredService<HomeViewModel>();
        Action<long> openInManage = appId => Dispatcher.Invoke(() => { window.NavigateToManage(); _ = manage.OpenDetailForAppIdAsync(appId); });
        home.NavigateToGame = openInManage;
        download.NavigateToGame = openInManage;
        builds.NavigateToManage = openInManage;
        // Drag‑and‑drop install
        Func<long, Task> installByAppId = appId =>
        {
            Dispatcher.Invoke(() => HandleProtocolUrl($"luatools://install/{appId}"));
            return Task.CompletedTask;
        };
        home.Drop.InstallByAppId = installByAppId;
        download.Drop.InstallByAppId = installByAppId;
        // Dashboard navigation
        home.NavigateToPlugin = () => Dispatcher.Invoke(window.NavigateToPlugin);
        home.NavigateToManage = () => Dispatcher.Invoke(window.NavigateToManage);
        home.NavigateToSettings = () => Dispatcher.Invoke(window.NavigateToSettings);
        home.NavigateToMode = () => Dispatcher.Invoke(window.NavigateToMode);
        main.Onboarding.RefreshHome = () => Dispatcher.Invoke(() => home.LoadAsync());
        // Refresh library on install
        var luaInstaller = _host.Services.GetRequiredService<LuaInstaller>();
        var appInfo = _host.Services.GetRequiredService<SteamAppInfoCache>();
        luaInstaller.Installed += appId => Dispatcher.InvokeAsync(async () =>
        {
            _ = manage.LoadAsync();
            _ = builds.LoadAsync();
            await home.RefreshLibraryAsync();
            if (await appInfo.EnsureFullDetailsAsync(appId))
                await home.RefreshLibraryAsync();
        });
        string? url = Program.StartupUrl ?? ProtocolService.TryReadPending();
        bool silentStartup = (url is not null && ProtocolService.Parse(url).Silent) || Program.StartMinimized;
        _exitAfterSilentInstall = silentStartup && Program.StartupUrl is not null && !settingsVm.MinimizeToTray;
        if (silentStartup)
        {
            window.StartSilent();
            try { await main.InitializeAsync(); } catch { /* offline → install proceeds as guest */ }
        }
        else
        {
            window.Show();
            var cache = _host.Services.GetRequiredService<CacheService>();
            if (!cache.OnboardingComplete)
            {
                var unlocker = _host.Services.GetRequiredService<UnlockerService>();
                var installer = _host.Services.GetRequiredService<PluginInstallerService>();
                bool configured = unlocker.SelectedMode is (UnlockerMode.Ost or UnlockerMode.Bst) && installer.IsInstalledLocally();
                if (configured) cache.OnboardingComplete = true; else main.Onboarding.IsOpen = true;
            }
        }
        if (url is not null) HandleProtocolUrl(url);
        if (Program.SessionTrayLock) _ = RunUpdateFlowAsync();
        _ = _host.Services.GetRequiredService<DonateKeysService>().SendPendingKeysIfEnabledAsync();
        _ = _host.Services.GetRequiredService<AnalyticsService>().TrackAppLaunchAsync();
        _ = _host.Services.GetRequiredService<HardwareAppIdService>().EnsureFreshAsync();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (Updates.HasStagedUpdate) Updates.ApplyOnExit();
        await _host.StopAsync();
        _host.Dispose();
        base.OnExit(e);
    }

    private void RelaunchApp()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe is not null)
            {
                var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe",
                    $"/c timeout /t 2 /nobreak >nul & start \"\" \"{exe}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                };
                System.Diagnostics.Process.Start(psi);
            }
        }
        catch { /* if relaunch fails, the user can reopen manually */ }
        finally
        {
            Shutdown();
        }
    }

    private void HandleProtocolUrl(string url)
    {
        var (action, appId, silent) = ProtocolService.Parse(url);
        if (action is null || appId is null) return;
        var window = _host.Services.GetRequiredService<MainWindow>();
        var download = _host.Services.GetRequiredService<DownloadViewModel>();
        var manage = _host.Services.GetRequiredService<ManageViewModel>();
        var fixes = _host.Services.GetRequiredService<FixesViewModel>();
        switch (action)
        {
            case "game":
                window.NavigateToAdd();
                download.SeedSearch(appId.Value);
                break;
            case "install":
                if (silent)
                {
                    _ = download.ProtocolInstall(appId.Value,
                        (msg, error) => Dispatcher.Invoke(() =>
                        {
                            window.ShowInstallNotification(msg, error);
                            if (_exitAfterSilentInstall)
                                _ = Task.Delay(6000).ContinueWith(_ => Dispatcher.Invoke(Shutdown));
                        }));
                }
                else
                {
                    window.NavigateToAdd();
                    _ = download.ProtocolInstall(appId.Value);
                }
                break;
            case "manage":
                window.NavigateToManage();
                _ = manage.OpenDetailForAppIdAsync(appId.Value);
                break;
            case "fix":
                window.NavigateToFixes();
                _ = fixes.OpenForAppIdAsync(appId.Value);
                break;
        }
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.ToString(), "Unhandled Exception", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
