using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace GameGuy.Services;

/// <summary>
/// What a Steam <c>.manifest</c> tells us without contacting Steam: the depot it belongs to, its true
/// uncompressed size, and whether its filenames are encrypted.
/// </summary>
/// <param name="SizeOnDisk">
/// <c>cb_disk_original</c> — the size the depot occupies once installed. This is the authoritative
/// number: app info's size can be absent entirely (a token-gated app returns no depot list at all), and
/// the manifest is already on disk by the time a download is budgeted.
/// </param>
/// <param name="FilenamesEncrypted">
/// Whether the payload's filenames are still encrypted with the depot key. Usually FALSE — Steam stores
/// them decrypted in <c>depotcache</c> — which is exactly why key checking cannot rely on this.
/// </param>
/// <param name="GidManifest">
/// The manifest's own id. Together with <paramref name="DepotId"/> this is the file's self-declared
/// identity, which lets a cached <c>&lt;depot&gt;_&lt;gid&gt;.manifest</c> be checked against its name
/// rather than trusted because it exists.
/// </param>
public readonly record struct ManifestInfo(
    long DepotId, bool FilenamesEncrypted, long SizeOnDisk, ulong GidManifest);

/// <summary>
/// Minimal reader for Steam's depot manifest format. Local, allocation-light, no network and no
/// dependency beyond the BCL.
/// </summary>
/// <remarks>
/// <para>The file is a flat run of <c>[magic:uint32][length:uint32][bytes]</c> sections, optionally
/// wrapped in a zip. Only the metadata section is parsed here, and only three of its fields — this is
/// deliberately not a general protobuf decoder, just enough to answer "how big is this depot" and
/// "can the key be checked against it".</para>
///
/// <para>Everything fails soft: a malformed or truncated file yields null rather than throwing. Of the
/// 2,298 manifests in one real depotcache, one does not parse, and a single bad file must never take
/// down a download that would otherwise work.</para>
/// </remarks>
public static class ManifestFile
{
    private const uint PayloadMagic = 0x71F617D0;
    private const uint MetadataMagic = 0x1F4812BE;
    private const uint EofMagic = 0x32C415AB;

    // ContentManifestMetadata field numbers (see DepotDownloader's manifest.proto).
    private const int FieldDepotId = 1;
    private const int FieldGidManifest = 2;
    private const int FieldFilenamesEncrypted = 4;
    private const int FieldSizeOnDisk = 5;

    /// <summary>Read a manifest's metadata, or null if the file is missing or unparseable.</summary>
    /// <remarks>
    /// Seeks over the payload rather than loading the file. The payload holds every file entry and runs
    /// to megabytes (3 MB is common), while the metadata this returns is a few dozen bytes — and this is
    /// now called once per depot when a picker opens, so reading whole files would be felt.
    /// </remarks>
    public static ManifestInfo? TryRead(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            // A zipped manifest has to be inflated whole; it cannot be seeked through.
            Span<byte> peek = stackalloc byte[2];
            if (fs.Read(peek) == 2 && peek[0] == 'P' && peek[1] == 'K')
                return ParseMetadata(FindSection(Unwrap(File.ReadAllBytes(path)), MetadataMagic));
            fs.Position = 0;

            byte[] header = new byte[8];
            while (fs.Position + 8 <= fs.Length)
            {
                if (fs.Read(header, 0, 8) != 8) return null;
                uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
                uint len = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));

                if (magic == EofMagic) break;
                if (len > int.MaxValue || fs.Position + len > fs.Length) return null; // truncated

                if (magic != MetadataMagic) { fs.Position += len; continue; }

