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
        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<SettingsService>();
                services.AddSingleton<CacheService>();
                services.AddSingleton<SteamService>();
                services.AddSingleton<SteamAppListCache>();
                services.AddSingleton<SteamAppInfoCache>();
                services.AddSingleton<CoverCache>();
                services.AddSingleton<FixLookupService>();
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

    /// <summary>
    /// Warn when Steam has overwritten launch options we'd applied, and offer to put them back.
    ///
    /// <para>
    /// Steam rebuilds appinfo.vdf from PICS on login, app updates and store browsing. It did so twice
    /// while this feature was being written, so an applied edit is not permanent. Re-applying is offered
    /// but never automatic: it closes Steam, which is not something to do behind the user's back at
    /// startup. Costs nothing when no launch options have been edited (the store short-circuits on empty).
    /// </para>
    /// </summary>
    private async Task CheckLaunchOptionDriftAsync()
    {
        try
        {
            var launch = _host.Services.GetRequiredService<Services.AppInfo.LaunchOptionsService>();
            if (launch.Store.IsEmpty) return;

            // Indexing the ~373 MB cache takes a couple of seconds, never on the UI thread.
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

    /// <summary>
    /// The drift notice's "Re-apply" button: confirm, then write the staged edits back into appinfo.
    ///
    /// <para>
    /// The write runs OFF the UI thread. Unlike the launch-options dialog (which is modal, so its own
    /// synchronous apply merely blocks a window that's already blocking), this fires with the main window
    /// live, and <c>Apply</c> indexes a ~373 MB file, copies a backup and rewrites it. On the UI thread
    /// that's a multi-second freeze of the whole app.
    /// </para>
    /// </summary>
    private static async Task ReapplyDriftedAsync(
        Services.AppInfo.LaunchOptionsService launch, IReadOnlyList<int> drifted, ToastService toast)
    {
        // Same wording as the dialog's own prompt: closing Steam should never read as a different
        // decision depending on where it was triggered from.
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

    /// <summary>Set by OnStartup to <see cref="RunUpdateFlowAsync"/> so non-UI callers (e.g. the
    /// /check-updates HTTP handler) can run the exact same update flow instead of a divergent one.</summary>
    internal static Func<Task>? RunUpdateFlow;

    /// <summary>The Steam-open update flow (fully silent): update the APP first, unconditionally, before
    /// ever touching the plugin. Then, once the running app is guaranteed current, check/apply a plugin
    /// update against it. Called on a loader (--tray-locked) launch and on the Steam-open re-check poke;
    /// safe to call repeatedly.
    /// <para>
    /// App-before-plugin is load-bearing, not just tidy ordering: the app and plugin are NOT independently
    /// safe to update out of order whenever a plugin release changes something the app's own compiled code
    /// depends on (e.g. <see cref="Services.CefInjectorService"/>'s CDP port is a compile-time constant.
    /// An old app build talking to a freshly-updated plugin that moved the port simply can't connect, and
    /// won't self-heal until the app itself happens to update, which is not guaranteed to land in the same
    /// pass: the app and plugin ship from separate repos on separate cadences, so one can succeed while the
    /// other fails/lags). Restarting into the latest app FIRST, before it goes anywhere near a plugin
    /// update, means whatever the plugin changes is always applied by a process that already understands
    /// it.
    /// </para></summary>
    private async Task RunUpdateFlowAsync()
    {
        if (!_updateFlowGate.Wait(0)) return; // another run already in progress
        try
        {
            // 1) Stage + immediately apply any app update, before touching the plugin at all.
            //    ApplyAndRestart() terminates this process; the relaunched instance (launched with
            //    --tray-locked) re-enters this same flow via OnStartup once it's already current, so this
            //    run's job ends here. There is nothing safe left for THIS process to do.
            try { await Updates.CheckAndStageAsync(); } catch { /* offline / not installed */ }
            if (Updates.HasStagedUpdate)
            {
                Dispatcher.Invoke(() => Updates.ApplyAndRestart(new[] { "--minimized", "--tray-locked" }));
                return;
            }

            // 2) No app update pending: safe to check/apply a plugin update against this (already-current) app.
            try
            {
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
            catch { /* offline / install error. Retry next Steam-open */ }
        }
        finally { _updateFlowGate.Release(); }
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Legacy cleanup: older builds staged downloads in ~/Downloads/LuaTools (they now stage in
        // %TEMP% and self-delete). Remove any leftovers from that user-visible folder, best-effort.
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

        // Rewrite any pre-3-mode SelectedMode BEFORE anything reads it. UnlockerService.SelectedMode
        // would otherwise parse a legacy value to null and quietly present an unconfigured app. Users
        // whose mode was retired outright (SteamTools, the CloudRedirect fix) have no mode now, so
        // onboarding is forced back open: OnboardingComplete is a permanent flag that every existing
        // user already has set, and clearing SelectedMode alone would leave them with no mode AND no
        // overlay explaining why.
        if (ModeMigration.Apply(_host.Services.GetRequiredService<SettingsService>()))
            _host.Services.GetRequiredService<CacheService>().OnboardingComplete = false;

        var main = _host.Services.GetRequiredService<MainViewModel>();
        var settingsVm = _host.Services.GetRequiredService<SettingsViewModel>();

        // Changing the language needs a relaunch (x:Static resources resolve at parse time).
        settingsVm.RequestRestart = RelaunchApp;

        var window = _host.Services.GetRequiredService<MainWindow>();

        // Turning off "Minimize to tray" while hidden in the tray → bring the window back.
        settingsVm.RequestShowWindow = () => Dispatcher.Invoke(window.RestoreFromTray);

        // Relaunching the app (single-instance) signals this event → surface the existing window and
        // check for any protocol URL a second instance wrote. AutoReset + executeOnlyOnce:false so it
        // keeps firing for every relaunch.
        if (Program.ShowWindowSignal is not null)
            System.Threading.ThreadPool.RegisterWaitForSingleObject(
                Program.ShowWindowSignal,
                (_, _) => Dispatcher.Invoke(() =>
                {
                    // A silent install relaunch stays headless: don't surface the window for it.
                    string? pending = ProtocolService.TryReadPending();
                    bool silent = pending is not null && ProtocolService.Parse(pending).Silent;
                    if (!silent)
                        window.RestoreFromTray();
                    if (pending is not null)
                        HandleProtocolUrl(pending);
                }),
                null, System.Threading.Timeout.Infinite, executeOnlyOnce: false);

        // A --tray-locked relaunch (the loader) signals this → enable close-to-tray for the session even if
        // this instance was started without the flag. Idempotent; keeps firing for every relaunch.
        if (Program.EnableTrayLockSignal is not null)
            System.Threading.ThreadPool.RegisterWaitForSingleObject(
                Program.EnableTrayLockSignal,
                (_, _) => Program.SessionTrayLock = true,
                null, System.Threading.Timeout.Infinite, executeOnlyOnce: false);

        // A --tray-locked relaunch (the loader on Steam-open) signals this → re-run the update flow so an
        // already-running app still updates when the user opens Steam. Guarded internally against overlap.
        if (Program.RecheckUpdatesSignal is not null)
            System.Threading.ThreadPool.RegisterWaitForSingleObject(
                Program.RecheckUpdatesSignal,
                (_, _) => _ = RunUpdateFlowAsync(),
                null, System.Threading.Timeout.Infinite, executeOnlyOnce: false);

        // Expose the same flow to non-UI callers (the /check-updates HTTP handler).
        RunUpdateFlow = RunUpdateFlowAsync;

        // Settings' own "Sign in with Discord" button → browser OAuth (unchanged).
        settingsVm.RequestSignIn = () => main.SignInCommand.ExecuteAsync(null);

        // Guests hitting a protected action on other pages → navigate to Settings with context banner.
        Func<Task> navigateToSignIn = () =>
        {
            Dispatcher.Invoke(() =>
            {
                settingsVm.LoginRequiredMessage = LuaToolsGui.Resources.Strings.Settings_LoginRequired;
                window.NavigateToSettings();
            });
            return Task.CompletedTask;
        };
        _host.Services.GetRequiredService<DownloadViewModel>().RequestSignIn = navigateToSignIn;
        _host.Services.GetRequiredService<FixesViewModel>().RequestSignIn = navigateToSignIn;
        var toast = _host.Services.GetRequiredService<ToastService>();
        toast.Attach(window.RootSnackbar); // wire the presenter before anything can raise a toast

        // Language changed → persistent toast offering an immediate relaunch.
        settingsVm.RequestRestartPrompt = () => Dispatcher.Invoke(() =>
            toast.ShowAction(
                LuaToolsGui.Resources.Strings.Lang_Changed_Title,
                LuaToolsGui.Resources.Strings.Lang_Changed_Body,
                LuaToolsGui.Resources.Strings.Lang_Changed_Restart,
                () => settingsVm.RequestRestart?.Invoke()));

        // App updates now apply silently via RunUpdateFlowAsync (restart-on-Steam-open, unconditionally
        // and before any plugin update), so no "Restart" prompt toast.
        var download = _host.Services.GetRequiredService<DownloadViewModel>();

        var manage = _host.Services.GetRequiredService<ManageViewModel>();

        // Manage page "Update" → go to the Add page pre-seeded with that appid.
        manage.NavigateToAdd = appId =>
            Dispatcher.Invoke(() => { window.NavigateToAdd(); download.SeedSearch(appId); });

        // Manage flyout "Manage Build" → go to the Builds page with that game selected.
        var builds = _host.Services.GetRequiredService<BuildsViewModel>();
        manage.NavigateToBuilds = appId =>
            Dispatcher.Invoke(() => { window.NavigateToBuilds(); _ = builds.SelectAppAsync(appId); });

        // Manage flyout "Launch options…" → modal editor over Steam's appinfo cache.
        manage.OpenLaunchOptions = (appId, name) => Dispatcher.Invoke(() =>
        {
            var dialog = new LaunchOptionsDialog(
                _host.Services.GetRequiredService<LaunchOptionsViewModel>(), appId, name)
            { Owner = window };
            dialog.ShowDialog();
        });

        // Steam regenerates appinfo.vdf from PICS, wiping launch edits. Check once at startup and
        // OFFER to re-apply, never silently, since applying closes Steam.
        _ = CheckLaunchOptionDriftAsync();

        // Home "recently added" + Add install banner "Reveal" → go to Manage and open that game's detail.
        Action<long> openInManage = appId =>
            Dispatcher.Invoke(() => { window.NavigateToManage(); _ = manage.OpenDetailForAppIdAsync(appId); });
        var home = _host.Services.GetRequiredService<HomeViewModel>();
        home.NavigateToGame = openInManage;
        download.NavigateToGame = openInManage;

        // Add page → the post-fetch "this game has a fix" banner opens it straight in the Fixes page.
        download.OpenFixesForGame = appId => Dispatcher.Invoke(() =>
        {
            window.NavigateToFixes();
            _ = _host.Services.GetRequiredService<FixesViewModel>().OpenForAppIdAsync(appId);
        });
        builds.NavigateToManage = openInManage; // Builds "Manage" button: the reverse of "Manage Build"

        // Dragging a SteamDB / Steam store link onto either drop box installs that appid. Routed through
        // HandleProtocolUrl rather than calling ProtocolInstall directly, so a dropped link and
        // luatools://install/<id> are literally the same path and can't drift apart later.
        // DropInstallViewModel is transient, so Home and Add each hold their own instance.
        Func<long, Task> installByAppId = appId =>
        {
            Dispatcher.Invoke(() => HandleProtocolUrl($"luatools://install/{appId}"));
            return Task.CompletedTask;
        };
        home.Drop.InstallByAppId = installByAppId;
        download.Drop.InstallByAppId = installByAppId;

        // Home dashboard cells → section navigation.
        home.NavigateToPlugin = () => Dispatcher.Invoke(window.NavigateToPlugin);
        home.NavigateToManage = () => Dispatcher.Invoke(window.NavigateToManage);
        home.NavigateToSettings = () => Dispatcher.Invoke(window.NavigateToSettings);
        home.NavigateToMode = () => Dispatcher.Invoke(window.NavigateToMode);

        // Onboarding finished applying its actions → refresh the Home dashboard tiles (mode + plugin status).
        main.Onboarding.RefreshHome = () => Dispatcher.Invoke(() => home.LoadAsync());

        // Any game added (plugin store-page button, drag-drop, Add page, Fixes) → refresh the library views
        // live. LuaInstaller.Installed can fire on a background thread (plugin install), so marshal to UI.
        var luaInstaller = _host.Services.GetRequiredService<LuaInstaller>();
        var appInfo = _host.Services.GetRequiredService<SteamAppInfoCache>();
        luaInstaller.Installed += appId => Dispatcher.InvokeAsync(async () =>
        {
            _ = manage.LoadAsync();            // re-scan so Manage updates too if it's the visible page
            _ = builds.LoadAsync();            // a newly installed lua is a new variant in the vault
            await home.RefreshLibraryAsync();  // game appears (its cover may lag for newer titles)

            // Newer titles have no guessable header URL: the classic CDN path 404s and the real header is
            // a content-hashed store_item_assets URL that only comes from appdetails. Warm that game's
            // details at interactive priority (retries past throttling), then refresh again so its cover
            // fills in instead of staying blank until an app restart.
            if (await appInfo.EnsureFullDetailsAsync(appId))
                await home.RefreshLibraryAsync();
        });

        // Handle a protocol URL from the command line (first launch) or from a temp file left by a
        // second instance that exited before the signal listener was wired up.
        string? url = Program.StartupUrl ?? ProtocolService.TryReadPending();

        // A silent install launch (luatools://install/silent/<id>) runs headless: stay in the tray and
        // never surface the window. The window's Loaded handler (which restores auth) won't fire when we
        // skip Show(), so restore the session explicitly before the install runs.
        bool silentStartup = (url is not null && ProtocolService.Parse(url).Silent) || Program.StartMinimized;

        // Auto-exit after a silent install only when this was a COLD launch for it (StartupUrl came on the
        // command line, not from an already-running second instance) AND the user doesn't keep a tray app
        // around. Otherwise the app was already living somewhere and must stay.
        _exitAfterSilentInstall = silentStartup
            && Program.StartupUrl is not null
            && !settingsVm.MinimizeToTray;

        if (silentStartup)
        {
            window.StartSilent();
            try { await main.InitializeAsync(); } catch { /* offline → install proceeds as guest */ }
        }
        else
        {
            window.Show();

            // First-run onboarding: show the welcome overlay on a fresh install. Skip it (and mark done)
            // when the user is already set up (a managed mode selected AND the plugin installed), so
            // existing users / dev machines aren't nagged. Marking done here is permanent, so switching
            // mode later never re-triggers onboarding (only ModeMigration ever clears it again).
            var cache = _host.Services.GetRequiredService<CacheService>();
            if (!cache.OnboardingComplete)
            {
                var unlocker = _host.Services.GetRequiredService<UnlockerService>();
                var installer = _host.Services.GetRequiredService<PluginInstallerService>();
                // Custom deliberately doesn't count: a first-run user can't meaningfully choose "I'll
                // manage it myself" before they've been shown what the options are.
                bool configured =
                    unlocker.SelectedMode is (UnlockerMode.Ost or UnlockerMode.Bst)
                    && installer.IsInstalledLocally();
                if (configured) cache.OnboardingComplete = true;
                else main.Onboarding.IsOpen = true;
            }
        }

        if (url is not null)
            HandleProtocolUrl(url);

        // Background, non-blocking Steam-open update flow (app + plugin), but ONLY in the loader context
        // (--tray-locked). A manual / protocol / silent-install launch skips it, so the app never
        // auto-updates or restarts mid-manual-use. It only happens when Steam launches us. (Velopack only
        // updates to a STRICTLY HIGHER version, so every release must bump --packVersion.)
        if (Program.SessionTrayLock)
            _ = RunUpdateFlowAsync();

        // Background, non-blocking key donation (runs only when the setting is on; silent + deduped).
        _ = _host.Services.GetRequiredService<DonateKeysService>().SendPendingKeysIfEnabledAsync();

        // Anonymous app-launch ping (Umami). Fire-and-forget; never blocks.
        _ = _host.Services.GetRequiredService<AnalyticsService>().TrackAppLaunchAsync();

        // Warm the hardware-appid blacklist (refreshes from GitHub if the cache is stale). Fire-and-forget.
        _ = _host.Services.GetRequiredService<HardwareAppIdService>().EnsureFreshAsync();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        // If an update was downloaded but not yet applied, stage it for after exit.
        if (Updates.HasStagedUpdate)
            Updates.ApplyOnExit();

        await _host.StopAsync();
        _host.Dispose();
        base.OnExit(e);
    }

    /// <summary>Relaunch the app (used after a language change). The single-instance mutex is released
    /// only when THIS process exits, so we start the new instance via a short delayed shell command. By
    /// the time it launches the exe, our mutex is free and the new instance won't bow out.</summary>
    private void RelaunchApp()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe is not null)
            {
                // cmd: wait ~1.2s for this process's mutex to release, then start the exe detached.
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

    /// <summary>Route a luatools:// protocol URL to the appropriate page and action.</summary>
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
                    // Headless: don't navigate or surface; install in the background, then a tray balloon.
                    _ = download.ProtocolInstall(appId.Value,
                        (msg, error) => Dispatcher.Invoke(() =>
                        {
                            window.ShowInstallNotification(msg, error);
                            // Cold launch + no tray app wanted → exit once the balloon has had time to show.
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
}
