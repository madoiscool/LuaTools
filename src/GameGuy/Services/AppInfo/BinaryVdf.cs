using System.IO;
using System.Text;

namespace GameGuy.Services.AppInfo;

/// <summary>Value markers used by Valve's binary KeyValues (the format inside appinfo.vdf).</summary>
public enum VdfType : byte
{
    Table = 0x00,
    String = 0x01,
    Int32 = 0x02,
    Float = 0x03,
    Pointer = 0x04,
    WString = 0x05,
    Color = 0x06,
    UInt64 = 0x07,
    End = 0x08,
    Int64 = 0x0A,
    EndAlt = 0x0B,
}

/// <summary>
/// One key/value pair. <see cref="Value"/> is a <see cref="VdfTable"/> for <see cref="VdfType.Table"/>,
/// a <c>byte[]</c> for strings (see <see cref="VdfTable"/> for why), and a boxed primitive otherwise.
/// </summary>
public sealed class VdfProperty(string name, VdfType type, object value)
{
    public string Name { get; set; } = name;
    public VdfType Type { get; } = type;
    public object Value { get; set; } = value;

    public VdfTable? AsTable() => Value as VdfTable;

    /// <summary>Decode a string value for display. Lossy for the rare non-UTF-8 blob, which is exactly
    /// why the raw bytes are what's stored, so an untouched value still writes back unchanged.</summary>
    public string AsText() => Value is byte[] b ? Encoding.UTF8.GetString(b) : Value?.ToString() ?? "";

    public static VdfProperty Text(string name, string value) =>
        new(name, VdfType.String, Encoding.UTF8.GetBytes(value));

    public static VdfProperty Table(string name, VdfTable value) => new(name, VdfType.Table, value);
}

/// <summary>
/// A binary-VDF object: an ORDERED LIST of properties, deliberately not a dictionary.
///
/// <para>
/// The format permits the SAME KEY TWICE inside one object. 15 of the 176,869 apps in a real
/// appinfo.vdf do it (app 46830 lists <c>/silent</c> twice). A dictionary silently collapses those,
/// which shortens the re-serialized blob and corrupts the file. Order also has to be preserved exactly,
/// since the bytes are hashed.
/// </para>
/// </summary>
public sealed class VdfTable
{
    public List<VdfProperty> Items { get; } = [];

    public VdfProperty? Find(string name) =>
        Items.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public VdfTable? GetTable(string name) => Find(name)?.AsTable();

    public string? GetText(string name) => Find(name)?.AsText();

    /// <summary>Get a child table, creating it if absent (used when building new launch entries).</summary>
    public VdfTable EnsureTable(string name)
    {
        if (GetTable(name) is { } existing) return existing;
        var table = new VdfTable();
        Items.Add(VdfProperty.Table(name, table));
        return table;
    }

    /// <summary>Set a string value, or remove the key entirely when <paramref name="value"/> is empty.
    /// Real launch entries omit unused optionals rather than storing <c>""</c>.</summary>
    public void SetText(string name, string? value)
    {
        if (string.IsNullOrEmpty(value)) { Remove(name); return; }
        if (Find(name) is { } existing) existing.Value = Encoding.UTF8.GetBytes(value);
        else Items.Add(VdfProperty.Text(name, value));
    }

    public void Remove(string name)
    {
        for (int i = Items.Count - 1; i >= 0; i--)
            if (string.Equals(Items[i].Name, name, StringComparison.OrdinalIgnoreCase))
                Items.RemoveAt(i);
    }

    /// <summary>Deep copy. Used to snapshot a launch table before it's edited.</summary>
    public VdfTable Clone()
    {
        var copy = new VdfTable();
        foreach (var p in Items)
        {
            object value = p.Value switch
            {
                VdfTable t => t.Clone(),
                byte[] b => b.ToArray(),
                var v => v,
            };
            copy.Items.Add(new VdfProperty(p.Name, p.Type, value));
        }
        return copy;
    }
}

