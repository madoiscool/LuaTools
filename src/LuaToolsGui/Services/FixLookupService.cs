namespace LuaToolsGui.Services;

/// <summary>What the fix listing knows about one game: how many fixes, and what kind they are.</summary>
/// <param name="Tags">
/// The fixes' categories as the listing names them ("Online Fix", "Ubisoft", …). Worth showing,
/// because the endpoint is called "denuvo" but the listing is overwhelmingly not: of the games it
/// returns, the vast majority are Online Fixes and no tag named "Denuvo" exists at all.
/// </param>
public record GameFixSummary(int Count, IReadOnlyList<string> Tags);

/// <summary>
/// Answers one question for the Add page: does this game have fixes, how many, and of what kind?
///
/// <para>
/// The listing endpoint returns every game that has a fix in one shot, so it is fetched once per
/// session and kept as an appid → summary map. That keeps "did the game I just fetched have a fix?"
/// free after the first lookup, instead of a per-game request while the user types.
/// </para>
///
/// <para>
/// Best-effort by design: no listing (offline, API down) means no banner, never an error. A failed
/// load isn't cached, so the next lookup tries again.
/// </para>
/// </summary>
public class FixLookupService(LuaToolsApiClient api)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyDictionary<long, GameFixSummary>? _summaries;

    /// <summary>What the listing has for this appid, or null when it has nothing for it — which also
    /// covers "the listing couldn't be loaded" (the caller can't tell, and doesn't need to).</summary>
    public async Task<GameFixSummary?> GetFixSummaryAsync(long appId, CancellationToken ct = default)
    {
        var summaries = await EnsureLoadedAsync(ct);
        return summaries is not null && summaries.TryGetValue(appId, out var summary) ? summary : null;
    }

    private async Task<IReadOnlyDictionary<long, GameFixSummary>?> EnsureLoadedAsync(CancellationToken ct)
    {
        if (_summaries is not null) return _summaries;

        await _gate.WaitAsync(ct);
        try
        {
            if (_summaries is not null) return _summaries; // won the race elsewhere

            var listing = await api.GetDenuvoListingsAsync(ct);
            if (listing is null) return null; // don't cache a failure. Retry on the next lookup

            var summaries = new Dictionary<long, GameFixSummary>();
            foreach (var game in listing.Games)
                if (long.TryParse(game.AppId, out long id) && game.FixCount > 0)
                    summaries[id] = new GameFixSummary(
                        game.FixCount,
                        game.Tags.Select(t => t.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList());

            return _summaries = summaries;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return null; // same as above: silence, and try again next time
        }
        finally
        {
            _gate.Release();
        }
    }
}
