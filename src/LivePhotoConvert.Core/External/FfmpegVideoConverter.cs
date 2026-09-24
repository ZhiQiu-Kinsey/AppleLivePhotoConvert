using LivePhotoConvert.Core.Abstractions;
using LivePhotoConvert.Core.Io;

namespace LivePhotoConvert.Core.External;

/// <summary>
/// 基于 FFmpeg 的视频容器转换：优先流复制，失败或需要烧录镜像时再重新编码。
/// </summary>
public sealed class FfmpegVideoConverter : IVideoConverter
{
    private readonly string _executablePath;

    private FfmpegVideoConverter(string executablePath) => _executablePath = executablePath;

    public static string ExecutableName => OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

    /// <exception cref="FileNotFoundException">找不到 FFmpeg</exception>
    public static FfmpegVideoConverter Create(string? executablePath = null) =>
        new(ToolLocator.Find(ExecutableName, executablePath, "ffmpeg", "FFmpeg", "bin")
            ?? throw new ToolNotFoundException(ExecutableName));

    public Task ConvertToMp4Async(string sourcePath, string destinationPath, bool forceTranscode = false, CancellationToken cancellationToken = default) =>
        RemuxOrTranscodeAsync(
            forceTranscode ? null : BuildArguments(sourcePath, destinationPath, "copy", "aac", audioBitrate: "192k"),
            BuildArguments(sourcePath, destinationPath, "libx264", "aac", pixelFormat: "yuv420p", audioBitrate: "192k"),
            destinationPath,
            cancellationToken);

    public Task RemuxToMovAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default) =>
        RemuxOrTranscodeAsync(
            BuildArguments(sourcePath, destinationPath, "copy", "pcm_s16le", movContainer: true),
            BuildArguments(sourcePath, destinationPath, "libx264", "pcm_s16le", movContainer: true, pixelFormat: "yuv420p"),
            destinationPath,
            cancellationToken);

    private async Task RemuxOrTranscodeAsync(IReadOnlyList<string>? remuxArguments, IReadOnlyList<string> transcodeArguments, string destinationPath, CancellationToken cancellationToken)
    {
        string? remuxError = null;
        if (remuxArguments is not null)
        {
            var remux = await ProcessRunner.RunAsync(_executablePath, remuxArguments, cancellationToken);
            if (remux.Success && IsNonEmptyFile(destinationPath))
            {
                return;
            }

            remuxError = remux.ErrorSummary;
            FileHelper.TryDeleteFile(destinationPath);
        }

        var transcode = await ProcessRunner.RunAsync(_executablePath, transcodeArguments, cancellationToken);
        if (transcode.Success && IsNonEmptyFile(destinationPath))
        {
            return;
        }

        FileHelper.TryDeleteFile(destinationPath);
        throw new InvalidOperationException(remuxError is null
            ? $"FFmpeg 重新编码失败：{transcode.ErrorSummary}"
            : $"FFmpeg 换容器失败：{remuxError}；重新编码失败：{transcode.ErrorSummary}");
    }

    private static IReadOnlyList<string> BuildArguments(
        string sourcePath,
        string destinationPath,
        string videoCodec,
        string audioCodec,
        bool movContainer = false,
        string? pixelFormat = null,
        string? audioBitrate = null)
    {
        List<string> arguments = ["-nostdin", "-hide_banner", "-loglevel", "error", "-i", sourcePath, "-map", "0:v:0", "-map", "0:a:0?", "-c:v", videoCodec];
        if (pixelFormat is not null)
        {
            arguments.AddRange(["-pix_fmt", pixelFormat]);
        }

        arguments.AddRange(["-c:a", audioCodec]);
        if (audioBitrate is not null)
        {
            arguments.AddRange(["-b:a", audioBitrate]);
        }

        arguments.AddRange(["-movflags", "+faststart"]);
        if (movContainer)
        {
            arguments.AddRange(["-f", "mov"]);
        }

        arguments.AddRange(["-y", destinationPath]);
        return arguments;
    }

    private static bool IsNonEmptyFile(string path) => new FileInfo(path) is { Exists: true, Length: > 0 };
}
