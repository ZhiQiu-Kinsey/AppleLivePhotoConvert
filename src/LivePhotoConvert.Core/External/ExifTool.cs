using System.Globalization;
using LivePhotoConvert.Core.Abstractions;
using LivePhotoConvert.Core.Io;

namespace LivePhotoConvert.Core.External;

/// <summary>
/// 基于 ExifTool 常驻进程会话的元数据读写与高性能解析引擎
/// </summary>
public sealed class ExifTool : IExifTool
{
    /// <summary>
    /// 静态缓存的 EXIF 标准日期格式数组（避免每次解析日期时重复创建数组）
    /// </summary>
    private static readonly string[] ExifDateFormats =
    [
        "yyyy:MM:dd HH:mm:ss",
        "yyyy:MM:dd HH:mm:sszzz",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-ddTHH:mm:sszzz"
    ];

    private readonly ExifToolSession _session;

    /// <summary>
    /// 初始化 ExifTool 会话实例
    /// </summary>
    /// <param name="executablePath">ExifTool 可执行文件的绝对路径</param>
    private ExifTool(string executablePath)
    {
        _session = new ExifToolSession(executablePath, ExifToolConfig.EnsureCreated());
    }

    /// <summary>
    /// 当前操作系统的 ExifTool 可执行文件名（Windows 下为 exiftool.exe，类 Unix 下为 exiftool）
    /// </summary>
    public static string ExecutableName => OperatingSystem.IsWindows() ? "exiftool.exe" : "exiftool";

