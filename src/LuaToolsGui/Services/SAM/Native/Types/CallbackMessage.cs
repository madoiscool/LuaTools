using System;
using System.Runtime.InteropServices;

namespace LuaToolsGui.Services.SAM.Native.Types;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct CallbackMessage
{
    public int User;
    public int Id;
    public IntPtr ParamPointer;
    public int ParamSize;
}
