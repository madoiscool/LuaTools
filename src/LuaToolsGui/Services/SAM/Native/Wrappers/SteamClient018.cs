using System;
using System.Runtime.InteropServices;
using LuaToolsGui.Services.SAM.Native.Interfaces;

namespace LuaToolsGui.Services.SAM.Native.Wrappers;

public class SteamClient018 : NativeWrapper<ISteamClient018>
{
    #region CreateSteamPipe
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int NativeCreateSteamPipe(IntPtr self);

    public int CreateSteamPipe()
    {
        return Call<int, NativeCreateSteamPipe>(Functions.CreateSteamPipe, ObjectAddress);
    }
    #endregion

    #region ReleaseSteamPipe
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool NativeReleaseSteamPipe(IntPtr self, int pipe);

    public bool ReleaseSteamPipe(int pipe)
    {
        return Call<bool, NativeReleaseSteamPipe>(Functions.ReleaseSteamPipe, ObjectAddress, pipe);
    }
    #endregion

    #region CreateLocalUser
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int NativeCreateLocalUser(IntPtr self, ref int pipe, Types.AccountType type);

    public int CreateLocalUser(ref int pipe, Types.AccountType type)
    {
        var call = GetFunction<NativeCreateLocalUser>(Functions.CreateLocalUser);
        return call(ObjectAddress, ref pipe, type);
    }
    #endregion

    #region ConnectToGlobalUser
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int NativeConnectToGlobalUser(IntPtr self, int pipe);

    public int ConnectToGlobalUser(int pipe)
    {
        return Call<int, NativeConnectToGlobalUser>(
            Functions.ConnectToGlobalUser,
            ObjectAddress,
            pipe);
    }
    #endregion

    #region ReleaseUser
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void NativeReleaseUser(IntPtr self, int pipe, int user);

    public void ReleaseUser(int pipe, int user)
    {
        Call<NativeReleaseUser>(Functions.ReleaseUser, ObjectAddress, pipe, user);
    }
    #endregion

    #region SetLocalIPBinding
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void NativeSetLocalIPBinding(IntPtr self, uint host, ushort port);

    public void SetLocalIPBinding(uint host, ushort port)
    {
        Call<NativeSetLocalIPBinding>(Functions.SetLocalIPBinding, ObjectAddress, host, port);
    }
    #endregion

    #region GetISteamUser
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate IntPtr NativeGetISteamUser(IntPtr self, int user, int pipe, IntPtr version);

    private TClass GetISteamUser<TClass>(int user, int pipe, string version)
        where TClass : INativeWrapper, new()
    {
        using var nativeVersion = NativeStrings.StringToStringHandle(version);
        IntPtr address = Call<IntPtr, NativeGetISteamUser>(
            Functions.GetISteamUser,
            ObjectAddress,
            user,
            pipe,
            nativeVersion.Handle);
        TClass result = new();
        result.SetupFunctions(address);
        return result;
    }
    #endregion

    #region GetSteamUser012
    public SteamUser012 GetSteamUser012(int user, int pipe)
    {
        return GetISteamUser<SteamUser012>(user, pipe, "SteamUser012");
    }
    #endregion

    #region GetISteamUserStats
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate IntPtr NativeGetISteamUserStats(IntPtr self, int user, int pipe, IntPtr version);

    private TClass GetISteamUserStats<TClass>(int user, int pipe, string version)
        where TClass : INativeWrapper, new()
    {
        using var nativeVersion = NativeStrings.StringToStringHandle(version);
        IntPtr address = Call<IntPtr, NativeGetISteamUserStats>(
            Functions.GetISteamUserStats,
            ObjectAddress,
            user,
            pipe,
            nativeVersion.Handle);
        TClass result = new();
        result.SetupFunctions(address);
        return result;
    }
    #endregion

    #region GetSteamUserStats013
    public SteamUserStats013 GetSteamUserStats013(int user, int pipe)
    {
        return GetISteamUserStats<SteamUserStats013>(user, pipe, "STEAMUSERSTATS_INTERFACE_VERSION013");
    }
    #endregion

    #region GetISteamUtils
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate IntPtr NativeGetISteamUtils(IntPtr self, int pipe, IntPtr version);

    public TClass GetISteamUtils<TClass>(int pipe, string version)
        where TClass : INativeWrapper, new()
    {
        using var nativeVersion = NativeStrings.StringToStringHandle(version);
        IntPtr address = Call<IntPtr, NativeGetISteamUtils>(
            Functions.GetISteamUtils,
            ObjectAddress,
            pipe,
            nativeVersion.Handle);
        TClass result = new();
        result.SetupFunctions(address);
        return result;
    }
    #endregion

    #region GetSteamUtils004
    public SteamUtils005 GetSteamUtils004(int pipe)
    {
        return GetISteamUtils<SteamUtils005>(pipe, "SteamUtils005");
    }
    #endregion

    #region GetISteamApps
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate IntPtr NativeGetISteamApps(IntPtr self, int user, int pipe, IntPtr version);

    private TClass GetISteamApps<TClass>(int user, int pipe, string version)
        where TClass : INativeWrapper, new()
    {
        using var nativeVersion = NativeStrings.StringToStringHandle(version);
        IntPtr address = Call<IntPtr, NativeGetISteamApps>(
            Functions.GetISteamApps,
            ObjectAddress,
            user,
            pipe,
            nativeVersion.Handle);
        TClass result = new();
        result.SetupFunctions(address);
        return result;
    }
    #endregion

    #region GetSteamApps001
    public SteamApps001 GetSteamApps001(int user, int pipe)
    {
        return GetISteamApps<SteamApps001>(user, pipe, "STEAMAPPS_INTERFACE_VERSION001");
    }
    #endregion

    #region GetSteamApps008
    public SteamApps008 GetSteamApps008(int user, int pipe)
    {
        return GetISteamApps<SteamApps008>(user, pipe, "STEAMAPPS_INTERFACE_VERSION008");
    }
    #endregion
}
