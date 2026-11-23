using System.Collections.Frozen;
using LivePhotoConvert.Core.Abstractions;
using LivePhotoConvert.Core.External.Tools;
using LivePhotoConvert.Core.Io;

namespace LivePhotoConvert.Core.External;

/// <summary>
/// 基于 FFmpeg 的视频容器转换：优先流复制，失败或需要烧录方向时再重新编码。
/// HDR 源重新编码走 libx265 10-bit 并保留色彩元数据；HEVC 输出一律标记 hvc1。
/// </summary>
public sealed class FfmpegVideoConverter : IVideoConverter
{
    private const string HevcEncoder = "libx265";
    private const string HdrPixelFormat = "yuv420p10le";
    private const string SdrPixelFormat = "yuv420p";

    /// <summary>色调映射的参考白（nits）：HDR 线性光按它归一，与播放端一致。</summary>
    private const string ToneMapReferenceWhiteNits = "100";
    private const string SdrPrimaries = "bt709";
    private const string SdrTransfer = "bt709";
    private const string SdrMatrix = "bt709";

    private readonly string _executablePath;
    private readonly Lazy<Task<FrozenSet<string>>> _encoders;
    private readonly Lazy<Task<FrozenSet<string>>> _filters;

    private FfmpegVideoConverter(string executablePath, IEnumerable<string>? availableEncoders, IEnumerable<string>? availableFilters)
    {
        _executablePath = executablePath;
        _encoders = availableEncoders is null
            ? new(() => ListEncodersAsync(executablePath))
            : new(Task.FromResult(availableEncoders.ToFrozenSet(StringComparer.Ordinal)));
        _filters = availableFilters is null
            ? new(() => ListFiltersAsync(executablePath))
            : new(Task.FromResult(availableFilters.ToFrozenSet(StringComparer.Ordinal)));
    }

    public static string ExecutableName => OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

    /// <param name="executablePath">指定的 ffmpeg 路径；null 时按默认位置查找</param>
    /// <param name="availableEncoders">已知可用的编码器名单；null 时在首次需要重新编码 HDR 源时运行 <c>ffmpeg -encoders</c> 探测（只支持 8-bit 的 libx265 视为缺失）</param>
    /// <param name="availableFilters">已知可用的滤镜名单；null 时在首次需要色调映射时运行 <c>ffmpeg -filters</c> 探测</param>
    /// <exception cref="FileNotFoundException">找不到 FFmpeg</exception>
    public static FfmpegVideoConverter Create(string? executablePath = null, IEnumerable<string>? availableEncoders = null, IEnumerable<string>? availableFilters = null) =>
        new(ToolLocator.Find(ExecutableName, executablePath, "ffmpeg", "FFmpeg", "bin")
            ?? throw new ToolNotFoundException(ExecutableName),
            availableEncoders,
            availableFilters);

    public Task ConvertToMp4Async(string sourcePath, string destinationPath, VideoConversionOptions? options = null, CancellationToken cancellationToken = default) =>
        ConvertAsync(sourcePath, destinationPath, VideoContainer.Mp4, options ?? VideoConversionOptions.Default, cancellationToken);

    public Task RemuxToMovAsync(string sourcePath, string destinationPath, VideoConversionOptions? options = null, CancellationToken cancellationToken = default) =>
        ConvertAsync(sourcePath, destinationPath, VideoContainer.Mov, options ?? VideoConversionOptions.Default, cancellationToken);

