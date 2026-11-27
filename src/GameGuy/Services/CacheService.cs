using System.IO;
using System.Text.Json;

namespace GameGuy.Services;

/// <summary>App-tracked bookkeeping (not user settings). Install fingerprints, cached digests, etc.</summary>
public class CacheData
{
    // ── OpenSteamTools install fingerprint (Mode page up-to-date check) ──
    public string? OpenSteamToolsInstalledVersion { get; set; }
    public string? OpenSteamToolsInstalledZipDigest { get; set; }

    // ── Steam appdetails rate-limit window (rolling ~200 req / ~200s per IP) ──
    // Unix-ms timestamps of recent requests, so the sliding window survives restarts and we don't
    // burst fresh into a still-counting window.
    public List<long> SteamApiRequestTimes { get; set; } = [];

    // ── Donate Keys dedup (permanent) ────────────────────────────────
    // Appids whose decryption keys have already been donated. The backend accepts each depot only
    // once per IP, so this is permanent. A donated appid is never re-sent.
    public List<string> DonatedAppIds { get; set; } = [];

    // ── Hardware appid blacklist (refreshed from GitHub, ~14-day TTL) ──
    // Steam "hardware" appids (Deck, Index, controllers, VR) to hide from featured/search.
    public List<long> HardwareAppIds { get; set; } = [];
    public long HardwareAppIdsFetchedAtMs { get; set; } // Unix-ms of last successful fetch; 0 = never

    // ── Loaded-apps notification list (dismissable) ──────────────────
    // Appids the plugin surfaces as "recently loaded" on the store page; cleared on dismiss.
    // Replaces the old Lua backend's loadedappids.txt.
    public List<long> LoadedAppIds { get; set; } = [];

    // ── First-run onboarding ─────────────────────────────────────────
    // True once the user has seen (and dismissed) the welcome overlay, or the app decided they're
    // already set up. Kept in cache (not settings). It's app bookkeeping, not a user preference.
    public bool OnboardingComplete { get; set; }

    // ── Downloads tab history ────────────────────────────────────────
    // Finished downloads shown in the Downloads tab, newest first, capped. Records only: a queued job
    // holds delegates and can't be serialized, so nothing here is resumable.
    public List<Downloads.DownloadHistoryRecord> DownloadHistory { get; set; } = [];

    // ── Downloaded tool fingerprints (DepotDownloader, Steamless, SteamAutoCrack) ──
    // The release tag last installed, plus when we last asked GitHub. Without these the tools were
    // fetched once by a bare File.Exists check and then pinned forever, which matters now that the
    // DepotDownloader re-pack republishes on every upstream release. CheckedAtMs is Unix-ms; 0 = never.
    public string? DepotDownloaderVersion { get; set; }
    public long DepotDownloaderCheckedAtMs { get; set; }
    public string? SteamlessVersion { get; set; }
    public long SteamlessCheckedAtMs { get; set; }
    public string? SteamAutoCrackVersion { get; set; }
    public long SteamAutoCrackCheckedAtMs { get; set; }
}

