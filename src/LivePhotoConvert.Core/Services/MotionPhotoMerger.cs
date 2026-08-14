using System.Collections.Concurrent;
using LivePhotoConvert.Core.Abstractions;
using LivePhotoConvert.Core.Io;
using LivePhotoConvert.Core.Matching;
using LivePhotoConvert.Core.Models;

namespace LivePhotoConvert.Core.Services;

/// <summary>
/// 动态照片合成核心调度服务（将 iPhone 实况照片或其他成对的图片与微视频合成为符合 Google GCamera 规范的 Motion Photo）
/// </summary>
/// <param name="exifTool">EXIF 与 XMP 元数据读写服务</param>
/// <param name="imageConverter">图像解码与转码服务</param>
/// <param name="videoConverter">视频转码与容器封装服务</param>
/// <param name="progress">进度与状态汇报回调（可选）</param>
public sealed class MotionPhotoMerger(IExifTool exifTool, IImageConverter imageConverter, IVideoConverter videoConverter, IProgressReporter? progress = null)
{
    private readonly IProgressReporter _progress = progress ?? NullProgressReporter.Instance;

    /// <summary>
    /// 多线程并发输出时的文件名原子预留同步锁
    /// </summary>
    private readonly Lock _outputGate = new();

    /// <summary>
    /// 执行动态照片的批量并发合成流水线
    /// </summary>
    /// <remarks>
    /// 合成流水线分为三个核心阶段：<br/>
    /// 1. <b>并行校验阶段</b>：对所有候选媒体对进行时间戳与时长校验，剔除无关的同名照片/视频；<br/>
    /// 2. <b>分组仲裁阶段</b>：优先采纳 ContentIdentifier 确定性配对，同名多格式自动选举最高画质组（如 HEIC 优于 JPG）；<br/>
    /// 3. <b>并行合成与清理阶段</b>：转码格式 $\to$ 原子占位 $\to$ 二进制流拼接 $\to$ 注入 XMP $\to$ 校验有效性 $\to$ 同步时间戳 $\to$ 安全归档原文件。
    /// </remarks>
    /// <param name="pairing">由匹配引擎初步分析得到的媒体配对结果</param>
    /// <param name="options">合成控制选项（包含并发度、输出目录、覆盖策略、原文件清理动作等）</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>包含成功数、跳过项、失败明细与清理计数的 <see cref="MergeReport"/></returns>
    public async Task<MergeReport> MergeAsync(PairingResult pairing, MergeOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pairing);
        ArgumentNullException.ThrowIfNull(options);
        Directory.CreateDirectory(options.OutputDirectory);
        var cleaner = new SourceFileCleaner(options.SourceFileAction, options.InputDirectory);
        var failures = new ConcurrentBag<FailureRecord>();
        var cleanupFailures = new ConcurrentBag<FailureRecord>();
        var skippedItems = new ConcurrentBag<FailureRecord>();
        var validator = options.SkipValidation ? null : new PairValidator(exifTool, pairing.PhotoContentIdentifiers, pairing.VideoContentIdentifiers);
        var candidates = pairing.Pairs;
        int total;
        var succeeded = 0;
        var cleanedFiles = 0;

