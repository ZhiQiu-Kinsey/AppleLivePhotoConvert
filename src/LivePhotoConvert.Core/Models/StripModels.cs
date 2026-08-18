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
    /// HEIC 压缩质量 (1–100)，默认 65（画质接近无损，体积比原始 JPEG 减少约 50%–70%）
    /// </summary>
    public int HeicQuality { get; init; } = 65;

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
