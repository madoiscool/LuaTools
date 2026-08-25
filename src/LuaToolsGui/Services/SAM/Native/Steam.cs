using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace LuaToolsGui.Services.SAM.Native;

public static class Steam
{
    private struct NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true, BestFitMapping = false, ThrowOnUnmappableChar = true)]
        internal static extern IntPtr GetProcAddress(IntPtr module, string name);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetDllDirectory(string path);

        internal const uint LoadWithAlteredSearchPath = 8;
    }

    private static Delegate? GetExportDelegate<TDelegate>(IntPtr module, string name)
    {
        IntPtr address = NativeMethods.GetProcAddress(module, name);
        return address == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer(address, typeof(TDelegate));
    }

    private static TDelegate? GetExportFunction<TDelegate>(IntPtr module, string name)
        where TDelegate : class
    {
        var del = GetExportDelegate<TDelegate>(module, name);
        return del is null ? null : (TDelegate)(object)del;
    }

    private static IntPtr _handle = IntPtr.Zero;
    private static string? _customSteamPath;

    public static void SetCustomInstallPath(string? path)
    {
        _customSteamPath = path;
    }

    public static string? GetInstallPath()
    {
        if (!string.IsNullOrWhiteSpace(_customSteamPath) && Directory.Exists(_customSteamPath))
        {
            return _customSteamPath;
        }

        // Try standard registry locations
        var hkcuPath = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        if (!string.IsNullOrWhiteSpace(hkcuPath) && Directory.Exists(hkcuPath))
        {
            return Path.GetFullPath(hkcuPath.Replace('/', '\\'));
        }

        var hklm32Path = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
        if (!string.IsNullOrWhiteSpace(hklm32Path) && Directory.Exists(hklm32Path))
        {
            return Path.GetFullPath(hklm32Path.Replace('/', '\\'));
        }

        var hklmPath = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath", null) as string;
        if (!string.IsNullOrWhiteSpace(hklmPath) && Directory.Exists(hklmPath))
        {
            return Path.GetFullPath(hklmPath.Replace('/', '\\'));
        }

        return null;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private delegate IntPtr NativeCreateInterface(string version, IntPtr returnCode);

    private static NativeCreateInterface? _callCreateInterface;

    public static TClass? CreateInterface<TClass>(string version)
        where TClass : INativeWrapper, new()
    {
        if (_callCreateInterface is null) return default;
        IntPtr address = _callCreateInterface(version, IntPtr.Zero);
        if (address == IntPtr.Zero) return default;

        TClass instance = new();
        instance.SetupFunctions(address);
        return instance;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool NativeSteamGetCallback(int pipe, out Types.CallbackMessage message, out int call);

    private static NativeSteamGetCallback? _callSteamBGetCallback;

    public static bool GetCallback(int pipe, out Types.CallbackMessage message, out int call)
    {
        if (_callSteamBGetCallback is null)
        {
            message = default;
            call = 0;
            return false;
        }
        return _callSteamBGetCallback(pipe, out message, out call);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool NativeSteamFreeLastCallback(int pipe);

    private static NativeSteamFreeLastCallback? _callSteamFreeLastCallback;

    public static bool FreeLastCallback(int pipe)
    {
        if (_callSteamFreeLastCallback is null) return false;
        return _callSteamFreeLastCallback(pipe);
    }

    public static bool Load()
    {
        if (_handle != IntPtr.Zero)
        {
            return true;
        }

        string? path = GetInstallPath();
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        NativeMethods.SetDllDirectory(path + ";" + Path.Combine(path, "bin"));

        string dllName = Environment.Is64BitProcess ? "steamclient64.dll" : "steamclient.dll";
        string dllPath = Path.Combine(path, dllName);

        if (!File.Exists(dllPath))
        {
            // Fallback check in bin/ if present
            string altPath = Path.Combine(path, "bin", dllName);
            if (File.Exists(altPath))
            {
                dllPath = altPath;
            }
        }

        IntPtr module = NativeMethods.LoadLibraryEx(dllPath, IntPtr.Zero, NativeMethods.LoadWithAlteredSearchPath);
        if (module == IntPtr.Zero)
        {
            return false;
        }

        _callCreateInterface = GetExportFunction<NativeCreateInterface>(module, "CreateInterface");
        if (_callCreateInterface == null)
        {
            return false;
        }

        _callSteamBGetCallback = GetExportFunction<NativeSteamGetCallback>(module, "Steam_BGetCallback");
        if (_callSteamBGetCallback == null)
        {
            return false;
        }

        _callSteamFreeLastCallback = GetExportFunction<NativeSteamFreeLastCallback>(module, "Steam_FreeLastCallback");
        if (_callSteamFreeLastCallback == null)
        {
            return false;
        }

        _handle = module;
        return true;
    }
}
