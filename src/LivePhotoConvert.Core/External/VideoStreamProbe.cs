using System.Collections.Frozen;
using System.Globalization;
using System.Text.RegularExpressions;

namespace LivePhotoConvert.Core.External;

/// <summary>CIE 1931 xy 色度坐标。</summary>
public readonly record struct Chromaticity(double X, double Y);

/// <summary>HDR10 母版显示器色域与亮度（SMPTE ST 2086）。</summary>
/// <param name="MinLuminance">最低亮度，cd/m²</param>
/// <param name="MaxLuminance">最高亮度，cd/m²</param>
public sealed record MasteringDisplay(Chromaticity Red, Chromaticity Green, Chromaticity Blue, Chromaticity WhitePoint, double MinLuminance, double MaxLuminance)
{
    /// <summary>x265 <c>master-display</c> 参数：色度以 0.00002、亮度以 0.0001 cd/m² 为单位，顺序固定为 G、B、R。</summary>
    public string ToX265Parameter() => string.Create(
        CultureInfo.InvariantCulture,
        $"G({Chroma(Green.X)},{Chroma(Green.Y)})B({Chroma(Blue.X)},{Chroma(Blue.Y)})R({Chroma(Red.X)},{Chroma(Red.Y)})WP({Chroma(WhitePoint.X)},{Chroma(WhitePoint.Y)})L({Luma(MaxLuminance)},{Luma(MinLuminance)})");

    private static long Chroma(double value) => (long)Math.Round(value / 0.00002);

    private static long Luma(double value) => (long)Math.Round(value * 10000);
}

/// <summary>HDR10 内容亮度（MaxCLL / MaxFALL，cd/m²）。</summary>
public sealed record ContentLightLevel(int MaxCll, int MaxFall);

/// <summary>
/// 视频首个视频流的编码与色彩信息。色彩字段沿用 FFmpeg 的名称（如 <c>bt2020nc</c>、<c>arib-std-b67</c>），未声明时为 null。
/// </summary>
public sealed record VideoStreamInfo
{
    /// <summary>编码名，如 <c>hevc</c>、<c>h264</c>。</summary>
    public required string Codec { get; init; }

    /// <summary>编码档次，如 <c>Main 10</c>。</summary>
    public string? Profile { get; init; }

    /// <summary>容器中的四字符码，如 <c>hvc1</c>、<c>hev1</c>、<c>avc1</c>；Matroska 等无此信息。</summary>
    public string? CodecTag { get; init; }

    public string? PixelFormat { get; init; }

    public int? BitDepth { get; init; }

    /// <summary>编码宽度（未旋转）。</summary>
    public int Width { get; init; }

    /// <summary>编码高度（未旋转）。</summary>
    public int Height { get; init; }

    /// <summary>显示时需顺时针旋转的角度（0/90/180/270），与 ExifTool 的 <c>Rotation</c> 同义。</summary>
    public int Rotation { get; init; }

    /// <summary><c>tv</c>（有限范围）或 <c>pc</c>（全范围）。</summary>
    public string? ColorRange { get; init; }

    /// <summary>YUV 矩阵系数（FFmpeg 称 colorspace）。</summary>
    public string? ColorSpace { get; init; }

    public string? ColorPrimaries { get; init; }

    public string? ColorTransfer { get; init; }

    public double? FrameRate { get; init; }

    public TimeSpan? Duration { get; init; }

    /// <summary>容器带有杜比视界配置记录（dvcC/dvvC）。</summary>
    public bool HasDolbyVision { get; init; }

    public int? DolbyVisionProfile { get; init; }

    public MasteringDisplay? MasteringDisplay { get; init; }

    public ContentLightLevel? ContentLightLevel { get; init; }

    /// <summary>传递函数为 HLG 或 PQ。</summary>
    public bool IsHdr => ColorTransfer is VideoStreamProbe.TransferHlg or VideoStreamProbe.TransferPq;

    public bool IsHevc => Codec == "hevc";

    public bool IsH264 => Codec == "h264";

    public int DisplayWidth => Rotation is 90 or 270 ? Height : Width;

    public int DisplayHeight => Rotation is 90 or 270 ? Width : Height;
}