        // 临时目录建在系统高速本地临时目录下，避免在外部驱动器/移动硬盘上高并发创建产生 I/O 拥堵
        var tempDirectory = Path.Combine(Path.GetTempPath(), "LivePhotoConvert", $"temp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        try
        {
            // ── 阶段 1：并行校验所有候选，判断照片与视频是否确属同一张实况照片 ──
            var validations = new ConcurrentDictionary<MediaPair, PairValidationResult>();
            await Parallel.ForEachAsync(
                candidates,
                new ParallelOptions { MaxDegreeOfParallelism = options.Parallelism, CancellationToken = cancellationToken },
                async (pair, token) =>
                {
                    var result = validator is null
                        ? PairValidationResult.Accept([])
                        : await validator.ValidateAsync(pair, token);
                    validations[pair] = result;
                });

            // ── 阶段 2：选择参与合成的分组 ──
            // ContentIdentifier 精确配对的候选是确定的 1:1 配对，直接采用；
            // 其余同名候选按扩展名优先级排序，每组选第一个校验通过的（同名多格式取画质更好的）。
            var chosen = new List<MediaPair>();
            foreach (var pair in candidates.Where(pair => pair.IsContentIdentifierMatched))
            {
                if (validations[pair].IsAccepted)
                {
                    chosen.Add(pair);
                }
                else
                {
                    skippedItems.Add(new FailureRecord(pair.Name, string.Join("；", validations[pair].Reasons)));
                }
            }

            foreach (var group in candidates.Where(pair => !pair.IsContentIdentifierMatched)
                                            .GroupBy(pair => pair.Name, StringComparer.OrdinalIgnoreCase))
            {
                var selected = group.FirstOrDefault(pair => validations[pair].IsAccepted);
                if (selected is null)
                {
                    var reasons = group.SelectMany(p => validations[p].Reasons).Distinct();
                    skippedItems.Add(new FailureRecord(group.Key, string.Join("；", reasons)));
                    continue;
                }

                chosen.Add(selected);
            }

            total = chosen.Count + skippedItems.Count;
            var completed = skippedItems.Count;

            // 同名但不同内容的照片（iCloud 下载可能出现 IMG_0456.JPG 与 IMG_0456.JPEG 两张不同实况），
            // 输出名用扩展名区分，避免互相覆盖
            var outputNames = ResolveOutputNames(chosen);

            // ── 阶段 3：并行合成选中的分组 ──
            await Parallel.ForEachAsync(
                chosen,
                new ParallelOptions { MaxDegreeOfParallelism = options.Parallelism, CancellationToken = cancellationToken },
                async (pair, token) =>
                {
                    try
                    {
                        var outputBaseName = outputNames[pair];
                        await MergeOneAsync(pair, outputBaseName, options, tempDirectory, token);
                        Interlocked.Increment(ref succeeded);

                        // 只有合成成功并通过校验的这一组才会被清理，未匹配的文件绝不会进入这里
                        var cleanup = cleaner.Clean([pair.PhotoPath, pair.VideoPath]);
                        Interlocked.Add(ref cleanedFiles, cleanup.CleanedCount);
                        foreach (var failure in cleanup.Failures)
                        {
                            cleanupFailures.Add(failure);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        failures.Add(new FailureRecord(Path.GetFileName(pair.PhotoPath), ex.Message));
                    }
                    finally
                    {
                        _progress.Report(Interlocked.Increment(ref completed), total, Path.GetFileName(pair.PhotoPath));
                    }
                });
        }
        finally
        {
            FileHelper.TryDeleteDirectory(tempDirectory);
        }

        return new MergeReport
        {
            Total = total,
            Succeeded = succeeded,
            CleanedFileCount = cleanedFiles,
            SkippedItems = [.. skippedItems],
            Failures = [.. failures],
            CleanupFailures = [.. cleanupFailures]
        };
    }

    /// <summary>
    /// 合成单组照片与视频为 Google 格式动态照片
    /// </summary>
    /// <param name="pair">当前待合成的媒体对</param>
    /// <param name="outputBaseName">输出文件的基础名称（不含 MVIMG_ 前缀与 .jpg 扩展名）</param>
    /// <param name="options">合成控制选项</param>
    /// <param name="tempDirectory">线程隔离的临时工作目录</param>
    /// <param name="cancellationToken">取消令牌</param>
    private async Task MergeOneAsync(MediaPair pair, string outputBaseName, MergeOptions options, string tempDirectory, CancellationToken cancellationToken)
    {
        string? temporaryPhoto = null;
        string? temporaryVideo = null;
        string? outputPath = null;
        try
        {
            // 安卓动态照片格式规范要求封面必须为标准 JPEG，HEIC 与 PNG 都需要先转码
            var photoPath = pair.PhotoPath;
            if (!MediaFileTypes.IsJpeg(photoPath))
            {
                temporaryPhoto = Path.Combine(tempDirectory, $"{Guid.NewGuid():N}.jpg");
                await imageConverter.ConvertToJpegAsync(photoPath, temporaryPhoto, cancellationToken);
                photoPath = temporaryPhoto;
            }

            var videoPath = pair.VideoPath;
            if (!MediaFileTypes.IsMp4(videoPath))
            {
                temporaryVideo = Path.Combine(tempDirectory, $"{Guid.NewGuid():N}.mp4");
                // 检查是否为带有 2D 镜像矩阵的前置摄像头视频，若存在镜像则强制重新编码烧录像素
                var isMirrored = await exifTool.IsMirroredVideoAsync(videoPath, cancellationToken);
                await videoConverter.ConvertToMp4Async(videoPath, temporaryVideo, isMirrored, cancellationToken);
                videoPath = temporaryVideo;
            }

            // 在并发锁保护下原子解析并占位目标文件，杜绝多线程重名竞态
            outputPath = UniquePath.ReserveAtomic(options.OutputDirectory, $"MVIMG_{outputBaseName}.jpg", options.Overwrite, _outputGate);
            var (photoLength, totalLength) = await BinaryFile.ConcatAsync(photoPath, videoPath, outputPath, cancellationToken);

            // 写入 Google GCamera XMP 动态照片元数据与微视频偏移量
            await exifTool.WriteMotionPhotoTagsAsync(outputPath, totalLength - photoLength, cancellationToken);

            // 写入元数据后校验输出文件（确保文件完整包含封面与内嵌视频）
            var finalLength = new FileInfo(outputPath).Length;
            var videoLength = totalLength - photoLength;
            if (finalLength <= videoLength + 1024)
            {
                throw new InvalidDataException($"合成校验失败：输出文件 {finalLength} 字节异常过小，未能完整包含封面与内嵌视频。");
            }

            // 让合成后的照片保留原照片或视频中更早的时间戳，相册按时间排序时才不会乱
            FileTimestamp.SyncEarliest(outputPath, pair.PhotoPath, pair.VideoPath);
        }
        catch (Exception)
        {
            // 失败时清理写了一半的半成品输出，避免在输出目录留下损坏无法播放的文件
            FileHelper.TryDeleteFile(outputPath);
            throw;
        }
        finally
        {
            FileHelper.TryDeleteFile(temporaryPhoto);
            FileHelper.TryDeleteFile(temporaryVideo);
        }
    }

    /// <summary>
    /// 为选中的分组解析唯一输出名：同名但不同内容的照片（扩展名不同）用扩展名区分
    /// </summary>
    /// <param name="pairs">选中的分组</param>
    /// <returns>分组到输出基础名（不含扩展名）的映射字典</returns>
    private static Dictionary<MediaPair, string> ResolveOutputNames(IReadOnlyList<MediaPair> pairs)
    {
        var result = new Dictionary<MediaPair, string>();
        foreach (var group in pairs.GroupBy(pair => pair.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() == 1)
            {
                result[group.First()] = group.Key;
                continue;
            }

            // 同名但照片文件不同（如 IMG_0456.JPG 与 IMG_0456.JPEG 是两张不同的实况照片），
            // 用照片扩展名区分输出名
            foreach (var pair in group)
            {
                var extension = Path.GetExtension(pair.PhotoPath).TrimStart('.').ToLowerInvariant();
                result[pair] = $"{pair.Name}.{extension}";
            }
        }

        return result;
    }
}
