namespace LivePhotoConvert.Cli.Ui;

/// <summary>
/// 控制台交互与现代化 UI（基于 Spectre.Console，支持默认路径定位与即时响应取消）
/// </summary>
static class ConsoleUi
{
    /// <summary>
    /// 当前是否可以弹出 Windows 文件夹选择对话框
    /// </summary>
    [SupportedOSPlatformGuard("windows")]
    private static bool CanUseFolderDialog => OperatingSystem.IsWindows() && !Console.IsInputRedirected && !Console.IsOutputRedirected;

    /// <summary>
    /// 打印居中或左对齐的美观分割线标题
    /// </summary>
    /// <param name="text">标题文本</param>
    /// <param name="color">边框颜色</param>
    public static void PrintHeader(string text, string color = "cyan")
    {
        AnsiConsole.WriteLine();
        var rule = new Rule($"[bold]{text.EscapeMarkup()}[/]")
        {
            Style = Style.Parse(color)
        };
        AnsiConsole.Write(rule);
        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// 让用户指定一个已存在的目录（支持默认选中初始目录与取消令牌）
    /// </summary>
    /// <param name="message">提示信息</param>
    /// <param name="initialDirectory">默认预选中的初始目录（如已选择的输入目录）</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>目录路径；用户放弃时返回 <c>null</c></returns>
    public static async Task<string?> SelectFolderAsync(string message, string? initialDirectory = null, CancellationToken cancellationToken = default)
    {
        if (CanUseFolderDialog && !cancellationToken.IsCancellationRequested)
        {
            AnsiConsole.MarkupLine($"[cyan]{message.EscapeMarkup()}[/]：即将弹出文件夹选择框……");
            var selected = FolderPicker.Show(message, initialDirectory);
            if (selected is not null && Directory.Exists(selected))
            {
                AnsiConsole.MarkupLine($"[green][[√]] 已选择：[/] {selected.EscapeMarkup()}");
                return selected;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            AnsiConsole.MarkupLine("[yellow]已取消对话框选择，可在下方控制台直接输入或拖拽路径。[/]");
        }

        return await ReadFolderFromConsoleAsync(message, initialDirectory, cancellationToken);
    }

    /// <summary>
    /// 从控制台读取目录路径（支持即时取消）
    /// </summary>
    private static async Task<string?> ReadFolderFromConsoleAsync(string message, string? defaultPath = null, CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var title = string.IsNullOrWhiteSpace(defaultPath)
                ? $"[cyan]{message.EscapeMarkup()}[/] [grey](可直接拖拽文件夹到此处，输入 q 退出):[/]"
                : $"[cyan]{message.EscapeMarkup()}[/] [grey](直接回车使用默认: {defaultPath.EscapeMarkup()}，输入 q 退出):[/]";

            var prompt = new TextPrompt<string>(title).AllowEmpty();

            var input = (await prompt.ShowAsync(AnsiConsole.Console, cancellationToken)).Replace("\"", string.Empty).Trim();

            if (string.IsNullOrEmpty(input) && !string.IsNullOrWhiteSpace(defaultPath) && Directory.Exists(defaultPath))
            {
                return defaultPath;
            }

            if (string.IsNullOrEmpty(input) || string.Equals(input, "q", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (Directory.Exists(input))
            {
                return input;
            }

            AnsiConsole.MarkupLine("[red][[X]] 无效的目录路径，请重新输入。[/]");
        }

        return null;
    }

    /// <summary>
    /// 询问合成成功后如何处理输入目录中【已匹配】的原始文件（支持即时取消）
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>用户选择的处理方式</returns>
    public static async Task<SourceFileAction> AskSourceFileActionAsync(CancellationToken cancellationToken = default)
    {
        var prompt = new SelectionPrompt<SourceFileAction>().Title("[yellow]合成成功后，如何处理输入目录中【已匹配】的原始照片与视频？(使用 ↑/↓ 选择，回车确认)[/]")
                                                            .PageSize(6)
                                                            .UseConverter(action => action switch
                                                            {
                                                                SourceFileAction.Keep => "保留原始文件 (默认，未匹配的文件在任何选项下都不会被处理)",
                                                                SourceFileAction.Move => $"移动到 \"{SourceFileCleaner.MergedFolderName}\" 子文件夹",
                                                                SourceFileAction.Recycle => "删除到回收站 (仅 Windows 系统可用)",
                                                                SourceFileAction.Delete => "永久删除 (不可恢复，需二次确认)",
                                                                _ => action.ToString()
                                                            })
                                                            .AddChoices(
                                                                SourceFileAction.Keep,
                                                                SourceFileAction.Move,
                                                                SourceFileAction.Recycle,
                                                                SourceFileAction.Delete
                                                            );

        var selected = await prompt.ShowAsync(AnsiConsole.Console, cancellationToken);

        switch (selected)
        {
            case SourceFileAction.Recycle when !OperatingSystem.IsWindows():
                AnsiConsole.MarkupLine("[yellow]当前系统不是 Windows，无法使用回收站，已自动降级为：保留原始文件。[/]");
                return SourceFileAction.Keep;
            case SourceFileAction.Delete when !await ConfirmAsync("[bold red]警告：永久删除后文件将无法找回！确认要永久删除吗？[/]", cancellationToken):
                AnsiConsole.MarkupLine("[yellow]未确认永久删除，已自动改为：保留原始文件。[/]");
                return SourceFileAction.Keep;
            default:
                return selected;
        }

    }

    /// <summary>
    /// 询问拆分输出的目标格式（支持即时取消）
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>用户选择的目标格式</returns>
    public static async Task<SplitTargetFormat> AskSplitTargetFormatAsync(CancellationToken cancellationToken = default)
    {
        var prompt = new SelectionPrompt<SplitTargetFormat>()
                     .Title("[yellow]请选择拆分输出的目标格式 (使用 ↑/↓ 选择，回车确认)：[/]")
                     .PageSize(4)
                     .UseConverter(format => format switch
                     {
                         SplitTargetFormat.Android => "1. 标准安卓格式 (.jpg/.heic + .mp4，原生无损提取，适合备份与通用播放)",
                         SplitTargetFormat.Apple => "2. 苹果实况照片 (.jpg/.heic + .mov，写入 Live Photo 元数据，可导入 iOS/Mac 动态播放)",
                         _ => format.ToString()
                     })
                     .AddChoices(
                         SplitTargetFormat.Android,
                         SplitTargetFormat.Apple
                     );

        return await prompt.ShowAsync(AnsiConsole.Console, cancellationToken);
    }

    /// <summary>
    /// 请求用户确认 (Yes/No，支持取消令牌)
    /// </summary>
    /// <param name="message">提示信息</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>是否确认</returns>
    public static async Task<bool> ConfirmAsync(string message, CancellationToken cancellationToken = default)
    {
        var prompt = new ConfirmationPrompt(message) { DefaultValue = false };
        return await prompt.ShowAsync(AnsiConsole.Console, cancellationToken);
    }

    /// <summary>
    /// 请求用户确认 (同步重载兼容)
    /// </summary>
    public static bool Confirm(string message)
    {
        return AnsiConsole.Confirm(message, defaultValue: false);
    }

    /// <summary>
    /// 以指定颜色输出一行文本
    /// </summary>
    /// <param name="message">文本</param>
    /// <param name="color">颜色</param>
    public static void WriteLine(string message, ConsoleColor color)
    {
        var style = color switch
        {
            ConsoleColor.Red => "red",
            ConsoleColor.Yellow => "yellow",
            ConsoleColor.Green => "green",
            ConsoleColor.Cyan => "cyan",
            ConsoleColor.Gray or ConsoleColor.DarkGray => "grey",
            _ => "white"
        };

        AnsiConsole.MarkupLine($"[{style}]{message.EscapeMarkup()}[/]");
    }
}
