using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaToolsGui.Models;
using LuaToolsGui.Services;

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
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isProgressIndeterminate;

    public bool CanDownload => IsAvailable && !IsLocked;

    public SourceRowViewModel(DownloadViewModel parent, string name, string status)
    {
        _parent = parent;
        Name = name;
        Status = status;
        var meta = SourceMeta.Get(name);
        DisplayName = meta.DisplayName ?? name;
        DiscordUrl = meta.DiscordUrl;
        NeedsKey = meta.RequiresUserKey;
    }

    [RelayCommand]
    private Task DownloadAsync() => _parent.DownloadFromSourceAsync(this);

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
    private readonly FixLookupService _fixes;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _detailsCts;

    // Per-confirm steamcmd lookup: depot/DLC id → its real depot info (name/size/os/lang).
    private IReadOnlyDictionary<long, ContentDepot> _depotsById = new Dictionary<long, ContentDepot>();

    /// <summary>Set by App so a guest hitting "Download" can be sent through the Discord sign-in flow.</summary>
    public Func<Task>? RequestSignIn { get; set; }

    /// <summary>Set by App: navigate to Manage and open this appid's detail (the install banner's "Reveal").</summary>
    public Action<long>? NavigateToGame { get; set; }

    /// <summary>Set by App: navigate to Fixes and open this appid's fixes (the post-fetch banner).</summary>
    public Action<long>? OpenFixesForGame { get; set; }

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

    /// <summary>
    /// Number of published fixes for the fetched game, filled in by <see cref="CheckFixesAsync"/> after
    /// a Fetch. 0 hides the banner, which is also what a failed lookup leaves behind.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFix))]
    [NotifyPropertyChangedFor(nameof(FixBannerText))]
    private int _fixCount;

    /// <summary>What kind of fixes they are ("Online Fix", "Ubisoft", …), straight from the listing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FixBannerText))]
    private string _fixTags = "";

    public bool HasFix => FixCount > 0;

    /// <summary>
    /// The banner sentence. The kinds are named when the listing gives them, because "a fix" says very
    /// little: the same listing covers Online Fixes, Ubisoft and Rockstar fixes, achievement fixes and
    /// more, and telling the user which one is waiting is the whole point of the banner.
    /// </summary>
    public string FixBannerText => FixTags.Length > 0
        ? string.Format(Resources.Strings.Add_Fix_Available_Tags, FixCount, FixTags)
        : string.Format(Resources.Strings.Add_Fix_Available, FixCount);

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

    [ObservableProperty] private bool _isGenerating;
    [ObservableProperty] private double _generateProgress;
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

        // In FastFetch the chain (fetch → download → install) completes inline by the time we get here,
        // and the overwrite overlay is skipped in silent mode, so the outcome is fully settled. Report
        // InstallStatus on success/handled-failure, else whatever Error the chain left behind.
        if (onComplete is not null)
        {
            if (InstallStatus is not null)
                onComplete(InstallStatus, InstallFailed);
            else
                onComplete(Error ?? Resources.Strings.Add_Err_Generic, true);
            _silentInstall = false;
        }
    }

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

    // Set while a confirm is pending so the confirm/cancel commands know what to install.
    private (string zipPath, long appId)? _pendingInstall;

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
        HardwareAppIdService hardware, FixLookupService fixes, DropInstallViewModel drop)
    {
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
        _fixes = fixes;
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
                // Independent of the source check, and deliberately not awaited: whether the game has a
                // Denuvo fix has no bearing on fetching its manifest sources, and the banner showing a
                // moment later is better than holding up the fetch.
                _ = CheckFixesAsync(Details.AppId);

                var statuses = await _api.CheckSourcesAsync(Details.AppId.ToString());

                // The manifest backend no longer reports the Hubcap/Morrenus source. Synthesize it
                // ourselves from a direct Hubcap status check (or show it locked if no key is set).
                await AddHubcapSourceAsync(statuses, Details.AppId.ToString());

                // Premium (key-gated) sources first, like the website
                foreach (var (name, status) in statuses.OrderByDescending(kv => SourceMeta.Get(kv.Key).RequiresUserKey ? 1 : 0))
                    Sources.Add(new SourceRowViewModel(this, name, status));

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

    /// <summary>
    /// Look up how many Denuvo fixes exist for the game that was just fetched, and surface the banner.
    ///
    /// <para>
    /// The result is dropped if the user has moved on to another game in the meantime: the lookup can
    /// outlive the fetch that started it, and a banner advertising fixes for the previous game would be
    /// worse than no banner at all.
    /// </para>
    /// </summary>
    private async Task CheckFixesAsync(long appId)
    {
        try
        {
            var summary = await _fixes.GetFixSummaryAsync(appId);
            if (Details?.AppId != appId) return; // user moved on. Drop it

            FixCount = summary?.Count ?? 0;
            FixTags = summary is null ? "" : string.Join(", ", summary.Tags);
        }
        catch
        {
            // Offline / API down. The banner just doesn't appear.
        }
    }

    /// <summary>Banner action: jump to the Fixes page with this game already open.</summary>
    [RelayCommand]
    private void OpenFixes()
    {
        if (Details is { } details) OpenFixesForGame?.Invoke(details.AppId);
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

    /// <summary>Base-game manifest zip: download, then install (confirming first if a lua already exists).</summary>
    public async Task DownloadFromSourceAsync(SourceRowViewModel source)
    {
        if (Details is null || Sources.Any(s => s.IsDownloading)) return;

        // Hubcap downloads use the user's OWN key and never touch lua.tools, so a guest with a key
        // configured can download without signing in. Every other source still needs a lua.tools account.
        bool hubcapWithKey = source.NeedsKey && !string.IsNullOrEmpty(_settings.HubcapApiKey);
        if (!hubcapWithKey && await PromptSignInIfGuestAsync(Resources.Strings.Add_SignIn_Download)) return;
        Error = null;
        LastDownload = null;
        InstallStatus = null;
        long appId = Details.AppId;
        source.IsDownloading = true;
        source.IsProgressIndeterminate = true;
        try
        {
            var progress = new Progress<double?>(p =>
            {
                source.IsProgressIndeterminate = p is null;
                if (p is not null) source.Progress = p.Value * 100;
            });
            // Key-gated (Hubcap) sources download DIRECTLY from hubcapmanifest.com with the user's key;
            // everything else goes through lua.tools and counts toward the 25/day limit.
            DownloadedFile download;
            if (source.NeedsKey)
            {
                download = await _hubcap.DownloadManifestAsync(
                    appId.ToString(), _settings.HubcapApiKey ?? "", progress);
            }
            else
            {
                download = await _api.DownloadManifestAsync(
                    appId.ToString(), source.Name, Details.Name, progress);
            }
            LastDownload = download;

            if (source.NeedsKey) await ApplyHubcapStateAsync(); // refresh the key's X/Y usage badge
            else await RefreshStandardUsageAsync(); // non-Hubcap usage just changed (counts toward 25/day)

            // If a lua for this game is already installed, confirm with a before/after diff first, unless
            // this is a silent install, which has no surfaced window and just overwrites.
            string? existing = _installer.ReadInstalledLua(appId);
            if (!_silentInstall && existing is not null && await ShowOverwriteConfirmAsync(existing, download.FilePath, appId))
                return; // waiting on the user; confirm/cancel command finishes the install

            InstallZipAndReport(download.FilePath, appId);
        }
        catch (ApiException ex)
        {
            Error = ex.Message;
        }
        catch (Exception)
        {
            Error = Resources.Strings.Add_Err_Download;
        }
        finally
        {
            source.IsDownloading = false;
            source.Progress = 0;
            source.IsProgressIndeterminate = false;
        }
    }

    /// <summary>DLC lua: download and install silently (it's just an unlock, no confirm).</summary>
    [RelayCommand]
    private async Task GenerateDlcAsync()
    {
        if (Details?.BaseAppId is null || IsGenerating) return;
        if (await PromptSignInIfGuestAsync(Resources.Strings.Add_SignIn_Download)) return;
        Error = null;
        LastDownload = null;
        InstallStatus = null;
        long appId = Details.AppId;
        IsGenerating = true;
        try
        {
            var download = await _api.GenerateDlcAsync(appId.ToString(), Details.BaseAppId, Details.Name, null);
            LastDownload = download;

            var result = _installer.InstallLua(download.FilePath, appId);
            ReportInstall(result);
            DeleteStaged(download.FilePath); // installed. Drop the temp staging copy
            await RefreshStandardUsageAsync(); // DLC generate counts toward 25/day
        }
        catch (ApiException ex)
        {
            Error = ex.Message;
        }
        catch (Exception)
        {
            Error = Resources.Strings.Add_Err_Generate;
        }
        finally
        {
            IsGenerating = false;
        }
    }

    // ── Install + overwrite confirm ─────────────────────────────────

    /// <summary>Build the before/after diff and open the confirm overlay. Returns true if shown.</summary>
    private async Task<bool> ShowOverwriteConfirmAsync(string oldLuaPath, string newZipPath, long appId)
    {
        var oldLua = LuaFileParser.Parse(oldLuaPath, appId);
        var newLua = ExtractLuaFromZip(newZipPath, appId);
        if (newLua is null) { InstallZipAndReport(newZipPath, appId); return false; } // can't diff. Just install

        var diff = LuaFileParser.Diff(oldLua, newLua);

        // Names from caches + one steamcmd call (cached per app) → real names, sizes, OS, language.
        await _appList.EnsureLoadedAsync();
        _depotsById = await BuildDepotLookupAsync(appId);
        DiffAdded = diff.Added.Select(ToDiffRow).ToList();
        DiffRemoved = diff.Removed.Select(ToDiffRow).ToList();
        ConfirmTitle = string.Format(Resources.Strings.Add_Confirm_Replace, Details?.Name ?? appId.ToString());
        ConfirmNoChanges = diff.HasChanges ? null : Resources.Strings.Add_Confirm_NoChanges;
        _pendingInstall = (newZipPath, appId);
        IsConfirmingOverwrite = true;

        // Lazily resolve any DLC rows still showing a bare id via appdetails, then rebuild.
        var unnamed = diff.Added.Concat(diff.Removed)
            .Where(e => DiffDisplayName(e) is null)
            .Select(e => e.Id).Distinct().ToList();
        if (unnamed.Count > 0)
        {
            await Parallel.ForEachAsync(unnamed, new ParallelOptions { MaxDegreeOfParallelism = 4 },
                async (id, _) => await _appInfo.ResolveAsync(id));
            if (IsConfirmingOverwrite && _pendingInstall is { } p && p.appId == appId)
            {
                DiffAdded = diff.Added.Select(ToDiffRow).ToList();
                DiffRemoved = diff.Removed.Select(ToDiffRow).ToList();
            }
        }
        return true;
    }

    [RelayCommand]
    private void ConfirmOverwrite()
    {
        IsConfirmingOverwrite = false;
        if (_pendingInstall is { } p) InstallZipAndReport(p.zipPath, p.appId);
        _pendingInstall = null;
    }

    [RelayCommand]
    private void CancelOverwrite()
    {
        IsConfirmingOverwrite = false;
        if (_pendingInstall is { } p) DeleteStaged(p.zipPath); // not installing. Don't leave it in temp
        _pendingInstall = null;
        InstallStatus = Resources.Strings.Add_Status_Cancelled;
        InstallFailed = false;
    }

    private void InstallZipAndReport(string zipPath, long appId)
    {
        _installedAppId = appId; // remember for the banner's Reveal, even after the search is cleared
        // The download is always saved as "<appid>.zip", but some sources return a BARE .lua (no zip
        // wrapper). Unzipping that throws "End of Central Directory record could not be found". Sniff
        // the bytes instead of trusting the extension: real zips start with "PK\x03\x04".
        ReportInstall(IsZip(zipPath)
            ? _installer.InstallZip(zipPath, appId)
            : _installer.InstallLua(zipPath, appId));
        DeleteStaged(zipPath); // installed into Steam. The temp staging copy is no longer needed
    }

    /// <summary>Best-effort delete of a staged download after it's been installed, so nothing piles
    /// up in the temp staging folder.</summary>
    private static void DeleteStaged(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    /// <summary>True if the file begins with the ZIP local-file-header magic (PK\x03\x04). A bare .lua
    /// (or any non-zip the server returned under a .zip name) returns false → install it as a loose lua.</summary>
    private static bool IsZip(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> sig = stackalloc byte[4];
            return fs.Read(sig) == 4 && sig[0] == 0x50 && sig[1] == 0x4B && sig[2] == 0x03 && sig[3] == 0x04;
        }
        catch { return false; }
    }

    /// <summary>Turn an InstallResult into the result banner text/state.</summary>
    private void ReportInstall(InstallResult result)
    {
        if (result.Error is not null)
        {
            InstallFailed = true;
            InstallStatus = result.Error;
            return;
        }

        if (result.AnyFailed)
        {
            InstallFailed = true;
            InstallStatus = string.Format(Resources.Strings.Add_Status_InstallFailed, result.Failed.Count);
            return;
        }

        InstallFailed = false;
        string name = Details?.Name ?? "lua";
        InstallStatus = result.ManifestCount > 0
            ? string.Format(Resources.Strings.Add_Status_AddedManifests, name, result.ManifestCount)
            : string.Format(Resources.Strings.Add_Status_AddedFetch, name);
        if (_fastFetchSource is not null)
        {
            InstallStatus += " " + string.Format(Resources.Strings.Add_FastFetch_Via, _fastFetchSource);
            _fastFetchSource = null;
        }
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
            if (!IsZip(zipPath)) return LuaFileParser.Parse(zipPath, appId); // bare .lua → parse as-is

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
        FixCount = 0;
        FixTags = "";
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