/// <summary>
/// Reads/writes binary VDF. In appinfo v29 (<c>0x07564429</c>) every KEY is a u32 index into a shared
/// string table rather than an inline string; older versions inline them.
/// </summary>
public static class BinaryVdf
{
    /// <summary>Parse one object and everything nested under it. The stream is left just past its
    /// terminator.</summary>
    public static VdfTable Read(Stream s, IReadOnlyList<string> strings, bool indexedKeys)
    {
        var table = new VdfTable();
        while (true)
        {
            int marker = s.ReadByte();
            if (marker < 0 || marker == (int)VdfType.End || marker == (int)VdfType.EndAlt) return table;

            var type = (VdfType)marker;
            string name = indexedKeys ? strings[(int)ReadUInt32(s)] : ReadCString(s);

            object value = type switch
            {
                VdfType.Table => Read(s, strings, indexedKeys),
                VdfType.String => ReadCStringBytes(s),
                VdfType.Int32 or VdfType.Pointer or VdfType.Color => ReadInt32(s),
                VdfType.UInt64 => ReadUInt64(s),
                VdfType.Int64 => ReadInt64(s),
                VdfType.Float => BitConverter.ToSingle(ReadExactly(s, 4)),
                _ => throw new InvalidDataException($"Unsupported VDF value type 0x{marker:X2}"),
            };
            table.Items.Add(new VdfProperty(name, type, value));
        }
    }

    /// <summary>Serialize an object. <paramref name="intern"/> maps a key to its string-table index
    /// (v29) and is ignored when keys are inline.</summary>
    public static void Write(Stream s, VdfTable table, Func<string, uint> intern, bool indexedKeys)
    {
        foreach (var p in table.Items)
        {
            s.WriteByte((byte)p.Type);
            if (indexedKeys) WriteUInt32(s, intern(p.Name));
            else WriteCString(s, Encoding.UTF8.GetBytes(p.Name));

            switch (p.Type)
            {
                case VdfType.Table:
                    Write(s, (VdfTable)p.Value, intern, indexedKeys);
                    break;
                case VdfType.String:
                    WriteCString(s, (byte[])p.Value);
                    break;
                case VdfType.Int32 or VdfType.Pointer or VdfType.Color:
                    WriteInt32(s, (int)p.Value);
                    break;
                case VdfType.UInt64:
                    s.Write(BitConverter.GetBytes((ulong)p.Value));
                    break;
                case VdfType.Int64:
                    s.Write(BitConverter.GetBytes((long)p.Value));
                    break;
                case VdfType.Float:
                    s.Write(BitConverter.GetBytes((float)p.Value));
                    break;
                default:
                    throw new InvalidDataException($"Can't serialize VDF type {p.Type}");
            }
        }
        s.WriteByte((byte)VdfType.End);
    }

    // ── primitives ──────────────────────────────────────────────────

    public static byte[] ReadExactly(Stream s, int count)
    {
        var buffer = new byte[count];
        s.ReadExactly(buffer);
        return buffer;
    }

    public static uint ReadUInt32(Stream s) => BitConverter.ToUInt32(ReadExactly(s, 4));
    public static int ReadInt32(Stream s) => BitConverter.ToInt32(ReadExactly(s, 4));
    public static ulong ReadUInt64(Stream s) => BitConverter.ToUInt64(ReadExactly(s, 8));
    public static long ReadInt64(Stream s) => BitConverter.ToInt64(ReadExactly(s, 8));

    public static void WriteUInt32(Stream s, uint v) => s.Write(BitConverter.GetBytes(v));
    public static void WriteInt32(Stream s, int v) => s.Write(BitConverter.GetBytes(v));

    /// <summary>
    /// Read a NUL-terminated string as RAW BYTES. Not decoded: a handful of old apps (17390, 42990,
    /// 96800…) carry text that isn't valid UTF-8, and decoding with replacement turns each bad byte
    /// into U+FFFD, which re-encodes to three bytes and silently changes the blob's length.
    /// </summary>
    public static byte[] ReadCStringBytes(Stream s)
    {
        var buffer = new MemoryStream();
        int b;
        while ((b = s.ReadByte()) > 0) buffer.WriteByte((byte)b);
        return buffer.ToArray();
    }

    public static string ReadCString(Stream s) => Encoding.UTF8.GetString(ReadCStringBytes(s));

    public static void WriteCString(Stream s, byte[] raw)
    {
        s.Write(raw);
        s.WriteByte(0);
    }
}
