using System;

namespace LuaToolsGui.Services.SAM.Native;

public interface ICallback
{
    int Id { get; }
    bool IsServer { get; }
    void Run(IntPtr param);
}
