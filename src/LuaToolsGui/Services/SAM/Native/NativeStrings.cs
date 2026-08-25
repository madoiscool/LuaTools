using System;
using System.Runtime.InteropServices;
using System.Text;

namespace LuaToolsGui.Services.SAM.Native;

internal class NativeStrings
{
    public sealed class StringHandle : IDisposable
    {
        private bool _isDisposed;
        public IntPtr Handle { get; private set; }

        public StringHandle(IntPtr handle)
        {
            Handle = handle;
        }

        ~StringHandle()
        {
            Dispose(false);
        }

        private void Dispose(bool disposing)
        {
            if (_isDisposed) return;
            if (Handle != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(Handle);
                Handle = IntPtr.Zero;
            }
            _isDisposed = true;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }

    public static StringHandle StringToStringHandle(string? value)
    {
        if (value is null)
        {
            return new StringHandle(IntPtr.Zero);
        }

        byte[] bytes = Encoding.UTF8.GetBytes(value);
        IntPtr pointer = Marshal.AllocHGlobal(bytes.Length + 1);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        Marshal.WriteByte(pointer, bytes.Length, 0);
        return new StringHandle(pointer);
    }

    public static string? PointerToString(IntPtr nativeData)
    {
        if (nativeData == IntPtr.Zero) return null;
        int length = 0;
        while (Marshal.ReadByte(nativeData, length) != 0)
        {
            length++;
        }
        if (length == 0) return string.Empty;
        byte[] buffer = new byte[length];
        Marshal.Copy(nativeData, buffer, 0, buffer.Length);
        return Encoding.UTF8.GetString(buffer);
    }

    public static string? PointerToString(IntPtr nativeData, int length)
    {
        if (nativeData == IntPtr.Zero) return null;
        byte[] buffer = new byte[length];
        Marshal.Copy(nativeData, buffer, 0, buffer.Length);
        int realLength = Array.IndexOf(buffer, (byte)0);
        if (realLength >= 0)
        {
            length = realLength;
        }
        return length == 0 ? string.Empty : Encoding.UTF8.GetString(buffer, 0, length);
    }
}
