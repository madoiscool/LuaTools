using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace LuaToolsGui.Services.SAM.Schema;

internal static class StreamHelpers
{
    public static byte ReadValueU8(this Stream stream)
    {
        int b = stream.ReadByte();
        if (b < 0) throw new EndOfStreamException();
        return (byte)b;
    }

    public static int ReadValueS32(this Stream stream)
    {
        Span<byte> data = stackalloc byte[4];
        int read = stream.Read(data);
        if (read < 4) throw new EndOfStreamException();
        return BitConverter.ToInt32(data);
    }

    public static uint ReadValueU32(this Stream stream)
    {
        Span<byte> data = stackalloc byte[4];
        int read = stream.Read(data);
        if (read < 4) throw new EndOfStreamException();
        return BitConverter.ToUInt32(data);
    }

    public static ulong ReadValueU64(this Stream stream)
    {
        Span<byte> data = stackalloc byte[8];
        int read = stream.Read(data);
        if (read < 8) throw new EndOfStreamException();
        return BitConverter.ToUInt64(data);
    }

    public static float ReadValueF32(this Stream stream)
    {
        Span<byte> data = stackalloc byte[4];
        int read = stream.Read(data);
        if (read < 4) throw new EndOfStreamException();
        return BitConverter.ToSingle(data);
    }

    internal static string ReadStringInternalDynamic(this Stream stream, Encoding encoding, char end)
    {
        int characterSize = encoding.GetByteCount("e");
        string characterEnd = end.ToString(CultureInfo.InvariantCulture);

        int i = 0;
        var data = new byte[128 * characterSize];

        while (true)
        {
            if (i + characterSize > data.Length)
            {
                Array.Resize(ref data, data.Length + (128 * characterSize));
            }

            int read = stream.Read(data, i, characterSize);
            if (read < characterSize)
            {
                break;
            }

            if (encoding.GetString(data, i, characterSize) == characterEnd)
            {
                break;
            }

            i += characterSize;
        }

        if (i == 0)
        {
            return string.Empty;
        }

        return encoding.GetString(data, 0, i);
    }

    public static string ReadStringAscii(this Stream stream)
    {
        return stream.ReadStringInternalDynamic(Encoding.ASCII, '\0');
    }

    public static string ReadStringUnicode(this Stream stream)
    {
        return stream.ReadStringInternalDynamic(Encoding.UTF8, '\0');
    }
}
