using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Poe2PriceGui.Converters;

/// <summary>
/// POE2 小地图图标形状名称 → Geometry，用于在 UI 中绘制图标预览。
/// 形状列表参考 chromatic-poe-main 的 Shape 枚举。
/// </summary>
[ValueConversion(typeof(string), typeof(Geometry))]
public class MinimapShapeToGeometryConverter : IValueConverter
{
    // 统一使用 32x32 的坐标系，便于缩放。
    private static readonly Dictionary<string, Geometry> Geometries = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Circle"] = Geometry.Parse("M 16,2 A 14,14 0 1 1 15.99,2 Z"),
        ["Square"] = Geometry.Parse("M 4,4 L 28,4 L 28,28 L 4,28 Z"),
        ["Diamond"] = Geometry.Parse("M 16,2 L 30,16 L 16,30 L 2,16 Z"),
        ["Triangle"] = Geometry.Parse("M 16,3 L 29,27 L 3,27 Z"),
        ["Star"] = Geometry.Parse("M 16,2 L 19,12 L 30,12 L 21,19 L 24,30 L 16,23 L 8,30 L 11,19 L 2,12 L 13,12 Z"),
        ["Cross"] = Geometry.Parse("M 12,2 L 20,2 L 20,12 L 30,12 L 30,20 L 20,20 L 20,30 L 12,30 L 12,20 L 2,20 L 2,12 L 12,12 Z"),
        ["Hexagon"] = Geometry.Parse("M 8,2 L 24,2 L 30,16 L 24,30 L 8,30 L 2,16 Z"),
        ["Pentagon"] = Geometry.Parse("M 16,2 L 30,12 L 25,30 L 7,30 L 2,12 Z"),
        ["Moon"] = Geometry.Parse("M 16,2 A 14,14 0 1 0 16,30 A 10,10 0 1 1 16,2 Z"),
        ["Raindrop"] = Geometry.Parse("M 16,2 C 26,10 30,20 16,30 C 2,20 6,10 16,2 Z"),
        ["Kite"] = Geometry.Parse("M 16,2 L 28,12 L 16,30 L 4,12 Z"),
        ["UpsideDownHouse"] = Geometry.Parse("M 2,10 L 16,30 L 30,10 L 24,10 L 24,2 L 8,2 L 8,10 Z"),
    };

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string shape && Geometries.TryGetValue(shape, out var geometry))
            return geometry;
        return Geometries["Circle"];
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
