using System.Globalization;
using System.Text.RegularExpressions;

namespace LivePhotoConvert.Desktop.Features.Playback;

/// <summary>showinfo 报告的一帧；时间为源时间轴上的绝对值，尚未归零。</summary>
public readonly record struct ShowInfoFrame(long Index, TimeSpan? Pts, TimeSpan? Duration);

/// <summary>
/// 解析 FFmpeg <c>showinfo</c> 滤镜写到标准错误的逐帧信息，用于取得每帧的显示时间戳。
/// </summary>
/// <remarks>
/// 典型格式（FFmpeg 6/7）：
/// <c>[Parsed_showinfo_2 @ 0x…] n:   3 pts:   1536 pts_time:0.1     duration:    512 duration_time:0.0333333 fmt:bgra …</c>；
/// 旧版本在 pts_time 后还有 <c>pos:</c> 且没有 duration。pts_time 只打印 6 位有效数字，
/// 因此优先用整数 pts 乘以 <c>config in time_base</c> 行给出的时间基换算。
/// </remarks>
public sealed partial class ShowInfoParser
{
    private long _timeBaseNumerator;
    private long _timeBaseDenominator;

    /// <summary>解析一行；时间基配置行会被记住并返回 false。</summary>
    public bool TryParse(string line, out ShowInfoFrame frame)
    {
        frame = default;
        if (!line.Contains("Parsed_showinfo", StringComparison.Ordinal))
        {
            return false;
        }

        if (TimeBaseRegex().Match(line) is { Success: true } timeBase)
        {
            var numerator = long.Parse(timeBase.Groups[1].ValueSpan, CultureInfo.InvariantCulture);
            var denominator = long.Parse(timeBase.Groups[2].ValueSpan, CultureInfo.InvariantCulture);
            if (numerator > 0 && denominator > 0)
            {
                (_timeBaseNumerator, _timeBaseDenominator) = (numerator, denominator);
            }

            return false;
        }

        if (FrameRegex().Match(line) is not { Success: true } match)
        {
            return false;
        }

        var index = long.Parse(match.Groups[1].ValueSpan, CultureInfo.InvariantCulture);
        var pts = FromTimeBase(match.Groups[2].ValueSpan) ?? FromSeconds(match.Groups[3].ValueSpan);
        TimeSpan? duration = null;
        if (DurationRegex().Match(line) is { Success: true } durationMatch)
        {
            duration = FromTimeBase(durationMatch.Groups[1].ValueSpan) ?? FromSeconds(durationMatch.Groups[2].ValueSpan);
            if (duration <= TimeSpan.Zero)
            {
                duration = null;
            }
        }

        frame = new ShowInfoFrame(index, pts, duration);
        return true;
    }

    private TimeSpan? FromTimeBase(ReadOnlySpan<char> value)
    {
        if (_timeBaseDenominator == 0 || !long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var units))
        {
            return null;
        }

        // Int128 防止大时间基（如 1/90000）乘以长时间戳溢出
        return TimeSpan.FromTicks((long)((Int128)units * _timeBaseNumerator * TimeSpan.TicksPerSecond / _timeBaseDenominator));
    }

    private static TimeSpan? FromSeconds(ReadOnlySpan<char> value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && double.IsFinite(seconds)
            ? TimeSpan.FromSeconds(seconds)
            : null;

    [GeneratedRegex(@"config in time_base: (\d+)/(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex TimeBaseRegex();

    [GeneratedRegex(@"\bn:\s*(\d+)\s+pts:\s*(\S+)\s+pts_time:(\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex FrameRegex();

    [GeneratedRegex(@"\bduration:\s*(\S+)\s+duration_time:(\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex DurationRegex();
}
