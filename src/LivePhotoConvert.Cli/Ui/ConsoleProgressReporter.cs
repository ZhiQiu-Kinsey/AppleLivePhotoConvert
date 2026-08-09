using LivePhotoConvert.Core.Abstractions;

namespace LivePhotoConvert.Cli.Ui;

/// <summary>
/// 在控制台绘制现代化照片转换进度条（基于 Spectre.Console，具备严格的列宽与字符覆盖清洗机制）
/// </summary>
sealed class ConsoleProgressReporter : IProgressReporter
{
    private readonly Lock _gate = new();
    private readonly bool _plainOutput = Console.IsOutputRedirected;
    private int _lastRenderWidth;
    private bool _hasDrawn;

    /// <inheritdoc />
    public void Report(int completed, int total, string currentItem)
    {
        if (total <= 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_plainOutput)
            {
                AnsiConsole.MarkupLine($"[[{completed}/{total}]] {currentItem.EscapeMarkup()}");
                return;
            }

            var progress = Math.Clamp((double)completed / total, 0, 1);
            var percentage = progress * 100.0;
            var bar = RenderProgressBar(progress, 16);

            // 严格固定文件名显示列宽为 24 个字符（左对齐，不足补空格），杜绝上一个长文件名留下末尾字符残影
            var fixedName = Truncate(currentItem, 24).PadRight(24);

            var line = $"\r  {bar} [bold cyan]{percentage,5:F1}%[/] ([green]{completed,4}[/]/{total}) [grey]{fixedName.EscapeMarkup()}[/]";

            // 预估纯文本显示宽度，若当前行比上一行短则追加额外空格彻底擦除末尾残影
            var currentEstimateWidth = 16 + 8 + 12 + 24 + 4;
            var extraSpaces = Math.Max(4, _lastRenderWidth - currentEstimateWidth + 4);
            _lastRenderWidth = currentEstimateWidth;

            AnsiConsole.Markup(line + new string(' ', extraSpaces));
            _hasDrawn = true;
        }
    }

    /// <summary>
    /// 结束进度显示，换行收尾
    /// </summary>
    public void Complete()
    {
        lock (_gate)
        {
            if (_hasDrawn && !_plainOutput)
            {
                AnsiConsole.WriteLine();
                _hasDrawn = false;
                _lastRenderWidth = 0;
            }
        }
    }

    private static string RenderProgressBar(double progress, int barWidth)
    {
        var filledCount = (int)Math.Round(progress * barWidth);
        filledCount = Math.Clamp(filledCount, 0, barWidth);

        var filled = new string('━', Math.Max(0, filledCount - 1)) + (filledCount > 0 ? "╸" : string.Empty);
        var empty = new string('━', barWidth - filled.Length);
        return $"[green]{filled}[/][grey]{empty}[/]";
    }

    private static string Truncate(string text, int maxLength) => text.Length <= maxLength ? text : $"...{text[^(maxLength - 3)..]}";
}
