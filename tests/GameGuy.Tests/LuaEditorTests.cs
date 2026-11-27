using System.IO;
using GameGuy.Services;
using Xunit;

namespace GameGuy.Tests;

/// <summary>
/// Tests for <see cref="LuaEditor"/>. The per-depot Lock/On switches on the Builds page. These rewrite
/// the file Steam loads, so a sloppy match is how you'd silently disable the wrong depot (or the base
/// app, breaking the whole lua).
/// </summary>
public class LuaEditorTests
{
    private const string Lua = """
        addappid(386940, 1, "basekey")
        addappid(228983,0,"aabb")
        setManifestid(228983,"111111111")
        addappid(228985,0,"ccdd")
        --setManifestid(228985,"222222222")
        """;

    private static string[] Lines(string lua) => lua.Replace("\r\n", "\n").Split('\n');

    // ── Lock (setManifestid) ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Unlock_CommentsOutTheActivePin()
    {
        string result = LuaEditor.SetDepotLocked(Lua, 228983, locked: false);

        Assert.Contains("--setManifestid(228983,\"111111111\")", result);
        Assert.DoesNotContain("\nsetManifestid(228983", "\n" + result);
        // The other depot's already-commented pin is untouched.
        Assert.Contains("--setManifestid(228985,\"222222222\")", result);
    }

    [Fact]
    public void Lock_UncommentsAPin()
    {
        string result = LuaEditor.SetDepotLocked(Lua, 228985, locked: true);

        Assert.Contains("setManifestid(228985,\"222222222\")", result);
        Assert.DoesNotContain("--setManifestid(228985", result);
    }

    [Fact]
    public void Lock_IsANoOpWhenAlreadyInThatState()
    {
        Assert.Equal(Lua, LuaEditor.SetDepotLocked(Lua, 228983, locked: true));
        Assert.Equal(Lua, LuaEditor.SetDepotLocked(Lua, 228985, locked: false));
    }

    /// <summary>Round-tripping must return the exact original text. Otherwise the content hash drifts and
    /// a variant stops matching itself.</summary>
    [Fact]
    public void Lock_RoundTripsExactly()
    {
        string off = LuaEditor.SetDepotLocked(Lua, 228983, locked: false);
        Assert.Equal(Lua, LuaEditor.SetDepotLocked(off, 228983, locked: true));
    }

    // ── Enable (addappid) ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Disable_CommentsOutTheDecryptionKeyLine()
    {
        string result = LuaEditor.SetDepotEnabled(Lua, 228983, enabled: false);

        Assert.Contains("--addappid(228983,0,\"aabb\")", result);
        // Only that depot: the others keep their keys.
        Assert.Contains("\naddappid(228985,0,\"ccdd\")", result.Replace("\r\n", "\n"));
        Assert.StartsWith("addappid(386940, 1, \"basekey\")", result);
    }

    [Fact]
    public void Enable_UncommentsTheKeyLine()
    {
        string off = LuaEditor.SetDepotEnabled(Lua, 228983, enabled: false);
        string on = LuaEditor.SetDepotEnabled(off, 228983, enabled: true);

        Assert.Equal(Lua, on);
    }

    /// <summary>An id can appear as both a bare addappid and a keyed one. Leaving either behind would
    /// half-apply the switch.</summary>
    [Fact]
    public void Disable_CommentsOutEveryLineForThatId()
    {
        const string dual = """
            addappid(386940)
            addappid(228983)
            addappid(228983,0,"aabb")
            """;

        string result = LuaEditor.SetDepotEnabled(dual, 228983, enabled: false);

        Assert.Contains("--addappid(228983)", result);
        Assert.Contains("--addappid(228983,0,\"aabb\")", result);
        Assert.Contains("\naddappid(386940)", "\n" + result.Replace("\r\n", "\n"));
    }

    /// <summary>Ids are matched exactly. 228983 must never catch 2289830 or 22898.</summary>
    [Fact]
    public void Toggles_DoNotMatchIdsBySubstring()
    {
        const string similar = """
            addappid(2289830,0,"aa")
            addappid(22898,0,"bb")
            addappid(228983,0,"cc")
            """;

        string result = LuaEditor.SetDepotEnabled(similar, 228983, enabled: false);
        var lines = Lines(result);

        Assert.Equal("addappid(2289830,0,\"aa\")", lines[0]);
        Assert.Equal("addappid(22898,0,\"bb\")", lines[1]);
        Assert.Equal("--addappid(228983,0,\"cc\")", lines[2]);
    }