    private async Task ConvertAsync(string sourcePath, string destinationPath, VideoContainer container, VideoConversionOptions options, CancellationToken cancellationToken)
    {
        var input = VideoStreamProbe.FileInput(sourcePath);
        // 输出也用绝对路径：以 "-" 开头或含冒号的相对路径会被 FFmpeg 误当选项或协议
        var output = Path.GetFullPath(destinationPath);
        var source = await VideoStreamProbe.ProbeAsync(_executablePath, input, cancellationToken: cancellationToken);

        string? remuxError = null;
        // 色调映射必须重新编码，流复制会把 HDR 标记原样带过去；SDR 源不需要，仍可流复制
        var needsToneMap = options.ToneMapToSdr && source is { IsHdr: true };
        if (needsToneMap)
        {
            var filters = await _filters.Value.WaitAsync(cancellationToken);
            if (!filters.Contains(ZscaleFilter) || !filters.Contains(TonemapFilter))
            {
                // 不能悄悄流复制：调用方要据此决定退回保留 HDR 并告知用户
                throw new VideoConversionException(
                    VideoConversionError.HdrToneMapUnavailable,
                    $"源视频为 HDR，当前 FFmpeg 缺少 {ZscaleFilter} 或 {TonemapFilter} 滤镜，无法色调映射为 SDR。");
            }
        }

        if (!options.ForceTranscode && !options.BakeOrientation && !needsToneMap)
        {
            var remux = await ProcessRunner.RunAsync(_executablePath, BuildRemuxArguments(input, output, source, container), cancellationToken);
            if (remux.Success && IsNonEmptyFile(output))
            {
                return;
            }

            remuxError = remux.ErrorSummary;
            FileHelper.TryDeleteFile(output);
        }

        var (planned, hevc) = await PlanTranscodeAsync(input, source, options, remuxError, cancellationToken);
        var transcode = await ProcessRunner.RunAsync(
            _executablePath,
            BuildTranscodeArguments(input, output, planned, hevc, options.BakeOrientation, options.ToneMapToSdr, container),
            cancellationToken);
        if (transcode.Success && IsNonEmptyFile(output))
        {
            return;
        }

        FileHelper.TryDeleteFile(output);
        throw new VideoConversionException(VideoConversionError.EncodeFailed, remuxError is null
            ? $"FFmpeg 重新编码失败：{transcode.ErrorSummary}"
            : $"FFmpeg 换容器失败：{remuxError}；重新编码失败：{transcode.ErrorSummary}");
    }

    /// <summary>决定重新编码用 HEVC 10-bit 还是 H.264 8-bit；HDR 源缺 libx265 时除非调用方允许，否则拒绝降级。</summary>
    private async Task<(VideoStreamInfo? Source, bool Hevc)> PlanTranscodeAsync(
        string input, VideoStreamInfo? source, VideoConversionOptions options, string? remuxError, CancellationToken cancellationToken)
    {
        if (source is not { IsHdr: true })
        {
            return (source, false);
        }

        // 色调映射为 SDR 后输出是 8-bit BT.709；仍用 HEVC 与苹果实况一致（原生实况视频是 HEVC）
        if (options.ToneMapToSdr)
        {
            var mapped = await _encoders.Value.WaitAsync(cancellationToken);
            return (source, mapped.Contains(HevcEncoder));
        }

        var encoders = await _encoders.Value.WaitAsync(cancellationToken);
        if (encoders.Contains(HevcEncoder))
        {
            // HDR10 母版显示器与内容亮度多数只在码流 SEI 中，流信息里看不到，需解码首帧读取
            if (source.MasteringDisplay is null && source.ContentLightLevel is null
                && await VideoStreamProbe.ProbeAsync(_executablePath, input, readFrameMetadata: true, cancellationToken) is { } detailed)
            {
                source = detailed;
            }

            return (source, true);
        }

        if (options.AllowHdrDowngrade)
        {
            return (source, false);
        }

        var transfer = source.ColorTransfer == VideoStreamProbe.TransferPq ? "PQ" : "HLG";
        throw new VideoConversionException(
            VideoConversionError.HdrEncoderUnavailable,
            $"源视频为 HDR（{transfer}），当前 FFmpeg 缺少 {HevcEncoder} 编码器，无法以 10-bit 保真重新编码；请更换带 {HevcEncoder} 的 FFmpeg。"
            + (remuxError is null ? string.Empty : $"换容器失败：{remuxError}"));
    }

