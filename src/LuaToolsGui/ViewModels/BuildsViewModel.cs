using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaToolsGui.Services;
using LuaToolsGui.Services.Downloads;
using Microsoft.Win32;

namespace LuaToolsGui.ViewModels;

/// <summary>
/// One depot/DLC row in the Builds page's breakdown. Title = name or "Depot"; Meta = id · size · os ·
/// lang. ManifestId is this lua's ACTIVE pin, CommentedManifestId a pin that exists but was commented
/// out by "Auto Update Apps", and PublicManifestId what Steam currently ships.
/// </summary>
public record DepotRow(
    long Id, string Title, string Meta, bool IsDlc, bool IsShared, string SteamDbUrl,
    string? ManifestId, string? CommentedManifestId, string? PublicManifestId,
    bool IsInLua = false, bool IsEnabled = false, bool CanToggle = false, bool IsBaseApp = false)
{
    /// <summary>Locked = this lua pins the depot to a fixed manifest (an ACTIVE setManifestid).</summary>
    public bool IsLocked => ManifestId is not null;

    /// <summary>
    /// The lock switch needs a setManifestid line to comment in/out. Without one there's nothing to
    /// toggle. We'd have to invent a manifest id, which is not something to guess at.
    /// </summary>
    public bool CanLock => CanToggle && (ManifestId is not null || CommentedManifestId is not null);

    /// <summary>The enable switch is offered for anything the lua declares, except the base app. Turning
    /// THAT off doesn't disable a depot, it breaks the whole file.</summary>
    public bool CanEnable => CanToggle && !IsBaseApp;

    /// <summary>
    /// The id whose lua lines the switches rewrite. Usually <see cref="Id"/>, but for a DLC the lua
    /// declares the DLC APP id rather than the depot id, and that's the line that has to change.
    /// </summary>
    public long ToggleId { get; init; }

    /// <summary>Depot size in bytes (0 for DLC entitlements with no depot of their own). Kept as a
    /// number, not just baked into <see cref="Meta"/>, so a download selection can be totalled.</summary>
    public long Size { get; init; }

    /// <summary>Raw oslist from Steam ("windows", "macos", "linux"), or null when undeclared. Kept
    /// separate from the prettified copy inside <see cref="Meta"/> so it can be matched on.</summary>
    public string? Os { get; init; }

    /// <summary>Raw Steam language code ("english", "schinese", ...), null for language-neutral content.
    /// Separate from <see cref="Meta"/> for the same reason as <see cref="Os"/>: it is matched on, not
    /// just displayed.</summary>
    public string? Language { get; init; }

    /// <summary>Owning app for a shared redistributable depot, else null. See ContentDepot.FromAppId.</summary>
    public long? FromAppId { get; init; }

    /// <summary>
    /// Size in bytes from this depot's <c>setManifestid(id, "gid", size)</c> line, or 0 when the lua
    /// omits it. Kept separate from <see cref="Size"/> (which is app info's) because the two describe
    /// DIFFERENT builds: this one matches the manifest the lua pins, app info's matches whatever the
    /// public branch ships today.
    /// </summary>
    public long LuaSize { get; init; }

    /// <summary>
    /// Free-text match for the depot search box: name, depot id, DLC app id, manifest ids, and the
    /// literal type words "DLC"/"SHARED". <see cref="Meta"/> already carries id · size · os · language,
    /// so searching it covers platform and size too; <see cref="ToggleId"/> is checked separately because
    /// a DLC's app id never appears in Meta.
    /// </summary>
    public bool Matches(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        string q = query.Trim();

        bool Has(string? s) => s is not null && s.Contains(q, StringComparison.OrdinalIgnoreCase);

        return Has(Title)
            || Has(Meta)
            || Has(Id.ToString())
            || Has(ToggleId.ToString())
            || Has(ManifestId)
            || Has(CommentedManifestId)
            || (IsDlc && Has("DLC"))
            || (IsShared && Has(Resources.Strings.Confirm_Shared));
    }

    /// <summary>The manifest line under the title, or null when this row has no pin at all.</summary>
    public string? ManifestLabel =>
        ManifestId is not null ? string.Format(Resources.Strings.Builds_Manifest, ManifestId)
        : CommentedManifestId is not null
            ? string.Format(Resources.Strings.Builds_Manifest, CommentedManifestId) +
              "  ·  " + Resources.Strings.Builds_PinDisabled
            : null;

    public bool HasManifest => ManifestLabel is not null;

    /// <summary>Greyed out when the pin exists but isn't in force (commented out).</summary>
    public bool IsPinActive => ManifestId is not null;

    /// <summary>Shown only when this lua is pinned to something OTHER than what Steam ships now.
    /// That's the whole point of picking an older build, so it's worth calling out.</summary>
    public string? OutdatedLabel =>
        ManifestId is not null && PublicManifestId is not null && ManifestId != PublicManifestId
            ? string.Format(Resources.Strings.Builds_ManifestLatest, PublicManifestId)
            : null;

    public bool IsOutdated => OutdatedLabel is not null;
}

/// <summary>
/// One row in the build switcher, always a stored <see cref="LuaVariant"/>. There is no "unsaved
/// changes" row any more: live bytes matching no saved build or preset ARE the Default, captured by
/// <see cref="LuaVault.SyncDefaultFromLive"/> before this list is built.
/// </summary>
public partial class VariantRowViewModel : ObservableObject
{
    public LuaVariant Variant { get; }

    public string Hash => Variant.Hash;
    public string? BuildId => Variant.BuildId;

    /// <summary>True when this row is what Steam is using right now, including when the live lua is
    /// this variant plus an unsaved edit (see <see cref="HasPendingEdit"/>).</summary>
    [ObservableProperty] private bool _isActive;

    /// <summary>
    /// True when the live lua is this variant with changes that haven't been saved back to it. Without
    /// surfacing this, a build mid-edit is indistinguishable from a saved one and switching away
    /// discards the work silently. The header's "Save to &lt;build&gt;" button being the only hint.
    /// </summary>
    public bool HasPendingEdit { get; init; }

    public string Label => Variant.DisplayLabel;

    /// <summary>Sub-line: "8 depots · 3 DLC · Pinned" plus provenance and edit state when they apply.</summary>
    public string Meta
    {
        get
        {
            var parts = new List<string>
            {
                string.Format(Resources.Strings.Builds_Meta_Depots, Variant.DepotCount, Variant.DlcCount),
                Variant.IsPinned ? Resources.Strings.Builds_Pinned : Resources.Strings.Builds_AutoUpdating,
            };
            if (!string.IsNullOrWhiteSpace(Variant.Source))
                parts.Add(string.Format(Resources.Strings.Builds_Source, Variant.Source));
            if (HasPendingEdit) parts.Add(Resources.Strings.Builds_Meta_Unsaved);
            return string.Join("  ·  ", parts);
        }
    }

    public VariantRowViewModel(LuaVariant variant) => Variant = variant;
}

/// <summary>
/// Pick a game on the left, switch which stored lua is live on the right.
///
/// <para>
/// <b>Named "Depots" in the UI</b>, and its stored luas are "presets" there. The type names here
/// (Builds*, <see cref="LuaVariantKind.Build"/>, <c>Builds_*</c> resource keys) predate that and were
/// deliberately left alone: the codebase already has <see cref="SteamDepotInfo"/> and
/// <see cref="DepotRow"/> for actual Steam depots, so a DepotsViewModel beside them would be worse than
/// the mismatch. "Build" survives in the UI only where it means a real Steam build id (Build 7691388).
/// </para>
///
/// <para>
/// Steam only ever reads one file per game (stplug-in\&lt;appid&gt;.lua), so "which preset am I on" is
/// really "which of my stored luas is currently copied there". <see cref="LuaVault"/> answers that by
/// hashing the live file, which is why nothing here tracks an "active" flag of its own.
/// </para>
/// </summary>
public partial class BuildsViewModel : PagedListViewModel<LuaTileViewModel>
{
    private readonly SteamService _steam;
    private readonly LuaVault _vault;
    private readonly SteamAppListCache _appList;
    private readonly SteamAppInfoCache _appInfo;
    private readonly CoverCache _covers;
    private readonly SteamDepotInfo _depotInfo;
    private readonly ToastService _toast;
    private readonly SettingsService _settings;

    private List<LuaTileViewModel> _allGames = [];

    /// <summary>Guards against a slow depot fetch for a game the user has since navigated away from.</summary>
    private long _depotLoadToken;

    private readonly DepotDownloaderService _depotTool;
    private readonly DownloadQueue _queue;
    private readonly ManifestJobFactory _jobs;

