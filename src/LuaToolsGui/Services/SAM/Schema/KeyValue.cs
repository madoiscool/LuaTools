using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LuaToolsGui.Services.SAM.Schema;

public class KeyValue
{
    private static readonly KeyValue _invalid = new();
    public string Name { get; set; } = "<root>";
    public KeyValueType Type { get; set; } = KeyValueType.None;
    public object? Value { get; set; }
    public bool Valid { get; set; }

    public List<KeyValue>? Children { get; set; }

    public KeyValue this[string key]
    {
        get
        {
            if (Children == null)
            {
                return _invalid;
            }

            var child = Children.FirstOrDefault(
                c => string.Compare(c.Name, key, StringComparison.OrdinalIgnoreCase) == 0);

            return child ?? _invalid;
        }
    }

    public string AsString(string defaultValue = "")
    {
        if (!Valid || Value == null)
        {
            return defaultValue;
        }

        return Value.ToString() ?? defaultValue;
    }

    public int AsInteger(int defaultValue = 0)
    {
        if (!Valid || Value == null)
        {
            return defaultValue;
        }

        switch (Type)
        {
            case KeyValueType.String:
            case KeyValueType.WideString:
            {
                return int.TryParse((string)Value, out int value) ? value : defaultValue;
            }
            case KeyValueType.Int32:
            {
                return (int)Value;
            }
            case KeyValueType.Float32:
            {
                return (int)(float)Value;
            }
            case KeyValueType.UInt64:
            {
                return (int)((ulong)Value & 0xFFFFFFFF);
            }
        }

        return defaultValue;
    }

    public float AsFloat(float defaultValue = 0.0f)
    {
        if (!Valid || Value == null)
        {
            return defaultValue;
        }

        switch (Type)
        {
            case KeyValueType.String:
            case KeyValueType.WideString:
            {
                return float.TryParse((string)Value, out float value) ? value : defaultValue;
            }
            case KeyValueType.Int32:
            {
                return (int)Value;
            }
            case KeyValueType.Float32:
            {
                return (float)Value;
            }
            case KeyValueType.UInt64:
            {
                return (ulong)Value & 0xFFFFFFFF;
            }
        }

        return defaultValue;
    }

    public bool AsBoolean(bool defaultValue = false)
    {
        if (!Valid || Value == null)
        {
            return defaultValue;
        }

        switch (Type)
        {
            case KeyValueType.String:
            case KeyValueType.WideString:
            {
                return int.TryParse((string)Value, out int value) ? value != 0 : defaultValue;
            }
            case KeyValueType.Int32:
            {
                return (int)Value != 0;
            }
            case KeyValueType.Float32:
            {
                return (int)(float)Value != 0;
            }
            case KeyValueType.UInt64:
            {
                return (ulong)Value != 0;
            }
        }

        return defaultValue;
    }

    public override string ToString()
    {
        if (!Valid) return "<invalid>";
        if (Type == KeyValueType.None) return Name;
        return $"{Name} = {Value}";
    }

    public static KeyValue? LoadAsBinary(string path)
    {
        if (!File.Exists(path)) return null;

        try
        {
            using var input = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var kv = new KeyValue();
            if (!kv.ReadAsBinary(input))
            {
                return null;
            }
            return kv;
        }
        catch
        {
            return null;
        }
    }

    public bool ReadAsBinary(Stream input)
    {
        Children = [];
        try
        {
            while (input.Position < input.Length)
            {
                var type = (KeyValueType)input.ReadValueU8();

                if (type == KeyValueType.End)
                {
                    break;
                }

                var current = new KeyValue
                {
                    Type = type,
                    Name = input.ReadStringUnicode(),
                };

                switch (type)
                {
                    case KeyValueType.None:
                    {
                        current.ReadAsBinary(input);
                        current.Valid = true;
                        break;
                    }
                    case KeyValueType.String:
                    {
                        current.Valid = true;
                        current.Value = input.ReadStringUnicode();
                        break;
                    }
                    case KeyValueType.WideString:
                    {
                        throw new FormatException("wstring is unsupported");
                    }
                    case KeyValueType.Int32:
                    {
                        current.Valid = true;
                        current.Value = input.ReadValueS32();
                        break;
                    }
                    case KeyValueType.UInt64:
                    {
                        current.Valid = true;
                        current.Value = input.ReadValueU64();
                        break;
                    }
                    case KeyValueType.Float32:
                    {
                        current.Valid = true;
                        current.Value = input.ReadValueF32();
                        break;
                    }
                    case KeyValueType.Color:
                    case KeyValueType.Pointer:
                    {
                        current.Valid = true;
                        current.Value = input.ReadValueU32();
                        break;
                    }
                    default:
                    {
                        throw new FormatException($"Unknown KeyValue type: {type}");
                    }
                }

                Children.Add(current);
            }

            Valid = true;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
