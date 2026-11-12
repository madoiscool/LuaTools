using System.IO;
using LuaToolsGui.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// The repair pass end to end, against a throwaway Steam install.
/// </summary>
/// <remarks>
/// This writes files into a user's library, so the parts worth pinning down are the ones that leave
/// something behind: it must not silently overwrite an appmanifest without a backup, must not touch a
/// game Steam already knows, and must not invent an entry for a folder it only thinks is the game.
/// </remarks>
public class InstallStateRepairTests : IDisposable
{
    private const long AppId = 424242;
    private const string InstallDir = "Some Game";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "lt-repair-" + Guid.NewGuid().ToString("N"));
    private readonly SettingsService _settings = new();
    private readonly string? _originalOverride;

    private readonly SteamService _steam;
    private readonly SteamLibraryService _library;
    private readonly InstallStateRepairService _repair;

    private string SteamDir => Path.Combine(_root, "steam");
    private string SteamApps => Path.Combine(SteamDir, "steamapps");
    private string Common => Path.Combine(SteamApps, "common");
    private string Acf => Path.Combine(SteamApps, $"appmanifest_{AppId}.acf");

    public InstallStateRepairTests()
    {
        _originalOverride = _settings.SteamPathOverride;

        Directory.CreateDirectory(Path.Combine(SteamDir, "config", "stplug-in"));
        Directory.CreateDirectory(Common);
        File.WriteAllText(Path.Combine(SteamApps, "libraryfolders.vdf"),
            "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"" + SteamDir.Replace(@"\", @"\\") +
            "\"\n\t\t\"apps\"\n\t\t{\n\t\t}\n\t}\n}\n");

        _settings.SteamPathOverride = SteamDir;
        _steam = new SteamService(_settings);
        _library = new SteamLibraryService(_steam);
        _repair = new InstallStateRepairService(_steam, _library, NullLogger<InstallStateRepairService>.Instance);
    }

    public void Dispose()
    {
        _settings.SteamPathOverride = _originalOverride;
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    /// <summary>A game folder under the name Steam has no record of, with its exe in place.</summary>
    private string GameFolder(string name = InstallDir + "s")
    {
        string dir = Path.Combine(Common, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "game.exe"), "MZ");
        File.WriteAllText(Path.Combine(dir, "data.pak"), new string('x', 512));
        return dir;
    }

    private InstallStateRepairResult Run() => _repair.RepairCore(AppId, InstallDir, "game.exe", "Some Game");

    [Fact]
    public void AdoptsTheFolderUnderItsRealName()
    {
        GameFolder();

        var result = Run();

        Assert.True(result.Repaired);
        Assert.True(File.Exists(Acf));
        Assert.Contains("\"installdir\"\t\t\"Some Games\"", File.ReadAllText(Acf));
        Assert.Contains("\"StateFlags\"\t\t\"4\"", File.ReadAllText(Acf));
    }

    [Fact]
    public void TheResolvedFolderIsReturned()
    {
        string dir = GameFolder();

        Assert.Equal(dir, Run().InstallDir);
    }

    [Fact]
    public void SizesTheRealFolder()
    {
        GameFolder();

        Run();

        // 512 bytes of pak plus the 2-byte exe.
        Assert.Contains("\"SizeOnDisk\"\t\t\"514\"", File.ReadAllText(Acf));
    }

    [Fact]
    public void AddsTheAppToTheLibraryIndex()
    {
        GameFolder();

        Run();

        string vdf = File.ReadAllText(Path.Combine(SteamApps, "libraryfolders.vdf"));
        Assert.Contains($"\"{AppId}\"", vdf);
    }

    [Fact]
    public void WritesTheAppIdForLaunchesOutsideSteam()
    {
        string dir = GameFolder();

        Run();

        Assert.Equal(AppId.ToString(), File.ReadAllText(Path.Combine(dir, "steam_appid.txt")));
    }

    /// <summary>An orphaned staging folder makes Steam resume a download instead of reconfiguring.</summary>
    [Fact]
    public void ClearsOrphanedStaging()
    {
        GameFolder();
        string staging = Path.Combine(SteamApps, "downloading", AppId.ToString());
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "half.bin"), "x");

        Run();

        Assert.False(Directory.Exists(staging));
    }

    [Fact]
    public void BacksUpAnExistingManifestBeforeReplacingIt()
    {
        GameFolder();
        File.WriteAllText(Acf, "\"AppState\"\n{\n\t\"StateFlags\"\t\t\"1538\"\n}\n");

        var result = Run();
        Assert.True(result.Repaired, result.Reason);

        Assert.NotEmpty(Directory.GetFiles(SteamApps, "*.bak-*"));
        Assert.Contains("\"StateFlags\"\t\t\"4\"", File.ReadAllText(Acf));
    }

    [Fact]
    public void LeavesAnAlreadyInstalledGameAlone()
    {
        GameFolder();
        File.WriteAllText(Acf, "\"AppState\"\n{\n\t\"StateFlags\"\t\t\"4\"\n\t\"installdir\"\t\t\"Some Games\"\n}\n");

        var result = Run();

        Assert.False(result.Repaired);
        Assert.Equal("already-installed", result.Reason);
        Assert.Single(Directory.GetFiles(SteamApps, "appmanifest_*.acf")); // no backup either
    }

    [Fact]
    public void ReportsWhenThereIsNoFolderToAdopt()
    {
        var result = Run();

        Assert.False(result.Repaired);
        Assert.Equal("no-candidate", result.Reason);
        Assert.False(File.Exists(Acf));
    }

    [Fact]
    public void ReportsWhenTheFolderHasNoGameInIt()
    {
        Directory.CreateDirectory(Path.Combine(Common, InstallDir + "s")); // name matches, exe absent

        Assert.Equal("no-candidate", Run().Reason);
    }

    /// <summary>
    /// A shared depot only resolves when the app that owns it lists it. Without this the depot
    /// dependency build aborts and the entry never leaves "update".
    /// </summary>
    [Fact]
    public void RegistersAReferencedSharedDepotOnItsOwner()
    {
        GameFolder();
        string ownerAcf = Path.Combine(SteamApps, "appmanifest_228980.acf");
        File.WriteAllText(ownerAcf,
            "\"AppState\"\n{\n\t\"appid\"\t\t\"228980\"\n\t\"InstalledDepots\"\n\t{\n\t}\n}\n");
        File.WriteAllText(Path.Combine(SteamDir, "config", "stplug-in", $"{AppId}.lua"),
            $"-- Build: 777\naddappid({AppId}, 1, \"aaa\")\naddappid(228988, 1, \"bbb\") -- VC 2019 Redist (Shared from App 228980)\n-- setManifestid(228988, \"6645201662696499616\", 29212173)\n");

        Run();

        Assert.Contains("\"228988\"", File.ReadAllText(ownerAcf));
        Assert.Contains("6645201662696499616", File.ReadAllText(ownerAcf));
    }

    [Fact]
    public void ReadsDepotsAndBuildFromTheLua()
    {
        GameFolder();
        File.WriteAllText(Path.Combine(SteamDir, "config", "stplug-in", $"{AppId}.lua"),
            $"-- Build: 777\naddappid({AppId}, 1, \"aaa\")\naddappid({AppId + 1}, 1, \"bbb\")\n-- setManifestid({AppId + 1}, \"555666\", 1024)\n");

        Run();

        string acf = File.ReadAllText(Acf);
        Assert.Contains("\"manifest\"\t\t\"555666\"", acf);
        Assert.Contains("\"buildid\"\t\t\"777\"", acf);
    }
}
