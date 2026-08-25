using System;
using System.Runtime.InteropServices;

namespace LuaToolsGui.Services.SAM.Native.Interfaces;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct ISteamApps001
{
    public IntPtr GetAppData;
}
