using System.IO;
using System.Security.Cryptography;
using System.Text;
using GameGuy.Services.AppInfo;
using Xunit;

namespace GameGuy.Tests;

/// <summary>
/// Tests for the appinfo.vdf codec, built around the three things that corrupt the file SILENTLY.
/// Each one was a real bug in the Python prototype this was ported from:
/// <list type="number">
/// <item>duplicate keys in one object (15 of 176,869 real apps) collapsing if a dictionary is used;</item>
/// <item>non-UTF-8 strings inflating when decoded with replacement;</item>
/// <item>a stale sha1_binary after an edit.</item>
/// </list>
/// </summary>
public class AppInfoCodecTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), $"appinfotest_{Guid.NewGuid():N}");

    public AppInfoCodecTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    // ── a synthetic v29 file containing every trap ──────────────────

    /// <summary>Bytes that are NOT valid UTF-8 (a lone 0xFD continuation), as some old apps carry.</summary>
    private static readonly byte[] InvalidUtf8 = [0x53, 0x74, 0xFD, 0xFE, 0x72];

    private string WriteSampleFile()
    {
        // TRAP 2b: the STRING TABLE itself carries entries that aren't valid UTF-8. Decoding those for
        // lookup and re-encoding on write inflates each bad byte to a 3-byte U+FFFD, which made a
        // no-edit rebuild of the real file come out 12 bytes long. The raw bytes must be written back.
        byte[][] strings =
        [
            .. new[]
            {
                "appinfo", "appid", "common", "name", "config", "launch", "0", "executable",
                "arguments", "oslist", "type", "description", "legacy", "dup",
            }.Select(Encoding.UTF8.GetBytes),
            [(byte)'b', (byte)'a', (byte)'d', 0xFD, (byte)'k'],
        ];

        static VdfTable Launch0()
        {
            var entry = new VdfTable();
            entry.Items.Add(VdfProperty.Text("executable", "Game.exe"));
            entry.Items.Add(VdfProperty.Text("type", "default"));
            var cfg = new VdfTable();
            cfg.Items.Add(VdfProperty.Text("oslist", "windows"));
            entry.Items.Add(VdfProperty.Table("config", cfg));
            return entry;
        }

        var appinfo = new VdfTable();
        appinfo.Items.Add(new VdfProperty("appid", VdfType.Int32, 603750));

        var common = new VdfTable();
        common.Items.Add(VdfProperty.Text("name", "Test Game"));
        // TRAP 2: raw non-UTF-8 bytes.
        common.Items.Add(new VdfProperty("legacy", VdfType.String, InvalidUtf8));
        // TRAP 1: the same key twice in one object.
        common.Items.Add(VdfProperty.Text("dup", "first"));
        common.Items.Add(VdfProperty.Text("dup", "second"));
        // a property whose KEY comes from the non-UTF-8 table entry
        common.Items.Add(VdfProperty.Text(Encoding.UTF8.GetString(strings[^1]), "odd-key"));
        appinfo.Items.Add(VdfProperty.Table("common", common));

        var launch = new VdfTable();
        launch.Items.Add(VdfProperty.Table("0", Launch0()));
        var config = new VdfTable();
        config.Items.Add(VdfProperty.Table("launch", launch));
        appinfo.Items.Add(VdfProperty.Table("config", config));

        var root = new VdfTable();
        root.Items.Add(VdfProperty.Table("appinfo", appinfo));

        var lookup = new Dictionary<string, uint>(StringComparer.Ordinal);
        for (int i = 0; i < strings.Length; i++) lookup[Encoding.UTF8.GetString(strings[i])] = (uint)i;

        using var blob = new MemoryStream();
        BinaryVdf.Write(blob, root, k => lookup[k], indexedKeys: true);
        byte[] body = blob.ToArray();

        var meta = new byte[60];
        BitConverter.GetBytes(1u).CopyTo(meta, 0);            // infoState
        BitConverter.GetBytes(1234u).CopyTo(meta, 4);         // lastUpdated
        BitConverter.GetBytes(99ul).CopyTo(meta, 8);          // picsToken
        Encoding.ASCII.GetBytes("TEXTHASH-DO-NOT-TOUCH").AsSpan(0, 20).CopyTo(meta.AsSpan(16));
        BitConverter.GetBytes(4242u).CopyTo(meta, 36);        // changeNumber
        SHA1.HashData(body).CopyTo(meta, 40);                 // sha1_binary

        string path = Path.Combine(_tmp, "appinfo.vdf");
        using var f = File.Create(path);
        BinaryVdf.WriteUInt32(f, AppInfoFile.Magic29);
        BinaryVdf.WriteUInt32(f, 1);
        long offsetPos = f.Position;
        f.Write(BitConverter.GetBytes(0L));

        BinaryVdf.WriteInt32(f, 603750);
        BinaryVdf.WriteInt32(f, meta.Length + body.Length);
        f.Write(meta);
        f.Write(body);
        BinaryVdf.WriteInt32(f, 0);

        long tableOffset = f.Position;
        BinaryVdf.WriteInt32(f, strings.Length);
        foreach (byte[] s in strings) BinaryVdf.WriteCString(f, s);
        f.Position = offsetPos;
        f.Write(BitConverter.GetBytes(tableOffset));
        return path;
    }

    private static byte[] MetaOf(string path, AppInfoFile file, int appId)
    {
        var entry = file.Entries[appId];
        using var f = File.OpenRead(path);
        f.Position = entry.Offset + 8;
        return BinaryVdf.ReadExactly(f, file.MetaLength);
    }

    // ── the traps ───────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_IsByteIdentical()
    {
        string src = WriteSampleFile();
        string dst = Path.Combine(_tmp, "out.vdf");

        using (var file = new AppInfoFile(src)) file.SaveAs(dst, new Dictionary<int, VdfTable>());

        Assert.Equal(File.ReadAllBytes(src), File.ReadAllBytes(dst));
    }

    /// <summary>A dictionary-backed model silently drops the second copy and shortens the blob.</summary>
    [Fact]
    public void DuplicateKeysInOneObject_BothSurvive()
    {
        using var file = new AppInfoFile(WriteSampleFile());
        var common = file.ReadAppBody(603750)!.GetTable("common")!;

        var dups = common.Items.Where(p => p.Name == "dup").ToList();
        Assert.Equal(2, dups.Count);
        Assert.Equal("first", dups[0].AsText());
        Assert.Equal("second", dups[1].AsText());
    }

    /// <summary>Decoding with replacement turns each bad byte into U+FFFD, which re-encodes to three
    /// bytes. The blob silently changes length. Raw bytes must survive an edit elsewhere.</summary>
    [Fact]
    public void NonUtf8String_SurvivesAnEditToAnotherField()
    {
        string src = WriteSampleFile();
        string dst = Path.Combine(_tmp, "out.vdf");

        using (var file = new AppInfoFile(src))
        {
            var root = file.ReadApp(603750)!;
            var body = root.GetTable("appinfo")!;
            var options = LaunchOption.ReadAll(body);
            options[0].Arguments = "-windowed";
            LaunchOption.WriteAll(body, options);
            file.SaveAs(dst, new Dictionary<int, VdfTable> { [603750] = root });
        }

        using var reread = new AppInfoFile(dst);
        var legacy = reread.ReadAppBody(603750)!.GetTable("common")!.Find("legacy")!;
        Assert.Equal(InvalidUtf8, (byte[])legacy.Value);
    }

    [Fact]
    public void Edit_RestampsBinaryHash_AndPreservesTextHashAndChangeNumber()
    {
        string src = WriteSampleFile();
        string dst = Path.Combine(_tmp, "out.vdf");
        byte[] originalMeta;

        using (var file = new AppInfoFile(src))
        {
            originalMeta = MetaOf(src, file, 603750);
            var root = file.ReadApp(603750)!;
            var body = root.GetTable("appinfo")!;
            var options = LaunchOption.ReadAll(body);
            options[0].Arguments = "-novid";
            LaunchOption.WriteAll(body, options);
            file.SaveAs(dst, new Dictionary<int, VdfTable> { [603750] = root });
        }

        using var reread = new AppInfoFile(dst);
        var entry = reread.Entries[603750];
        byte[] meta = MetaOf(dst, reread, 603750);

        using var f = File.OpenRead(dst);
        f.Position = entry.Offset + 8 + reread.MetaLength;
        byte[] blob = BinaryVdf.ReadExactly(f, entry.Size - reread.MetaLength);

        Assert.Equal(SHA1.HashData(blob), meta[40..60]);              // recomputed
        Assert.NotEqual(originalMeta[40..60], meta[40..60]);          // and actually changed
        Assert.Equal(originalMeta[16..36], meta[16..36]);             // sha1_text untouched
        Assert.Equal(4242u, entry.ChangeNumber);                      // changeNumber not bumped
    }

    // ── launch entries ──────────────────────────────────────────────

    [Fact]
    public void LaunchOptions_ReadWriteRoundTrip()
    {
        string src = WriteSampleFile();
        string dst = Path.Combine(_tmp, "out.vdf");

        using (var file = new AppInfoFile(src))
        {
            var root = file.ReadApp(603750)!;
            var body = root.GetTable("appinfo")!;
            var options = LaunchOption.ReadAll(body);

            Assert.Single(options);
            Assert.Equal("Game.exe", options[0].Executable);
            Assert.Equal("windows", options[0].OsList);

            options.Add(new LaunchOption
            {
                Index = LaunchOption.NextIndex(options),
                Executable = "ModLoader.exe",
                Arguments = "--profile dev",
                WorkingDir = "mods",
                Description = "Play with mods",
                Type = "option1",
                OsList = "windows",
            });
            LaunchOption.WriteAll(body, options);
            file.SaveAs(dst, new Dictionary<int, VdfTable> { [603750] = root });
        }

        using var reread = new AppInfoFile(dst);
        var after = LaunchOption.ReadAll(reread.ReadAppBody(603750)!);

        Assert.Equal(2, after.Count);
        Assert.Equal("1", after[1].Index);
        Assert.Equal("ModLoader.exe", after[1].Executable);
        Assert.Equal("--profile dev", after[1].Arguments);
        Assert.Equal("mods", after[1].WorkingDir);
        Assert.Equal("Play with mods", after[1].Description);
        Assert.Equal("option1", after[1].Type);
        Assert.Equal("windows", after[1].OsList);
    }

    /// <summary>Unused optionals are omitted, matching Steam. `executable` is the only field present in
    /// 100% of real entries, and writing empty keys diverges from every one of them.</summary>
    [Fact]
    public void EmptyOptionalFields_AreOmittedNotWrittenBlank()
    {
        var table = new LaunchOption { Executable = "run.sh", OsList = "linux" }.ToTable();

        Assert.NotNull(table.Find("executable"));
        Assert.Null(table.Find("arguments"));
        Assert.Null(table.Find("workingdir"));
        Assert.Null(table.Find("description"));
        Assert.Equal("linux", table.GetTable("config")!.GetText("oslist"));
    }

    /// <summary>Entries carry keys we don't model (`vacmodulefilename`, `description_loc`); editing one
    /// field must not drop them.</summary>
    [Fact]
    public void UnmodelledKeys_SurviveAnEdit()
    {
        var existing = new VdfTable();
        existing.Items.Add(VdfProperty.Text("executable", "hl.exe"));
        existing.Items.Add(VdfProperty.Text("vacmodulefilename", @"resource\sourceinit.dat"));

        var option = LaunchOption.FromTable("0", existing);
        option.Arguments = "-steam";
        var result = option.ToTable(existing);

        Assert.Equal(@"resource\sourceinit.dat", result.GetText("vacmodulefilename"));
        Assert.Equal("-steam", result.GetText("arguments"));
    }

    /// <summary>
    /// Reordering renumbers the keys, because Steam's running order comes from the key rather than from
    /// file position. The unmodelled keys must follow their OWN entry across the renumber. Matching the
    /// old table by the new index instead of the source index would graft entry 0's extras onto entry 1.
    /// </summary>
    [Fact]
    public void Reorder_RenumbersKeys_AndCarriesEachEntrysUnmodelledKeysWithIt()
    {
        var body = new VdfTable();
        var launch = new VdfTable();
        foreach (var (index, exe, extra) in new[] { ("0", "hl.exe", "a.dat"), ("1", "hl2.exe", "b.dat") })
        {
            var entry = new VdfTable();
            entry.Items.Add(VdfProperty.Text("executable", exe));
            entry.Items.Add(VdfProperty.Text("vacmodulefilename", extra));
            launch.Items.Add(VdfProperty.Table(index, entry));
        }
        body.EnsureTable("config").Items.Add(VdfProperty.Table("launch", launch));

        var options = LaunchOption.ReadAll(body);
        options.Reverse();                  // what Move(-1) does to the list
        LaunchOption.Renumber(options);     // what Save() then does to the keys
        LaunchOption.WriteAll(body, options);

        var after = LaunchOption.ReadAll(body);
        var written = body.GetTable("config")!.GetTable("launch")!;

        Assert.Equal(["0", "1"], after.Select(o => o.Index));
        Assert.Equal("hl2.exe", after[0].Executable);
        Assert.Equal("hl.exe", after[1].Executable);
        Assert.Equal("b.dat", written.GetTable("0")!.GetText("vacmodulefilename"));
        Assert.Equal("a.dat", written.GetTable("1")!.GetText("vacmodulefilename"));
    }

    /// <summary>Gaps are normal (1,892 real apps have non-contiguous launch indices), so a new entry
    /// appends past the highest rather than filling holes or renumbering.</summary>
    [Fact]
    public void NextIndex_AppendsPastTheHighestAndIgnoresGaps()
    {
        var options = new List<LaunchOption>
        {
            new() { Index = "0" }, new() { Index = "2" }, new() { Index = "3" },
        };
        Assert.Equal("4", LaunchOption.NextIndex(options));
        Assert.Equal("0", LaunchOption.NextIndex([]));
    }

    [Fact]
    public void UnknownMagic_IsRejected()
    {
        string path = Path.Combine(_tmp, "bogus.vdf");
        using (var f = File.Create(path))
        {
            BinaryVdf.WriteUInt32(f, 0x07564430);   // a hypothetical v30
            BinaryVdf.WriteUInt32(f, 1);
        }
        Assert.Throws<AppInfoFormatException>(() => new AppInfoFile(path));
    }
}
