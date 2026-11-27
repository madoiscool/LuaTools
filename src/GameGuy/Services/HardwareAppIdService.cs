using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameGuy.Services;

/// <summary>
/// Blacklist of Steam "hardware" appids (Steam Deck, Valve Index, controllers, VR headsets) so they don't
/// appear in the Add page's featured strips or search results. The list comes from a community JSON file on
/// GitHub, fetched through <see cref="GithubProxy"/> (so it works in blocked regions) and cached in
/// cache.json with a ~14-day TTL. The in-memory set is seeded from the cache in the ctor, so
/// <see cref="IsBlacklisted"/> is usable immediately (and never throws) even before any network call.
/// Mirrors <see cref="SteamAppListCache"/>'s lazy-load + stale-fallback style.
/// </summary>
public class HardwareAppIdService
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly GithubProxy _gh;
    private readonly CacheService _cache;
    private readonly HashSet<long> _ids;
    private Task? _loadTask;

    public HardwareAppIdService(GithubProxy gh, CacheService cache)
    {
        _gh = gh;
        _cache = cache;
        // Seed from whatever was last cached so the filter works on the very first frame.
        _ids = [.. cache.GetHardwareAppIds()];
    }

    /// <summary>True if this appid is Steam hardware that should be hidden. Synchronous + allocation-free.</summary>
    public bool IsBlacklisted(long appId) => _ids.Contains(appId);

    /// <summary>Ensure the blacklist is reasonably fresh: refetch from GitHub if the cache is stale or empty,
    /// else no-op. Runs at most once per session. Never throws. On failure the seeded/stale set stays.</summary>
    public Task EnsureFreshAsync() => _loadTask ??= RefreshIfStaleAsync();

    private async Task RefreshIfStaleAsync()
    {
        long fetchedAt = _cache.GetHardwareAppIdsFetchedAt();
        bool fresh = fetchedAt > 0 && _ids.Count > 0
            && DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(fetchedAt) < MaxAge;
        if (fresh) return; // cached copy is recent enough

        try
        {
            using var res = await _gh.SendAsync(AppConfig.HardwareAppIdListUrl);
            if (res is null || !res.IsSuccessStatusCode) return; // keep the seeded/stale set

            string body = await res.Content.ReadAsStringAsync();
            var entries = JsonSerializer.Deserialize<List<HardwareApp>>(body, JsonOpts);
            if (entries is null || entries.Count == 0) return; // empty/garbage → don't wipe a good cache

            var ids = entries.Where(e => e.AppId > 0).Select(e => e.AppId).Distinct().ToList();
            _ids.Clear();
            foreach (long id in ids) _ids.Add(id);
            _cache.SaveHardwareAppIds(ids, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
        catch
        {
            // Offline / all mirrors down / parse error → keep the existing set. Best-effort, like the
            // other caches; the blacklist is a nicety, never a hard dependency.
        }
    }

    /// <summary>One entry of the hardware_appid.json array (only the appid matters to us).</summary>
    private sealed record HardwareApp([property: JsonPropertyName("appid")] long AppId);
}
