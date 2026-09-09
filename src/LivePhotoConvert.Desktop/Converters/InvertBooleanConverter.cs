using System.Globalization;
using Avalonia.Data.Converters;

namespace LivePhotoConvert.Desktop.Converters;

/// <summary>
/// 布尔值反转转换器
/// </summary>
public sealed class InvertBooleanConverter : IValueConverter
{
    public static readonly InvertBooleanConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b) return !b;
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b) return !b;
        return false;
    }
}