    /// <summary>
    /// 定位 ExifTool 并创建会话实例
    /// </summary>
    /// <param name="executablePath">用户显式指定的路径，为空时自动在程序目录、tools 子目录及环境变量 PATH 中定位</param>
    /// <returns>可用的 ExifTool 实例</returns>
    /// <exception cref="FileNotFoundException">未找到 ExifTool 可执行程序</exception>
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
        const string timestampUs = "1500000";

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
            FileHelper.TryDeleteFile(tempXmpPath);
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
            long? lastParsedOffset = null;
            foreach (var line in containerText.AsSpan().EnumerateLines())
            {
                var trimmed = line.Trim();
                if (!trimmed.IsEmpty && long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var containerOffset))
                {
                    lastParsedOffset = containerOffset;
                }
            }

            if (lastParsedOffset.HasValue)
            {
                return lastParsedOffset.Value;
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

    /// <summary>
    /// 为照片写入 Apple Live Photo 唯一配对标识 (ContentIdentifier)
    /// </summary>
    /// <param name="photoPath">照片路径</param>
    /// <param name="contentIdentifier">生成的配对 UUID</param>
    /// <param name="cancellationToken">取消令牌</param>
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

    /// <summary>
    /// 为 QuickTime 视频写入 Apple Live Photo 唯一配对标识 (ContentIdentifier) 与静态帧时间锚点
    /// </summary>
    /// <param name="videoPath">QuickTime MOV 视频路径</param>
    /// <param name="contentIdentifier">生成的配对 UUID</param>
    /// <param name="cancellationToken">取消令牌</param>
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
    public async Task<DateTime?> TryReadCreateDateAsync(string filePath, CancellationToken cancellationToken = default)
    {
        // 依次尝试多个时间标签，取第一个有效值
        string[] tags = ["-DateTimeOriginal", "-CreateDate", "-MediaCreateDate"];
        foreach (var tag in tags)
        {
            List<string> arguments = ["-s3", tag, filePath];
            var response = await _session.ExecuteAsync(arguments, cancellationToken);
            var text = response.StandardOutput.Trim();
            if (!string.IsNullOrEmpty(text) && TryParseExifDate(text.AsSpan(), out var date))
            {
                return date;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<TimeSpan?> TryReadDurationAsync(string filePath, CancellationToken cancellationToken = default)
    {
        List<string> arguments = ["-s3", "-Duration#", filePath];
        var response = await _session.ExecuteAsync(arguments, cancellationToken);
        var text = response.StandardOutput.Trim();
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<bool> IsMirroredVideoAsync(string videoPath, CancellationToken cancellationToken = default)
    {
        try
        {
            // 读取所有轨道的变换矩阵，任一轨道带镜像（行列式为负）即视为镜像视频。
            // 后置摄像头视频的矩阵是恒等或纯旋转（行列式为正），只有前置摄像头才会出现镜像矩阵。
            List<string> arguments = ["-a", "-s3", "-MatrixStructure", videoPath];
            var response = await _session.ExecuteAsync(arguments, cancellationToken);
            foreach (var line in response.StandardOutput.AsSpan().EnumerateLines())
            {
                var trimmed = line.Trim();
                if (!trimmed.IsEmpty && TryParseMatrixDeterminant(trimmed, out var determinant) && determinant < 0)
                {
                    return true;
                }
            }

            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // 矩阵读取失败时按无需镜像处理，保持原有的无损换容器行为
            return false;
        }
    }

    /// <summary>
    /// 零分配解析 ExifTool 输出的 QuickTime MatrixStructure 变换矩阵并计算行列式 (ad - bc)
    /// </summary>
    /// <remarks>
    /// QuickTime 仿射变换矩阵按行主序包含 9 个数值：<br/>
    /// [a, b, u]<br/>
    /// [c, d, v]<br/>
    /// [x, y, w]<br/>
    /// 其中 2D 线性变换由前 2x2 子矩阵 (a,b,c,d) 决定。若行列式 ad - bc &lt; 0 则说明存在水平/垂直镜像翻转。
    /// </remarks>
    /// <param name="line">形如 "0 1 0 1 0 0 0 0 1" 的 3x3 矩阵行切片</param>
    /// <param name="determinant">输出计算得到的行列式值</param>
    /// <returns>是否成功解析出合法矩阵</returns>
    private static bool TryParseMatrixDeterminant(ReadOnlySpan<char> line, out double determinant)
    {
        determinant = 0;
        Span<double> values = stackalloc double[5];
        var count = 0;

        foreach (var range in line.Split(' '))
        {
            var token = line[range].Trim();
            if (token.IsEmpty)
            {
                continue;
            }

            if (count < 5)
            {
                if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out values[count]))
                {
                    return false;
                }
                count++;
            }
        }

        if (count < 5)
        {
            return false;
        }

        // 3x3 矩阵按行主序输出：a(0), b(1), u(2), c(3), d(4)。镜像只取决于 a*d - b*c
        determinant = values[0] * values[4] - values[1] * values[3];
        return true;
    }

    /// <summary>
    /// 尝试解析 ExifTool 输出的各种格式的时间字符串
    /// </summary>
    /// <param name="text">时间文本切片</param>
    /// <param name="date">解析出的 DateTime</param>
    /// <returns>是否成功解析</returns>
    private static bool TryParseExifDate(ReadOnlySpan<char> text, out DateTime date) =>
        DateTime.TryParseExact(text, ExifDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _session.DisposeAsync();

    /// <summary>
    /// 当 ExifTool 执行报错时抛出异常（采用零分配 EnumerateLines 过滤无害的 Warning 警告）
    /// </summary>
    /// <param name="response">ExifTool 会话响应</param>
    /// <param name="operation">当前操作名称描述</param>
    private static void ThrowIfFailed(ExifToolResponse response, string operation)
    {
        if (string.IsNullOrWhiteSpace(response.StandardError))
        {
            return;
        }

        // Warning 通常为非致命元数据提示，不影响正常写入
        var isRealError = false;
        foreach (var line in response.StandardError.AsSpan().EnumerateLines())
        {
            var trimmed = line.Trim();
            if (!trimmed.IsEmpty && !trimmed.StartsWith("Warning:", StringComparison.OrdinalIgnoreCase))
            {
                isRealError = true;
                break;
            }
        }

        if (isRealError)
        {
            throw new InvalidOperationException($"ExifTool {operation}失败：{Describe(response)}");
        }
    }

    /// <summary>
    /// 将标准输出与标准错误拼接成便于日志与异常排查的可读文本
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


