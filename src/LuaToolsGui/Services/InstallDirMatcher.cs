using System.IO;

namespace LuaToolsGui.Services;

/// <summary>
/// Picks the folder a game is really installed in when nothing in Steam points at it.
/// </summary>
/// <remarks>
/// <para>
/// <c>installdir</c> is an appinfo value, and <see cref="SteamLibraryService.GetInstallDir"/> can only
/// follow what an appmanifest states. A folder that was renamed by hand, unpacked out of a repack, or
/// copied in from another drive never matches it, so the payload sits on disk unreachable: no install
/// row, and Steam re-downloads what is already there.
/// </para>
/// <para>
/// Matching is deliberately conservative — normalized-name equality only, plus a requirement that the
/// app's own launch executable is inside. That keeps sibling folders such as <c>Steamworks Shared</c>
/// from ever being mistaken for a game. No fuzzy distance, no "contains": with a fixed, small
/// candidate list, a wrong match is much more expensive than no match.
/// </para>
/// </remarks>
public static class InstallDirMatcher
{
    /// <summary>
    /// Best candidate folder under <paramref name="candidates"/> for <paramref name="installDir"/>, or
    /// null. <paramref name="launchExe"/> is the appinfo launch executable, relative to the install root
    /// (it can contain subfolders); when null, any executable will do.
    /// </summary>
    public static string? Match(string? installDir, string? launchExe, IEnumerable<string>? candidates)
    {
        if (candidates is null || string.IsNullOrWhiteSpace(installDir)) return null;

        string wanted = Normalize(installDir);
        if (wanted.Length == 0) return null;

        string? best = null;
        int bestScore = 0;

        foreach (string candidate in candidates)
        {
            string name = Path.GetFileName(candidate.TrimEnd(Path.DirectorySeparatorChar));
            if (name.Length == 0) continue;

            int score = string.Equals(name, installDir.Trim(), StringComparison.OrdinalIgnoreCase)
                ? 2
                : string.Equals(Normalize(name), wanted, StringComparison.Ordinal) ? 1 : 0;
            if (score == 0 || score < bestScore) continue;

            if (!HasLaunchExe(candidate, launchExe)) continue;

            // Equal score: keep the first, so the result does not depend on enumeration order beyond it.
            if (score > bestScore) { best = candidate; bestScore = score; }
        }

        return best;
    }

    /// <summary>
    /// Lowercase, single-spaced, minus the decoration people add when they copy a game around: a
    /// trailing <c>s</c> ("Astroneer" vs "Astroneers"), version tails ("v1.2.3", "(1.0)"), and
    /// "copy"/"backup" suffixes. Applied to both sides, so it never makes two different games equal.
    /// </summary>
    internal static string Normalize(string value)
    {
        string s = value.Trim().ToLowerInvariant();
        s = string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        while (true)
        {
            int space = s.LastIndexOf(' ');
            if (space <= 0) break;

            string tail = s[(space + 1)..];
            bool drop =
                tail is "copy" or "kopie" or "copia" or "backup" ||
                VersionTail.IsMatch(tail);
            if (!drop) break;

            s = s[..space].TrimEnd();
        }

        s = s.TrimEnd(' ', '.', '-', '_', '(', ')', '[', ']', ',');
        if (s.Length > 4 && s.EndsWith('s')) s = s[..^1];
        return s;
    }

    /// <summary>"v1.2.3", "(1.0)", "1.0.0.1" — the shapes a version suffix takes in a folder name.</summary>
    private static readonly System.Text.RegularExpressions.Regex VersionTail =
        new(@"^\(?v?[0-9]+(\.[0-9]+)+\)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static bool HasLaunchExe(string dir, string? launchExe)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(launchExe))
                return File.Exists(Path.Combine(dir, launchExe.Replace('/', '\\')));

            return Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly).Any();
        }
        catch { return false; }
    }
}
