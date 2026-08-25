using System;
using System.Runtime.InteropServices;
using LuaToolsGui.Services.SAM.Native.Interfaces;

namespace LuaToolsGui.Services.SAM.Native.Wrappers;

public class SteamUtils005 : NativeWrapper<ISteamUtils005>
{
    #region GetConnectedUniverse
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int NativeGetConnectedUniverse(IntPtr self);

    public int GetConnectedUniverse()
    {
        return Call<int, NativeGetConnectedUniverse>(Functions.GetConnectedUniverse, ObjectAddress);
    }
    #endregion

    #region GetIPCountry
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate IntPtr NativeGetIPCountry(IntPtr self);

    public string? GetIPCountry()
    {
        var result = Call<IntPtr, NativeGetIPCountry>(Functions.GetIPCountry, ObjectAddress);
        return NativeStrings.PointerToString(result);
    }
    #endregion

    #region GetImageSize
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool NativeGetImageSize(IntPtr self, int index, out int width, out int height);

    public bool GetImageSize(int index, out int width, out int height)
    {
        var call = GetFunction<NativeGetImageSize>(Functions.GetImageSize);
        return call(ObjectAddress, index, out width, out height);
    }
    #endregion

    #region GetImageRGBA
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool NativeGetImageRGBA(IntPtr self, int index, byte[] buffer, int length);

    public bool GetImageRGBA(int index, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var call = GetFunction<NativeGetImageRGBA>(Functions.GetImageRGBA);
        return call(ObjectAddress, index, data, data.Length);
    }
    #endregion

    #region GetAppID
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate uint NativeGetAppId(IntPtr self);

    public uint GetAppId()
    {
        return Call<uint, NativeGetAppId>(Functions.GetAppID, ObjectAddress);
    }
    #endregion
}
