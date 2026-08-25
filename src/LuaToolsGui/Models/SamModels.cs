using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using LuaToolsGui.Services.SAM.Native.Types;

namespace LuaToolsGui.Models;

public partial class SamGameInfo : ObservableObject
{
    public uint Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "normal"; // "normal", "demo", "mod", "junk"
    public string? ImageUrl { get; set; }
    public bool IsInstalledInLuaTools { get; set; }

    [ObservableProperty]
    private string? _displayCoverUrl;
}

public partial class SamAchievement : ObservableObject
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? IconNormal { get; set; }
    public string? IconLocked { get; set; }
    public bool IsHidden { get; set; }
    public int Permission { get; set; }

    public bool OriginalIsAchieved { get; set; }
    public DateTime? UnlockTime { get; set; }

    [ObservableProperty]
    private bool _isAchieved;

    [ObservableProperty]
    private string? _iconUrl;

    [ObservableProperty]
    private string? _lockedIconUrl;

    public bool IsModified => IsAchieved != OriginalIsAchieved;

    public string DisplayUnlockTime => UnlockTime.HasValue
        ? UnlockTime.Value.ToString("g")
        : string.Empty;

    public string EffectiveIconUrl => IsAchieved
        ? (IconUrl ?? LockedIconUrl ?? string.Empty)
        : (LockedIconUrl ?? IconUrl ?? string.Empty);

    partial void OnIsAchievedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsModified));
        OnPropertyChanged(nameof(EffectiveIconUrl));
    }
}

public partial class SamStat : ObservableObject
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public UserStatType StatType { get; set; } = UserStatType.Integer;

    public int MinInt { get; set; } = int.MinValue;
    public int MaxInt { get; set; } = int.MaxValue;
    public float MinFloat { get; set; } = float.MinValue;
    public float MaxFloat { get; set; } = float.MaxValue;
    public bool IncrementOnly { get; set; }
    public int Permission { get; set; }

    public int OriginalIntValue { get; set; }
    public float OriginalFloatValue { get; set; }

    [ObservableProperty]
    private int _intValue;

    [ObservableProperty]
    private float _floatValue;

    [ObservableProperty]
    private string _valueString = string.Empty;

    public bool IsFloat => StatType is UserStatType.Float or UserStatType.AverageRate;

    public bool IsModified
    {
        get
        {
            if (IsFloat)
            {
                return Math.Abs(FloatValue - OriginalFloatValue) > 0.0001f;
            }
            return IntValue != OriginalIntValue;
        }
    }

    public string DisplayType => StatType switch
    {
        UserStatType.Integer => "Integer",
        UserStatType.Float => "Float",
        UserStatType.AverageRate => "Average Rate",
        _ => StatType.ToString(),
    };

    public string RangeDescription
    {
        get
        {
            if (IsFloat)
            {
                if (MinFloat > float.MinValue && MaxFloat < float.MaxValue)
                    return $"[{MinFloat} .. {MaxFloat}]";
                if (MinFloat > float.MinValue)
                    return $">= {MinFloat}";
                if (MaxFloat < float.MaxValue)
                    return $"<= {MaxFloat}";
                return "Any float";
            }

            if (MinInt > int.MinValue && MaxInt < int.MaxValue)
                return $"[{MinInt} .. {MaxInt}]";
            if (MinInt > int.MinValue)
                return $">= {MinInt}";
            if (MaxInt < int.MaxValue)
                return $"<= {MaxInt}";
            return "Any integer";
        }
    }

    partial void OnIntValueChanged(int value)
    {
        _valueString = value.ToString();
        OnPropertyChanged(nameof(ValueString));
        OnPropertyChanged(nameof(IsModified));
    }

    partial void OnFloatValueChanged(float value)
    {
        _valueString = value.ToString("G");
        OnPropertyChanged(nameof(ValueString));
        OnPropertyChanged(nameof(IsModified));
    }

    partial void OnValueStringChanged(string value)
    {
        if (IsFloat)
        {
            if (float.TryParse(value, out float f))
            {
                _floatValue = f;
                OnPropertyChanged(nameof(FloatValue));
                OnPropertyChanged(nameof(IsModified));
            }
        }
        else
        {
            if (int.TryParse(value, out int i))
            {
                _intValue = i;
                OnPropertyChanged(nameof(IntValue));
                OnPropertyChanged(nameof(IsModified));
            }
        }
    }
}

public class SamGameStatsData
{
    public uint AppId { get; set; }
    public string GameName { get; set; } = string.Empty;
    public List<SamAchievement> Achievements { get; set; } = [];
    public List<SamStat> Stats { get; set; } = [];
    public string? ErrorMessage { get; set; }
}

public class SamStoreRequest
{
    public uint AppId { get; set; }
    public Dictionary<string, bool> Achievements { get; set; } = [];
    public Dictionary<string, int> IntStats { get; set; } = [];
    public Dictionary<string, float> FloatStats { get; set; } = [];
    public bool ResetAll { get; set; }
    public bool ResetAchievementsToo { get; set; }
}

public class SamStoreResult
{
    public bool Success { get; set; }
    public int AchievementsStored { get; set; }
    public int StatsStored { get; set; }
    public string? ErrorMessage { get; set; }
}
