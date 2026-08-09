using System.Collections.Concurrent;
using LivePhotoConvert.Core.Abstractions;
using LivePhotoConvert.Core.Io;
using LivePhotoConvert.Core.Matching;
using LivePhotoConvert.Core.Models;

namespace LivePhotoConvert.Core.Services;

/// <summary>
/// 把动态照片拆分回照片与视频
/// </summary>
/// <remarks>
/// 创建拆分器
/// </remarks>
/// <param name="exifTool">元数据读写</param>
/// <param name="videoConverter">视频转换器，转为 Apple Live Photo 格式时使用</param>
/// <param name="progress">进度回调</param>
public sealed class MotionPhotoSplitter(IExifTool exifTool, IVideoConverter? videoConverter = null, IProgressReporter? progress = null)
{
    /// <summary>
    /// 可能包含内嵌视频的文件扩展名
    /// </summary>
    private static readonly string[] CandidateExtensions = [".jpg", ".jpeg", ".heic"];
    private readonly IProgressReporter _progress = progress ?? NullProgressReporter.Instance;

    /// <summary>
    /// 照片与视频必须成对使用同一个文件名后缀，解析与占位要一次做完
    /// </summary>
    private readonly Lock _outputGate = new();

    /// <summary>
    /// 扫描输入目录中可能是动态照片的文件
    /// </summary>
    /// <param name="inputDirectory">输入目录</param>
    /// <returns>候选文件路径</returns>
    public static IReadOnlyList<string> FindCandidates(string inputDirectory) =>
    [
        .. Directory.EnumerateFiles(inputDirectory, "*", SearchOption.TopDirectoryOnly)
                    .Where(path => CandidateExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                    .Order(StringComparer.OrdinalIgnoreCase)
    ];

    /// <summary>
    /// 拆分动态照片
    /// </summary>
    /// <param name="options">拆分参数</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>拆分结果汇总</returns>
    public async Task<SplitReport> SplitAsync(SplitOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        Directory.CreateDirectory(options.OutputDirectory);
        var candidates = FindCandidates(options.InputDirectory);
        var failures = new ConcurrentBag<FailureRecord>();
        var total = candidates.Count;
        var completed = 0;
        var succeeded = 0;
        var skipped = 0;
        var tempDirectory = Path.Combine(Path.GetTempPath(), "LivePhotoConvert", $"split-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        try
        {
            await Parallel.ForEachAsync(
                candidates,
                new ParallelOptions { MaxDegreeOfParallelism = options.Parallelism, CancellationToken = cancellationToken },
                async (imagePath, token) =>
                {
                    try
                    {
                        if (await SplitOneAsync(imagePath, options, tempDirectory, token))
                        {
                            Interlocked.Increment(ref succeeded);
                        }
                        else
                        {
                            Interlocked.Increment(ref skipped);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        failures.Add(new FailureRecord(Path.GetFileName(imagePath), ex.Message));
                    }
                    finally
                    {
                        _progress.Report(Interlocked.Increment(ref completed), total, Path.GetFileName(imagePath));
                    }
                });
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }

        return new SplitReport
        {
            Total = total,
            Succeeded = succeeded,
            Skipped = skipped,
            Failures = [.. failures]
        };
    }

    /// <summary>
    /// 拆分单个文件
    /// </summary>
    /// <param name="imagePath">动态照片路径</param>
    /// <param name="options">拆分参数</param>
    /// <param name="tempDirectory">临时目录</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>是否确实拆分了；文件不是动态照片时返回 <c>false</c></returns>
    private async Task<bool> SplitOneAsync(string imagePath, SplitOptions options, string tempDirectory, CancellationToken cancellationToken)
    {
        var videoLength = await exifTool.TryReadMicroVideoOffsetAsync(imagePath, cancellationToken);
        if (videoLength is null)
        {
            // 目录里混有普通图片是常态，跳过即可，不算失败
            return false;
        }

        var totalLength = new FileInfo(imagePath).Length;
        if (videoLength.Value >= totalLength)
        {
            throw new InvalidDataException($"元数据中的视频长度 {videoLength.Value} 字节不小于文件总长度 {totalLength} 字节，该文件可能已损坏。");
        }

        var photoLength = totalLength - videoLength.Value;
        var baseName = Path.GetFileNameWithoutExtension(imagePath);
        // 嗅探照片与视频实际的二进制格式
        var photoExt = await SniffExtensionAsync(imagePath, 0, (int)Math.Min(photoLength, 64), h => MediaFileTypes.DetectPhotoExtension(h, Path.GetExtension(imagePath)), cancellationToken);
        var videoExt = await SniffExtensionAsync(imagePath, photoLength, (int)Math.Min(videoLength.Value, 64), h => MediaFileTypes.DetectVideoExtension(h), cancellationToken);
        if (options.TargetFormat == SplitTargetFormat.Apple)
        {
            if (videoConverter is null)
            {
                throw new InvalidOperationException("未配置视频转换器，无法转换为 Apple Live Photo 格式。");
            }
            var (photoPath, videoPath) = ReserveOutputPaths(options, baseName, photoExt, ".mov");
            string? tempVideo = null;
            try
            {
                // 照片直接截取并清理动态照片标记
                await BinaryFile.CopySegmentAsync(imagePath, photoPath, 0, photoLength, cancellationToken);
                await exifTool.RemoveMotionPhotoTagsAsync(photoPath, cancellationToken);

                // 提取内嵌视频到临时文件
                tempVideo = Path.Combine(tempDirectory, $"{Guid.NewGuid():N}{videoExt}");
                await BinaryFile.CopySegmentAsync(imagePath, tempVideo, photoLength, videoLength.Value, cancellationToken);

                // 无损重封装为 QuickTime MOV 容器
                await videoConverter.RemuxToMovAsync(tempVideo, videoPath, cancellationToken);

                // 生成全局唯一配对 UUID 并写入照片与视频
                var contentIdentifier = Guid.NewGuid().ToString().ToUpperInvariant();
                await exifTool.WriteAppleContentIdentifierAsync(photoPath, contentIdentifier, cancellationToken);
                await exifTool.WriteAppleVideoMetadataAsync(videoPath, contentIdentifier, cancellationToken);

                PreserveTimestamps(imagePath, photoPath, videoPath);
                return true;
            }
            catch (Exception)
            {
                TryDeleteFile(photoPath);
                TryDeleteFile(videoPath);
                throw;
            }
            finally
            {
                TryDeleteFile(tempVideo);
            }
        }
        else
        {
            // 标准安卓格式无损解包
            var (photoPath, videoPath) = ReserveOutputPaths(options, baseName, photoExt, videoExt);
            try
            {
                // 照片在前
                await BinaryFile.CopySegmentAsync(imagePath, photoPath, 0, photoLength, cancellationToken);
                // 拆出来的照片仍带着动态照片标记，清掉才是一张普通图片
                await exifTool.RemoveMotionPhotoTagsAsync(photoPath, cancellationToken);
                // 视频在后
                await BinaryFile.CopySegmentAsync(imagePath, videoPath, photoLength, videoLength.Value, cancellationToken);

                PreserveTimestamps(imagePath, photoPath, videoPath);
                return true;
            }
            catch (Exception)
            {
                TryDeleteFile(photoPath);
                TryDeleteFile(videoPath);
                throw;
            }
        }
    }

    /// <summary>
    /// 从文件指定偏移读取头部字节并嗅探扩展名
    /// </summary>
    private static async Task<string> SniffExtensionAsync(string filePath, long offset, int length, Func<ReadOnlySpan<byte>, string> sniffer, CancellationToken cancellationToken)
    {
        var bufferLength = Math.Min(64, length);
        if (bufferLength <= 0)
        {
            return sniffer(ReadOnlySpan<byte>.Empty);
        }

        var buffer = new byte[bufferLength];
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        stream.Seek(offset, SeekOrigin.Begin);
        var read = await stream.ReadAsync(buffer.AsMemory(0, bufferLength), cancellationToken);
        return sniffer(buffer.AsSpan(0, read));
    }

    /// <summary>
    /// 继承原照片的时间戳，确保在相册中查看时按时间正确排序
    /// </summary>
    private static void PreserveTimestamps(string sourcePath, string photoPath, string videoPath)
    {
        try
        {
            var creationTime = File.GetCreationTime(sourcePath);
            var lastWriteTime = File.GetLastWriteTime(sourcePath);

            File.SetCreationTime(photoPath, creationTime);
            File.SetLastWriteTime(photoPath, lastWriteTime);

            File.SetCreationTime(videoPath, creationTime);
            File.SetLastWriteTime(videoPath, lastWriteTime);
        }
        catch (Exception)
        {
            // 部分文件系统不支持设置时间，不影响文件本身
        }
    }

    /// <summary>
    /// 确定成对的输出路径，并在非覆盖模式下占住这两个文件名
    /// </summary>
    /// <param name="options">拆分参数</param>
    /// <param name="baseName">原文件名（不含扩展名）</param>
    /// <param name="photoExt">照片扩展名</param>
    /// <param name="videoExt">视频扩展名</param>
    /// <returns>照片与视频的输出路径</returns>
    /// <exception cref="IOException">尝试次数耗尽仍未找到可用文件名</exception>
    private (string PhotoPath, string VideoPath) ReserveOutputPaths(SplitOptions options, string baseName, string photoExt, string videoExt)
    {
        if (options.Overwrite)
        {
            return (Path.Combine(options.OutputDirectory, $"{baseName}{photoExt}"), Path.Combine(options.OutputDirectory, $"{baseName}{videoExt}"));
        }

        // 照片和视频要用同一个后缀，因此不能各自独立解析文件名
        lock (_outputGate)
        {
            for (var index = 0; index < int.MaxValue; index++)
            {
                var suffix = index == 0 ? string.Empty : $"_{index}";
                var photoPath = Path.Combine(options.OutputDirectory, $"{baseName}{suffix}{photoExt}");
                var videoPath = Path.Combine(options.OutputDirectory, $"{baseName}{suffix}{videoExt}");
                if (Occupied(photoPath) || Occupied(videoPath))
                {
                    continue;
                }

                // 立刻占位，避免并行的另一个文件解析到同一组名字
                File.Create(photoPath).Dispose();
                File.Create(videoPath).Dispose();
                return (photoPath, videoPath);
            }

            throw new IOException($"无法为 {baseName} 找到不冲突的输出文件名。");
        }

        static bool Occupied(string path) => File.Exists(path) || Directory.Exists(path);
    }

    /// <summary>
    /// 尽力删除文件
    /// </summary>
    /// <param name="path">文件路径</param>
    private static void TryDeleteFile(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // 删不掉只会留下一个残缺文件，不影响其他文件的处理
        }
    }

    /// <summary>
    /// 尽力删除临时目录
    /// </summary>
    /// <param name="path">目录路径</param>
    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception)
        {
            // 临时目录清理失败不影响主流程
        }
    }
}
