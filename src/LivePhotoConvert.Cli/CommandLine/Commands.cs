using System.ComponentModel;
using LivePhotoConvert.Core.Matching;
using LivePhotoConvert.Core.Models;
using Spectre.Console.Cli;

namespace LivePhotoConvert.Cli.CommandLine;

/// <summary>
/// 全局通用命令行选项设置基类
/// </summary>
public class CommonSettings : CommandSettings
{
    /// <summary>
    /// 是否在检测到外部依赖工具缺失时自动下载安装
    /// </summary>
    [Description("自动下载并安装缺失的外部工具组件 (ExifTool / FFmpeg)")]
    [CommandOption("--auto-download|--download-tools")]
    public bool AutoDownload { get; init; }

    /// <summary>
    /// 用户自定义的 GitHub 镜像加速前缀
    /// </summary>
    [Description("自定义 GitHub 镜像加速前缀（例如 https://ghproxy.net/）")]
    [CommandOption("--mirror|--custom-mirror <URL>")]
    public string? CustomMirror { get; init; }

    /// <summary>
    /// 显式指定的 ExifTool 可执行文件路径
    /// </summary>
    [Description("显式指定 ExifTool 可执行文件的绝对路径")]
    [CommandOption("--exiftool <PATH>")]
    public string? ExifToolPath { get; init; }

    /// <summary>
    /// 显式指定的 FFmpeg 可执行文件路径
    /// </summary>
    [Description("显式指定 FFmpeg 可执行文件的绝对路径")]
    [CommandOption("--ffmpeg <PATH>")]
    public string? FfmpegPath { get; init; }

    /// <summary>
    /// 显式指定的 heif-enc 可执行文件路径
    /// </summary>
    [Description("显式指定 heif-enc 可执行文件的绝对路径")]
    [CommandOption("--heif-enc <PATH>")]
    public string? HeifEncPath { get; init; }

    /// <summary>
    /// 跳过所有前置与删除确认提示
    /// </summary>
    [Description("跳过所有前置与删除确认提示")]
    [CommandOption("-y|--assume-yes|--yes")]
    public bool AssumeYes { get; init; }

    /// <summary>
    /// 最大并发任务数
    /// </summary>
    [Description("最大并发处理任务数（默认根据 CPU 逻辑核心数自动规划）")]
    [CommandOption("-p|--parallel <N>")]
    public int? Parallelism { get; init; }
}

/// <summary>
/// 动态照片合成命令的选项设置
/// </summary>
public sealed class MergeSettings : CommonSettings
{
    /// <summary>
    /// 待扫描合成的输入目录
    /// </summary>
    [Description("待扫描合成的实况照片与视频所在输入目录")]
    [CommandOption("-i|--input <PATH>")]
    public string? Input { get; init; }

    /// <summary>
    /// 动态照片输出存放目录
    /// </summary>
    [Description("合成后 Motion Photo 动态照片的输出存放目录")]
    [CommandOption("-o|--output <PATH>")]
    public string? Output { get; init; }

    /// <summary>
    /// 合成成功后如何处理已匹配的原始文件
    /// </summary>
    [Description("合成成功后如何处理原始文件：Keep (保留，默认)、Move (移至已合成目录)、Recycle (放入回收站)、Delete (永久删除)")]
    [CommandOption("-a|--action|--source-action <ACTION>")]
    public SourceFileAction? SourceAction { get; init; }

    /// <summary>
    /// 目标文件已存在时是否覆盖
    /// </summary>
    [Description("输出目录存在同名文件时直接覆盖，不自动重命名递增序号")]
    [CommandOption("--overwrite")]
    public bool Overwrite { get; init; }

    /// <summary>
    /// 跳过特征校验强制按主干名匹配
    /// </summary>
    [Description("跳过内容特征与时间差校验，强制仅按文件名主干进行宽松配对")]
    [CommandOption("--skip-validation|--no-verify")]
    public bool SkipValidation { get; init; }
}

/// <summary>
/// 动态照片拆分命令的选项设置
/// </summary>
public sealed class SplitSettings : CommonSettings
{
    /// <summary>
    /// 待拆分的动态照片输入目录
    /// </summary>
    [Description("待拆分的动态照片所在输入目录")]
    [CommandOption("-i|--input <PATH>")]
    public string? Input { get; init; }

    /// <summary>
    /// 拆分后的输出存放目录
    /// </summary>
    [Description("拆分后提取的照片与视频输出存放目录")]
    [CommandOption("-o|--output <PATH>")]
    public string? Output { get; init; }

    /// <summary>
    /// 拆分目标格式
    /// </summary>
    [Description("拆分目标格式：Android (标准安卓动态照片解包)、Apple (重构成包含 UUID 的苹果实况照片对)")]
    [CommandOption("-f|--format <FORMAT>")]
    public SplitTargetFormat? Format { get; init; }

    /// <summary>
    /// 目标文件已存在时是否覆盖
    /// </summary>
    [Description("输出目录存在同名文件时直接覆盖")]
    [CommandOption("--overwrite")]
    public bool Overwrite { get; init; }
}

