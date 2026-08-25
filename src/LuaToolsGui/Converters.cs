using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LuaToolsGui;

/// <summary>
/// Loads an image path/URL into a BitmapImage with CacheOption=OnLoad, so cached cover files on
/// disk aren't left locked (allowing later refresh/clear). Handles local paths and http(s) URLs.
/// </summary>
public class ImagePathToSourceConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s || string.IsNullOrWhiteSpace(s)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.UriSource = new Uri(s, UriKind.Absolute);
            bmp.EndInit();
            return bmp;
        }
        catch
        {
            return null; // bad path/URL. Show nothing
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}



public class BooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null or "" ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Collapse a block unless there is something to show AND enough room to show a useful amount of it.
/// Bind [0] = a has-content bool, [1] = the AVAILABLE height, with the minimum in px as
/// ConverterParameter.
///
/// <para>
/// [1] must be the height of the CONTAINER, never of the element being hidden. Binding a control's own
/// Visibility to its own ActualHeight is circular: collapsing drives the height to 0, which fails the
/// test the other way and flips it back, and WPF oscillates.
/// </para>
/// </summary>
public class MinHeightToVisibilityConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        bool hasContent = values.Length > 0 && values[0] is true;
        double available = values.Length > 1 && values[1] is double h ? h : 0;
        double min = double.TryParse(parameter?.ToString(), NumberStyles.Any,
                                     CultureInfo.InvariantCulture, out double m) ? m : 0;

        return hasContent && available >= min ? Visibility.Visible : Visibility.Collapsed;
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Multi-binding equality: true when all bound values are equal. Used by the pager to mark the
/// active page button (binds the button's page number + the current page).</summary>
public class EqualityMultiConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is null || values.Length < 2 || values.Any(v => v is null)) return false;
        var first = System.Convert.ToInt64(values[0]);
        return values.Skip(1).All(v => System.Convert.ToInt64(v) == first);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Maps a Manage filter/sort option's stored value (an English key like "Recently added" or
/// "Any") to its localized DISPLAY label, leaving the stored value itself unchanged so all the
/// switch/equality logic in ManageViewModel keeps working. Unknown values (Steam genres/types/years)
/// pass through verbatim. They're data, not UI chrome.</summary>
public class FilterOptionDisplayConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s ? s switch
        {
            "Any" => Resources.Strings.Manage_Opt_Any,
            "Free" => Resources.Strings.Manage_Opt_Free,
            "Paid" => Resources.Strings.Manage_Opt_Paid,
            "Hide adult" => Resources.Strings.Manage_Content_HideAdult,
            "Adult only" => Resources.Strings.Manage_Content_AdultOnly,
            "Recently added" => Resources.Strings.Manage_Sort_RecentlyAdded,
            "Name (A–Z)" => Resources.Strings.Manage_Sort_NameAZ,
            "Release date (newest)" => Resources.Strings.Manage_Sort_ReleaseNewest,
            "Metacritic" => Resources.Strings.Manage_Sort_Metacritic,
            "Most reviewed" => Resources.Strings.Manage_Sort_MostReviewed,
            "All" => Resources.Strings.Manage_PageSize_All, // page-size dropdown
            _ => s, // genre/type/year/page-number data. Show as-is
        } : value;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Source status → badge color (mirrors the website's StatusBadge).</summary>
public class StatusToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Available = new((Color)ColorConverter.ConvertFromString("#22c55e"));
    private static readonly SolidColorBrush Unavailable = new((Color)ColorConverter.ConvertFromString("#6b7280"));
    private static readonly SolidColorBrush Other = new((Color)ColorConverter.ConvertFromString("#f97316"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        "available" => Available,
        "unavailable" => Unavailable,
        _ => Other,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public class EnumEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null || parameter == null) return false;
        return string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is true && parameter is string paramStr)
        {
            return Enum.Parse(targetType, paramStr);
        }
        return Binding.DoNothing;
    }
}
