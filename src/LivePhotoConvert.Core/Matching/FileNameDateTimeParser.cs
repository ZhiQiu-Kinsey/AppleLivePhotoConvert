using System.Text.RegularExpressions;

namespace LivePhotoConvert.Core.Matching;

/// <summary>
/// 全格式文件名拍摄时间解析引擎
/// </summary>
/// <remarks>
/// 广泛适配苹果、小米、华为、三星、OPPO、vivo、Google Pixel 等各大品牌手机，
/// 以及微信、QQ、WhatsApp、Telegram 等社交软件和各类网盘、相册备份工具导出的各种时间戳与日期命名格式。
/// </remarks>
public static partial class FileNameDateTimeParser
{
    private const long MinUnixEpochMs = 946684800000L;   // 2000-01-01 00:00:00 UTC
    private const long MaxUnixEpochMs = 2524608000000L;  // 2050-01-01 00:00:00 UTC

    private const long MinUnixEpochSec = 946684800L;     // 2000-01-01 00:00:00 UTC
    private const long MaxUnixEpochSec = 2524608000L;    // 2050-01-01 00:00:00 UTC

    /// <summary>
    /// 尝试从文件名（包含或不包含路径与扩展名）中全通道解析拍摄时间
    /// </summary>
    /// <param name="fileNameOrPath">文件名或完整路径</param>
    /// <param name="result">解析出的本地拍摄时间</param>
    /// <returns>是否成功提取出合法时间</returns>
    public static bool TryParse(string fileNameOrPath, out DateTime result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(fileNameOrPath))
        {
            return false;
        }

        var fileName = Path.GetFileNameWithoutExtension(fileNameOrPath);

        // 通道 1：标准年月日时分秒（如 20260905_124144, 2026-09-05 12.41.44, 2026_09_05_12_41_44, PXL_20260905_124144123）
        if (TryExtractFullDateTime(fileName, out result))
        {
            return true;
        }

        // 通道 2：年月日 + 时分（无秒，如用户样本 2026_05_05_11_20_IMG_0277）
        if (TryExtractYearMonthDayHourMinute(fileName, out result))
        {
            return true;
        }

        // 通道 3：WhatsApp 专用格式（如 IMG-20260905-WA0001, VID-20260905-WA0012）
        if (TryExtractWhatsAppDate(fileName, out result))
        {
            return true;
        }

        // 通道 4：13位 Unix 毫秒时间戳（如微信 mmexport1683256803123, QQ_1683256803123）
        if (TryExtractUnixTimestampMilliseconds(fileName, out result))
        {
            return true;
        }

        // 通道 5：10位 Unix 秒级时间戳（如 1683256803, IMG_1683256803）
        if (TryExtractUnixTimestampSeconds(fileName, out result))
        {
            return true;
        }

