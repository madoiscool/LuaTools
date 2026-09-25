using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaToolsGui.Models;
using LuaToolsGui.Services;

namespace LuaToolsGui.ViewModels;

/// <summary>One card on the Mode page. A single unlocker backend's name, status, and action button.</summary>
public partial class ModeCardViewModel(UnlockerMode mode, string title, string description) : ObservableObject
{
    public UnlockerMode Mode { get; } = mode;
    public string Title { get; } = title;
    public string Description { get; } = description;

    [ObservableProperty] private string _statusText = Resources.Strings.Mode_Checking;
    [ObservableProperty] private string _buttonText = Resources.Strings.Mode_Btn_Install;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowActionButton))]
    private bool _isActive;

    /// <summary>
    /// Custom has nothing to install, update or reinstall, so once it's the active mode there is no
    /// action left to offer and the button is hidden. The ACTIVE badge already says it's selected.
    /// Every other card always has something to do (install, update, or switch to it).
    /// </summary>
    public bool ShowActionButton => !(Mode == UnlockerMode.Custom && IsActive);

    /// <summary>Our own fork is the steer-here option.</summary>
    public bool IsRecommended => Mode == UnlockerMode.Bst;

    /// <summary>OST is the nightly channel, so it carries the amber warning.</summary>
    public bool IsExperimental => Mode == UnlockerMode.Ost;

    /// <summary>Both OpenSteamTool-derived builds carry native CloudRedirect support.</summary>
    public bool SupportsCloudRedirect => Mode is UnlockerMode.Ost or UnlockerMode.Bst;
}

/// <summary>
/// "Mode" page: OpenSteamTools / BetterSteamTools / Custom. Mutually exclusive, one active at a time.
/// Checks status on page open; each card installs/switches after a Steam-shutdown confirmation, then
/// relaunches Steam so the new mode takes effect.
/// </summary>
public partial class ModeViewModel : ObservableObject
{
    private readonly UnlockerService _unlocker;
    private readonly ToastService _toast;
    private readonly SteamService _steam;
    private readonly CloudRedirectService _cloudRedirect;

