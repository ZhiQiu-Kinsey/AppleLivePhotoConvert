using System.Globalization;
using Avalonia.Data.Converters;

namespace LivePhotoConvert.Desktop.Converters;

/// <summary>
/// 数值阈值转换器：当绑定值（通常为容器宽度 Bounds.Width）大于等于 ConverterParameter 阈值时返回 true。
/// 用于窗口宽度自适应——区域过窄时优雅折叠次要信息，保证关键控件不被裁剪。
/// </summary>
public sealed class MinWidthToBoolConverter : IValueConverter
{
    public static readonly MinWidthToBoolConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double current = value is double d ? d : 0d;
        bool invert = false;
        string? paramStr = parameter?.ToString();
        if (paramStr is not null && (paramStr.StartsWith('<') || paramStr.StartsWith('!')))
        {
            invert = true;
            paramStr = paramStr[1..];
        }

        double threshold = double.TryParse(paramStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double pv) ? pv : 0d;
        return invert ? current < threshold : current >= threshold;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
