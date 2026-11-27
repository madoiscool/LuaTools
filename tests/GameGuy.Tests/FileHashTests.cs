using System.IO;
using GameGuy.Services;
using Xunit;

namespace GameGuy.Tests;

/// <summary>
/// The hash check a fix revert leans on to decide whether the file on disk is still the one it wrote.
/// </summary>
/// <remarks>
/// The behaviour that matters most here is the one that looks like a bug: <see cref="FileHash.Matches"/>
/// returns TRUE for a null expected hash. Records written before hashing existed have no hashes, and an
/// absent hash has to mean "nothing to check" rather than "check failed" — otherwise every fix applied by
/// an older build would become permanently un-revertable the moment the user updated.
/// </remarks>
public class FileHashTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "lt-hash-" + Guid.NewGuid().ToString("N"));

    public FileHashTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private string Write(string name, string content)
    {
        string p = Path.Combine(_dir, name);
        File.WriteAllText(p, content);
        return p;
    }

    /// <summary>A known vector, so a future "optimisation" can't quietly change the algorithm.</summary>
    [Fact]
    public void Sha256_MatchesTheKnownVectorForAbc()
    {
        string p = Path.Combine(_dir, "abc.bin");
        File.WriteAllBytes(p, "abc"u8.ToArray());

        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", FileHash.Sha256(p));
    }

    [Fact]
    public void Sha256_IsNullForAMissingFile() =>
        Assert.Null(FileHash.Sha256(Path.Combine(_dir, "nope.bin")));

    [Fact]
    public void Sha256_IsNullForNullOrEmptyPath()
    {
        Assert.Null(FileHash.Sha256(null));
        Assert.Null(FileHash.Sha256("  "));
    }

    [Fact]
    public void Sha256_IsReadableWhileAnotherHandleIsOpen()
    {
        string p = Write("locked.bin", "payload");

        // FileShare.ReadWrite: a game or another tool holding the file must not turn into "cannot verify".
        using var hold = new FileStream(p, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        Assert.NotNull(FileHash.Sha256(p));
    }

    [Fact]
    public void Matches_IsTrueForTheSameContent()
    {
        string p = Write("a.bin", "same");
        Assert.True(FileHash.Matches(p, FileHash.Sha256(p)));
    }

    [Fact]
    public void Matches_IsFalseOnceTheContentChanges()
    {
        string p = Write("b.bin", "before");
        string? original = FileHash.Sha256(p);

        File.WriteAllText(p, "after");

        Assert.False(FileHash.Matches(p, original));
    }

    /// <summary>Hex casing is an encoding detail, not a difference in content.</summary>
    [Fact]
    public void Matches_IgnoresHexCase()
    {
        string p = Write("c.bin", "case");
        Assert.True(FileHash.Matches(p, FileHash.Sha256(p)!.ToUpperInvariant()));
    }

    /// <summary>
    /// The compatibility rule, spelled out: no recorded hash means the check passes. Pre-hash fix records
    /// must keep reverting exactly as they did before hashing was added.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Matches_TreatsAnAbsentHashAsNothingToCheck(string? expected)
    {
        string p = Write("d.bin", "whatever");
        Assert.True(FileHash.Matches(p, expected));
    }

    /// <summary>But a real expected hash against a missing file is a genuine mismatch, not a pass.</summary>
    [Fact]
    public void Matches_IsFalseWhenTheFileIsGoneButAHashWasExpected() =>
        Assert.False(FileHash.Matches(
            Path.Combine(_dir, "missing.bin"),
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"));
}