    internal static IReadOnlyList<string> BuildRemuxArguments(string input, string output, VideoStreamInfo? source, VideoContainer container)
    {
        List<string> arguments = ["-nostdin", "-hide_banner", "-loglevel", "error", "-i", input, "-map", "0:v:0", "-map", "0:a:0?", "-c:v", "copy"];
        if (source is { IsHevc: true })
        {
            // FFmpeg 复制 HEVC 默认沿用源标记（常为 hev1），iOS 与 QuickTime 只识别 hvc1
            arguments.AddRange(["-tag:v", "hvc1"]);
        }

        AddAudioAndContainer(arguments, output, container);
        return arguments;
    }

    /// <summary>
    /// 编码器默认 CRF（x264 23、x265 28）面向带宽；实况视频只有几秒，体积不敏感，取视觉无损的质量。
    /// </summary>
    private const string TranscodeCrf = "18";

    internal static IReadOnlyList<string> BuildTranscodeArguments(string input, string output, VideoStreamInfo? source, bool hevc, bool bakeOrientation, bool toneMapToSdr, VideoContainer container)
    {
        List<string> arguments = ["-nostdin", "-hide_banner", "-loglevel", "error"];
        if (!bakeOrientation)
        {
            // 默认 autorotate 会把像素转正并丢掉显示矩阵；关闭后矩阵原样写入输出，与流复制一致。
            // FFmpeg 7 起 -autorotate 只接受 -noautorotate 写法，旧式 "-autorotate 0" 会报选项位置错误。
            arguments.Add("-noautorotate");
        }

        // 只有 HDR 源需要色调映射；SDR 源（如小米）保持原有曲线，套用映射会改变画面观感
        var applyToneMap = toneMapToSdr && source is { IsHdr: true };
        // 色调映射后是 8-bit SDR 画面，即使编码器仍用 HEVC 也不能标 10-bit
        var pixelFormat = hevc && !applyToneMap ? HdrPixelFormat : SdrPixelFormat;
        arguments.AddRange(["-i", input, "-map", "0:v:0", "-map", "0:a:0?"]);
        // 统一输出有限范围：像素格式不变时 FFmpeg 不会自动换算范围，只改标记会让全范围源发灰或过曝
        arguments.AddRange(["-vf", applyToneMap ? BuildToneMapFilter(source!) : BuildPassThroughFilter(source, pixelFormat)]);
        if (hevc)
        {
            arguments.AddRange(["-c:v", HevcEncoder, "-pix_fmt", pixelFormat, "-crf", TranscodeCrf, "-x265-params", BuildX265Parameters(applyToneMap ? null : source), "-tag:v", "hvc1"]);
        }
        else
        {
            arguments.AddRange(["-c:v", "libx264", "-pix_fmt", pixelFormat, "-crf", TranscodeCrf]);
        }

        // 色调映射后整段画面已是 BT.709 SDR，沿用源的三元组会让播放器按 HDR 解释而发灰
        var (primaries, transfer, space) = applyToneMap
            ? (SdrPrimaries, SdrTransfer, SdrMatrix)
            : (source?.ColorPrimaries, source?.ColorTransfer, source?.ColorSpace);
        if (primaries is { } p)
        {
            arguments.AddRange(["-color_primaries", p]);
        }

        if (transfer is { } t)
        {
            arguments.AddRange(["-color_trc", t]);
        }

        if (space is { } s)
        {
            arguments.AddRange(["-colorspace", s]);
        }

        arguments.AddRange(["-color_range", "tv"]);
        AddAudioAndContainer(arguments, output, container);
        return arguments;
    }

