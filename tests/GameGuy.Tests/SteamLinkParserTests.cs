using GameGuy.Services;
using Xunit;

namespace GameGuy.Tests;

/// <summary>
/// Appid extraction for dropped links. The rejects matter more than the accepts here: getting one wrong
/// doesn't fail loudly, it installs a different game than the one dropped.
/// </summary>
public class SteamLinkParserTests
{
    [Theory]
    // SteamDB: the id sits in the same slot on every sub-page.
    [InlineData("https://steamdb.info/app/648800/", 648800)]
    [InlineData("https://steamdb.info/app/648800/depots/", 648800)]
    [InlineData("https://steamdb.info/app/648800/patchnotes/", 648800)]
    [InlineData("http://steamdb.info/app/1/", 1)]
    // Steam store, including the named and age-gated forms.
    [InlineData("https://store.steampowered.com/app/648800/Raft/", 648800)]
    [InlineData("https://store.steampowered.com/app/648800", 648800)]
    [InlineData("https://store.steampowered.com/agecheck/app/648800/", 648800)]
    [InlineData("https://store.steampowered.com/app/648800/Raft/?snr=1_7_7_230_150_1", 648800)]
    // Community.
    [InlineData("https://steamcommunity.com/app/648800/discussions/", 648800)]
    // steam:// verbs.
    [InlineData("steam://store/648800", 648800)]
    [InlineData("steam://install/648800", 648800)]
    public void RecognisedLinks_YieldTheAppId(string text, long expected) =>
        Assert.Equal(expected, SteamLinkParser.AppIdFrom(text));

    [Theory]
    // A DEPOT id, not an app id: installing it would fetch a different game entirely.
    [InlineData("https://steamdb.info/depot/648801/")]
    // A PACKAGE id, likewise.
    [InlineData("https://steamdb.info/sub/12345/")]
    // Bare numbers are deliberately not links; dragged text is too often incidental.
    [InlineData("648800")]
    [InlineData("app 648800")]
    // A composite rungameid (non-Steam shortcut / mod), which is not an appid.
    [InlineData("steam://rungameid/13548193523847888896")]
    // Nothing to find.
    [InlineData("https://example.com/app/648800/")]
    [InlineData("https://steamdb.info/")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void UnrecognisedText_YieldsNull(string? text) =>
        Assert.Null(SteamLinkParser.AppIdFrom(text));

    [Fact]
    public void TextWithSeveralLinks_TakesTheFirst() =>
        Assert.Equal(648800, SteamLinkParser.AppIdFrom(
            "https://steamdb.info/app/648800/\nhttps://steamdb.info/app/220/"));

    /// <summary>Case and scheme vary by where the link was copied from.</summary>
    [Fact]
    public void MatchingIsCaseInsensitive() =>
        Assert.Equal(648800, SteamLinkParser.AppIdFrom("HTTPS://STEAMDB.INFO/APP/648800/"));

    /// <summary>A trailing fragment is common when dragging from an anchor.</summary>
    [Fact]
    public void FragmentsAndTrailingTextAreIgnored() =>
        Assert.Equal(648800, SteamLinkParser.AppIdFrom("https://steamdb.info/app/648800/#section"));
}
