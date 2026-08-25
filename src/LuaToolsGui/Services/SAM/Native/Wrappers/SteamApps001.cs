using System;
using System.Runtime.InteropServices;
using LuaToolsGui.Services.SAM.Native.Interfaces;

namespace LuaToolsGui.Services.SAM.Native.Wrappers;

public class SteamApps001 : NativeWrapper<ISteamApps001>
{
    #region GetAppData
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int NativeGetAppData(
        IntPtr self,
        uint appId,
        IntPtr key,
        IntPtr value,
        int valueLength);

    public string? GetAppData(uint appId, string key)
    {
        using var nativeHandle = NativeStrings.StringToStringHandle(key);
        const int valueLength = 1024;
        var valuePointer = Marshal.AllocHGlobal(valueLength);
        try
        {
            int result = Call<int, NativeGetAppData>(
                Functions.GetAppData,
                ObjectAddress,
                appId,
                nativeHandle.Handle,
                valuePointer,
                valueLength);
            return result == 0 ? null : NativeStrings.PointerToString(valuePointer, valueLength);
        }
        finally
        {
            Marshal.FreeHGlobal(valuePointer);
        }
    }
    #endregion
}