    /// <summary>
    /// 不做色调映射时的滤镜：先把源的色彩三元组写到帧上，再统一换算为有限范围。
    /// </summary>
    /// <remarks>
    /// FFmpeg 7.1 起编码器的色彩属性取自帧，只有矩阵与范围参与滤镜协商；码流 VUI 缺失时帧上的原色与传递特性是 unknown，
    /// 会覆盖 <c>-color_primaries</c> / <c>-color_trc</c>，HDR 输出因此丢掉 HLG / PQ 标记。用 setparams 显式声明可兼容所有版本。
    /// </remarks>
    internal static string BuildPassThroughFilter(VideoStreamInfo? source, string pixelFormat)
    {
        List<string> declared = [];
        if (source?.ColorPrimaries is { } primaries && X265Primaries.Contains(primaries))
        {
            declared.Add($"color_primaries={primaries}");
        }

        if (source?.ColorTransfer is { } transfer && X265Transfers.Contains(transfer))
        {
            declared.Add($"color_trc={transfer}");
        }

        if (source?.ColorSpace is { } space && X265Matrices.Contains(space))
        {
            declared.Add($"colorspace={space}");
        }

        var scale = $"scale=out_range=tv,format={pixelFormat}";
        return declared.Count == 0 ? scale : $"setparams={string.Join(':', declared)},{scale}";
    }

    /// <summary>
    /// HDR → BT.709 SDR：按源的色彩属性线性化，在 BT.2020 线性光下按参考白做 hable 映射，再按 BT.709 编码为 8-bit 有限范围。
    /// </summary>
    /// <remarks>
    /// 输入属性必须显式写进第一个 zscale，且同时给出输出色域：码流 VUI 缺失而容器 colr 有标注时，
    /// zscale 从帧上读不到属性，会报「no path between colorspaces」。
    /// </remarks>
    internal static string BuildToneMapFilter(VideoStreamInfo source)
    {
        List<string> input = [];
        if (source.ColorRange switch { "pc" => "full", "tv" => "limited", _ => null } is { } range)
        {
            input.Add($"rin={range}");
        }

        if (source.ColorSpace is { } matrix && ZscaleMatrices.Contains(matrix))
        {
            input.Add($"min={matrix}");
        }

        var primaries = source.ColorPrimaries is { } declared && ZscalePrimaries.Contains(declared) ? declared : "bt2020";
        input.Add($"pin={primaries}");
        if (source.ColorTransfer is { } transfer)
        {
            input.Add($"tin={transfer}");
        }

        input.AddRange(["t=linear", $"npl={ToneMapReferenceWhiteNits}", $"p={primaries}"]);
        return $"zscale={string.Join(':', input)},format=gbrpf32le,tonemap=hable:desat=0,"
            + $"zscale=t={SdrTransfer}:m={SdrMatrix}:p={SdrPrimaries}:r=limited,format={SdrPixelFormat}";
    }

    /// <summary>
    /// 色彩三元组同时写进 VUI（x265 参数）与容器（colr），HDR10 静态元数据写进 SEI；
    /// 只传 x265 认识的名称，未知值交给 x265 默认的 unknown。
    /// </summary>
    internal static string BuildX265Parameters(VideoStreamInfo? source)
    {
        List<string> parameters = ["log-level=error"];
        if (source?.ColorPrimaries is { } primaries && X265Primaries.Contains(primaries))
        {
            parameters.Add($"colorprim={primaries}");
        }

        if (source?.ColorTransfer is { } transfer && X265Transfers.Contains(transfer))
        {
            parameters.Add($"transfer={transfer}");
        }

        if (source?.ColorSpace is { } space && X265Matrices.Contains(space))
        {
            parameters.Add($"colormatrix={space}");
        }

        parameters.Add("range=limited");
        if (source?.MasteringDisplay is { } mastering)
        {
            parameters.Add($"master-display={mastering.ToX265Parameter()}");
        }

        if (source?.ContentLightLevel is { } light)
        {
            parameters.Add($"max-cll={light.MaxCll},{light.MaxFall}");
        }

        return string.Join(':', parameters);
    }

