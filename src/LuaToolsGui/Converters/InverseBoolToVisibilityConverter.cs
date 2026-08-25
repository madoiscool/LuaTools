using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LuaToolsGui
{
    /// <summary>
    /// Inverts a boolean to Visibility: true → Collapsed, false → Visible.
    /// </summary>
    public sealed class InverseBoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return (value is bool b && b) ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return !(value is Visibility v && v == Visibility.Visible);
        }
    }
}
