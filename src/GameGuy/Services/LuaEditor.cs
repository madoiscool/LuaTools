using System.Text.RegularExpressions;

namespace GameGuy.Services;

/// <summary>
/// Toggles individual depot lines in a lua's TEXT, for the Builds page's per-depot switches.
///
/// <para>
/// Both switches work by commenting a line in or out. That's the only mechanism the lua format has,
/// and it's exactly what "Auto Update Apps" already does to manifest pins:
/// </para>
/// <list type="bullet">
/// <item><b>Lock</b>. <c>setManifestid(depot, "…")</c>. Active = pinned to that manifest. Commented out =
/// Steam is free to update the depot.</item>
/// <item><b>Enable</b>. <c>addappid(depot, 1, "key")</c>. Active = the decryption key applies. Commented
/// out = the depot isn't unlocked at all.</item>
/// </list>
///
/// <para>
/// Pure string→string so it's testable without touching Steam, and so a failed edit can never leave a
/// half-written file: the caller writes the returned text in one go.
/// </para>
/// </summary>
public static class LuaEditor
{
    // A line's leading "--" (with optional space), captured so it can be stripped or re-added while
    // preserving the original indentation.
    private const string CommentPrefix = @"^(?<indent>\s*)(?<comment>--\s*)?";

    /// <summary>Matches setManifestid(&lt;depot&gt;, …. Commented or not.</summary>
    private static Regex PinLine(long depotId) =>
        new(CommentPrefix + @"(?<body>setManifestid\s*\(\s*" + depotId + @"\s*[,)])",
            RegexOptions.IgnoreCase);

    /// <summary>Matches addappid(&lt;depot&gt;…. Commented or not, keyed or bare.</summary>
    private static Regex AddAppIdLine(long depotId) =>
        new(CommentPrefix + @"(?<body>addappid\s*\(\s*" + depotId + @"\s*[,)])",
            RegexOptions.IgnoreCase);

    /// <summary>Lock (pin) or unlock a depot by commenting its setManifestid line in/out.</summary>
    public static string SetDepotLocked(string lua, long depotId, bool locked) =>
        Rewrite(lua, PinLine(depotId), active: locked);

    /// <summary>Enable or disable a depot by commenting its addappid (decryption key) line in/out.</summary>
    public static string SetDepotEnabled(string lua, long depotId, bool enabled) =>
        Rewrite(lua, AddAppIdLine(depotId), active: enabled);

    /// <summary>
    /// Comment in/out every line matching <paramref name="line"/>. An id can legitimately appear on more
    /// than one line (e.g. a bare <c>addappid(id)</c> plus a keyed <c>addappid(id, 1, "key")</c>), and
    /// leaving one of them behind would half-apply the toggle, so all matches are rewritten.
    /// </summary>
    private static string Rewrite(string lua, Regex line, bool active)
    {
        // Split on '\n' only, and put it back the same way, so CRLF files keep their '\r' untouched
        // (it rides along at the end of each element).
        var lines = lua.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            var m = line.Match(lines[i]);
            if (!m.Success) continue;

            bool isCommented = m.Groups["comment"].Success;
            if (isCommented == !active) continue; // already in the requested state

            lines[i] = active
                // Uncomment: drop just the leading "--" run, keep everything after it.
                ? m.Groups["indent"].Value + lines[i][(m.Groups["comment"].Index + m.Groups["comment"].Length)..]
                // Comment: insert "--" after the indent.
                : m.Groups["indent"].Value + "--" + lines[i][m.Groups["indent"].Length..];
        }

        return string.Join('\n', lines);
    }
}