    public BuildsViewModel(SteamService steam, LuaVault vault, SteamAppListCache appList,
        SteamAppInfoCache appInfo, CoverCache covers, SteamDepotInfo depotInfo, ToastService toast,
        SettingsService settings, DepotDownloaderService depotTool, DownloadQueue queue,
        ManifestJobFactory jobs)
    {
        _depotTool = depotTool;
        _queue = queue;
        _jobs = jobs;
        _steam = steam;
        _vault = vault;
        _appList = appList;
        _appInfo = appInfo;
        _covers = covers;
        _depotInfo = depotInfo;
        _toast = toast;
        _settings = settings;

        // The base offers 12/24/48/All, sized for the full-width Manage and Fixes grids. This list is a
        // narrow sidebar of single-line rows, so it gets its own steps. PageSizeOptions is a per-instance
        // collection, so replacing its contents here doesn't touch the other pages.
        PageSizeOptions.Clear();
        foreach (string option in new[] { "10", "17", "25", "50", AllPages }) PageSizeOptions.Add(option);

        InitPageSize(settings.BuildsPageSize);

        // A size saved before this list existed (12, 24, 48) is no longer offered, which would leave the
        // dropdown blank. Snap it to the default and persist, rather than showing an empty box.
        if (!PageSizeOptions.Contains(SelectedPageSize)) SelectedPageSize = "10";

        // Any install (Add page, drag-drop, plugin) captures into the vault on a background thread.
        // Refresh so a new build shows up without the user leaving the page.
        _vault.VaultChanged += appId => OnUi(() =>
        {
            if (ActiveGame?.AppId == appId) RefreshVariants();
        });
    }

    // Paging (Items/PageSize/CurrentPage/PrevPage/NextPage/…), IsLoading, EmptyMessage and IsEmpty are
    // inherited from PagedListViewModel<LuaTileViewModel>. The Builds pager renders only prev/label/next.
    // PageNumbers exists on the base but is deliberately not bound.
    protected override void SavePageSizeSetting(int size) => _settings.BuildsPageSize = size;

    // ── Game list (left) ────────────────────────────────────────────

    [ObservableProperty] private string _searchText = "";
    partial void OnSearchTextChanged(string value) => ApplyGameFilter();

    /// <summary>
    /// The row highlighted in the list. This goes NULL on every page change. The base hands `Items` a
    /// brand-new collection, and the ListBox pushes null back through the binding when the selected item
    /// isn't in it. So it can't be what the right-hand panel reads; see <see cref="ActiveGame"/>.
    /// </summary>
    [ObservableProperty] private LuaTileViewModel? _selectedGame;

    /// <summary>
    /// The game the right-hand panel is showing, and the target of every action on it. Only ever set from
    /// a non-null selection, so paging away from a game keeps its builds on screen instead of blanking
    /// the panel back to "pick a game".
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private LuaTileViewModel? _activeGame;

    public bool HasSelection => ActiveGame is not null;

    partial void OnSelectedGameChanged(LuaTileViewModel? value)
    {
        if (value is null) return; // a page change, not a deselection. Keep showing the current game
        ActiveGame = value;
    }

    partial void OnActiveGameChanged(LuaTileViewModel? value)
    {
        CancelEdit();
        IsRenaming = false;
        // A filter carried over from the previous game would show the new one's table as empty or
        // half-missing, which reads as a data bug rather than a leftover search.
        DepotSearchText = "";
        RefreshVariants();
        _ = LoadDepotsAsync();
    }

    /// <summary>In-flight load, so concurrent callers JOIN it instead of racing past an empty list.</summary>
    private Task? _loading;

    /// <summary>
    /// Scan stplug-in and build the left-hand game list. Called when the page is shown.
    /// <para>
    /// Concurrent callers await the SAME load rather than the second one bailing out: navigating via
    /// "Manage Build" triggers the view's Loaded → LoadAsync AND SelectAppAsync → LoadAsync at once, and
    /// a bail-out there left SelectAppAsync looking at a still-empty list, so the deep-link silently
    /// reported the game as missing instead of selecting it.
    /// </para>
    /// </summary>
    public Task LoadAsync()
    {
        if (_loading is { IsCompleted: false }) return _loading;
        return _loading = LoadCoreAsync();
    }