/// <summary>
/// 外部依赖工具管理命令的选项设置
/// </summary>
public sealed class ToolsSettings : CommonSettings
{
}

/// <summary>
/// 合成命令控制器
/// </summary>
public sealed class MergeCommand : AsyncCommand<MergeSettings>
{
    /// <inheritdoc />
    public override async Task<int> ExecuteAsync(CommandContext context, MergeSettings settings)
    {
        var options = new CliOptions
        {
            Command = CliCommand.Merge,
            Input = settings.Input,
            Output = settings.Output,
            SourceAction = settings.SourceAction,
            Overwrite = settings.Overwrite,
            SkipValidation = settings.SkipValidation,
            AutoDownload = settings.AutoDownload,
            CustomMirror = settings.CustomMirror,
            ExifToolPath = settings.ExifToolPath,
            FfmpegPath = settings.FfmpegPath,
            AssumeYes = settings.AssumeYes,
            Parallelism = settings.Parallelism
        };

        return await Program.RunMergeAsync(options, interactive: false, Program.ActiveCancellationToken);
    }
}

/// <summary>
/// 拆分命令控制器
/// </summary>
public sealed class SplitCommand : AsyncCommand<SplitSettings>
{
    /// <inheritdoc />
    public override async Task<int> ExecuteAsync(CommandContext context, SplitSettings settings)
    {
        var options = new CliOptions
        {
            Command = CliCommand.Split,
            Input = settings.Input,
            Output = settings.Output,
            SplitFormat = settings.Format ?? SplitTargetFormat.Android,
            ExplicitSplitFormat = settings.Format.HasValue,
            Overwrite = settings.Overwrite,
            AutoDownload = settings.AutoDownload,
            CustomMirror = settings.CustomMirror,
            ExifToolPath = settings.ExifToolPath,
            FfmpegPath = settings.FfmpegPath,
            AssumeYes = settings.AssumeYes,
            Parallelism = settings.Parallelism
        };

        return await Program.RunSplitAsync(options, interactive: false, Program.ActiveCancellationToken);
    }
}

/// <summary>
/// 外部依赖工具管理命令控制器
/// </summary>
public sealed class ToolsCommand : AsyncCommand<ToolsSettings>
{
    /// <inheritdoc />
    public override async Task<int> ExecuteAsync(CommandContext context, ToolsSettings settings)
    {
        var options = new CliOptions
        {
            Command = CliCommand.DownloadTools,
            AutoDownload = settings.AutoDownload,
            CustomMirror = settings.CustomMirror,
            ExifToolPath = settings.ExifToolPath,
            FfmpegPath = settings.FfmpegPath,
            AssumeYes = settings.AssumeYes
        };

        return await Program.RunDownloadToolsAsync(options, interactive: false, Program.ActiveCancellationToken);
    }
}

/// <summary>
/// 动态照片瘦身命令的选项设置
/// </summary>
public sealed class StripSettings : CommonSettings
{
    /// <summary>
    /// 待处理的照片所在输入目录
    /// </summary>
    [Description("待处理的照片所在输入目录")]
    [CommandOption("-i|--input <PATH>")]
    public string? Input { get; init; }

    /// <summary>
    /// 处理后照片的输出目录（省略则就地修改原文件）
    /// </summary>
    [Description("处理后照片的输出目录（省略则就地修改原文件，⚠ 不可撤销）")]
    [CommandOption("-o|--output <PATH>")]
    public string? Output { get; init; }

    /// <summary>
    /// 跳过 HEIC 转换，仅剥离视频
    /// </summary>
    [Description("跳过 HEIC 转换，仅剥离动态照片中的内嵌视频")]
    [CommandOption("--no-heic")]
    public bool NoHeic { get; init; }

    /// <summary>
    /// HEIC 压缩质量
    /// </summary>
    [Description("HEIC 压缩质量 (1-100，默认 65，越高画质越好体积越大)")]
    [CommandOption("-q|--quality <N>")]
    public int? Quality { get; init; }

    /// <summary>
    /// 目标文件已存在时是否覆盖
    /// </summary>
    [Description("输出目录存在同名文件时直接覆盖")]
    [CommandOption("--overwrite")]
    public bool Overwrite { get; init; }
}

/// <summary>
/// 瘦身命令控制器
/// </summary>
public sealed class StripCommand : AsyncCommand<StripSettings>
{
    /// <inheritdoc />
    public override async Task<int> ExecuteAsync(CommandContext context, StripSettings settings)
    {
        var options = new CliOptions
        {
            Command = CliCommand.Strip,
            Input = settings.Input,
            Output = settings.Output,
            ConvertToHeic = !settings.NoHeic,
            HeicQuality = settings.Quality ?? 65,
            Overwrite = settings.Overwrite,
            AutoDownload = settings.AutoDownload,
            CustomMirror = settings.CustomMirror,
            ExifToolPath = settings.ExifToolPath,
            FfmpegPath = settings.FfmpegPath,
            HeifEncPath = settings.HeifEncPath,
            AssumeYes = settings.AssumeYes,
            Parallelism = settings.Parallelism
        };

        return await Program.RunStripAsync(options, interactive: false, Program.ActiveCancellationToken);
    }
}
