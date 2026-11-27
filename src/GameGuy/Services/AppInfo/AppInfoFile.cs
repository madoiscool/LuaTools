using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace GameGuy.Services.AppInfo;

/// <summary>Thrown when appinfo.vdf isn't a format we understand. A Steam-side version bump.</summary>
public class AppInfoFormatException(string message) : Exception(message);

/// <summary>Where one app's record sits in the file, and its parsed metadata.</summary>
public sealed record AppInfoEntry(int AppId, long Offset, int Size, uint ChangeNumber);

/// <summary>
/// Reader/writer for Steam's <c>appcache\appinfo.vdf</c>.
///
/// <para>Layout (v27/28/29):</para>
/// <code>
/// magic u32 | universe u32 | [v29] string-table offset i64
/// repeated: appid u32 | size u32 | meta | blob        (terminated by appid == 0)
/// [v29] string table at the header offset: count u32, then NUL-terminated UTF-8
/// </code>
/// <para>
/// <c>size</c> counts the bytes AFTER the size field, i.e. meta + blob. The meta block is 60 bytes on
/// v28+ (40 on v27, which has no binary SHA-1):
/// <c>infoState(4) lastUpdated(4) picsToken(8) sha1_text(20) changeNumber(4) sha1_binary(20)</c>.
/// </para>
/// <para>
/// The file is ~373 MB with ~177k apps, so nothing is held in memory beyond a
/// <c>appid → offset</c> index (~1.4 MB): apps are read on demand and saves stream.
/// </para>
/// </summary>
public sealed class AppInfoFile : IDisposable
{
    public const uint Magic27 = 0x07564427;
    public const uint Magic28 = 0x07564428;
    public const uint Magic29 = 0x07564429;

    // Offsets within the meta block.
    private const int Sha1TextOffset = 16;
    private const int ChangeNumberOffset = 36;
    private const int Sha1BinaryOffset = 40;

    private readonly FileStream _file;
    private readonly Dictionary<int, AppInfoEntry> _index = [];

    public string Path { get; }
    public uint MagicNumber { get; }
    public uint Universe { get; }

    /// <summary>The v29 string table, decoded for lookup. Keys are indices into this; it is only ever
    /// appended to, so existing indices in untouched blobs stay valid.</summary>
    public List<string> Strings { get; } = [];

    /// <summary>
    /// The same table as RAW BYTES, which is what gets written back.
    ///
    /// <para>
    /// A few table entries aren't valid UTF-8. Decoding them to a string and re-encoding turns each bad
    /// byte into U+FFFD (three bytes instead of one), so a no-edit rebuild came out 12 bytes larger.
    /// Keeping the original bytes makes the rebuild exact; only newly interned keys are encoded.
    /// </para>
    /// </summary>
    private readonly List<byte[]> _stringBytes = [];

    public bool IndexedKeys => MagicNumber >= Magic29;
    public int MetaLength => MagicNumber >= Magic28 ? 60 : 40;
    private long _appsStart;

    public IReadOnlyDictionary<int, AppInfoEntry> Entries => _index;

    public AppInfoFile(string path)
    {
        Path = path;
        _file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        MagicNumber = BinaryVdf.ReadUInt32(_file);
        Universe = BinaryVdf.ReadUInt32(_file);
        if (MagicNumber is not (Magic27 or Magic28 or Magic29))
            throw new AppInfoFormatException($"Unknown appinfo version 0x{MagicNumber:X8}.");

        if (IndexedKeys)
        {
            long tableOffset = BinaryVdf.ReadInt64(_file);
            long resume = _file.Position;
            ReadStringTable(tableOffset);
            _file.Position = resume;
        }

        _appsStart = _file.Position;
        BuildIndex();
    }

    private void ReadStringTable(long offset)
    {
        _file.Position = offset;
        uint count = BinaryVdf.ReadUInt32(_file);
        for (uint i = 0; i < count; i++)
        {
            byte[] raw = BinaryVdf.ReadCStringBytes(_file);
            _stringBytes.Add(raw);
            Strings.Add(Encoding.UTF8.GetString(raw));   // lookup/display only. Raw is what's written
        }
    }

    /// <summary>Walk the app records recording offsets, skipping every blob. ~0.5s for 177k apps.</summary>
    private void BuildIndex()
    {
        _file.Position = _appsStart;
        while (true)
        {
            long offset = _file.Position;
            var header = new byte[8];
            if (_file.Read(header, 0, 8) < 8) return;

            int appId = BitConverter.ToInt32(header, 0);
            if (appId == 0) return;
            int size = BitConverter.ToInt32(header, 4);

            // changeNumber lives inside the meta block; read it without parsing the blob.
            var meta = BinaryVdf.ReadExactly(_file, MetaLength);
            uint changeNumber = BitConverter.ToUInt32(meta, ChangeNumberOffset);

            _index[appId] = new AppInfoEntry(appId, offset, size, changeNumber);
            _file.Position = offset + 8 + size;   // size counts meta + blob
        }
    }

