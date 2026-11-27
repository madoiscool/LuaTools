using System.Net;
using System.Net.Http;
using GameGuy.Services;
using Xunit;

namespace GameGuy.Tests;

/// <summary>
/// Cloudflare DNS-over-HTTPS resolution, used when a user's ISP DNS-blocks lua.tools.
/// </summary>
/// <remarks>
/// The behaviour that carries the most weight here is what happens when DoH CANNOT answer. Every such
/// path must yield an empty result rather than throwing or returning a wrong address, because the
/// caller treats "empty" as "fall back to the system resolver". A resolver that threw, or that reported
/// NXDOMAIN as if it were an answer, would turn a recoverable lookup into a dead request.
/// </remarks>
public class DohResolverTests
{
    // ── Parsing ──────────────────────────────────────────────────────

    [Fact]
    public void Parse_ReadsARecordsAndTheLowestTtl()
    {
        var (addresses, ttl) = DohResolver.Parse("""
        {"Status":0,"Answer":[
          {"name":"lua.tools.","type":1,"TTL":300,"data":"104.21.0.1"},
          {"name":"lua.tools.","type":1,"TTL":120,"data":"172.67.0.2"}
        ]}
        """);

        Assert.Equal([IPAddress.Parse("104.21.0.1"), IPAddress.Parse("172.67.0.2")], addresses);
        Assert.Equal(TimeSpan.FromSeconds(120), ttl); // lowest wins, so nothing is cached past its life
    }

    /// <summary>
    /// A verbatim live response, captured from <c>https://1.1.1.1/dns-query?name=lua.tools&amp;type=A</c>
    /// on 2026-09-25. Guards the parser against Cloudflare's actual wire shape rather than a hand-made
    /// approximation of it: the extra top-level fields (TC/RD/RA/AD/CD/Question) must all be ignored.
    /// </summary>
    [Fact]
    public void Parse_HandlesARealCloudflareResponse()
    {
        var (addresses, ttl) = DohResolver.Parse(
            """{"Status":0,"TC":false,"RD":true,"RA":true,"AD":false,"CD":false,"Question":[{"name":"lua.tools","type":1}],"Answer":[{"name":"lua.tools","type":1,"TTL":300,"data":"104.21.52.64"},{"name":"lua.tools","type":1,"TTL":300,"data":"172.67.196.53"}]}""");

        Assert.Equal([IPAddress.Parse("104.21.52.64"), IPAddress.Parse("172.67.196.53")], addresses);
        Assert.Equal(TimeSpan.FromSeconds(300), ttl);
    }

    /// <summary>A CNAME is a step in the chain, not an answer. Only the A records it leads to count.</summary>
    [Fact]
    public void Parse_SkipsCnameRecords()
    {
        var (addresses, _) = DohResolver.Parse("""
        {"Status":0,"Answer":[
          {"name":"lua.tools.","type":5,"TTL":300,"data":"target.example.com."},
          {"name":"target.example.com.","type":1,"TTL":300,"data":"104.21.0.1"}
        ]}
        """);

        Assert.Equal([IPAddress.Parse("104.21.0.1")], addresses);
    }

    [Fact]
    public void Parse_ReadsAaaaRecords()
    {
        var (addresses, _) = DohResolver.Parse("""
        {"Status":0,"Answer":[{"name":"lua.tools.","type":28,"TTL":300,"data":"2606:4700::1"}]}
        """);

        Assert.Equal([IPAddress.Parse("2606:4700::1")], addresses);
    }

    /// <summary>
    /// NXDOMAIN (Status 3) is a real DNS answer meaning "no such host". It must still produce nothing,
    /// so the caller falls back instead of treating a negative reply as authoritative.
    /// </summary>
    [Fact]
    public void Parse_TreatsNonZeroStatusAsNoAnswer()
    {
        var (addresses, _) = DohResolver.Parse("""{"Status":3,"Answer":[]}""");
        Assert.Empty(addresses);
    }

