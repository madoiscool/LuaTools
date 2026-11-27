using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaToolsGui.Models;
using LuaToolsGui.Services;

namespace LuaToolsGui.ViewModels;

/// <summary>
/// First-run welcome overlay. Shown once (gated by <see cref="CacheService.OnboardingComplete"/>) over the
/// whole app: asks for the authentication key, offers "apply recommended settings" (BetterSteamTools +
/// FastFetch) and "install the plugin", then applies the chosen actions on "Let's go!" and dismisses.
/// </summary>
public partial class OnboardingViewModel : ObservableObject
{
    private readonly AuthService _auth;
    private readonly CacheService _cache;
    private readonly SettingsService _settings;
    private readonly UnlockerService _unlocker;
    private readonly SteamService _steam;
    private readonly PluginInstallerService _installer;
    private readonly ToastService _toast;

    public OnboardingViewModel(AuthService auth, CacheService cache, SettingsService settings,
        UnlockerService unlocker, SteamService steam, PluginInstallerService installer, ToastService toast)
    {
        _auth = auth;
        _cache = cache;
        _settings = settings;
        _unlocker = unlocker;
        _steam = steam;
        _installer = installer;
        _toast = toast;
        _auth.AuthStateChanged += () => IsSignedIn = _auth.IsSignedIn;
        IsSignedIn = _auth.IsSignedIn;
    }

    /// <summary>Whether the overlay is visible. Set true by App on a fresh first launch.</summary>
    [ObservableProperty] private bool _isOpen;

    /// <summary>Set by App: refresh the Home dashboard after onboarding applies its actions (so the mode
    /// and plugin status tiles reflect the fresh install).</summary>
    public Func<Task>? RefreshHome { get; set; }

    /// <summary>True when a key is already stored (skips the key-entry block).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsAuthKey))]
    [NotifyPropertyChangedFor(nameof(CanFinish))]
    private bool _isSignedIn;
    public bool NeedsAuthKey => !IsSignedIn;

    /// <summary>What the user pasted into the onboarding key box.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveAuthKey))]
    private string _authKeyInput = "";

    public bool CanSaveAuthKey => !string.IsNullOrWhiteSpace(AuthKeyInput);

    /// <summary>True when the overlay is only asking for the key (a key is required at startup and none is
    /// stored yet): the recommended-settings / plugin choices are hidden so nothing gets reinstalled.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowChoices))]
    private bool _keyOnly;

    public bool ShowChoices => !KeyOnly;

    /// <summary>Open the overlay as a key-only prompt (no first-run choices). Used when a key is required
    /// but none is stored, so the returning user isn't shown the install questions again. A no-op while the
    /// overlay is already up: the first-run flow shows the key box next to its choices, and the App calls
    /// this again when the key is cleared.</summary>
    public void OpenKeyOnly()
    {
        if (IsOpen) return;
        KeyOnly = true;
        ApplyRecommended = false;
        InstallPlugin = false;
        IsOpen = true;
    }

    // The two yes/no choices: both default to "yes".
    [ObservableProperty] private bool _applyRecommended = true;
    [ObservableProperty] private bool _installPlugin = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotBusy))]
    [NotifyPropertyChangedFor(nameof(CanFinish))]
    private bool _isBusy;
    public bool NotBusy => !IsBusy;

    /// <summary>A key is required, so "Let's go!" stays disabled until one is saved.</summary>
    public bool CanFinish => NotBusy && !NeedsAuthKey;

    /// <summary>Error text shown under the key box; null when none.</summary>
    [ObservableProperty] private string? _statusLine;

    [RelayCommand]
    private void SetApplyRecommended(bool value) => ApplyRecommended = value;

    [RelayCommand]
    private void SetInstallPlugin(bool value) => InstallPlugin = value;

    /// <summary>Store the pasted key. The overlay stays open so the user can finish the other choices.</summary>
    [RelayCommand(CanExecute = nameof(CanSaveAuthKey))]
    private void SaveAuthKey()
    {
        if (string.IsNullOrWhiteSpace(AuthKeyInput)) return;
        _auth.SetKey(AuthKeyInput);
        AuthKeyInput = "";
        StatusLine = null;
        IsSignedIn = _auth.IsSignedIn;
    }

    partial void OnAuthKeyInputChanged(string value) => SaveAuthKeyCommand.NotifyCanExecuteChanged();

    partial void OnIsSignedInChanged(bool value) => FinishCommand.NotifyCanExecuteChanged();

    partial void OnIsBusyChanged(bool value) => FinishCommand.NotifyCanExecuteChanged();

    /// <summary>"Let's go!". Mark onboarding done, close the overlay immediately, then apply the chosen
    /// actions in the background. Closing first means a long-running (Steam-restarting) install can never
    /// leave the welcome dialog stuck.</summary>
    [RelayCommand(CanExecute = nameof(CanFinish))]
    private void Finish()
    {
        if (IsBusy) return;
        // Key-only runs never touch the install: its choices are hidden and both flags are false.
        bool applyRecommended = ShowChoices && ApplyRecommended;
        bool installPlugin = ShowChoices && InstallPlugin;

        _cache.OnboardingComplete = true; // seen, never show again
        IsOpen = false;                    // dismiss now, before any slow work

        if (applyRecommended || installPlugin)
            _ = ApplyChoicesAsync(applyRecommended, installPlugin);
    }

    /// <summary>Applies the recommended-settings / plugin choices in the background (best-effort, with
    /// toasts). Runs after the overlay is already closed.</summary>
    private async Task ApplyChoicesAsync(bool applyRecommended, bool installPlugin)
    {
        IsBusy = true;
        _toast.Show(Resources.Strings.Onboarding_Title, Resources.Strings.Onboarding_Applying);
        try
        {
            // Close Steam ONCE up front so both installs run against a stopped Steam, then relaunch it once
            // at the end. Avoids the double restart of letting each installer manage Steam separately.
            // (The plugin installer only relaunches Steam if it was up when it ran; since we pre-stopped it,
            // it won't, and our StartSteam below is the single relaunch.)
            await Task.Run(_steam.StopSteam);

            if (applyRecommended)
            {
                _settings.FastFetch = true;
                var result = await _unlocker.InstallAsync(UnlockerMode.Bst); // the Recommended mode
                if (!result.Success)
                    _toast.Show(Resources.Strings.Onboarding_Title, result.Error ?? "", error: true);
            }

            if (installPlugin)
            {
                var (ok, error) = await _installer.InstallAsync(null);
                if (!ok)
                    _toast.Show(Resources.Strings.Onboarding_Title, error ?? "", error: true);
            }

            await Task.Run(_steam.StartSteam);
        }
        catch (Exception ex)
        {
            _toast.Show(Resources.Strings.Onboarding_Title, ex.Message, error: true);
        }
        finally
        {
            IsBusy = false;
            // Refresh the Home dashboard so the mode + plugin status tiles reflect what we just installed.
            if (RefreshHome is not null)
                try { await RefreshHome(); } catch { /* best effort */ }
        }
    }
}
