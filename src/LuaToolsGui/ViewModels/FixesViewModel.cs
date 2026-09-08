using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaToolsGui.Models;
using LuaToolsGui.Services;
using LuaToolsGui.Services.Downloads;

namespace LuaToolsGui.ViewModels;

/// <summary>A game card in the Fixes grid.</summary>
public partial class FixGameCardVm(DenuvoGameListing g) : ObservableObject
{
    public string AppId { get; } = g.AppId;
    public string Name { get; } = g.Name;
    public string? HeaderImage { get; } = g.HeaderImage;
    public int FixCount { get; } = g.FixCount;
    public IReadOnlyList<string> TagIds { get; } = g.Tags.Select(t => t.Id).ToList();
    public string FixCountLabel => string.Format(Resources.Strings.Fixes_Count, FixCount);

    /// <summary>Local cached cover path (set after CoverCache resolves it); bound via ImagePathToSource.</summary>
    [ObservableProperty] private string? _cover;
    private int _resolving;

    public bool Matches(string q) =>
        Name.Contains(q, StringComparison.OrdinalIgnoreCase) || AppId.Contains(q);

    /// <summary>Cache the header image to disk once (CoverCache, keyed by appid), then expose its path.</summary>
    public async Task EnsureCoverAsync(CoverCache covers)
    {
        if (Cover is not null || string.IsNullOrWhiteSpace(HeaderImage)) return;
        if (!long.TryParse(AppId, out long appid)) return;
        if (Interlocked.Exchange(ref _resolving, 1) == 1) return;
        try
        {
            string? local = covers.GetLocalPath(appid) ?? await covers.EnsureAsync(appid, HeaderImage!);
            if (local is not null) Cover = local;
        }
        finally { Interlocked.Exchange(ref _resolving, 0); }
    }
}

/// <summary>A tag filter pill; IsSelected drives its active highlight.</summary>
public partial class TagPillVm(DenuvoTag t) : ObservableObject
{
    public string Id { get; } = t.Id;
    public string Name { get; } = t.Name;
    [ObservableProperty] private bool _isSelected;
}

/// <summary>One fix (release) in the per-game flyout.</summary>
public partial class FixItemVm(DenuvoFix f) : ObservableObject
{
    public string Id { get; } = f.Id;
    public string Title { get; } = f.Title;
    public string? Description { get; } = f.Description;
    public IReadOnlyList<DenuvoTag> Tags { get; } = f.Tags;
    public bool HasManifest { get; } = f.HasManifest;
    public bool HasFix { get; } = f.HasFix;
    public string? ManifestFilename { get; } = f.ManifestFilename;
    public string? FixFilename { get; } = f.FixFilename;
    public string DateLabel { get; } = FormatDate(f.CreatedAt);

    /// <summary>In-flight queue items for this fix's two slots. The buttons and their progress bars bind
    /// straight through, so the shared queue stays the only owner of download state.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownloadManifest))]
    private DownloadItem? _manifestItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownloadFix))]
    private DownloadItem? _fixItem;

    /// <summary>
    /// Whether the game is installed on disk. Only the FIX slot cares: it extracts a zip into the game
    /// folder, so with no folder there is nothing to apply. The MANIFEST slot installs a lua and works
    /// whether or not the game is installed.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownloadFix), nameof(FixHint))]

    private bool _gameInstalled;

    /// <summary>True when the fix has been applied (its revert manifest exists on disk). Drives the Revert button.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasApplied), nameof(CanDownloadFix), nameof(FixHint))]
    private bool _isApplied;

    public bool HasApplied => IsApplied;

    public bool CanDownloadManifest => HasManifest && ManifestItem?.IsActive != true;
    public bool CanDownloadFix => HasFix && GameInstalled && !IsApplied && FixItem?.IsActive != true;

    /// <summary>Why the Fix button is greyed out, or null when it isn't. A null ToolTip shows nothing,
    /// so this doubles as the "should there be a tooltip at all" test.</summary>
    public string? FixHint =>
        IsApplied ? Resources.Strings.Fixes_Applied_Hint
        : GameInstalled ? null
        : Resources.Strings.Fixes_NotInstalled_Hint;

    private static string FormatDate(string? iso) =>
        DateTimeOffset.TryParse(iso, out var d) ? d.UtcDateTime.ToString("d MMM yyyy") : "";
}

