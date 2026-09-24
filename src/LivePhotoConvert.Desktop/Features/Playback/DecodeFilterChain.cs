using System.Collections.Frozen;
using System.Globalization;
using Avalonia;
using LivePhotoConvert.Core.External;

namespace LivePhotoConvert.Desktop.Features.Playback;

/// <summary>
/// 构造解码滤镜链：缩放到输出尺寸、转换为 BGRA，并在末尾用 showinfo 报告每帧时间戳。
/// </summary>
/// <remarks>
/// FFmpeg 默认的 autorotate 会在滤镜链之前插入转置，因此链中的宽高按显示方向（已转正）计算。
/// </remarks>
public static class DecodeFilterChain
{
    /// <summary>HDR 映射到 SDR 时参考白的亮度（cd/m²，ITU-R BT.2408）。</summary>
    private const int ReferenceWhiteNits = 203;

    /// <summary>checksum=0：逐帧校验和对 1080p 帧是可观的无谓开销。</summary>
    private const string ShowInfo = "showinfo=checksum=0";

    public static string Build(VideoStreamInfo source, PixelSize output) =>
        source.IsHdr ? BuildHdr(source, output) : BuildSdr(source, output);

    /// <summary>
    /// SDR：一次 swscale 完成缩放与 YUV→BGRA；显式给出输入矩阵与范围，避免未标注或容器与码流不一致时按默认值发灰或过曝。
    /// </summary>
    public static string BuildSdr(VideoStreamInfo source, PixelSize output) => string.Create(
        CultureInfo.InvariantCulture,
        $"scale=w={output.Width}:h={output.Height}:flags=lanczos+accurate_rnd+full_chroma_int:in_color_matrix={ScaleMatrix(source)}:in_range={ScaleRange(source.ColorRange)},format=bgra,{ShowInfo}");

    /// <summary>
    /// HDR：线性光下缩放（在第一个 zscale 内完成）、BT.2020→BT.709 色域转换、mobius 色调映射，再编码为 BT.709。
    /// </summary>
    /// <remarks>
    /// 末尾先转 8 位 gbrp 再交给 swscale 重排为 bgra：否则 swscale 要直接把 32 位浮点转成 bgra，
    /// 实测 1080p 慢约 25%。
    /// </remarks>
    public static string BuildHdr(VideoStreamInfo source, PixelSize output)
    {
        List<string> input = [$"w={output.Width}", $"h={output.Height}", "f=lanczos"];
        // 显式声明输入色彩属性：码流 VUI 缺失而容器 colr 有标注时，zscale 读不到帧属性会报「no path between colorspaces」
        if (ZscaleRange(source.ColorRange) is { } range)
        {
            input.Add($"rin={range}");
        }

        if (source.ColorSpace is { } matrix && ZscaleMatrices.Contains(matrix))
        {
            input.Add($"min={matrix}");
        }

        if (source.ColorPrimaries is { } primaries && ZscalePrimaries.Contains(primaries))
        {
            input.Add($"pin={primaries}");
        }

        if (source.ColorTransfer is { } transfer)
        {
            input.Add($"tin={transfer}");
        }

        input.Add("t=linear");
        input.Add(string.Create(CultureInfo.InvariantCulture, $"npl={ReferenceWhiteNits}"));
        return string.Create(
            CultureInfo.InvariantCulture,
            $"zscale={string.Join(':', input)},format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=mobius:desat=0,zscale=t=bt709:m=bt709:r=pc,format=gbrp,format=bgra,{ShowInfo}");
    }

    /// <summary>HDR 链需要的滤镜是否可用；SDR 只用 FFmpeg 内置滤镜。</summary>
    public static bool IsSupported(VideoStreamInfo source, IReadOnlySet<string> filters) =>
        !source.IsHdr || FfmpegFilters.SupportsHdrToneMapping(filters);

    /// <summary>
    /// swscale 对未标注矩阵的视频一律按 BT.601 解释；高清内容实际几乎都是 BT.709（与主流播放器的推断一致）。
    /// </summary>
    internal static string ScaleMatrix(VideoStreamInfo source) => source.ColorSpace switch
    {
        "bt709" => "bt709",
        "smpte170m" or "bt470bg" => "bt601",
        "smpte240m" => "smpte240m",
        "fcc" => "fcc",
        "bt2020nc" or "bt2020c" => "bt2020",
        null when source.DisplayWidth >= 1280 || source.DisplayHeight >= 720 => "bt709",
        _ => "auto"
    };

    internal static string ScaleRange(string? colorRange) => colorRange switch
    {
        "pc" => "full",
        "tv" => "limited",
        _ => "auto"
    };

    private static string? ZscaleRange(string? colorRange) => colorRange switch
    {
        "pc" => "full",
        "tv" => "limited",
        _ => null
    };

    private static readonly FrozenSet<string> ZscaleMatrices = FrozenSet.Create(
        StringComparer.Ordinal, "bt709", "bt470bg", "smpte170m", "bt2020nc", "bt2020c");

    private static readonly FrozenSet<string> ZscalePrimaries = FrozenSet.Create(
        StringComparer.Ordinal, "bt709", "bt470bg", "smpte170m", "bt2020", "smpte432");
}

public static class PlaybackGeometry
{
    /// <summary>
    /// 输出像素尺寸：源（显示方向）等比缩进「显示尺寸 × 缩放」的框内，不放大，宽高取偶数（yuv420 源与多数缩放路径要求）。
    /// </summary>
    /// <param name="sourceWidth">转正后的源宽度</param>
    /// <param name="sourceHeight">转正后的源高度</param>
    /// <param name="target">控件显示尺寸（逻辑像素）</param>
    /// <param name="scaling">屏幕缩放（RenderScaling）</param>
    public static PixelSize ComputeOutputSize(int sourceWidth, int sourceHeight, PixelSize target, double scaling)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(target.Width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(target.Height);
        if (!double.IsFinite(scaling) || scaling <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scaling));
        }

        var factor = Math.Min(1.0, Math.Min(target.Width * scaling / sourceWidth, target.Height * scaling / sourceHeight));
        return new PixelSize(Even(sourceWidth * factor, sourceWidth), Even(sourceHeight * factor, sourceHeight));
    }

    private static int Even(double value, int source)
    {
        var even = (int)Math.Round(value / 2, MidpointRounding.AwayFromZero) * 2;
        // 源本身为奇数时向下取偶，保证不放大
        return Math.Max(2, Math.Min(even, source - (source % 2)));
    }
}
