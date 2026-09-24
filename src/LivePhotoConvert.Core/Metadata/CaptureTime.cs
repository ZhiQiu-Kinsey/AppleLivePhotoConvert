using System.Globalization;

namespace LivePhotoConvert.Core.Metadata;

/// <summary>
/// 拍摄时间：相机记录的当地时间，以及可选的 UTC 偏移。
/// </summary>
/// <remarks>
/// 照片的 DateTimeOriginal 通常只有当地时间，视频的 QuickTime 时间则以 UTC 存储。
/// 两边都有偏移时按绝对时刻比较，否则按当地时间比较，避免时区差被误判为拍摄时间差。
/// </remarks>
public readonly record struct CaptureTime(DateTime LocalTime, TimeSpan? Offset)
{
    /// <summary>
    /// 与另一时间的间隔（绝对值）。
    /// </summary>
    public TimeSpan DistanceTo(CaptureTime other) =>
        Offset is { } offset && other.Offset is { } otherOffset
            ? (new DateTimeOffset(LocalTime, offset) - new DateTimeOffset(other.LocalTime, otherOffset)).Duration()
            : (LocalTime - other.LocalTime).Duration();

    /// <summary>
    /// ExifTool 写入格式；有偏移时带上偏移，便于 ExifTool 正确换算为 QuickTime 的 UTC 时间。
    /// </summary>
    public string ToExifString() =>
        LocalTime.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture)
        + (Offset is { } offset ? FormatOffset(offset) : string.Empty);

    /// <summary>
    /// 解析 ExifTool 输出的时间，支持 <c>yyyy:MM:dd HH:mm:ss[.fff][±HH:mm|Z]</c> 及 ISO 8601 分隔符。
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> text, out CaptureTime value)
    {
        value = default;
        text = text.Trim();
        if (text.Length < 19
            || !TryParseInt(text[..4], out var year) || !TryParseInt(text[5..7], out var month) || !TryParseInt(text[8..10], out var day)
            || !TryParseInt(text[11..13], out var hour) || !TryParseInt(text[14..16], out var minute) || !TryParseInt(text[17..19], out var second)
            || year < 1900 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 59)
        {
            return false;
        }

        var rest = text[19..];
        if (!rest.IsEmpty && rest[0] == '.')
        {
            var end = 1;
            while (end < rest.Length && char.IsAsciiDigit(rest[end]))
            {
                end++;
            }

            rest = rest[end..];
        }

        TimeSpan? offset = null;
        if (!rest.IsEmpty && (offset = ParseOffset(rest)) is null)
        {
            return false;
        }

        value = new CaptureTime(new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified), offset);
        return true;
    }

    /// <summary>
    /// 解析 EXIF OffsetTime 类标签（<c>±HH:mm</c> 或 <c>Z</c>）；格式不符时返回 <c>null</c>。
    /// </summary>
    public static TimeSpan? ParseOffset(ReadOnlySpan<char> text)
    {
        text = text.Trim();
        if (text is ['Z' or 'z'])
        {
            return TimeSpan.Zero;
        }

        if (text.Length != 6 || text[0] is not ('+' or '-') || text[3] != ':'
            || !TryParseInt(text[1..3], out var hours) || !TryParseInt(text[4..6], out var minutes)
            || hours > 14 || minutes >= 60)
        {
            return null;
        }

        var magnitude = new TimeSpan(hours, minutes, 0);
        return text[0] == '-' ? -magnitude : magnitude;
    }

    private static string FormatOffset(TimeSpan offset) =>
        offset == TimeSpan.Zero ? "+00:00" : (offset < TimeSpan.Zero ? "-" : "+") + offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture);

    private static bool TryParseInt(ReadOnlySpan<char> text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
}
