using System.Globalization;
using Avalonia.Data.Converters;

namespace LivePhotoConvert.Desktop.Converters;

/// <summary>
/// 相等比较转换器：当绑定值等于 ConverterParameter 时返回 true，否则返回 false。
/// 用于视图菜单中指示当前选中项的圆点（•）可见性。
/// </summary>
public sealed class EqualityConverter : IValueConverter
{
    public static readonly EqualityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null)
        {
            return false;
        }

        return string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