        // 通道 6：纯年月日 8位（如 2026-09-05_IMG_0001, 20260905_IMG_0001）
        if (TryExtractDateOnly(fileName, out result))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// 通道 1：提取完整年月日时分秒（涵盖连续紧凑型与以 - _ . 空格等分隔的格式）
    /// </summary>
    private static bool TryExtractFullDateTime(string text, out DateTime result)
    {
        result = default;
        foreach (Match match in FullDateTimeRegex().Matches(text))
        {
            if (TryBuildDateTime(
                match.Groups["year"].ValueSpan,
                match.Groups["month"].ValueSpan,
                match.Groups["day"].ValueSpan,
                match.Groups["hour"].ValueSpan,
                match.Groups["minute"].ValueSpan,
                match.Groups["second"].ValueSpan,
                out result))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 通道 2：提取年月日 + 时分（秒数自动补 00）
    /// </summary>
    private static bool TryExtractYearMonthDayHourMinute(string text, out DateTime result)
    {
        result = default;
        foreach (Match match in YearMonthDayHourMinuteRegex().Matches(text))
        {
            if (TryBuildDateTime(
                match.Groups["year"].ValueSpan,
                match.Groups["month"].ValueSpan,
                match.Groups["day"].ValueSpan,
                match.Groups["hour"].ValueSpan,
                match.Groups["minute"].ValueSpan,
                "00",
                out result))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 通道 3：提取 WhatsApp 日期格式（如 IMG-20260905-WA0001）
    /// </summary>
    private static bool TryExtractWhatsAppDate(string text, out DateTime result)
    {
        result = default;
        var match = WhatsAppRegex().Match(text);
        if (match.Success)
        {
            return TryBuildDateTime(
                match.Groups["year"].ValueSpan,
                match.Groups["month"].ValueSpan,
                match.Groups["day"].ValueSpan,
                "00", "00", "00",
                out result);
        }

        return false;
    }

    /// <summary>
    /// 通道 4：提取 13 位毫秒 Unix 时间戳（如微信 mmexport1683256803123、QQ_1683256803123）
    /// </summary>
    private static bool TryExtractUnixTimestampMilliseconds(string text, out DateTime result)
    {
        result = default;
        foreach (Match match in UnixMsRegex().Matches(text))
        {
            if (long.TryParse(match.Groups[1].ValueSpan, out var ms) && ms >= MinUnixEpochMs && ms <= MaxUnixEpochMs)
            {
                try
                {
                    result = DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
                    return true;
                }
                catch (ArgumentOutOfRangeException)
                {
                    // 忽略无效时间戳
                }
            }
        }

        return false;
    }

    /// <summary>
    /// 通道 5：提取 10 位秒级 Unix 时间戳（如 1683256803）
    /// </summary>
    private static bool TryExtractUnixTimestampSeconds(string text, out DateTime result)
    {
        result = default;
        foreach (Match match in UnixSecRegex().Matches(text))
        {
            if (long.TryParse(match.Groups[1].ValueSpan, out var sec) && sec >= MinUnixEpochSec && sec <= MaxUnixEpochSec)
            {
                try
                {
                    result = DateTimeOffset.FromUnixTimeSeconds(sec).LocalDateTime;
                    return true;
                }
                catch (ArgumentOutOfRangeException)
                {
                    // 忽略无效时间戳
                }
            }
        }

        return false;
    }

    /// <summary>
    /// 通道 6：提取纯年月日（8位）
    /// </summary>
    private static bool TryExtractDateOnly(string text, out DateTime result)
    {
        result = default;
        foreach (Match match in DateOnlyRegex().Matches(text))
        {
            if (TryBuildDateTime(
                match.Groups["year"].ValueSpan,
                match.Groups["month"].ValueSpan,
                match.Groups["day"].ValueSpan,
                "00", "00", "00",
                out result))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 校验并构建合法的 DateTime（包含闰年与月份边界检查）
    /// </summary>
    private static bool TryBuildDateTime(
        ReadOnlySpan<char> yearSpan,
        ReadOnlySpan<char> monthSpan,
        ReadOnlySpan<char> daySpan,
        ReadOnlySpan<char> hourSpan,
        ReadOnlySpan<char> minuteSpan,
        ReadOnlySpan<char> secondSpan,
        out DateTime result)
    {
        result = default;
        if (!int.TryParse(yearSpan, out var year) || year < 1990 || year > 2099)
        {
            return false;
        }

        if (!int.TryParse(monthSpan, out var month) || month < 1 || month > 12)
        {
            return false;
        }

        if (!int.TryParse(daySpan, out var day) || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return false;
        }

        if (!int.TryParse(hourSpan, out var hour) || hour < 0 || hour > 23)
        {
            return false;
        }

        if (!int.TryParse(minuteSpan, out var minute) || minute < 0 || minute > 59)
        {
            return false;
        }

        if (!int.TryParse(secondSpan, out var second) || second < 0 || second > 59)
        {
            return false;
        }

        result = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Local);
        return true;
    }

    // ── 正则表达式（基于 .NET 10 GeneratedRegex 源生成器，完全 AOT 兼容且零分配） ──

    /// <summary>
    /// 完整年月日时分秒（支持分隔符如 2026-09-05-12-41-44、2026-09-05 12.41.44，或连写 20260905_124144，允许带毫秒如 PXL_20260905_124144123）
    /// </summary>
    [GeneratedRegex(@"(?<!\d)(?<year>199\d|20\d\d)[-_.\s]?(?<month>0[1-9]|1[0-2])[-_.\s]?(?<day>0[1-9]|[12]\d|3[01])(?:[-_.\sT]+|(?<=[0-9]{8}))(?<hour>[01]\d|2[0-3])[-_.\s:]?(?<minute>[0-5]\d)[-_.\s:]?(?<second>[0-5]\d)(?:\d{1,6})?(?!\d)")]
    private static partial Regex FullDateTimeRegex();

    /// <summary>
    /// 年月日 + 时分（无秒，如 2026_05_05_11_20、2026-05-05 11:20）
    /// </summary>
    [GeneratedRegex(@"(?<!\d)(?<year>199\d|20\d\d)[-_.\s](?<month>0[1-9]|1[0-2])[-_.\s](?<day>0[1-9]|[12]\d|3[01])(?:[-_.\sT]+)(?<hour>[01]\d|2[0-3])[-_.\s:](?<minute>[0-5]\d)(?!\d)")]
    private static partial Regex YearMonthDayHourMinuteRegex();

    /// <summary>
    /// WhatsApp 专属格式：IMG-YYYYMMDD-WA... 或 VID-YYYYMMDD-WA...
    /// </summary>
    [GeneratedRegex(@"(?:IMG|VID)[-_](?<year>20\d\d)(?<month>0[1-9]|1[0-2])(?<day>0[1-9]|[12]\d|3[01])[-_]WA", RegexOptions.IgnoreCase)]
    private static partial Regex WhatsAppRegex();

    /// <summary>
    /// 13 位毫秒 Unix 时间戳（如微信 mmexport1683256803123）
    /// </summary>
    [GeneratedRegex(@"(?<!\d)(1[0-9]{12})(?!\d)")]
    private static partial Regex UnixMsRegex();

    /// <summary>
    /// 10 位秒级 Unix 时间戳（如 1683256803）
    /// </summary>
    [GeneratedRegex(@"(?<!\d)(1[0-9]{9})(?!\d)")]
    private static partial Regex UnixSecRegex();

    /// <summary>
    /// 纯年月日（8位，如 2026-09-05 或 20260905）
    /// </summary>
    [GeneratedRegex(@"(?<!\d)(?<year>199\d|20\d\d)[-_.\s]?(?<month>0[1-9]|1[0-2])[-_.\s]?(?<day>0[1-9]|[12]\d|3[01])(?!\d)")]
    private static partial Regex DateOnlyRegex();
}