/// <summary>
/// 用 <c>ffmpeg -hide_banner -i</c> 的标准错误读取视频流信息。
/// </summary>
/// <remarks>不用 ffprobe：部分 FFmpeg 发行包（如国内镜像的 BtbN 包）只带 ffmpeg 可执行文件。</remarks>
public static partial class VideoStreamProbe
{
    public const string TransferHlg = "arib-std-b67";
    public const string TransferPq = "smpte2084";

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMinutes(1);

    /// <summary>
    /// 普通文件的 FFmpeg 输入。转为绝对路径，因为 <c>a:b.mp4</c> 这类相对路径会被当成名为 <c>a</c> 的协议。
    /// </summary>
    public static string FileInput(string path) => Path.GetFullPath(path);

    /// <summary>
    /// 用 <c>subfile</c> 协议直接读取文件中一段字节（如动态照片内嵌视频），无需先切出临时文件。
    /// </summary>
    /// <remarks>
    /// FFmpeg 只把 <c>subfile,</c> 与第一个 <c>,,:</c> 之间的部分当作选项，其后原样作为内层地址，
    /// 因此路径中的逗号、冒号、空格与中文都无需转义；内层地址同样要求绝对路径。
    /// </remarks>
    public static string SubfileInput(string path, long offset, long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        // end 为不含的结束偏移
        return string.Create(CultureInfo.InvariantCulture, $"subfile,,start,{offset},end,{offset + length},,:{Path.GetFullPath(path)}");
    }

    /// <summary>
    /// 探测首个视频流；无法打开或没有视频流时返回 null。
    /// </summary>
    /// <param name="ffmpegPath">ffmpeg 可执行文件</param>
    /// <param name="input">由 <see cref="FileInput"/> 或 <see cref="SubfileInput"/> 生成的输入地址</param>
    /// <param name="readFrameMetadata">额外解码首帧读取帧级 HDR10 静态元数据（母版显示器与内容亮度通常只在码流 SEI 中）</param>
    /// <param name="cancellationToken">取消令牌</param>
    public static async Task<VideoStreamInfo?> ProbeAsync(string ffmpegPath, string input, bool readFrameMetadata = false, CancellationToken cancellationToken = default)
    {
        List<string> arguments = ["-nostdin", "-hide_banner", "-i", input];
        if (readFrameMetadata)
        {
            arguments.AddRange(["-map", "0:v:0", "-an", "-sn", "-dn", "-frames:v", "1", "-vf", "showinfo", "-f", "null", "-"]);
        }

        // 只给 -i 时 FFmpeg 以「未指定输出」退出码 1 结束，流信息仍完整，故不看退出码
        var result = await ProcessRunner.RunAsync(ffmpegPath, arguments, cancellationToken, ProbeTimeout);
        return Parse(result.StandardError);
    }

    /// <summary>
    /// 解析 FFmpeg 标准错误中第一个输入的首个视频流；帧级信息（showinfo）用于补全流级缺失的字段。
    /// </summary>
    public static VideoStreamInfo? Parse(string standardError)
    {
        var lines = standardError.ReplaceLineEndings("\n").Split('\n');
        TimeSpan? duration = null;
        VideoStreamInfo? info = null;
        var inFirstInput = false;
        for (var i = 0; i < lines.Length && info is null; i++)
        {
            var line = lines[i];
            if (line.StartsWith("Input #", StringComparison.Ordinal))
            {
                if (inFirstInput)
                {
                    break;
                }

                inFirstInput = line.StartsWith("Input #0", StringComparison.Ordinal);
                continue;
            }

            if (line.StartsWith("Output #", StringComparison.Ordinal))
            {
                break;
            }

            if (!inFirstInput)
            {
                continue;
            }

            if (duration is null && DurationRegex().Match(line) is { Success: true } durationMatch)
            {
                duration = new TimeSpan(0, int.Parse(durationMatch.Groups[1].ValueSpan, CultureInfo.InvariantCulture), int.Parse(durationMatch.Groups[2].ValueSpan, CultureInfo.InvariantCulture))
                    + TimeSpan.FromSeconds(double.Parse(durationMatch.Groups[3].ValueSpan, CultureInfo.InvariantCulture));
                continue;
            }

            if (VideoStreamRegex().Match(line) is { Success: true } streamMatch)
            {
                info = ParseStreamLine(streamMatch.Groups[1].Value);
                if (info is not null)
                {
                    info = ApplyStreamDetails(info, lines.AsSpan(i + 1));
                }
            }
        }

        return info is null ? null : ApplyFrameDetails(info with { Duration = duration }, lines);
    }

