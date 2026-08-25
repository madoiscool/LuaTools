using System;
using System.Runtime.InteropServices;

namespace LuaToolsGui.Services.SAM.Native;

public class Callback<TParameter> : ICallback
{
    public delegate void CallbackFunction(TParameter parameter);

    public int Id { get; protected set; }
    public bool IsServer { get; protected set; }
    public event CallbackFunction? OnRun;

    public void Run(IntPtr param)
    {
        if (OnRun is null) return;
        var parameter = (TParameter)Marshal.PtrToStructure(param, typeof(TParameter))!;
        OnRun(parameter);
    }
}