    [Theory]
    [InlineData("""{"Status":0}""")]                                              // no Answer at all
    [InlineData("""{"Status":0,"Answer":[]}""")]                                  // empty Answer
    [InlineData("""{"Status":0,"Answer":[{"type":1,"data":"not-an-ip"}]}""")]      // unparseable address
    [InlineData("""{"Status":0,"Answer":[{"type":1}]}""")]                         // no data field
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("")]
    public void Parse_YieldsNothingForUnusableBodies(string json) =>
        Assert.Empty(DohResolver.Parse(json).Addresses);

    // ── Bypass rules ─────────────────────────────────────────────────

    /// <summary>
    /// Names a public resolver cannot or should not answer. Sending these to Cloudflare would add a
    /// round-trip to every local call and leak the name, for an answer that could only be wrong.
    /// </summary>
    [Theory]
    [InlineData("127.0.0.1")]      // the local HTTP server the plugin talks to
    [InlineData("::1")]
    [InlineData("167.235.229.108")] // AppConfig.ManifestBackendUrl is already a bare IP
    [InlineData("localhost")]
    [InlineData("LOCALHOST")]
    [InlineData("printer.local")]
    [InlineData("myhost")]          // single label: intranet, not public DNS
    [InlineData("")]
    [InlineData(null)]
    public void ShouldBypass_IsTrueForLocalAndLiteralHosts(string? host) =>
        Assert.True(DohResolver.ShouldBypass(host));

    [Theory]
    [InlineData("lua.tools")]
    [InlineData("db.lua.tools")]
    [InlineData("api.github.com")]
    [InlineData("hubcapmanifest.com")]
    public void ShouldBypass_IsFalseForRealHosts(string host) =>
        Assert.False(DohResolver.ShouldBypass(host));

    [Fact]
    public async Task ResolveAsync_ReturnsEmptyForABypassedHostWithoutAskingTheNetwork()
    {
        var handler = new StubHandler("""{"Status":0,"Answer":[{"type":1,"TTL":60,"data":"1.2.3.4"}]}""");
        var resolver = new DohResolver(handler);

        Assert.Empty(await resolver.ResolveAsync("localhost"));
        Assert.Equal(0, handler.Calls); // never went out at all
    }

    // ── Caching and failure ──────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_CachesUntilTheTtlExpires()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var handler = new StubHandler("""{"Status":0,"Answer":[{"type":1,"TTL":120,"data":"1.2.3.4"}]}""");
        var resolver = new DohResolver(handler) { UtcNow = () => now };

        Assert.Single(await resolver.ResolveAsync("lua.tools"));
        Assert.Single(await resolver.ResolveAsync("lua.tools"));
        Assert.Equal(1, handler.Calls); // second answer came from cache

        now = now.AddSeconds(121);
        Assert.Single(await resolver.ResolveAsync("lua.tools"));
        Assert.Equal(2, handler.Calls);
    }

    /// <summary>
    /// A TTL of 0 would otherwise mean a DoH round-trip on every single connection. The floor keeps a
    /// short-lived record usable for long enough to be worth having.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_AppliesTheMinimumTtlFloor()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var handler = new StubHandler("""{"Status":0,"Answer":[{"type":1,"TTL":0,"data":"1.2.3.4"}]}""");
        var resolver = new DohResolver(handler) { UtcNow = () => now };

        await resolver.ResolveAsync("lua.tools");
        now = now.AddSeconds(25);
        await resolver.ResolveAsync("lua.tools");

        Assert.Equal(1, handler.Calls); // still cached despite TTL 0
    }

    [Fact]
    public async Task ResolveAsync_ReturnsEmptyWhenTheEndpointIsUnreachable()
    {
        var resolver = new DohResolver(new ThrowingHandler());
        Assert.Empty(await resolver.ResolveAsync("lua.tools")); // no throw: the caller must get to fall back
    }

    [Fact]
    public async Task ResolveAsync_ReturnsEmptyOnAnErrorStatusCode()
    {
        var resolver = new DohResolver(new StubHandler("", HttpStatusCode.ServiceUnavailable));
        Assert.Empty(await resolver.ResolveAsync("lua.tools"));
    }

    /// <summary>A failed lookup must not be cached, or one blip would blind the resolver for minutes.</summary>
    [Fact]
    public async Task ResolveAsync_DoesNotCacheAFailure()
    {
        var handler = new StubHandler("""{"Status":2}""");
        var resolver = new DohResolver(handler);

        await resolver.ResolveAsync("lua.tools");
        await resolver.ResolveAsync("lua.tools");

        Assert.Equal(2, handler.Calls);
    }

    // ── Stubs ────────────────────────────────────────────────────────

    private sealed class StubHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("no route to host");
    }
}
