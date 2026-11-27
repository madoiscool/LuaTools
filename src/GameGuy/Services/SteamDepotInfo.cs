using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;

namespace GameGuy.Services;

/// <summary>
/// One depot of an app. If DlcAppId is set, this depot ships files for that DLC. IsShared marks a
/// redistributable pulled from another app (VC++, DirectX). Os/Language only present when declared.
/// PublicManifestId is the manifest Steam currently ships on the public branch. Compare it against a
/// lua's setManifestid pin to tell "pinned to the latest build" from "pinned to an older one".
/// </summary>
public record ContentDepot(long Id, long Size, long? DlcAppId, bool IsShared, string? Os, string? Language,
    string? PublicManifestId = null)
{
    public bool IsDlc => DlcAppId is not null;

    /// <summary>
    /// The app that actually owns this depot's content (<c>depotfromapp</c>), for shared redistributables
    /// like the VC++/DirectX runtimes under app 228980. Null for a game's own depots.
    /// </summary>
    /// <remarks>
    /// A shared depot appears in the consuming game's app-info as a three-field stub — config,
    /// depotfromapp, sharedinstall — with NO manifests block at all, so it has neither a gid nor a size
    /// here. Both live under the owning app, which is why this id has to be kept rather than collapsed
    /// into the <see cref="IsShared"/> bool: it's the only way to go and look them up.
    /// </remarks>
    public long? FromAppId { get; init; }
}

/// <summary>
/// Authoritative depot list for an app, from api.steamcmd.net (same data SteamDB shows). Includes
/// shared redistributables (flagged IsShared) so the UI can bucket them separately. DlcIds is the
/// app's full declared DLC list (extended.listofdlc). Many have no depot (store-only entitlements).
/// LaunchExes are the main Windows game executables (relative paths) from config.launch. The exact
/// exe(s) Steam runs, used to target Steamless DRM removal (empty if the manifest has no launch config).
/// PublicBuildId is the build the public branch is on right now, so the Builds page can show how far
/// behind a pinned lua is (null when the app declares no branches).
/// </summary>
public record AppDepotInfo(long AppId, IReadOnlyList<ContentDepot> Depots, IReadOnlyList<long> DlcIds,
    IReadOnlyList<string> LaunchExes, string? PublicBuildId = null);

public class SteamDepotInfo
{
    /// <summary>
    /// How long a FAILED lookup is remembered. Successes are kept for the whole session. Depot manifests
    /// don't move often enough to re-fetch per navigation, and <see cref="Invalidate"/> covers the case
    /// where they have. A failure is different: caching it forever meant one blip or one 15s timeout gave
    /// that game a permanent "couldn't load depot info", with no way back short of restarting the app.
    /// </summary>
    private static readonly TimeSpan FailureTtl = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<long, (AppDepotInfo? Info, DateTime At)> _cache = new(); // memory-only
    private readonly ConcurrentDictionary<long, Task<AppDepotInfo?>> _inFlight = new();

    public SteamDepotInfo() : this(AppHttp.SharedHandler) { }

    /// <summary>Test seam: swap in a stub handler so caching behaviour can be checked by counting the
    /// requests that actually go out, rather than by hitting the real API.</summary>
    internal SteamDepotInfo(HttpMessageHandler handler) =>
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>Injectable clock so the failure TTL is testable without sleeping.</summary>
    internal Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;

    /// <summary>Fetch + parse the content depots (memory-cached, deduped). Null on failure.</summary>
    public Task<AppDepotInfo?> GetAsync(long appId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(appId, out var hit) &&
            (hit.Info is not null || UtcNow() - hit.At < FailureTtl))
            return Task.FromResult(hit.Info);

        var task = _inFlight.GetOrAdd(appId, id => FetchAsync(id, ct));

