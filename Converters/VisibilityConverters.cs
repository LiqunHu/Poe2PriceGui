using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Poe2PriceGui.Converters;

/// <summary>
/// 非空字符串 → Visible，空/null → Collapsed。
/// </summary>
public class NonEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is string s && !string.IsNullOrWhiteSpace(s) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 反转布尔 → Visibility（true → Collapsed，false → Visible）。
/// </summary>
public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool b && !b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 非零集合计数 → Visible。
/// </summary>
public class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int count) return count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (value is System.Collections.ICollection col) return col.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 非零数字 → Visible，0/null → Collapsed。
/// </summary>
public class NonZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == null) return Visibility.Collapsed;
        return value switch
        {
            int i => i > 0 ? Visibility.Visible : Visibility.Collapsed,
            double d => d > 0 ? Visibility.Visible : Visibility.Collapsed,
            decimal dec => dec > 0 ? Visibility.Visible : Visibility.Collapsed,
            _ => Visibility.Collapsed
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 颜色名称字符串 → Brush（参考 xiletrade Strings.Color 的 Gold/DeepSkyBlue/Peru/Green 等命名）。
/// 空/null → White（默认前景色）。
/// </summary>
public class ColorNameToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string s || string.IsNullOrWhiteSpace(s))
            return Brushes.White;
        try
        {
            return (Brush?)new BrushConverter().ConvertFromString(s) ?? Brushes.White;
        }
        catch
        {
            return Brushes.White;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
