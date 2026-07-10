using System.Globalization;
using System.IO;
using System.Windows.Data;

namespace Poe2PriceGui.Converters;

/// <summary>
/// 将完整文件路径转换为不带扩展名的文件名，用于下拉框显示。
/// </summary>
[ValueConversion(typeof(string), typeof(string))]
public class FileNameWithoutExtensionConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string path)
        {
            return Path.GetFileNameWithoutExtension(path);
        }
        return value;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value;
    }
}