    /// <summary>Parse one app's property table, or null if the app isn't in this file.</summary>
    public VdfTable? ReadApp(int appId)
    {
        if (!_index.TryGetValue(appId, out var entry)) return null;
        _file.Position = entry.Offset + 8 + MetaLength;
        return BinaryVdf.Read(_file, Strings, IndexedKeys);
    }

    /// <summary>The <c>appinfo</c> child, which is what every real record wraps its content in.</summary>
    public VdfTable? ReadAppBody(int appId) => ReadApp(appId)?.GetTable("appinfo");

    // ── saving ──────────────────────────────────────────────────────

    /// <summary>
    /// Write a copy of the file with <paramref name="edits"/> applied, streaming rather than buffering.
    ///
    /// <para>
    /// Apps that aren't being edited are copied through as RAW BYTES. That's not just an optimisation:
    /// re-serializing every app would have to survive duplicate keys and non-UTF-8 strings across all
    /// 177k of them, whereas copying makes them byte-exact by construction and confines any risk to the
    /// one app actually being changed.
    /// </para>
    /// </summary>
    /// <param name="edits">appid → the full replacement property table (the <c>appinfo</c> wrapper included).</param>
    public void SaveAs(string destination, IReadOnlyDictionary<int, VdfTable> edits)
    {
        // Seeded from the ORIGINAL bytes so existing entries write back unchanged; new keys are appended,
        // which keeps every index already baked into untouched blobs valid.
        var strings = new List<byte[]>(_stringBytes);
        var lookup = new Dictionary<string, uint>(strings.Count, StringComparer.Ordinal);
        for (int i = 0; i < Strings.Count; i++) lookup.TryAdd(Strings[i], (uint)i);

        uint Intern(string key)
        {
            if (lookup.TryGetValue(key, out uint index)) return index;
            index = (uint)strings.Count;
            strings.Add(Encoding.UTF8.GetBytes(key));
            lookup[key] = index;
            return index;
        }

        using var output = File.Open(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        BinaryVdf.WriteUInt32(output, MagicNumber);
        BinaryVdf.WriteUInt32(output, Universe);

        long tableOffsetPos = output.Position;
        if (IndexedKeys) output.Write(BitConverter.GetBytes(0L));   // patched once the table lands

        foreach (var entry in _index.Values.OrderBy(e => e.Offset))
        {
            _file.Position = entry.Offset + 8;
            var meta = BinaryVdf.ReadExactly(_file, MetaLength);

            if (edits.TryGetValue(entry.AppId, out var replacement))
            {
                using var blob = new MemoryStream();
                BinaryVdf.Write(blob, replacement, Intern, IndexedKeys);
                byte[] bytes = blob.ToArray();

                // sha1_binary is exactly SHA1(blob). Verified against 3,000 apps and against
                // SteamEdit's own writer. Leaving it stale would ship a hash of the pre-edit bytes.
                // sha1_text is NOT reproducible from the blob (it hashes Steam's own text rendering),
                // so it and changeNumber are carried over untouched.
                if (MagicNumber >= Magic28)
                    SHA1.HashData(bytes).CopyTo(meta, Sha1BinaryOffset);

                BinaryVdf.WriteInt32(output, entry.AppId);
                BinaryVdf.WriteInt32(output, meta.Length + bytes.Length);
                output.Write(meta);
                output.Write(bytes);
            }
            else
            {
                BinaryVdf.WriteInt32(output, entry.AppId);
                BinaryVdf.WriteInt32(output, entry.Size);
                output.Write(meta);
                CopyRange(_file, output, entry.Size - MetaLength);
            }
        }

        BinaryVdf.WriteInt32(output, 0);   // appid 0 terminator

        if (IndexedKeys)
        {
            long tableOffset = output.Position;
            BinaryVdf.WriteInt32(output, strings.Count);
            foreach (byte[] s in strings) BinaryVdf.WriteCString(output, s);
            output.Position = tableOffsetPos;
            output.Write(BitConverter.GetBytes(tableOffset));
        }
    }

    private static void CopyRange(Stream from, Stream to, int count)
    {
        var buffer = new byte[81920];
        while (count > 0)
        {
            int read = from.Read(buffer, 0, Math.Min(buffer.Length, count));
            if (read <= 0) throw new EndOfStreamException("appinfo.vdf ended mid-app.");
            to.Write(buffer, 0, read);
            count -= read;
        }
    }

    public void Dispose() => _file.Dispose();
}
