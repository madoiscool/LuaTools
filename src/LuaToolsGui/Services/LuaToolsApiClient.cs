using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using LuaToolsGui.Models;

namespace LuaToolsGui.Services;

public class ApiException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

public record DownloadedFile(string FilePath, string FileName);

/// <summary>Typed client for the lua.tools web API, authenticated with a Supabase bearer token.</summary>
public class LuaToolsApiClient(AuthService auth, SteamAppInfoCache appInfo, CoverCache covers)
{
    // Interim staging destination: downloads land here, get installed into Steam, then are deleted.
    // Under %TEMP% (not the user's Downloads) so nothing accumulates in a user-visible folder.
    private static readonly string InterimDownloadsFolder =
        Path.Combine(Path.GetTempPath(), "LuaToolsGui", "downloads");

    private readonly HttpClient _http = new()
    {
        BaseAddress = new Uri(AppConfig.ApiBaseUrl),
        Timeout = TimeSpan.FromMinutes(5), // large manifest zips on slow connections
    };

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // ── Endpoints ───────────────────────────────────────────────────

    public async Task<List<SteamSearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        // Calls Steam's public store-search directly (no lua.tools, no auth). Guests can search.
        var url = $"{AppConfig.SteamStoreSearchUrl}?term={Uri.EscapeDataString(query)}&l=english&cc=US";
        var res = await _http.GetAsync(url, ct);
        if (!res.IsSuccessStatusCode) return [];