    private static VideoStreamInfo? ParseStreamLine(string description)
    {
        var parts = SplitTopLevel(description);
        if (parts.Count == 0)
        {
            return null;
        }

        var head = parts[0];
        var codecEnd = head.IndexOf(' ');
        var codec = codecEnd < 0 ? head : head[..codecEnd];
        string? profile = null;
        string? tag = null;
        foreach (Match group in ParenthesizedRegex().Matches(head))
        {
            var content = group.Groups[1].Value;
            if (CodecTagRegex().Match(content) is { Success: true } tagMatch)
            {
                tag ??= tagMatch.Groups[1].Value;
            }
            else
            {
                profile ??= content;
            }
        }

        var info = new VideoStreamInfo { Codec = codec, Profile = profile, CodecTag = tag };
        double? fps = null;
        double? tbr = null;
        // 像素格式紧跟编码名；只认这一位置，避免把 lossless 等标记误当像素格式
        if (parts.Count > 1 && !SizeRegex().IsMatch(parts[1]) && PixelFormatRegex().Match(parts[1]) is { Success: true } pixel)
        {
            info = ApplyPixelFormat(info, pixel.Groups[1].Value, pixel.Groups[2].Success ? pixel.Groups[2].Value : null);
        }

        foreach (var part in parts.Skip(1))
        {
            if (SizeRegex().Match(part) is { Success: true } size)
            {
                info = info with
                {
                    Width = int.Parse(size.Groups[1].ValueSpan, CultureInfo.InvariantCulture),
                    Height = int.Parse(size.Groups[2].ValueSpan, CultureInfo.InvariantCulture)
                };
            }
            else if (RateRegex().Match(part) is { Success: true } rate)
            {
                var value = double.Parse(rate.Groups[1].ValueSpan, CultureInfo.InvariantCulture) * (rate.Groups[2].Length > 0 ? 1000 : 1);
                if (rate.Groups[3].ValueSpan is "fps")
                {
                    fps ??= value;
                }
                else
                {
                    tbr ??= value;
                }
            }
        }

        return info with { FrameRate = fps ?? tbr };
    }

    /// <summary>
    /// 像素格式括号内依次可能出现：「N bpc」、色彩范围、矩阵/原色/传递三元组（三者相同时只写一个）、场序、色度位置。
    /// </summary>
    private static VideoStreamInfo ApplyPixelFormat(VideoStreamInfo info, string pixelFormat, string? details)
    {
        info = info with { PixelFormat = pixelFormat, BitDepth = BitDepthOf(pixelFormat) };
        if (details is null)
        {
            return info;
        }

        foreach (var raw in details.Split(','))
        {
            var item = raw.Trim();
            if (item is "tv" or "pc")
            {
                info = info with { ColorRange = item };
            }
            else if (item.EndsWith(" bpc", StringComparison.Ordinal) && int.TryParse(item.AsSpan(0, item.Length - 4), CultureInfo.InvariantCulture, out var bpc))
            {
                info = info with { BitDepth = bpc };
            }
            else if (item.Split('/') is [var space, var primaries, var transfer])
            {
                info = info with { ColorSpace = Known(space), ColorPrimaries = Known(primaries), ColorTransfer = Known(transfer) };
            }
            else if (!NonColorItems.Contains(item) && !item.Contains(' ') && item.Length > 0)
            {
                info = info with { ColorSpace = Known(item), ColorPrimaries = Known(item), ColorTransfer = Known(item) };
            }
        }

        return info;
    }