    public ObservableCollection<ModeCardViewModel> Cards { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotBusy))]
    [NotifyPropertyChangedFor(nameof(CanUseCloudRedirect))]
    private bool _isBusy;
    public bool NotBusy => !IsBusy;

    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isProgressIndeterminate;

    // ── Steam-shutdown confirmation overlay ──────────────────────────
    [ObservableProperty] private bool _isConfirming;
    [ObservableProperty] private string _confirmTitle = "";
    private ModeCardViewModel? _pendingCard;

    // Revert-to-vanilla confirmation overlay state.
    [ObservableProperty] private bool _isConfirmingRevert;

    /// <summary>"Also remove game luas" checkbox on the revert confirmation.</summary>
    [ObservableProperty] private bool _revertCleanLua;

    /// <summary>"Also remove pinned manifests" checkbox on the revert confirmation.</summary>
    [ObservableProperty] private bool _revertCleanManifests;

    /// <summary>True when a backup exists AND mode artifacts are present → the Revert card is offered.</summary>
    [ObservableProperty] private bool _canRevert;

    /// <summary>Backup status line for the revert card, or null.</summary>
    [ObservableProperty] private string? _backupSummary;

    public ModeViewModel(UnlockerService unlocker, ToastService toast, SteamService steam,
        CloudRedirectService cloudRedirect)
    {
        _unlocker = unlocker;
        _toast = toast;
        _steam = steam;
        _cloudRedirect = cloudRedirect;
    }

    /// <summary>CloudRedirect "Manage" (add-on panel): download (cache) the CloudRedirect GUI and launch it.</summary>
    [RelayCommand]
    private async Task ManageCloudRedirect()
    {
        if (IsBusy) return;
        IsBusy = true;
        IsProgressIndeterminate = true;
        Progress = 0;
        try
        {
            var prog = new Progress<double?>(p =>
            {
                IsProgressIndeterminate = p is null;
                if (p is not null) Progress = p.Value * 100;
            });

            bool ok = await _cloudRedirect.LaunchAsync(prog);
            if (!ok)
                _toast.Show(Resources.Strings.Mode_CloudRedirect_Manage,
                    Resources.Strings.Mode_CloudRedirect_LaunchFailed, error: true);
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    // ── CloudRedirect add-on (bottom panel; usable when either OST or BST is active) ───
    private const string CloudRedirectTitle = "CloudRedirect"; // product name, not localized

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCloudRedirectManage))]
    [NotifyPropertyChangedFor(nameof(ShowCloudRedirectUpdate))]
    [NotifyPropertyChangedFor(nameof(CanUseCloudRedirect))]
    private bool _cloudRedirectUnlocked;

    /// <summary>Buttons on the add-on panel are usable only when unlocked (Nightly active) and idle.</summary>
    public bool CanUseCloudRedirect => CloudRedirectUnlocked && !IsBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CloudRedirectToggleText))]
    [NotifyPropertyChangedFor(nameof(ShowCloudRedirectManage))]
    private bool _cloudRedirectEnabled;

    [ObservableProperty] private bool _cloudRedirectInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCloudRedirectUpdate))]
    private bool _cloudRedirectUpdateAvailable;

    [ObservableProperty] private string _cloudRedirectStatusText = "";

    public string CloudRedirectToggleText => CloudRedirectEnabled
        ? Resources.Strings.Mode_CloudRedirect_Disable
        : Resources.Strings.Mode_CloudRedirect_Enable;
    public bool ShowCloudRedirectUpdate => CloudRedirectUnlocked && CloudRedirectUpdateAvailable;
    public bool ShowCloudRedirectManage => CloudRedirectUnlocked && CloudRedirectEnabled;

    /// <summary>Refresh the add-on panel state. Reads dll/toml from disk (cheap); checks GitHub for an
    /// update only when unlocked (Nightly active), respecting forceRefresh.</summary>
    private async Task RefreshCloudRedirectAsync(bool forceRefresh)
    {
        CloudRedirectUnlocked = _unlocker.SelectedMode is UnlockerMode.Ost or UnlockerMode.Bst;
        var s = await _unlocker.GetCloudRedirectStateAsync(checkUpdate: CloudRedirectUnlocked, forceRefresh);
        CloudRedirectInstalled = s.Installed;
        CloudRedirectEnabled = s.Enabled;
        CloudRedirectUpdateAvailable = s.UpdateAvailable;

        CloudRedirectStatusText = !CloudRedirectUnlocked ? Resources.Strings.Mode_CloudRedirect_Locked
            : !s.Installed ? Resources.Strings.Mode_CloudRedirect_Status_NotInstalled
            : s.UpdateAvailable ? Resources.Strings.Mode_CloudRedirect_Status_UpdateAvailable
            : s.Enabled ? Resources.Strings.Mode_CloudRedirect_Status_Enabled
            : Resources.Strings.Mode_CloudRedirect_Status_Disabled;
    }

    /// <summary>Enable/disable the add-on (edits opensteamtool.toml; first enable also downloads the dll).
    /// Lightweight, no Steam close; the change applies on the next Steam launch.</summary>
    [RelayCommand]
    private async Task ToggleCloudRedirect()
    {
        if (IsBusy || !CloudRedirectUnlocked) return;
        bool enabling = !CloudRedirectEnabled;
        IsBusy = true;
        IsProgressIndeterminate = true;
        Progress = 0;
        try
        {
            var prog = new Progress<double?>(p =>
            {
                IsProgressIndeterminate = p is null;
                if (p is not null) Progress = p.Value * 100;
            });

            var result = enabling
                ? await _unlocker.EnableCloudRedirectAsync(prog)
                : _unlocker.DisableCloudRedirect();

            if (result.Success)
                _toast.Show(CloudRedirectTitle, enabling
                    ? Resources.Strings.Mode_CloudRedirect_Toast_Enabled
                    : Resources.Strings.Mode_CloudRedirect_Toast_Disabled);
            else
                _toast.Show(CloudRedirectTitle, result.Error ?? "", error: true);

            await LoadAsync();
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    /// <summary>Replace cloud_redirect.dll with the latest. Reports "close Steam" if it's locked.</summary>
    [RelayCommand]
    private async Task UpdateCloudRedirect()
    {
        if (IsBusy || !CloudRedirectUnlocked) return;
        IsBusy = true;
        IsProgressIndeterminate = true;
        Progress = 0;
        try
        {
            var prog = new Progress<double?>(p =>
            {
                IsProgressIndeterminate = p is null;
                if (p is not null) Progress = p.Value * 100;
            });

            var result = await _unlocker.UpdateCloudRedirectAsync(prog);
            if (result.Success)
                _toast.Show(CloudRedirectTitle, Resources.Strings.Mode_CloudRedirect_Toast_Updated);
            else
                _toast.Show(CloudRedirectTitle, result.Error ?? "", error: true);

            await LoadAsync();
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    /// <summary>True if a hidden mode should be shown: its reveal-file exists, or it's the active mode.</summary>
    private bool IsModeVisible(ModeDefinition def)
    {
        if (def.HiddenUnlessFile is null) return true;          // always-visible mode
        if (_unlocker.SelectedMode == def.Mode) return true;    // active → keep visible even if file gone
        string? root = _steam.EffectivePath;
        return root is not null && File.Exists(Path.Combine(root, def.HiddenUnlessFile));
    }

    /// <summary>Rebuild the visible card list (hidden modes appear only when revealed). Preserves
    /// existing cards so their bound state isn't reset; adds/removes as visibility changes.</summary>
    private void SyncCards()
    {
        var visible = _unlocker.Modes
            .Where(IsModeVisible)
            .ToList();

        // Remove cards no longer visible.
        for (int i = Cards.Count - 1; i >= 0; i--)
            if (!visible.Any(d => d.Mode == Cards[i].Mode))
                Cards.RemoveAt(i);

        // Add newly-visible cards in definition order.
        foreach (var def in visible)
            if (!Cards.Any(c => c.Mode == def.Mode))
            {
                int idx = visible.IndexOf(def);
                idx = Math.Min(idx, Cards.Count);
                Cards.Insert(idx, new ModeCardViewModel(def.Mode, def.DisplayName, def.Description));
            }
    }

    /// <summary>
    /// Page open / refresh. Only the ACTIVE mode is checked against GitHub (inactive cards just show
    /// "Switch to this": switching re-fetches anyway, so pinging for them is wasted, and their hash
    /// check would be misleading since modes share filenames). Active check is cached briefly.
    /// </summary>
    private bool _detectionAttempted;

    public async Task LoadAsync(bool forceRefresh = false)
    {
        // First time with no mode selected: try to auto-detect an existing install by hashing the
        // on-disk DLLs against published releases, and adopt the match as active.
        if (!_detectionAttempted && _unlocker.SelectedMode is null)
        {
            _detectionAttempted = true;
            await _unlocker.DetectActiveModeAsync();
        }

        // Re-evaluate which cards are visible (hidden modes appear only when revealed).
        SyncCards();

        var active = _unlocker.SelectedMode;
        foreach (var card in Cards)
        {
            if (card.Mode == active)
            {
                card.StatusText = Resources.Strings.Mode_Checking;
                Apply(card, await _unlocker.GetStateAsync(card.Mode, forceRefresh));
            }
            else
            {
                // No network for inactive modes.
                Apply(card, new ModeState(card.Mode, ModeStatus.NotInstalled, IsActive: false, null));
            }
        }

        // Bottom CloudRedirect add-on panel (locked unless Nightly BST is the active mode).
        await RefreshCloudRedirectAsync(forceRefresh);

        // Revert card: visible only when a backup exists AND there are mode artifacts to undo.
        CanRevert = _unlocker.CanRevert;
        BackupSummary = _unlocker.BackupSummary;
    }

    // ── Revert to vanilla Steam ──────────────────────────────────────

    /// <summary>"Revert to vanilla" button → show the confirmation overlay.</summary>
    [RelayCommand]
    private void StartRevert()
    {
        if (IsBusy || !_unlocker.CanRevert) return;
        RevertCleanLua = false;
        RevertCleanManifests = false;
        IsConfirmingRevert = true;
    }

    [RelayCommand]
    private void CancelRevert()
    {
        IsConfirmingRevert = false;
    }

    /// <summary>Confirmed revert: close Steam, restore/delete mode files, restart Steam, report.</summary>
    [RelayCommand]
    private async Task ConfirmRevert()
    {
        IsConfirmingRevert = false;
        if (IsBusy) return;
        IsBusy = true;
        IsProgressIndeterminate = true;
        Progress = 0;
        try
        {
            // Files are locked while Steam runs; same choreography as install.
            await Task.Run(_steam.StopSteam);

            var result = await Task.Run(() =>
                _unlocker.Revert(RevertCleanLua, RevertCleanManifests));

            if (result.Success)
            {
                bool started = await Task.Run(_steam.StartSteam);
                _toast.Show(Resources.Strings.Mode_Revert_Title, started
                    ? Resources.Strings.Mode_Revert_Toast_Done
                    : Resources.Strings.Mode_Revert_Toast_Done_NoStart);
            }
            else
            {
                // Revert (partially) failed: still bring Steam back up.
                await Task.Run(_steam.StartSteam);
                _toast.Show(Resources.Strings.Mode_Revert_Title,
                    result.Error ?? Resources.Strings.Mode_Revert_Failed_Body, error: true);
            }

            await LoadAsync();
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    private DateTime _lastCheck;

    [RelayCommand]
    private async Task CheckForUpdates()
    {
        // 30s cooldown: a forced refresh hits GitHub, and the unauthenticated API allows only 60/hr.
        if (IsBusy || DateTime.UtcNow - _lastCheck < TimeSpan.FromSeconds(30)) return;
        _lastCheck = DateTime.UtcNow;
        await LoadAsync(forceRefresh: true);
    }

    private void Apply(ModeCardViewModel card, ModeState s)
    {
        // Only the ACTIVE mode shows real install/update status. Inactive modes share filenames with
        // the active one (different contents), so their hash check is meaningless. Show them simply
        // as the switch target instead of a misleading "Update available".
        if (s.Status == ModeStatus.UserManaged)
            // Custom: there's nothing to be up to date WITH, so the status never varies and the only
            // action is switching to it. Checked before IsActive so it never reads "Not active".
            card.StatusText = Resources.Strings.Mode_UserManaged;
        else if (s.Status == ModeStatus.Unknown)
            card.StatusText = Resources.Strings.Mode_StatusUnavailable;
        else if (!s.IsActive)
            card.StatusText = Resources.Strings.Mode_NotActive;
        else
            card.StatusText = s.Status switch
            {
                ModeStatus.NotInstalled => Resources.Strings.Mode_NotInstalled,
                ModeStatus.UpToDate => Resources.Strings.Mode_UpToDate,
                ModeStatus.UpdateAvailable => Resources.Strings.Mode_UpdateAvailable,
                _ => Resources.Strings.Mode_StatusUnavailable,
            };

        card.ButtonText = (s.IsActive, s.Status) switch
        {
            // Never rendered: ShowActionButton hides the button in this exact case. Mapped anyway so
            // a regression in that binding surfaces a harmless "Switch to this" rather than falling
            // through to "Install" on a mode that installs nothing.
            (true, ModeStatus.UserManaged) => Resources.Strings.Mode_Btn_Switch,
            (true, ModeStatus.UpToDate) => Resources.Strings.Mode_Btn_Reinstall,
            (true, ModeStatus.UpdateAvailable) => Resources.Strings.Mode_Btn_Update,
            (true, _) => Resources.Strings.Mode_Btn_Install,
            (false, _) => Resources.Strings.Mode_Btn_Switch,
        };
        card.IsActive = s.IsActive;
    }

    // ── Install with confirmation ────────────────────────────────────

    /// <summary>Card button → ask the user to confirm (Steam will be closed) before doing anything.</summary>
    [RelayCommand]
    private void Install(ModeCardViewModel card)
    {
        if (IsBusy) return;
        _pendingCard = card;
        ConfirmTitle = card.IsActive
            ? string.Format(Resources.Strings.Mode_Confirm_Reinstall, card.Title)
            : string.Format(Resources.Strings.Mode_Confirm_Switch, card.Title);
        IsConfirming = true;
    }

    [RelayCommand]
    private void CancelConfirm()
    {
        IsConfirming = false;
        _pendingCard = null;
    }

    [RelayCommand]
    private async Task ConfirmInstall()
    {
        IsConfirming = false;
        var card = _pendingCard;
        _pendingCard = null;
        if (card is null) return;

        await RunInstall(card.Mode);
    }

    private async Task RunInstall(UnlockerMode mode)
    {
        if (IsBusy) return;
        IsBusy = true;
        IsProgressIndeterminate = true;
        Progress = 0;
        try
        {
            var prog = new Progress<double?>(p =>
            {
                IsProgressIndeterminate = p is null;
                if (p is not null) Progress = p.Value * 100;
            });

            // Correct order: kill Steam → write the files → relaunch Steam.
            // (Files can't be overwritten while Steam holds them open. CloudRedirect's CLI also closes
            //  Steam itself, but stopping first is harmless and keeps all modes consistent.)
            await Task.Run(_steam.StopSteam);

            var result = await _unlocker.InstallAsync(mode, prog);

            if (result.Success)
            {
                bool started = await Task.Run(_steam.StartSteam);
                _toast.Show(Resources.Strings.Mode_Toast_Updated, started
                    ? string.Format(Resources.Strings.Mode_Toast_Updated_Restarting, mode)
                    : string.Format(Resources.Strings.Mode_Toast_Updated_Start, mode));
            }
            else
            {
                // Install failed: bring Steam back up anyway so the user isn't left without it.
                await Task.Run(_steam.StartSteam);
                _toast.Show(Resources.Strings.Mode_Toast_InstallFailed, result.Error ?? Resources.Strings.Mode_Toast_InstallFailed_Body, error: true);
            }

            await LoadAsync();
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }
}