        // FetchAsync drops its own de-dupe entry in a finally, which is correct ONLY when it actually
        // suspends: GetOrAdd stores the task after the factory returns, so a fetch that completes
        // synchronously (a stubbed handler, a socket that had the response ready) runs that finally
        // before the entry exists, and the completed task then sits in _inFlight forever. Served in
        // place of every future fetch. Sweep it here, where the entry is known to be stored.
        if (task.IsCompleted) _inFlight.TryRemove(appId, out _);
        return task;
    }

    /// <summary>
    /// Forget this app so the next <see cref="GetAsync"/> re-fetches. This is what makes the Builds
    /// page's Refresh button actually refresh: without it, "Latest build on Steam" stayed frozen at
    /// whatever was fetched the first time that game was opened this session.
    /// </summary>
    public void Invalidate(long appId)
    {
        _cache.TryRemove(appId, out _);
        _inFlight.TryRemove(appId, out _); // a settled de-dupe entry would be re-served as if it were fresh
    }

    private async Task<AppDepotInfo?> FetchAsync(long appId, CancellationToken ct)
    {
        try
        {
            using var res = await _http.GetAsync($"https://api.steamcmd.net/v1/info/{appId}", ct);
            if (!res.IsSuccessStatusCode) return Cache(appId, null);

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty(appId.ToString(), out var app))
                return Cache(appId, null);

            var depots = new List<ContentDepot>();
            if (app.TryGetProperty("depots", out var depotMap) && depotMap.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in depotMap.EnumerateObject())
                {
                    if (!long.TryParse(entry.Name, out long depotId)) continue;   // skip branches/baselanguages/etc.
                    if (entry.Value.ValueKind != JsonValueKind.Object) continue;
                    var v = entry.Value;

                    // Shared redistributable (VC++, DirectX, …). Belongs to another app. Keep it, flagged,
                    // AND keep the owning app id — the manifest can only be resolved from there.
                    bool isShared = v.TryGetProperty("depotfromapp", out var fromEl);
                    long? fromAppId = isShared && long.TryParse(fromEl.GetString(), out long fa) ? fa : null;

                    long? dlcAppId = v.TryGetProperty("dlcappid", out var dlcEl) && long.TryParse(dlcEl.GetString(), out long dlc)
                        ? dlc : null;

                    string? os = null, lang = null;
                    if (v.TryGetProperty("config", out var cfg) && cfg.ValueKind == JsonValueKind.Object)
                    {
                        if (cfg.TryGetProperty("oslist", out var osEl)) os = osEl.GetString();
                        if (cfg.TryGetProperty("dlclanguage", out var lEl)) lang = lEl.GetString();
                        else if (cfg.TryGetProperty("language", out var l2)) lang = l2.GetString();
                    }

                    depots.Add(new ContentDepot(depotId, ReadPublicSize(v), dlcAppId, isShared, os, lang,
                        ReadPublicManifestId(v)) { FromAppId = fromAppId });
                }
            }

            // The public branch's current build id lives alongside the numeric depot entries, under the
            // non-numeric "branches" key the depot loop above skips.
            string? publicBuildId = null;
            if (app.TryGetProperty("depots", out var dm) && dm.ValueKind == JsonValueKind.Object &&
                dm.TryGetProperty("branches", out var branches) &&
                branches.TryGetProperty("public", out var pubBranch) &&
                pubBranch.TryGetProperty("buildid", out var buildEl))
                publicBuildId = buildEl.ValueKind == JsonValueKind.String
                    ? buildEl.GetString()
                    : buildEl.ToString();

            // Full declared DLC list: includes store-only DLC with no depot of their own.
            var dlcIds = new List<long>();
            if (app.TryGetProperty("extended", out var ext) &&
                ext.TryGetProperty("listofdlc", out var listEl) &&
                listEl.GetString() is { } csv)
            {
                foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (long.TryParse(part, out long id)) dlcIds.Add(id);
            }

            var launchExes = ParseLaunchExes(app);

            return Cache(appId, new AppDepotInfo(appId, depots, dlcIds, launchExes, publicBuildId));
        }
        catch (OperationCanceledException) { return null; } // don't poison the cache on cancel
        catch { return Cache(appId, null); }
        finally { _inFlight.TryRemove(appId, out _); }
    }

    /// <summary>The manifest id (gid) the public branch currently ships for this depot, or null.</summary>
    private static string? ReadPublicManifestId(JsonElement depot)
    {
        if (depot.TryGetProperty("manifests", out var m) &&
            m.TryGetProperty("public", out var pub) &&
            pub.TryGetProperty("gid", out var gidEl))
            return gidEl.ValueKind == JsonValueKind.String ? gidEl.GetString() : gidEl.ToString();
        return null;
    }

    private static long ReadPublicSize(JsonElement depot)
    {
        if (depot.TryGetProperty("manifests", out var m) &&
            m.TryGetProperty("public", out var pub) &&
            pub.TryGetProperty("size", out var sizeEl) &&
            long.TryParse(sizeEl.GetString(), out long size))
            return size;
        return 0;
    }

    /// <summary>
    /// The main Windows game executable(s) from config.launch. A launch entry qualifies when it targets
    /// Windows, has NO betakey (excludes dev/QA/Denuvo branch builds), and NO ownsdlc gate (excludes
    /// artbook/OST launchers). type=="default" entries are preferred and listed first. Returns relative
    /// paths (normalized to backslashes). Empty when the manifest has no usable launch config.
    /// </summary>
    private static IReadOnlyList<string> ParseLaunchExes(JsonElement app)
    {
        if (!app.TryGetProperty("config", out var cfg) || cfg.ValueKind != JsonValueKind.Object ||
            !cfg.TryGetProperty("launch", out var launch) || launch.ValueKind != JsonValueKind.Object)
            return [];

        var def = new List<string>();    // type == "default"
        var other = new List<string>();  // any other qualifying entry (older games omit type)
        foreach (var entry in launch.EnumerateObject())
        {
            var e = entry.Value;
            if (e.ValueKind != JsonValueKind.Object) continue;
            if (!e.TryGetProperty("executable", out var exeEl) || exeEl.GetString() is not { Length: > 0 } exe)
                continue;

            bool isWindows = true, hasBeta = false, hasOwnsDlc = false;
            if (e.TryGetProperty("config", out var lc) && lc.ValueKind == JsonValueKind.Object)
            {
                if (lc.TryGetProperty("oslist", out var osEl) && osEl.GetString() is { } os)
                    isWindows = os.Contains("windows", StringComparison.OrdinalIgnoreCase);
                hasBeta = lc.TryGetProperty("betakey", out var bk) && !string.IsNullOrWhiteSpace(bk.GetString());
                hasOwnsDlc = lc.TryGetProperty("ownsdlc", out _);
            }
            if (!isWindows || hasBeta || hasOwnsDlc) continue;

            string norm = exe.Replace('/', '\\');
            bool isDefault = e.TryGetProperty("type", out var tEl) &&
                             string.Equals(tEl.GetString(), "default", StringComparison.OrdinalIgnoreCase);
            (isDefault ? def : other).Add(norm);
        }
        // default entries first, de-duplicated, preserving order.
        return def.Concat(other).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private AppDepotInfo? Cache(long appId, AppDepotInfo? info)
    {
        _cache[appId] = (info, UtcNow());
        return info;
    }
}
