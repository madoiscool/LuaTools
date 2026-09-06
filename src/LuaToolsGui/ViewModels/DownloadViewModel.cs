using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaToolsGui.Models;
using LuaToolsGui.Services;
using LuaToolsGui.Services.Downloads;

namespace LuaToolsGui.ViewModels;

public partial class SourceRowViewModel : ObservableObject
{
    private readonly DownloadViewModel _parent;

    public string Name { get; }
    public string DisplayName { get; }
    public string Status { get; }
    public string? DiscordUrl { get; }
    public bool NeedsKey { get; }

    public bool IsAvailable => Status == "available";
    public string StatusLabel => Status.ToUpperInvariant();

    /// <summary>Hide the status badge when availability is unknown (e.g. a Hubcap row with no key set.
    /// We can't check, so we don't show a misleading badge; the "needs key" hint covers it instead).</summary>
    public bool ShowStatus => Status != "unknown";

    /// <summary>Hubcap key not configured. Show a hint instead of a download button.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    private bool _isLocked;

    [ObservableProperty] private string? _statsText;
    [ObservableProperty] private bool _isSupporter;

    /// <summary>The queue item for this row's in-flight download, if any. The row's progress bar binds
    /// straight through to it, so the queue stays the only owner of download state.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    private DownloadItem? _queueItem;

    // Guard against re-queuing from a double-click while the item is still live. The queue's DedupeKey
    // would collapse it anyway; this just keeps the button from looking clickable.
    public bool CanDownload => IsAvailable && !IsLocked && QueueItem?.IsActive != true;

    /// <param name="displayName">
    /// Overrides the source-meta table. A source declared by a pack is not in it and never will be —
    /// its label travels with the declaration.
    /// </param>
    public SourceRowViewModel(DownloadViewModel parent, string name, string status, string? displayName = null)
    {
        _parent = parent;
        Name = name;
        Status = status;
        var meta = SourceMeta.Get(name);
        DisplayName = displayName ?? meta.DisplayName ?? name;
        DiscordUrl = meta.DiscordUrl;
        NeedsKey = meta.RequiresUserKey;
    }

    [RelayCommand]
    private async Task DownloadAsync() => await _parent.DownloadFromSourceAsync(this);

    [RelayCommand]
    private void OpenDiscord()
    {
        if (DiscordUrl is not null)
            Process.Start(new ProcessStartInfo(DiscordUrl) { UseShellExecute = true });
    }
}

/// <summary>One row in the overwrite-confirm diff (a depot/DLC the new lua adds or removes).</summary>
public record DiffRow(string Title, string Meta, bool IsDlc, bool IsShared, string SteamDbUrl);

/// <summary>A featured-game card on the Add page (Steam top-sellers / new-releases strips). Clicking it
/// loads that appid's details just like picking a search result.</summary>
public record FeaturedItem(long AppId, string Name, string? Image);

public partial class DownloadViewModel : ObservableObject
{
    private readonly LuaToolsApiClient _api;
    private readonly HubcapService _hubcap;
    private readonly SettingsService _settings;
    private readonly AuthService _auth;
    private readonly ToastService _toast;
    private readonly LuaInstaller _installer;
    private readonly SteamAppListCache _appList;
    private readonly SteamAppInfoCache _appInfo;
    private readonly SteamDepotInfo _depotInfo;
    private readonly HardwareAppIdService _hardware;
    private readonly DownloadQueue _queue;
    private readonly ManifestJobFactory _jobs;
    private readonly Services.Sources.SourcePackRegistry _packs;
    private readonly Services.Sources.PackSourceService _packSources;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _detailsCts;

    // Per-confirm steamcmd lookup: depot/DLC id → its real depot info (name/size/os/lang).
    private IReadOnlyDictionary<long, ContentDepot> _depotsById = new Dictionary<long, ContentDepot>();

    /// <summary>Set by App so a guest hitting "Download" can be sent through the Discord sign-in flow.</summary>
    public Func<Task>? RequestSignIn { get; set; }

    /// <summary>Set by App: navigate to Manage and open this appid's detail (the install banner's "Reveal").</summary>
    public Action<long>? NavigateToGame { get; set; }

    public ObservableCollection<SteamSearchResult> SearchResults { get; } = [];
    public ObservableCollection<SourceRowViewModel> Sources { get; } = [];
    public ObservableCollection<DlcDepot> DlcDepots { get; } = [];

    // ── Featured strips (Steam top-sellers / new-releases), shown when the page is idle ──
    public ObservableCollection<FeaturedItem> TopSellers { get; } = [];
    public ObservableCollection<FeaturedItem> NewReleases { get; } = [];

    // Per-row visibility so an empty category collapses (raised in LoadFeaturedAsync).
    public bool HasTopSellers => TopSellers.Count > 0;
    public bool HasNewReleases => NewReleases.Count > 0;

