using System.IO;
using System.Text.RegularExpressions;
using LuaToolsGui.Services.AppInfo;

namespace LuaToolsGui.Services;

/// <summary>
/// Resolves where a Steam game is installed on disk by walking Valve's KeyValues files:
/// registry Steam root → steamapps\libraryfolders.vdf (every library, possibly across drives) →
/// per-library steamapps\appmanifest_&lt;appid&gt;.acf (the game's installdir) → common\&lt;installdir&gt;.
/// Best-effort: returns null if Steam/the game can't be located. Used to apply Denuvo "fix" zips.
/// </summary>
public partial class SteamLibraryService(SteamService steam)
{
    // "path"        "D:\\SteamLibrary"     → the library root (libraryfolders.vdf)
    // "installdir"  "Elden Ring"           → folder under steamapps\common (appmanifest_*.acf)
    // Values are quoted; backslashes are escaped (\\). One key per line.
    [GeneratedRegex(@"""path""\s*""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex PathRegex();

    [GeneratedRegex(@"""installdir""\s*""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex InstallDirRegex();

    [GeneratedRegex(@"""appid""\s*""(\d+)""", RegexOptions.IgnoreCase)]
    private static partial Regex AppIdRegex();

    [GeneratedRegex(@"""name""\s*""([^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex NameRegex();

    /// <summary>An installed game, as its appmanifest describes it.</summary>
    public record InstalledGame(long AppId, string Name, string InstallDir);

    /// <summary>
    /// Full path to the game's install folder (…\steamapps\common\&lt;installdir&gt;) if it exists on
    /// disk, else null (game not installed / Steam not found / unreadable).
    /// </summary>
    /// <remarks>
    /// Follows the appmanifest first. When none describes the game it falls back to
    /// <see cref="GetInstallDirFallback"/>: a copied or unpacked folder is on disk whether or not Steam
    /// was ever told about it, and callers care about the files, not the paperwork.
    /// </remarks>
    public string? GetInstallDir(long appId)
    {
        try
        {
            string? steamRoot = steam.EffectivePath;
            if (steamRoot is null) return null;

            foreach (string library in GetLibraryRoots(steamRoot))
            {
                string acf = Path.Combine(library, "steamapps", $"appmanifest_{appId}.acf");
                if (!File.Exists(acf)) continue;

                var m = InstallDirRegex().Match(File.ReadAllText(acf));
                if (!m.Success) continue;

                string installDir = Unescape(m.Groups[1].Value);
                string full = Path.Combine(library, "steamapps", "common", installDir);
                if (Directory.Exists(full)) return full;
            }

            // No appmanifest described it. Before giving up, look for the folder itself: the files being
            // on disk under a name nobody recorded is not the same as the game being absent.
            return GetInstallDirFallback(appId);
        }
        catch { /* unreadable VDF/ACF or odd path. Treat as not found */ }
        return null;
    }

    /// <summary>
    /// Every installed game across every library, from the appmanifests. Lazy, and skips anything whose
    /// folder isn't actually on disk.
    /// </summary>
    /// <remarks>
    /// Enumerating the .acf files is the cheap part (~39 ms across three drives here); what costs is
    /// whatever the caller then does per game. Yields rather than returning a list so a caller looking
    /// for one thing can stop early.
    /// </remarks>
    public IEnumerable<InstalledGame> EnumerateInstalled()
    {
        string? steamRoot = steam.EffectivePath;
        if (steamRoot is null) yield break;

        foreach (string library in GetLibraryRoots(steamRoot))
        {
            string steamapps = Path.Combine(library, "steamapps");
            string[] acfs;
            try
            {
                if (!Directory.Exists(steamapps)) continue;
                acfs = Directory.GetFiles(steamapps, "appmanifest_*.acf");
            }
            catch { continue; }

            foreach (string acf in acfs)
            {
                InstalledGame? game = null;
                try
                {
                    string text = File.ReadAllText(acf);

                    var idm = AppIdRegex().Match(text);
                    var dirm = InstallDirRegex().Match(text);
                    if (!idm.Success || !dirm.Success) continue;
                    if (!long.TryParse(idm.Groups[1].Value, out long appId)) continue;

                    string full = Path.Combine(steamapps, "common", Unescape(dirm.Groups[1].Value));
                    if (!Directory.Exists(full)) continue;

                    var nm = NameRegex().Match(text);
                    game = new InstalledGame(appId, nm.Success ? nm.Groups[1].Value : appId.ToString(), full);
                }
                catch { /* unreadable or malformed acf: skip this one, not the whole library */ }

                if (game is not null) yield return game;
            }
        }
    }

    /// <summary>Every Steam library root (the main install plus any added libraries).</summary>
    public static IEnumerable<string> GetLibraryRoots(string steamRoot)
    {
        // The main install is always a library.
        yield return steamRoot;

        string vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;

        string text;
        try { text = File.ReadAllText(vdf); } catch { yield break; }

        foreach (Match m in PathRegex().Matches(text))
        {
            string path = Unescape(m.Groups[1].Value);
            // The main root often appears here too; harmless duplicate (we just probe each).
            if (!string.Equals(path, steamRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(path))
                yield return path;
        }
    }

    /// <summary>A game folder that Steam has no install row for, but which is plainly the game.</summary>
    /// <param name="SteamAppsDir">The library's <c>steamapps</c> folder, where an appmanifest would go.</param>
    /// <param name="FullPath">The install folder itself.</param>
    public sealed record AdoptCandidate(string SteamAppsDir, string FullPath);

    /// <summary>
    /// The folder this app is sitting in with no appmanifest pointing at it, if there is one.
    /// </summary>
    /// <remarks>
    /// Only reached when the manifest walk found nothing, which is the copy/repack case: the files are
    /// there under a name an installdir does not agree with. The matching rule lives in
    /// <see cref="InstallDirMatcher"/>; this only supplies the candidates.
    /// </remarks>
    public AdoptCandidate? FindAdoptable(string? installDir, string? launchExe)
    {
        if (steam.EffectivePath is not { } steamRoot) return null;

        foreach (string library in GetLibraryRoots(steamRoot))
        {
            string steamapps = Path.Combine(library, "steamapps");
            string common = Path.Combine(steamapps, "common");

            string[] dirs;
            try
            {
                if (!Directory.Exists(common)) continue;
                dirs = Directory.GetDirectories(common);
            }
            catch { continue; }

            if (InstallDirMatcher.Match(installDir, launchExe, dirs) is { } match)
                return new AdoptCandidate(steamapps, match);
        }

        return null;
    }

    /// <summary>
    /// The install folder if it exists on disk. Falls back to the appinfo installdir when no appmanifest
    /// describes the game, so a folder Steam has not been told about yet still resolves.
    /// </summary>
    public string? GetInstallDirFallback(long appId)
    {
        try
        {
            if (steam.AppInfoPath is not { } appInfoPath || !File.Exists(appInfoPath)) return null;

            using var appInfo = new AppInfoFile(appInfoPath);
            return appInfo.ReadAppBody((int)appId) is { } body
                ? FindAdoptable(
                    body.GetTable("config")?.GetText("installdir"),
                    LaunchOption.ReadAll(body).FirstOrDefault()?.Executable)?.FullPath
                : null;
        }
        catch { return null; }
    }

    /// <summary>VDF strings escape backslashes as "\\"; collapse to a real path.</summary>
    private static string Unescape(string s) => s.Replace(@"\\", @"\");
}
