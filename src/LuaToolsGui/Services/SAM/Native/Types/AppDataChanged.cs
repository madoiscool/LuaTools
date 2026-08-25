using System.Runtime.InteropServices;

namespace LuaToolsGui.Services.SAM.Native.Types;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct AppDataChanged
{
    public uint Id;
    [MarshalAs(UnmanagedType.I1)]
    public bool Result;
}
