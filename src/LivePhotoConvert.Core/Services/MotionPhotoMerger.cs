using System.Collections.Concurrent;
using LivePhotoConvert.Core.Abstractions;
using LivePhotoConvert.Core.Io;
using LivePhotoConvert.Core.Matching;
using LivePhotoConvert.Core.Models;

namespace LivePhotoConvert.Core.Services;

/// <summary>
/// 把照片与视频合成为动态照片
/// </summary>
/// <remarks>
/// 创建合成器
/// </remarks>
/// <param name="exifTool">元数据读写</param>
/// <param name="imageConverter">图片转换</param>
/// <param name="videoConverter">视频转换</param>
/// <param name="progress">进度回调</param>
public sealed class MotionPhotoMerger(IExifTool exifTool, IImageConverter imageConverter, IVideoConverter videoConverter, IProgressReporter? progress = null)
{
    private readonly IProgressReporter _progress = progress ?? NullProgressReporter.Instance;

    /// <summary>
    /// 「解析可用输出名 + 写入」必须是原子操作，否则并行时两个分组可能选中同一个输出路径
    /// </summary>
    private readonly Lock _outputGate = new();

    /// <summary>
    /// 合成动态照片
    /// </summary>
    /// <param name="pairing">匹配结果</param>
    /// <param name="options">合成参数</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>合成结果汇总</returns>
    public async Task<MergeReport> MergeAsync(PairingResult pairing, MergeOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pairing);
        ArgumentNullException.ThrowIfNull(options);
        Directory.CreateDirectory(options.OutputDirectory);
        var cleaner = new SourceFileCleaner(options.SourceFileAction, options.InputDirectory);
        var failures = new ConcurrentBag<FailureRecord>();
        var cleanupFailures = new ConcurrentBag<FailureRecord>();
        var skippedItems = new ConcurrentBag<FailureRecord>();
        var validator = options.SkipValidation ? null : new PairValidator(exifTool);
        var candidates = pairing.Pairs;
        // 同名文件可能生成多个候选（按扩展名优先级排序），最终每个文件名只合成一组
        var total = candidates.Select(pair => pair.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var completed = 0;
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

            // ── 阶段 2：按文件名分组，每组选第一个校验通过的候选 ──
            // 候选已按扩展名优先级排序，因此同名多格式（如 .heic 与 .jpg）优先取画质更好的；
            // 若首选与视频并非同一张实况照片（校验不通过），自动回退到同名的下一个候选。
            var chosen = new List<MediaPair>();
            foreach (var group in candidates.GroupBy(pair => pair.Name, StringComparer.OrdinalIgnoreCase))
            {
                var selected = group.FirstOrDefault(pair => validations[pair].IsAccepted);
                if (selected is null)
                {
                    foreach (var pair in group)
                    {
                        skippedItems.Add(new FailureRecord(group.Key, string.Join("；", validations[pair].Reasons)));
                    }
                    continue;
                }

                chosen.Add(selected);
                foreach (var other in group.Where(pair => !pair.Equals(selected)))
                {
                    skippedItems.Add(new FailureRecord(group.Key, $"同名候选 {Path.GetFileName(other.PhotoPath)} + {Path.GetFileName(other.VideoPath)} 未采用，已选用 {Path.GetFileName(selected.PhotoPath)} + {Path.GetFileName(selected.VideoPath)}"));
                }
            }

            // ── 阶段 3：并行合成选中的分组 ──
            await Parallel.ForEachAsync(
                chosen,
                new ParallelOptions { MaxDegreeOfParallelism = options.Parallelism, CancellationToken = cancellationToken },
                async (pair, token) =>
                {
                    try
                    {
                        await MergeOneAsync(pair, options, tempDirectory, token);
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
                        ErrorLogger.Log(ex, $"合成动态照片失败: {pair.Name}");
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
            TryDeleteDirectory(tempDirectory);
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
    /// 合成单组照片与视频
    /// </summary>
    /// <param name="pair">照片与视频</param>
    /// <param name="options">合成参数</param>
    /// <param name="tempDirectory">临时目录</param>
    /// <param name="cancellationToken">取消令牌</param>
    private async Task MergeOneAsync(MediaPair pair, MergeOptions options, string tempDirectory, CancellationToken cancellationToken)
    {
        string? temporaryPhoto = null;
        string? temporaryVideo = null;
        string? outputPath = null;
        try
        {
            // 安卓动态照片格式要求封面为 JPEG，HEIC 与 PNG 都需要先转换
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
                // 前置摄像头视频带镜像矩阵，安卓播放器不识别，需转码把方向烧进像素
                var isMirrored = await exifTool.IsMirroredVideoAsync(videoPath, cancellationToken);
                await videoConverter.ConvertToMp4Async(videoPath, temporaryVideo, isMirrored, cancellationToken);
                videoPath = temporaryVideo;
            }

            outputPath = ReserveOutputPath(options, pair.Name);
            var (photoLength, totalLength) = await BinaryFile.ConcatAsync(photoPath, videoPath, outputPath, cancellationToken);

            await exifTool.WriteMotionPhotoTagsAsync(outputPath, totalLength - photoLength, cancellationToken);

            // 写入元数据后校验输出文件（确保文件包含完整的封面与内嵌视频）
            var finalLength = new FileInfo(outputPath).Length;
            var videoLength = totalLength - photoLength;
            if (finalLength <= videoLength + 1024)
            {
                throw new InvalidDataException($"合成校验失败：输出文件 {finalLength} 字节异常过小，未能完整包含封面与内嵌视频。");
            }

            // 让合成后的照片保留原照片的时间，相册按时间排序时才不会乱。
            // 若原图片因第三方工具中转导致修改时间失真，回退参考配对的 .MOV 视频原始时间。
            try
            {
                var photoCreationTime = File.GetCreationTime(pair.PhotoPath);
                var photoWriteTime = File.GetLastWriteTime(pair.PhotoPath);
                var videoCreationTime = File.GetCreationTime(pair.VideoPath);
                var videoWriteTime = File.GetLastWriteTime(pair.VideoPath);

                var earliestCreation = photoCreationTime < videoCreationTime ? photoCreationTime : videoCreationTime;
                var earliestWrite = photoWriteTime < videoWriteTime ? photoWriteTime : videoWriteTime;

                File.SetCreationTime(outputPath, earliestCreation);
                File.SetLastWriteTime(outputPath, earliestWrite);
            }
            catch (Exception)
            {
                // 时间没设上不影响动态照片本身
            }
        }
        catch (Exception)
        {
            // 失败时清掉写了一半的输出，避免在输出目录留下无法播放的残次品
            TryDeleteFile(outputPath);
            throw;
        }
        finally
        {
            TryDeleteFile(temporaryPhoto);
            TryDeleteFile(temporaryVideo);
        }
    }

    /// <summary>
    /// 确定输出路径，并在非覆盖模式下占住这个文件名
    /// </summary>
    /// <param name="options">合成参数</param>
    /// <param name="baseName">原照片的文件名（不含扩展名）</param>
    /// <returns>输出路径</returns>
    private string ReserveOutputPath(MergeOptions options, string baseName)
    {
        var fileName = $"MVIMG_{baseName}.jpg";
        if (options.Overwrite)
        {
            return Path.Combine(options.OutputDirectory, fileName);
        }
        // 解析出可用文件名后立刻创建占位文件，避免并行的另一组解析到同一个名字
        lock (_outputGate)
        {
            var path = UniquePath.Resolve(options.OutputDirectory, fileName);
            File.Create(path).Dispose();
            return path;
        }
    }

    /// <summary>
    /// 尽力删除临时文件
    /// </summary>
    /// <param name="path">文件路径，可为空</param>
    private static void TryDeleteFile(string? path)
    {
        if (path is null)
        {
            return;
        }
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // 临时目录整体会被删除，单个文件删不掉不影响结果
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
            Directory.Delete(path, recursive: true);
        }
        catch (Exception)
        {
            // 删不掉只会留下一个空的临时目录，不影响已合成的照片
        }
    }
}
