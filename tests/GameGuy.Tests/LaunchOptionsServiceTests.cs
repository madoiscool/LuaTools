using System.IO;
using System.Security.Cryptography;
using System.Text;
using GameGuy.Services.AppInfo;
using Xunit;

namespace GameGuy.Tests;

/// <summary>
/// End-to-end tests for the launch-option workflow: read → stage → apply → restore, plus the drift
/// detection that makes edits survive Steam rewriting appinfo.vdf.
///
/// <para>
/// Steam process control is INJECTED and stubbed here. The real service kills and relaunches Steam, and
/// a unit test must never be able to do that to the machine it runs on.
/// </para>
/// </summary>
public class LaunchOptionsServiceTests : IDisposable
{
    private const int AppId = 603750;

    private readonly string _tmp = Path.Combine(Path.GetTempPath(), $"launchsvc_{Guid.NewGuid():N}");
    private readonly string _appInfo;
    private readonly LaunchModStore _store;
    private readonly LaunchOptionsService _service;

    private int _stopCalls;
    private int _startCalls;
    private bool _steamRunning;

    public LaunchOptionsServiceTests()
    {
        Directory.CreateDirectory(_tmp);
        _appInfo = Path.Combine(_tmp, "appinfo.vdf");
        WriteAppInfo(_appInfo, [("0", "Game.exe", "windows"), ("1", "nw", "linux")]);

        _store = new LaunchModStore(Path.Combine(_tmp, "launchmods.json"));
        _service = new LaunchOptionsService(
            () => _appInfo,
            () => _steamRunning,
            () => _stopCalls++,
            () => _startCalls++,
            _store,
            Path.Combine(_tmp, "backup"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>Minimal but structurally real v29 appinfo containing one app.</summary>
    private static void WriteAppInfo(string path, (string Index, string Exe, string Os)[] launches)
    {
        string[] keys =
        [
            "appinfo", "appid", "common", "name", "config", "installdir", "launch",
            "executable", "type", "oslist", "0", "1", "2", "3",
        ];
        var lookup = new Dictionary<string, uint>(StringComparer.Ordinal);
        for (int i = 0; i < keys.Length; i++) lookup[keys[i]] = (uint)i;

        var appinfo = new VdfTable();
        appinfo.Items.Add(new VdfProperty("appid", VdfType.Int32, AppId));

        var common = new VdfTable();
        common.Items.Add(VdfProperty.Text("name", "Test Game"));
        appinfo.Items.Add(VdfProperty.Table("common", common));

        var launch = new VdfTable();
        foreach (var (index, exe, os) in launches)
        {
            var entry = new VdfTable();
            entry.Items.Add(VdfProperty.Text("executable", exe));
            entry.Items.Add(VdfProperty.Text("type", "default"));
            var cfg = new VdfTable();
            cfg.Items.Add(VdfProperty.Text("oslist", os));
            entry.Items.Add(VdfProperty.Table("config", cfg));
            launch.Items.Add(VdfProperty.Table(index, entry));
        }

        var config = new VdfTable();
        config.Items.Add(VdfProperty.Text("installdir", "Test Game"));
        config.Items.Add(VdfProperty.Table("launch", launch));
        appinfo.Items.Add(VdfProperty.Table("config", config));

        var root = new VdfTable();
        root.Items.Add(VdfProperty.Table("appinfo", appinfo));

        using var blob = new MemoryStream();
        BinaryVdf.Write(blob, root, k => lookup[k], indexedKeys: true);
        byte[] body = blob.ToArray();

        var meta = new byte[60];
        BitConverter.GetBytes(7777u).CopyTo(meta, 36);   // changeNumber
        SHA1.HashData(body).CopyTo(meta, 40);

        using var f = File.Create(path);
        BinaryVdf.WriteUInt32(f, AppInfoFile.Magic29);
        BinaryVdf.WriteUInt32(f, 1);
        long offsetPos = f.Position;
        f.Write(BitConverter.GetBytes(0L));
        BinaryVdf.WriteInt32(f, AppId);
        BinaryVdf.WriteInt32(f, meta.Length + body.Length);
        f.Write(meta);
        f.Write(body);
        BinaryVdf.WriteInt32(f, 0);
        long table = f.Position;
        BinaryVdf.WriteInt32(f, keys.Length);
        foreach (string k in keys) BinaryVdf.WriteCString(f, Encoding.UTF8.GetBytes(k));
        f.Position = offsetPos;
        f.Write(BitConverter.GetBytes(table));
    }

    // ── read ────────────────────────────────────────────────────────

    [Fact]
    public void Read_ReturnsEntriesInstallDirAndChangeNumber()
    {
        var state = _service.Read(AppId)!;

        Assert.Equal(2, state.Current.Count);
        Assert.Equal("Game.exe", state.Current[0].Executable);
        Assert.Equal("linux", state.Current[1].OsList);
        Assert.Equal("Test Game", state.InstallDir);
        Assert.Equal(7777u, state.ChangeNumber);
        Assert.False(state.IsModded);
    }

    [Fact]
    public void Read_ReturnsNullForAnAppSteamDoesntKnow() => Assert.Null(_service.Read(999999));

    // ── full cycle ──────────────────────────────────────────────────

    [Fact]
    public void StageThenApply_WritesTheEditIntoAppInfo()
    {
        var state = _service.Read(AppId)!;
        var desired = state.Current.Select(o => o.Clone()).ToList();
        desired.Add(new LaunchOption
        {
            Index = LaunchOption.NextIndex(desired),
            Executable = "ModLoader.exe",
            Arguments = "--profile dev",
            Type = "default",
            OsList = "windows",
        });

        _service.Stage(AppId, state.ChangeNumber, state.Current, desired);

        // Staging alone must not touch appinfo: applying is a separate, confirmed step.
        Assert.Equal(2, _service.Read(AppId)!.Current.Count);
        Assert.True(_service.Read(AppId)!.IsModded);

        var result = _service.Apply(
            new Dictionary<int, IReadOnlyList<LaunchOption>> { [AppId] = desired }, restartSteam: true);

        Assert.True(result.Ok);
        var after = _service.Read(AppId)!.Current;
        Assert.Equal(3, after.Count);
        Assert.Equal("ModLoader.exe", after[2].Executable);
        Assert.Equal("--profile dev", after[2].Arguments);
    }

    [Fact]
    public void Apply_ClosesAndRestartsSteamOnlyWhenItWasRunning()
    {
        var state = _service.Read(AppId)!;
        var edits = new Dictionary<int, IReadOnlyList<LaunchOption>> { [AppId] = state.Current };

        _steamRunning = false;
        _service.Apply(edits, restartSteam: true);
        Assert.Equal(0, _stopCalls);
        Assert.Equal(0, _startCalls);

        _steamRunning = true;
        var result = _service.Apply(edits, restartSteam: true);
        Assert.True(result.SteamWasRunning);
        Assert.Equal(1, _stopCalls);
        Assert.Equal(1, _startCalls);

        // …and it stays closed when the caller says so.
        _service.Apply(edits, restartSteam: false);
        Assert.Equal(2, _stopCalls);
        Assert.Equal(1, _startCalls);
    }

    [Fact]
    public void Restore_PutsBackTheSnapshotAndForgetsTheMod()
    {
        var state = _service.Read(AppId)!;
        var desired = new List<LaunchOption>
        {
            new() { Index = "0", Executable = "Replaced.exe", Type = "default", OsList = "windows" },
        };
        _service.Stage(AppId, state.ChangeNumber, state.Current, desired);
        _service.Apply(new Dictionary<int, IReadOnlyList<LaunchOption>> { [AppId] = desired }, false);
        Assert.Single(_service.Read(AppId)!.Current);

        var original = _service.StageRestore(AppId)!;
        _service.Apply(new Dictionary<int, IReadOnlyList<LaunchOption>> { [AppId] = original }, false);
        _store.Remove(AppId);

        var after = _service.Read(AppId)!;
        Assert.Equal(2, after.Current.Count);
        Assert.Equal("Game.exe", after.Current[0].Executable);
        Assert.Equal("nw", after.Current[1].Executable);
        Assert.False(after.IsModded);
    }

    [Fact]
    public void Apply_BacksUpAppInfoFirst()
    {
        var state = _service.Read(AppId)!;
        _service.Apply(new Dictionary<int, IReadOnlyList<LaunchOption>> { [AppId] = state.Current }, false);

        var backups = Directory.GetFiles(Path.Combine(_tmp, "backup"), "appinfo-*.vdf");
        Assert.Single(backups);
    }

    // ── drift ───────────────────────────────────────────────────────

    [Fact]
    public void FindDrifted_IsEmptyWhenNothingIsStaged() => Assert.Empty(_service.FindDrifted());

    [Fact]
    public void FindDrifted_SpotsSteamOverwritingAnAppliedEdit()
    {
        var state = _service.Read(AppId)!;
        var desired = state.Current.Select(o => o.Clone()).ToList();
        desired[0].Arguments = "-novid";
        _service.Stage(AppId, state.ChangeNumber, state.Current, desired);
        _service.Apply(new Dictionary<int, IReadOnlyList<LaunchOption>> { [AppId] = desired }, false);

        Assert.Empty(_service.FindDrifted());               // freshly applied → in sync

        // Steam regenerates appinfo from PICS, wiping our change.
        WriteAppInfo(_appInfo, [("0", "Game.exe", "windows"), ("1", "nw", "linux")]);

        Assert.Equal([AppId], _service.FindDrifted());
    }

    /// <summary>
    /// The drift notice's "Re-apply" button hands back the ids <see cref="LaunchOptionsService.FindDrifted"/>
    /// reported. An app can have been restored between the check and the click, so an id the store no
    /// longer knows is skipped rather than failing the whole batch.
    /// </summary>
    [Fact]
    public void Reapply_WritesStagedEditsBack_AndSkipsAppsNoLongerModded()
    {
        var state = _service.Read(AppId)!;
        var desired = state.Current.Select(o => o.Clone()).ToList();
        desired[0].Arguments = "-novid";
        _service.Stage(AppId, state.ChangeNumber, state.Current, desired);
        _service.Apply(new Dictionary<int, IReadOnlyList<LaunchOption>> { [AppId] = desired }, false);

        // Steam regenerates appinfo from PICS, wiping the edit.
        WriteAppInfo(_appInfo, [("0", "Game.exe", "windows"), ("1", "nw", "linux")]);
        Assert.Equal([AppId], _service.FindDrifted());

        // 999999 was never staged: it must not stop AppId being written.
        var result = _service.Reapply([AppId, 999999], restartSteam: false);

        Assert.True(result.Ok);
        Assert.Equal("-novid", _service.Read(AppId)!.Current[0].Arguments);
        Assert.Empty(_service.FindDrifted());
    }

    [Fact]
    public void Reapply_WithNothingStaged_IsANoOp()
    {
        var result = _service.Reapply([AppId], restartSteam: false);

        Assert.True(result.Ok);
        Assert.Equal(0, _stopCalls);   // never worth closing Steam for an empty batch
    }

    /// <summary>When Steam updates an app, the stored snapshot is re-based on read so a later Restore
    /// writes back the CURRENT shipped config, not one from a version that no longer exists.</summary>
    [Fact]
    public void Read_RebasesTheSnapshotAfterSteamUpdatesTheApp()
    {
        var state = _service.Read(AppId)!;
        _service.Stage(AppId, state.ChangeNumber, state.Current, state.Current);
        Assert.Equal(7777u, _store.Get(AppId)!.ChangeNumber);

        // A new Steam build: different entries AND a different changeNumber.
        WriteAppInfoWithChangeNumber(_appInfo, 8888u);

        _service.Read(AppId);

        var mod = _store.Get(AppId)!;
        Assert.Equal(8888u, mod.ChangeNumber);
        Assert.Equal("Game64.exe", mod.Original[0].Executable);
    }

    private static void WriteAppInfoWithChangeNumber(string path, uint changeNumber)
    {
        WriteAppInfo(path, [("0", "Game64.exe", "windows")]);
        // Patch the changeNumber in place: meta starts 8 bytes into the record, which starts at 16.
        using var f = File.Open(path, FileMode.Open, FileAccess.Write);
        f.Position = 16 + 8 + 36;
        f.Write(BitConverter.GetBytes(changeNumber));
    }
}
