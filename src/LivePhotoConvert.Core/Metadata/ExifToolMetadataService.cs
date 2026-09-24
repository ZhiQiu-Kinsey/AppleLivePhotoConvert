using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Threading.Channels;
using LivePhotoConvert.Core.External;
using LivePhotoConvert.Core.Io;
using LivePhotoConvert.Core.Media;
using LivePhotoConvert.Core.Pipeline;

namespace LivePhotoConvert.Core.Metadata;

/// <summary>
/// 基于 ExifTool 常驻进程池的元数据读写。
/// </summary>
/// <remarks>
/// 读取按批次（每条命令几十个文件）输出 JSON，一次往返取回配对校验、命名与写入所需的全部标签；
/// 进程池大小与转换并发度一致，避免单个 ExifTool 进程把并行流水线串行化。
/// </remarks>
public sealed class ExifToolMetadataService : IMetadataService
{
    private const int ReadBatchSize = 64;

    private static readonly string[] StandardReadArguments =
    [
        "-j", "-n", "-G1", "-a",
        "-DateTimeOriginal", "-OffsetTimeOriginal", "-CreateDate", "-CreationDate", "-MediaCreateDate",
        "-ContentIdentifier", "-Duration", "-MatrixStructure",
        "-GPSLatitude", "-GPSLongitude", "-GPSAltitude", "-Make", "-Model", "-Software",
        "-HDRHeadroom", "-HDRGain", "-AuxiliaryImageType", "-HDRGainMapVersion"
    ];

    private static readonly string[] StillImageTimeArguments = ["-ee", "-TrackDuration", "-MediaDuration", "-StillImageTime"];

    private readonly string _executablePath;
    private readonly string _configPath;
    private readonly int _maxSessions;
    private readonly Channel<ExifToolSession> _idle = Channel.CreateUnbounded<ExifToolSession>();
    private readonly ConcurrentBag<ExifToolSession> _sessions = [];
    private int _sessionCount;

    private ExifToolMetadataService(string executablePath, int maxSessions)
    {
        _executablePath = executablePath;
        _configPath = ExifToolConfig.EnsureCreated();
        _maxSessions = Math.Max(1, maxSessions);
    }

    public static string ExecutableName => OperatingSystem.IsWindows() ? "exiftool.exe" : "exiftool";

    /// <summary>
    /// 定位 ExifTool 并创建服务。
    /// </summary>
    /// <param name="executablePath">用户指定的路径；为空时在程序目录、tools 子目录与 PATH 中查找</param>
    /// <param name="maxSessions">最多同时运行的 ExifTool 进程数</param>
    /// <exception cref="FileNotFoundException">找不到 ExifTool</exception>
    public static ExifToolMetadataService Create(string? executablePath = null, int maxSessions = 2)
    {
        var path = ToolLocator.Find(ExecutableName, executablePath, "ExifTool", "exiftool")
                   ?? throw new ToolNotFoundException(ExecutableName);
        return new ExifToolMetadataService(path, maxSessions);
    }

    public async Task<IReadOnlyDictionary<string, MediaMetadata>> ReadAsync(
        IReadOnlyCollection<string> paths,
        MetadataScope scope = MetadataScope.Standard,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var byKey = new Dictionary<string, string>(PathKeyComparer);
        foreach (var path in paths)
        {
            byKey.TryAdd(PathKey(path), path);
        }

        var results = new ConcurrentDictionary<string, MediaMetadata>(StringComparer.Ordinal);
        string[] arguments = scope == MetadataScope.StillImageTime
            ? [.. StandardReadArguments, .. StillImageTimeArguments]
            : StandardReadArguments;

        await Parallel.ForEachAsync(
            byKey.Values.Chunk(ReadBatchSize),
            new ParallelOptions { MaxDegreeOfParallelism = _maxSessions, CancellationToken = cancellationToken },
            async (batch, token) =>
            {
                var response = await ExecuteAsync([.. arguments, .. batch], token);
                foreach (var metadata in ExifToolJson.ParseMetadata(response.StandardOutput))
                {
                    if (byKey.TryGetValue(PathKey(metadata.Path), out var original))
                    {
                        results[original] = metadata with { Path = original };
                    }
                }
            });

        return byKey.Values.ToDictionary(path => path, path => results.GetValueOrDefault(path) ?? MediaMetadata.Empty(path), StringComparer.Ordinal);
    }

    public async Task<string?> ReadXmpAsync(string path, CancellationToken cancellationToken = default)
    {
        if (MediaFileTypes.HasJpegExtension(path))
        {
            await using var stream = OpenRead(path);
            return MotionPhotoLayout.ReadJpegXmp(stream);
        }

        var response = await ExecuteAsync(["-b", "-XMP", path], cancellationToken);
        return string.IsNullOrWhiteSpace(response.StandardOutput) ? null : response.StandardOutput;
    }

    public async Task WriteMotionPhotoAsync(string coverPath, long videoLength, long presentationTimestampUs, CancellationToken cancellationToken = default)
    {
        var xmp = MotionPhotoXmp.Apply(await ReadXmpAsync(coverPath, cancellationToken), videoLength, presentationTimestampUs);
        await WithSidecarAsync(coverPath, xmp, async sidecar =>
        {
            var response = await ExecuteAsync([$"-xmp<={sidecar}", "-EXIF:MicroVideo=1", "-overwrite_original", coverPath], cancellationToken);
            EnsureUpdated(response, "写入动态照片标记");
        });
    }