                byte[] meta = new byte[len];
                return fs.ReadAtLeast(meta, (int)len, throwOnEndOfStream: false) == (int)len
                    ? ParseMetadata(meta)
                    : null;
            }
            return null; // no metadata section
        }
        catch { return null; } // unreadable, truncated, or not a manifest at all
    }

    private static ManifestInfo? ParseMetadata(byte[]? meta)
    {
        if (meta is null) return null;

        long depotId = 0, size = 0;
        ulong gid = 0;
        bool encrypted = false;

        int o = 0;
        while (o < meta.Length)
        {
            if (!ReadTag(meta, ref o, out int field, out int wire)) return null;
            if (wire == 0)
            {
                if (!ReadVarint(meta, ref o, out ulong v)) return null;
                switch (field)
                {
                    case FieldDepotId: depotId = (long)v; break;
                    case FieldGidManifest: gid = v; break;
                    case FieldFilenamesEncrypted: encrypted = v != 0; break;
                    case FieldSizeOnDisk: size = (long)v; break;
                }
            }
            else if (!SkipField(meta, ref o, wire)) return null;
        }

        return new ManifestInfo(depotId, encrypted, size, gid);
    }

    /// <summary>
    /// True when this file really is the manifest its name claims — it parses, and its own depot id and
    /// gid match. Guards against a truncated or half-written cache entry being trusted because it exists.
    /// </summary>
    public static bool Matches(string? path, long depotId, string manifestId) =>
        TryRead(path) is { } info
        && info.DepotId == depotId
        && ulong.TryParse(manifestId, out ulong gid)
        && info.GidManifest == gid;

    /// <summary>
    /// Prove a depot key is the right one, when the manifest allows it.
    /// </summary>
    /// <returns>
    /// True if a filename decrypted cleanly. <b>Also true when the manifest's filenames are not
    /// encrypted</b> — there is nothing to test, so this reports "no objection", not "verified".
    /// </returns>
    /// <remarks>
    /// Only ~1.5% of cached manifests still carry encrypted filenames, so this is an opportunistic
    /// extra check on top of "is a key present at all", never a replacement for it. Treating a
    /// not-encrypted manifest as a pass is the only honest option: reporting failure there would reject
    /// every depot, and claiming verification would be a lie.
    /// </remarks>
    public static bool KeyLooksValid(string? path, byte[] key)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || key.Length != 32) return true;

        // Cheap metadata pass first. There is nothing to test unless the filenames are still encrypted,
        // which is the small minority — this keeps the multi-MB payload read off the other ~98%.
        if (TryRead(path) is not { FilenamesEncrypted: true }) return true;

        try
        {
            byte[] data = Unwrap(File.ReadAllBytes(path));
            if (FindSection(data, PayloadMagic) is not { } payload) return true;
            if (FirstFilename(payload) is not { } name) return true;

            // Base64 only while encrypted; a decrypted name is raw UTF-8 and won't round-trip.
            byte[] cipher;
            try { cipher = Convert.FromBase64String(name); }
            catch (FormatException) { return true; }
            if (cipher.Length <= 16 || cipher.Length % 16 != 0) return true;

            return TryDecryptName(cipher, key);
        }
        catch { return true; } // never block a download on this check failing to run
    }

    /// <summary>
    /// Steam's filename cipher: the leading 16 bytes are an IV encrypted with AES-ECB under the depot
    /// key, and the remainder is AES-CBC under that IV. A wrong key fails the PKCS7 unpad.
    /// </summary>
    private static bool TryDecryptName(byte[] cipher, byte[] key)
    {
        using var ecb = Aes.Create();
        ecb.Key = key;
        ecb.Mode = CipherMode.ECB;
        ecb.Padding = PaddingMode.None;
        byte[] iv = ecb.CreateDecryptor().TransformFinalBlock(cipher, 0, 16);

        using var cbc = Aes.Create();
        cbc.Key = key;
        cbc.IV = iv;
        cbc.Mode = CipherMode.CBC;
        cbc.Padding = PaddingMode.PKCS7;

        try
        {
            byte[] plain = cbc.CreateDecryptor().TransformFinalBlock(cipher, 16, cipher.Length - 16);
            // A correct key yields a printable path; a wrong one that happens to unpad yields control bytes.
            foreach (byte b in plain)
                if (b < 0x20 && b != 0) return false;
            return true;
        }
        catch (CryptographicException) { return false; } // bad padding = wrong key
    }

    /// <summary>A manifest may be zipped; if so the single entry inside is the real thing.</summary>
    private static byte[] Unwrap(byte[] data)
    {
        if (data.Length < 2 || data[0] != 'P' || data[1] != 'K') return data;

        using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
        var entry = zip.Entries.FirstOrDefault();
        if (entry is null) return data;

        using var s = entry.Open();
        using var buf = new MemoryStream();
        s.CopyTo(buf);
        return buf.ToArray();
    }

    /// <summary>Walk the section table and return the first section with this magic.</summary>
    private static byte[]? FindSection(byte[] data, uint magic)
    {
        int o = 0;
        while (o + 8 <= data.Length)
        {
            uint m = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(o));
            int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(o + 4));
            o += 8;

            if (m == EofMagic) break;
            if (len < 0 || o + len > data.Length) break; // truncated

            if (m == magic) return data[o..(o + len)];
            o += len;
        }
        return null;
    }

    /// <summary>The first FileMapping's filename, as stored (base64 while encrypted).</summary>
    private static string? FirstFilename(byte[] payload)
    {
        int o = 0;
        while (o < payload.Length)
        {
            if (!ReadTag(payload, ref o, out int field, out int wire)) return null;

            if (field == 1 && wire == 2) // repeated FileMapping
            {
                if (!ReadVarint(payload, ref o, out ulong len)) return null;
                int end = o + (int)len;
                if (end > payload.Length) return null;

                int inner = o;
                while (inner < end)
                {
                    if (!ReadTag(payload, ref inner, out int f2, out int w2)) return null;
                    if (f2 == 1 && w2 == 2) // filename
                    {
                        if (!ReadVarint(payload, ref inner, out ulong n)) return null;
                        if (inner + (int)n > payload.Length) return null;
                        return Encoding.UTF8.GetString(payload, inner, (int)n);
                    }
                    if (!SkipField(payload, ref inner, w2)) return null;
                }
                o = end;
            }
            else if (!SkipField(payload, ref o, wire)) return null;
        }
        return null;
    }

    private static bool ReadTag(byte[] d, ref int o, out int field, out int wire)
    {
        field = wire = 0;
        if (!ReadVarint(d, ref o, out ulong tag)) return false;
        field = (int)(tag >> 3);
        wire = (int)(tag & 0x07);
        return true;
    }

    private static bool ReadVarint(byte[] d, ref int o, out ulong value)
    {
        value = 0;
        int shift = 0;
        while (o < d.Length)
        {
            byte b = d[o++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
            if (shift > 63) return false; // malformed
        }
        return false; // ran off the end
    }

    private static bool SkipField(byte[] d, ref int o, int wire)
    {
        switch (wire)
        {
            case 0: return ReadVarint(d, ref o, out _);
            case 1: o += 8; return o <= d.Length;
            case 5: o += 4; return o <= d.Length;
            case 2:
                if (!ReadVarint(d, ref o, out ulong len)) return false;
                o += (int)len;
                return o <= d.Length;
            default: return false; // groups: not used by this format
        }
    }
}
