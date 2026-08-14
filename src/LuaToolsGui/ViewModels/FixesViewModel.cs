using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaToolsGui.Models;
using LuaToolsGui.Services;

namespace LuaToolsGui.ViewModels;

/// <summary>The two sub-tabs on the Fixes page: the game grid (default) and the recently-added feed.</summary>
public enum FixesPage { Games, Recent }

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

    private static string FormatDate(string? iso) =>
        DateTimeOffset.TryParse(iso, out var d) ? d.UtcDateTime.ToString("d MMM yyyy") : "";
}

/// <summary>One entry in the Fixes page's "Recently added" feed (a fix, with its game + tags).</summary>
public partial class RecentFixVm(DenuvoRecentFix f) : ObservableObject
{
    public string Id { get; } = f.Id;
    public string AppId { get; } = f.AppId;
    public string Title { get; } = f.Title;
    public string? Description { get; } = f.Description;
    public IReadOnlyList<DenuvoTag> Tags { get; } = f.Tags;

    private readonly string? _headerImage = f.Game?.HeaderImage;
    private readonly string? _createdAt = f.CreatedAt;

    public string GameNameLabel =>
        string.IsNullOrWhiteSpace(f.Game?.Name) ? $"App ID: {AppId}" : f.Game!.Name;
    public string BuildLabel =>
        string.Format(Resources.Strings.Fixes_Build, string.IsNullOrWhiteSpace(Title) ? "—" : Title);
    public string DateLabel => FormatDate(_createdAt);
    public bool IsNew => TryParseDate(_createdAt, out var d) && d >= DateTime.UtcNow.AddDays(-3);

    /// <summary>Local cached cover path (set after CoverCache resolves it); bound via ImagePathToSource.</summary>
    [ObservableProperty] private string? _cover;
    private int _resolving;

    public async Task EnsureCoverAsync(CoverCache covers)
    {
        if (Cover is not null || string.IsNullOrWhiteSpace(_headerImage)) return;
        if (!long.TryParse(AppId, out long appid)) return;
        if (Interlocked.Exchange(ref _resolving, 1) == 1) return;
        try
        {
            string? local = covers.GetLocalPath(appid) ?? await covers.EnsureAsync(appid, _headerImage!);
            if (local is not null) Cover = local;
        }
        finally { Interlocked.Exchange(ref _resolving, 0); }
    }

    private static bool TryParseDate(string? iso, out DateTimeOffset d) => DateTimeOffset.TryParse(iso, out d);
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
    private readonly LuaInstaller installer;
    private readonly SteamService steam;
    private readonly SteamLibraryService library;
    private readonly CoverCache covers;
    private readonly ToastService toast;
    private readonly SettingsService settings;

    public FixesViewModel(
        LuaToolsApiClient api, AuthService auth, LuaInstaller installer, SteamService steam,
        SteamLibraryService library, CoverCache covers, ToastService toast, SettingsService settings)
    {
        this.api = api;
        this.auth = auth;
        this.installer = installer;
        this.steam = steam;
        this.library = library;
        this.covers = covers;
        this.toast = toast;
        this.settings = settings;
        InitPageSize(settings.FixesPageSize);
    }

    /// <summary>Set by App so a guest hitting a download is sent through the Discord sign-in flow.</summary>
    public Func<Task>? RequestSignIn { get; set; }

    // The master list; the displayed page slice lives in the base's Items collection.
    private List<FixGameCardVm> _allGames = [];

    public ObservableCollection<TagPillVm> Tags { get; } = [];

