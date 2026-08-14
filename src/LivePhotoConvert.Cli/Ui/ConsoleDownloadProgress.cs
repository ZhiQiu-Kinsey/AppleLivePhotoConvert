namespace LivePhotoConvert.Cli.Ui;

/// <summary>
/// 基于 Spectre.Console 的现代化流式下载与进度条运行器
/// </summary>
static class SpectreDownloadRunner
{
    /// <summary>
    /// 运行带专业进度条与速率显示的下载任务
    /// </summary>
    /// <param name="tool">工具下载元数据</param>
    /// <param name="customMirror">用户指定的镜像代理前缀</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>生成的可执行文件完整路径；若取消或失败则返回 <c>null</c></returns>
    public static async Task<string?> DownloadAsync(ToolDownloadInfo tool,string? customMirror = null,CancellationToken cancellationToken = default)
    {
        try
        {
            return await AnsiConsole.Progress()
                                    .AutoClear(false)
                                    .HideCompleted(false)
                                    .Columns(
                                    [
                                        new TaskDescriptionColumn { Alignment = Justify.Left },
                                        new ProgressBarColumn(),
                                        new PercentageColumn(),
                                        new RemainingTimeColumn(),
                                        new DownloadedColumn(),
                                        new SpinnerColumn(Spinner.Known.Dots)
                                    ])
                                    .StartAsync(async ctx =>
                                    {
                                        var task = ctx.AddTask($"[cyan]准备下载 {tool.ToolName}...[/]", maxValue: 100);
                                        task.IsIndeterminate = true;

                                        var progressReporter = new Progress<DownloadProgressReport>(report =>
                                        {
                                            task.Description = $"[cyan]正在从 {report.SourceName} 下载 {tool.ToolName}[/]";
                                            if (report.TotalBytes is > 0)
                                            {
                                                task.IsIndeterminate = false;
                                                task.MaxValue = report.TotalBytes.Value;
                                            }
                                            else
                                            {
                                                task.IsIndeterminate = true;
                                            }
                                            task.Value = report.DownloadedBytes;
                                        });

                                        var downloadedPath = await ToolDownloader.DownloadAndExtractAsync(
                                            tool,
                                            customMirror,
                                            progressReporter,
                                            onSourceSwitch: (source, error) =>
                                            {
                                                if (error is not null && !cancellationToken.IsCancellationRequested)
                                                {
                                                    AnsiConsole.MarkupLine($"[grey]备用源自动切换：{source.Name.EscapeMarkup()}[/]");
                                                }
                                            },
                                            cancellationToken);

                                        task.IsIndeterminate = false;
                                        task.Description = $"[green]{tool.ToolName} 下载并提取成功！[/]";
                                        if (task.MaxValue > 0)
                                        {
                                            task.Value = task.MaxValue;
                                        }

                                        AnsiConsole.MarkupLine($"[green][[√]] {tool.ToolName} 已就绪：{downloadedPath.EscapeMarkup()}[/]");
                                        return downloadedPath;
                                    });
        }
        catch (OperationCanceledException)
        {
            // 用户主动取消时静默优雅返回，不输出红字报错
            return null;
        }
        catch (Exception ex)
        {
            var logPath = ErrorLogger.Log(ex, $"{tool.ToolName} 自动下载未完成");
            AnsiConsole.MarkupLine($"[yellow][[！]] {tool.ToolName} 下载未能完成，详细原因已写入日志：{logPath.EscapeMarkup()}[/]");
            return null;
        }
    }
}