    [Fact]
    public void Toggles_PreserveIndentation()
    {
        const string indented = "    addappid(228983,0,\"aa\")";

        string off = LuaEditor.SetDepotEnabled(indented, 228983, enabled: false);
        Assert.Equal("    --addappid(228983,0,\"aa\")", off);
        Assert.Equal(indented, LuaEditor.SetDepotEnabled(off, 228983, enabled: true));
    }

    /// <summary>CRLF files must not be silently rewritten to LF. That alone would change every hash.</summary>
    [Fact]
    public void Toggles_PreserveCrlfLineEndings()
    {
        string crlf = "addappid(386940)\r\naddappid(228983,0,\"aa\")\r\n";

        string off = LuaEditor.SetDepotEnabled(crlf, 228983, enabled: false);

        Assert.Contains("\r\n", off);
        Assert.Equal("addappid(386940)\r\n--addappid(228983,0,\"aa\")\r\n", off);
        Assert.Equal(crlf, LuaEditor.SetDepotEnabled(off, 228983, enabled: true));
    }

    [Fact]
    public void Toggles_HandleSpacedCommentStyle()
    {
        const string spaced = "-- addappid(228983,0,\"aa\")";

        string on = LuaEditor.SetDepotEnabled(spaced, 228983, enabled: true);
        Assert.Equal("addappid(228983,0,\"aa\")", on);
    }

    // ── The parser has to keep switched-off depots visible ──────────────────────────────────────────

    /// <summary>
    /// Switching a depot off must not also lose its NAME. The display name is the trailing comment on the
    /// addappid line ('addappid(2784471, …) -- Depot 2784471'); when that capture was skipped for
    /// commented-out lines, toggling a depot renamed its row from "Depot 2784471" to a bare "Depot".
    /// </summary>
    [Fact]
    public void Parser_KeepsTheTrailingNameOfADisabledDepot()
    {
        const string real = """
            addappid(2784470) -- 9 Kings
            addappid(2784471, 1, "aa") -- Depot 2784471
            addappid(2784472, 1, "bb") -- Depot 2784472
            """;

        string off = LuaEditor.SetDepotEnabled(real, 2784471, enabled: false);

        string path = Path.Combine(Path.GetTempPath(), $"luaedit_{Guid.NewGuid():N}.lua");
        try
        {
            File.WriteAllText(path, off);
            var parsed = LuaFileParser.Parse(path, 2784470)!;

            var disabled = Assert.Single(parsed.DisabledEntries, e => e.Id == 2784471);
            Assert.Equal("Depot 2784471", disabled.Comment);
            // The still-enabled one is unaffected.
            Assert.Equal("Depot 2784472", Assert.Single(parsed.Entries, e => e.Id == 2784472).Comment);
        }
        finally { try { File.Delete(path); } catch { /* best effort */ } }
    }

    /// <summary>
    /// A depot the user switched off must still be reported (as disabled), or its row would drop out of
    /// "In lua" on the Builds page and take the switch with it. Leaving no way to switch it back on.
    /// </summary>
    [Fact]
    public void Parser_ReportsDisabledDepotsSeparatelyFromActiveOnes()
    {
        string off = LuaEditor.SetDepotEnabled(Lua, 228983, enabled: false);

        string path = Path.Combine(Path.GetTempPath(), $"luaedit_{Guid.NewGuid():N}.lua");
        try
        {
            File.WriteAllText(path, off);
            var parsed = LuaFileParser.Parse(path, 386940)!;

            Assert.DoesNotContain(parsed.Entries, e => e.Id == 228983);          // no longer active
            Assert.Contains(parsed.DisabledEntries, e => e.Id == 228983);        // but still known
            Assert.Contains(parsed.Entries, e => e.Id == 228985);                // untouched
            Assert.Contains(parsed.Entries, e => e.Id == 386940);
        }
        finally { try { File.Delete(path); } catch { /* best effort */ } }
    }
}
