using System;
using System.Runtime.InteropServices;

namespace LuaToolsGui.Services.SAM.Native.Interfaces;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct ISteamApps008
{
    public IntPtr IsSubscribed;
    public IntPtr IsLowViolence;
    public IntPtr IsCybercafe;
    public IntPtr IsVACBanned;
    public IntPtr GetCurrentGameLanguage;
    public IntPtr GetAvailableGameLanguages;
    public IntPtr IsSubscribedApp;
    public IntPtr IsDlcInstalled;
    public IntPtr GetEarliestPurchaseUnixTime;
    public IntPtr IsSubscribedFromFreeWeekend;
    public IntPtr GetDLCCount;
    public IntPtr BGetDLCDataByIndex;
    public IntPtr InstallDLC;
    public IntPtr UninstallDLC;
    public IntPtr RequestAppProofOfPurchaseKey;
    public IntPtr GetCurrentBetaName;
    public IntPtr MarkContentCorrupt;
    public IntPtr GetInstalledDepots;
    public IntPtr GetAppInstallDir;
    public IntPtr BIsAppInstalled;
    public IntPtr GetAppOwner;
    public IntPtr GetLaunchQueryParam;
    public IntPtr GetDlcDownloadProgress;
    public IntPtr GetAppBuildId;
    public IntPtr RegisterActivationCode;
}
