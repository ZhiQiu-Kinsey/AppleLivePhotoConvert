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
    public async Task ConvertToMp4Async(string sourcePath, string destinationPath, bool forceTranscode = false, CancellationToken cancellationToken = default)
    {
        string? remuxError = null;
        if (!forceTranscode)
        {
            // 绝大多数 iPhone 的 MOV 视频流本身就是标准的 H.264/HEVC 编码，音频为 AAC，
            // 直接执行流复制（-c:v copy）换容器只需数毫秒，且保持 100% 原画质无损。
            var remux = await ProcessRunner.RunAsync(_executablePath, BuildRemuxArguments(sourcePath, destinationPath), cancellationToken);
            if (remux.Success)
            {
                return;
            }

            // 换容器失败（例如原视频包含不被 MP4 容器接受的特殊编码流），自动回退至完整重新编码
            remuxError = Summarize(remux.StandardError);
            FileHelper.TryDeleteFile(destinationPath);
        }

        // 强制转码场景：iPhone 前置自拍摄像头录制的视频带有 2D 镜像变换矩阵（行列式为负）。
        // 安卓系统相册和大多数播放器无法识别 QuickTime 镜像矩阵，会导致画面显示颠倒/左右翻转。
        // 此时必须通过 FFmpeg 重新编码，触发 FFmpeg 内部的 autorotate 滤镜把镜像与旋转直接烧录进视频像素。
        var transcode = await ProcessRunner.RunAsync(_executablePath, BuildTranscodeArguments(sourcePath, destinationPath), cancellationToken);
        if (transcode.Success)
        {
            return;
        }

        FileHelper.TryDeleteFile(destinationPath);
        var remuxDescription = forceTranscode ? "强制转码（前置镜像视频）" : $"换容器错误：{remuxError}";
        throw new InvalidOperationException($"FFmpeg 转换视频失败。{remuxDescription}；重新编码错误：{Summarize(transcode.StandardError)}");
    }

    /// <inheritdoc />
    public async Task RemuxToMovAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        // MP4 转换为 Apple QuickTime MOV 容器，通常仅需重封装（-c copy），极速无损
        var remux = await ProcessRunner.RunAsync(_executablePath, BuildRemuxMovArguments(sourcePath, destinationPath), cancellationToken);
        if (remux.Success && File.Exists(destinationPath))
        {
            return;
        }
        FileHelper.TryDeleteFile(destinationPath);

        // 极端情况下若包含不兼容流，自动回退到标准 H.264 + AAC 重新编码封装为 MOV
        var transcode = await ProcessRunner.RunAsync(_executablePath, BuildTranscodeMovArguments(sourcePath, destinationPath), cancellationToken);
        if (transcode.Success && File.Exists(destinationPath))
        {
            return;
        }
        FileHelper.TryDeleteFile(destinationPath);
        throw new InvalidOperationException($"FFmpeg 封装 MOV 视频失败。换容器错误：{Summarize(remux.StandardError)}；重新编码错误：{Summarize(transcode.StandardError)}");
    }

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
    /// 构建极速无损换容器为 MP4 的 FFmpeg 参数（视频流直接 copy，音频转换为标准 AAC）
    /// </summary>
    private static IReadOnlyList<string> BuildRemuxArguments(string sourcePath, string destinationPath) =>
    [
        "-i", sourcePath,
        "-c:v", "copy",
        "-c:a", "aac",
        "-b:a", "192k",
        "-map", "0:v:0",
        "-map", "0:a:0?",
        "-movflags", "+faststart",
        "-y",
        destinationPath
    ];

    /// <summary>
    /// 构建重新编码为标准兼容 MP4 (H.264 + YUV420P) 的 FFmpeg 参数
    /// </summary>
    private static IReadOnlyList<string> BuildTranscodeArguments(string sourcePath, string destinationPath) =>
    [
        "-i", sourcePath,
        "-c:v", "libx264",
        "-pix_fmt", "yuv420p",
        "-c:a", "aac",
        "-map", "0:v:0",
        "-map", "0:a:0?",
        "-movflags", "+faststart",
        "-y",
        destinationPath
    ];

    /// <summary>
    /// 构建将 MP4 重封装为 QuickTime MOV 格式的参数
    /// </summary>
    private static IReadOnlyList<string> BuildRemuxMovArguments(string sourcePath, string destinationPath) =>
    [
        "-i", sourcePath,
        "-c", "copy",
        "-map", "0:v:0",
        "-map", "0:a:0?",
        "-movflags", "+faststart",
        "-f", "mov",
        "-y",
        destinationPath
    ];

    /// <summary>
    /// 构建重新编码并输出为 QuickTime MOV 格式的参数
    /// </summary>
    private static IReadOnlyList<string> BuildTranscodeMovArguments(string sourcePath, string destinationPath) =>
    [
        "-i", sourcePath,
        "-c:v", "libx264",
        "-pix_fmt", "yuv420p",
        "-c:a", "aac",
        "-map", "0:v:0",
        "-map", "0:a:0?",
        "-movflags", "+faststart",
        "-f", "mov",
        "-y",
        destinationPath
    ];

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