/// <summary>
/// "Fixes" page: browse games with Denuvo fixes (grid + search + tag filter), open a game to see its
/// fixes, and download a fix's manifest (force-locked lua install) or fix zip (extract into the game
/// folder if installed). Downloads are auth-gated and count toward the 25/day limit (server-side).
/// </summary>
public partial class FixesViewModel : PagedListViewModel<FixGameCardVm>
{
    private readonly LuaToolsApiClient api;
    private readonly AuthService auth;
    private readonly CoverCache covers;
    private readonly ToastService toast;
    private readonly SettingsService settings;
    private readonly DownloadQueue queue;
    private readonly ManifestJobFactory jobs;
    private readonly SteamLibraryService library;
    private readonly SteamService steam;

    public FixesViewModel(
        LuaToolsApiClient api, AuthService auth, CoverCache covers, ToastService toast,
        SettingsService settings, DownloadQueue queue, ManifestJobFactory jobs,
        SteamLibraryService library, SteamService steam)
    {
        this.api = api;
        this.auth = auth;
        this.covers = covers;
        this.toast = toast;
        this.settings = settings;
        this.queue = queue;
        this.jobs = jobs;
        this.library = library;
        this.steam = steam;
        InitPageSize(settings.FixesPageSize);
    }

    /// <summary>Set by App so a guest hitting a download is sent through the Discord sign-in flow.</summary>
    public Func<Task>? RequestSignIn { get; set; }

    // The master list; the displayed page slice lives in the base's Items collection.
    private List<FixGameCardVm> _allGames = [];

    public ObservableCollection<TagPillVm> Tags { get; } = [];

    // IsLoading, EmptyMessage and the IsEmpty gating are inherited from PagedListViewModel<FixGameCardVm>.
    // Page size persists via SavePageSizeSetting below.
    protected override void SavePageSizeSetting(int size) => settings.FixesPageSize = size;

    /// <summary>Warm the cover images for just the freshly-shown page (idempotent, off-UI).</summary>
    protected override void OnPageSliced(IReadOnlyList<FixGameCardVm> slice)
    {
        foreach (var g in slice) _ = g.EnsureCoverAsync(covers);
    }

    [ObservableProperty] private string _searchText = "";
    partial void OnSearchTextChanged(string value) => ApplyFilter();

    [ObservableProperty] private string? _selectedTagId; // null = "All"

    // Appids with a lua in Steam's config/stplug-in ("my games"), so the page can filter the fix
    // listing down to games the user actually owns. Empty when Steam isn't set up / no luas installed.
    private HashSet<long> _installedAppIds = [];

    /// <summary>True once the listing has been fetched (set at the end of LoadAsync).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanFilter))]
    private bool _loaded;

    /// <summary>Gates the filter pills ("my games" + tags) behind a finished, non-empty listing.</summary>
    public bool CanFilter => Loaded && _allGames.Count > 0;

    /// <summary>Only show fix games the user has added (a lua in stplug-in). Mirrors Manage's "my games".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MyGamesHint))]
    private bool _myGamesOnly;

    public string MyGamesHint => _installedAppIds.Count == 0
        ? Resources.Strings.Fixes_MyGames_NotInstalled
        : string.Format(Resources.Strings.Fixes_MyGames_Count, _installedAppIds.Count);

    partial void OnMyGamesOnlyChanged(bool value)
    {
        if (value)
        {
            SelectedTagId = null; // one filter at a time — turning "my games" on drops any tag
            foreach (var pill in Tags) pill.IsSelected = false;
        }
        ApplyFilter();
    }

    // ── Detail flyout ───────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDetailOpen))]
    private FixGameCardVm? _selectedGame;

    public bool IsDetailOpen => SelectedGame is not null;
    public ObservableCollection<FixItemVm> Fixes { get; } = [];
    [ObservableProperty] private bool _isLoadingFixes;

