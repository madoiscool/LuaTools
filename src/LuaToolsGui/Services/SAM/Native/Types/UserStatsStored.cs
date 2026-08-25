using System.Runtime.InteropServices;

namespace LuaToolsGui.Services.SAM.Native.Types;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct UserStatsStored
{
    public ulong GameId;
    public int Result;
}
