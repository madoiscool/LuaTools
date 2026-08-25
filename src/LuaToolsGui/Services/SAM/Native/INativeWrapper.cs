using System;

namespace LuaToolsGui.Services.SAM.Native;

public interface INativeWrapper
{
    void SetupFunctions(IntPtr objectAddress);
}
