namespace LivePhotoConvert.Core.Models;

/// <summary>
/// 拆分动态照片的目标格式
/// </summary>
public enum SplitTargetFormat
{
    /// <summary>
    /// 标准安卓格式：提取原生照片与视频（.jpg/.heic + .mp4），无损纯切片
    /// </summary>
    Android,

    /// <summary>
    /// 苹果实况照片格式：转换为 Apple Live Photo 兼容格式（.jpg/.heic + .mov），写入配对 Content Identifier
    /// </summary>
    Apple
}

/// <summary>
/// 拆分动态照片的参数
/// </summary>
public sealed record SplitOptions
{
    /// <summary>
    /// 输入目录，存放待拆分的动态照片
    /// </summary>
    public required string InputDirectory { get; init; }

    /// <summary>
    /// 输出目录，存放拆分出的照片与视频
    /// </summary>
    public required string OutputDirectory { get; init; }

    /// <summary>
    /// 拆分输出的目标格式（标准安卓或苹果实况照片）
    /// </summary>
    public SplitTargetFormat TargetFormat { get; init; } = SplitTargetFormat.Android;

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
/// 拆分结果汇总
/// </summary>
public sealed record SplitReport
{
    /// <summary>
    /// 扫描到的候选文件总数
    /// </summary>
    public required int Total { get; init; }

    /// <summary>
    /// 拆分成功的数量
    /// </summary>
    public required int Succeeded { get; init; }

    /// <summary>
    /// 跳过的数量，通常是不含动态照片标记的普通图片
    /// </summary>
    public required int Skipped { get; init; }

    /// <summary>
    /// 拆分失败的文件
    /// </summary>
    public required IReadOnlyList<FailureRecord> Failures { get; init; }
}
