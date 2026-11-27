using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameGuy.Models;
using Microsoft.Extensions.Logging;

namespace GameGuy.Services;

public record SteamAppInfo(string Name, string? HeaderImage);

/// <summary>The slice of app-details used by the Manage page filters/sorts. Parsed from the cached
/// full blob; fields are best-effort (null/empty when absent).</summary>
public record AppFilterData(
    string? Type,                  // game / dlc / demo / …
    IReadOnlyList<string> Genres,  // e.g. ["Action","RPG"]
    bool Windows, bool Mac, bool Linux,
    int? ReleaseYear,
    DateTime? ReleaseDate,         // full parsed release date (for accurate newest-first sort)
    string? ReleaseDateText,       // the raw Steam date string (for display, e.g. "24 Feb, 2022")
    bool IsFree,
    int? Metacritic,
    long? Reviews,                 // recommendations.total
    bool IsAdult);                 // has a nudity/sexual Steam content descriptor (id 1, 3, or 4)

/// <summary>
/// Display-only slice of the cached appdetails blob, for the Manage flyout. Separate from
/// <see cref="AppFilterData"/> on purpose: that one is parsed for every game on every filter pass,
/// while these fields are only ever needed for the one game whose flyout is open.
/// </summary>
public record AppOverview(
    string? ShortDescription,
    IReadOnlyList<string> Developers,
    IReadOnlyList<string> Publishers,
    IReadOnlyList<string> Genres)
{
    public string DeveloperLabel => string.Join(", ", Developers);
    public string PublisherLabel => string.Join(", ", Publishers);

    public bool HasDescription => !string.IsNullOrWhiteSpace(ShortDescription);
    public bool HasDeveloper => Developers.Count > 0;

    /// <summary>
    /// Developer and publisher are the SAME studio for roughly 40% of apps. Showing both lines then
    /// just reads as a duplicated typo, so the publisher line is only worth rendering when it differs.
    /// </summary>
    public bool HasDistinctPublisher =>
        Publishers.Count > 0 &&
        !string.Equals(PublisherLabel, DeveloperLabel, StringComparison.OrdinalIgnoreCase);

    public bool HasGenres => Genres.Count > 0;

    /// <summary>True when there's anything at all worth rendering (else the section stays collapsed).</summary>
    public bool HasAnything => HasDescription || HasDeveloper || HasGenres;
}

