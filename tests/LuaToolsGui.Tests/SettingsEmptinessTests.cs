using System.Reflection;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// SettingsService deletes settings.json when nothing is set. Any property the emptiness check misses is
/// silently lost whenever it is the only thing the user changed, which already happened twice.
/// </summary>
public class SettingsEmptinessTests
{
    public static IEnumerable<object[]> EveryProperty() =>
        typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => new object[] { p.Name });

    [Fact]
    public void DefaultSettings_AreEmpty() =>
        Assert.True(SettingsService.IsEmpty(new AppSettings()));

    [Theory]
    [MemberData(nameof(EveryProperty))]
    public void SettingAnySingleProperty_MakesItNonEmpty(string propertyName)
    {
        var settings = new AppSettings();
        var prop = typeof(AppSettings).GetProperty(propertyName)!;
        var type = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
        object value = type == typeof(bool) ? false
            : type == typeof(int) ? 0
            : type == typeof(string) ? "x"
            : throw new NotSupportedException($"Add a sample value for {type} ({propertyName}).");
        prop.SetValue(settings, value);

        Assert.False(SettingsService.IsEmpty(settings));
    }
}