/// <summary>
/// Persists internal app state to %AppData%\GameGuy\cache.json. Distinct from user-facing
/// settings (SettingsService). Use this for values the app records about itself (versions/hashes
/// of installed components, cached lookups), not for choices the user makes.
/// </summary>
public class CacheService
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppConfig.DataDirName);
    private static readonly string FilePath = Path.Combine(Dir, "cache.json");
    private static readonly string TmpPath = FilePath + ".tmp";

    /// <summary>
    /// Serializes every read-modify-write of the cache.
    /// </summary>
    /// <remarks>
    /// Writers now come from several threads at once: the download queue persists history from the
    /// dispatcher whenever an item finishes, while DepotDownloaderService and SteamlessService record
    /// tool versions from background threads mid-download. Two unsynchronized writers could interleave
    /// the file write or lose each other's field, and <see cref="Load"/> silently resets to an empty
    /// cache on a parse failure — which would quietly wipe DonatedAppIds, a list that is permanent by
    /// design because the backend accepts each depot only once per IP.
    /// </remarks>
    private readonly object _gate = new();

    private CacheData _cache = new();

    public CacheService() => Load();

    /// <summary>OpenSteamTools: the release tag last installed (for the up-to-date check).</summary>
    public string? OpenSteamToolsInstalledVersion
    {
        get => _cache.OpenSteamToolsInstalledVersion;
        set { _cache.OpenSteamToolsInstalledVersion = string.IsNullOrWhiteSpace(value) ? null : value; Save(); }
    }

    /// <summary>OpenSteamTools: sha256 of the release zip last installed (inner DLL hashes aren't published).</summary>
    public string? OpenSteamToolsInstalledZipDigest
    {
        get => _cache.OpenSteamToolsInstalledZipDigest;
        set { _cache.OpenSteamToolsInstalledZipDigest = string.IsNullOrWhiteSpace(value) ? null : value; Save(); }
    }

    /// <summary>Unix-ms timestamps of recent Steam appdetails requests (rolling rate-limit window).</summary>
    public IReadOnlyList<long> GetSteamApiRequestTimes() => _cache.SteamApiRequestTimes;

    /// <summary>Persist the current rate-limit window (called periodically by the limiter, not per request).</summary>
    public void SaveSteamApiRequestTimes(IEnumerable<long> times)
    {
        _cache.SteamApiRequestTimes = times.ToList();
        Save();
    }

    // ── Donate Keys dedup (permanent) ────────────────────────────────

    /// <summary>Appids whose keys have already been donated (never re-sent).</summary>
    public IReadOnlyCollection<string> GetDonatedAppIds() => _cache.DonatedAppIds;

    public void SaveDonatedAppIds(IEnumerable<string> appIds)
    {
        _cache.DonatedAppIds = appIds.Distinct().ToList();
        Save();
    }

    // ── Hardware appid blacklist ─────────────────────────────────────

    /// <summary>Cached hardware appids to filter out of featured/search.</summary>
    public IReadOnlyList<long> GetHardwareAppIds() => _cache.HardwareAppIds;

    /// <summary>Unix-ms of the last successful blacklist fetch (0 = never), for the TTL check.</summary>
    public long GetHardwareAppIdsFetchedAt() => _cache.HardwareAppIdsFetchedAtMs;

    public void SaveHardwareAppIds(IEnumerable<long> ids, long fetchedAtMs)
    {
        _cache.HardwareAppIds = ids.Distinct().ToList();
        _cache.HardwareAppIdsFetchedAtMs = fetchedAtMs;
        Save();
    }

    // ── Loaded-apps notification list ────────────────────────────────

    /// <summary>Appids surfaced as "recently loaded" on the store page (dismissable).</summary>
    public IReadOnlyList<long> GetLoadedAppIds() => _cache.LoadedAppIds;

    public void SaveLoadedAppIds(IEnumerable<long> ids)
    {
        _cache.LoadedAppIds = ids.Distinct().ToList();
        Save();
    }

    // ── Downloads tab history ────────────────────────────────────────

    /// <summary>Finished downloads, newest first.</summary>
    public IReadOnlyList<Downloads.DownloadHistoryRecord> GetDownloadHistory() => _cache.DownloadHistory;

    /// <summary>Replace the history, keeping only the newest <paramref name="cap"/> entries.</summary>
    public void SaveDownloadHistory(IEnumerable<Downloads.DownloadHistoryRecord> records, int cap = 100)
    {
        _cache.DownloadHistory = records
            .OrderByDescending(r => r.CompletedAtMs)
            .Take(cap)
            .ToList();
        Save();
    }

    // ── Downloaded tool fingerprints ─────────────────────────────────

    /// <summary>Release tag of the installed DepotDownloader, or null if never recorded.</summary>
    public string? DepotDownloaderVersion
    {
        get => _cache.DepotDownloaderVersion;
        set { _cache.DepotDownloaderVersion = string.IsNullOrWhiteSpace(value) ? null : value; Save(); }
    }

    /// <summary>Unix-ms of the last successful DepotDownloader release check; 0 = never.</summary>
    public long DepotDownloaderCheckedAtMs
    {
        get => _cache.DepotDownloaderCheckedAtMs;
        set { _cache.DepotDownloaderCheckedAtMs = value; Save(); }
    }

    /// <summary>Release tag of the installed Steamless, or null if never recorded.</summary>
    public string? SteamlessVersion
    {
        get => _cache.SteamlessVersion;
        set { _cache.SteamlessVersion = string.IsNullOrWhiteSpace(value) ? null : value; Save(); }
    }

    /// <summary>Unix-ms of the last successful Steamless release check; 0 = never.</summary>
    public long SteamlessCheckedAtMs
    {
        get => _cache.SteamlessCheckedAtMs;
        set { _cache.SteamlessCheckedAtMs = value; Save(); }
    }

    /// <summary>Release tag of the installed SteamAutoCrack, or null if never recorded.</summary>
    public string? SteamAutoCrackVersion
    {
        get => _cache.SteamAutoCrackVersion;
        set { _cache.SteamAutoCrackVersion = string.IsNullOrWhiteSpace(value) ? null : value; Save(); }
    }

    /// <summary>Unix-ms of the last successful SteamAutoCrack release check; 0 = never.</summary>
    public long SteamAutoCrackCheckedAtMs
    {
        get => _cache.SteamAutoCrackCheckedAtMs;
        set { _cache.SteamAutoCrackCheckedAtMs = value; Save(); }
    }

    /// <summary>Clear the loaded-apps notification list (ReadLoadedApps → DismissLoadedApps).</summary>
    public void ClearLoadedAppIds()
    {
        _cache.LoadedAppIds = [];
        Save();
    }

    /// <summary>Remove a single appid from the loaded-apps notification list (e.g. after its Lua is
    /// removed) so a removed game doesn't linger in the "recently added" popup.</summary>
    public void RemoveLoadedAppId(long id)
    {
        if (_cache.LoadedAppIds.Remove(id))
            Save();
    }

    /// <summary>True once the first-run onboarding overlay has been completed (or auto-skipped because the
    /// user is already set up). Persisted so it shows at most once.</summary>
    public bool OnboardingComplete
    {
        get => _cache.OnboardingComplete;
        set { _cache.OnboardingComplete = value; Save(); }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                _cache = JsonSerializer.Deserialize<CacheData>(File.ReadAllText(FilePath)) ?? new CacheData();
        }
        catch
        {
            _cache = new CacheData();
        }
    }

    private void Save()
    {
        lock (_gate) { SaveLocked(); }
    }

    private void SaveLocked()
    {
        bool empty = _cache.OpenSteamToolsInstalledVersion is null
            && _cache.OpenSteamToolsInstalledZipDigest is null
            && _cache.SteamApiRequestTimes.Count == 0
            && _cache.DonatedAppIds.Count == 0
            && _cache.HardwareAppIds.Count == 0
            && _cache.HardwareAppIdsFetchedAtMs == 0
            && _cache.LoadedAppIds.Count == 0
            && !_cache.OnboardingComplete
            && _cache.DownloadHistory.Count == 0
            && _cache.DepotDownloaderVersion is null
            && _cache.DepotDownloaderCheckedAtMs == 0
            && _cache.SteamlessVersion is null
            && _cache.SteamlessCheckedAtMs == 0
            && _cache.SteamAutoCrackVersion is null
            && _cache.SteamAutoCrackCheckedAtMs == 0;
        if (empty)
        {
            try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { /* best effort */ }
            return;
        }

        Directory.CreateDirectory(Dir);

        // Write-then-move, the same shape SettingsService uses. A bare WriteAllText that is interrupted
        // (crash, power loss, or a second writer) leaves truncated JSON, and Load() answers that by
        // resetting to an empty cache — silently discarding everything, including the permanent
        // DonatedAppIds list. File.Move over an existing file is atomic on NTFS.
        string json = JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(TmpPath, json);
        File.Move(TmpPath, FilePath, overwrite: true);
    }
}