    public async Task RemoveMotionPhotoAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        var xmp = MotionPhotoXmp.Remove(await ReadXmpAsync(imagePath, cancellationToken));
        await WithSidecarAsync(imagePath, xmp, async sidecar =>
        {
            List<string> arguments = sidecar is null ? [] : [$"-xmp<={sidecar}"];
            arguments.AddRange(["-EXIF:MicroVideo=", "-overwrite_original", imagePath]);
            var response = await ExecuteAsync(arguments, cancellationToken);
            ThrowIfFailed(response, "清除动态照片标记");
        });
    }

    public async Task CopyCoverMetadataAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        var response = await ExecuteAsync(
            ["-tagsFromFile", sourcePath, "-all:all", "--Orientation", "--ExifImageWidth", "--ExifImageHeight",
             "-unsafe", "-icc_profile", "-Orientation#=1", "-overwrite_original", destinationPath],
            cancellationToken);
        ThrowIfFailed(response, "复制封面元数据");
    }

    public async Task WriteApplePhotoIdentifierAsync(string photoPath, string contentIdentifier, CancellationToken cancellationToken = default)
    {
        var template = SidecarPath(photoPath, ".jpg");
        try
        {
            await File.WriteAllBytesAsync(template, AppleMakerNote.BuildTemplateJpeg(contentIdentifier), cancellationToken);
            var response = await ExecuteAsync(["-tagsFromFile", template, "-MakerNotes", "-overwrite_original", photoPath], cancellationToken);
            EnsureUpdated(response, "写入 Apple 照片标识");
        }
        finally
        {
            FileHelper.TryDeleteFile(template);
        }

        var verify = await ExecuteAsync(["-s3", "-ContentIdentifier", photoPath], cancellationToken);
        if (!string.Equals(verify.StandardOutput.Trim(), contentIdentifier, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"写入 Apple 照片标识后回读不一致：{verify}");
        }
    }

    public async Task WriteAppleVideoTagsAsync(string videoPath, AppleVideoTags tags, CancellationToken cancellationToken = default)
    {
        List<string> arguments = [];
        if (tags.CaptureTime is { } captureTime)
        {
            // QuickTime 以 UTC 存储；带偏移的时间由 ExifTool（QuickTimeUTC=1）换算，不带偏移时按本机时区换算
            var value = captureTime.ToExifString();
            foreach (var tag in (ReadOnlySpan<string>)["QuickTime:CreateDate", "QuickTime:ModifyDate", "TrackCreateDate", "TrackModifyDate", "MediaCreateDate", "MediaModifyDate", "Keys:CreationDate"])
            {
                arguments.Add($"-{tag}={value}");
            }
        }

        if (tags.Location is { } location)
        {
            arguments.Add($"-Keys:GPSCoordinates={location.ToQuickTimeString()}");
        }

        AddIfPresent(arguments, "Keys:Make", tags.Make);
        AddIfPresent(arguments, "Keys:Model", tags.Model);
        AddIfPresent(arguments, "Keys:Software", tags.Software);
        arguments.AddRange([$"-Keys:ContentIdentifier={tags.ContentIdentifier}", "-overwrite_original", videoPath]);

        var response = await ExecuteAsync(arguments, cancellationToken);
        EnsureUpdated(response, "写入 Apple 视频元数据");
    }

    public async ValueTask DisposeAsync()
    {
        _idle.Writer.TryComplete();
        foreach (var session in _sessions)
        {
            await session.DisposeAsync();
        }
    }

    private async Task<ExifToolResponse> ExecuteAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var session = await RentAsync(cancellationToken);
        try
        {
            return await session.ExecuteAsync(arguments, cancellationToken);
        }
        finally
        {
            _idle.Writer.TryWrite(session);
        }
    }

    private async ValueTask<ExifToolSession> RentAsync(CancellationToken cancellationToken)
    {
        if (_idle.Reader.TryRead(out var idle))
        {
            return idle;
        }

        if (Interlocked.Increment(ref _sessionCount) <= _maxSessions)
        {
            var session = new ExifToolSession(_executablePath, _configPath);
            _sessions.Add(session);
            return session;
        }

        Interlocked.Decrement(ref _sessionCount);
        return await _idle.Reader.ReadAsync(cancellationToken);
    }

    private static async Task WithSidecarAsync(string target, string? xmp, Func<string?, Task> action)
    {
        if (xmp is null)
        {
            await action(null);
            return;
        }

        var sidecar = SidecarPath(target, ".xmp");
        try
        {
            await File.WriteAllTextAsync(sidecar, xmp, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            await action(sidecar);
        }
        finally
        {
            FileHelper.TryDeleteFile(sidecar);
        }
    }

    private static string SidecarPath(string target, string extension) =>
        OutputCommitter.CreateStagingPath(Path.GetDirectoryName(Path.GetFullPath(target))!, extension);

    private static void AddIfPresent(List<string> arguments, string tag, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !value.AsSpan().ContainsAny('\r', '\n'))
        {
            arguments.Add($"-{tag}={value}");
        }
    }

    private static void ThrowIfFailed(ExifToolResponse response, string operation)
    {
        if (response.HasErrors)
        {
            throw new InvalidOperationException($"ExifTool {operation}失败：{response}");
        }
    }

    private static void EnsureUpdated(ExifToolResponse response, string operation)
    {
        ThrowIfFailed(response, operation);
        if (response.UpdatedFileCount != 1)
        {
            throw new InvalidOperationException($"ExifTool {operation}未生效：{response}");
        }
    }

    private static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess);

    private static readonly StringComparer PathKeyComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// ExifTool 在 Windows 上会把 SourceFile 中的反斜杠输出为正斜杠，比较前统一格式。
    /// </summary>
    private static string PathKey(string path) => Path.GetFullPath(path).Replace('\\', '/');
}
