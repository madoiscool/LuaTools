using System;
using System.Runtime.InteropServices;

namespace LuaToolsGui.Services.SAM.Native.Interfaces;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct ISteamUtils005
{
    public IntPtr GetSecondsSinceAppActive;
    public IntPtr GetSecondsSinceComputerActive;
    public IntPtr GetConnectedUniverse;
    public IntPtr GetServerRealTime;
    public IntPtr GetIPCountry;
    public IntPtr GetImageSize;
    public IntPtr GetImageRGBA;
    public IntPtr GetCSERIPPort;
    public IntPtr GetCurrentBatteryPower;
    public IntPtr GetAppID;
    public IntPtr SetOverlayNotificationPosition;
    public IntPtr IsAPICallCompleted;
    public IntPtr GetAPICallFailureReason;
    public IntPtr GetAPICallResult;
    public IntPtr RunFrame;
    public IntPtr GetIPCCallCount;
    public IntPtr SetWarningMessageHook;
    public IntPtr IsOverlayEnabled;
    public IntPtr BOverlayNeedsPresent;
    public IntPtr CheckFileSignature;
    public IntPtr ShowGamepadTextInput;
    public IntPtr GetEnteredGamepadTextLength;
    public IntPtr GetEnteredGamepadTextInput;
    public IntPtr GetGameLauncherMode;
}
