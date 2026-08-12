using System.Reflection;
using System.Text;
using LivePhotoConvert.Cli.CommandLine;
using LivePhotoConvert.Cli.Ui;
using LivePhotoConvert.Core.Matching;

namespace LivePhotoConvert.Cli;

/// <summary>
/// 程序入口
/// </summary>
public static class Program
{
    private static volatile CancellationTokenSource? _activeCts;

    /// <summary>
    /// 入口方法
    /// </summary>
    /// <param name="args">命令行参数</param>
    /// <returns>退出码</returns>
    public static async Task<int> Main(string[] args)
    {
        ConfigureConsole();

        var parsed = CliParser.Parse(args);
        if (parsed.Error is not null)
        {
            ConsoleUi.WriteLine(parsed.Error, ConsoleColor.Red);
            Console.WriteLine();
            HelpText.Print();
            return ExitCodes.InvalidArguments;
        }

        var options = parsed.Options!;
        using var globalCts = new CancellationTokenSource();
        _activeCts = globalCts;

        Console.CancelKeyPress += (_, eventArgs) =>
        {
            // 拦截 Ctrl+C，通知当前活动任务与 UI 提示立即中断
            eventArgs.Cancel = true;
            try
            {
                _activeCts?.Cancel();
            }
            catch
            {
                // 忽略
            }
        };

        try
        {
            return options.Command switch
            {
                CliCommand.Help => PrintHelp(),
                CliCommand.Version => PrintVersion(),
                CliCommand.DownloadTools => await RunDownloadToolsAsync(options, interactive: false, globalCts.Token),
                CliCommand.Interactive => await RunInteractiveAsync(globalCts.Token),
                CliCommand.Merge => await RunMergeAsync(options, interactive: false, globalCts.Token),
                CliCommand.Split => await RunSplitAsync(options, interactive: false, globalCts.Token),
                _ => ExitCodes.InvalidArguments
            };
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]当前操作已取消。[/]");
            return ExitCodes.Canceled;
        }
        catch (Exception ex)
        {
            var logPath = ErrorLogger.Log(ex, "程序主流程异常");
            AnsiConsole.MarkupLine($"[yellow][[！]] 执行遇到异常，详细信息已记录至日志文件：{logPath.EscapeMarkup()}[/]");
            return ExitCodes.Failure;
        }
    }

    /// <summary>
    /// 初始化控制台
    /// </summary>
    private static void ConfigureConsole()
    {
        try
        {
            // 让含中文、emoji 的文件名也能正确显示
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
            // 输出被重定向时无法设置编码，忽略即可
        }

        try
        {
            if (OperatingSystem.IsWindows() && !Console.IsOutputRedirected)
            {
                Console.Title = "动态照片工具箱";
            }
        }
        catch (Exception)
        {
            // 部分终端不支持设置标题
        }
    }

    /// <summary>
    /// 显示交互菜单（基于 Spectre.Console）
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>退出码</returns>
    private static async Task<int> RunInteractiveAsync(CancellationToken cancellationToken)
    {
        // 首次启动时自动检查外部依赖工具：若已就绪则静默直接进入菜单，若缺失则引导自动下载
        await EnsureToolsOnStartupAsync(new CliOptions { Command = CliCommand.DownloadTools }, cancellationToken);
        if (cancellationToken.IsCancellationRequested)
        {
            return ExitCodes.Canceled;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            ConsoleUi.PrintHeader("欢迎使用动态照片工具箱");

            var prompt = new SelectionPrompt<string>().Title("[yellow]请选择要执行的操作 (使用方向键 ↑/↓ 选择，回车确认)：[/]")
                                                      .PageSize(6)
                                                      .AddChoices(
                                                      [
                                                          "1. 合成动态照片 (苹果实况照片 → 安卓动态照片)",
                                                          "2. 拆分动态照片 (安卓动态照片 → 照片 + 视频)",
                                                          "3. 检查并下载外部依赖工具 (ExifTool 与 FFmpeg)",
                                                          "4. 退出程序"
                                                      ]);

            string choice;
            try
            {
                choice = await prompt.ShowAsync(AnsiConsole.Console, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                AnsiConsole.MarkupLine("[grey]程序退出。[/]");
                return ExitCodes.Success;
            }

            if (choice.StartsWith("4", StringComparison.Ordinal))
            {
                AnsiConsole.MarkupLine("[grey]程序退出。[/]");
                return ExitCodes.Success;
            }

            using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeCts = stepCts;

            try
            {
                if (choice.StartsWith("1", StringComparison.Ordinal))
                {
                    await RunMergeAsync(new CliOptions { Command = CliCommand.Merge }, interactive: true, stepCts.Token);
                }
                else if (choice.StartsWith("2", StringComparison.Ordinal))
                {
                    await RunSplitAsync(new CliOptions { Command = CliCommand.Split }, interactive: true, stepCts.Token);
                }
                else if (choice.StartsWith("3", StringComparison.Ordinal))
                {
                    await RunDownloadToolsAsync(new CliOptions { Command = CliCommand.DownloadTools }, interactive: true, stepCts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                AnsiConsole.MarkupLine("[yellow]当前操作已取消。[/]");
                WaitForReturn(ExitCodes.Canceled, interactive: true);
            }
            finally
            {
                _activeCts = null;
            }
        }

        return ExitCodes.Success;
    }

    /// <summary>
    /// 执行合成
    /// </summary>
    private static async Task<int> RunMergeAsync(CliOptions options, bool interactive, CancellationToken cancellationToken)
    {
        ConsoleUi.PrintHeader("合成动态照片");

        var input = await ResolveInputDirectoryAsync(options.Input, "请选择输入目录", null, cancellationToken);
        if (input is null || cancellationToken.IsCancellationRequested)
        {
            return WaitForReturn(ExitCodes.Canceled, interactive);
        }

        // 输出目录默认高亮并定位到用户选择的输入目录
        var output = await ResolveOutputDirectoryAsync(options.Output, "请选择输出目录", input, cancellationToken);
        if (output is null || cancellationToken.IsCancellationRequested)
        {
            return WaitForReturn(ExitCodes.Canceled, interactive);
        }

        WarnIfSameDirectory(input, output);

        var pairing = MediaPairMatcher.Match(Directory.EnumerateFiles(input, "*", SearchOption.TopDirectoryOnly));
        Console.WriteLine($"匹配到 {pairing.Pairs.Count} 组动态照片。");
        Console.WriteLine($"未匹配的照片 {pairing.UnmatchedPhotoCount} 个，未匹配的视频 {pairing.UnmatchedVideoCount} 个，均不会被合成或清理。");
        if (pairing.SkippedDuplicateCount > 0)
        {
            Console.WriteLine($"另有 {pairing.SkippedDuplicateCount} 个同名备选格式文件未参与合成，也不会被清理。");
        }

        if (pairing.Pairs.Count == 0)
        {
            ConsoleUi.WriteLine("没有可以合成的文件，请确认照片和视频的文件名前缀一致。", ConsoleColor.Yellow);
            return WaitForReturn(ExitCodes.Success, interactive);
        }

        // 命令行没指定处理方式时，交互模式下询问，非交互模式下保守地保留原文件
        var sourceAction = options.SourceAction ?? (interactive ? await ConsoleUi.AskSourceFileActionAsync(cancellationToken) : SourceFileAction.Keep);

        if (cancellationToken.IsCancellationRequested)
        {
            return WaitForReturn(ExitCodes.Canceled, interactive);
        }

        if (!options.AssumeYes && !await ConsoleUi.ConfirmAsync("是否开始转换？", cancellationToken))
        {
            AnsiConsole.MarkupLine("[yellow]转换已取消。[/]");
            return WaitForReturn(ExitCodes.Canceled, interactive);
        }

        var exifTool = await EnsureExifToolAsync(options, interactive, cancellationToken);
        if (exifTool is null || cancellationToken.IsCancellationRequested)
        {
            return WaitForReturn(ExitCodes.Failure, interactive);
        }

        await using var exifToolDisposer = exifTool;

        var videoConverter = await EnsureFfmpegAsync(options, interactive, cancellationToken);
        if (videoConverter is null || cancellationToken.IsCancellationRequested)
        {
            return WaitForReturn(ExitCodes.Failure, interactive);
        }

        var progress = new ConsoleProgressReporter();
        var merger = new MotionPhotoMerger(exifTool, MagickImageConverter.Instance, videoConverter, progress);

        var mergeOptions = new MergeOptions
        {
            InputDirectory = input,
            OutputDirectory = output,
            SourceFileAction = sourceAction,
            Overwrite = options.Overwrite,
            SkipValidation = options.SkipValidation,
            Parallelism = options.Parallelism ?? MergeOptions.DefaultParallelism
        };

        ConsoleUi.PrintHeader("正在合成");
        var report = await merger.MergeAsync(pairing, mergeOptions, cancellationToken);
        progress.Complete();

        Console.WriteLine();
        Console.WriteLine($"成功合成 {report.Succeeded}/{report.Total} 张动态照片。");
        if (report.SkippedItems.Count > 0)
        {
            ConsoleUi.WriteLine($"校验跳过 {report.SkippedItems.Count} 组（可用 --no-verify 关闭校验）。", ConsoleColor.Yellow);
        }
        if (sourceAction != SourceFileAction.Keep)
        {
            Console.WriteLine($"已{DescribeAction(sourceAction)}原始文件 {report.CleanedFileCount} 个。");
        }

        PrintFailures("以下分组因校验不通过而跳过（照片与视频可能不是同一张实况照片）：", report.SkippedItems);
        PrintFailures("以下分组合成失败：", report.Failures);
        PrintFailures("以下原始文件清理失败，请手动处理（动态照片已合成成功，不受影响）：", report.CleanupFailures);

        return WaitForReturn(report.Failures.Count > 0 || report.SkippedItems.Count > 0 ? ExitCodes.PartialFailure : ExitCodes.Success, interactive);
    }

    /// <summary>
    /// 执行拆分
    /// </summary>
    private static async Task<int> RunSplitAsync(CliOptions options, bool interactive, CancellationToken cancellationToken)
    {
        ConsoleUi.PrintHeader("拆分动态照片");

        var input = await ResolveInputDirectoryAsync(options.Input, "请选择输入目录", null, cancellationToken);
        if (input is null || cancellationToken.IsCancellationRequested)
        {
            return WaitForReturn(ExitCodes.Canceled, interactive);
        }

        // 输出目录默认高亮并定位到用户选择的输入目录
        var output = await ResolveOutputDirectoryAsync(options.Output, "请选择输出目录", input, cancellationToken);
        if (output is null || cancellationToken.IsCancellationRequested)
        {
            return WaitForReturn(ExitCodes.Canceled, interactive);
        }

        WarnIfSameDirectory(input, output);

        var candidates = MotionPhotoSplitter.FindCandidates(input);
        Console.WriteLine($"找到 {candidates.Count} 个待检查的图片文件，其中不含动态照片标记的会被跳过。");
        if (candidates.Count == 0)
        {
            ConsoleUi.WriteLine("输入目录中没有 jpg 文件。", ConsoleColor.Yellow);
            return WaitForReturn(ExitCodes.Success, interactive);
        }

        var targetFormat = options.SplitFormat;
        if (interactive && !options.ExplicitSplitFormat)
        {
            targetFormat = await ConsoleUi.AskSplitTargetFormatAsync(cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return WaitForReturn(ExitCodes.Canceled, interactive);
            }
        }

        if (!options.AssumeYes && !await ConsoleUi.ConfirmAsync("是否开始拆分？", cancellationToken))
        {
            AnsiConsole.MarkupLine("[yellow]拆分已取消。[/]");
            return WaitForReturn(ExitCodes.Canceled, interactive);
        }

        var exifTool = await EnsureExifToolAsync(options, interactive, cancellationToken);
        if (exifTool is null || cancellationToken.IsCancellationRequested)
        {
            return WaitForReturn(ExitCodes.Failure, interactive);
        }

        await using var exifToolDisposer = exifTool;

        FfmpegVideoConverter? videoConverter = null;
        if (targetFormat == SplitTargetFormat.Apple)
        {
            videoConverter = await EnsureFfmpegAsync(options, interactive, cancellationToken);
            if (videoConverter is null || cancellationToken.IsCancellationRequested)
            {
                return WaitForReturn(ExitCodes.Failure, interactive);
            }
        }

        var progress = new ConsoleProgressReporter();
        var splitter = new MotionPhotoSplitter(exifTool, videoConverter, progress);

        var splitOptions = new SplitOptions
        {
            InputDirectory = input,
            OutputDirectory = output,
            TargetFormat = targetFormat,
            Overwrite = options.Overwrite,
            Parallelism = options.Parallelism ?? MergeOptions.DefaultParallelism
        };

        ConsoleUi.PrintHeader("正在拆分");
        var report = await splitter.SplitAsync(splitOptions, cancellationToken);
        progress.Complete();

        Console.WriteLine();
        Console.WriteLine($"成功拆分 {report.Succeeded}/{report.Total} 个文件，跳过 {report.Skipped} 个非动态照片。");
        PrintFailures("以下文件拆分失败：", report.Failures);

        return WaitForReturn(report.Failures.Count > 0 ? ExitCodes.PartialFailure : ExitCodes.Success, interactive);
    }

    /// <summary>
    /// 确保首次启动时外部依赖工具就绪；若全部就绪则静默直接进入菜单，若缺失则引导自动下载
    /// </summary>
    private static async Task EnsureToolsOnStartupAsync(CliOptions options, CancellationToken cancellationToken)
    {
        var exifToolPath = ToolLocator.Find(ExifTool.ExecutableName, options.ExifToolPath, "ExifTool", "exiftool", "tools");
        var ffmpegPath = ToolLocator.Find(FfmpegVideoConverter.ExecutableName, options.FfmpegPath, "ffmpeg", "FFmpeg", "bin", "tools");

        if (exifToolPath is not null && ffmpegPath is not null)
        {
            return;
        }

        await RunDownloadToolsAsync(options, interactive: true, cancellationToken, waitOnComplete: false);
    }

    /// <summary>
    /// 检查并下载外部工具
    /// </summary>
    private static async Task<int> RunDownloadToolsAsync(CliOptions options, bool interactive, CancellationToken cancellationToken, bool waitOnComplete = true)
    {
        ConsoleUi.PrintHeader("外部依赖工具检查与下载");

        var exifToolPath = ToolLocator.Find(ExifTool.ExecutableName, options.ExifToolPath, "ExifTool", "exiftool", "tools");
        var ffmpegPath = ToolLocator.Find(FfmpegVideoConverter.ExecutableName, options.FfmpegPath, "ffmpeg", "FFmpeg", "bin", "tools");

        Console.WriteLine($"ExifTool 状态：{(exifToolPath is not null ? $"已就绪 ({exifToolPath})" : "未找到")}");
        Console.WriteLine($"FFmpeg   状态：{(ffmpegPath is not null ? $"已就绪 ({ffmpegPath})" : "未找到")}");
        Console.WriteLine();

        if (exifToolPath is not null && ffmpegPath is not null)
        {
            ConsoleUi.WriteLine("所有外部工具均已就绪，无需重复下载。", ConsoleColor.Green);
            return waitOnComplete ? WaitForReturn(ExitCodes.Success, interactive) : ExitCodes.Success;
        }

        if (!options.AutoDownload && interactive && !await ConsoleUi.ConfirmAsync("是否立即自动下载缺失的工具组件？", cancellationToken))
        {
            AnsiConsole.MarkupLine("[yellow]操作已取消。[/]");
            return waitOnComplete ? WaitForReturn(ExitCodes.Success, interactive) : ExitCodes.Success;
        }

        if (exifToolPath is null && !cancellationToken.IsCancellationRequested)
        {
            await SpectreDownloadRunner.DownloadAsync(
                ExternalToolMetadata.ExifTool,
                options.CustomMirror,
                cancellationToken);
        }

        if (ffmpegPath is null && !cancellationToken.IsCancellationRequested)
        {
            await SpectreDownloadRunner.DownloadAsync(
                ExternalToolMetadata.FFmpeg,
                options.CustomMirror,
                cancellationToken);
        }

        Console.WriteLine();
        ConsoleUi.WriteLine("工具检查与下载流程已结束。", ConsoleColor.Cyan);
        return waitOnComplete ? WaitForReturn(ExitCodes.Success, interactive) : ExitCodes.Success;
    }

    /// <summary>
    /// 确保 ExifTool 可用，未找到时根据配置自动下载或输出中文引导
    /// </summary>
    private static async Task<ExifTool?> EnsureExifToolAsync(CliOptions options, bool interactive, CancellationToken cancellationToken)
    {
        var existingPath = ToolLocator.Find(ExifTool.ExecutableName, options.ExifToolPath, "ExifTool", "exiftool", "tools");
        if (existingPath is not null)
        {
            return ExifTool.Create(existingPath);
        }

        var shouldDownload = options.AutoDownload || (interactive && await ConsoleUi.ConfirmAsync("未检测到 ExifTool 组件，是否立即自动下载安装？", cancellationToken));
        if (shouldDownload && !cancellationToken.IsCancellationRequested)
        {
            var downloaded = await SpectreDownloadRunner.DownloadAsync(
                ExternalToolMetadata.ExifTool,
                options.CustomMirror,
                cancellationToken);

            if (downloaded is not null)
            {
                return ExifTool.Create(downloaded);
            }
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            PrintMissingToolHelp("ExifTool", ExifTool.ExecutableName, ExternalToolMetadata.ExifTool.ManualDownloadHelpUrl);
        }
        return null;
    }

    /// <summary>
    /// 确保 FFmpeg 可用，未找到时根据配置自动下载或输出中文引导
    /// </summary>
    private static async Task<FfmpegVideoConverter?> EnsureFfmpegAsync(CliOptions options, bool interactive, CancellationToken cancellationToken)
    {
        var existingPath = ToolLocator.Find(FfmpegVideoConverter.ExecutableName, options.FfmpegPath, "ffmpeg", "FFmpeg", "bin", "tools");
        if (existingPath is not null)
        {
            return FfmpegVideoConverter.Create(existingPath);
        }

        var shouldDownload = options.AutoDownload || (interactive && await ConsoleUi.ConfirmAsync("未检测到 FFmpeg 视频处理组件，是否立即自动下载安装？", cancellationToken));
        if (shouldDownload && !cancellationToken.IsCancellationRequested)
        {
            var downloaded = await SpectreDownloadRunner.DownloadAsync(
                ExternalToolMetadata.FFmpeg,
                options.CustomMirror,
                cancellationToken);

            if (downloaded is not null)
            {
                return FfmpegVideoConverter.Create(downloaded);
            }
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            PrintMissingToolHelp("FFmpeg", FfmpegVideoConverter.ExecutableName, ExternalToolMetadata.FFmpeg.ManualDownloadHelpUrl);
        }
        return null;
    }

    /// <summary>
    /// 输出友好的工具缺失中文指引
    /// </summary>
    private static void PrintMissingToolHelp(string toolName, string executableName, string helpUrl)
    {
        ConsoleUi.WriteLine($"\n[!] 未找到 {toolName} ({executableName})", ConsoleColor.Red);
        ConsoleUi.WriteLine("解决方案：", ConsoleColor.Yellow);
        ConsoleUi.WriteLine("  1. 自动下载：执行 LivePhotoConvert tools，或在运行命令时添加 --auto-download 选项。", ConsoleColor.Gray);
        ConsoleUi.WriteLine($"  2. 手动放置：从 {helpUrl} 下载并将 {executableName} 放置在以下任一位置：", ConsoleColor.Gray);
        ConsoleUi.WriteLine($"     - 程序根目录：{AppContext.BaseDirectory}", ConsoleColor.DarkGray);
        ConsoleUi.WriteLine($"     - 外部工具目录：{ToolDownloader.GetWritableToolDirectory()}", ConsoleColor.DarkGray);
        ConsoleUi.WriteLine($"     - 或将其所在目录加入系统环境变量 PATH。", ConsoleColor.DarkGray);
        Console.WriteLine();
    }

    /// <summary>
    /// 确定输入目录（支持默认目录定位与即时取消）
    /// </summary>
    private static async Task<string?> ResolveInputDirectoryAsync(string? provided, string message, string? defaultPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(provided))
        {
            var selected = await ConsoleUi.SelectFolderAsync(message, defaultPath, cancellationToken);
            return selected is null ? null : Path.GetFullPath(selected);
        }

        if (Directory.Exists(provided))
        {
            return Path.GetFullPath(provided);
        }

        ConsoleUi.WriteLine($"输入目录不存在：{provided}", ConsoleColor.Red);
        return null;
    }

    /// <summary>
    /// 确定输出目录，命令行指定的目录不存在时创建（支持默认选中输入目录与即时取消）
    /// </summary>
    private static async Task<string?> ResolveOutputDirectoryAsync(string? provided, string message, string? defaultPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(provided))
        {
            var selected = await ConsoleUi.SelectFolderAsync(message, defaultPath, cancellationToken);
            return selected is null ? null : Path.GetFullPath(selected);
        }

        try
        {
            return Directory.CreateDirectory(provided).FullName;
        }
        catch (Exception ex)
        {
            ConsoleUi.WriteLine($"无法创建输出目录 {provided}：{ex.Message}", ConsoleColor.Red);
            return null;
        }
    }

    /// <summary>
    /// 输入输出目录相同时给出提示
    /// </summary>
    private static void WarnIfSameDirectory(string input, string output)
    {
        if (string.Equals(input, output, StringComparison.OrdinalIgnoreCase))
        {
            ConsoleUi.WriteLine("提示：输入和输出是同一个目录，生成的文件会与原始文件混在一起。", ConsoleColor.Yellow);
        }
    }

    /// <summary>
    /// 打印失败清单
    /// </summary>
    private static void PrintFailures(string title, IReadOnlyList<FailureRecord> failures)
    {
        if (failures.Count == 0)
        {
            return;
        }

        ConsoleUi.WriteLine($"{title}（共 {failures.Count} 个）", ConsoleColor.Yellow);
        foreach (var failure in failures)
        {
            Console.WriteLine($"  {failure.Item}：{failure.Message}");
        }
    }

    /// <summary>
    /// 描述原始文件的处理方式
    /// </summary>
    private static string DescribeAction(SourceFileAction action) => action switch
    {
        SourceFileAction.Move => $"移动到 \"{SourceFileCleaner.MergedFolderName}\" 子文件夹",
        SourceFileAction.Recycle => "删除到回收站",
        SourceFileAction.Delete => "永久删除",
        _ => "保留"
    };

    /// <summary>
    /// 在交互模式下等待按键后返回上一级菜单；非交互模式下直接返回退出码
    /// </summary>
    private static int WaitForReturn(int exitCode, bool interactive)
    {
        if (!interactive || Console.IsInputRedirected)
        {
            return exitCode;
        }

        Console.WriteLine();
        Console.WriteLine("按任意键返回上一级菜单……");
        try
        {
            Console.ReadKey(intercept: true);
        }
        catch
        {
            // 部分特殊终端或重定向忽略
        }

        return exitCode;
    }

    /// <summary>
    /// 打印帮助
    /// </summary>
    private static int PrintHelp()
    {
        HelpText.Print();
        return ExitCodes.Success;
    }

    /// <summary>
    /// 打印版本
    /// </summary>
    private static int PrintVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "未知";
        Console.WriteLine($"动态照片工具箱 {version}");
        return ExitCodes.Success;
    }
}

/// <summary>
/// 退出码
/// </summary>
static class ExitCodes
{
    /// <summary>
    /// 全部成功
    /// </summary>
    public const int Success = 0;

    /// <summary>
    /// 运行过程中出错
    /// </summary>
    public const int Failure = 1;

    /// <summary>
    /// 命令行参数有误
    /// </summary>
    public const int InvalidArguments = 2;

    /// <summary>
    /// 用户取消
    /// </summary>
    public const int Canceled = 3;

    /// <summary>
    /// 部分文件处理失败
    /// </summary>
    public const int PartialFailure = 4;
}
