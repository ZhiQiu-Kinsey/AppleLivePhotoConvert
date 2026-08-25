using LivePhotoConvert.Core.Abstractions;
using LivePhotoConvert.Core.Io;

namespace LivePhotoConvert.Core.External;

/// <summary>
/// 基于 FFmpeg 的视频与图像多媒体转码与流封装转换引擎
/// </summary>
public sealed class FfmpegVideoConverter : IVideoConverter, IImageConverter
{
    private readonly string _executablePath;

    /// <summary>
    /// 初始化 FFmpeg 转换器实例
    /// </summary>
    /// <param name="executablePath">FFmpeg 可执行文件的绝对路径</param>
    private FfmpegVideoConverter(string executablePath) => _executablePath = executablePath;

    /// <summary>
    /// 当前操作系统的 FFmpeg 可执行文件名（Windows 下为 ffmpeg.exe，类 Unix 下为 ffmpeg）
    /// </summary>
    public static string ExecutableName => OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

    /// <summary>
    /// 定位 FFmpeg 并创建转换器实例
    /// </summary>
    /// <param name="executablePath">用户显式指定的路径，为空时自动在程序目录、tools 子目录及环境变量 PATH 中定位</param>
    /// <returns>可用的 FFmpeg 转换器实例</returns>
    /// <exception cref="FileNotFoundException">未找到 FFmpeg 可执行程序</exception>
    public static FfmpegVideoConverter Create(string? executablePath = null)
    {
        var path = ToolLocator.Find(ExecutableName, executablePath, "ffmpeg", "FFmpeg", "bin", "tools") ?? throw new FileNotFoundException($"未找到 {ExecutableName}。请把 FFmpeg 放到程序目录、tools 子目录下或加入 PATH，" + $"也可以用 --ffmpeg 参数指定完整路径。");
        return new FfmpegVideoConverter(path);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 绝大多数 iPhone 的 MOV 视频流本身就是标准的 H.264/HEVC 编码，直接换容器即可；<br/>
    /// 前置摄像头视频带 2D 镜像变换矩阵（行列式为负），安卓相册不识别，需强制重新编码把方向烧进像素。
    /// </remarks>
    public Task ConvertToMp4Async(string sourcePath, string destinationPath, bool forceTranscode = false, CancellationToken cancellationToken = default) =>
        RemuxOrTranscodeAsync(
            sourcePath,
            destinationPath,
            remuxArguments: forceTranscode ? null : BuildMuxArguments(sourcePath, destinationPath, "copy", "aac", audioBitrate: "192k"),
            transcodeArguments: BuildMuxArguments(sourcePath, destinationPath, "libx264", "aac", pixelFormat: "yuv420p"),
            operation: "转换视频",
            remuxDescription: forceTranscode ? "强制转码（前置镜像视频）" : "换容器错误",
            cancellationToken);

    /// <inheritdoc />
    public Task RemuxToMovAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default) =>
        RemuxOrTranscodeAsync(
            sourcePath,
            destinationPath,
            remuxArguments: BuildMuxArguments(sourcePath, destinationPath, "copy", "pcm_s16le", movContainer: true),
            transcodeArguments: BuildMuxArguments(sourcePath, destinationPath, "libx264", "pcm_s16le", movContainer: true, pixelFormat: "yuv420p"),
            operation: "封装 MOV 视频",
            remuxDescription: "换容器错误",
            cancellationToken);

    /// <inheritdoc />
    public async Task ConvertToJpegAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> arguments =
        [
            "-i", sourcePath,
            "-frames:v", "1",
            "-q:v", "2",
            "-y",
            destinationPath
        ];

