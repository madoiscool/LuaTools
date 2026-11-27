using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameGuy.Services;

namespace GameGuy.ViewModels;

/// <summary>
/// "Plugin" page: the store-page plugin MANAGER. The app no longer bundles the frontend. It installs,
/// updates, and removes the LuaTools plugin (the "Add via LuaTools" button on Steam store pages) by
/// downloading it from GitHub releases via <see cref="PluginInstallerService"/>. One install path
/// (LuaLoader); if the Millennium mod is present it just coexists (and install disables Millennium's own
/// redundant gameguy plugin).
/// </summary>
public partial class PluginViewModel : ObservableObject
{
    private readonly PluginInstallerService _installer;
    private readonly ToastService _toast;

    public PluginViewModel(PluginInstallerService installer, ToastService toast)
    {
        _installer = installer;
        _toast = toast;
    }

    [ObservableProperty] private string _installedVersion = "—";
    [ObservableProperty] private string _latestVersion = "—";
    [ObservableProperty] private string _frontendStatus = Resources.Strings.Plugin_Checking;
    [ObservableProperty] private string _dllStatus = Resources.Strings.Plugin_Checking;

    // Per-component status flags: drive the colored status icons in the view (green check / amber
    // warning / grey dismiss). The *Status strings above stay the row label text.
    [ObservableProperty] private bool _frontendInstalled;
    [ObservableProperty] private bool _dllOk;
    [ObservableProperty] private bool _dllOutOfDate;
    [ObservableProperty] private bool _dllNotInstalled = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallButtonText))]
    [NotifyPropertyChangedFor(nameof(InstallIsPrimary))]
    [NotifyPropertyChangedFor(nameof(ShowUpToDate))]
    [NotifyPropertyChangedFor(nameof(CanUninstall))]
    private bool _isInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallButtonText))]
    [NotifyPropertyChangedFor(nameof(InstallIsPrimary))]
    [NotifyPropertyChangedFor(nameof(ShowUpToDate))]
    private bool _updateAvailable;

    /// <summary>True when the install button should be the loud green primary CTA. Only when there's an
    /// actionable state (fresh install or an update). A healthy up-to-date "Reinstall" stays secondary.</summary>
    public bool InstallIsPrimary => !IsInstalled || UpdateAvailable;

    /// <summary>Green "Up to date" pill on the version line. Only when installed and nothing to update.</summary>
    public bool ShowUpToDate => IsInstalled && !UpdateAvailable;

    /// <summary>True when the Millennium mod is detected. Shown as a "coexisting" info line, not a card.</summary>
    [ObservableProperty] private bool _millenniumCoexisting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotBusy))]
    [NotifyPropertyChangedFor(nameof(CanUninstall))]
    private bool _isBusy;
    public bool NotBusy => !IsBusy;
    public bool CanUninstall => IsInstalled && !IsBusy;

    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isProgressIndeterminate;

    /// <summary>Non-null shows a small info/error line under the buttons (e.g. offline).</summary>
    [ObservableProperty] private string? _statusLine;

    public string InstallButtonText => !IsInstalled
        ? Resources.Strings.Plugin_Btn_Install
        : UpdateAvailable ? Resources.Strings.Plugin_Btn_Update : Resources.Strings.Plugin_Btn_Reinstall;

    public async Task LoadAsync() => await RefreshAsync(force: false);

    private async Task RefreshAsync(bool force)
    {
        var st = await _installer.GetStatusAsync(force);
        IsInstalled = st.FrontendInstalled && st.DllInstalled;
        InstalledVersion = st.InstalledTag ?? (IsInstalled ? Resources.Strings.Plugin_Version_Unknown : "—");
        LatestVersion = st.Offline ? Resources.Strings.Plugin_Version_Offline : (st.LatestTag ?? "—");
        FrontendInstalled = st.FrontendInstalled;
        FrontendStatus = st.FrontendInstalled ? Resources.Strings.Plugin_Status_Installed : Resources.Strings.Plugin_Status_NotInstalled;
        DllOk = st.DllInstalled && st.DllMatches;
        DllOutOfDate = st.DllInstalled && !st.DllMatches;
        DllNotInstalled = !st.DllInstalled;
        DllStatus = !st.DllInstalled
            ? Resources.Strings.Plugin_Status_NotInstalled
            : st.DllMatches ? Resources.Strings.Plugin_Status_UpToDate : Resources.Strings.Plugin_Status_OutOfDate;
        UpdateAvailable = st.UpdateAvailable;
        MillenniumCoexisting = st.MillenniumPresent;
        // Offline takes priority (it's the more actionable/common case); the port warning is secondary and
        // only worth surfacing once we actually know install state, not on every offline check.
        StatusLine = st.Offline ? Resources.Strings.Plugin_Status_OfflineCheck
            : st.Port8080Busy ? Resources.Strings.Plugin_Status_Port8080Busy
            : null;
    }

    private bool ConfirmSteamRestart()
    {
        var result = System.Windows.MessageBox.Show(
            Resources.Strings.Plugin_Confirm_RestartBody,
            Resources.Strings.Plugin_Confirm_RestartCaption,
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Warning);
        return result == System.Windows.MessageBoxResult.OK;
    }

    private IProgress<double?> MakeProgress() => new Progress<double?>(p =>
    {
        if (p is null) { IsProgressIndeterminate = true; }
        else { IsProgressIndeterminate = false; Progress = p.Value * 100; }
    });

    [RelayCommand]
    private async Task Install()
    {
        if (IsBusy) return;
        if (!ConfirmSteamRestart()) return;

        IsBusy = true;
        IsProgressIndeterminate = true;
        Progress = 0;
        try
        {
            var (ok, error) = await _installer.InstallAsync(MakeProgress());
            _toast.Show(Resources.Strings.Plugin_Toast_Title, ok
                ? Resources.Strings.Plugin_Toast_Installed
                : string.Format(Resources.Strings.Plugin_Toast_InstallFailed, error), error: !ok);
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync(force: true);
        }
    }

    [RelayCommand]
    private async Task Uninstall()
    {
        if (IsBusy || !IsInstalled) return;
        if (!ConfirmSteamRestart()) return;

        IsBusy = true;
        IsProgressIndeterminate = true;
        try
        {
            var (ok, error) = await _installer.UninstallAsync();
            _toast.Show(Resources.Strings.Plugin_Toast_Title, ok
                ? Resources.Strings.Plugin_Toast_Removed
                : string.Format(Resources.Strings.Plugin_Toast_UninstallFailed, error), error: !ok);
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync(force: true);
        }
    }

    [RelayCommand]
    private async Task CheckForUpdates()
    {
        if (IsBusy) return;
        IsBusy = true;
        IsProgressIndeterminate = true;
        try
        {
            await RefreshAsync(force: true);
            if (StatusLine is null)
                _toast.Show(Resources.Strings.Plugin_Toast_Title,
                    UpdateAvailable ? Resources.Strings.Plugin_Toast_UpdateAvailable : Resources.Strings.Plugin_Toast_UpToDate);
        }
        finally { IsBusy = false; }
    }
}