    private async Task LoadCoreAsync()
    {
        IsLoading = true;
        try
        {
            string? dir = _steam.StPlugInDir;
            if (dir is null || !Directory.Exists(dir))
            {
                _allGames = [];
                EmptyMessage = dir is null
                    ? Resources.Strings.Manage_Empty_NoSteam
                    : Resources.Strings.Manage_Empty_NoLuas;
                ApplyGameFilter();
                return;
            }

            await _appList.EnsureLoadedAsync();

            var games = await Task.Run(() =>
            {
                // A game is listed while there is something here to act on:
                //   1. installed: a live <appid>.lua Steam reads
                //   2. loose: <appid>_<buildid>.lua sitting in stplug-in, inert until applied
                //
                // Deliberately NOT a third "every app with vault variants" source. Every install captures
                // a Default variant (LuaInstaller.CaptureInstalled -> SyncDefaultFromLive), so every game
                // has a vault entry — and deleting its lua from Manage doesn't touch the vault. Including
                // vaulted apps therefore kept deleted games on this page forever, offering a Default that
                // mirrored a file Steam no longer had.
                //
                // Nothing is deleted to achieve this: the variants stay on disk untouched and the game
                // reappears with them intact once its lua is added back. Hidden, not discarded.
                var installed = LuaInstaller.EnumerateInstalled(dir).ToDictionary(f => f.AppId, f => f.Path);
                var appIds = new HashSet<long>(installed.Keys);
                foreach (var (appId, _, _) in _vault.EnumerateLooseBuildLuas()) appIds.Add(appId);

                return appIds
                    .Select(appId =>
                    {
                        string path = installed.TryGetValue(appId, out var p) ? p : Path.Combine(dir, $"{appId}.lua");
                        string? name = _appList.GetName(appId) ?? _appInfo.GetCached(appId)?.Name;
                        var added = File.Exists(path) ? File.GetLastWriteTime(path) : DateTime.MinValue;
                        return new LuaTileViewModel(appId, path, added,
                            name ?? string.Format(Resources.Strings.Common_AppFallback, appId), name is null);
                    })
                    .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            });

            _allGames = games;
            EmptyMessage = Resources.Strings.Manage_Empty_NoLuas;
            ApplyGameFilter();
            await RefreshBadgesAsync();

            // Keep the current selection across a refresh; otherwise select nothing (the page shows a
            // "pick a game" prompt) so we never guess at a game the user didn't ask for.
            if (ActiveGame is { } prev)
            {
                ActiveGame = _allGames.FirstOrDefault(g => g.AppId == prev.AppId);
                SelectedGame = Items.FirstOrDefault(g => g.AppId == prev.AppId);
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Filtering only. Badges are computed by <see cref="RefreshBadgesAsync"/>, not here. This
    /// runs on every search keystroke, and badging hashes files off disk. Search spans the WHOLE library,
    /// not just the visible page; the base re-slices and resets to page 1.</summary>
    private void ApplyGameFilter()
    {
        string q = SearchText.Trim();
        SetFiltered(string.IsNullOrEmpty(q) ? _allGames : _allGames.Where(g => g.Matches(q)).ToList());
    }

    /// <summary>Recompute every game's "currently running" badge off the UI thread (one file hash + index
    /// read per vaulted game. Fine once per load, far too slow per keystroke).</summary>
    private async Task RefreshBadgesAsync()
    {
        var games = _allGames.ToList();
        var badges = await Task.Run(() => games.ToDictionary(g => g.AppId, g => BadgeFor(g.AppId)));
        foreach (var g in games)
            if (badges.TryGetValue(g.AppId, out string? badge)) g.VariantBadge = badge;
    }

    /// <summary>
    /// The label shown next to a game in the list: what it's currently running. A game with nothing
    /// captured yet is untracked, not mislabelled, so it gets no badge; the same goes for a live hash the
    /// vault hasn't seen, which only happens before this game's first
    /// <see cref="LuaVault.SyncDefaultFromLive"/> and resolves to "Default" the moment it runs.
    /// </summary>
    private string? BadgeFor(long appId)
    {
        if (!_vault.HasVariants(appId)) return null;      // nothing captured for this game yet
        string? activeHash = _vault.GetActiveHash(appId);
        if (activeHash is null) return null;              // no live lua (a stored build that isn't applied)

        return _vault.GetVariants(appId).FirstOrDefault(v => v.Hash == activeHash)?.DisplayLabel;
    }

    /// <summary>Select a game by appid (the Manage flyout's "Manage Build" deep-link). Rescans if the
    /// list hasn't loaded yet or the game isn't in it.</summary>
    public async Task SelectAppAsync(long appId)
    {
        var game = _allGames.FirstOrDefault(g => g.AppId == appId);
        if (game is null)
        {
            await LoadAsync();
            game = _allGames.FirstOrDefault(g => g.AppId == appId);
        }
        if (game is null)
        {
            _toast.Show(Resources.Strings.Manage_Toast_NotFound_Title,
                Resources.Strings.Manage_Toast_NotFound_Body, error: true);
            return;
        }

        // Clear any search so the game is definitely in the filtered set…
        if (!_filtered.Contains(game)) { SearchText = ""; ApplyGameFilter(); }

        // …then jump to the PAGE holding it. Without this the ListBox simply can't select a game that
        // isn't on the current page, and the deep link would silently do nothing for anything past page 1.
        int index = _filtered.IndexOf(game);
        if (index >= 0 && PageSize > 0) CurrentPage = index / PageSize + 1;

        ActiveGame = game;                                             // drives the right-hand panel
        SelectedGame = Items.FirstOrDefault(g => g.AppId == appId);    // highlights the row
    }

    /// <summary>Called by the view as a game row scrolls into view. Resolves its cover (cached after).</summary>
    public void ResolveGame(LuaTileViewModel game) => _ = game.EnsureResolvedAsync(_appInfo, _covers);

    /// <summary>Set by App. The reverse of Manage's "Manage Build": open this game on the Manage page.</summary>
    public Action<long>? NavigateToManage { get; set; }

    [RelayCommand]
    private void OpenInManage()
    {
        if (ActiveGame is { } game) NavigateToManage?.Invoke(game.AppId);
    }

    /// <summary>
    /// Reload the page. Drops the selected game's cached depot info first. That's a session-long memory
    /// cache, so without this Refresh rebuilt the list and re-read the vault but still showed the "latest
    /// build on Steam" figures fetched the first time the game was opened. Only the selected game's data
    /// is on screen, so there's nothing to gain from clearing the rest.
    /// </summary>
    [RelayCommand]
    private Task Refresh()
    {
        if (ActiveGame is { } game) _depotInfo.Invalidate(game.AppId);
        return LoadAsync();
    }

    // ── Variants (right) ────────────────────────────────────────────

    public ObservableCollection<VariantRowViewModel> Variants { get; } = [];

    /// <summary>The row the user has highlighted. Selecting only STAGES a switch. Apply performs it, so
    /// a stray click can't silently swap which version of a game Steam downloads.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    [NotifyPropertyChangedFor(nameof(CanRename))]
    private VariantRowViewModel? _selectedVariant;

    /// <summary>Set while a depot switch rebuilds the variant list, so the reselection it causes doesn't
    /// fire its own (spinner-showing) depot reload. The caller does one quiet reload instead.</summary>
    private bool _suppressDepotReload;

    partial void OnSelectedVariantChanged(VariantRowViewModel? value)
    {
        IsRenaming = false;
        // The table describes the SELECTED variant, so a user picking a different one must re-read it.
        if (!_suppressDepotReload) _ = LoadDepotsAsync();
    }

    /// <summary>
    /// True when the selected row IS the live lua, including when the live file is that preset plus
    /// unsaved changes (<see cref="ResolveActiveHash"/> keeps such a row active).
    ///
    /// <para>
    /// Deliberately ONE member rather than the same expression written at each use. The depot table and
    /// <see cref="EditLive"/> both have to answer "is the thing on screen the live file?", and when they
    /// disagreed the table described a preset's pre-edit bytes while edits went to the live file: a depot
    /// toggle wrote, the table reloaded from the stored copy, the switch sprang back, and the next click
    /// asked for the state it already thought it had, which <c>EditLive</c> then discarded as a no-op.
    /// Two clicks, one write, no error.
    /// </para>
    /// </summary>
    private bool SelectionDescribesLive => SelectedVariant is null or { IsActive: true };

    public bool CanApply => SelectedVariant is { IsActive: false };
    public bool CanDelete => SelectedVariant is { IsActive: false };
    public bool CanRename => SelectedVariant is not null;

    [ObservableProperty] private string? _latestBuildLabel;

    /// <summary>
    /// Re-entrancy guard. The capture calls below raise <see cref="LuaVault.VaultChanged"/>, whose handler
    /// calls straight back into here. The inner pass would fill the list, then the outer pass would
    /// resume past its own Clear() and append everything a second time (every build listed twice).
    /// </summary>
    private bool _refreshingVariants;

    /// <summary>Rebuild the switcher from the vault + the live file's hash.</summary>
    private void RefreshVariants()
    {
        if (_refreshingVariants) return;
        _refreshingVariants = true;
        try { RefreshVariantsCore(); }
        finally { _refreshingVariants = false; }
    }

    private void RefreshVariantsCore()
    {
        Variants.Clear();
        SelectedVariant = null;

        if (ActiveGame is not { } game) return;

        // Point the single Default slot at the live lua before showing anything, so a lua that matches no
        // saved build (a fresh install, a hand edit, another tool) is listed as the Default rather than
        // going unrepresented.
        _vault.SyncDefaultFromLive(game.AppId);
        // Pick up any <appid>_<buildid>.lua the user already dropped in stplug-in. Steam ignores those
        // files, so without this they'd sit there looking installed while doing nothing.
        _vault.AdoptLooseBuildLuas(game.AppId);

        // A dropped-in build lua is inert until it's the <appid>.lua Steam reads. See the method's docs
        // for why this only fires when the game has nothing live at all.
        _vault.ApplyBuildIfNothingLive(game.AppId);

        string? liveHash = _vault.GetActiveHash(game.AppId);
        var stored = _vault.GetVariants(game.AppId);

        // Mid-edit the live bytes match nothing stored. They're a divergence FROM a variant, and that
        // variant is what the user is looking at. Resolve it so the row stays active and selected.
        string? editBase = _vault.GetEditBase(game.AppId);
        string? activeHash = ResolveActiveHash(liveHash, stored, editBase);
        string? pendingHash = activeHash != liveHash ? activeHash : null;

        // Live matches a stored variant exactly, so nothing is pending. Drop any edit base still on
        // record. Only Apply/SaveText/UpdateVariant clear it otherwise, so undoing an edit by hand (toggle
        // a depot off, then on) left the header offering "Save to <preset>" with nothing left to save.
        if (pendingHash is null && editBase is not null) _vault.SetEditBase(game.AppId, null);

        foreach (var v in stored)
            Variants.Add(new VariantRowViewModel(v)
            {
                IsActive = v.Hash == activeHash,
                HasPendingEdit = v.Hash == pendingHash,
            });

        SelectedVariant = Variants.FirstOrDefault(v => v.IsActive) ?? Variants.FirstOrDefault();

        string? badge = BadgeFor(game.AppId);
        foreach (var g in _allGames.Where(g => g.AppId == game.AppId)) g.VariantBadge = badge;

        // Computed from the vault, so they only change when it does.
        OnPropertyChanged(nameof(EditBaseVariant));
        OnPropertyChanged(nameof(HasEditBase));
        OnPropertyChanged(nameof(SaveInPlaceLabel));
    }

    /// <summary>
    /// Which stored variant the switcher should treat as live.
    ///
    /// <para>
    /// Usually just "the one whose bytes match the live lua". The case worth naming is an edit in
    /// progress: writing to the live file makes it match NOTHING (the variant it came from still holds
    /// its pre-edit bytes under its old content hash), so the answer has to be the variant it diverged
    /// FROM: recorded as the edit base. Returning null there instead meant no row was active, the
    /// selection fell through to whatever sorted first (the Default, which
    /// <see cref="LuaVault.SyncDefaultFromLive"/> re-captures so it usually is), and the user was moved
    /// off the build they were editing mid-edit. Taking the next depot toggle with them, into the
    /// Default's stored copy.
    /// </para>
    ///
    /// <para>Pure and static so it can be tested without the eight services the page needs.</para>
    /// </summary>
    internal static string? ResolveActiveHash(
        string? liveHash, IReadOnlyList<LuaVariant> stored, string? editBase)
    {
        if (liveHash is null) return null;                              // nothing installed
        if (stored.Any(v => v.Hash == liveHash)) return liveHash;       // ordinary case

        // Diverged. Fall back to the live hash when there's no usable base, no row matches, which is
        // the honest answer, and it must not throw. After a SyncDefaultFromLive this shouldn't arise.
        return editBase is not null && stored.Any(v => v.Hash == editBase) ? editBase : liveHash;
    }

    /// <summary>Copy the selected variant over stplug-in\&lt;appid&gt;.lua, then offer a Steam restart.</summary>
    [RelayCommand]
    private void Apply()
    {
        if (ActiveGame is not { } game || SelectedVariant?.Variant is not { } variant) return;

        if (!_vault.Apply(game.AppId, variant))
        {
            _toast.Show(Resources.Strings.Builds_Title, Resources.Strings.Builds_Apply_Failed, error: true);
            return;
        }

        RefreshVariants();
        _toast.Show(Resources.Strings.Builds_Title,
            string.Format(Resources.Strings.Builds_Apply_Done, variant.DisplayLabel));
    }

    [RelayCommand]
    private void DeleteVariant()
    {
        if (ActiveGame is not { } game || SelectedVariant?.Variant is not { } variant) return;

        var result = MessageBox.Show(
            string.Format(Resources.Strings.Builds_Delete_Body, variant.DisplayLabel),
            Resources.Strings.Builds_Delete_Title,
            MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (result != MessageBoxResult.OK) return;

        if (_vault.Delete(game.AppId, variant.Hash)) RefreshVariants();
    }

    // ── Rename ──────────────────────────────────────────────────────

    [ObservableProperty] private bool _isRenaming;
    [ObservableProperty] private string _renameText = "";

    [RelayCommand]
    private void StartRename()
    {
        if (SelectedVariant?.Variant is not { } v) return;
        RenameText = v.Label ?? v.DisplayLabel;
        IsRenaming = true;
    }

    /// <summary>Save the new name. Only sets the label. The build id and file are untouched, so a
    /// renamed build still knows which build it is.</summary>
    [RelayCommand]
    private void CommitRename()
    {
        if (ActiveGame is { } game && SelectedVariant?.Variant is { } v)
            _vault.Rename(game.AppId, v.Hash, RenameText);
        IsRenaming = false;
        RefreshVariants();
    }

    [RelayCommand]
    private void CancelRename() => IsRenaming = false;

    // ── Editor ──────────────────────────────────────────────────────

    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editorText = "";

    /// <summary>Edit the LIVE lua (what Steam actually reads), not the selected variant. Editing a
    /// stored copy the user isn't running would be a confusing no-op.</summary>
    [RelayCommand]
    private void Edit()
    {
        if (ActiveGame is not { } game) return;
        EditorText = _vault.ReadLiveText(game.AppId);
        IsEditing = true;
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
        EditorText = "";
    }

    /// <summary>Write the editor's text to stplug-in and store it. Updating the build it came from
    /// when there is one, else letting the Default slot pick the new bytes up (see
    /// <see cref="SaveInPlace"/>).</summary>
    [RelayCommand]
    private void SaveEdit()
    {
        if (ActiveGame is not { } game) return;

        RememberEditBase(game.AppId);
        if (!_vault.WriteLive(game.AppId, EditorText))
        {
            _toast.Show(Resources.Strings.Builds_Title, Resources.Strings.Builds_Save_Failed, error: true);
            return;
        }

        IsEditing = false;
        SaveInPlace();
    }

    /// <summary>
    /// Put the live lua back into the variant it was edited from, keeping that variant's name and build
    /// id. With no base (the edit started from the Default) there is nothing to save: the Default
    /// tracks the live lua, so syncing the slot IS the save. "Save as preset" stays available as its own
    /// button for when the user wants to keep a named copy.
    /// </summary>
    [RelayCommand]
    private void SaveInPlace()
    {
        if (ActiveGame is not { } game) return;

        string? baseHash = _vault.GetEditBase(game.AppId);
        if (baseHash is null) { _vault.SyncDefaultFromLive(game.AppId); return; }

        // Saving doesn't change a single byte of the live lua (it just gives it a home), so the depot
        // table is already correct. UpdateVariant raises VaultChanged, so suppress across it too.
        LuaVariant? updated;
        _suppressDepotReload = true;
        try
        {
            updated = _vault.UpdateVariant(game.AppId, baseHash, _vault.ReadLiveText(game.AppId));
            if (updated is not null)
            {
                RefreshVariants();
                SelectedVariant = Variants.FirstOrDefault(v => v.Hash == updated.Hash) ?? SelectedVariant;
            }
        }
        finally { _suppressDepotReload = false; }

        if (updated is null) { SaveAsPreset(); return; } // base vanished. Don't lose the edit

        _ = LoadDepotsAsync(quiet: true);
        _toast.Show(Resources.Strings.Builds_Title,
            string.Format(Resources.Strings.Builds_Save_Updated, updated.DisplayLabel));
    }

    /// <summary>The variant Save would overwrite, for the button's label. Null → Save makes a new preset.</summary>
    public LuaVariant? EditBaseVariant =>
        ActiveGame is { } g && _vault.GetEditBase(g.AppId) is { } h
            ? _vault.GetVariants(g.AppId).FirstOrDefault(v => v.Hash == h)
            : null;

    public bool HasEditBase => EditBaseVariant is not null;

    /// <summary>e.g. Save to “Build 24410208”. Names the target so an overwrite is never a surprise.</summary>
    public string SaveInPlaceLabel => EditBaseVariant is { } v
        ? string.Format(Resources.Strings.Builds_Action_SaveTo, v.DisplayLabel)
        : Resources.Strings.Builds_Action_SaveAsPreset;

    /// <summary>Store the edits as a named preset WITHOUT touching what Steam is running.</summary>
    [RelayCommand]
    private void SaveAsPreset()
    {
        if (ActiveGame is not { } game) return;

        string text = IsEditing ? EditorText : _vault.ReadLiveText(game.AppId);
        if (string.IsNullOrWhiteSpace(text)) return;

        var saved = _vault.SaveText(game.AppId, text, null);
        if (saved is null)
        {
            _toast.Show(Resources.Strings.Builds_Title, Resources.Strings.Builds_Save_Failed, error: true);
            return;
        }

        IsEditing = false;
        RefreshVariants();
        SelectedVariant = Variants.FirstOrDefault(v => v.Hash == saved.Hash) ?? SelectedVariant;
        _toast.Show(Resources.Strings.Builds_Title, Resources.Strings.Builds_Preset_Saved);
    }

    // ── Depot download (select mode) ─────────────────────────────────

    /// <summary>
    /// One tickable depot in the download picker. A separate type from <see cref="DepotRow"/> on purpose:
    /// DepotRow is an immutable record shared by the browse table, and selection is transient state that
    /// belongs only to this mode.
    /// </summary>
    public partial class DepotPickRow : ObservableObject
    {
        public required long DepotId { get; init; }
        public required string Title { get; init; }
        public required string Meta { get; init; }
        public required long Size { get; init; }
        public string? ManifestId { get; init; }

        /// <summary>Path to the depot's manifest in Steam's depotcache, or null when Steam doesn't have
        /// it yet — which is no longer a blocker: the run loop fetches it from the API.</summary>
        public string? ManifestPath { get; init; }

        /// <summary>True when the manifest isn't on disk and will have to be fetched during the run.</summary>
        public bool NeedsFetch => ManifestPath is null;

        /// <summary>Set when this depot can't be downloaded, saying why. Null means it can.</summary>
        public string? BlockReason { get; init; }

        public bool CanDownload => BlockReason is null;

        public string? Os { get; init; }

        /// <summary>
        /// The depot's Steam language code ("english", "schinese", ...), or null for language-neutral
        /// content. Null is the important case: those depots are the actual game and are always taken.
        /// </summary>
        public string? Language { get; init; }

        /// <summary>
        /// A depot built for another platform (a macOS or Linux build). Still listed and still tickable,
        /// but not ticked by default: on DELTARUNE the macOS depot was 864 MB of the 1.7 GB a select-all
        /// pulled down. A depot with no declared OS is shared content and counts as ours.
        /// </summary>
        public bool IsOtherPlatform =>
            Os is { Length: > 0 } && !Os.Contains("windows", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// A shared redistributable (VC++/DirectX runtimes) owned by another app. Selectable, but not
        /// ticked by default: it's usually already installed system-wide, and its size is unknown until
        /// download time, so select-all would otherwise add an unknowable amount.
        /// </summary>
        public bool IsShared => FromAppId is not null;

        /// <summary>
        /// Content that belongs to a DLC rather than the base game. Carried so the language fallback can
        /// ignore it: DLC declares no language, so it is always "wanted", and letting it count as a
        /// selection would suppress the fallback on a game whose real content is all language depots.
        /// </summary>
        public bool IsDlc { get; init; }

        public long? FromAppId { get; init; }

        [ObservableProperty] private bool _isSelected;
    }


    /// <summary>True while the depot table is in "pick what to download" mode.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBrowseMode))]
    private bool _isSelectMode;

    /// <summary>Inverse of <see cref="IsSelectMode"/>, for collapsing the normal table.</summary>
    public bool IsBrowseMode => !IsSelectMode;

    [ObservableProperty] private IReadOnlyList<DepotPickRow> _depotPicks = [];

    /// <summary>"Download 4 depots (8.2 GB)" — recomputed as boxes are ticked.</summary>
    public string DownloadConfirmLabel => string.Format(
        Resources.Strings.Builds_Select_Confirm,
        DepotPicks.Count(p => p.IsSelected),
        Services.Downloads.ByteFormat.Size(DepotPicks.Where(p => p.IsSelected).Sum(p => p.Size)));

    public bool HasDepotSelection => DepotPicks.Any(p => p.IsSelected);

    /// <summary>Where the selected depots will be written. Chosen per download, before committing, so
    /// the space warning below can report against the drive that will actually receive the files.</summary>
    [ObservableProperty] private string _depotOutDir = "";

    /// <summary>
    /// Free bytes on <see cref="DepotOutDir"/>'s volume. Cached rather than read from a computed
    /// property: AvailableFreeSpace is a syscall and the label re-evaluates on every checkbox tick,
    /// which cannot change free space. Refreshed when the folder changes or select mode opens.
    /// </summary>
    [ObservableProperty] private long? _freeBytes;

    partial void OnDepotOutDirChanged(string value)
    {
        FreeBytes = DepotDownloaderService.FreeSpaceFor(value);
        RaiseSpaceProps();
    }

    /// <summary>Total bytes the ticked (and downloadable) depots will need.</summary>
    public long RequiredBytes =>
        DepotPicks.Where(p => p is { IsSelected: true, CanDownload: true }).Sum(p => p.Size);

    /// <summary>False only when we KNOW the drive is short. An unreadable drive is not a warning.</summary>
    public bool HasEnoughSpace => FreeBytes is not { } free || free >= RequiredBytes;

    /// <summary>"Needs 110 GB · 4.3 GB free on C:\" — turns red via the view when short.</summary>
    public string SpaceLabel => FreeBytes is not { } free
        ? ""
        : string.Format(Resources.Strings.Builds_Select_Space,
            Services.Downloads.ByteFormat.Size(RequiredBytes),
            Services.Downloads.ByteFormat.Size(free),
            DepotDownloaderService.DriveOf(DepotOutDir));

    private void RaiseSpaceProps()
    {
        OnPropertyChanged(nameof(RequiredBytes));
        OnPropertyChanged(nameof(HasEnoughSpace));
        OnPropertyChanged(nameof(SpaceLabel));
    }

    /// <summary>Pick a different destination without leaving select mode or losing the ticks.</summary>
    [RelayCommand]
    private void ChangeDepotFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = Resources.Strings.Builds_Select_ChooseFolder,
            InitialDirectory = DepotOutDir,
        };
        if (dialog.ShowDialog() == true) DepotOutDir = dialog.FolderName;
    }

    private void OnPickChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DepotPickRow.IsSelected)) return;
        OnPropertyChanged(nameof(DownloadConfirmLabel));
        OnPropertyChanged(nameof(HasDepotSelection));
        RaiseSpaceProps();
    }