        var data = await ReadJsonAsync<SteamStoreSearchResponse>(res, ct);
        return (data?.Items ?? [])
            .Take(8)
            .Select(i => new SteamSearchResult
            {
                AppId = i.Id,
                Name = i.Name,
                Icon = i.TinyImage ?? $"https://cdn.cloudflare.steamstatic.com/steam/apps/{i.Id}/capsule_sm_120.jpg",
            })
            .ToList();
    }

    /// <summary>Steam's featured "top sellers" + "new releases" lists for the Add page strips. Public,
    /// no auth. Returns empty lists on any failure (the strips just don't show). Each list keeps only real
    /// games (type 0) that have a capsule image, capped to keep the strips light.</summary>
    public async Task<(List<SteamFeaturedItem> TopSellers, List<SteamFeaturedItem> NewReleases)> GetFeaturedAsync(
        CancellationToken ct = default)
    {
        try
        {
            var res = await _http.GetAsync($"{AppConfig.SteamFeaturedUrl}?cc=us&l=english", ct);
            if (!res.IsSuccessStatusCode) return ([], []);
            var data = await ReadJsonAsync<SteamFeaturedResponse>(res, ct);

            // Steam's featuredcategories genuinely repeats appids within a list (e.g. top_sellers returns
            // the same game 2–3×), so DistinctBy the appid. Keeps the first, preserving Steam's order.
            static List<SteamFeaturedItem> Clean(SteamFeaturedCategory? c) =>
                (c?.Items ?? [])
                    .Where(i => i.Type == 0 && i.Id > 0 && !string.IsNullOrEmpty(i.LargeCapsuleImage))
                    .DistinctBy(i => i.Id)
                    .Take(20)
                    .ToList();

            return (Clean(data?.TopSellers), Clean(data?.NewReleases));
        }
        catch { return ([], []); }
    }

    /// <summary>Public endpoint, no auth required.</summary>
    /// <summary>Game metadata straight from Steam's appdetails (cached to details\&lt;appid&gt;.json via the
    /// throttle, interactive priority), no lua.tools proxy. ANY fetch path funnels through here (normal /
    /// DLC / fast / plugin add), so this is also where the header image gets warmed into covers\.</summary>
    public async Task<GameDetails?> GetDetailsAsync(string appid, CancellationToken ct = default)
    {
        if (!long.TryParse(appid, out long id)) return null;
        var details = await appInfo.ResolveGameDetailsAsync(id, ct);
        if (details is { HeaderImage: { Length: > 0 } img })
            _ = covers.EnsureAsync(id, img, CancellationToken.None); // warm the cover cache (best-effort)
        return details;
    }

    /// <summary>Source name → "available" | "unavailable" | other status.</summary>
    public async Task<Dictionary<string, string>> CheckSourcesAsync(string appid, CancellationToken ct = default)
    {
        // Calls the manifest backend directly (no lua.tools, no auth). The backend is gated
        // by a fixed User-Agent rather than a token, so guests can check availability.
        var req = new HttpRequestMessage(HttpMethod.Get, $"{AppConfig.ManifestBackendUrl}/check_apis?appid={appid}");
        req.Headers.TryAddWithoutValidation("User-Agent", AppConfig.ManifestBackendUserAgent);
        var res = await _http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) return [];
        return await ReadJsonAsync<Dictionary<string, string>>(res, ct) ?? [];
    }

    /// <summary>
    /// The standard lua.tools daily download usage (25/day), counted live from the user_downloads
    /// table via Supabase REST. The same source the website reads. RLS scopes it to the signed-in
    /// user, so no user id is needed. Null on failure / not signed in.
    /// </summary>
    public async Task<StandardUsage?> GetStandardUsageAsync(CancellationToken ct = default)
    {
        try
        {
            // Count today's rows without fetching them: HEAD + Prefer: count=exact → Content-Range header.
            var todayUtc = DateTime.UtcNow.Date.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
            string url = $"{AppConfig.SupabaseUrl}/rest/v1/user_downloads" +
                         $"?select=appid&downloaded_at=gte.{Uri.EscapeDataString(todayUtc)}";

            var req = new HttpRequestMessage(HttpMethod.Head, url);
            req.Headers.TryAddWithoutValidation("apikey", AppConfig.SupabaseAnonKey);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await auth.GetValidAccessTokenAsync());
            req.Headers.TryAddWithoutValidation("Prefer", "count=exact");

            using var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return null;

            // Content-Range: "0-24/25"  (or "*/0" when empty). The count is after the slash.
            string? range = res.Content.Headers.TryGetValues("Content-Range", out var v) ? v.FirstOrDefault()
                          : (res.Headers.TryGetValues("Content-Range", out var hv) ? hv.FirstOrDefault() : null);
            int used = 0;
            if (range is not null && range.Split('/') is [_, var countStr] && int.TryParse(countStr, out int c))
                used = c;
            return new StandardUsage(used, AppConfig.DailyDownloadLimit);
        }
        catch
        {
            return null; // decorative, never block on it
        }
    }

    public async Task<SupporterStatus?> GetSupporterStatusAsync(CancellationToken ct = default)
    {
        try
        {
            var res = await SendAsync(HttpMethod.Get, "/api/me/supporter-status", ct);
            return await ReadJsonAsync<SupporterStatus>(res, ct);
        }
        catch { return null; }
    }

    public async Task<DlcInfo?> GetDlcInfoAsync(string appid, string baseAppId, CancellationToken ct = default)
    {
        var res = await SendAsync(HttpMethod.Get, $"/api/dlc/info?appid={appid}&base={baseAppId}", ct);
        return await ReadJsonAsync<DlcInfo>(res, ct);
    }

    public Task<DownloadedFile> DownloadManifestAsync(
        string appid, string source, string? gameName, IProgress<double?>? progress, CancellationToken ct = default)
    {
        string url = $"/api/manifest/download?appid={appid}&source={Uri.EscapeDataString(source)}";
        if (!string.IsNullOrEmpty(gameName)) url += $"&game_name={Uri.EscapeDataString(gameName)}";
        return DownloadFileAsync(url, $"{appid}.zip", progress, ct);
    }

    public Task<DownloadedFile> GenerateDlcAsync(
        string appid, string baseAppId, string? gameName, IProgress<double?>? progress, CancellationToken ct = default)
    {
        string url = $"/api/dlc/generate?appid={appid}&base={baseAppId}";
        if (!string.IsNullOrEmpty(gameName)) url += $"&game_name={Uri.EscapeDataString(gameName)}";
        return DownloadFileAsync(url, $"{appid}.lua", progress, ct);
    }

    // ── Denuvo fixes ────────────────────────────────────────────────

    /// <summary>Public. Every game that has at least one Denuvo fix, plus the tag catalogue.</summary>
    public async Task<DenuvoListingsResponse?> GetDenuvoListingsAsync(CancellationToken ct = default)
    {
        var res = await _http.GetAsync("/api/denuvo/listings", ct);
        if (!res.IsSuccessStatusCode) return null;
        return await ReadJsonAsync<DenuvoListingsResponse>(res, ct);
    }

    /// <summary>Public. One game's fixes (id/title/desc/tags + which download slots exist).</summary>
    public async Task<DenuvoFixesResponse?> GetDenuvoFixesAsync(string appid, CancellationToken ct = default)
    {
        var res = await _http.GetAsync($"/api/denuvo/fixes?appid={Uri.EscapeDataString(appid)}", ct);
        if (!res.IsSuccessStatusCode) return null; // 404 = no fixes for this appid
        return await ReadJsonAsync<DenuvoFixesResponse>(res, ct);
    }

    /// <summary>
    /// Public: the most recently-published Denuvo fixes across ALL games. There's no /api/denuvo feed
    /// endpoint, so this reads the (anon-readable) Supabase `denuvo_fixes` table directly via PostgREST,
    /// the same way <see cref="GetStandardUsageAsync"/> reads `user_downloads`. Returns the newest
    /// <paramref name="limit"/> fixes, each with its game name/header and tags embedded. Null on failure.
    /// </summary>
    public async Task<List<DenuvoRecentFix>?> GetRecentDenuvoFixesAsync(
        int limit = 25, CancellationToken ct = default)
    {
        try
        {
            // PostgREST select with embedded resources (to-one denuvo_games; to-many denuvo_fix_tags,
            // each carrying its denuvo_tags). Bounded read; anon RLS permits SELECT on these tables.
            string url = $"{AppConfig.SupabaseUrl}/rest/v1/denuvo_fixes" +
                         "?select=id,appid,title,description,created_at," +
                         "denuvo_games(name,header_image)," +
                         "denuvo_fix_tags(tag_id,denuvo_tags(id,name,slug,color))" +
                         $"&order=created_at.desc&limit={limit}";

            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("apikey", AppConfig.SupabaseAnonKey);

            using var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return null;
            return await ReadJsonAsync<List<DenuvoRecentFix>>(res, ct);
        }
        catch { return null; } // offline / table locked down → the Recent tab shows its own error state
    }

    /// <summary>
    /// Auth: download a fix's "manifest" or "fix" slot. The endpoint returns a short-lived signed
    /// R2 URL (counts toward 25/day); we then fetch the file from that URL. Caller must be signed in.
    /// </summary>
    public async Task<DownloadedFile> DownloadDenuvoAsync(
        string fixId, string slot, string fallbackName, IProgress<double?>? progress, CancellationToken ct = default)
    {
        // 1. Ask the API for a signed URL (auth + daily-limit gate live here).
        var res = await SendAsync(HttpMethod.Get,
            $"/api/denuvo/download?fix={Uri.EscapeDataString(fixId)}&slot={Uri.EscapeDataString(slot)}", ct);
        var signed = await ReadJsonAsync<DenuvoDownloadResponse>(res, ct);
        if (string.IsNullOrWhiteSpace(signed?.Url))
            throw new ApiException(Resources.Strings.Api_Err_EmptyDownloadLink);

        // 2. Fetch the file from R2 (no auth header: the signed URL carries its own credentials).
        return await DownloadFromUrlAsync(signed.Url, fallbackName, progress, ct);
    }

    // ── Plumbing ────────────────────────────────────────────────────

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        // All callers of SendAsync are login-gated endpoints; the caller ensures the user is signed in.
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await auth.GetValidAccessTokenAsync());

        var res = await _http.SendAsync(req, completion, ct);
        if (res.IsSuccessStatusCode) return res;

        string message = string.Format(Resources.Strings.Api_Err_RequestFailed, (int)res.StatusCode);
        try
        {
            var err = JsonSerializer.Deserialize<ApiError>(await res.Content.ReadAsStringAsync(ct), JsonOpts);
            // Deliberately NOT localized: this text comes from the lua.tools API, which serves English
            // only. There is no key to look up, and a server message is more specific than our generic
            // fallback, so it wins. The fallback above and the 401 case below are the localizable parts.
            if (!string.IsNullOrWhiteSpace(err?.Error)) message = err.Error;
        }
        catch { /* non-JSON error body */ }

        if (res.StatusCode == HttpStatusCode.Unauthorized) message = Resources.Strings.Api_Err_SessionExpired;
        throw new ApiException(message, res.StatusCode);
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage res, CancellationToken ct) =>
        JsonSerializer.Deserialize<T>(await res.Content.ReadAsStringAsync(ct), JsonOpts);

    private async Task<DownloadedFile> DownloadFileAsync(
        string url, string fallbackName, IProgress<double?>? progress, CancellationToken ct)
    {
        var res = await SendAsync(HttpMethod.Get, url, ct, HttpCompletionOption.ResponseHeadersRead);
        return await SaveResponseAsync(res, fallbackName, progress, ct);
    }

    /// <summary>Download a file from an absolute URL with NO auth header (e.g. a signed R2 link).</summary>
    private async Task<DownloadedFile> DownloadFromUrlAsync(
        string url, string fallbackName, IProgress<double?>? progress, CancellationToken ct)
    {
        // New request (not via SendAsync) so no Bearer header and the absolute URL isn't prefixed.
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
            throw new ApiException(string.Format(Resources.Strings.Api_Err_DownloadFailed, (int)res.StatusCode), res.StatusCode);
        return await SaveResponseAsync(res, fallbackName, progress, ct);
    }

    private async Task<DownloadedFile> SaveResponseAsync(
        HttpResponseMessage res, string fallbackName, IProgress<double?>? progress, CancellationToken ct)
    {
        string fileName = res.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? fallbackName;
        foreach (char c in Path.GetInvalidFileNameChars()) fileName = fileName.Replace(c, '_');

        string folder = InterimDownloadsFolder;
        Directory.CreateDirectory(folder);
        string filePath = Path.Combine(folder, fileName);

        long? total = res.Content.Headers.ContentLength;
        await using var src = await res.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(filePath);

        var buffer = new byte[81920];
        long written = 0;
        int read;
        while ((read = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct);
            written += read;
            progress?.Report(total is > 0 ? (double)written / total.Value : null);
        }

        return new DownloadedFile(filePath, fileName);
    }
}
