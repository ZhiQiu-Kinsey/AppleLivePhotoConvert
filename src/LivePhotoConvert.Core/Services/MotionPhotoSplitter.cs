using System.Collections.Concurrent;
using System.Collections.Frozen;
using LivePhotoConvert.Core.Abstractions;
using LivePhotoConvert.Core.Io;
using LivePhotoConvert.Core.Matching;
using LivePhotoConvert.Core.Models;

namespace LivePhotoConvert.Core.Services;

/// <summary>
/// 动态照片拆分调度服务（支持将 Android/Google 动态照片无损解包为独立图片与视频，或重构封装为 iOS 兼容的 Apple Live Photo 实况照片）
/// </summary>
/// <param name="exifTool">EXIF 与 XMP 元数据读写服务</param>
/// <param name="videoConverter">视频转换与重封装服务（转为 Apple Live Photo 模式时必需）</param>
/// <param name="progress">进度与状态汇报回调（可选）</param>
public sealed class MotionPhotoSplitter(IExifTool exifTool, IVideoConverter? videoConverter = null, IProgressReporter? progress = null)
{
    /// <summary>
    /// 可能包含内嵌微视频的候选图片扩展名集合
    /// </summary>
    private static readonly FrozenSet<string> CandidateExtensions = new[] { ".jpg", ".jpeg", ".heic" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private readonly IProgressReporter _progress = progress ?? NullProgressReporter.Instance;

    /// <summary>
    /// 并发拆分输出时的成对文件名原子占位锁
    /// </summary>
    private readonly Lock _outputGate = new();

    /// <summary>
    /// 扫描输入目录中所有可能为动态照片的图片文件列表
    /// </summary>
    /// <param name="inputDirectory">输入目录路径</param>
    /// <returns>按字母序排列的候选文件完整路径列表</returns>
    public static IReadOnlyList<string> FindCandidates(string inputDirectory) =>
    [
        .. Directory.EnumerateFiles(inputDirectory, "*", SearchOption.TopDirectoryOnly)
                    .Where(path => CandidateExtensions.Contains(Path.GetExtension(path)))
                    .Order(StringComparer.OrdinalIgnoreCase)
    ];

    /// <summary>
    /// 执行动态照片的批量并发拆分流水线
    /// </summary>
    /// <param name="options">拆分控制选项（包含目标格式、输入输出路径、并发度、覆盖策略等）</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>包含成功数、跳过非实况图片数及失败明细的 <see cref="SplitReport"/></returns>
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
            FileHelper.TryDeleteDirectory(tempDirectory);
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
    /// 拆分单个动态照片文件
    /// </summary>
    /// <param name="imagePath">待拆分的动态照片路径</param>
    /// <param name="options">拆分控制选项</param>
    /// <param name="tempDirectory">临时工作目录</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>若成功识别并完成拆分返回 <c>true</c>；若文件为普通非动态照片则跳过并返回 <c>false</c></returns>
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

        // 嗅探照片与视频实际的二进制格式（防止扩展名被篡改或封面非 JPEG）
        var photoExt = await SniffExtensionAsync(imagePath, 0, (int)Math.Min(photoLength, 64), h => MediaFileTypes.DetectPhotoExtension(h, Path.GetExtension(imagePath)), cancellationToken);
        var videoExt = await SniffExtensionAsync(imagePath, photoLength, (int)Math.Min(videoLength.Value, 64), h => MediaFileTypes.DetectVideoExtension(h), cancellationToken);

        if (options.TargetFormat == SplitTargetFormat.Apple)
        {
            if (videoConverter is null)
            {
                throw new InvalidOperationException("未配置视频转换器，无法转换为 Apple Live Photo 格式。");
            }

            // 原子成对预留输出文件
            var (photoPath, videoPath) = UniquePath.ReservePairAtomic(options.OutputDirectory, baseName, photoExt, ".mov", options.Overwrite, _outputGate);
            string? tempVideo = null;
            try
            {
                // 照片直接流式截取并清理残留的 GCamera 动态照片标记
                await BinaryFile.CopySegmentAsync(imagePath, photoPath, 0, photoLength, cancellationToken);
                await exifTool.RemoveMotionPhotoTagsAsync(photoPath, cancellationToken);

                // 提取内嵌视频到临时文件
                tempVideo = Path.Combine(tempDirectory, $"{Guid.NewGuid():N}{videoExt}");
                await BinaryFile.CopySegmentAsync(imagePath, tempVideo, photoLength, videoLength.Value, cancellationToken);

                // 无损重封装为 QuickTime MOV 容器
                await videoConverter.RemuxToMovAsync(tempVideo, videoPath, cancellationToken);

                // 生成全局唯一的配对 UUID 并写入照片 EXIF 与 QuickTime 视频 Keys
                var contentIdentifier = Guid.NewGuid().ToString().ToUpperInvariant();
                await exifTool.WriteAppleContentIdentifierAsync(photoPath, contentIdentifier, cancellationToken);
                await exifTool.WriteAppleVideoMetadataAsync(videoPath, contentIdentifier, cancellationToken);

                // 同步原动态照片的拍摄时间戳
                FileTimestamp.Sync(imagePath, photoPath, videoPath);
                return true;
            }
            catch (Exception)
            {
                FileHelper.TryDeleteFile(photoPath);
                FileHelper.TryDeleteFile(videoPath);
                throw;
            }
            finally
            {
                FileHelper.TryDeleteFile(tempVideo);
            }
        }
        else
        {
            // 标准安卓格式无损解包（生成封面 .jpg 与微视频 .mp4）
            var (photoPath, videoPath) = UniquePath.ReservePairAtomic(options.OutputDirectory, baseName, photoExt, videoExt, options.Overwrite, _outputGate);
            try
            {
                // 1. 照片流式截取（前半段）
                await BinaryFile.CopySegmentAsync(imagePath, photoPath, 0, photoLength, cancellationToken);
                // 拆出来的照片仍带着动态照片标记，清除后方为标准静态图片
                await exifTool.RemoveMotionPhotoTagsAsync(photoPath, cancellationToken);

                // 2. 视频流式截取（后半段）
                await BinaryFile.CopySegmentAsync(imagePath, videoPath, photoLength, videoLength.Value, cancellationToken);

                // 同步原动态照片的拍摄时间戳
                FileTimestamp.Sync(imagePath, photoPath, videoPath);
                return true;
            }
            catch (Exception)
            {
                FileHelper.TryDeleteFile(photoPath);
                FileHelper.TryDeleteFile(videoPath);
                throw;
            }
        }
    }

    /// <summary>
    /// 从文件指定偏移位置读取少量头部特征字节并进行零分配格式嗅探
    /// </summary>
    /// <param name="filePath">文件全路径</param>
    /// <param name="offset">数据起始偏移（字节）</param>
    /// <param name="length">读取长度（字节）</param>
    /// <param name="sniffer">嗅探器委托函数</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>检测出的扩展名字符串（含句点）</returns>
    private static async Task<string> SniffExtensionAsync(string filePath, long offset, int length, Func<ReadOnlySpan<byte>, string> sniffer, CancellationToken cancellationToken)
    {
        var bufferLength = Math.Min(64, length);
        if (bufferLength <= 0)
        {
            return sniffer([]);
        }

        var buffer = new byte[bufferLength];
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        stream.Seek(offset, SeekOrigin.Begin);
        var read = await stream.ReadAsync(buffer.AsMemory(0, bufferLength), cancellationToken);
        return sniffer(buffer.AsSpan(0, read));
    }
}
