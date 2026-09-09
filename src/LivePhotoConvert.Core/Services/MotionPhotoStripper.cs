using System.Collections.Concurrent;
using System.Collections.Frozen;
using LivePhotoConvert.Core.Abstractions;
using LivePhotoConvert.Core.Io;
using LivePhotoConvert.Core.Models;

namespace LivePhotoConvert.Core.Services;

/// <summary>
/// 动态照片瘦身服务：批量剥离内嵌视频并可选转换为 HEIC 格式，释放存储空间
/// </summary>
/// <param name="exifTool">EXIF 与 XMP 元数据读写服务</param>
/// <param name="imageConverter">图像格式转换服务</param>
/// <param name="progress">进度与状态汇报回调（可选）</param>
public sealed class MotionPhotoStripper(IExifTool exifTool, IImageConverter imageConverter, IProgressReporter? progress = null)
{
    /// <summary>
    /// 可能包含内嵌微视频的候选图片扩展名集合
    /// </summary>
    private static readonly FrozenSet<string> CandidateExtensions = new[] { ".jpg", ".jpeg", ".heic" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private readonly IProgressReporter _progress = progress ?? NullProgressReporter.Instance;

    /// <summary>
    /// 并发输出时的文件名原子占位锁
    /// </summary>
    private readonly Lock _outputGate = new();

    private static readonly string[] CompanionVideoExtensions = [".mov", ".MOV", ".mp4", ".MP4"];

    /// <summary>
    /// 寻找同名伴随短视频（用于苹果实况对或分离实况照片，如 IMG_0001.HEIC 对应 IMG_0001.MOV）
    /// </summary>
    public static string? FindCompanionVideo(string imagePath)
    {
        var dir = Path.GetDirectoryName(imagePath);
        if (string.IsNullOrEmpty(dir)) return null;
        var stem = Path.GetFileNameWithoutExtension(imagePath);
        foreach (var ext in CompanionVideoExtensions)
        {
            var candidate = Path.Combine(dir, stem + ext);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>
    /// 扫描输入目录中所有可能需要处理的图片文件列表
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
    /// 对指定输入路径（单文件或目录）执行免转码只读分析，快速估算可释放空间
    /// </summary>
    /// <param name="inputPath">输入文件或目录路径</param>
    /// <param name="convertToHeic">是否按转码 HEIC（质量 90 估算 45% 体积）预估体积</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>包含各项明细与统计总量的只读分析报告</returns>
    public async ValueTask<StripAnalysisReport> AnalyzeAsync(
        string inputPath,
        bool convertToHeic = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);

        IReadOnlyList<string> candidates;
        if (Directory.Exists(inputPath))
        {
            candidates = FindCandidates(inputPath);
        }
        else if (File.Exists(inputPath))
        {
            candidates = [inputPath];
        }
        else
        {
            throw new DirectoryNotFoundException($"指定的输入路径不存在：{inputPath}");
        }

        if (candidates.Count == 0)
        {
            return new StripAnalysisReport([], 0, 0, 0, 0);
        }

        var items = new ConcurrentBag<StripAnalysisItem>();

        await Parallel.ForEachAsync(
            candidates,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = MergeOptions.DefaultParallelism,
                CancellationToken = cancellationToken
            },
            async (filePath, token) =>
            {
                var fileInfo = new FileInfo(filePath);
                var originalBytes = fileInfo.Length;
                var ext = Path.GetExtension(filePath);
                var isAlreadyHeic = ext.Equals(".heic", StringComparison.OrdinalIgnoreCase);

                long videoBytes = 0;
                var hasVideo = false;

                // 1. 优先检查同名伴随短视频（苹果实况对 / 分离实况对）
                var companionVideo = FindCompanionVideo(filePath);
                if (companionVideo != null)
                {
                    try
                    {
                        var compInfo = new FileInfo(companionVideo);
                        if (compInfo.Exists)
                        {
                            videoBytes = compInfo.Length;
                            originalBytes += videoBytes;
                            hasVideo = true;
                        }
                    }
                    catch
                    {
                        // ignored
                    }
                }

                // 2. 若无伴随短视频，再检测是否为安卓单文件内嵌视频（MicroVideo）
                if (!hasVideo)
                {
                    try
                    {
                        var videoLength = await exifTool.TryReadMicroVideoOffsetAsync(filePath, token);
                        if (videoLength is > 0 && videoLength.Value < originalBytes)
                        {
                            videoBytes = videoLength.Value;
                            hasVideo = true;
                        }
                    }
                    catch (Exception) when (!token.IsCancellationRequested)
                    {
                        // 只读分析容错：取消异常必须向上传播，其余异常静默跳过
                    }
                }

                var cleanImageBytes = originalBytes - videoBytes;
                var needsHeicConversion = convertToHeic && !isAlreadyHeic;

                // 静态照片转 HEIC 压缩比通常约为原图 45%
                long estimatedHeicBytes = needsHeicConversion
                    ? (long)(cleanImageBytes * 0.45)
                    : cleanImageBytes;

                long estimatedFinalBytes = needsHeicConversion
                    ? estimatedHeicBytes
                    : cleanImageBytes;

                items.Add(new StripAnalysisItem(
                    filePath: filePath,
                    originalBytes: originalBytes,
                    videoBytes: videoBytes,
                    estimatedHeicBytes: estimatedHeicBytes,
                    hasEmbeddedVideo: hasVideo,
                    needsHeicConversion: needsHeicConversion,
                    estimatedFinalBytes: estimatedFinalBytes
                ));
            });

        var orderedItems = items.OrderBy(x => x.FilePath, StringComparer.OrdinalIgnoreCase).ToList();
        var totalOriginal = orderedItems.Sum(x => x.OriginalBytes);
        var totalVideo = orderedItems.Sum(x => x.VideoBytes);
        var totalEstimatedHeic = orderedItems.Sum(x => x.EstimatedHeicBytes);
        var totalEstimatedSaved = orderedItems.Sum(x => x.EstimatedSavedBytes);

        return new StripAnalysisReport(
            orderedItems,
            totalOriginal,
            totalVideo,
            totalEstimatedHeic,
            totalEstimatedSaved
        );
    }

    /// <summary>
    /// 对指定输入路径执行免转码只读分析（默认开启 HEIC 预估）
    /// </summary>
    public ValueTask<StripAnalysisReport> AnalyzeAsync(
        string inputPath,
        CancellationToken cancellationToken = default) =>
        AnalyzeAsync(inputPath, convertToHeic: true, cancellationToken);

    /// <summary>
    /// 基于 StripOptions 对输入目录执行免转码只读分析
    /// </summary>
    public ValueTask<StripAnalysisReport> AnalyzeAsync(
        StripOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return AnalyzeAsync(options.InputDirectory, options.ConvertToHeic, cancellationToken);
    }

    /// <summary>
    /// 执行动态照片的批量并发瘦身流水线
    /// </summary>
    /// <param name="options">瘦身控制选项（包含目标格式、输入输出路径、并发度等）</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>包含成功数、跳过数、节省字节数及失败明细的 <see cref="StripReport"/></returns>
    public async Task<StripReport> StripAsync(StripOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var isInPlace = string.IsNullOrEmpty(options.OutputDirectory);
        if (!isInPlace)
        {
            Directory.CreateDirectory(options.OutputDirectory!);
        }

        var candidates = FindCandidates(options.InputDirectory);
        var failures = new ConcurrentBag<FailureRecord>();
        var total = candidates.Count;
        var completed = 0;
        var stripped = 0;
        var converted = 0;
        var skipped = 0;
        long savedBytes = 0;

        var tempDirectory = Path.Combine(Path.GetTempPath(), "LivePhotoConvert", $"strip-{Guid.NewGuid():N}");
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
                        var result = await StripOneAsync(imagePath, options, tempDirectory, token);
                        if (result.WasStripped)
                        {
                            Interlocked.Increment(ref stripped);
                        }

                        if (result.WasConverted)
                        {
                            Interlocked.Increment(ref converted);
                        }

                        if (result.WasSkipped)
                        {
                            Interlocked.Increment(ref skipped);
                        }

                        Interlocked.Add(ref savedBytes, result.BytesSaved);
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

        return new StripReport
        {
            Total = total,
            StrippedCount = stripped,
            ConvertedCount = converted,
            Skipped = skipped,
            SavedBytes = savedBytes,
            Failures = [.. failures]
        };
    }

    /// <summary>
    /// 处理单个文件：剥离视频（如有） + 转换 HEIC（如需要）
    /// </summary>
    private async Task<StripOneResult> StripOneAsync(string imagePath, StripOptions options, string tempDirectory, CancellationToken cancellationToken)
    {
        var originalSize = new FileInfo(imagePath).Length;
        if (originalSize == 0)
        {
            throw new InvalidDataException($"输入文件为空 (0 字节)，无法执行空间瘦身：{imagePath}");
        }

        var isInPlace = string.IsNullOrEmpty(options.OutputDirectory);
        var ext = Path.GetExtension(imagePath);
        var isAlreadyHeic = ext.Equals(".heic", StringComparison.OrdinalIgnoreCase);

        var companionVideo = FindCompanionVideo(imagePath);
        long companionVideoBytes = 0;
        if (companionVideo != null && File.Exists(companionVideo))
        {
            try
            {
                companionVideoBytes = new FileInfo(companionVideo).Length;
                originalSize += companionVideoBytes;
            }
            catch
            {
                // ignored
            }
        }

        // 1. 检测是否为动态照片（伴随视频或内嵌视频）
        long? videoLength = null;
        if (companionVideoBytes == 0)
        {
            videoLength = await exifTool.TryReadMicroVideoOffsetAsync(imagePath, cancellationToken);
        }
        var hasEmbeddedVideo = videoLength is not null && videoLength.Value > 0;
        var hasCompanionVideo = companionVideoBytes > 0;
        var hasVideo = hasCompanionVideo || hasEmbeddedVideo;

        if (hasEmbeddedVideo && videoLength!.Value >= (originalSize - companionVideoBytes))
        {
            throw new InvalidDataException($"元数据中的视频长度 {videoLength.Value} 字节不小于文件总长度 {originalSize - companionVideoBytes} 字节，该文件可能已损坏。");
        }

        // 判定是否需要转 HEIC
        var needsConvert = options.ConvertToHeic && !isAlreadyHeic;

        // 如果既没有视频（伴随/内嵌）也不需要转 HEIC，则跳过
        if (!hasVideo && !needsConvert)
        {
            return StripOneResult.Skip;
        }

        var baseName = Path.GetFileNameWithoutExtension(imagePath);
        var tempId = $"{baseName}-{Guid.NewGuid():N}";

        // 提前保存原始文件时间戳（就地模式下原文件可能被删除，必须在操作前捕获）
        var originalCreationTime = File.GetCreationTime(imagePath);
        var originalLastWriteTime = File.GetLastWriteTime(imagePath);

        // 阶段一：剥离视频（提取纯图片部分）
        string cleanImagePath;
        if (hasEmbeddedVideo)
        {
            var photoLength = (originalSize - companionVideoBytes) - videoLength!.Value;
            cleanImagePath = Path.Combine(tempDirectory, $"{tempId}-clean{ext}");
            await BinaryFile.CopySegmentAsync(imagePath, cleanImagePath, 0, photoLength, cancellationToken);
            await exifTool.RemoveMotionPhotoTagsAsync(cleanImagePath, cancellationToken);
        }
        else
        {
            // 无内嵌视频（或为伴随视频模式），直接以原文件作为输入
            cleanImagePath = imagePath;
        }

        // 阶段二：转换为 HEIC
        string finalPath;
        string? tempHeicPath = null;
        var wasConverted = false;

        try
        {
            if (needsConvert)
            {
                tempHeicPath = Path.Combine(tempDirectory, $"{tempId}-heic.heic");
                await imageConverter.ConvertToHeicAsync(cleanImagePath, tempHeicPath, options.HeicQuality, cancellationToken);
                finalPath = tempHeicPath;
                wasConverted = true;
            }
            else
            {
                // 已经是 HEIC，只需使用剥离后的文件
                finalPath = cleanImagePath;
            }

            // 阶段三：写入目标位置并同步时间戳
            string resultPath;
            if (isInPlace)
            {
                // 就地模式：原子替换原文件
                if (needsConvert)
                {
                    // 扩展名从 .jpg → .heic，需要新文件名
                    resultPath = Path.Combine(Path.GetDirectoryName(imagePath)!, baseName + ".heic");
                    File.Move(finalPath, resultPath, overwrite: true);
                    // 删除原始 .jpg 文件（如果扩展名不同）
                    if (!string.Equals(imagePath, resultPath, StringComparison.OrdinalIgnoreCase))
                    {
                        FileHelper.TryDeleteFile(imagePath);
                    }
                }
                else
                {
                    // 仅剥离了视频，扩展名不变
                    resultPath = imagePath;
                    if (finalPath != imagePath)
                    {
                        File.Move(finalPath, imagePath, overwrite: true);
                    }
                }

                // 若存在同名伴随短视频，就地删除释放空间
                if (hasCompanionVideo && companionVideo != null)
                {
                    FileHelper.TryDeleteFile(companionVideo);
                }
            }
            else
            {
                // 输出目录模式
                var outputExt = wasConverted ? ".heic" : ext;
                var outputFileName = $"{baseName}{outputExt}";
                resultPath = UniquePath.ReserveAtomic(options.OutputDirectory!, outputFileName, options.Overwrite, _outputGate);
                File.Move(finalPath, resultPath, overwrite: true);
            }

            // 使用预先捕获的时间戳同步到最终文件（不依赖可能已被删除的原文件）
            try
            {
                File.SetCreationTime(resultPath, originalCreationTime);
                File.SetLastWriteTime(resultPath, originalLastWriteTime);
            }
            catch
            {
                // 忽略时间设置异常（例如部分只读网络驱动器）
            }

            var finalSize = new FileInfo(resultPath).Length;
            var bytesSaved = Math.Max(0, originalSize - finalSize);

            return new StripOneResult(hasVideo, wasConverted, false, bytesSaved);
        }
        finally
        {
            // 清理阶段一生成的临时干净文件（仅当它是临时文件时）
            if (hasVideo && cleanImagePath != imagePath)
            {
                FileHelper.TryDeleteFile(cleanImagePath);
            }

            // 如果 HEIC 转换后的临时文件还存在（可能在就地替换时已被 Move 走），尝试清理
            FileHelper.TryDeleteFile(tempHeicPath);
        }
    }

    /// <summary>
    /// 单个文件处理结果
    /// </summary>
    private readonly record struct StripOneResult(bool WasStripped, bool WasConverted, bool WasSkipped, long BytesSaved)
    {
        /// <summary>
        /// 跳过的结果常量
        /// </summary>
        public static StripOneResult Skip { get; } = new(false, false, true, 0);
    }
}
