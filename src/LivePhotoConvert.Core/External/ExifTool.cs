using System.Globalization;
using LivePhotoConvert.Core.Abstractions;

namespace LivePhotoConvert.Core.External;

/// <summary>
/// 基于 ExifTool 的元数据读写
/// </summary>
public sealed class ExifTool : IExifTool
{
    private readonly ExifToolSession _session;

    /// <summary>
    /// 创建实例
    /// </summary>
    /// <param name="executablePath">ExifTool 可执行文件路径</param>
    private ExifTool(string executablePath)
    {
        _session = new ExifToolSession(executablePath, ExifToolConfig.EnsureCreated());
    }

    /// <summary>
    /// ExifTool 的可执行文件名
    /// </summary>
    public static string ExecutableName => OperatingSystem.IsWindows() ? "exiftool.exe" : "exiftool";

    /// <summary>
    /// 定位 ExifTool 并创建实例
    /// </summary>
    /// <param name="executablePath">显式指定的路径，为空时自动查找</param>
    /// <returns>实例</returns>
    /// <exception cref="FileNotFoundException">未找到 ExifTool</exception>
    public static ExifTool Create(string? executablePath = null)
    {
        var path = ToolLocator.Find(ExecutableName, executablePath, "ExifTool", "exiftool", "tools")
                   ?? throw new FileNotFoundException($"未找到 {ExecutableName}。请把 ExifTool 放到程序目录或其 tools 子目录下，" + $"或加入 PATH，也可以用 --exiftool 参数指定完整路径。");
        return new ExifTool(path);
    }

    /// <inheritdoc />
    public async Task WriteMotionPhotoTagsAsync(string imagePath, long videoOffset, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(videoOffset);

        var offset = videoOffset.ToString(CultureInfo.InvariantCulture);
        // 沿用原有取值：以偏移量的一半作为封面帧时间戳。它并非真实的视频时长，
        // 但这是此前已验证可被相册接受的写法，改动会影响封面帧的选取，故保持不变。
        var timestamp = (videoOffset / 2).ToString(CultureInfo.InvariantCulture);

        List<string> arguments =
        [
            "-XMP-GCamera:MicroVideo=1",
            "-XMP-GCamera:MicroVideoVersion=1",
            $"-XMP-GCamera:MicroVideoOffset={offset}",
            $"-XMP-GCamera:MicroVideoPresentationTimestampUs={timestamp}",
            // 小米相册识别动态照片的关键标签，见 ExifToolConfig
            "-MicroVideo=1",
            "-overwrite_original",
            imagePath
        ];
        var response = await _session.ExecuteAsync(arguments, cancellationToken);
        ThrowIfFailed(response, "写入动态照片标记");
        if (!response.StandardOutput.Contains("1 image files updated", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"ExifTool 未能写入动态照片标记：{Describe(response)}");
        }
    }

    /// <inheritdoc />
    public async Task RemoveMotionPhotoTagsAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        List<string> arguments =
        [
            "-XMP-GCamera:MicroVideo=",
            "-XMP-GCamera:MicroVideoVersion=",
            "-XMP-GCamera:MicroVideoOffset=",
            "-XMP-GCamera:MicroVideoPresentationTimestampUs=",
            "-MicroVideo=",
            "-overwrite_original",
            imagePath
        ];

        var response = await _session.ExecuteAsync(arguments, cancellationToken);
        // 文件本来就没有这些标签时 ExifTool 会报告 0 files updated，这属于正常情况
        ThrowIfFailed(response, "清除动态照片标记");
    }

    /// <inheritdoc />
    public async Task<long?> TryReadMicroVideoOffsetAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        List<string> arguments =
        [
            "-s3",
            "-XMP-GCamera:MicroVideoOffset",
            imagePath
        ];

        var response = await _session.ExecuteAsync(arguments, cancellationToken);
        ThrowIfFailed(response, "读取动态照片标记");
        var text = response.StandardOutput.Trim();
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset) ? offset : null;
    }

    /// <inheritdoc />
    public async Task<string?> TryReadContentIdentifierAsync(string filePath, ContentIdentifierKind kind, CancellationToken cancellationToken = default)
    {
        var tag = kind == ContentIdentifierKind.Photo ? "-ContentIdentifier" : "-Keys:ContentIdentifier";
        List<string> arguments =
        [
            "-s3",
            tag,
            filePath
        ];

        var response = await _session.ExecuteAsync(arguments, cancellationToken);
        ThrowIfFailed(response, "读取唯一标识");
        var text = response.StandardOutput.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <inheritdoc />
    public async Task WriteAppleContentIdentifierAsync(string photoPath, string contentIdentifier, CancellationToken cancellationToken = default)
    {
        List<string> arguments =
        [
            $"-ContentIdentifier={contentIdentifier}",
            "-overwrite_original",
            photoPath
        ];

        var response = await _session.ExecuteAsync(arguments, cancellationToken);
        ThrowIfFailed(response, "写入 Apple 照片标识");
    }

    /// <inheritdoc />
    public async Task WriteAppleVideoMetadataAsync(string videoPath, string contentIdentifier, CancellationToken cancellationToken = default)
    {
        List<string> arguments =
        [
            $"-Keys:ContentIdentifier={contentIdentifier}",
            "-Keys:StillImageTime=0",
            "-overwrite_original",
            videoPath
        ];

        var response = await _session.ExecuteAsync(arguments, cancellationToken);
        ThrowIfFailed(response, "写入 Apple 视频标识");
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _session.DisposeAsync();

    /// <summary>
    /// 当 ExifTool 报错时抛出异常
    /// </summary>
    private static void ThrowIfFailed(ExifToolResponse response, string operation)
    {
        if (string.IsNullOrWhiteSpace(response.StandardError))
        {
            return;
        }

        // Warning 不影响正常写入
        var isRealError = response.StandardError
                                  .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                  .Any(line => !line.StartsWith("Warning:", StringComparison.OrdinalIgnoreCase));

        if (isRealError)
        {
            throw new InvalidOperationException($"ExifTool {operation}失败：{Describe(response)}");
        }
    }

    /// <summary>
    /// 把输出与错误拼成便于阅读的描述
    /// </summary>
    private static string Describe(ExifToolResponse response)
    {
        var output = response.StandardOutput.Trim();
        var error = response.StandardError.Trim();
        return (string.IsNullOrEmpty(output), string.IsNullOrEmpty(error)) switch
        {
            (false, false) => $"{output}（错误：{error}）",
            (false, true) => output,
            (true, false) => error,
            _ => "无输出"
        };
    }
}