    /// <summary>Show the featured strips only when the user hasn't started anything, no text typed, no
    /// results open, no game loaded, no install banner. Once they search or pick a game, the strips hide.</summary>
    public bool ShowFeatured =>
        (TopSellers.Count > 0 || NewReleases.Count > 0)
        && string.IsNullOrWhiteSpace(SearchText)
        && !HasDetails && !HasSources && !HasInstallResult;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFeatured))]
    private string _searchText = "";
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private bool _isResultsOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetails))]
    [NotifyPropertyChangedFor(nameof(ShowFeatured))]
    [NotifyCanExecuteChangedFor(nameof(FetchCommand))]
    private GameDetails? _details;

    public bool HasDetails => Details is not null;
    public string GenresText => Details is null ? "" : string.Join(", ", Details.Genres);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FetchCommand))]
    private bool _isChecking;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSources))]
    [NotifyPropertyChangedFor(nameof(ShowFeatured))]
    private bool _sourcesLoaded;

    public bool HasSources => SourcesLoaded && Sources.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDlcInfo))]
    [NotifyPropertyChangedFor(nameof(DlcComplete))]
    [NotifyPropertyChangedFor(nameof(CanGenerateDlc))]
    private DlcInfo? _dlcInfo;

    public bool HasDlcInfo => DlcInfo is not null;
    public bool DlcComplete => DlcInfo?.MissingCount == 0;
    // haveCount > 0 → keys exist; missingCount == 0 → addappid alone suffices (mirrors the website rule)
    public bool CanGenerateDlc => DlcInfo is not null && (DlcInfo.HaveCount > 0 || DlcInfo.MissingCount == 0);

    /// <summary>True while the DLC job is queued or running (drives the button's spinner/disabled state).</summary>
    public bool IsGenerating => DlcQueueItem?.IsActive == true;
    [ObservableProperty] private string? _error;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLastDownload))]
    private DownloadedFile? _lastDownload;

    public bool HasLastDownload => LastDownload is not null;

    /// <summary>Drag-and-drop installer shown on the Add page.</summary>
    public DropInstallViewModel Drop { get; }

    // Set while a luatools://install/silent/<id> install runs so the download path skips the interactive
    // overwrite-confirm overlay (there's no surfaced window for the user to confirm it on).
    private bool _silentInstall;

    /// <summary>
    /// Called by the protocol handler for luatools://install/&lt;appid&gt;. Seeds the app, fetches details,
    /// enables FastFetch, and auto-fires the download. When <paramref name="onComplete"/> is supplied
    /// (the silent variant) the whole fetch→download→install chain runs headless and the final outcome
    /// is reported back via the callback (message, isError) so the caller can pop a tray notification.
    /// </summary>
    public async Task ProtocolInstall(long appId, Action<string, bool>? onComplete = null)
    {
        _silentInstall = onComplete is not null;
        // Cleared so the completion await below can't latch onto a PREVIOUS protocol install's item and
        // report its stale outcome when this one enqueues nothing (no sources, or the fetch failed).
        _lastEnqueued = null;
        FastFetch = true;
        _suppressSearch = true;
        SearchText = appId.ToString();
        _suppressSearch = false;
        ResetResults();
        IsResultsOpen = false;

        try
        {
            Details = await _api.GetDetailsAsync(appId.ToString());
            OnPropertyChanged(nameof(GenresText));
        }
        catch
        {
            onComplete?.Invoke(Resources.Strings.Add_Err_Generic, true);
            _silentInstall = false;
            return;
        }

        if (HasDetails)
            await FetchCommand.ExecuteAsync(null);

        // The download is no longer inline: FetchAsync only ENQUEUES it. Wait for the queue item to
        // reach a terminal state before reporting, otherwise the caller's tray balloon fires early and
        // (on a cold silent launch) App's post-balloon shutdown timer can kill the app mid-download.
        if (onComplete is not null)
        {
            var item = _lastEnqueued;
            if (item is not null) await item.Completion;

            if (InstallStatus is not null)
                onComplete(InstallStatus, InstallFailed);
            else
                onComplete(Error ?? Resources.Strings.Add_Err_Generic, true);
            _silentInstall = false;
        }
    }

    // The most recent item this view model queued. ProtocolInstall awaits it so the silent-install
    // callback reflects the real outcome rather than "the download started".
    private DownloadItem? _lastEnqueued;

    // ── Steam-plugin headless add (reflected over HTTP; no window) ────
    /// <summary>Headless add driven by the Steam store plugin. Seeds the appid and runs the SAME
    /// FetchAsync pipeline the app UI uses (dynamic sources + Hubcap synth + key-gating + usage +
    /// FastFetch auto-download). The plugin polls state via the HTTP server and, when FastFetch is
    /// off, picks a source with <see cref="DownloadSourceByNameAsync"/>. Unlike ProtocolInstall this
    /// does NOT force FastFetch. It respects the user's setting so the plugin popup matches the app.</summary>
    public async Task StartPluginAddAsync(long appId)
    {
        _silentInstall = true; // headless: no surfaced window to confirm an overwrite on
        _suppressSearch = true;
        SearchText = appId.ToString();
        _suppressSearch = false;
        ResetResults();
        IsResultsOpen = false;
        InstallStatus = null;
        Error = null;
        try
        {
            Details = await _api.GetDetailsAsync(appId.ToString());
            OnPropertyChanged(nameof(GenresText));
        }
        catch
        {
            Error = Resources.Strings.Add_Err_Generic;
            return;
        }
        if (HasDetails) await FetchCommand.ExecuteAsync(null);
    }

    /// <summary>Plugin picked a source by name (FastFetch-off path). Download+install it headlessly.</summary>
    public Task DownloadSourceByNameAsync(string name)
    {
        _silentInstall = true;
        var row = Sources.FirstOrDefault(s =>
            string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(s.DisplayName, name, StringComparison.OrdinalIgnoreCase));
        return row is null ? Task.CompletedTask : DownloadFromSourceAsync(row);
    }


    // ── Install result banner ───────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInstallResult))]
    [NotifyPropertyChangedFor(nameof(ShowFeatured))]
    private string? _installStatus;

    [ObservableProperty] private bool _installFailed;
    public bool HasInstallResult => InstallStatus is not null;

    // The appid the banner's "Reveal" opens. Captured at install time so it survives the user clearing
    // the search box (which nulls Details) while the banner is still showing.
    private long? _installedAppId;

    // ── Overwrite confirm overlay (base game whose lua already exists) ──
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDiffAdded))]
    private List<DiffRow> _diffAdded = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDiffRemoved))]
    private List<DiffRow> _diffRemoved = [];

    [ObservableProperty] private bool _isConfirmingOverwrite;
    [ObservableProperty] private string _confirmTitle = "";
    [ObservableProperty] private string? _confirmNoChanges;

    public bool HasDiffAdded => DiffAdded.Count > 0;
    public bool HasDiffRemoved => DiffRemoved.Count > 0;

    // Set while an overwrite overlay is open; the confirm/cancel commands complete it, which unblocks
    // the queue job waiting in ConfirmOverwriteAsync.
    private TaskCompletionSource<bool>? _pendingConfirm;

    // Only one overlay can be shown at a time (there is a single set of overlay properties), so
    // simultaneous confirmations serialize here rather than overwriting each other's diff.
    private readonly SemaphoreSlim _confirmGate = new(1, 1);

    /// <summary>The in-flight DLC generate, so the DLC button can disable itself while it runs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGenerating))]
    private DownloadItem? _dlcQueueItem;

    private bool _suppressSearch;
    private string? _fastFetchSource;

    [ObservableProperty] private bool _fastFetch;
    partial void OnFastFetchChanged(bool value) => _settings.FastFetch = value;

    /// <summary>Re-sync the FastFetch toggle from the saved setting when the Add view appears. The Settings
    /// page exposes the same toggle, and both VMs are singletons that otherwise only read it at startup.</summary>
    public void SyncFastFetch() => FastFetch = _settings.FastFetch;

    public DownloadViewModel(LuaToolsApiClient api, HubcapService hubcap, SettingsService settings,
        AuthService auth, ToastService toast, LuaInstaller installer,
        SteamAppListCache appList, SteamAppInfoCache appInfo, SteamDepotInfo depotInfo,
        HardwareAppIdService hardware, DropInstallViewModel drop,
        DownloadQueue queue, ManifestJobFactory jobs,
        Services.Sources.SourcePackRegistry packs, Services.Sources.PackSourceService packSources)
    {
        _packs = packs;
        _packSources = packSources;
        _api = api;
        _hubcap = hubcap;
        _settings = settings;
        _auth = auth;
        _toast = toast;
        _installer = installer;
        _appList = appList;
        _appInfo = appInfo;
        _depotInfo = depotInfo;
        _hardware = hardware;
        _queue = queue;
        _jobs = jobs;
        Drop = drop;
        _fastFetch = settings.FastFetch;
    }

    /// <summary>
    /// Pre-fill from the Manage page "Update" action and fetch the app's details directly. The appid
    /// is authoritative here, so resolve it straight away. Don't route through search (bare numbers
    /// are now treated as title queries, not appids, so the text path would search instead of resolve).
    /// </summary>
    public void SeedSearch(long appId)
    {
        _suppressSearch = true;
        SearchText = appId.ToString();
        _suppressSearch = false;

        ResetResults();
        Details = null;
        IsResultsOpen = false;
        _ = FetchDetailsDebouncedAsync(appId.ToString());
    }

    /// <summary>Guests can browse but must sign in for gated actions. Returns true if a sign-in was triggered.</summary>
    private async Task<bool> PromptSignInIfGuestAsync(string message)
    {
        if (!_auth.IsGuest) return false;
        Error = message;
        _toast.Show(Resources.Strings.Fixes_SignInRequired, message);
        if (RequestSignIn is not null) await RequestSignIn();
        return true;
    }

    // ── Search ──────────────────────────────────────────────────────

    partial void OnSearchTextChanged(string value)
    {
        if (_suppressSearch) return;

        ResetResults();
        Details = null;

        // Cleared the box → back to the idle/featured state: dismiss the leftover install banner
        // (ResetResults already cleared Error). Otherwise HasInstallResult keeps the featured strips hidden.
        if (string.IsNullOrWhiteSpace(value))
            InstallStatus = null;

        string? appid = ExtractAppId(value);
        if (appid is not null)
        {
            IsResultsOpen = false;
            _ = FetchDetailsDebouncedAsync(appid);
            return;
        }

        _ = SearchDebouncedAsync(value);
    }

    private async Task SearchDebouncedAsync(string query)
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(350, cts.Token);
            if (string.IsNullOrWhiteSpace(query) || query.Length > 100)
            {
                SearchResults.Clear();
                IsResultsOpen = false;
                return;
            }

            IsSearching = true;
            var results = await _api.SearchAsync(query.Trim(), cts.Token);
            if (cts.Token.IsCancellationRequested) return;

            SearchResults.Clear();
            foreach (var r in results)
                if (!_hardware.IsBlacklisted(r.AppId)) SearchResults.Add(r); // hide Steam hardware
            IsResultsOpen = SearchResults.Count > 0;
        }
        catch (OperationCanceledException) { /* superseded by newer input */ }
        catch (ApiException ex) { Error = ex.Message; }
        catch { /* network hiccup during typing. Ignore */ }
        finally
        {
            if (_searchCts == cts) IsSearching = false;
        }
    }

    private async Task FetchDetailsDebouncedAsync(string appid)
    {
        _detailsCts?.Cancel();
        var cts = _detailsCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(400, cts.Token);
            Details = await _api.GetDetailsAsync(appid, cts.Token);
            OnPropertyChanged(nameof(GenresText));
        }
        catch (OperationCanceledException) { }
        catch { Details = null; }
    }

    [RelayCommand]
    private async Task SelectResultAsync(SteamSearchResult result)
    {
        _suppressSearch = true;
        SearchText = result.Name;
        _suppressSearch = false;

        IsResultsOpen = false;
        ResetResults();

        _detailsCts?.Cancel();
        var cts = _detailsCts = new CancellationTokenSource();
        try
        {
            Details = await _api.GetDetailsAsync(result.AppId.ToString(), cts.Token);
            OnPropertyChanged(nameof(GenresText));
        }
        catch (OperationCanceledException) { }
        catch { Details = null; }
    }

    /// <summary>Click a featured-strip card → load that game's details (same path as a search result).</summary>
    [RelayCommand]
    private async Task SelectFeaturedAsync(FeaturedItem item)
    {
        if (item is null) return;
        _suppressSearch = true;
        SearchText = item.Name;
        _suppressSearch = false;

        IsResultsOpen = false;
        ResetResults();

        _detailsCts?.Cancel();
        var cts = _detailsCts = new CancellationTokenSource();
        try
        {
            Details = await _api.GetDetailsAsync(item.AppId.ToString(), cts.Token);
            OnPropertyChanged(nameof(GenresText));
        }
        catch (OperationCanceledException) { }
        catch { Details = null; }
    }

    /// <summary>Fetch the Steam featured strips once (top sellers + new releases). Best-effort: on failure
    /// the collections stay empty and the strips simply don't render. Steam hardware (Deck, Index, …) is
    /// filtered out via the hardware blacklist.</summary>
    public async Task LoadFeaturedAsync()
    {
        if (TopSellers.Count > 0 || NewReleases.Count > 0) return; // already loaded
        await _hardware.EnsureFreshAsync(); // make sure the blacklist is current before filtering
        var (top, fresh) = await _api.GetFeaturedAsync();
        foreach (var i in top)
            if (!_hardware.IsBlacklisted(i.Id)) TopSellers.Add(new FeaturedItem(i.Id, i.Name, i.LargeCapsuleImage));
        foreach (var i in fresh)
            if (!_hardware.IsBlacklisted(i.Id)) NewReleases.Add(new FeaturedItem(i.Id, i.Name, i.LargeCapsuleImage));
        OnPropertyChanged(nameof(HasTopSellers));
        OnPropertyChanged(nameof(HasNewReleases));
        OnPropertyChanged(nameof(ShowFeatured));
    }

    [RelayCommand]
    private void CloseResults() => IsResultsOpen = false;

    // ── Fetch (sources or DLC info, depending on app type) ─────────

    private bool CanFetch() => HasDetails && !IsChecking;

    [RelayCommand(CanExecute = nameof(CanFetch))]
    private async Task FetchAsync()
    {
        if (Details is null) return;
        // DLC info is login-only; manifest source checking is public (guests allowed).
        if (Details.IsDlc && await PromptSignInIfGuestAsync(Resources.Strings.Add_SignIn_Dlc)) return;
        ResetResults();
        IsChecking = true;
        try
        {
            if (Details.IsDlc)
            {
                if (Details.BaseAppId is null)
                {
                    Error = Resources.Strings.Add_Err_BaseGame;
                    return;
                }
                DlcInfo = await _api.GetDlcInfoAsync(Details.AppId.ToString(), Details.BaseAppId);
                DlcDepots.Clear();
                // Included depots first, then missing: mirrors the website ordering
                foreach (var d in DlcInfo?.Depots.OrderByDescending(d => d.Included) ?? Enumerable.Empty<DlcDepot>())
                    DlcDepots.Add(d);
            }
            else
            {
                var statuses = await _api.CheckSourcesAsync(Details.AppId.ToString());

                // The manifest backend no longer reports the Hubcap/Morrenus source. Synthesize it
                // ourselves from a direct Hubcap status check (or show it locked if no key is set).
                await AddHubcapSourceAsync(statuses, Details.AppId.ToString());

                // Premium (key-gated) sources first, like the website
                foreach (var (name, status) in statuses.OrderByDescending(kv => SourceMeta.Get(kv.Key).RequiresUserKey ? 1 : 0))
                    Sources.Add(new SourceRowViewModel(this, name, status));

                await AddPackSourcesAsync(Details.AppId);

                await ApplyHubcapStateAsync();

                if (FastFetch)
                {
                    var best = Sources.FirstOrDefault(s => s.CanDownload);
                    if (best is null)
                    {
                        Error = Resources.Strings.Add_FastFetch_NoSource;
                        return;
                    }
                    _fastFetchSource = best.DisplayName;
                    await DownloadFromSourceAsync(best);
                }
                else
                {
                    SourcesLoaded = true;
                    await RefreshStandardUsageAsync();
                }
            }
        }
        catch (ApiException ex)
        {
            Error = ex.Message;
        }
        catch (Exception)
        {
            Error = Resources.Strings.Add_Err_Generic;
        }
        finally
        {
            IsChecking = false;
        }
    }

    /// <summary>The Hubcap source name as the website/source-meta keys it.</summary>
    private const string HubcapSourceName = "Sadie (Morrenus)";

    /// <summary>
    /// Inject the Hubcap row into the source-status map. The manifest backend no longer returns it, so
    /// availability comes from a direct Hubcap status check using the user's own key. With no key we still
    /// surface the row (as "available") so it shows up locked with the "needs a key" hint.
    /// </summary>
    private async Task AddHubcapSourceAsync(Dictionary<string, string> statuses, string appid)
    {
        string? key = _settings.HubcapApiKey;
        if (string.IsNullOrEmpty(key))
        {
            // No key → we genuinely can't check (the status endpoint needs the key). Mark it "unknown"
            // so the row shows up locked with the "needs a key" hint and NO misleading availability badge.
            statuses[HubcapSourceName] = "unknown";
            return;
        }

        var status = await _hubcap.CheckStatusAsync(key, appid);
        statuses[HubcapSourceName] = status?.ManifestFileExists == true ? "available" : "unavailable";
    }

    private async Task ApplyHubcapStateAsync()
    {
        var keyRows = Sources.Where(s => s.NeedsKey).ToList();
        if (keyRows.Count == 0) return;

        // No key configured → lock the premium rows (the row shows the "needs Hubcap key" hint).
        string? key = _settings.HubcapApiKey;
        if (string.IsNullOrEmpty(key))
        {
            foreach (var row in keyRows) row.IsLocked = true;
            return;
        }

        var stats = await _hubcap.GetStatsAsync(key);
        foreach (var row in keyRows)
        {
            row.IsLocked = stats?.CanMakeRequests != true;
            if (stats is not null)
                row.StatsText = $"{stats.DailyUsage}/{stats.DailyLimit}";
        }
    }

    /// <summary>
    /// Refresh the standard "X/25" daily usage badge on every non-Hubcap source row (those count
    /// toward the lua.tools daily limit; Hubcap sources show their own X/800 instead). Signed-in only.
    /// </summary>
    public async Task RefreshStandardUsageAsync()
    {
        var standardRows = Sources.Where(s => !s.NeedsKey).ToList();
        if (standardRows.Count == 0) return;
        if (_auth.IsGuest) return;

        var usageTask = _api.GetStandardUsageAsync();
        var supporterTask = _api.GetSupporterStatusAsync();
        await Task.WhenAll(usageTask, supporterTask);

        var usage = usageTask.Result;
        bool isSupporter = supporterTask.Result?.IsSupporter == true;

        foreach (var row in standardRows)
        {
            row.IsSupporter = isSupporter;
            if (usage is null) continue;
            row.StatsText = isSupporter
                ? $"{usage.Used} / {Resources.Strings.Add_Unlimited}"
                : $"{usage.Used}/{usage.Limit}";
        }
    }

    // ── Downloads ───────────────────────────────────────────────────

    /// <summary>
    /// Base-game manifest zip. Builds a job and hands it to the shared queue; the download, the
    /// overwrite confirmation and the install all happen there.
    /// </summary>
    /// <remarks>
    /// Returns as soon as the item is queued, so several games can be added back to back. The old
    /// "one at a time" behaviour came from a <c>Sources.Any(s =&gt; s.IsDownloading)</c> gate; duplicate
    /// suppression is now the queue's DedupeKey, and the queue's concurrency cap (default 1) decides
    /// how many actually run at once.
    /// </remarks>
    public async Task<DownloadItem?> DownloadFromSourceAsync(SourceRowViewModel source)
    {
        if (Details is null) return null;

        // Hubcap downloads use the user's OWN key and never touch lua.tools, so a guest with a key
        // configured can download without signing in. Every other source still needs a lua.tools account.
        // A pack source is fetched straight from the url its file names and never touches lua.tools, so
        // a lua.tools account has no bearing on it. Resolved before the gate for that reason.
        var packSource = _packs.Sources.FirstOrDefault(x =>
            string.Equals(x.Name, source.Name, StringComparison.OrdinalIgnoreCase));

        bool hubcapWithKey = source.NeedsKey && !string.IsNullOrEmpty(_settings.HubcapApiKey);
        if (!hubcapWithKey && packSource is null
            && await PromptSignInIfGuestAsync(Resources.Strings.Add_SignIn_Download)) return null;

        Error = null;
        LastDownload = null;
        InstallStatus = null;

        // Captured now, not read from Details later: once the download is backgrounded the user can load
        // a different game before the confirmation appears, and the dialog must still name THIS one.
        long appId = Details.AppId;
        string gameName = Details.Name;
        bool needsKey = source.NeedsKey;

        // Silent/headless installs have no surfaced window to confirm on, so they skip the gate.
        Func<DownloadedFile, DownloadItem, CancellationToken, Task<bool>>? confirm =
            _silentInstall ? null : (file, _, ct) => ConfirmOverwriteAsync(file, appId, gameName, ct);

        var job = packSource is not null
            ? _jobs.CreatePackSourceJob(packSource, appId, gameName,
                confirm: confirm,
                onFinished: (item, result) => OnManifestFinished(item, result, needsKey: false),
                onReveal: () => NavigateToGame?.Invoke(appId))
            : _jobs.CreateManifestJob(
                appId, gameName, source.Name, needsKey,
                confirm: confirm,
                onFinished: (item, result) => OnManifestFinished(item, result, needsKey),
                onReveal: () => NavigateToGame?.Invoke(appId));

        var queued = _queue.Enqueue(job);
        source.QueueItem = queued;
        _lastEnqueued = queued;
        return queued;
    }

    /// <summary>
    /// Append a row for every pack-declared source that has this game.
    /// </summary>
    /// <remarks>
    /// Appended after the app's own sources rather than ranked among them: their order is a decision
    /// this app makes, and a file the user dropped in a folder should not be able to overturn it.
    /// Probed in parallel, because the count is whatever the user installed and doing them in sequence
    /// would put a pack's latency on the critical path of every fetch. A probe that fails means "this
    /// source doesn't have it", never an error.
    /// </remarks>
    private async Task AddPackSourcesAsync(long appId)
    {
        _packs.Reload(SourceMeta.All.Keys.ToList());

        var sources = _packs.Sources;
        if (sources.Count == 0) return;

        var probes = sources.Select(src => (Source: src, Has: SafeHasAsync(src, appId))).ToList();
        await Task.WhenAll(probes.Select(p => p.Has));

        foreach (var (src, has) in probes)
        {
            if (!has.Result) continue;
            Sources.Add(new SourceRowViewModel(this, src.Name, "available", src.DisplayName)
            {
                StatsText = src.Badge,
            });
        }
    }

    /// <summary>A HasGameAsync that never throws: a failed or offline lookup just means "not covered".</summary>
    private async Task<bool> SafeHasAsync(Services.Sources.PackSource src, long appId)
    {
        try { return await _packSources.HasGameAsync(src, appId); }
        catch { return false; }
    }

    /// <summary>DLC lua: download and install silently (it's just an unlock, no confirm).</summary>
    [RelayCommand]
    private async Task GenerateDlcAsync()
    {
        if (Details?.BaseAppId is null) return;
        if (await PromptSignInIfGuestAsync(Resources.Strings.Add_SignIn_Download)) return;

        Error = null;
        LastDownload = null;
        InstallStatus = null;

        long appId = Details.AppId;
        var job = _jobs.CreateDlcJob(
            appId, Details.BaseAppId, Details.Name,
            onFinished: (item, result) => OnManifestFinished(item, result, needsKey: false),
            onReveal: () => NavigateToGame?.Invoke(appId));

        DlcQueueItem = _lastEnqueued = _queue.Enqueue(job);
    }

    /// <summary>
    /// Terminal callback for a manifest/DLC job: drive the install banner and refresh the usage badge.
    /// Runs on the dispatcher.
    /// </summary>
    private void OnManifestFinished(DownloadItem item, JobResult? result, bool needsKey)
    {
        _installedAppId = item.AppId; // the banner's "Reveal" target, even after the search is cleared

        if (result is null)
        {
            // Cancelled, or failed before the install phase. item.Message already holds the reason.
            if (item.Status == DownloadStatus.Failed) Error = item.Message;
            else { InstallStatus = item.Message; InstallFailed = false; }
        }
        else
        {
            InstallFailed = !result.Ok;
            InstallStatus = result.Message;
            if (result.Ok && _fastFetchSource is not null)
            {
                InstallStatus += " " + string.Format(Resources.Strings.Add_FastFetch_Via, _fastFetchSource);
                _fastFetchSource = null;
            }
        }

        // Usage just changed: Hubcap against the key's own quota, everything else against the 25/day.
        _ = needsKey ? ApplyHubcapStateAsync() : RefreshStandardUsageAsync();
    }

    // ── Install + overwrite confirm ─────────────────────────────────

    /// <summary>
    /// The queue's confirmation gate for a manifest whose lua is already installed: show the
    /// before/after diff overlay and block until the user answers. True installs, false discards.
    /// </summary>
    /// <remarks>
    /// <para>Called by <see cref="DownloadQueue"/> from a background thread, so everything here marshals
    /// to the dispatcher. While this is awaited the item has already released its concurrency slot, so
    /// an overlay the user ignores delays only its own install.</para>
    ///
    /// <para><paramref name="appId"/> and <paramref name="gameName"/> are passed in rather than read
    /// from <c>Details</c>: the user can load a different game while this download is in flight, and the
    /// overlay must describe the game that was actually downloaded.</para>
    ///
    /// <para>Only one overlay can be open at a time (there is a single set of overlay properties), so
    /// concurrent confirmations queue behind <c>_confirmGate</c>.</para>
    /// </remarks>
    private async Task<bool> ConfirmOverwriteAsync(
        DownloadedFile file, long appId, string gameName, CancellationToken ct)
    {
        // No existing lua → nothing to confirm against, install straight away.
        string? existing = _installer.ReadInstalledLua(appId);
        if (existing is null) return true;

        await _confirmGate.WaitAsync(ct);
        try
        {
            var oldLua = LuaFileParser.Parse(existing, appId);
            var newLua = ExtractLuaFromZip(file.FilePath, appId);
            if (newLua is null) return true; // can't diff (bare lua / unreadable zip) → just install

            var diff = LuaFileParser.Diff(oldLua, newLua);
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            await App.Current.Dispatcher.InvokeAsync(async () =>
            {
                // Names from caches + one steamcmd call (cached per app) → real names, sizes, OS, language.
                await _appList.EnsureLoadedAsync();
                _depotsById = await BuildDepotLookupAsync(appId);
                DiffAdded = diff.Added.Select(ToDiffRow).ToList();
                DiffRemoved = diff.Removed.Select(ToDiffRow).ToList();
                ConfirmTitle = string.Format(Resources.Strings.Add_Confirm_Replace, gameName);
                ConfirmNoChanges = diff.HasChanges ? null : Resources.Strings.Add_Confirm_NoChanges;
                _pendingConfirm = tcs;
                IsConfirmingOverwrite = true;

                // Lazily resolve any DLC rows still showing a bare id via appdetails, then rebuild.
                var unnamed = diff.Added.Concat(diff.Removed)
                    .Where(e => DiffDisplayName(e) is null)
                    .Select(e => e.Id).Distinct().ToList();
                if (unnamed.Count > 0)
                {
                    await Parallel.ForEachAsync(unnamed, new ParallelOptions { MaxDegreeOfParallelism = 4 },
                        async (id, _) => await _appInfo.ResolveAsync(id));
                    if (IsConfirmingOverwrite && ReferenceEquals(_pendingConfirm, tcs))
                    {
                        DiffAdded = diff.Added.Select(ToDiffRow).ToList();
                        DiffRemoved = diff.Removed.Select(ToDiffRow).ToList();
                    }
                }
            });

            // Cancelling the download while the overlay is open closes it and declines.
            await using var reg = ct.Register(() => tcs.TrySetResult(false));
            bool answer = await tcs.Task;

            await App.Current.Dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(_pendingConfirm, tcs))
                {
                    _pendingConfirm = null;
                    IsConfirmingOverwrite = false;
                }
            });
            return answer;
        }
        finally
        {
            _confirmGate.Release();
        }
    }

    [RelayCommand]
    private void ConfirmOverwrite()
    {
        IsConfirmingOverwrite = false;
        _pendingConfirm?.TrySetResult(true);
    }

    [RelayCommand]
    private void CancelOverwrite()
    {
        IsConfirmingOverwrite = false;
        _pendingConfirm?.TrySetResult(false);
    }

    /// <summary>Fetch the app's depots from steamcmd (cached) and index by depot id + dlcappid.</summary>
    private async Task<IReadOnlyDictionary<long, ContentDepot>> BuildDepotLookupAsync(long appId)
    {
        var map = new Dictionary<long, ContentDepot>();
        var info = await _depotInfo.GetAsync(appId);
        if (info is null) return map;
        foreach (var d in info.Depots)
        {
            map[d.Id] = d;
            if (d.DlcAppId is { } dlc) map[dlc] = d;
        }
        return map;
    }

    /// <summary>Real Steam name for a lua entry (DLC name from caches, or lua comment); null if unknown.</summary>
    private string? DiffDisplayName(LuaEntry e)
    {
        var d = _depotsById.GetValueOrDefault(e.Id);
        if (d?.DlcAppId is { } dlc)
        {
            string? steamName = _appList.GetName(dlc) ?? _appInfo.GetCached(dlc)?.Name;
            if (steamName is not null) return steamName;
        }
        return _appList.GetName(e.Id) ?? _appInfo.GetCached(e.Id)?.Name ?? e.Comment;
    }

    /// <summary>Enriched diff row: real name + "id · size · OS · lang" + DLC/SHARED chip + SteamDB link.</summary>
    private DiffRow ToDiffRow(LuaEntry e)
    {
        var d = _depotsById.GetValueOrDefault(e.Id);
        bool isDlc = d?.IsDlc ?? false;
        bool isShared = d?.IsShared ?? false;

        string title = DiffDisplayName(e)
            ?? (isDlc ? string.Format(Resources.Strings.Manage_DlcName, d!.DlcAppId)
                : isShared ? Resources.Strings.Manage_SharedDepot
                : e.HasKey ? Resources.Strings.Manage_Depot
                : string.Format(Resources.Strings.Manage_DlcName, e.Id));

        var meta = new List<string> { e.Id.ToString() };
        if (d is { Size: > 0 }) meta.Add(FormatSize(d.Size));
        if (!string.IsNullOrWhiteSpace(d?.Os)) meta.Add(PrettyOs(d!.Os!));
        if (!string.IsNullOrWhiteSpace(d?.Language)) meta.Add(d!.Language!);

        string url = d?.DlcAppId is { } dlcId
            ? $"https://steamdb.info/app/{dlcId}/"
            : $"https://steamdb.info/depot/{e.Id}/";

        return new DiffRow(title, string.Join("  ·  ", meta), isDlc, isShared, url);
    }

    [RelayCommand]
    private static void OpenSteamDb(DiffRow row) => SteamService.OpenUrl(row.SteamDbUrl);

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "";
        double gb = bytes / 1024d / 1024d / 1024d;
        if (gb >= 1) return $"{gb:0.##} GB";
        return $"{bytes / 1024d / 1024d:0.#} MB";
    }

    private static string PrettyOs(string os) => os switch
    {
        "windows" => "Windows",
        "macos" or "macosx" => "macOS",
        "linux" => "Linux",
        _ => os
    };

    /// <summary>Extract the .lua from a manifest zip and parse it (without installing). A bare-lua
    /// download (no zip wrapper) is parsed directly so the overwrite-diff still works for those sources.</summary>
    private static LuaContents? ExtractLuaFromZip(string zipPath, long appId)
    {
        try
        {
            // bare .lua → parse as-is. Same byte sniff the install path uses.
            if (!ManifestJobFactory.IsZip(zipPath)) return LuaFileParser.Parse(zipPath, appId);

            using var archive = System.IO.Compression.ZipFile.OpenRead(zipPath);
            var luaEntry = archive.Entries.FirstOrDefault(e =>
                e.Name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase));
            if (luaEntry is null) return null;

            string tmp = Path.Combine(Path.GetTempPath(), $"luatools_diff_{appId}_{Guid.NewGuid():N}.lua");
            luaEntry.ExtractToFile(tmp, overwrite: true);
            try { return LuaFileParser.Parse(tmp, appId); }
            finally { try { File.Delete(tmp); } catch { /* temp cleanup best-effort */ } }
        }
        catch { return null; }
    }

    /// <summary>The install banner's "Reveal" → open this game in the Manage detail view.</summary>
    [RelayCommand]
    private void RevealInstalled()
    {
        if ((_installedAppId ?? Details?.AppId) is { } appId) NavigateToGame?.Invoke(appId);
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private void ResetResults()
    {
        Sources.Clear();
        SourcesLoaded = false;
        DlcInfo = null;
        DlcDepots.Clear();
        Error = null;
        LastDownload = null;
        _fastFetchSource = null;
    }

    /// <summary>
    /// Extract an appid from a Steam/SteamDB store URL (…/app/&lt;id&gt;), or from a bare number of 5+
    /// digits. Short numbers go through normal title search instead. Game titles like "007", "500"
    /// or "1942" are numbers too. Tradeoff: very old games with short appids (e.g. 500 = Left 4 Dead)
    /// won't auto-resolve from a bare number and must be searched or pasted as a URL.
    /// </summary>
    private static string? ExtractAppId(string input)
    {
        string trimmed = input.Trim();
        if (Regex.IsMatch(trimmed, @"^\d{5,}$")) return trimmed;
        var m = Regex.Match(trimmed, @"/app/(\d+)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }
}