/// <summary>
/// Resolves Steam app name + header image by appid, cached to disk so each game is looked up once.
/// </summary>
public class SteamAppInfoCache
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppConfig.DataDirName);
    // Full raw appdetails 'data' blob per appid: the single on-disk source of truth. Name + header image
    // (the fast in-RAM index below) are derived from these on demand; there is no separate appinfo.json.
    private static readonly string DetailsDir = Path.Combine(Dir, "details");

    private readonly HttpClient _http = AppHttp.Create(TimeSpan.FromSeconds(15));
    // In-memory fast path for name/header-image. Populated by network resolves, and lazily rehydrated from
    // the /details blobs on a GetCached miss. A null value means "looked, no usable details" (negative
    // cache) so we don't re-read a missing/empty blob on every grid render.
    private readonly ConcurrentDictionary<long, SteamAppInfo?> _cache = new();

    // Sliding-window rate limiter. MEASURED (3 clean test rounds): appdetails allows ~200 requests in
    // a rolling ~200-second window per IP; exceeding it returns 429 (no Retry-After) until the oldest
    // request ages out (~200s). We burst up to MaxPerWindow, then pace at the window edge so we never
    // trip the 429. The window timestamps persist to cache.json so a restart resumes it (no fresh burst
    // into a still-counting window).
    private readonly CacheService _cache2;
    private readonly SemaphoreSlim _rateGate = new(1, 1);
    private readonly Queue<DateTime> _requestTimes = new();
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(200);
    private const int MaxPerWindow = 190; // small safety margin under the measured 200
    private DateTime _lastPersist = DateTime.MinValue;

    private readonly ILogger<SteamAppInfoCache> _log;

    public SteamAppInfoCache(CacheService cache, ILogger<SteamAppInfoCache> log)
    {
        _cache2 = cache;
        _log = log;
        // Restore the rolling window from a previous run so we don't burst fresh into a counting window.
        var now = DateTime.UtcNow;
        foreach (long ms in _cache2.GetSteamApiRequestTimes())
        {
            var t = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
            if (now - t < Window) _requestTimes.Enqueue(t);
        }
    }

    /// <summary>
    /// Predictable CDN cover URL. Works for older and even delisted apps (assets stay on the CDN);
    /// some newer apps 404 here and fall back to the appdetails header_image.
    /// </summary>
    public static string GuessHeaderImageUrl(long appid) =>
        $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appid}/header.jpg";

    /// <summary>Fast, synchronous name + header-image lookup for the UI hot path. Served from RAM; on a
    /// miss it lazily reads the app's /details blob (the source of truth) once, then memoizes the result
    /// (including a negative result, so a missing/empty blob isn't re-read on every render).</summary>
    public SteamAppInfo? GetCached(long appid)
    {
        if (_cache.TryGetValue(appid, out var info)) return info; // hit (may be a cached null)
        var fromDisk = ReadInfoFromDetails(appid);
        _cache[appid] = fromDisk; // memoize hit OR miss
        return fromDisk;
    }

    /// <summary>Derive name + header image from the cached /details blob, or null if it's absent, empty
    /// (the delisted "{}" marker), or unparseable.</summary>
    private static SteamAppInfo? ReadInfoFromDetails(long appid)
    {
        try
        {
            if (!File.Exists(DetailsPath(appid))) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(DetailsPath(appid)));
            var d = doc.RootElement;
            if (d.ValueKind != JsonValueKind.Object) return null;
            string? name = d.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(name)) return null;
            string? image = d.TryGetProperty("header_image", out var img) ? img.GetString() : null;
            return new SteamAppInfo(name, image);
        }
        catch { return null; } // corrupt/partial blob → treat as not-cached
    }

    // ── appdetails response shape ────────────────────────────────────

    /// <summary>
    /// Pick out the appdetails entry that actually describes <paramref name="appid"/>.
    /// Null when no entry in the response can be PROVEN to be this app's.
    /// </summary>
    /// <remarks>
    /// appdetails used to be keyed by the appid you asked for. Since ~Sep 2026, any app that HAS child
    /// apps (a DLC, a soundtrack) comes back keyed by one of those CHILD ids instead: appids=500 answers
    /// under "1660740" ("Left 4 Dead - Uncensored", whose parent is 500 and which itself returns
    /// success:false when queried on its own). Measured the same way for 730→2678630, 620→323180,
    /// 220→323140, 243470→293061 and 1091500→2441600. Apps with no child apps (10, 240, 271590, 5080530)
    /// and EVERY success:false body are still keyed by the requested id. The inner data.steam_appid always
    /// echoes the id we asked for; it is the only trustworthy anchor left, and filters=basic changes none
    /// of this. Do not "simplify" the scan away.
    ///
    /// There is deliberately no "if the object has exactly one entry, take it" fallback. It would look
    /// like it fixes more cases, but its failure mode is silent and permanent: the wrong game's name,
    /// header image, genres and release date get written to details\&lt;appid&gt;.json, which is the single
    /// on-disk source of truth for this app from then on. Resolving nothing costs one wasted request and
    /// self-heals on the next ship; resolving the wrong game needs the user to find and delete their
    /// cache. Refuse rather than guess.
    ///
    /// Returns JsonElement? rather than throwing on purpose: GetProperty(appid) throwing
    /// KeyNotFoundException into a bare catch is exactly what made this bug invisible for so long.
    /// internal, not private, so the shape tolerance is unit-testable with no HTTP stub
    /// (InternalsVisibleTo GameGuy.Tests).
    /// </remarks>
    internal static JsonElement? FindAppDetailsEntry(JsonElement root, long appid)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;

        // 1. The direct key. Still correct for childless apps, and the ONLY way a success:false body can
        //    ever be matched (it carries no "data" object to anchor on). Refused only if it DOES carry a
        //    data object and that object names some other app.
        if (root.TryGetProperty(appid.ToString(), out var direct) &&
            (!TryGetDataObject(direct, out var directData) || DataNamesApp(directData, appid)))
            return direct;

        // 2. Child-keyed shape: the key is a DLC/soundtrack id, so scan for the entry whose
        //    data.steam_appid is the app we actually asked for.
        foreach (var prop in root.EnumerateObject())
            if (TryGetDataObject(prop.Value, out var data) && DataNamesApp(data, appid))
                return prop.Value;

        return null;
    }

    /// <summary>The entry's "data" object, if it has one. A success:false entry does not.</summary>
    private static bool TryGetDataObject(JsonElement entry, out JsonElement data)
    {
        data = default;
        return entry.ValueKind == JsonValueKind.Object &&
               entry.TryGetProperty("data", out data) && data.ValueKind == JsonValueKind.Object;
    }

    /// <summary>True when this data blob's steam_appid is the app we asked for. A missing or non-numeric
    /// steam_appid counts as "not proven", so it is refused rather than assumed.</summary>
    private static bool DataNamesApp(JsonElement data, long appid) =>
        data.TryGetProperty("steam_appid", out var sa) && sa.ValueKind == JsonValueKind.Number &&
        sa.TryGetInt64(out long got) && got == appid;

    /// <summary>Fetch an app's name + header image from Steam (throttled, retries on 429/403). Null on
    /// failure. Pulls the FULL appdetails payload and caches the whole blob (for filters). Name/header
    /// are derived from it, so each app is only ever fetched once.</summary>
    public async Task<SteamAppInfo?> ResolveAsync(long appid, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(appid, out var cached)) return cached;

        // cc=us so region-blocked games (e.g. titles banned in the user's country) still resolve.
        // This is metadata only, not purchasing, so a neutral region is correct.
        var url = $"https://store.steampowered.com/api/appdetails?appids={appid}&cc=us&l=english";

        const int maxAttempts = 3; // initial + 2 retries
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            await ThrottleAsync(ct);
            try
            {
                using var res = await _http.GetAsync(url, ct);
                if (res.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden)
                {
                    if (attempt < maxAttempts - 1) { await Task.Delay(TimeSpan.FromSeconds(4 * (attempt + 1)), ct); continue; }
                    return null;
                }
                if (!res.IsSuccessStatusCode) return null;

                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                if (FindAppDetailsEntry(doc.RootElement, appid) is not { } entry)
                {
                    // Steam changed the response shape again. Resolve nothing, cache nothing — a null
                    // here already means "couldn't resolve right now" to every caller.
                    LogUnresolvableOnce(appid);
                    return null;
                }

                // success:false carries no "data" at all, and a success:true with no usable data object
                // is just as empty. Either way Steam has nothing for this app.
                if (!entry.TryGetProperty("success", out var okEl) || okEl.ValueKind != JsonValueKind.True ||
                    !entry.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                {
                    // Delisted/unavailable → no details to cache for filters; mark so backfill skips it.
                    _ = SaveFullDetailsAsync(appid, "{}");
                    return null;
                }

                string? name = data.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(name)) return null;

                string? image = data.TryGetProperty("header_image", out var img) ? img.GetString() : null;
                var info = new SteamAppInfo(name, image);
                _cache[appid] = info; // warm the session RAM index

                // Persist the whole 'data' blob (this is the full payload). The single on-disk source of
                // truth; name + header image are re-derived from it (no separate appinfo.json).
                _ = SaveFullDetailsAsync(appid, data.GetRawText());
                return info;
            }
            catch (OperationCanceledException) { return null; }
            catch { return null; } // offline / unknown appid / parse error
        }
        return null;
    }

    // ── Full details (for filters) ───────────────────────────────────

    /// <summary>True if the full raw appdetails blob is already cached on disk for this appid.</summary>
    public bool HasFullDetails(long appid) => File.Exists(DetailsPath(appid));

    /// <summary>Read the cached full appdetails 'data' JSON for this appid, or null if not cached.</summary>
    public string? GetFullDetails(long appid)
    {
        try { return HasFullDetails(appid) ? File.ReadAllText(DetailsPath(appid)) : null; }
        catch { return null; }
    }

    /// <summary>Parse the cached full blob into the fields the Manage filters use. Null if not cached
    /// yet (the caller treats that as "details loading"). Memoized so repeated filters don't re-parse.</summary>
    public AppFilterData? GetFilterData(long appid)
    {
        if (_filterCache.TryGetValue(appid, out var memo)) return memo;
        string? raw = GetFullDetails(appid);
        if (raw is null) return null;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var d = doc.RootElement;

            string? type = d.TryGetProperty("type", out var t) ? t.GetString() : null;

            var genres = new List<string>();
            if (d.TryGetProperty("genres", out var g) && g.ValueKind == JsonValueKind.Array)
                foreach (var item in g.EnumerateArray())
                    if (item.TryGetProperty("description", out var desc) && desc.GetString() is { } s)
                        genres.Add(s);

            bool win = false, mac = false, lin = false;
            if (d.TryGetProperty("platforms", out var p) && p.ValueKind == JsonValueKind.Object)
            {
                win = p.TryGetProperty("windows", out var w) && w.ValueKind == JsonValueKind.True;
                mac = p.TryGetProperty("mac", out var m) && m.ValueKind == JsonValueKind.True;
                lin = p.TryGetProperty("linux", out var l) && l.ValueKind == JsonValueKind.True;
            }

            int? year = null;
            DateTime? releaseDate = null;
            string? releaseDateText = null;
            if (d.TryGetProperty("release_date", out var rd) && rd.TryGetProperty("date", out var dateEl) &&
                dateEl.GetString() is { } ds)
            {
                releaseDateText = string.IsNullOrWhiteSpace(ds) ? null : ds.Trim();
                // Steam dates come in regional formats like "24 Feb, 2022", "Feb 24, 2022", or just
                // "2022" / "Q1 2022" / "Coming soon". Try a full parse first (for accurate sorting);
                // always fall back to a 4-digit year (for the Year filter dropdown).
                if (DateTime.TryParse(ds, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var parsed))
                {
                    releaseDate = parsed;
                    year = parsed.Year;
                }
                else
                {
                    var m = Regex.Match(ds, @"\b(19|20)\d{2}\b"); // pull a 4-digit year from "Q1 2022" etc.
                    if (m.Success && int.TryParse(m.Value, out int y))
                    {
                        year = y;
                        releaseDate = new DateTime(y, 1, 1); // year-only → sort as Jan 1 of that year
                    }
                }
            }

            bool isFree = d.TryGetProperty("is_free", out var f) && f.ValueKind == JsonValueKind.True;

            int? meta = null;
            if (d.TryGetProperty("metacritic", out var mc) && mc.TryGetProperty("score", out var sc) &&
                sc.TryGetInt32(out int score)) meta = score;

            long? reviews = null;
            if (d.TryGetProperty("recommendations", out var rec) && rec.TryGetProperty("total", out var tot) &&
                tot.TryGetInt64(out long rv)) reviews = rv;

            // Adult content via Steam's official content_descriptors taxonomy (region-independent).
            // Only the genuinely-adult tier counts: id 3 = "Adult Only Sexual Content", 4 = "Frequent
            // Nudity or Sexual Content". We deliberately EXCLUDE id 1 ("Some Nudity or Sexual Content")
            // because that's the mainstream-M tier. It flags AAA games like Ghost of Tsushima / Ready or
            // Not that aren't "adult". (id 2 = violence/gore, 5 = general mature also excluded.) Absent → non-adult.
            bool isAdult = false;
            if (d.TryGetProperty("content_descriptors", out var cd) &&
                cd.TryGetProperty("ids", out var ids) && ids.ValueKind == JsonValueKind.Array)
                isAdult = ids.EnumerateArray().Any(e => e.TryGetInt32(out int id) && (id is 3 or 4));

            var result = new AppFilterData(type, genres, win, mac, lin, year, releaseDate, releaseDateText, isFree, meta, reviews, isAdult);
            _filterCache[appid] = result;
            return result;
        }
        catch
        {
            return null; // malformed cache entry → treat as not-yet-available
        }
    }

    private readonly ConcurrentDictionary<long, AppFilterData> _filterCache = new();
    private readonly ConcurrentDictionary<long, AppOverview> _overviewCache = new();

    // Appids whose appdetails entry we could not identify at all this session. Used ONLY to keep the log
    // to one line per app — deliberately nothing is persisted for them (see FindAppDetailsEntry: a "{}"
    // marker here would permanently poison a perfectly good game's cache just to save a few requests).
    private readonly ConcurrentDictionary<long, byte> _unresolvable = new();

    /// <summary>Report, once per app per session, that no entry in the response could be proven to be
    /// this app's. In normal operation this never fires; if it starts firing, Steam changed the response
    /// shape again and <see cref="FindAppDetailsEntry"/> needs another case.</summary>
    private void LogUnresolvableOnce(long appid)
    {
        if (_unresolvable.TryAdd(appid, 0))
            _log.LogWarning("appdetails: no entry with steam_appid={AppId} in the response — Steam's " +
                            "response shape changed again. Nothing cached for this app.", appid);
    }

    /// <summary>
    /// Blurb + studio + genres from the cached blob, for the Manage flyout. Null when the app has no
    /// cached details yet, or the blob is the "{}" delisted marker. The caller collapses the section
    /// rather than rendering an empty shell.
    /// </summary>
    public AppOverview? GetOverview(long appid)
    {
        if (_overviewCache.TryGetValue(appid, out var memo)) return memo;

        string? raw = GetFullDetails(appid);
        if (string.IsNullOrWhiteSpace(raw) || raw == "{}") return null;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var d = doc.RootElement;

            // Steam's short_description carries no HTML tags but DOES carry entities in ~6% of apps
            // ("management &amp; tycoon game"). Undecoded, that renders literally on screen.
            string? blurb = d.TryGetProperty("short_description", out var sd) ? sd.GetString() : null;
            if (!string.IsNullOrWhiteSpace(blurb)) blurb = WebUtility.HtmlDecode(blurb).Trim();

            var overview = new AppOverview(
                string.IsNullOrWhiteSpace(blurb) ? null : blurb,
                ReadStringArray(d, "developers"),   // occasionally more than one studio
                ReadStringArray(d, "publishers"),
                ReadDescriptions(d, "genres"));

            _overviewCache[appid] = overview;
            return overview;
        }
        catch
        {
            return null; // malformed cache entry → treat as not-yet-available
        }
    }

    /// <summary>Read a JSON array of plain strings (developers / publishers), decoding entities.</summary>
    private static List<string> ReadStringArray(JsonElement d, string property)
    {
        var list = new List<string>();
        if (d.TryGetProperty(property, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var item in arr.EnumerateArray())
                if (item.GetString() is { } s && !string.IsNullOrWhiteSpace(s))
                    list.Add(WebUtility.HtmlDecode(s).Trim());
        return list;
    }

    /// <summary>Read an array of { description } objects (genres / categories).</summary>
    private static List<string> ReadDescriptions(JsonElement d, string property)
    {
        var list = new List<string>();
        if (d.TryGetProperty(property, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var item in arr.EnumerateArray())
                if (item.TryGetProperty("description", out var desc) && desc.GetString() is { } s)
                    list.Add(WebUtility.HtmlDecode(s).Trim());
        return list;
    }

    /// <summary>Fetch (if needed) + build the app's <see cref="GameDetails"/> straight from Steam's
    /// appdetails. Replacing the old lua.tools/api/steam/details proxy, which was just this endpoint with
    /// 7 fields mapped. Interactive priority (beats the Manage backfill). Null if delisted/unavailable.</summary>
    public async Task<GameDetails?> ResolveGameDetailsAsync(long appid, CancellationToken ct = default)
    {
        await EnsureFullDetailsAsync(appid, ct); // interactive priority; caches the blob to details\<appid>.json
        return GetGameDetails(appid);
    }

    /// <summary>Build <see cref="GameDetails"/> from the cached Steam appdetails blob (same field mapping
    /// the lua.tools proxy did). Null if not cached, delisted ("{}"), or nameless.</summary>
    public GameDetails? GetGameDetails(long appid)
    {
        string? raw = GetFullDetails(appid);
        if (string.IsNullOrWhiteSpace(raw) || raw == "{}") return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var d = doc.RootElement;
            string? name = d.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(name)) return null;

            var genres = new List<string>();
            if (d.TryGetProperty("genres", out var g) && g.ValueKind == JsonValueKind.Array)
                foreach (var it in g.EnumerateArray())
                    if (it.TryGetProperty("description", out var de) && de.GetString() is { } s) genres.Add(s);

            // For DLC, Steam returns the parent game under "fullgame" (appid may be string or number).
            string? baseApp = null;
            if (d.TryGetProperty("fullgame", out var fg) && fg.ValueKind == JsonValueKind.Object
                && fg.TryGetProperty("appid", out var fa))
                baseApp = fa.ValueKind == JsonValueKind.String ? fa.GetString() : fa.GetRawText();

            return new GameDetails
            {
                Name = name,
                AppId = d.TryGetProperty("steam_appid", out var sa) && sa.TryGetInt64(out var said) ? said : appid,
                Type = d.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "",
                BaseAppId = baseApp,
                Genres = genres,
                HeaderImage = d.TryGetProperty("header_image", out var hi) ? hi.GetString() : null,
                ReleaseDate = d.TryGetProperty("release_date", out var rd)
                    && rd.TryGetProperty("date", out var dt) ? dt.GetString() : null,
            };
        }
        catch { return null; }
    }

    /// <summary>
    /// Fetch + cache the FULL appdetails blob (no filters=basic) for a single appid, if not already
    /// cached. Throttled + retried like ResolveAsync. Returns true if the full blob is now on disk.
    /// Used by the background backfill so filters eventually have complete data.
    /// </summary>
    public async Task<bool> EnsureFullDetailsAsync(long appid, CancellationToken ct = default, bool background = false)
    {
        if (HasFullDetails(appid)) return true;

        // cc=us so region-blocked games (e.g. titles banned in the user's country) still resolve.
        // This is metadata only, not purchasing, so a neutral region is correct.
        var url = $"https://store.steampowered.com/api/appdetails?appids={appid}&cc=us&l=english";
        const int maxAttempts = 3;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            await ThrottleAsync(ct, background);
            try
            {
                using var res = await _http.GetAsync(url, ct);
                if (res.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden)
                {
                    if (attempt < maxAttempts - 1) { await Task.Delay(TimeSpan.FromSeconds(4 * (attempt + 1)), ct); continue; }
                    return false;
                }
                if (!res.IsSuccessStatusCode) return false;

                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                if (FindAppDetailsEntry(doc.RootElement, appid) is not { } entry)
                {
                    // Nothing landed on disk, so say so. Deliberately NOT the "{}" marker: that is
                    // permanent (HasFullDetails would return true forever, so nothing ever re-fetches),
                    // and poisoning a healthy game's cache is far worse than a few retried requests.
                    LogUnresolvableOnce(appid);
                    return false;
                }

                if (!entry.TryGetProperty("success", out var okEl) || okEl.ValueKind != JsonValueKind.True ||
                    !entry.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                {
                    // Delisted / unavailable even in a neutral region → won't ever resolve. Cache an
                    // empty marker so backfill stops retrying and it's not counted as "still fetching".
                    await SaveFullDetailsAsync(appid, "{}");
                    return true;
                }

                await SaveFullDetailsAsync(appid, data.GetRawText());

                // Opportunistically warm the session RAM index too if it's missing.
                if (!_cache.ContainsKey(appid) && data.TryGetProperty("name", out var n) && n.GetString() is { } nm && nm.Length > 0)
                {
                    string? image = data.TryGetProperty("header_image", out var img) ? img.GetString() : null;
                    _cache[appid] = new SteamAppInfo(nm, image);
                }
                return true;
            }
            catch (OperationCanceledException) { return false; }
            catch { return false; }
        }
        return false;
    }

    /// <summary>
    /// Background backfill: for each appid missing the full blob, fetch it gently. Lower priority than
    /// on-demand lookups (they share the throttle; UI bursts win because they run first). Stops on
    /// cancellation. Caller passes the library appids (most-relevant first). <paramref name="onProgress"/>
    /// fires after each fetch so the UI can fill in dropdowns/counts live.
    /// </summary>
    public async Task BackfillFullDetailsAsync(IEnumerable<long> appids, Action? onProgress = null, CancellationToken ct = default)
    {
        foreach (long appid in appids)
        {
            if (ct.IsCancellationRequested) return;
            if (HasFullDetails(appid)) continue;
            await EnsureFullDetailsAsync(appid, ct, background: true); // yields to interactive Add/tile lookups
            onProgress?.Invoke();
            // Small gap so backfill stays a trickle and yields to interactive lookups.
            try { await Task.Delay(TimeSpan.FromMilliseconds(750), ct); } catch (OperationCanceledException) { return; }
        }
    }

    private static string DetailsPath(long appid) => Path.Combine(DetailsDir, $"{appid}.json");

    private async Task SaveFullDetailsAsync(long appid, string rawJson)
    {
        try
        {
            Directory.CreateDirectory(DetailsDir);
            await File.WriteAllTextAsync(DetailsPath(appid), rawJson);
        }
        catch { /* best effort */ }
    }

    // >0 while an interactive (non-background) throttle acquisition is queued. Background work (the Manage
    // backfill) steps aside for these, so Add-page/tile lookups always get the next slot ahead of it.
    private int _interactiveWaiting;

    /// <summary>Burst up to MaxPerWindow, then pace at the edge of the window (never trips 429). Interactive
    /// callers (default) take priority; <paramref name="background"/> callers yield the next slot, and the
    /// gate during a cap-wait. To any waiting interactive request.</summary>
    private async Task ThrottleAsync(CancellationToken ct, bool background = false)
    {
        if (!background) Interlocked.Increment(ref _interactiveWaiting);
        try
        {
            await _rateGate.WaitAsync(ct);
            bool held = true;
            try
            {
                while (true)
                {
                    // Background work releases the gate and waits while any interactive request is queued,
                    // so the interactive one acquires the gate + next slot first.
                    if (background && Volatile.Read(ref _interactiveWaiting) > 0)
                    {
                        _rateGate.Release(); held = false;
                        await Task.Delay(50, ct);
                        await _rateGate.WaitAsync(ct); held = true;
                        continue;
                    }

                    var now = DateTime.UtcNow;
                    while (_requestTimes.Count > 0 && now - _requestTimes.Peek() > Window)
                        _requestTimes.Dequeue();

                    if (_requestTimes.Count < MaxPerWindow)
                    {
                        _requestTimes.Enqueue(now);
                        PersistWindow(now);
                        return;
                    }

                    // At the cap: release the gate (so a higher-priority request can proceed), wait for the
                    // oldest to age out, then re-acquire and re-check.
                    var wait = _requestTimes.Peek() + Window - now;
                    _rateGate.Release(); held = false;
                    await Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.FromMilliseconds(100), ct);
                    await _rateGate.WaitAsync(ct); held = true;
                }
            }
            finally { if (held) _rateGate.Release(); }
        }
        finally
        {
            if (!background) Interlocked.Decrement(ref _interactiveWaiting);
        }
    }

    /// <summary>Persist the current rolling window to cache.json (throttled to ~5s so we don't thrash
    /// the file on every request). Called under the rate gate, so the queue is stable here.</summary>
    private void PersistWindow(DateTime now)
    {
        if (now - _lastPersist < TimeSpan.FromSeconds(5)) return;
        _lastPersist = now;
        var times = _requestTimes.Select(t => new DateTimeOffset(t, TimeSpan.Zero).ToUnixTimeMilliseconds());
        _cache2.SaveSteamApiRequestTimes(times);
    }

}