    /// <summary>读取视频流行之后缩进的元数据与 Side data，直到下一个流或非缩进行。</summary>
    private static VideoStreamInfo ApplyStreamDetails(VideoStreamInfo info, ReadOnlySpan<string> following)
    {
        foreach (var line in following)
        {
            if (line.Length == 0 || !char.IsWhiteSpace(line[0]) || line.TrimStart().StartsWith("Stream #", StringComparison.Ordinal))
            {
                break;
            }

            if (DisplayMatrixRegex().Match(line) is { Success: true } matrix
                && double.TryParse(matrix.Groups[1].ValueSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out var counterClockwise))
            {
                // displaymatrix 给出的是逆时针角度
                info = info with { Rotation = NormalizeRotation(-counterClockwise) };
            }
            else if (RotateTagRegex().Match(line) is { Success: true } rotate)
            {
                // 旧版 FFmpeg 的 rotate 标签是顺时针角度；新旧版本同时输出时以 displaymatrix 为准
                if (info.Rotation == 0)
                {
                    info = info with { Rotation = NormalizeRotation(int.Parse(rotate.Groups[1].ValueSpan, CultureInfo.InvariantCulture)) };
                }
            }
            else if (DolbyVisionRegex().Match(line) is { Success: true } dovi)
            {
                info = info with
                {
                    HasDolbyVision = true,
                    DolbyVisionProfile = dovi.Groups[1].Success ? int.Parse(dovi.Groups[1].ValueSpan, CultureInfo.InvariantCulture) : null
                };
            }
            else
            {
                info = ApplyHdrMetadata(info, line);
            }
        }

        return info;
    }

    private static VideoStreamInfo ApplyFrameDetails(VideoStreamInfo info, string[] lines)
    {
        foreach (var line in lines)
        {
            if (!line.Contains("Parsed_showinfo", StringComparison.Ordinal))
            {
                continue;
            }

            info = ApplyHdrMetadata(info, line);
            if (FrameColorRegex().Match(line) is { Success: true } color)
            {
                info = info with
                {
                    ColorRange = info.ColorRange ?? Known(color.Groups[1].Value),
                    ColorSpace = info.ColorSpace ?? Known(color.Groups[2].Value),
                    ColorPrimaries = info.ColorPrimaries ?? Known(color.Groups[3].Value),
                    ColorTransfer = info.ColorTransfer ?? Known(color.Groups[4].Value)
                };
            }
        }

        return info;
    }

    /// <summary>流级（"Mastering Display Metadata, …"）与帧级（"Mastering display metadata: …"）写法都认，先出现者为准。</summary>
    private static VideoStreamInfo ApplyHdrMetadata(VideoStreamInfo info, string line)
    {
        if (info.MasteringDisplay is null
            && line.Contains("mastering display metadata", StringComparison.OrdinalIgnoreCase)
            && line.Contains("has_primaries:1", StringComparison.Ordinal)
            && line.Contains("has_luminance:1", StringComparison.Ordinal)
            && ParseMasteringDisplay(line) is { } mastering)
        {
            info = info with { MasteringDisplay = mastering };
        }

        if (info.ContentLightLevel is null && ContentLightRegex().Match(line) is { Success: true } cll)
        {
            info = info with
            {
                ContentLightLevel = new ContentLightLevel(
                    int.Parse(cll.Groups[1].ValueSpan, CultureInfo.InvariantCulture),
                    int.Parse(cll.Groups[2].ValueSpan, CultureInfo.InvariantCulture))
            };
        }

        return info;
    }

    private static MasteringDisplay? ParseMasteringDisplay(string line)
    {
        Chromaticity? red = null, green = null, blue = null, white = null;
        foreach (Match match in ChromaticityRegex().Matches(line))
        {
            var point = new Chromaticity(double.Parse(match.Groups[2].ValueSpan, CultureInfo.InvariantCulture), double.Parse(match.Groups[3].ValueSpan, CultureInfo.InvariantCulture));
            switch (match.Groups[1].ValueSpan)
            {
                case "r": red = point; break;
                case "g": green = point; break;
                case "b": blue = point; break;
                case "wp": white = point; break;
            }
        }

        double? min = null, max = null;
        foreach (Match match in LuminanceRegex().Matches(line))
        {
            var value = double.Parse(match.Groups[2].ValueSpan, CultureInfo.InvariantCulture);
            if (match.Groups[1].ValueSpan is "min")
            {
                min = value;
            }
            else
            {
                max = value;
            }
        }

        return red is { } r && green is { } g && blue is { } b && white is { } w && min is { } lo && max is { } hi
            ? new MasteringDisplay(r, g, b, w, lo, hi)
            : null;
    }

