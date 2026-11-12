using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// The appmanifest a repair pass writes.
/// </summary>
/// <remarks>
/// Steam reads these fields literally and silently misbehaves on the ones that matter: no
/// <c>FullyInstalled</c> bit means the entry stays an update, and without
/// <c>AutoUpdateBehavior 1</c> the scheduler immediately chases a build the pinned lua was never
/// describing. So the assertions here are about the values being exactly the intended ones, and about
/// the escaping holding for the folder names people actually have.
/// </remarks>
public class InstallStateAcfTests
{
    private static readonly (long Id, string Gid, long Size)[] Depots = [(1, "111", 10), (2, "222", 20)];

    private static string Build(
        string installDir = "ASTRONEER",
        IReadOnlyDictionary<long, long>? shared = null,
        long buildId = 12345) =>
        InstallStateRepairService.BuildAcf(
            99, "ASTRONEER", installDir, Depots, shared ?? new Dictionary<long, long>(), 4096, buildId);

    [Fact]
    public void WritesFullyInstalled_WithNothingLeftToFetch()
    {
        string acf = Build();

        Assert.Contains("\"StateFlags\"\t\t\"4\"", acf);
        Assert.Contains("\"UpdateResult\"\t\t\"0\"", acf);
        Assert.Contains("\"BytesToDownload\"\t\t\"0\"", acf);
        Assert.Contains("\"StagingSize\"\t\t\"0\"", acf);
    }

    [Fact]
    public void PinsAutoUpdateToLaunchTime_SoTheAdoptedBuildIsNotImmediatelyReplaced()
    {
        Assert.Contains("\"AutoUpdateBehavior\"\t\t\"1\"", Build());
    }

    [Fact]
    public void WritesEveryDepotWithItsGidAndSize()
    {
        string acf = Build();

        Assert.Contains("\"1\"", acf);
        Assert.Contains("\"manifest\"\t\t\"111\"", acf);
        Assert.Contains("\"size\"\t\t\"10\"", acf);
        Assert.Contains("\"manifest\"\t\t\"222\"", acf);
    }

    [Fact]
    public void RecordsTheInstallFolderAsItIs_NotARenamedOne()
    {
        Assert.Contains("\"installdir\"\t\t\"Stronghold Crusader Definitive Editions\"", Build("Stronghold Crusader Definitive Editions"));
    }

    /// <summary>A quote or backslash in a folder name would otherwise end the value early.</summary>
    [Fact]
    public void EscapesTheInstallDir()
    {
        string acf = Build(@"Weird \ ""Game""");

        Assert.Contains("Weird \\\\ \\\"Game\\\"", acf);
    }

    [Fact]
    public void WritesSharedDepotsWithTheirOwner()
    {
        string acf = Build(shared: new Dictionary<long, long> { [228988] = 228980 });

        Assert.Contains("\"SharedDepots\"", acf);
        Assert.Contains("\"228988\"\t\t\"228980\"", acf);
    }

    [Fact]
    public void KeepsTheBuildIdFromTheLua()
    {
        Assert.Contains("\"buildid\"\t\t\"12345\"", Build(buildId: 12345));
        Assert.Contains("\"TargetBuildID\"\t\t\"12345\"", Build(buildId: 12345));
    }

    [Fact]
    public void WritesTheRealSize()
    {
        Assert.Contains("\"SizeOnDisk\"\t\t\"4096\"", Build());
    }

    [Fact]
    public void NoDepots_StillProducesAValidManifest()
    {
        string acf = InstallStateRepairService.BuildAcf(
            99, "X", "X", [], new Dictionary<long, long>(), 0, 0);

        Assert.Contains("\"InstalledDepots\"", acf);
        Assert.Contains("\"StateFlags\"\t\t\"4\"", acf);
        Assert.EndsWith("}\n", acf);
    }
}