    // Per-game tag filter (only meaningful when this game's fixes span multiple tags).
    private List<FixItemVm> _allFixes = [];
    public ObservableCollection<TagPillVm> FixTags { get; } = [];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFixTags))]
    private string? _selectedFixTagId;
    public bool HasFixTags => FixTags.Count > 0;

    // Downloads are owned by the shared DownloadQueue; per-fix progress lives on FixItemVm. The page no
    // longer has an IsBusy gate, so several fixes can be queued without waiting for each other.

    // ── Load ─────────────────────────────────────────────────────────

    /// <param name="force">True to re-fetch even if already loaded (the Refresh button); otherwise the
    /// listing loads once per session.</param>
    public async Task LoadAsync(bool force = false)
    {
        if (!force && _allGames.Count > 0) return; // load once per session
        IsLoading = true;
        try
        {
            var data = await api.GetDenuvoListingsAsync();
            if (data is null)
            {
                EmptyMessage = Resources.Strings.Fixes_Err_Load;
                return;
            }

            _allGames = data.Games.Select(g => new FixGameCardVm(g)).ToList();
            Tags.Clear();
            foreach (var t in data.Tags) Tags.Add(new TagPillVm(t));

            // "My games" filter source: the same stplug-in scan the Manage page uses, so the toggle
            // shows only games the user actually added. Scanned once per listing load.
            _installedAppIds = steam.StPlugInDir is { } dir
                ? await Task.Run(() => LuaInstaller.EnumerateInstalled(dir).Select(i => i.AppId).ToHashSet())
                : [];
            OnPropertyChanged(nameof(MyGamesHint));

            ApplyFilter();
            if (_allGames.Count == 0) EmptyMessage = Resources.Strings.Fixes_Empty_None;
        }
        catch
        {
            EmptyMessage = Resources.Strings.Fixes_Err_Load;
        }
        finally
        {
            IsLoading = false;
            Loaded = true; // gates the filter pills: they appear only once the listing settled
        }
    }

    [RelayCommand]
    private Task Refresh() => RefreshWithCooldownAsync(async () =>
    {
        if (SearchText.Length > 0) SearchText = ""; // reset filter → full list visible
        if (SelectedTagId is not null) SelectTag(SelectedTagId); // clear active tag (toggles off)
        if (MyGamesOnly) MyGamesOnly = false; // ditto for the "my games" filter
        await LoadAsync(force: true);
        toast.Show(Resources.Strings.Fixes_Toast_Refreshed_Title,
            string.Format(Resources.Strings.Fixes_Toast_Refreshed_Body, _allGames.Count));
    });

    [RelayCommand]
    private void SelectTag(string? tagId)
    {
        SelectedTagId = SelectedTagId == tagId ? null : tagId; // toggle off when re-clicked
        if (MyGamesOnly) MyGamesOnly = false; // one filter at a time — picking a tag drops "my games"
        foreach (var pill in Tags) pill.IsSelected = pill.Id == SelectedTagId;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        string q = SearchText.Trim();
        IEnumerable<FixGameCardVm> shown = _allGames;
        if (SelectedTagId is { } tag) shown = shown.Where(g => g.TagIds.Contains(tag));
        if (MyGamesOnly) shown = shown.Where(g => long.TryParse(g.AppId, out long id) && _installedAppIds.Contains(id));
        if (q.Length > 0) shown = shown.Where(g => g.Matches(q));

        // Hand the filtered list to the base: it slices the visible page and (via OnPageSliced) warms
        // that page's covers.
        SetFiltered(shown);
    }

    // ── Detail flyout ───────────────────────────────────────────────

    /// <summary>
    /// Open the detail flyout for a specific game by its Steam AppId. Loads the listing if needed,
    /// finds the game card, and opens its fix detail (the flyout makes its own per-appid API call).
    /// Used by the luatools://fix/ protocol handler.
    /// </summary>
    public async Task OpenForAppIdAsync(long appId)
    {
        if (_allGames.Count == 0)
            await LoadAsync();

        var game = _allGames.FirstOrDefault(g => g.AppId == appId.ToString());
        if (game is null) return;

        SearchText = "";
        SelectedTagId = null;
        ApplyFilter();

        await OpenGame(game);
    }

    [RelayCommand]
    private void CopyAppId(FixGameCardVm game)
    {
        if (!SteamService.CopyToClipboard(game.AppId))
            toast.Show(Resources.Strings.Common_CopyAppId, Resources.Strings.Err_ClipboardBusy, error: true);
    }

    /// <summary>
    /// Open the game's Steam install folder — where <c>ApplyDenuvoFix</c> extracts a fix to.
    /// </summary>
    /// <remarks>
    /// Resolved on click, not bound to a property: <c>GetInstallDir</c> walks libraryfolders.vdf and the
    /// appmanifest files, which is far too much work to repeat for every card in a grid on every render.
    /// The cost of that is the action being offered for games that aren't installed, so it reports the
    /// same "game not found" toast the fix flow already uses rather than failing silently.
    /// </remarks>
    [RelayCommand]
    private void ShowInFolder(FixGameCardVm game)
    {
        string? dir = long.TryParse(game.AppId, out long appId) ? library.GetInstallDir(appId) : null;
        if (SteamService.ShowInExplorer(dir)) return;

        toast.Show(Resources.Strings.Fixes_Toast_GameNotFound,
            string.Format(Resources.Strings.Fixes_Toast_GameNotFound_Body, game.Name), error: true);
    }

    [RelayCommand]
    private async Task OpenGame(FixGameCardVm game)
    {
        SelectedGame = game;
        _ = game.EnsureCoverAsync(covers); // ensure the flyout header image is cached too
        Fixes.Clear();
        _allFixes = [];
        FixTags.Clear();
        SelectedFixTagId = null;
        IsLoadingFixes = true;
        try
        {
            var data = await api.GetDenuvoFixesAsync(game.AppId);
            if (data is not null)
            {
                _allFixes = data.Fixes.Select(f => new FixItemVm(f)).ToList();

                // Is the game on disk? GetInstallDir walks libraryfolders.vdf + appmanifest_*.acf, so
                // it's file I/O — off the UI thread. Resolved once here rather than per fix row.
                string? installDir = long.TryParse(game.AppId, out long gameAppId)
                    ? await Task.Run(() => library.GetInstallDir(gameAppId))
                    : null;
                foreach (var f in _allFixes)
                {
                    f.GameInstalled = installDir is not null;
                    // Whether this specific fix has been applied (revert manifest on disk).
                    f.IsApplied = installDir is not null
                        && File.Exists(ManifestJobFactory.GetFixManifestPath(installDir, f.Id));
                }

                // Build the per-game filter pills from the distinct tags across this game's fixes.
                // But only when there's more than one (a single tag is no filter).
                var distinct = _allFixes.SelectMany(f => f.Tags)
                    .GroupBy(t => t.Id).Select(g => g.First())
                    .OrderBy(t => t.Name).ToList();
                if (distinct.Count > 1)
                    foreach (var t in distinct) FixTags.Add(new TagPillVm(t));

                OnPropertyChanged(nameof(HasFixTags));
                ApplyFixFilter();
            }
        }
        catch { /* leave empty. Flyout shows "no fixes" */ }
        finally { IsLoadingFixes = false; }
    }

    [RelayCommand]
    private void SelectFixTag(string? tagId)
    {
        SelectedFixTagId = SelectedFixTagId == tagId ? null : tagId;
        foreach (var pill in FixTags) pill.IsSelected = pill.Id == SelectedFixTagId;
        ApplyFixFilter();
    }

    private void ApplyFixFilter()
    {
        IEnumerable<FixItemVm> shown = _allFixes;
        if (SelectedFixTagId is { } tag) shown = shown.Where(f => f.Tags.Any(t => t.Id == tag));
        Fixes.Clear();
        foreach (var f in shown) Fixes.Add(f);
    }

    [RelayCommand]
    private void CloseDetail() => SelectedGame = null;

    // ── Downloads ────────────────────────────────────────────────────

    [RelayCommand]
    private Task DownloadManifest(FixItemVm fix) => RunDownload(fix, "manifest");

    [RelayCommand]
    private Task DownloadFix(FixItemVm fix) => RunDownload(fix, "fix");

    /// <summary>Confirm, then revert an applied fix back to its original files.</summary>
    [RelayCommand]
    private void RevertFix(FixItemVm fix)
    {
        if (SelectedGame is not { } game) return;
        _pendingRevert = (fix, game);
        ConfirmRevertTitle = string.Format(Resources.Strings.Fixes_Revert_Confirm_Title, game.Name);
        ConfirmRevertBody = string.Format(Resources.Strings.Fixes_Revert_Confirm_Body, game.Name);
        IsConfirmingRevert = true;
    }

    [RelayCommand]
    private void CancelRevertConfirm()
    {
        IsConfirmingRevert = false;
        _pendingRevert = null;
    }

    [RelayCommand]
    private async Task ConfirmRevert()
    {
        IsConfirmingRevert = false;
        var pending = _pendingRevert;
        _pendingRevert = null;
        if (pending is not (var fix, var game)) return;
        if (!long.TryParse(game.AppId, out long appId)) return;

        var result = await Task.Run(() => jobs.RevertDenuvoFix(appId, fix.Id, game.Name));
        if (!result.Ok)
        {
            toast.Show(Resources.Strings.Fixes_Revert_Failed, result.Message ?? "", error: true);
            return;
        }

        fix.IsApplied = false;
    }

    private (FixItemVm Fix, FixGameCardVm Game)? _pendingRevert;
    [ObservableProperty] private bool _isConfirmingRevert;
    [ObservableProperty] private string _confirmRevertTitle = "";
    [ObservableProperty] private string _confirmRevertBody = "";

    /// <summary>
    /// Queue one slot of a fix. The download, install and result toast all happen in the shared queue,
    /// so this returns as soon as the item is enqueued.
    /// </summary>
    private async Task RunDownload(FixItemVm fix, string slot)
    {
        if (await PromptSignInIfGuestAsync(Resources.Strings.Fixes_SignIn)) return;
        if (SelectedGame is not { } game) return;
        if (!long.TryParse(game.AppId, out long appId)) return;

        // The Fix button is disabled for uninstalled games, but the flyout's snapshot can be stale by
        // now (and nothing stops a programmatic caller). Cheap local check, so do it before queueing
        // rather than after paying for a download.
        if (slot == "fix" && library.GetInstallDir(appId) is null)
        {
            toast.Show(Resources.Strings.Fixes_Toast_GameNotFound,
                string.Format(Resources.Strings.Fixes_Toast_GameNotFound_Body, game.Name), error: true);
            return;
        }

        string fallback = slot == "manifest"
            ? fix.ManifestFilename ?? $"{game.AppId}.zip"
            : fix.FixFilename ?? $"{game.AppId}_fix.zip";

        var job = jobs.CreateDenuvoJob(fix.Id, slot, fallback, appId, game.Name, fix.Title,
            onFinished: (item, result) =>
            {
                // The factory already toasts success and install failures. A download that never got
                // that far (network, auth, daily limit) still needs to say something.
                if (result is null && item.Status == DownloadStatus.Failed)
                {
                    toast.Show(Resources.Strings.Fixes_Toast_DownloadFailed,
                        item.Message ?? Resources.Strings.Fixes_Toast_DownloadFailed_Body, error: true);
                    return;
                }

                // A successfully applied fix unlocks the Revert button right away, without closing and
                // reopening the flyout.
                if (slot == "fix" && result?.Ok == true) fix.IsApplied = true;
            });

        var item = queue.Enqueue(job);
        if (slot == "manifest") fix.ManifestItem = item;
        else fix.FixItem = item;
    }

    private async Task<bool> PromptSignInIfGuestAsync(string message)
    {
        if (!auth.IsGuest) return false;
        toast.Show(Resources.Strings.Fixes_SignInRequired, message);
        if (RequestSignIn is not null) await RequestSignIn();
        return true;
    }
}
