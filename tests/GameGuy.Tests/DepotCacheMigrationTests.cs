using System.Buffers.Binary;
using System.IO;
using GameGuy.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameGuy.Tests;

/// <summary>
/// The one-time rescue of manifests this app used to write into <c>config\depotcache</c>, a folder Steam
/// never reads.
/// </summary>
/// <remarks>
/// Worth testing carefully because it is the rare startup task that MOVES a user's files, silently, with
/// no UI and nothing to confirm. Two failure modes matter more than the happy path:
///
/// <list type="bullet">
/// <item>Moving a corrupt file into the real depotcache is permanent damage, not a retryable error.
/// <c>LuaInstaller</c> skips a destination that already exists, so a bad entry survives every later fetch
/// and that depot can never download again.</item>
/// <item>Overwriting a good file that Steam put there itself would destroy something we did not create.</item>
/// </list>
///
/// So the tests below mostly pin down what the migration REFUSES to do.
/// </remarks>
public class DepotCacheMigrationTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lt-depotcache-" + Guid.NewGuid().ToString("N"));

    private string Legacy => Path.Combine(_root, "config", "depotcache");
    private string Real => Path.Combine(_root, "depotcache");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private DepotCacheMigrationResult Run() =>
        DepotCacheMigrationService.Migrate(Legacy, Real, NullLogger.Instance);

    // ── Fixtures ─────────────────────────────────────────────────────

    /// <summary>
    /// A byte-for-byte minimal real manifest: the 8-byte section header (metadata magic + length)
    /// followed by a protobuf carrying just depot id (field 1) and gid (field 2). This is what
    /// <c>ManifestFile.TryRead</c> actually looks for, so a synthetic one exercises the true gate
    /// without committing a binary fixture or depending on the machine having Steam installed.
    /// </summary>
    private static byte[] ManifestBytes(long depotId, ulong gid)
    {
        var meta = new List<byte>();
        meta.Add(0x08);                 // field 1 (depot id), varint
        meta.AddRange(Varint((ulong)depotId));
        meta.Add(0x10);                 // field 2 (gid), varint
        meta.AddRange(Varint(gid));

        var file = new byte[8 + meta.Count];
        BinaryPrimitives.WriteUInt32LittleEndian(file, 0x1F4812BE);            // metadata magic
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), (uint)meta.Count);
        meta.CopyTo(file, 8);
        return file;
    }

    private static IEnumerable<byte> Varint(ulong v)
    {
        while (v >= 0x80) { yield return (byte)(v | 0x80); v >>= 7; }
        yield return (byte)v;
    }

    private string WriteLegacy(string name, byte[] bytes)
    {
        Directory.CreateDirectory(Legacy);
        string path = Path.Combine(Legacy, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string WriteReal(string name, byte[] bytes)
    {
        Directory.CreateDirectory(Real);
        string path = Path.Combine(Real, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    // ── The happy path ───────────────────────────────────────────────

    /// <summary>A manifest stranded where Steam cannot see it ends up where Steam can.</summary>
    [Fact]
    public void StrandedManifest_IsMovedIntoTheRealFolder()
    {
        byte[] bytes = ManifestBytes(4889481, 4624935753769628021);
        WriteLegacy("4889481_4624935753769628021.manifest", bytes);

        var result = Run();

        Assert.Equal(1, result.Moved);
        Assert.Equal(0, result.Failed);
        Assert.True(File.Exists(Path.Combine(Real, "4889481_4624935753769628021.manifest")));
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(Real, "4889481_4624935753769628021.manifest")));
    }

    /// <summary>Once everything is rescued, the empty legacy folder goes, so later launches skip early.</summary>
    [Fact]
    public void EmptiedLegacyFolder_IsRemoved()
    {
        WriteLegacy("1231561_6402543383554775381.manifest", ManifestBytes(1231561, 6402543383554775381));

        Run();

        Assert.False(Directory.Exists(Legacy));
    }

    /// <summary>Running twice must be indistinguishable from running once.</summary>
    [Fact]
    public void SecondRun_IsANoOp()
    {
        WriteLegacy("632361_5715419509320521739.manifest", ManifestBytes(632361, 5715419509320521739));

        Assert.Equal(1, Run().Moved);

        var again = Run();
        Assert.True(again.IsEmpty);
        Assert.True(File.Exists(Path.Combine(Real, "632361_5715419509320521739.manifest")));
    }

    /// <summary>No legacy folder at all — the normal state — must not even create the real one.</summary>
    [Fact]
    public void MissingLegacyFolder_IsANoOp()
    {
        var result = Run();

        Assert.True(result.IsEmpty);
        Assert.False(Directory.Exists(Real));
    }

    // ── What it refuses to do ────────────────────────────────────────

    /// <summary>
    /// A file Steam already has is never overwritten. The name is content-addressed, so the copy in the
    /// legacy folder is the same bytes anyway — and a write there could fail on a file Steam holds open.
    /// </summary>
    [Fact]
    public void ManifestAlreadyInTheRealFolder_IsNotOverwritten()
    {
        const string name = "2587891_452405881105540002.manifest";
        byte[] real = ManifestBytes(2587891, 452405881105540002);
        WriteReal(name, real);
        WriteLegacy(name, [0xDE, 0xAD, 0xBE, 0xEF]); // deliberately different, to prove it is not copied

        var result = Run();

        Assert.Equal(0, result.Moved);
        Assert.Equal(1, result.AlreadyPresent);
        Assert.Equal(real, File.ReadAllBytes(Path.Combine(Real, name)));
    }

    /// <summary>
    /// A truncated or half-written file must stay put. Promoting it would poison that depot forever,
    /// since the installer skips a destination that already exists.
    /// </summary>
    [Fact]
    public void CorruptFile_IsNotMoved()
    {
        WriteLegacy("702350_7446681591222407330.manifest", [0x00, 0x01, 0x02, 0x03]);

        var result = Run();

        Assert.Equal(0, result.Moved);
        Assert.Equal(1, result.Rejected);
        Assert.True(File.Exists(Path.Combine(Legacy, "702350_7446681591222407330.manifest")));
    }

    /// <summary>
    /// A valid manifest under the wrong name is still wrong: the name is the lookup key, so moving it
    /// would answer a future cache probe with someone else's depot.
    /// </summary>
    [Fact]
    public void ManifestWhoseContentContradictsItsName_IsNotMoved()
    {
        WriteLegacy("991352_48810978170646456.manifest", ManifestBytes(883719, 2758490342177349494));

        var result = Run();

        Assert.Equal(0, result.Moved);
        Assert.Equal(1, result.Rejected);
    }

    [Theory]
    [InlineData("notamanifest.manifest")]      // no separator
    [InlineData("_123.manifest")]              // empty depot
    [InlineData("883719_.manifest")]           // empty gid
    [InlineData("abc_123.manifest")]           // non-numeric depot
    [InlineData("883719_notagid.manifest")]    // non-numeric gid
    public void UnrecognisedName_IsNotMoved(string name)
    {
        WriteLegacy(name, ManifestBytes(883719, 2758490342177349494));

        var result = Run();

        Assert.Equal(0, result.Moved);
        Assert.Equal(1, result.Rejected);
        Assert.True(File.Exists(Path.Combine(Legacy, name)));
    }

    /// <summary>Anything that is not a .manifest is none of this task's business.</summary>
    [Fact]
    public void UnrelatedFiles_AreLeftAlone()
    {
        Directory.CreateDirectory(Legacy);
        File.WriteAllText(Path.Combine(Legacy, "notes.txt"), "keep me");

        var result = Run();

        Assert.True(result.IsEmpty);
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(Legacy, "notes.txt")));
        Assert.True(Directory.Exists(Legacy)); // not empty, so not removed
    }

    /// <summary>
    /// If both paths ever resolved to the same folder, a move would delete the file. Guarded, and pinned
    /// here so nobody removes the guard as dead code.
    /// </summary>
    [Fact]
    public void SameFolderForBoth_IsRefused()
    {
        Directory.CreateDirectory(Real);
        string path = Path.Combine(Real, "883719_2758490342177349494.manifest");
        File.WriteAllBytes(path, ManifestBytes(883719, 2758490342177349494));

        var result = DepotCacheMigrationService.Migrate(Real, Real, NullLogger.Instance);

        Assert.True(result.IsEmpty);
        Assert.True(File.Exists(path));
    }

    /// <summary>A rejected file keeps the folder alive, so nothing silently disappears with it.</summary>
    [Fact]
    public void LeftoversKeepTheLegacyFolder()
    {
        WriteLegacy("702350_7446681591222407330.manifest", [0x00]);                            // rejected
        WriteLegacy("991352_48810978170646456.manifest", ManifestBytes(991352, 48810978170646456)); // moved

        var result = Run();

        Assert.Equal(1, result.Moved);
        Assert.Equal(1, result.Rejected);
        Assert.True(Directory.Exists(Legacy));
        Assert.True(File.Exists(Path.Combine(Legacy, "702350_7446681591222407330.manifest")));
    }
}
