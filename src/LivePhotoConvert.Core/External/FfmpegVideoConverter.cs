using LivePhotoConvert.Core.Abstractions;

namespace LivePhotoConvert.Core.External;

/// <summary>
/// 基于 FFmpeg 的视频转换与图片解码转换器
/// </summary>
public sealed class FfmpegVideoConverter : IVideoConverter, IImageConverter
{
    private readonly string _executablePath;

    /// <summary>
    /// 创建实例
    /// </summary>
    /// <param name="executablePath">FFmpeg 可执行文件路径</param>
    private FfmpegVideoConverter(string executablePath) => _executablePath = executablePath;

    /// <summary>
    /// FFmpeg 的可执行文件名
    /// </summary>
    public static string ExecutableName => OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

    /// <summary>
    /// 定位 FFmpeg 并创建实例
    /// </summary>
    /// <param name="executablePath">显式指定的路径，为空时自动查找</param>
    /// <returns>实例</returns>
    /// <exception cref="FileNotFoundException">未找到 FFmpeg</exception>
    public static FfmpegVideoConverter Create(string? executablePath = null)
    {
        var path = ToolLocator.Find(ExecutableName, executablePath, "ffmpeg", "FFmpeg", "bin", "tools")
                   ?? throw new FileNotFoundException($"未找到 {ExecutableName}。请把 FFmpeg 放到程序目录、tools 子目录下或加入 PATH，" + $"也可以用 --ffmpeg 参数指定完整路径。");

        return new FfmpegVideoConverter(path);
    }

    /// <inheritdoc />
    public async Task ConvertToMp4Async(string sourcePath, string destinationPath, bool forceTranscode = false, CancellationToken cancellationToken = default)
    {
        string? remuxError = null;
        if (!forceTranscode)
        {
            // iPhone 的 MOV 本身就是 H.264/HEVC + AAC，换容器即可，无需重新编码
            var remux = await ProcessRunner.RunAsync(_executablePath, BuildRemuxArguments(sourcePath, destinationPath), cancellationToken);
            if (remux.Success)
            {
                return;
            }

            // 换容器失败（例如编码格式不被 MP4 容器接受），回退到重新编码
            remuxError = Summarize(remux.StandardError);
            TryDeletePartialOutput(destinationPath);
        }

        // 强制转码时跳过换容器：前置摄像头视频带镜像矩阵（行列式为负），
        // 安卓相册等 MP4 播放器不识别镜像矩阵，只有重新编码才能让 FFmpeg 的
        // autorotate 把镜像与旋转一起烧进像素，输出无方向元数据的标准 MP4。
        var transcode = await ProcessRunner.RunAsync(_executablePath, BuildTranscodeArguments(sourcePath, destinationPath), cancellationToken);
        if (transcode.Success)
        {
            return;
        }

        TryDeletePartialOutput(destinationPath);
        var remuxDescription = forceTranscode ? "强制转码（前置镜像视频）" : $"换容器错误：{remuxError}";
        throw new InvalidOperationException($"FFmpeg 转换视频失败。{remuxDescription}；重新编码错误：{Summarize(transcode.StandardError)}");
    }

    /// <inheritdoc />
    public async Task RemuxToMovAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        // MP4 转 MOV 通常只需更换 QuickTime 容器，无需重新编码画面与声音
        var remux = await ProcessRunner.RunAsync(_executablePath, BuildRemuxMovArguments(sourcePath, destinationPath), cancellationToken);
        if (remux.Success && File.Exists(destinationPath))
        {
            return;
        }
        TryDeletePartialOutput(destinationPath);
        // 极端情况下若包含不兼容流，回退到标准 H.264 + AAC 重新编码
        var transcode = await ProcessRunner.RunAsync(_executablePath, BuildTranscodeMovArguments(sourcePath, destinationPath), cancellationToken);
        if (transcode.Success && File.Exists(destinationPath))
        {
            return;
        }
        TryDeletePartialOutput(destinationPath);
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
            TryDeletePartialOutput(destinationPath);
            throw new InvalidOperationException($"FFmpeg 转码图片失败：{Summarize(result.StandardError)}");
        }
    }

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

    private static void TryDeletePartialOutput(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // 清理失败时忽略，主流程已处理异常
        }
    }

    private static string Summarize(string error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return "无详细错误输出";
        }
        var lines = error.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length > 0 ? lines[^1] : error;
    }
}
