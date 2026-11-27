using System.IO;
using System.Security.Cryptography;
using System.Text;
using GameGuy.Services;
using GameGuy.Services.AppInfo;
using GameGuy.ViewModels;
using Xunit;

namespace GameGuy.Tests;

/// <summary>
/// Save-button enablement. Both halves of this were shipped broken once: <c>CanSave</c> started as a
/// plain computed property with no change notification (so Save was disabled forever), and the fix for
/// that left it keyed only on "a game loaded" (so Save was enabled forever). It now means "saving would
/// actually change something", which is what these tests pin.
/// </summary>
public class LaunchOptionsViewModelTests : IDisposable
{
    private const int AppId = 648800;

    private readonly string _tmp = Path.Combine(Path.GetTempPath(), $"launchvm_{Guid.NewGuid():N}");
    private readonly LaunchOptionsViewModel _vm;

    public LaunchOptionsViewModelTests()
    {
        Directory.CreateDirectory(_tmp);
        string appInfo = Path.Combine(_tmp, "appinfo.vdf");
        // Deliberately non-contiguous: an app whose indices already have gaps (1,892 real ones do) must
        // not read as dirty on open just because saving would compact them.
        WriteAppInfo(appInfo, [("0", "Raft.exe"), ("2", "Raft_legacy.exe")]);

        var store = new LaunchModStore(Path.Combine(_tmp, "launchmods.json"));
        var service = new LaunchOptionsService(() => appInfo, () => false, () => { }, () => { },
                                               store, Path.Combine(_tmp, "backup"));
        _vm = new LaunchOptionsViewModel(service, new ToastService());
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Save_IsDisabledOnOpen_EvenWhenIndicesHaveGaps()
    {
        await _vm.LoadAsync(AppId, "Raft");

        Assert.Null(_vm.Error);
        Assert.Equal(2, _vm.Entries.Count);
        Assert.False(_vm.CanSave);
    }

    [Fact]
    public async Task Save_EnablesOnAFieldEdit_AndDisablesAgainWhenItsUndone()
    {
        await _vm.LoadAsync(AppId, "Raft");

        _vm.Entries[0].Arguments = "-novid";
        Assert.True(_vm.CanSave);

        _vm.Entries[0].Arguments = "";
        Assert.False(_vm.CanSave);
    }

    [Fact]
    public async Task Save_EnablesOnAddDeleteAndReorder()
    {
        await _vm.LoadAsync(AppId, "Raft");

        _vm.AddEntryCommand.Execute(null);
        Assert.True(_vm.CanSave);

        _vm.DeleteEntryCommand.Execute(null);
        Assert.False(_vm.CanSave);

        _vm.Selected = _vm.Entries[1];
        _vm.MoveUpCommand.Execute(null);
        Assert.True(_vm.CanSave);

        _vm.MoveDownCommand.Execute(null);
        Assert.False(_vm.CanSave);
    }

    [Fact]
    public async Task Save_StaysDisabledWhenAFailedReadLeavesNoState()
    {
        await _vm.LoadAsync(999999, "Nothing");

        Assert.NotNull(_vm.Error);
        Assert.False(_vm.CanSave);
    }

    /// <summary>Minimal but structurally real v29 appinfo containing one app.</summary>
    private static void WriteAppInfo(string path, (string Index, string Exe)[] launches)
    {
        string[] keys =
        [
            "appinfo", "appid", "common", "name", "config", "installdir", "launch",
            "executable", "type", "oslist", "0", "1", "2",
        ];
        var lookup = new Dictionary<string, uint>(StringComparer.Ordinal);
        for (int i = 0; i < keys.Length; i++) lookup[keys[i]] = (uint)i;

        var appinfo = new VdfTable();
        appinfo.Items.Add(new VdfProperty("appid", VdfType.Int32, AppId));

        var common = new VdfTable();
        common.Items.Add(VdfProperty.Text("name", "Raft"));
        appinfo.Items.Add(VdfProperty.Table("common", common));

        var launch = new VdfTable();
        foreach (var (index, exe) in launches)
        {
            var entry = new VdfTable();
            entry.Items.Add(VdfProperty.Text("executable", exe));
            entry.Items.Add(VdfProperty.Text("type", "default"));
            var cfg = new VdfTable();
            cfg.Items.Add(VdfProperty.Text("oslist", "windows"));
            entry.Items.Add(VdfProperty.Table("config", cfg));
            launch.Items.Add(VdfProperty.Table(index, entry));
        }

        var config = new VdfTable();
        config.Items.Add(VdfProperty.Text("installdir", "Raft"));
        config.Items.Add(VdfProperty.Table("launch", launch));
        appinfo.Items.Add(VdfProperty.Table("config", config));

        var root = new VdfTable();
        root.Items.Add(VdfProperty.Table("appinfo", appinfo));

        using var blob = new MemoryStream();
        BinaryVdf.Write(blob, root, k => lookup[k], indexedKeys: true);
        byte[] body = blob.ToArray();

        var meta = new byte[60];
        BitConverter.GetBytes(7777u).CopyTo(meta, 36);
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
}
