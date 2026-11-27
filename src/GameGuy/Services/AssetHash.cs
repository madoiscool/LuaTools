using System.IO;
using System.Security.Cryptography;

namespace GameGuy.Services;

/// <summary>
/// SHA-256 verification of downloaded GitHub release assets.
/// </summary>
/// <remarks>
/// These two helpers existed as byte-identical private statics in both <c>UnlockerService</c> and
/// <c>PluginInstallerService</c>. Every service that downloads an executable and then runs it needs
/// them, so they live in one place rather than being copied a third and fourth time.
/// </remarks>
internal static class AssetHash
{
    /// <summary>Lowercase hex SHA-256 of a file's contents.</summary>
    public static string OfFile(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    /// <summary>Strip the "sha256:" prefix GitHub puts on asset digests; null if absent.</summary>
    public static string? ParseDigest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest)) return null;
        int colon = digest.IndexOf(':');
        return (colon >= 0 ? digest[(colon + 1)..] : digest).Trim().ToLowerInvariant();
    }

    /// <summary>
    /// True when the file matches the asset's advertised digest. Also true when the asset advertises no
    /// digest at all, so an older release without one is not treated as corrupt.
    /// </summary>
    public static bool Matches(string path, string? assetDigest)
    {
        if (ParseDigest(assetDigest) is not { } want) return true; // nothing to check against
        return OfFile(path).Equals(want, StringComparison.OrdinalIgnoreCase);
    }
}
