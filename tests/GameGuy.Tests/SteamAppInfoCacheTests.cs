using System.Text.Json;
using GameGuy.Services;
using Xunit;

namespace GameGuy.Tests;

/// <summary>
/// How an appdetails response is matched back to the appid that was requested. This is load-bearing and
/// it is not obvious: since ~Sep 2026 Steam keys the response by a CHILD app's id (a DLC or a soundtrack)
/// for any app that has one, while the inner data.steam_appid still echoes what was asked for. Before
/// that was handled, GetProperty(appid) threw KeyNotFoundException straight into a bare catch, so every
/// popular game silently failed to resolve and the Manage backfill re-requested it forever.
/// </summary>
public class SteamAppInfoCacheTests
{
    /// <summary>The returned element points into the document's buffer, so clone before it is disposed.</summary>
    private static JsonElement? Find(string json, long appid)
    {
        using var doc = JsonDocument.Parse(json);
        return SteamAppInfoCache.FindAppDetailsEntry(doc.RootElement, appid)?.Clone();
    }

    private static string Name(JsonElement? entry) =>
        entry!.Value.GetProperty("data").GetProperty("name").GetString()!;

    /// <summary>The bug itself: 500 ("Left 4 Dead") answers under 1660740 ("Left 4 Dead - Uncensored").</summary>
    [Fact]
    public void ChildKeyedResponse_IsMatchedByInnerSteamAppid()
    {
        var entry = Find("""{"1660740":{"success":true,"data":{"steam_appid":500,"name":"Left 4 Dead"}}}""", 500);

        Assert.NotNull(entry);
        Assert.Equal("Left 4 Dead", Name(entry));
    }

    /// <summary>Apps with no child apps are still keyed normally, and must keep working unchanged.</summary>
    [Fact]
    public void SameKeyedResponse_StillResolves()
    {
        var entry = Find("""{"10":{"success":true,"data":{"steam_appid":10,"name":"Counter-Strike"}}}""", 10);

        Assert.NotNull(entry);
        Assert.Equal("Counter-Strike", Name(entry));
    }

    /// <summary>
    /// A failed lookup has no "data" to anchor on, so it can only ever be found by its key. It has to be
    /// found: that is what writes the "{}" delisted marker that stops the backfill retrying the app.
    /// </summary>
    [Fact]
    public void FailedLookup_IsStillFoundByItsKey()
    {
        var entry = Find("""{"999999999":{"success":false}}""", 999999999);

        Assert.NotNull(entry);
        Assert.False(entry!.Value.GetProperty("success").GetBoolean());
    }

    /// <summary>
    /// The important refusal. There is exactly one entry and it looks plausible, but it describes a
    /// different app. Taking it would write the wrong game's name and art into detailsŀ.json
    /// permanently, so "no match" is the correct answer even though it resolves nothing.
    /// </summary>
    [Fact]
    public void EntryForAnotherApp_IsRefusedRatherThanGuessed() =>
        Assert.Null(Find("""{"1660740":{"success":true,"data":{"steam_appid":1660740,"name":"Uncensored"}}}""", 500));

    /// <summary>An unprovable payload is refused rather than assumed, in every malformed variant.</summary>
    [Theory]
    [InlineData("{}")]                                                             // empty object
    [InlineData("[]")]                                                             // not an object at all
    [InlineData("""{"1660740":{"success":true,"data":{"name":"L4D"}}}""")]          // no steam_appid
    [InlineData("""{"1660740":{"success":true,"data":{"steam_appid":"500"}}}""")]   // string, not number
    [InlineData("""{"1660740":{"success":true}}""")]                                // success but no data
    [InlineData("""{"1660740":"nonsense"}""")]                                      // entry isn't an object
    public void UnprovablePayloads_AreRefused(string json) => Assert.Null(Find(json, 500));

    /// <summary>
    /// Documents the ordering: the direct key wins over the scan, so the cheap path stays the common path
    /// and a sibling entry can never shadow the app's own.
    /// </summary>
    [Fact]
    public void DirectKey_WinsOverTheScan()
    {
        var entry = Find("""
            {"500":{"success":true,"data":{"steam_appid":500,"name":"Left 4 Dead"}},
             "1660740":{"success":true,"data":{"steam_appid":500,"name":"Uncensored"}}}
            """, 500);

        Assert.Equal("Left 4 Dead", Name(entry));
    }

    /// <summary>
    /// If Steam ever keys an entry by our appid but fills it with another app's data, the key is not
    /// trusted either — data.steam_appid is the anchor, not the key.
    /// </summary>
    [Fact]
    public void DirectKeyDescribingAnotherApp_IsRefused() =>
        Assert.Null(Find("""{"500":{"success":true,"data":{"steam_appid":1660740,"name":"Uncensored"}}}""", 500));
}
