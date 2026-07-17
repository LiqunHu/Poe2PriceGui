using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Poe2PriceGui.ViewModels;

namespace Poe2PriceGui.Converters;

/// <summary>
/// 过滤器视图模式 → Visibility：参数为 "StartPage" 或 "Editor"。
/// </summary>
[ValueConversion(typeof(FilterViewMode), typeof(Visibility))]
public class FilterViewModeToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is FilterViewMode mode && parameter is string target)
        {
            return mode.ToString() == target ? Visibility.Visible : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
