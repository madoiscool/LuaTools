using System.IO;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Choosing the folder a game is actually in, for games Steam has no install row for.
/// </summary>
/// <remarks>
/// This decides which directory gets an appmanifest written against it, so a wrong answer is not
/// cosmetic: it points Steam at the wrong folder and the entry has to be repaired again by hand. The
/// tests below are mostly about what the matcher REFUSES — a shared redist folder, a sibling game whose
/// name merely starts the same, a folder with no executable in it.
/// </remarks>
public class InstallDirMatcherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lt-adopt-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    /// <summary>Create a candidate folder, optionally with an exe at a relative path.</summary>
    private string Dir(string name, string? exe = "game.exe")
    {
        string full = Path.Combine(_root, name);
        Directory.CreateDirectory(full);
        if (exe is not null)
        {
            string target = Path.Combine(full, exe);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, "MZ");
        }
        return full;
    }

    private string? Match(string? installDir, string? exe, params string[] dirs) =>
        InstallDirMatcher.Match(installDir, exe, dirs);

    [Fact]
    public void ExactName_Wins()
    {
        string wanted = Dir("ASTRONEER");
        Dir("ASTRONEERs", "other.exe");

        Assert.Equal(wanted, Match("ASTRONEER", "game.exe", Directory.GetDirectories(_root)));
    }

    /// <summary>The actual reported shape: a trailing s on the folder, neither side sanitized for it.</summary>
    [Fact]
    public void TrailingS_StillMatches()
    {
        string wanted = Dir("ASTRONEERs");

        Assert.Equal(wanted, Match("ASTRONEER", "game.exe", Directory.GetDirectories(_root)));
    }

    [Fact]
    public void ExeInSubfolder_Matches()
    {
        string wanted = Dir("Astro", Path.Combine("Binaries", "Win64", "Astro-Win64-Shipping.exe"));

        Assert.Equal(wanted, Match("Astro", "Binaries/Win64/Astro-Win64-Shipping.exe", Directory.GetDirectories(_root)));
    }

    [Fact]
    public void VersionSuffixAndCopyTail_AreIgnored()
    {
        string wanted = Dir("Elden Ring v1.10.1");

        Assert.Equal(wanted, Match("Elden Ring", "game.exe", Directory.GetDirectories(_root)));
    }

    /// <summary>Sibling redistributables sit right next to the games and must never be adopted.</summary>
    [Fact]
    public void UnrelatedFolder_DoesNotMatch()
    {
        Dir("Steamworks Shared");

        Assert.Null(Match("ASTRONEER", "game.exe", Directory.GetDirectories(_root)));
    }

    [Fact]
    public void MatchingNameButNoExe_DoesNotMatch()
    {
        Dir("ASTRONEER", exe: null);

        Assert.Null(Match("ASTRONEER", "game.exe", Directory.GetDirectories(_root)));
    }

    [Fact]
    public void MissingLaunchExe_StillRequiresSomeExe()
    {
        string wanted = Dir("ASTRONEER");
        Dir("ASTRONEER copies", exe: null);

        Assert.Equal(wanted, Match("ASTRONEER", null, Directory.GetDirectories(_root)));
    }

    [Fact]
    public void NoInstallDir_DoesNotMatch()
    {
        Dir("ASTRONEER");

        Assert.Null(Match(null, "game.exe", Directory.GetDirectories(_root)));
        Assert.Null(Match("   ", "game.exe", Directory.GetDirectories(_root)));
    }

    [Fact]
    public void EmptyCandidateList_DoesNotMatch()
    {
        Assert.Null(Match("ASTRONEER", "game.exe", []));
    }

    [Theory]
    [InlineData("ASTRONEERs", "astroneer")]
    [InlineData("  ELDEN   Ring  ", "elden ring")]
    [InlineData("Elden Ring (1.0)", "elden ring")]
    [InlineData("Elden Ring - copy", "elden ring")]
    [InlineData("Astroneer", "astroneer")]
    public void Normalize_FoldsDecoration(string input, string expected) =>
        Assert.Equal(expected, InstallDirMatcher.Normalize(input));
}
