using System.IO;
using GameGuy.Services;
using Xunit;

namespace GameGuy.Tests;

/// <summary>
/// Tests for the two pieces of build-switching that fail SILENTLY when they're wrong:
/// <list type="bullet">
/// <item><see cref="LuaFileParser"/> telling an active manifest pin from one that "Auto Update Apps"
/// commented out. Get this wrong and the Builds page reports a lua as pinned to a build when Steam is
/// actually free to update it.</item>
/// <item><see cref="LuaInstaller.BuildIdFromFileName"/>, the ONLY source of build identity. A missed
/// match doesn't error, it just quietly files the download as a plain lua with no build.</item>
/// </list>
/// </summary>
public class LuaBuildTests
{
    private static LuaContents ParseText(string lua, long appId = 386940)
    {
        string path = Path.Combine(Path.GetTempPath(), $"luatest_{Guid.NewGuid():N}.lua");
        try
        {
            File.WriteAllText(path, lua);
            var parsed = LuaFileParser.Parse(path, appId);
            Assert.NotNull(parsed);
            return parsed!;
        }
        finally { try { File.Delete(path); } catch { /* best effort */ } }
    }

    // ── Manifest pins: active vs. commented out ─────────────────────────────────────────────────────

    [Fact]
    public void ActivePin_IsReportedAsPinned()
    {
        var lua = ParseText("""
            addappid(386940)
            addappid(228983, 1, "deadbeef")
            setManifestid(228983, "9931278451102938", 0)
            """);

        Assert.True(lua.HasActivePins);
        var depot = Assert.Single(lua.Entries, e => e.Id == 228983);
        Assert.Equal("9931278451102938", depot.ManifestId);
        Assert.Null(depot.CommentedManifestId);
    }

    /// <summary>
    /// The regression this whole distinction exists for: installing with "Auto Update Apps" on comments
    /// every setManifestid out. Scanning the file as one blob (the old behavior) still matched those
    /// lines, so an auto-updating lua looked pinned to a specific build.
    /// </summary>
    [Fact]
    public void CommentedOutPin_IsNotReportedAsPinned()
    {
        var lua = ParseText("""
            addappid(386940)
            addappid(228983, 1, "deadbeef")
            -- setManifestid(228983, "9931278451102938", 0)
            """);

        Assert.False(lua.HasActivePins);
        Assert.Empty(lua.ActivePins);

        var depot = Assert.Single(lua.Entries, e => e.Id == 228983);
        Assert.Null(depot.ManifestId);
        // Still surfaced separately, so the UI can say "pin present but disabled".
        Assert.Equal("9931278451102938", depot.CommentedManifestId);
        Assert.Equal("9931278451102938", lua.CommentedPins[228983]);
    }

    [Fact]
    public void MixedPins_AreBucketedIndependently()
    {
        var lua = ParseText("""
            addappid(386940)
            addappid(228983, 1, "aa")
            addappid(228985, 1, "bb")
            setManifestid(228983, "111", 0)
            -- setManifestid(228985, "222", 0)
            """);

        Assert.True(lua.HasActivePins);
        Assert.Equal("111", lua.ActivePins[228983]);
        Assert.False(lua.ActivePins.ContainsKey(228985));
        Assert.Equal("222", lua.CommentedPins[228985]);
    }

    /// <summary>A commented-out addappid must still never count as a declared depot.</summary>
    [Fact]
    public void CommentedOutAddAppId_IsIgnored()
    {
        var lua = ParseText("""
            addappid(386940)
            -- addappid(999999, 1, "cc")
            """);

        Assert.DoesNotContain(lua.Entries, e => e.Id == 999999);
    }

    // ── Build id extraction ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("386940_18234567.lua", "18234567")]
    [InlineData("C:\\downloads\\386940_18234567.lua", "18234567")]
    [InlineData("1623730_9.lua", "9")]
    public void BuildIdFromFileName_ReadsTheBuild(string path, string expected) =>
        Assert.Equal(expected, LuaInstaller.BuildIdFromFileName(path));

    [Theory]
    [InlineData("386940.lua")]              // plain lua, no build identity
    [InlineData("386940 (1).lua")]          // browser duplicate suffix
    [InlineData("Steamtools.lua")]
    [InlineData("386940_.lua")]             // trailing separator, no digits
    [InlineData("386940_beta.lua")]         // non-numeric suffix isn't a build id
    public void BuildIdFromFileName_ReturnsNullWithoutABuild(string path) =>
        Assert.Null(LuaInstaller.BuildIdFromFileName(path));

    /// <summary>A build-named file must still resolve its appid, or the install has nowhere to go.</summary>
    [Fact]
    public void AppIdFromFileName_StillWorksForBuildNamedFiles()
    {
        Assert.Equal(386940, LuaInstaller.AppIdFromFileName("386940_18234567.lua"));
        Assert.Equal(386940, LuaInstaller.AppIdFromFileName("386940.lua"));
    }
}