    private static void AddAudioAndContainer(List<string> arguments, string output, VideoContainer container)
    {
        if (container == VideoContainer.Mov)
        {
            arguments.AddRange(["-c:a", "pcm_s16le", "-movflags", "+faststart", "-f", "mov"]);
        }
        else
        {
            arguments.AddRange(["-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart"]);
        }

        arguments.AddRange(["-y", output]);
    }

    private static async Task<FrozenSet<string>> ListEncodersAsync(string executablePath)
    {
        try
        {
            var result = await ProcessRunner.RunAsync(executablePath, ["-nostdin", "-hide_banner", "-encoders"], CancellationToken.None, TimeSpan.FromSeconds(30));
            var encoders = ToolOutputParser.ParseEncoderNames(result.StandardOutput).ToHashSet(StringComparer.Ordinal);
            // 8-bit 版 x265 也注册 libx265，却无法输出 10-bit；对 HDR 保真转码等同于不可用，按缺失处理才能报 HdrEncoderUnavailable
            if (encoders.Contains(HevcEncoder))
            {
                var help = await ProcessRunner.RunAsync(executablePath, ["-nostdin", "-hide_banner", "-h", "encoder=" + HevcEncoder], CancellationToken.None, TimeSpan.FromSeconds(30));
                if (!ToolOutputParser.ParsePixelFormats(help.StandardOutput).Contains(HdrPixelFormat))
                {
                    encoders.Remove(HevcEncoder);
                }
            }

            return encoders.ToFrozenSet(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            return FrozenSet<string>.Empty;
        }
    }

    private static async Task<FrozenSet<string>> ListFiltersAsync(string executablePath)
    {
        try
        {
            var result = await ProcessRunner.RunAsync(executablePath, ["-nostdin", "-hide_banner", "-filters"], CancellationToken.None, TimeSpan.FromSeconds(30));
            return ToolOutputParser.ParseFilterNames(result.StandardOutput).ToFrozenSet(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            return FrozenSet<string>.Empty;
        }
    }

    private static bool IsNonEmptyFile(string path) => new FileInfo(path) is { Exists: true, Length: > 0 };

    private const string ZscaleFilter = "zscale";
    private const string TonemapFilter = "tonemap";

    private static readonly FrozenSet<string> ZscaleMatrices = FrozenSet.Create(
        StringComparer.Ordinal, "bt709", "bt470bg", "smpte170m", "bt2020nc", "bt2020c");

    private static readonly FrozenSet<string> ZscalePrimaries = FrozenSet.Create(
        StringComparer.Ordinal, "bt709", "bt470bg", "smpte170m", "bt2020", "smpte432");

    private static readonly FrozenSet<string> X265Primaries = FrozenSet.Create(
        StringComparer.Ordinal,
        "bt709", "bt470m", "bt470bg", "smpte170m", "smpte240m", "film", "bt2020", "smpte428", "smpte431", "smpte432");

    private static readonly FrozenSet<string> X265Transfers = FrozenSet.Create(
        StringComparer.Ordinal,
        "bt709", "bt470m", "bt470bg", "smpte170m", "smpte240m", "linear", "log100", "log316", "iec61966-2-4", "bt1361e",
        "iec61966-2-1", "bt2020-10", "bt2020-12", "smpte2084", "smpte428", "arib-std-b67");

    private static readonly FrozenSet<string> X265Matrices = FrozenSet.Create(
        StringComparer.Ordinal,
        "gbr", "bt709", "fcc", "bt470bg", "smpte170m", "smpte240m", "ycgco", "bt2020nc", "bt2020c", "smpte2085",
        "chroma-derived-nc", "chroma-derived-c", "ictcp");
}

/// <summary>输出容器。</summary>
internal enum VideoContainer
{
    Mp4,
    Mov
}
