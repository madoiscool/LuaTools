using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// The local backend is reachable by every web page the user opens. These pin down what it must refuse:
/// a request from another site, a DNS-rebound host name, and a GET aimed at a state-changing route.
/// </summary>
public class HttpServerSecurityTests
{
    [Theory]
    // The app's own HttpClient (CefInjectorService) sends no Origin.
    [InlineData("127.0.0.1:6767", null)]
    [InlineData("localhost:6767", null)]
    [InlineData("127.0.0.1:6767", "")]
    [InlineData("127.0.0.1:6767", "https://store.steampowered.com")]
    [InlineData("127.0.0.1:6767", "https://steamcommunity.com")]
    [InlineData("127.0.0.1:6767", "https://steamloopback.host")]
    public void TrustedCallers_AreAccepted(string host, string? origin) =>
        Assert.True(HttpServerService.IsTrustedRequest(host, origin));

    [Theory]
    // Any other site driving the backend through fetch or a form post.
    [InlineData("127.0.0.1:6767", "https://evil.example")]
    [InlineData("127.0.0.1:6767", "null")]
    // Look-alike and downgraded Steam origins.
    [InlineData("127.0.0.1:6767", "https://store.steampowered.com.evil.example")]
    [InlineData("127.0.0.1:6767", "http://store.steampowered.com")]
    // DNS rebinding: the page's own name resolves to 127.0.0.1 but is still sent as Host.
    [InlineData("rebind.evil.example:6767", null)]
    [InlineData(null, null)]
    [InlineData("127.0.0.1:80", null)]
    public void UntrustedCallers_AreRejected(string? host, string? origin) =>
        Assert.False(HttpServerService.IsTrustedRequest(host, origin));

    [Fact]
    public void Match_ParsesTheAppId() =>
        Assert.True(HttpServerService.Match("POST", "POST", "/remove/730", "/remove/{appid}", out var id) && id == 730);

    [Fact]
    public void Match_RejectsTheWrongMethod() =>
        // An <img src="http://127.0.0.1:6767/remove/730"> is a GET and must not reach the POST route.
        Assert.False(HttpServerService.Match("GET", "POST", "/remove/730", "/remove/{appid}", out _));

    [Theory]
    [InlineData("/remove/abc")]
    [InlineData("/remove/-1")]
    [InlineData("/remove/99999999999999999999")]
    [InlineData("/remove/")]
    [InlineData("/remove/730/extra")]
    public void Match_RejectsMalformedIds(string path) =>
        Assert.False(HttpServerService.Match("POST", "POST", path, "/remove/{appid}", out _));
}