    /// <summary>按不在圆括号或方括号内的逗号切分。</summary>
    private static List<string> SplitTopLevel(string text)
    {
        List<string> parts = [];
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '(' or '[':
                    depth++;
                    break;
                case ')' or ']':
                    depth = Math.Max(0, depth - 1);
                    break;
                case ',' when depth == 0:
                    parts.Add(text[start..i].Trim());
                    start = i + 1;
                    break;
            }
        }

        parts.Add(text[start..].Trim());
        return parts;
    }

    internal static int? BitDepthOf(string pixelFormat)
    {
        if (PlanarDepthRegex().Match(pixelFormat) is { Success: true } planar)
        {
            return int.Parse(planar.Groups[1].ValueSpan, CultureInfo.InvariantCulture);
        }

        if (PackedDepthRegex().Match(pixelFormat) is { Success: true } packed)
        {
            return int.Parse(packed.Groups[1].ValueSpan, CultureInfo.InvariantCulture);
        }

        if (pixelFormat is "rgb48le" or "rgb48be" or "bgr48le" or "bgr48be" or "rgba64le" or "rgba64be" or "bgra64le" or "bgra64be")
        {
            return 16;
        }

        // 不带字节序后缀的格式（yuv420p、nv12、bgra 等）都是 8 位
        return pixelFormat.EndsWith("le", StringComparison.Ordinal) || pixelFormat.EndsWith("be", StringComparison.Ordinal) || pixelFormat == "none"
            ? null
            : 8;
    }

    private static int NormalizeRotation(double degrees)
    {
        var rounded = (int)Math.Round(degrees / 90.0) * 90;
        return ((rounded % 360) + 360) % 360;
    }

    private static string? Known(string value) => value is "unknown" or "unspecified" or "reserved" or "" ? null : value;

    private static readonly FrozenSet<string> NonColorItems = FrozenSet.Create(
        StringComparer.Ordinal,
        "progressive", "left", "center", "topleft", "top", "bottomleft", "bottom", "unknown");

    [GeneratedRegex(@"^\s*Duration: (\d+):(\d{2}):(\d{2}(?:\.\d+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex DurationRegex();

    [GeneratedRegex(@"^\s*Stream #\d+:\d+\S*: Video: (.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex VideoStreamRegex();

    [GeneratedRegex(@"\(([^()]*)\)", RegexOptions.CultureInvariant)]
    private static partial Regex ParenthesizedRegex();

    [GeneratedRegex(@"^(\S+) / 0x[0-9A-Fa-f]+$", RegexOptions.CultureInvariant)]
    private static partial Regex CodecTagRegex();

    [GeneratedRegex(@"^(\d+)x(\d+)(?:\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex SizeRegex();

    [GeneratedRegex(@"^([\d.]+)(k?) (fps|tbr)\b", RegexOptions.CultureInvariant)]
    private static partial Regex RateRegex();

    [GeneratedRegex(@"^([a-z][a-z0-9_]*)(?:\((.*)\))?$", RegexOptions.CultureInvariant)]
    private static partial Regex PixelFormatRegex();

    [GeneratedRegex(@"displaymatrix: rotation of (-?[\d.]+) degrees", RegexOptions.CultureInvariant)]
    private static partial Regex DisplayMatrixRegex();

    [GeneratedRegex(@"^\s*rotate\s*:\s*(-?\d+)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex RotateTagRegex();

    [GeneratedRegex(@"DOVI configuration record(?::.*?\bprofile: (\d+))?", RegexOptions.CultureInvariant)]
    private static partial Regex DolbyVisionRegex();

    // FFmpeg 打印蓝色分量时 x、y 之间只有空格没有逗号
    [GeneratedRegex(@"\b(r|g|b|wp)\(\s*([\d.]+)[,\s]+([\d.]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex ChromaticityRegex();

    [GeneratedRegex(@"\b(min|max)_luminance=([\d.]+)", RegexOptions.CultureInvariant)]
    private static partial Regex LuminanceRegex();

    [GeneratedRegex(@"MaxCLL=(\d+),\s*MaxFALL=(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex ContentLightRegex();

    [GeneratedRegex(@"color_range:(\S+) color_space:(\S+) color_primaries:(\S+) color_trc:(\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex FrameColorRegex();

    [GeneratedRegex(@"^(?:[a-z][a-z0-9]*p|gray|ya)(\d{1,2})(?:le|be)$", RegexOptions.CultureInvariant)]
    private static partial Regex PlanarDepthRegex();

    // p010le、p210le、y210le、x2rgb10le 这类格式把位深写在名字末尾
    [GeneratedRegex(@"^(?:[pyP]\d|x2rgb|x2bgr)(\d{2})(?:le|be)$", RegexOptions.CultureInvariant)]
    private static partial Regex PackedDepthRegex();
}
