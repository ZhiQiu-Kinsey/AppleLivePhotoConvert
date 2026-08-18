namespace LivePhotoConvert.Cli.CommandLine;

/// <summary>
/// 命令行指定的操作
/// </summary>
enum CliCommand
{
    /// <summary>
    /// 未给出参数，进入交互菜单
    /// </summary>
    Interactive,

    /// <summary>
    /// 合成动态照片
    /// </summary>
    Merge,

    /// <summary>
    /// 拆分动态照片
    /// </summary>
    Split,

    /// <summary>
    /// 显示帮助
    /// </summary>
    Help,

    /// <summary>
    /// 显示版本
    /// </summary>
    Version,

    /// <summary>
    /// 下载并管理外部工具 (ExifTool / FFmpeg)
    /// </summary>
    DownloadTools,

    /// <summary>
    /// 瘦身优化：剥离动态照片视频并转换 HEIC
    /// </summary>
    Strip
}

/// <summary>
/// 命令行参数
/// </summary>
sealed record CliOptions
{
    /// <summary>
    /// 要执行的操作
    /// </summary>
    public CliCommand Command { get; init; }

    /// <summary>
    /// 输入目录
    /// </summary>
    public string? Input { get; init; }

    /// <summary>
    /// 输出目录
    /// </summary>
    public string? Output { get; init; }

    /// <summary>
    /// 合成成功后如何处理已匹配的原始文件，未指定时在交互中询问
    /// </summary>
    public SourceFileAction? SourceAction { get; init; }

    /// <summary>
    /// 目标文件已存在时是否覆盖
    /// </summary>
    public bool Overwrite { get; init; }

    /// <summary>
    /// 跳过配对校验（ContentIdentifier / 拍摄时间 / 视频时长），强制仅按文件名匹配
    /// </summary>
    public bool SkipValidation { get; init; }

    /// <summary>
    /// 是否在检测到工具缺失时自动下载安装（非交互模式下生效）
    /// </summary>
    public bool AutoDownload { get; init; }

    /// <summary>
    /// 用户自定义的 GitHub 镜像加速前缀（例如 https://ghproxy.com/）
    /// </summary>
    public string? CustomMirror { get; init; }

    /// <summary>
    /// 显式指定的 ExifTool 路径
    /// </summary>
    public string? ExifToolPath { get; init; }

    /// <summary>
    /// 显式指定的 FFmpeg 路径
    /// </summary>
    public string? FfmpegPath { get; init; }

    /// <summary>
    /// 所有确认提示一律按 Yes 处理
    /// </summary>
    public bool AssumeYes { get; init; }

    /// <summary>
    /// 最大并行任务数，未指定时采用默认值
    /// </summary>
    public int? Parallelism { get; init; }

    /// <summary>
    /// 拆分输出的目标格式（标准安卓或苹果实况照片）
    /// </summary>
    public SplitTargetFormat SplitFormat { get; init; } = SplitTargetFormat.Android;

    /// <summary>
    /// 显式指定的 heif-enc 路径
    /// </summary>
    public string? HeifEncPath { get; init; }

    /// <summary>
    /// 是否显式指定了拆分格式参数
    /// </summary>
    public bool ExplicitSplitFormat { get; init; }

    /// <summary>
    /// 是否将图片转换为 HEIC 格式（瘦身命令使用）
    /// </summary>
    public bool ConvertToHeic { get; init; } = true;

    /// <summary>
    /// HEIC 压缩质量 (1–100)（瘦身命令使用）
    /// </summary>
    public int HeicQuality { get; init; } = 65;
}