    /// <summary>
    /// Enter select mode. Every content depot the lua declares becomes a row, ticked by default; a depot
    /// whose manifest isn't in Steam's depotcache is listed but unticked and disabled, because the
    /// downloader cannot run without one.
    /// </summary>
    [RelayCommand]
    private void StartDepotDownload()
    {
        // The header button is already gated on HasSelection; this is the belt-and-braces read, and it
        // gives us the appid the default destination is built from.
        if (ActiveGame is not { } game) return;

        foreach (var old in DepotPicks) old.PropertyChanged -= OnPickChanged;

        // Read once per picker open rather than cached: Steam's language can change without this app
        // restarting, and it is a single registry lookup.
        string? steamLanguage = SteamService.SteamLanguage;

        // Read once, not per row: ResolveKeys parses the lua AND Steam's config.vdf on every call.
        var keys = _depotTool.ResolveKeys(game.AppId);

        // DLC is included when it actually ships bytes. Excluding every r.IsDlc row was too broad: most
        // DLC "depots" are 0-byte entitlement markers with nothing to fetch (American Truck Simulator has
        // 54 of them), but a real chunk of them carry content — the same game has 19 holding 2.5 GB of
        // map and truck DLC, all of them keyed by the lua, which the picker simply never offered.
        // Size comes straight off the depot info, so this costs no extra lookups.
        var picks = _allInLua
            .Where(r => !r.IsDlc || r.Size > 0)
            .Select(r =>
            {
                // An ACTIVE pin means the user deliberately locked this build, so it wins outright and
                // must never be silently upgraded. Otherwise take the build Steam ships today: a
                // commented-out pin means "Auto Update Apps" is on, i.e. the user wants to track latest,
                // so downloading the build the lua originally shipped with would be the wrong version.
                // The commented pin is a last resort only, for depots with no public manifest at all
                // (beta-branch-only content), where it's the sole version we know of.
                //
                // Safe because depot decryption keys are per-DEPOT and stable across manifest versions:
                // the key already in the lua decrypts the current manifest just as well as the old one.
                string? mid = r.ManifestId ?? r.PublicManifestId ?? r.CommentedManifestId;
                string? path = mid is null ? null : _depotTool.ResolveManifestPath(r.Id, mid);

                // Size, best source first:
                //  1. the manifest itself — authoritative, and describes the exact build being fetched;
                //  2. the lua's setManifestid size, but ONLY when an active pin is what we're downloading,
                //     because that figure belongs to the pinned build rather than the current one;
                //  3. app info, which matches the public branch;
                //  4. the lua's figure anyway, for a depot app info never listed (size would be 0).
                long size =
                    ManifestFile.TryRead(path) is { SizeOnDisk: > 0 } mf ? mf.SizeOnDisk
                    : r.ManifestId is not null && r.LuaSize > 0 ? r.LuaSize
                    : r.Size > 0 ? r.Size
                    : r.LuaSize;

                // All three checks are local — opening the picker costs zero API calls however many
                // depots the game has. Only a depot with no declared version is unreachable outright;
                // a missing manifest is now just a fetch, provided we're signed in to make it.
                // A shared depot has no gid here by design — its manifest lives under the owning app and
                // is resolved at download time, so a missing id is only fatal when there's nowhere to
                // look it up. Both checks stay local; the picker still makes zero requests.
                // The key check matters most for DLC: a DLC depot always has a public manifest id, so the
                // manifest test can never catch one whose key the lua simply doesn't carry — it would be
                // offered, ticked, and then abort the whole download at the first depot.
                //
                // Shared depots are NOT exempt, unlike the manifest test above them. A shared depot's
                // *manifest* is resolved from the owning app at download time, but nothing ever resolves
                // a *key* there — ResolveKeys reads this game's lua and config.vdf and that is all the
                // downloader will ever get. Exempting them here would only move the failure later.
                string? blocked =
                    mid is null && r.FromAppId is null ? Resources.Strings.Builds_Select_NoManifest
                    : !keys.ContainsKey(r.Id) ? Resources.Strings.Builds_Select_NoKey
                    : path is null && !_depotTool.CanFetchManifests ? Resources.Strings.Builds_Select_NeedAuthKey
                    : null;

                var pick = new DepotPickRow
                {
                    DepotId = r.Id,
                    Title = r.Title,
                    Meta = r.Meta,
                    Size = size,
                    ManifestId = mid,
                    ManifestPath = path,
                    Os = r.Os,
                    Language = r.Language,
                    IsDlc = r.IsDlc,
                    FromAppId = r.FromAppId,
                    BlockReason = blocked,
                };
                pick.IsSelected = pick.CanDownload && !pick.IsOtherPlatform && !pick.IsShared
                                  && WantedLanguage(pick.Language, steamLanguage);
                return pick;
            })
            .ToList();

        // Whole-set decision, so it can only run once every row exists. Subscribing afterwards keeps it
        // from firing OnPickChanged (and the space recalculation) once per row it flips.
        ApplyNoLanguageMatchFallback(picks);

        foreach (var p in picks) p.PropertyChanged += OnPickChanged;
        DepotPicks = picks;

        // Seed the destination (and with it the free-space read) before the bar first renders.
        string defaultRoot = Path.Combine(
            DownloadsFolder(), "LuaTools Depots", game.AppId.ToString());
        try { Directory.CreateDirectory(defaultRoot); } catch { /* the Change picker still opens */ }
        DepotOutDir = defaultRoot;

        IsSelectMode = true;
        OnPropertyChanged(nameof(DownloadConfirmLabel));
        OnPropertyChanged(nameof(HasDepotSelection));
        RaiseSpaceProps();
    }

