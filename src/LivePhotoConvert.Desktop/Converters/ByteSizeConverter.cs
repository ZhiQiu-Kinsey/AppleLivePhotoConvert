using System.Globalization;
using Avalonia.Data.Converters;

namespace LivePhotoConvert.Desktop.Converters;

/// <summary>
/// 把字节数格式化为 1024 进制的容量文本（如 428.5 MB）。
/// </summary>
public sealed class ByteSizeConverter : IValueConverter
{
    public static readonly ByteSizeConverter Instance = new();

    /// <summary>按固定区域格式化，中英界面显示一致。</summary>
    public static string Format(long bytes) => Format(bytes, CultureInfo.InvariantCulture);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Format(value switch
    {
        long l => l,
        int i => i,
        double d => (long)d,
        _ => 0
    }, culture);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static string Format(long bytes, IFormatProvider culture) => bytes switch
    {
        <= 0 => "0 B",
        < 1024 => string.Create(culture, $"{bytes} B"),
        < 1024 * 1024 => string.Create(culture, $"{bytes / 1024.0:F1} KB"),
        < 1024L * 1024 * 1024 => string.Create(culture, $"{bytes / (1024.0 * 1024.0):F1} MB"),
        _ => string.Create(culture, $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB")
    };
}
