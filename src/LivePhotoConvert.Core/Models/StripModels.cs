namespace LivePhotoConvert.Core.Models;

/// <summary>
/// 动态照片瘦身（剥离视频与 HEIC 转换）的参数
/// </summary>
public sealed record StripOptions
{
    /// <summary>
    /// 输入目录，存放待处理的图片文件
    /// </summary>
    public required string InputDirectory { get; init; }

    /// <summary>
    /// 输出目录；为 <c>null</c> 时就地修改原文件（原子替换，异常时不损坏原文件）
    /// </summary>
    public string? OutputDirectory { get; init; }

    /// <summary>
    /// 是否将非 HEIC 图片转换为 HEIC 格式以进一步压缩体积
    /// </summary>
    public bool ConvertToHeic { get; init; } = true;

    /// <summary>
    /// HEIC 压缩质量 (1–100)，默认 90（视觉几乎无损，体积比原始 JPEG 减少约 40%–60%）
    /// </summary>
    public int HeicQuality { get; init; } = 90;

    /// <summary>
    /// 输出目录已存在同名文件时是否直接覆盖；为 <c>false</c> 时自动追加 _1、_2 后缀
    /// </summary>
    public bool Overwrite { get; init; }

    /// <summary>
    /// 并行处理的文件数量
    /// </summary>
    public int Parallelism { get; init; } = MergeOptions.DefaultParallelism;
}

/// <summary>
/// 动态照片瘦身结果汇总
/// </summary>
public sealed record StripReport
{
    /// <summary>
    /// 扫描到的候选文件总数
    /// </summary>
    public required int Total { get; init; }

    /// <summary>
    /// 成功剥离内嵌视频的文件数量
    /// </summary>
    public required int StrippedCount { get; init; }

    /// <summary>
    /// 成功转换为 HEIC 格式的文件数量
    /// </summary>
    public required int ConvertedCount { get; init; }

    /// <summary>
    /// 跳过的文件数量（已是 HEIC 且不含内嵌视频）
    /// </summary>
    public required int Skipped { get; init; }

    /// <summary>
    /// 处理前后总共节省的字节数
    /// </summary>
    public required long SavedBytes { get; init; }

    /// <summary>
    /// 处理失败的文件
    /// </summary>
    public required IReadOnlyList<FailureRecord> Failures { get; init; }
}

/// <summary>
/// 空间瘦身单个文件的只读分析明细
/// </summary>
public sealed record StripAnalysisItem
{
    public StripAnalysisItem() { }

    public StripAnalysisItem(
        string filePath,
        long originalBytes,
        long videoBytes,
        long estimatedHeicBytes,
        bool hasEmbeddedVideo,
        bool needsHeicConversion = false,
        long estimatedFinalBytes = 0)
    {
        _filePath = filePath;
        OriginalBytes = originalBytes;
        VideoBytes = videoBytes;
        EstimatedHeicBytes = estimatedHeicBytes;
        HasEmbeddedVideo = hasEmbeddedVideo;
        NeedsHeicConversion = needsHeicConversion;
        EstimatedFinalBytes = estimatedFinalBytes == 0 ? estimatedHeicBytes : estimatedFinalBytes;
    }

    private readonly string _filePath = string.Empty;

    public string FilePath
    {
        get => _filePath;
        init => _filePath = value;
    }

    public string SourcePath
    {
        get => _filePath;
        init => _filePath = value;
    }

    public long OriginalBytes { get; init; }
    public long VideoBytes { get; init; }
    public long EstimatedHeicBytes { get; init; }
    public bool HasEmbeddedVideo { get; init; }
    public bool NeedsHeicConversion { get; init; }
    public long EstimatedFinalBytes { get; init; }

    public long? EmbeddedVideoBytes => HasEmbeddedVideo ? VideoBytes : null;
    public long EstimatedSavedBytes => Math.Max(0, OriginalBytes - EstimatedFinalBytes);
}

/// <summary>
/// 空间瘦身只读分析报告汇总
/// </summary>
public sealed record StripAnalysisReport
{
    public StripAnalysisReport() { }

    public StripAnalysisReport(
        IReadOnlyList<StripAnalysisItem> items,
        long totalOriginalBytes,
        long totalVideoBytes,
        long totalEstimatedHeicBytes,
        long totalEstimatedSavedBytes)
    {
        Items = items;
        _totalOriginalBytes = totalOriginalBytes;
        _totalVideoBytes = totalVideoBytes;
        _totalEstimatedHeicBytes = totalEstimatedHeicBytes;
        _totalEstimatedSavedBytes = totalEstimatedSavedBytes;
    }

    public IReadOnlyList<StripAnalysisItem> Items { get; init; } = [];

    private readonly long _totalOriginalBytes;
    public long TotalOriginalBytes
    {
        get => _totalOriginalBytes;
        init => _totalOriginalBytes = value;
    }
    public long OriginalTotalBytes
    {
        get => _totalOriginalBytes;
        init => _totalOriginalBytes = value;
    }

    private readonly long _totalVideoBytes;
    public long TotalVideoBytes
    {
        get => _totalVideoBytes;
        init => _totalVideoBytes = value;
    }
    public long VideoTotalBytes
    {
        get => _totalVideoBytes;
        init => _totalVideoBytes = value;
    }

    private readonly long _totalEstimatedHeicBytes;
    public long TotalEstimatedHeicBytes
    {
        get => _totalEstimatedHeicBytes;
        init => _totalEstimatedHeicBytes = value;
    }
    public long EstimatedHeicTotalBytes
    {
        get => _totalEstimatedHeicBytes;
        init => _totalEstimatedHeicBytes = value;
    }

    private readonly long _totalEstimatedSavedBytes;
    public long TotalEstimatedSavedBytes
    {
        get => _totalEstimatedSavedBytes;
        init => _totalEstimatedSavedBytes = value;
    }
    public long EstimatedSavedBytes
    {
        get => _totalEstimatedSavedBytes;
        init => _totalEstimatedSavedBytes = value;
    }
}