    // ── Sub-tabs: game grid (default) vs the recently-added fixes feed ──
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGamesMode))]
    [NotifyPropertyChangedFor(nameof(IsRecentMode))]
    private FixesPage _page = FixesPage.Games;

    public bool IsGamesMode => Page == FixesPage.Games;
    public bool IsRecentMode => Page == FixesPage.Recent;

    [RelayCommand]
    private void ShowGames() => Page = FixesPage.Games;

    [RelayCommand]
    private void ShowRecent()
    {
        Page = FixesPage.Recent;
        _ = LoadRecentAsync(); // usually a no-op: LoadAsync warms the feed at page load
    }

    public ObservableCollection<RecentFixVm> RecentFixes { get; } = [];
    [ObservableProperty] private bool _isLoadingRecent;
    [ObservableProperty] private string? _recentEmptyMessage;

    /// <summary>True once the "Recent" feed has at least one entry (gates the feed vs its empty/error message).</summary>
    public bool HasRecentItems => RecentFixes.Count > 0;

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

    // ── Download state (one at a time) ───────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotBusy))]
    private bool _isBusy;
    public bool NotBusy => !IsBusy;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isProgressIndeterminate;

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
            ApplyFilter();
            if (_allGames.Count == 0) EmptyMessage = Resources.Strings.Fixes_Empty_None;

            // Warm the "Recent" sub-tab in the background (one bounded PostgREST read, no auth).
            _ = LoadRecentAsync();
        }
        catch
        {
            EmptyMessage = Resources.Strings.Fixes_Err_Load;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Fetch the most recently-published fixes (feed for the "Recent" sub-tab). Loads once per
    /// session unless <paramref name="force"/> (the Refresh button) re-fetches.</summary>
    public async Task LoadRecentAsync(bool force = false)
    {
        if (!force && RecentFixes.Count > 0) return;
        IsLoadingRecent = true;
        try
        {
            var fixes = await api.GetRecentDenuvoFixesAsync(limit: 25);
            if (fixes is null)
            {
                RecentEmptyMessage = Resources.Strings.Fixes_Recent_Err_Load;
                return;
            }

            RecentFixes.Clear();
            foreach (var f in fixes) RecentFixes.Add(new RecentFixVm(f));
            foreach (var f in RecentFixes) _ = f.EnsureCoverAsync(covers);
            RecentEmptyMessage = RecentFixes.Count == 0 ? Resources.Strings.Fixes_Recent_Empty : null;
            OnPropertyChanged(nameof(HasRecentItems));
        }
        catch
        {
            RecentEmptyMessage = Resources.Strings.Fixes_Recent_Err_Load;
        }
        finally
        {
            IsLoadingRecent = false;
        }
    }

    [RelayCommand]
    private Task OpenRecentFix(RecentFixVm fix) =>
        long.TryParse(fix.AppId, out long appId) ? OpenForAppIdAsync(appId) : Task.CompletedTask;

    [RelayCommand]
    private Task Refresh() => RefreshWithCooldownAsync(async () =>
    {
        if (SearchText.Length > 0) SearchText = ""; // reset filter → full list visible
        if (SelectedTagId is not null) SelectTag(SelectedTagId); // clear active tag (toggles off)
        await LoadAsync(force: true);
        await LoadRecentAsync(force: true);
        toast.Show(Resources.Strings.Fixes_Toast_Refreshed_Title,
            string.Format(Resources.Strings.Fixes_Toast_Refreshed_Body, _allGames.Count));
    });

    [RelayCommand]
    private void SelectTag(string? tagId)
    {
        SelectedTagId = SelectedTagId == tagId ? null : tagId; // toggle off when re-clicked
        foreach (var pill in Tags) pill.IsSelected = pill.Id == SelectedTagId;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        string q = SearchText.Trim();
        IEnumerable<FixGameCardVm> shown = _allGames;
        if (SelectedTagId is { } tag) shown = shown.Where(g => g.TagIds.Contains(tag));
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

    private async Task RunDownload(FixItemVm fix, string slot)
    {
        if (IsBusy) return;
        if (await PromptSignInIfGuestAsync(Resources.Strings.Fixes_SignIn)) return;
        if (SelectedGame is not { } game) return;
        if (!long.TryParse(game.AppId, out long appId)) return;

        IsBusy = true;
        IsProgressIndeterminate = true;
        Progress = 0;
        try
        {
            string fallback = slot == "manifest"
                ? fix.ManifestFilename ?? $"{game.AppId}.zip"
                : fix.FixFilename ?? $"{game.AppId}_fix.zip";

            var prog = new Progress<double?>(p =>
            {
                IsProgressIndeterminate = p is null;
                if (p is not null) Progress = p.Value * 100;
            });

            var file = await api.DownloadDenuvoAsync(fix.Id, slot, fallback, prog);

            if (slot == "manifest")
                InstallManifest(file, appId, game.Name);
            else
                ApplyFix(file, appId, game.Name);
        }
        catch (ApiException ex)
        {
            toast.Show(Resources.Strings.Fixes_Toast_DownloadFailed, ex.Message, error: true);
        }
        catch (Exception)
        {
            toast.Show(Resources.Strings.Fixes_Toast_DownloadFailed, Resources.Strings.Fixes_Toast_DownloadFailed_Body, error: true);
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    /// <summary>Manifest slot: install into Steam force-LOCKED (Denuvo fixes must stay version-pinned).</summary>
    private void InstallManifest(DownloadedFile file, long appId, string gameName)
    {
        bool isZip = file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
        var result = isZip
            ? installer.InstallZip(file.FilePath, appId, forceLocked: true)
            : installer.InstallLuaFile(file.FilePath, appId, forceLocked: true);
        DeleteStaged(file.FilePath); // consumed by the install. Drop the temp staging copy

        if (result.AnyFailed)
        {
            toast.Show(Resources.Strings.Fixes_Toast_InstallFailed,
                result.Error ?? Resources.Strings.Fixes_Toast_InstallFailed_Body,
                error: true);
            return;
        }

        bool restarted = steam.RestartSteam();
        toast.Show(Resources.Strings.Fixes_Toast_FixInstalled, restarted
            ? string.Format(Resources.Strings.Fixes_Toast_FixInstalled_Restarting, gameName)
            : string.Format(Resources.Strings.Fixes_Toast_FixInstalled_Restart, gameName));
    }

    /// <summary>Fix slot: only applicable if the game is installed. Extract the zip into its folder.</summary>
    private void ApplyFix(DownloadedFile file, long appId, string gameName)
    {
        string? installDir = library.GetInstallDir(appId);
        if (installDir is null)
        {
            toast.Show(Resources.Strings.Fixes_Toast_GameNotFound, string.Format(Resources.Strings.Fixes_Toast_GameNotFound_Body, gameName), error: true);
            return;
        }

        try
        {
            // Extract into the game folder (overwrite existing files). Best-effort per entry.
            using var archive = ZipFile.OpenRead(file.FilePath);
            int failed = 0;
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue; // directory entry
                string dest = Path.Combine(installDir, entry.FullName);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    entry.ExtractToFile(dest, overwrite: true);
                }
                catch { failed++; }
            }

            if (failed > 0)
                toast.Show(Resources.Strings.Fixes_Toast_PartiallyApplied,
                    string.Format(Resources.Strings.Fixes_Toast_PartiallyApplied_Body, failed), error: true);
            else
                toast.Show(Resources.Strings.Fixes_Toast_FixApplied, string.Format(Resources.Strings.Fixes_Toast_FixApplied_Body, gameName));
        }
        catch (Exception ex)
        {
            toast.Show(Resources.Strings.Fixes_Toast_CouldntApply, ex.Message, error: true);
        }
        finally
        {
            DeleteStaged(file.FilePath); // archive is disposed by now. Drop the temp staging copy
        }
    }

    /// <summary>Best-effort delete of a staged download after it's been consumed by an install.</summary>
    private static void DeleteStaged(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    private async Task<bool> PromptSignInIfGuestAsync(string message)
    {
        if (!auth.IsGuest) return false;
        toast.Show(Resources.Strings.Fixes_SignInRequired, message);
        if (RequestSignIn is not null) await RequestSignIn();
        return true;
    }
}