        var result = await ProcessRunner.RunAsync(_executablePath, arguments, cancellationToken);
        if (!result.Success || !File.Exists(destinationPath))
        {
            FileHelper.TryDeleteFile(destinationPath);
            throw new InvalidOperationException($"FFmpeg 转码图片失败：{Summarize(result.StandardError)}");
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// FFmpeg 的 HEIC 编码支持有限，此处委托给 Magick.NET 原生实现以确保最佳质量与兼容性。
    /// </remarks>
    public Task ConvertToHeicAsync(string sourcePath, string destinationPath, int quality = 90, CancellationToken cancellationToken = default)
    {
        return MagickImageConverter.Instance.ConvertToHeicAsync(sourcePath, destinationPath, quality, cancellationToken);
    }

    /// <summary>
    /// 先尝试流复制换容器，失败后自动回退到重新编码；两阶段均失败时清理半成品并抛出异常
    /// </summary>
    /// <param name="sourcePath">源视频路径</param>
    /// <param name="destinationPath">目标输出路径</param>
    /// <param name="remuxArguments">换容器参数；传 null 表示跳过换容器直接重新编码（强制转码场景）</param>
    /// <param name="transcodeArguments">重新编码参数</param>
    /// <param name="operation">用于异常信息的操作描述</param>
    /// <param name="remuxDescription">换容器阶段的异常前缀描述（如「换容器错误」「强制转码」）</param>
    /// <param name="cancellationToken">取消令牌</param>
    private async Task RemuxOrTranscodeAsync(
        string sourcePath,
        string destinationPath,
        IReadOnlyList<string>? remuxArguments,
        IReadOnlyList<string> transcodeArguments,
        string operation,
        string remuxDescription,
        CancellationToken cancellationToken)
    {
        string? remuxError = null;
        if (remuxArguments is not null)
        {
            var remux = await ProcessRunner.RunAsync(_executablePath, remuxArguments, cancellationToken);
            if (remux.Success && File.Exists(destinationPath))
            {
                return;
            }

            remuxError = Summarize(remux.StandardError);
            FileHelper.TryDeleteFile(destinationPath);
        }

        var transcode = await ProcessRunner.RunAsync(_executablePath, transcodeArguments, cancellationToken);
        if (transcode.Success && File.Exists(destinationPath))
        {
            return;
        }

        FileHelper.TryDeleteFile(destinationPath);
        var remuxPart = remuxArguments is null ? remuxDescription : $"{remuxDescription}：{remuxError}";
        throw new InvalidOperationException($"FFmpeg {operation}失败。{remuxPart}；重新编码错误：{Summarize(transcode.StandardError)}");
    }

    /// <summary>
    /// 构建 FFmpeg 换容器/转码参数（-c:v copy 换容器 或 libx264 转码；-c:a aac/pcm_s16le）
    /// </summary>
    private static IReadOnlyList<string> BuildMuxArguments(
        string sourcePath,
        string destinationPath,
        string videoCodec,
        string audioCodec,
        bool movContainer = false,
        string? pixelFormat = null,
        string? audioBitrate = null)
    {
        List<string> arguments = ["-i", sourcePath, "-c:v", videoCodec];
        if (pixelFormat is not null)
        {
            arguments.Add("-pix_fmt");
            arguments.Add(pixelFormat);
        }

        arguments.Add("-c:a");
        arguments.Add(audioCodec);
        if (audioBitrate is not null)
        {
            arguments.Add("-b:a");
            arguments.Add(audioBitrate);
        }

        arguments.AddRange(["-map", "0:v:0", "-map", "0:a:0?", "-movflags", "+faststart"]);
        if (movContainer)
        {
            arguments.Add("-f");
            arguments.Add("mov");
        }

        arguments.Add("-y");
        arguments.Add(destinationPath);
        return arguments;
    }

    /// <summary>
    /// 零堆分配从 FFmpeg 的复杂 stderr 输出中提取最后一条关键错误概要
    /// </summary>
    private static string Summarize(string error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return "无详细错误输出";
        }

        ReadOnlySpan<char> lastNonEmptyLine = default;
        foreach (var line in error.AsSpan().EnumerateLines())
        {
            var trimmed = line.Trim();
            if (!trimmed.IsEmpty)
            {
                lastNonEmptyLine = trimmed;
            }
        }

        return lastNonEmptyLine.IsEmpty ? error : lastNonEmptyLine.ToString();
    }
}
