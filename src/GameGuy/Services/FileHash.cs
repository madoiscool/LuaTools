using System.IO;
using System.Security.Cryptography;

namespace GameGuy.Services;

/// <summary>
/// SHA-256 of a file, as lowercase hex, or null when it cannot be read.
/// </summary>
/// <remarks>
/// Used to prove a file on disk is still the one a fix wrote. SHA-256 rather than something faster
/// because it is in the BCL (no dependency), hardware-accelerated on every CPU this app targets
/// (measured ~2.1 GB/s here), and only ever runs over the handful of files a fix actually touches —
/// never the whole game. Disk read dominates, so a cheaper hash would buy nothing measurable.
///
/// Null on failure, never an exception: a locked or vanished file must degrade to "cannot verify" and
/// let the caller decide, not abort a revert halfway.
/// </remarks>
public static class FileHash
{
    /// <summary>SHA-256 as lowercase hex, or null if the file is missing or unreadable.</summary>
    public static string? Sha256(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            using var fs = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.SequentialScan);
            return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        }
        catch { return null; }
    }

    /// <summary>
    /// True when <paramref name="path"/> hashes to <paramref name="expected"/>.
    /// </summary>
    /// <returns>
    /// <b>True when <paramref name="expected"/> is null or empty</b> — an absent hash means the record
    /// predates hashing, which is "nothing to check", not "check failed". Callers rely on this to keep
    /// reverting fixes applied by older builds.
    /// </returns>
    public static bool Matches(string? path, string? expected)
    {
        if (string.IsNullOrEmpty(expected)) return true;
        return string.Equals(Sha256(path), expected, StringComparison.OrdinalIgnoreCase);
    }
}
