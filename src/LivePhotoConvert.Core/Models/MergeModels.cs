namespace LivePhotoConvert.Core.Models;

/// <summary>
/// 合成动态照片的参数
/// </summary>
public sealed record MergeOptions
{
    /// <summary>
    /// 输入目录，存放成对的照片与视频
    /// </summary>
    public required string InputDirectory { get; init; }

    /// <summary>
    /// 输出目录，存放合成后的动态照片
    /// </summary>
    public required string OutputDirectory { get; init; }

    /// <summary>
    /// 合成成功后如何处理【已匹配】的原始文件
    /// </summary>
    public SourceFileAction SourceFileAction { get; init; } = SourceFileAction.Keep;

    /// <summary>
    /// 输出目录已存在同名文件时是否直接覆盖；为 <c>false</c> 时自动追加 _1、_2 后缀
    /// </summary>
    public bool Overwrite { get; init; }

    /// <summary>
    /// 是否额外用苹果的 Content Identifier 校验照片与视频确实来自同一张实况照片
    /// </summary>
    /// <remarks>更严格但更慢，每组需要多读两次元数据。</remarks>
    public bool StrictPairing { get; init; }

    /// <summary>
    /// 并行处理的分组数量
    /// </summary>
    public int Parallelism { get; init; } = DefaultParallelism;

    /// <summary>
    /// 默认并行度，取 CPU 核心数的一半并限制在 1~4 之间，避免解码与转码同时抢占过多内存
    /// </summary>
    public static int DefaultParallelism => Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
}

/// <summary>
/// 单个文件处理失败的记录
/// </summary>
/// <param name="Item">出错的文件或分组名称</param>
/// <param name="Message">失败原因</param>
public sealed record FailureRecord(string Item, string Message);

/// <summary>
/// 合成结果汇总
/// </summary>
public sealed record MergeReport
{
    /// <summary>
    /// 匹配到的分组总数
    /// </summary>
    public required int Total { get; init; }

    /// <summary>
    /// 合成成功的数量
    /// </summary>
    public required int Succeeded { get; init; }

    /// <summary>
    /// 按所选方式成功清理的原始文件数量
    /// </summary>
    public required int CleanedFileCount { get; init; }

    /// <summary>
    /// 合成失败的分组
    /// </summary>
    public required IReadOnlyList<FailureRecord> Failures { get; init; }

    /// <summary>
    /// 清理失败的原始文件，动态照片本身已合成成功，不受影响
    /// </summary>
    public required IReadOnlyList<FailureRecord> CleanupFailures { get; init; }

    /// <summary>
    /// 合成失败的数量
    /// </summary>
    public int Failed => Failures.Count;
}