    [RelayCommand]
    private void CancelDepotDownload()
    {
        foreach (var p in DepotPicks) p.PropertyChanged -= OnPickChanged;
        DepotPicks = [];
        IsSelectMode = false;
    }

    /// <summary>
    /// Should this depot be ticked by default, on language grounds alone?
    /// </summary>
    /// <remarks>
    /// Null language means language-NEUTRAL content — the actual game — and is always wanted. That case
    /// carries the whole design: Witcher 3 has 39 neutral depots against 30 language ones, so a rule that
    /// only matched languages would strip most of the game.
    ///
    /// English is always taken as well. It is the near-universal fallback, it is usually what a missing
    /// or partial localisation degrades to, and it costs one depot.
    /// </remarks>
    private static bool WantedLanguage(string? depotLanguage, string? steamLanguage) =>
        depotLanguage is null
        || depotLanguage.Equals("english", StringComparison.OrdinalIgnoreCase)
        || (steamLanguage is not null
            && depotLanguage.Equals(steamLanguage, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// If the language rule ticked nothing, fall back to ticking every language depot.
    /// </summary>
    /// <remarks>
    /// Only when the selection is COMPLETELY empty. The obvious-looking test — "no language depot got
    /// picked" — is wrong, because plenty of games ship no English depot at all and carry English in
    /// their neutral content, offering language depots purely as extra localisations. Cyberpunk 2077
    /// (10 language depots, none English) and The Witcher 3 (8, none English) both work that way, and
    /// that test would have selected every localisation for them, which is the opposite of the point.
    ///
    /// An empty selection means the game is all language depots and none matched — a Japanese- or
    /// Chinese-only release on a machine whose Steam language it doesn't offer. Taking everything there
    /// errs toward a working install; the user can untick.
    /// </remarks>
    private static void ApplyNoLanguageMatchFallback(IReadOnlyList<DepotPickRow> picks)
    {
        // DLC is excluded from the test on purpose: it declares no language, so it is always selected,
        // and counting it here would mean a game that ships one DLC depot never gets the fallback.
        if (picks.Any(p => p.IsSelected && !p.IsDlc)) return; // neutral content (or a match) covers it

        var languageDepots = picks.Where(p => p.Language is not null).ToList();
        if (languageDepots.Count == 0) return;      // nothing language-specific to fall back to

        foreach (var p in languageDepots)
            p.IsSelected = p.CanDownload && !p.IsOtherPlatform && !p.IsShared;
    }

    [RelayCommand]
    private void SelectAllDepots()
    {
        foreach (var p in DepotPicks) p.IsSelected = p.CanDownload;
    }

    /// <summary>
    /// The user's Downloads folder, honouring a relocated one.
    /// </summary>
    /// <remarks>
    /// There is no %DOWNLOADS% environment variable and <c>Environment.SpecialFolder</c> has no Downloads
    /// member, so the only correct source is the Known Folder registry value. That matters here more than
    /// usual: anyone who moved Downloads onto a bigger drive is precisely the person pulling tens of GB of
    /// depot content, and a naive %USERPROFILE%\Downloads would send it to their system drive instead.
    ///
    /// Registry rather than SHGetKnownFolderPath deliberately — this codebase has no P/Invoke anywhere,
    /// and SteamService already resolves Steam's path the same way.
    /// </remarks>
    private static string DownloadsFolder()
    {
        // FOLDERID_Downloads. "User Shell Folders" holds the unexpanded form (e.g. "%USERPROFILE%\..."),
        // which is why it is read in preference to the expanded "Shell Folders" copy Windows keeps stale.
        const string DownloadsGuid = "{374DE290-123F-4565-9164-39C4925E467B}";
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
            if (key?.GetValue(DownloadsGuid) is string raw && raw.Length > 0)
            {
                string expanded = Environment.ExpandEnvironmentVariables(raw);
                if (Directory.Exists(expanded)) return expanded;
            }
        }
        catch { /* unreadable registry: fall through to the profile-relative guess */ }

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string guess = Path.Combine(profile, "Downloads");
        return Directory.Exists(guess) ? guess : profile;
    }

    /// <summary>Queue ONE item covering the whole selection, then jump to Downloads.</summary>
    [RelayCommand]
    private void ConfirmDepotDownload()
    {
        if (ActiveGame is not { } game) return;

        var selections = DepotPicks
            .Where(p => p is { IsSelected: true, CanDownload: true })
            .Select(p => new DepotSelection(p.DepotId, p.ManifestId, p.ManifestPath, p.Size)
            {
                FromAppId = p.FromAppId,
            })
            .ToList();
        if (selections.Count == 0) return;

        // Destination was chosen in select mode (so the space warning could report against it). Used
        // verbatim and captured by the job closure, so Pause/Resume and Retry reuse the same folder.
        string outDir = DepotOutDir;

        string name = string.IsNullOrWhiteSpace(game.Name) ? game.AppId.ToString() : game.Name;
        _queue.Enqueue(_jobs.CreateDepotJob(game.AppId, name, selections, outDir));
        CancelDepotDownload();
        RequestShowDownloads?.Invoke();
    }

    /// <summary>Set by App: navigate to the Downloads page once a depot job is queued.</summary>
    public Action? RequestShowDownloads { get; set; }

    // ── Depot / DLC breakdown (moved here from the Manage flyout) ───

    [ObservableProperty] private bool _isLoadingDepots;
    [ObservableProperty] private string? _depotError;

    /// <summary>True while inspecting a build that ISN'T the one Steam is running. The switches still
    /// work (they edit that build in place), but the change lands differently (saved immediately, Steam
    /// unaffected until Apply), so it's worth saying so.</summary>
    [ObservableProperty] private bool _editingInactiveBuild;

    // Unfiltered rows as BuildRows produced them. The public lists below are the SEARCHED view of these.
    // Filtering must never re-run BuildRows, which re-parses the lua and re-reads steamcmd.
    private IReadOnlyList<DepotRow> _allInLua = [];
    private IReadOnlyList<DepotRow> _allMissing = [];
    private IReadOnlyList<DepotRow> _allUnknown = [];

    [ObservableProperty] private IReadOnlyList<DepotRow> _inLua = [];
    [ObservableProperty] private IReadOnlyList<DepotRow> _missing = [];
    [ObservableProperty] private IReadOnlyList<DepotRow> _unknown = [];

    /// <summary>Free-text filter over all three depot sections at once.</summary>
    [ObservableProperty] private string _depotSearchText = "";

    partial void OnDepotSearchTextChanged(string value)
    {
        ApplyDepotFilter();
        OnPropertyChanged(nameof(HasNoDepotMatches));
    }

    /// <summary>True when a search is active and matched nothing anywhere. Drives a "no matches" line so
    /// a filtered-to-empty table doesn't just look broken.</summary>
    public bool HasNoDepotMatches =>
        !string.IsNullOrWhiteSpace(DepotSearchText) && !IsLoadingDepots
        && InLua.Count == 0 && Missing.Count == 0 && Unknown.Count == 0;

    /// <summary>Re-slice the three sections from the unfiltered rows. Cheap: pure in-memory predicate.</summary>
    private void ApplyDepotFilter()
    {
        string q = DepotSearchText;
        InLua = _allInLua.Where(r => r.Matches(q)).ToList();
        Missing = _allMissing.Where(r => r.Matches(q)).ToList();
        Unknown = _allUnknown.Where(r => r.Matches(q)).ToList();
        OnPropertyChanged(nameof(HasNoDepotMatches));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InLuaToggleLabel))]
    private bool _isInLuaExpanded = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MissingToggleLabel))]
    private bool _isMissingExpanded = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnknownToggleLabel))]
    private bool _isUnknownExpanded;

    public int InLuaCount => InLua.Count;
    public int MissingCount => Missing.Count;
    public int UnknownCount => Unknown.Count;
    public bool HasInLua => InLua.Count > 0;
    public bool HasMissing => Missing.Count > 0;
    public bool HasUnknown => Unknown.Count > 0;
    public string InLuaToggleLabel => $"{(IsInLuaExpanded ? "▾" : "▸")} {Resources.Strings.Manage_Toggle_InLua}";
    public string MissingToggleLabel => $"{(IsMissingExpanded ? "▾" : "▸")} {Resources.Strings.Manage_Toggle_Missing}";
    public string UnknownToggleLabel => $"{(IsUnknownExpanded ? "▾" : "▸")} {Resources.Strings.Manage_Toggle_Unknown}";

    partial void OnInLuaChanged(IReadOnlyList<DepotRow> value)
    { OnPropertyChanged(nameof(InLuaCount)); OnPropertyChanged(nameof(HasInLua)); }
    partial void OnMissingChanged(IReadOnlyList<DepotRow> value)
    { OnPropertyChanged(nameof(MissingCount)); OnPropertyChanged(nameof(HasMissing)); }
    partial void OnUnknownChanged(IReadOnlyList<DepotRow> value)
    { OnPropertyChanged(nameof(UnknownCount)); OnPropertyChanged(nameof(HasUnknown)); }

    [RelayCommand] private void ToggleInLua() => IsInLuaExpanded = !IsInLuaExpanded;
    [RelayCommand] private void ToggleMissing() => IsMissingExpanded = !IsMissingExpanded;
    [RelayCommand] private void ToggleUnknown() => IsUnknownExpanded = !IsUnknownExpanded;

    /// <summary>Open a depot/DLC's SteamDB page (depot page for depots, app page for DLC).</summary>
    [RelayCommand]
    private static void OpenSteamDb(DepotRow row) => SteamService.OpenUrl(row.SteamDbUrl);

    /// <summary>
    /// Copy the GAME's app id. Takes no row: a <see cref="DepotRow"/> carries a depot id, a DLC app id
    /// and an owning-app id, but never the app id of the game whose page this is — that is page state.
    /// </summary>
    [RelayCommand]
    private void CopyAppId()
    {
        if (ActiveGame is not { } game) return;
        if (!SteamService.CopyToClipboard(game.AppId.ToString()))
            _toast.Show(Resources.Strings.Common_CopyAppId, Resources.Strings.Err_ClipboardBusy, error: true);
    }

    /// <summary>Pin/unpin one depot. Comments its setManifestid line in or out.</summary>
    [RelayCommand]
    private void ToggleLock(DepotRow row)
    {
        if (row.CanLock) EditLive(row, text => LuaEditor.SetDepotLocked(text, row.ToggleId, !row.IsLocked));
    }

    /// <summary>Switch one depot on/off. Comments its addappid (decryption key) line in or out.</summary>
    [RelayCommand]
    private void ToggleEnabled(DepotRow row)
    {
        if (row.CanEnable) EditLive(row, text => LuaEditor.SetDepotEnabled(text, row.ToggleId, !row.IsEnabled));
    }

    /// <summary>
    /// Apply a text edit to whichever lua the depot table is currently showing.
    ///
    /// <para>
    /// Two routes, because there are two different things on screen:
    /// </para>
    /// <list type="bullet">
    /// <item><b>The live lua</b> (the row Steam is running). Write the file Steam reads. If that row is
    /// the Default, the edit lands in the Default slot immediately: <see cref="RememberEditBase"/> skips
    /// the Default, so no edit base is parked, and the <see cref="LuaVault.SyncDefaultFromLive"/> inside
    /// the refresh below adopts the new bytes. If it's a BUILD or preset, an edit base IS parked, the
    /// header's "Save to &lt;name&gt;" appears, and that row stays selected and badged ACTIVE with an
    /// "unsaved" marker: see <see cref="ResolveActiveHash"/>, which exists because the row silently
    /// losing its ACTIVE state here is what used to dump the user back on the Default mid-edit.</item>
    /// <item><b>A build that isn't applied</b>. Write straight into that stored variant, keeping its
    /// name and build id. There's no live file to park a pending edit in (the live lua is a *different*
    /// variant), so this saves immediately; Steam is untouched until the user hits Apply.</item>
    /// </list>
    ///
    /// <para>
    /// Editing must never be routed to the live file while a non-live variant is displayed. The table
    /// on screen and the file being changed would be two different luas.
    /// </para>
    ///
    /// <para>
    /// <b>Two deliberate omissions, both reviewed. They look like oversights and aren't:</b>
    /// </para>
    /// <list type="bullet">
    /// <item><b>No Save step, and no undo, for a toggle on the Default.</b> The Default is the working
    /// copy, so flipping a switch rewrites the live lua and replaces the stored Default, discarding what
    /// was there. Keeping a state before experimenting is what "Save as preset" is for.</item>
    /// <item><b>No restart prompt anywhere on this page.</b> OST/BST watch <c>config/stplug-in</c>, so
    /// rewriting the live lua applies it immediately. <see cref="Apply"/> and <see cref="SaveEdit"/> used
    /// to prompt; none of them do now, and a modal on every switch flip would have been unusable anyway.</item>
    /// </list>
    /// </summary>
    private void EditLive(DepotRow row, Func<string, string> edit)
    {
        if (ActiveGame is not { } game) return;

        bool editingLive = SelectionDescribesLive;
        var target = SelectedVariant?.Variant;

        string before = editingLive || target is null
            ? _vault.ReadLiveText(game.AppId)
            : _vault.ReadText(game.AppId, target.Hash);
        if (string.IsNullOrEmpty(before)) return;

        string after = edit(before);
        if (after == before) return; // already in that state. Don't churn the file or the UI

        // Suppress from BEFORE the write: both write paths raise VaultChanged, whose handler refreshes
        // the variants and reselects, which would fire its own spinner-showing reload before we got to
        // guard it. Only the redundant depot reloads are held off; one quiet pass runs at the end.
        bool ok;
        string? newHash = null;
        _suppressDepotReload = true;
        try
        {
            if (editingLive || target is null)
            {
                RememberEditBase(game.AppId);
                ok = _vault.WriteLive(game.AppId, after);
            }
            else
            {
                var updated = _vault.UpdateVariant(game.AppId, target.Hash, after);
                ok = updated is not null;
                newHash = updated?.Hash; // content-addressed: editing renames the stored file
            }
            if (ok) RefreshVariants();
        }
        finally { _suppressDepotReload = false; }

        if (!ok)
        {
            _toast.Show(Resources.Strings.Builds_Title, Resources.Strings.Builds_Save_Failed, error: true);
            return;
        }

        // Follow the edited variant to its new hash, or the switcher would snap back to the active build
        // and the user would watch their selection jump away mid-edit.
        if (newHash is not null)
            SelectedVariant = Variants.FirstOrDefault(v => v.Hash == newHash) ?? SelectedVariant;

        _ = LoadDepotsAsync(quiet: true);
    }

    /// <summary>
    /// Record which variant the live lua is about to diverge FROM, so Save can put the edit back where it
    /// came from. Must run BEFORE the write: afterwards the bytes match nothing and the origin is lost.
    /// A no-op once already diverged: the base is the variant the run of edits started at, not the last
    /// one touched.
    ///
    /// <para>
    /// The DEFAULT is deliberately never a base. It isn't a saved thing you write back to. It's the
    /// working copy, and it follows the live lua on its own (<see cref="LuaVault.SyncDefaultFromLive"/>).
    /// Setting it as a base would both offer a pointless "Save to Default" button and, worse, park an
    /// EditBaseHash that makes the sync skip its own slot.
    /// </para>
    /// </summary>
    private void RememberEditBase(long appId)
    {
        if (_vault.GetActiveVariant(appId) is { Kind: not LuaVariantKind.Default } current)
            _vault.SetEditBase(appId, current.Hash);
    }

    /// <summary>
    /// Compare the SELECTED variant's lua against the app's real depots + declared DLC. "In lua" =
    /// anything the lua adds (keyed depot OR keyless DLC entitlement). "Missing" = real depots/DLC the
    /// lua omits. "Unknown" = broken/0-byte/shared/unnamed noise.
    /// </summary>
    /// <param name="quiet">
    /// True when re-reading after a depot switch. The rows are already on screen and every input is
    /// cached in memory. Blanking the table and showing "Loading depot info…" because a checkbox
    /// changed just makes the list flash. Leaves the current rows visible and swaps them at the end.
    /// </param>
    private async Task LoadDepotsAsync(bool quiet = false)
    {
        long token = ++_depotLoadToken;
        if (ActiveGame is not { } game)
        {
            // Clear the unfiltered rows too: leaving them would let a later filter pass resurrect the
            // previous game's depots.
            _allInLua = []; _allMissing = []; _allUnknown = [];
            InLua = []; Missing = []; Unknown = []; DepotError = null; IsLoadingDepots = false;
            return;
        }

        DepotError = null;
        if (!quiet)
        {
            IsLoadingDepots = true;
            _allInLua = []; _allMissing = []; _allUnknown = [];
            InLua = []; Missing = []; Unknown = [];
            LatestBuildLabel = null;
        }

        long appId = game.AppId;

        // Every variant is editable: the live one via the live lua, any other one in place. Only the
        // ROUTE differs (see EditLive), so this just drives the explanatory note.
        bool editingLive = SelectionDescribesLive;
        EditingInactiveBuild = !editingLive;

        // Read from wherever the edits GO. For a preset that isn't applied that's its stored copy. The
        // point of the switcher, inspecting one before committing to it. For the live row it must be the
        // live file: mid-edit the stored copy is the PRE-edit bytes, and describing those put the switches
        // back the way they were before the user touched them (see SelectionDescribesLive).
        string? storedHash = editingLive ? null : SelectedVariant!.Hash;

        var lua = await Task.Run(() => storedHash is not null
            ? ParseVariant(appId, storedHash)
            : _vault.LivePath(appId) is { } live ? LuaFileParser.Parse(live, appId) : null);

        if (token != _depotLoadToken) return; // user moved on

        var info = await _depotInfo.GetAsync(game.AppId);
        if (token != _depotLoadToken) return;

        if (info is null)
        {
            DepotError = Resources.Strings.Manage_DepotError;
            IsLoadingDepots = false;
            return;
        }

        if (info.PublicBuildId is not null)
            LatestBuildLabel = string.Format(Resources.Strings.Builds_LatestBuild, info.PublicBuildId);

        BuildRows(info, lua);
        IsLoadingDepots = false;

        // Lazily resolve any DLC still showing "DLC <id>" via appdetails (throttled, persisted), then
        // rebuild rows. Covers both depot-backed DLC and store-only DLC from listofdlc.
        var named = Declarations(lua).Where(e => e.Value.Comment is not null).Select(e => e.Key).ToHashSet();
        var unnamedDlc = info.Depots.Where(d => d.DlcAppId is not null).Select(d => d.DlcAppId!.Value)
            .Concat(info.DlcIds).Distinct()
            .Where(a => _appList.GetName(a) is null && _appInfo.GetCached(a)?.Name is null && !named.Contains(a))
            .ToList();

        if (unnamedDlc.Count > 0)
        {
            await Parallel.ForEachAsync(unnamedDlc, new ParallelOptions { MaxDegreeOfParallelism = 4 },
                async (id, _) => await _appInfo.ResolveAsync(id));
            if (token == _depotLoadToken) BuildRows(info, lua); // caches now populated
        }
    }

    /// <summary>
    /// Every id the lua declares, active or switched off, keyed by id. Disabled entries have to be
    /// included or a depot the user just switched off would drop out of "In lua" and take its switch
    /// with it. The row has to stay put so it can be switched back on.
    /// </summary>
    /// <summary>
    /// Add depots the lua declares that Steam's app info never mentioned, so they still appear (and stay
    /// downloadable) instead of silently vanishing.
    /// </summary>
    /// <remarks>
    /// <para>Some apps require a Steam access token this app doesn't hold. steamcmd answers those with
    /// <c>"_missing_token": true</c> and a <b>null</b> depots block — no depot list at all, not an empty
    /// one. Risk of Rain 2, NBA 2K27, Touhou Luna Nights, Soundpad and Beam Eye Tracker all behave this
    /// way. Without this the whole page came up blank with nothing explaining why, because "In lua" is
    /// the intersection of the declarations and a depot list that was never delivered.</para>
    ///
    /// <para>Nothing is lost by falling back: the lua already carries every input a download needs — the
    /// depot id, its decryption key, and a manifest id (usually a commented pin). Only <b>size</b> is
    /// missing, so these rows show no MB and contribute nothing to the free-space estimate.</para>
    ///
    /// <para>Applied per id rather than only when the whole fetch came back empty, so it equally covers a
    /// single depot the app info happens to omit.</para>
    /// </remarks>
    private static void AddDeclaredButUnlisted(
        List<ContentDepot> items, Dictionary<long, LuaEntry> declared, long baseAppId,
        HashSet<long> depotDlcIds)
    {
        var known = items.Select(d => d.Id).ToHashSet();

        foreach (var (id, entry) in declared)
        {
            // Already covered, either as a real depot or as the DLC app id behind one.
            if (known.Contains(id) || depotDlcIds.Contains(id)) continue;

            // The base app's own addappid line is the app key, not a depot — downloading it is meaningless.
            if (id == baseAppId) continue;

            // A key means content; without one it's a store entitlement, which is what DlcAppId marks.
            bool isDepot = entry.HasKey;

            // Shared redistributables are only identifiable from the lua's comment here, and it matters:
            // FromAppId is what keeps them off the default selection (they're usually already installed).
            long? fromAppId = isDepot ? SharedOwnerFromComment(entry.Comment) : null;

            // The lua's own setManifestid size, when it has one: without it these rows report 0 bytes,
            // which understates the download total and leaves the free-space warning blind.
            items.Add(new ContentDepot(id, entry.SizeOnDisk ?? 0, isDepot ? null : id,
                fromAppId is not null,
                Os: null, Language: isDepot ? LanguageFromComment(entry.Comment) : null)
            { FromAppId = fromAppId });
        }
    }

    /// <summary>
    /// Steam's language codes, as they appear in a depot's <c>config.language</c>. Used to recognise a
    /// synthesized depot's language from its lua comment, which is the only clue available when Steam
    /// withheld the app info — without it a "Schinese" depot reads as language-neutral and is selected
    /// for everyone.
    /// </summary>
    private static readonly HashSet<string> SteamLanguageCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "arabic", "bulgarian", "schinese", "tchinese", "czech", "danish", "dutch", "english", "finnish",
        "french", "german", "greek", "hungarian", "indonesian", "italian", "japanese", "koreana",
        "norwegian", "polish", "portuguese", "brazilian", "romanian", "russian", "spanish", "latam",
        "swedish", "thai", "turkish", "ukrainian", "vietnamese",
    };

    /// <summary>The Steam language code a lua comment names, or null if it names something else.</summary>
    private static string? LanguageFromComment(string? comment)
    {
        if (string.IsNullOrWhiteSpace(comment)) return null;
        foreach (string word in comment.Split([' ', '	', '(', ')', ',', '-', ':'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (SteamLanguageCodes.TryGetValue(word, out string? code)) return code.ToLowerInvariant();
        return null;
    }

    /// <summary>Owning app id from a lua comment like "VC 2019 Redist (Shared from App 228980)".</summary>
    private static long? SharedOwnerFromComment(string? comment) =>
        comment is not null && SharedFromAppRegex().Match(comment) is { Success: true } m
        && long.TryParse(m.Groups[1].Value, out long owner)
            ? owner
            : null;

    [GeneratedRegex(@"Shared\s+from\s+App\s+(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SharedFromAppRegex();

    private static Dictionary<long, LuaEntry> Declarations(LuaContents? lua)
    {
        var all = new Dictionary<long, LuaEntry>();
        if (lua is null) return all;
        foreach (var e in lua.DisabledEntries) all[e.Id] = e;
        foreach (var e in lua.Entries) all[e.Id] = e;   // an active line wins over a commented one
        return all;
    }

    /// <summary>Parse a stored variant by copying it out to a temp file (LuaFileParser reads paths).</summary>
    private LuaContents? ParseVariant(long appId, string? hash)
    {
        if (hash is null) return null;
        string tmp = Path.Combine(Path.GetTempPath(), $"luabuilds_{Guid.NewGuid():N}.lua");
        try
        {
            File.WriteAllText(tmp, _vault.ReadText(appId, hash));
            return LuaFileParser.Parse(tmp, appId);
        }
        catch { return null; }
        finally { try { File.Delete(tmp); } catch { /* best effort */ } }
    }

    /// <summary>
    /// Split depots three ways: In-lua (declared), Missing (real depots the lua lacks), and Unknown
    /// (noise. Unnamed DLC, 0-byte/broken depots, shared redists). DLC named from the caches.
    /// </summary>
    private void BuildRows(AppDepotInfo info, LuaContents? lua)
    {
        var declared = Declarations(lua);
        var active = lua?.Entries.Select(e => e.Id).ToHashSet() ?? [];
        long baseAppId = lua?.BaseAppId ?? info.AppId;
        var luaNames = declared.Where(kv => kv.Value.Comment is not null)
            .ToDictionary(kv => kv.Key, kv => kv.Value.Comment!);

        // Unified comparison set: every real depot + every declared DLC that has no depot of its own
        // (store-only entitlements). Without the latter, keyless entitlement DLC would vanish.
        var items = new List<ContentDepot>(info.Depots);
        var depotDlcIds = info.Depots.Where(d => d.DlcAppId is not null).Select(d => d.DlcAppId!.Value).ToHashSet();
        foreach (long dlcId in info.DlcIds)
            if (!depotDlcIds.Contains(dlcId))
                items.Add(new ContentDepot(dlcId, 0, dlcId, IsShared: false, Os: null, Language: null));

        AddDeclaredButUnlisted(items, declared, baseAppId, depotDlcIds);

        bool DlcNameKnown(long dlcId) =>
            _appList.GetName(dlcId) is not null || _appInfo.GetCached(dlcId)?.Name is not null || luaNames.ContainsKey(dlcId);

        DepotRow Row(ContentDepot d)
        {
            // Prefer a real Steam name; fall back to the lua comment (e.g. "VC 2022 Redist").
            string? steamName = d.DlcAppId is { } dlcId ? (_appList.GetName(dlcId) ?? _appInfo.GetCached(dlcId)?.Name) : null;
            string title =
                steamName
                ?? luaNames.GetValueOrDefault(d.Id)
                ?? (d.IsDlc ? string.Format(Resources.Strings.Manage_DlcName, d.DlcAppId)
                    : d.IsShared ? Resources.Strings.Manage_SharedDepot : Resources.Strings.Manage_Depot);

            var meta = new List<string> { d.Id.ToString() };
            if (d.Size > 0) meta.Add(FormatSize(d.Size));
            if (!string.IsNullOrWhiteSpace(d.Os)) meta.Add(PrettyOs(d.Os));
            if (!string.IsNullOrWhiteSpace(d.Language)) meta.Add(d.Language!);

            string url = d.DlcAppId is { } dlc
                ? $"https://steamdb.info/app/{dlc}/"
                : $"https://steamdb.info/depot/{d.Id}/";

            // The switches act on the id the lua actually DECLARES. For a DLC that's the DLC app id, not
            // the depot id. Toggling the depot id would rewrite a line that doesn't exist.
            long declId = declared.ContainsKey(d.Id) ? d.Id : d.DlcAppId ?? d.Id;
            declared.TryGetValue(declId, out var entry);
            bool inLua = entry is not null;

            return new DepotRow(d.Id, title, string.Join("  ·  ", meta), d.IsDlc, d.IsShared, url,
                entry?.ManifestId, entry?.CommentedManifestId, d.PublicManifestId,
                IsInLua: inLua,
                IsEnabled: active.Contains(declId),
                CanToggle: inLua,   // anything the lua declares can be switched, in any variant
                IsBaseApp: declId == baseAppId)
            {
                ToggleId = declId, Size = d.Size, Os = d.Os, Language = d.Language,
                FromAppId = d.FromAppId, LuaSize = entry?.SizeOnDisk ?? 0,
            };
        }

        // In lua = the lua declares this id (a keyed depot OR a keyless DLC entitlement) or its DLC app
        // id, including declarations the user has switched off, so they can switch them back on.
        bool IsInLua(ContentDepot d) => declared.ContainsKey(d.Id) || (d.DlcAppId is { } a && declared.ContainsKey(a));

        // Unknown = noise to tuck away: shared redists, unnamed DLC, or 0-byte/broken depots.
        bool IsUnknown(ContentDepot d) =>
            d.IsShared
            || (d.IsDlc && !DlcNameKnown(d.DlcAppId!.Value))
            || (!d.IsDlc && d.Size == 0);

        _allInLua = items.Where(IsInLua).Select(Row).ToList();
        _allMissing = items.Where(d => !IsInLua(d) && !IsUnknown(d)).Select(Row).ToList();
        _allUnknown = items.Where(d => !IsInLua(d) && IsUnknown(d)).Select(Row).ToList();
        ApplyDepotFilter();
    }

    private static string PrettyOs(string os) => os switch
    {
        "windows" => "Windows",
        "macos" or "macosx" => "macOS",
        "linux" => "Linux",
        _ => os
    };

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "";
        double gb = bytes / 1024d / 1024d / 1024d;
        if (gb >= 1) return $"{gb:0.##} GB";
        double mb = bytes / 1024d / 1024d;
        return $"{mb:0.#} MB";
    }

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }
}
