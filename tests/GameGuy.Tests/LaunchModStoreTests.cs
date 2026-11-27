using System.IO;
using GameGuy.Services.AppInfo;
using Xunit;

namespace GameGuy.Tests;

/// <summary>
/// Tests for the durability layer: the part that makes launch edits survive Steam rewriting
/// appinfo.vdf. Its failure modes are silent: a lost snapshot means "restore original" can't work, and
/// a snapshot overwritten by our own edit means restore puts back the wrong thing.
/// </summary>
public class LaunchModStoreTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), $"launchmods_{Guid.NewGuid():N}");
    private readonly string _path;

    public LaunchModStoreTests()
    {
        Directory.CreateDirectory(_tmp);
        _path = Path.Combine(_tmp, "launchmods.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static List<LaunchOption> Options(params string[] executables) =>
        executables.Select((exe, i) => new LaunchOption
        {
            Index = i.ToString(), Executable = exe, Type = "default", OsList = "windows",
        }).ToList();

    [Fact]
    public void Save_CapturesTheSnapshotAndDesiredState()
    {
        var store = new LaunchModStore(_path);
        var original = Options("Game.exe");
        var desired = Options("Game.exe", "ModLoader.exe");

        store.Save(603750, 42, original, desired);

        var mod = store.Get(603750)!;
        Assert.Equal(42u, mod.ChangeNumber);
        Assert.Single(mod.Original);
        Assert.Equal(2, mod.Desired.Count);
        Assert.Equal("ModLoader.exe", mod.Desired[1].Executable);
    }

    /// <summary>
    /// THE trap: saving a second edit must not re-snapshot. If it did, the "original" would become our
    /// own first edit and restore could never get back to Steam's actual config.
    /// </summary>
    [Fact]
    public void Save_DoesNotOverwriteTheSnapshotOnASecondEdit()
    {
        var store = new LaunchModStore(_path);
        store.Save(603750, 42, Options("Game.exe"), Options("Game.exe", "Mod1.exe"));
        store.Save(603750, 42, Options("Game.exe", "Mod1.exe"), Options("Game.exe", "Mod2.exe"));

        var mod = store.Get(603750)!;
        Assert.Single(mod.Original);
        Assert.Equal("Game.exe", mod.Original[0].Executable);   // still Steam's, not our first edit
        Assert.Equal("Mod2.exe", mod.Desired[1].Executable);
    }

    [Fact]
    public void Store_PersistsAcrossInstances()
    {
        new LaunchModStore(_path).Save(603750, 42, Options("Game.exe"), Options("Game.exe", "Mod.exe"));

        var reopened = new LaunchModStore(_path);
        Assert.False(reopened.IsEmpty);
        Assert.Equal(2, reopened.Get(603750)!.Desired.Count);
    }

    [Fact]
    public void Remove_ForgetsTheGameAndEmptiesTheFile()
    {
        var store = new LaunchModStore(_path);
        store.Save(603750, 42, Options("Game.exe"), Options("Mod.exe"));
        store.Remove(603750);

        Assert.True(store.IsEmpty);
        Assert.Null(store.Get(603750));
        Assert.False(File.Exists(_path));   // nothing worth persisting → no stray file
        Assert.True(new LaunchModStore(_path).IsEmpty);
    }

    /// <summary>
    /// SteamEdit's re-base: when Steam updates an app, the stored snapshot describes a version that no
    /// longer exists, so it's refreshed rather than trusted.
    /// </summary>
    [Fact]
    public void Rebase_RefreshesTheSnapshotWhenSteamUpdatedTheApp()
    {
        var store = new LaunchModStore(_path);
        store.Save(603750, 42, Options("Game.exe"), Options("Game.exe", "Mod.exe"));

        bool rebased = store.Rebase(603750, 99, Options("Game64.exe", "Extra.exe"));

        Assert.True(rebased);
        var mod = store.Get(603750)!;
        Assert.Equal(99u, mod.ChangeNumber);
        Assert.Equal(2, mod.Original.Count);
        Assert.Equal("Game64.exe", mod.Original[0].Executable);
        Assert.Equal(2, mod.Desired.Count);          // our edit is untouched
    }

    [Fact]
    public void Rebase_DoesNothingWhenTheChangeNumberMatches()
    {
        var store = new LaunchModStore(_path);
        store.Save(603750, 42, Options("Game.exe"), Options("Mod.exe"));

        Assert.False(store.Rebase(603750, 42, Options("SOMETHING-ELSE.exe")));
        Assert.Equal("Game.exe", store.Get(603750)!.Original[0].Executable);
    }

    [Fact]
    public void Rebase_IgnoresUnknownApps()
    {
        Assert.False(new LaunchModStore(_path).Rebase(1, 2, Options("x.exe")));
    }

    // ── drift detection ─────────────────────────────────────────────

    [Fact]
    public void Differs_DetectsEveryManagedField()
    {
        var baseline = Options("Game.exe");

        Assert.False(LaunchModStore.Differs(baseline, Options("Game.exe")));
        Assert.True(LaunchModStore.Differs(baseline, Options("Other.exe")));
        Assert.True(LaunchModStore.Differs(baseline, Options("Game.exe", "Extra.exe")));

        var changed = Options("Game.exe");
        changed[0].Arguments = "-novid";
        Assert.True(LaunchModStore.Differs(baseline, changed));

        changed = Options("Game.exe");
        changed[0].BetaKey = "beta";
        Assert.True(LaunchModStore.Differs(baseline, changed));

        changed = Options("Game.exe");
        changed[0].Index = "7";
        Assert.True(LaunchModStore.Differs(baseline, changed));
    }

    [Fact]
    public void CorruptFile_IsPreservedNotSilentlyReset()
    {
        File.WriteAllText(_path, "{ this is not json");

        var store = new LaunchModStore(_path);

        Assert.True(store.IsEmpty);
        Assert.True(File.Exists(_path + ".corrupt"));   // moved aside for recovery, not overwritten
    }
}
