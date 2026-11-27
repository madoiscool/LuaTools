using System.Net;
using System.Net.Http;
using GameGuy.Services;
using Xunit;

namespace GameGuy.Tests;

/// <summary>
/// Caching behaviour of <see cref="SteamDepotInfo"/>. The cache is a session-long singleton, so getting
/// this wrong is invisible in a short run and infuriating in a long one: the Builds page's Refresh button
/// used to redraw everything while still showing manifest data fetched hours earlier, and a single failed
/// lookup stuck to a game until the app was restarted.
/// </summary>
public class SteamDepotInfoCacheTests
{
    private const long AppId = 386940;

    /// <summary>Counts outgoing requests and replies with whatever the test queued.</summary>
    private sealed class StubHandler(Func<HttpResponseMessage> reply) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(reply());
        }
    }

    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK)
    {
        // Both apps the tests ask for are present, so one canned body serves either lookup.
        Content = new StringContent(
            "{\"data\":{" +
            "\"386940\":{\"depots\":{\"228983\":{\"manifests\":{\"public\":{\"gid\":\"999\"}}}}}," +
            "\"999999\":{\"depots\":{\"228983\":{\"manifests\":{\"public\":{\"gid\":\"999\"}}}}}}}"),
    };

    private static HttpResponseMessage ServerError() => new(HttpStatusCode.InternalServerError);

    [Fact]
    public async Task Success_IsCachedForTheSession()
    {
        var stub = new StubHandler(Ok);
        var info = new SteamDepotInfo(stub);

        Assert.NotNull(await info.GetAsync(AppId));
        Assert.NotNull(await info.GetAsync(AppId));
        Assert.NotNull(await info.GetAsync(AppId));

        Assert.Equal(1, stub.Calls);
    }

    /// <summary>What makes the Builds page's Refresh button mean anything.</summary>
    [Fact]
    public async Task Invalidate_ForcesTheNextLookupToRefetch()
    {
        var stub = new StubHandler(Ok);
        var info = new SteamDepotInfo(stub);

        await info.GetAsync(AppId);
        Assert.Equal(1, stub.Calls);

        info.Invalidate(AppId);
        Assert.NotNull(await info.GetAsync(AppId));

        Assert.Equal(2, stub.Calls);
    }

    [Fact]
    public async Task Invalidate_OnlyDropsTheAppItNames()
    {
        var stub = new StubHandler(Ok);
        var info = new SteamDepotInfo(stub);

        await info.GetAsync(AppId);
        await info.GetAsync(999999);
        Assert.Equal(2, stub.Calls);

        info.Invalidate(AppId);
        await info.GetAsync(999999);

        Assert.Equal(2, stub.Calls); // the other app is still cached
    }

    /// <summary>
    /// A failure must not be permanent. Caching it forever meant one blip left a game showing "couldn't
    /// load depot info" for the whole session, with Refresh unable to clear it. The null WAS the
    /// cached answer.
    /// </summary>
    [Fact]
    public async Task Failure_IsRetriedOnceItsTtlHasPassed()
    {
        var now = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        bool failing = true;
        var stub = new StubHandler(() => failing ? ServerError() : Ok());
        var info = new SteamDepotInfo(stub) { UtcNow = () => now };

        Assert.Null(await info.GetAsync(AppId));
        Assert.Null(await info.GetAsync(AppId));
        Assert.Equal(1, stub.Calls);                 // still cached while fresh

        now = now.AddSeconds(61);
        failing = false;

        Assert.NotNull(await info.GetAsync(AppId));  // …and retried once stale
        Assert.Equal(2, stub.Calls);
    }

    /// <summary>The TTL applies to failures only. A success that's an hour old is still served.</summary>
    [Fact]
    public async Task Success_IsNotExpiredByTheFailureTtl()
    {
        var now = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        var stub = new StubHandler(Ok);
        var info = new SteamDepotInfo(stub) { UtcNow = () => now };

        await info.GetAsync(AppId);
        now = now.AddHours(1);

        Assert.NotNull(await info.GetAsync(AppId));
        Assert.Equal(1, stub.Calls);
    }
}
