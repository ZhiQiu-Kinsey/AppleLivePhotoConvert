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
        // 固定 1.5 秒（1,500,000 微秒）作为代表帧时间戳
        var timestampUs = "1500000";

        var tempXmpPath = Path.Combine(Path.GetTempPath(), "LivePhotoConvert", $"xmp_{Guid.NewGuid():N}.xml");
        var xmpContent = $"""
                          <x:xmpmeta xmlns:x="adobe:ns:meta/" x:xmptk="Adobe XMP Core 5.1.0-jc003">
                            <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
                              <rdf:Description rdf:about=""
                                  xmlns:GCamera="http://ns.google.com/photos/1.0/camera/"
                                  xmlns:Container="http://ns.google.com/photos/1.0/container/"
                                  xmlns:Item="http://ns.google.com/photos/1.0/container/item/"
                                GCamera:MotionPhoto="1"
                                GCamera:MotionPhotoVersion="1"
                                GCamera:MotionPhotoPresentationTimestampUs="{timestampUs}"
                                GCamera:MicroVideo="1"
                                GCamera:MicroVideoVersion="1"
                                GCamera:MicroVideoOffset="{offset}"
                                GCamera:MicroVideoPresentationTimestampUs="{timestampUs}">
                                <Container:Directory>
                                  <rdf:Seq>
                                    <rdf:li rdf:parseType="Resource">
                                      <Container:Item
                                        Item:Mime="image/jpeg"
                                        Item:Semantic="Primary"/>
                                    </rdf:li>
                                    <rdf:li rdf:parseType="Resource">
                                      <Container:Item
                                        Item:Mime="video/mp4"
                                        Item:Semantic="MotionPhoto"
                                        Item:Length="{offset}"
                                        Item:Padding="0"/>
                                    </rdf:li>
                                  </rdf:Seq>
                                </Container:Directory>
                              </rdf:Description>
                            </rdf:RDF>
                          </x:xmpmeta>
                          """;

        try
        {
            var tempDir = Path.GetDirectoryName(tempXmpPath)!;
            Directory.CreateDirectory(tempDir);
            await File.WriteAllTextAsync(tempXmpPath, xmpContent, cancellationToken);

            List<string> arguments =
            [
                $"-xmp<={tempXmpPath}",
                "-EXIF:MicroVideo=1",
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
        finally
        {
            try
            {
                File.Delete(tempXmpPath);
            }
            catch
            {
                // 忽略清理临时文件时的异常
            }
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
            "-XMP-GCamera:MotionPhoto=",
            "-XMP-GCamera:MotionPhotoVersion=",
            "-XMP-GCamera:MotionPhotoPresentationTimestampUs=",
            "-XMP-Container:Directory=",
            "-EXIF:MicroVideo=",
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
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset))
        {
            return offset;
        }

        // 尝试从 Google GContainer 容器结构中读取
        List<string> containerArguments =
        [
            "-s3",
            "-XMP-Container:DirectoryItemLength",
            imagePath
        ];
        var containerResponse = await _session.ExecuteAsync(containerArguments, cancellationToken);
        var containerText = containerResponse.StandardOutput.Trim();
        if (!string.IsNullOrEmpty(containerText))
        {
            var lines = containerText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length > 0 && long.TryParse(lines[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var containerOffset))
            {
                return containerOffset;
            }
        }

        return null;
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
