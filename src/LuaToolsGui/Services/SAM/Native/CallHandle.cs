using System;

namespace LuaToolsGui.Services.SAM.Native;

public struct CallHandle : IEquatable<CallHandle>
{
    public static readonly CallHandle Invalid = new(0);

    private readonly ulong _value;

    public CallHandle(ulong value)
    {
        _value = value;
    }

    public static implicit operator ulong(CallHandle handle) => handle._value;
    public static implicit operator CallHandle(ulong value) => new(value);

    public override bool Equals(object? obj) => obj is CallHandle handle && Equals(handle);
    public bool Equals(CallHandle other) => _value == other._value;
    public override int GetHashCode() => _value.GetHashCode();
    public override string ToString() => _value.ToString();

    public static bool operator ==(CallHandle left, CallHandle right) => left.Equals(right);
    public static bool operator !=(CallHandle left, CallHandle right) => !left.Equals(right);
}
