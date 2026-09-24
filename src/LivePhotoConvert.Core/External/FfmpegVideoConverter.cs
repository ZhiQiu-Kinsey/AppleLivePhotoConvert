using System.Collections.Frozen;
using System.Text.RegularExpressions;
using LivePhotoConvert.Core.Abstractions;
using LivePhotoConvert.Core.Io;

namespace LivePhotoConvert.Core.External;

/// <summary>
/// 基于 FFmpeg 的视频容器转换：优先流复制，失败或需要烧录方向时再重新编码。
/// HDR 源重新编码走 libx265 10-bit 并保留色彩元数据；HEVC 输出一律标记 hvc1。
/// </summary>
public sealed partial class FfmpegVideoConverter : IVideoConverter
{
    internal const string HevcEncoder = "libx265";
    private const string HdrPixelFormat = "yuv420p10le";
    private const string SdrPixelFormat = "yuv420p";

    private readonly string _executablePath;
    private readonly Lazy<Task<FrozenSet<string>>> _encoders;

    private FfmpegVideoConverter(string executablePath, IEnumerable<string>? availableEncoders)
    {
        _executablePath = executablePath;
        _encoders = availableEncoders is null
            ? new(() => ListEncodersAsync(executablePath))
            : new(Task.FromResult(availableEncoders.ToFrozenSet(StringComparer.Ordinal)));
    }

    public static string ExecutableName => OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

    /// <param name="executablePath">指定的 ffmpeg 路径；null 时按默认位置查找</param>
    /// <param name="availableEncoders">已知可用的编码器名单；null 时在首次需要重新编码 HDR 源时运行 <c>ffmpeg -encoders</c> 探测</param>
    /// <exception cref="FileNotFoundException">找不到 FFmpeg</exception>
    public static FfmpegVideoConverter Create(string? executablePath = null, IEnumerable<string>? availableEncoders = null) =>
        new(ToolLocator.Find(ExecutableName, executablePath, "ffmpeg", "FFmpeg", "bin")
            ?? throw new FileNotFoundException($"未找到 {ExecutableName}，请在「依赖引擎」页面下载或指定路径。"),
            availableEncoders);

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
        if (!options.ForceTranscode && !options.BakeOrientation)
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
            BuildTranscodeArguments(input, output, planned, hevc, options.BakeOrientation, container),
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

    internal static IReadOnlyList<string> BuildTranscodeArguments(string input, string output, VideoStreamInfo? source, bool hevc, bool bakeOrientation, VideoContainer container)
    {
        List<string> arguments = ["-nostdin", "-hide_banner", "-loglevel", "error"];
        if (!bakeOrientation)
        {
            // 默认 autorotate 会把像素转正并丢掉显示矩阵；关闭后矩阵原样写入输出，与流复制一致
            arguments.AddRange(["-autorotate", "0"]);
        }

        var pixelFormat = hevc ? HdrPixelFormat : SdrPixelFormat;
        arguments.AddRange(["-i", input, "-map", "0:v:0", "-map", "0:a:0?"]);
        // 统一输出有限范围：像素格式不变时 FFmpeg 不会自动换算范围，只改标记会让全范围源发灰或过曝
        arguments.AddRange(["-vf", $"scale=out_range=tv,format={pixelFormat}"]);
        if (hevc)
        {
            arguments.AddRange(["-c:v", HevcEncoder, "-pix_fmt", pixelFormat, "-crf", TranscodeCrf, "-x265-params", BuildX265Parameters(source), "-tag:v", "hvc1"]);
        }
        else
        {
            arguments.AddRange(["-c:v", "libx264", "-pix_fmt", pixelFormat, "-crf", TranscodeCrf]);
        }

        if (source?.ColorPrimaries is { } primaries)
        {
            arguments.AddRange(["-color_primaries", primaries]);
        }

        if (source?.ColorTransfer is { } transfer)
        {
            arguments.AddRange(["-color_trc", transfer]);
        }

        if (source?.ColorSpace is { } space)
        {
            arguments.AddRange(["-colorspace", space]);
        }

        arguments.AddRange(["-color_range", "tv"]);
        AddAudioAndContainer(arguments, output, container);
        return arguments;
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
            return ParseEncoders(result.StandardOutput);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            return FrozenSet<string>.Empty;
        }
    }

    /// <summary>解析 <c>ffmpeg -encoders</c>：每行为 6 位能力标记 + 编码器名。</summary>
    internal static FrozenSet<string> ParseEncoders(string standardOutput)
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (var line in standardOutput.ReplaceLineEndings("\n").Split('\n'))
        {
            if (EncoderLineRegex().Match(line) is { Success: true } match)
            {
                names.Add(match.Groups[1].Value);
            }
        }

        return names.ToFrozenSet(StringComparer.Ordinal);
    }

    private static bool IsNonEmptyFile(string path) => new FileInfo(path) is { Exists: true, Length: > 0 };

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

    [GeneratedRegex(@"^\s*[VASD.][A-Z.]{5,}\s+([A-Za-z0-9_\-]+)\s", RegexOptions.CultureInvariant)]
    private static partial Regex EncoderLineRegex();
}

/// <summary>输出容器。</summary>
internal enum VideoContainer
{
    Mp4,
    Mov
}
