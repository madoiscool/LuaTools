using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LuaToolsGui.Services.SAM.Native;

public abstract class NativeWrapper<TNativeFunctions> : INativeWrapper
    where TNativeFunctions : struct
{
    protected IntPtr ObjectAddress;
    protected TNativeFunctions Functions;

    public override string ToString()
    {
        return $"Steam Interface<{typeof(TNativeFunctions)}> #{ObjectAddress.ToInt64():X8}";
    }

    public void SetupFunctions(IntPtr objectAddress)
    {
        ObjectAddress = objectAddress;

        var iface = (NativeClass)Marshal.PtrToStructure(
            ObjectAddress,
            typeof(NativeClass))!;

        Functions = (TNativeFunctions)Marshal.PtrToStructure(
            iface.VirtualTable,
            typeof(TNativeFunctions))!;
    }

    private readonly Dictionary<IntPtr, Delegate> _functionCache = new();

    protected Delegate GetDelegate<TDelegate>(IntPtr pointer)
    {
        if (!_functionCache.TryGetValue(pointer, out var function))
        {
            function = Marshal.GetDelegateForFunctionPointer(pointer, typeof(TDelegate));
            _functionCache[pointer] = function;
        }
        return function;
    }

    protected TDelegate GetFunction<TDelegate>(IntPtr pointer)
        where TDelegate : class
    {
        return (TDelegate)(object)GetDelegate<TDelegate>(pointer);
    }

    protected void Call<TDelegate>(IntPtr pointer, params object[] args)
    {
        GetDelegate<TDelegate>(pointer).DynamicInvoke(args);
    }

    protected TReturn Call<TReturn, TDelegate>(IntPtr pointer, params object[] args)
    {
        return (TReturn)GetDelegate<TDelegate>(pointer).DynamicInvoke(args)!;
    }
}
