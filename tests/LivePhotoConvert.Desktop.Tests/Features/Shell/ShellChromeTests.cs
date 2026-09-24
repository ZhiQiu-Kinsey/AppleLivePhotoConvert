using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using FluentIcons.Avalonia;
using LivePhotoConvert.Core.External.Tools;
using LivePhotoConvert.Desktop.Features.Shell;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Tests.Features.Tools;
using LivePhotoConvert.Desktop.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace LivePhotoConvert.Desktop.Tests.Features.Shell;

/// <summary>外壳细节：导航悬停配色、标题栏副标题的自适应、侧栏依赖状态。</summary>
[Collection(ProcessStateCollection.Name)]
public sealed class ShellChromeTests
{
    [AvaloniaTheory]
    [InlineData(ThemeService.Light)]
    [InlineData(ThemeService.Dark)]
    public void NavButtons_PointerOver_SelectedKeepsSelectedColors_OthersUseHoverColor(string theme)
    {
        using var session = new ShellSession(theme: theme);
        var navButtons = session.Descendants<Button>().Where(b => b.Classes.Contains("nav-btn") && b.IsEffectivelyVisible).ToList();
        var selected = navButtons.Single(b => b.Classes.Contains("selected"));
        var other = navButtons.First(b => !b.Classes.Contains("selected"));

        Hover(session, selected);
        Assert.True(selected.IsPointerOver);
        Assert.Equal(Token(session, "AccentSoftBrush"), BackgroundOf(selected));
        Assert.Equal(Token(session, "AccentTextBrush"), ForegroundOf(selected));

        Hover(session, other);
        Assert.True(other.IsPointerOver);
        Assert.Equal(Token(session, "HoverBrush"), BackgroundOf(other));
        Assert.Equal(Token(session, "AccentSoftBrush"), BackgroundOf(selected));

        Screenshots.Save(session, $"shell-nav-hover-{theme.ToLowerInvariant()}");
        session.Log.AssertNoBindingErrors();
    }

    [AvaloniaTheory]
    [InlineData("zh")]
    [InlineData("en")]
    public void TitleSubtitle_ShowsInFullWhenItFits_AndHidesInsteadOfTruncatingWhenNarrow(string language)
    {
        using var session = new ShellSession(language);
        var panel = session.Descendants<FitOrHidePanel>().Single(p => p.Name == "SubtitlePanel");
        var subtitle = session.Descendants<TextBlock>().Single(t => t.Name == "SubtitleText");
        var title = session.Descendants<TextBlock>().First(t => t.Text == session.Localizer["AppTitle"]);
        Assert.Equal(session.Localizer["AppSubtitle"], subtitle.Text);

        Assert.False(panel.IsContentHidden);
        Assert.Equal(TextTrimming.None, subtitle.TextTrimming);
        Assert.True(subtitle.Bounds.Width >= NaturalWidth(subtitle) - 0.5,
            $"副标题被压缩：{subtitle.Bounds.Width:F1} < {NaturalWidth(subtitle):F1}");

        // 窗口本身有最小宽度，这里放开限制来模拟标题栏空间不足
        session.Window.MinWidth = 0;
        session.Window.Width = 380;
        session.Pump();

        Assert.True(panel.IsContentHidden);
        Assert.Equal(0, panel.Opacity);
        Assert.False(panel.IsHitTestVisible);
        Assert.True(title.Bounds.Width >= NaturalWidth(title) - 0.5, "应用标题不应被挤压");
        Screenshots.Save(session, $"shell-titlebar-narrow-{language}");

        session.Window.Width = 1280;
        session.Pump();
        Assert.False(panel.IsContentHidden);
        Assert.Equal(1, panel.Opacity);
        Assert.True(subtitle.Bounds.Width >= NaturalWidth(subtitle) - 0.5);
        session.Log.AssertNoBindingErrors();
    }

    [AvaloniaFact]
    public async Task SidebarToolStatus_MirrorsToolsPage_AndShowsThreeDistinctStates()
    {
        using var session = new ShellSession();
        var shell = session.Shell;
        var tools = shell.Tools;
        // 构造时已在后台探测一次；再完整探测一次并等它结束，避免探测结果覆盖下面手动设置的状态
        await tools.RescanToolsAsync();
        session.Pump();

        Assert.Equal(["ExifTool", "FFmpeg", "heif-enc"], shell.ToolStatuses.Select(s => s.Name));
        bool[] ready = [tools.IsExifToolReady, tools.IsFfmpegReady, tools.IsHeifEncReady];
        Assert.Equal(ready, shell.ToolStatuses.Select(s => s.IsReady));

        foreach (var status in shell.ToolStatuses)
        {
            status.Health = ToolHealth.Ready;
        }
        session.Pump();
        Assert.True(shell.AreToolsReady);
        Assert.Equal(["ok"], VisibleNavDots(session));
        Assert.Equal(["ok", "ok", "ok"], VisibleStatusIcons(session));

        shell.ToolStatuses[1].Health = ToolHealth.Attention;
        session.Pump();
        Assert.True(shell.DoToolsNeedAttention);
        Assert.Equal(["warn"], VisibleNavDots(session));
        Assert.Equal(["ok", "warn", "ok"], VisibleStatusIcons(session));
        Screenshots.Save(session, "shell-sidebar-tools-attention");

        shell.ToolStatuses[2].Health = ToolHealth.Missing;
        session.Pump();
        Assert.True(shell.AreToolsMissing);
        Assert.Equal(["bad"], VisibleNavDots(session));
        Assert.Equal(["ok", "warn", "bad"], VisibleStatusIcons(session));
        session.Log.AssertNoBindingErrors();
    }

    [AvaloniaFact]
    public async Task SidebarToolStatus_FollowsToolCardReadinessAndWarnings()
    {
        var registry = new FakeToolRegistry();
        registry.Infos[ToolId.ExifTool] = FakeToolRegistry.Found(ToolId.ExifTool, "/tools/exiftool", "13.59");
        // FFmpeg 可用但缺 HDR 能力，依赖页卡片显示警告
        registry.Infos[ToolId.Ffmpeg] = FakeToolRegistry.Found(ToolId.Ffmpeg, "/tools/ffmpeg", "7.0", ToolCapabilities.Libx264);
        using var session = new ShellSession(configure: services => services.AddSingleton<IToolRegistry>(registry));
        var shell = session.Shell;
        await shell.Tools.RescanToolsAsync();
        session.Pump();

        Assert.True(shell.Tools.Ffmpeg.HasWarning);
        Assert.Equal([ToolHealth.Ready, ToolHealth.Attention, ToolHealth.Missing], shell.ToolStatuses.Select(s => s.Health));
        Assert.True(shell.AreToolsMissing);
        Assert.Equal(["ok", "warn", "bad"], VisibleStatusIcons(session));
        Screenshots.Save(session, "shell-sidebar-tools-live");

        // 卡片状态变化（例如装好新版本后重新探测）立即反映到侧栏
        registry.Infos[ToolId.Ffmpeg] = FakeToolRegistry.Found(ToolId.Ffmpeg, "/tools/ffmpeg", "8.1.2", ToolCapabilities.Hdr | ToolCapabilities.Libx264);
        registry.Infos[ToolId.HeifEnc] = FakeToolRegistry.Found(ToolId.HeifEnc, "/tools/heif-enc", "1.23.4", recommended: "1.24.0");
        await shell.Tools.RescanToolsAsync();
        session.Pump();

        Assert.Equal([ToolHealth.Ready, ToolHealth.Ready, ToolHealth.Attention], shell.ToolStatuses.Select(s => s.Health));
        Assert.True(shell.DoToolsNeedAttention);
        Assert.Equal(["warn"], VisibleNavDots(session));

        registry.Infos[ToolId.HeifEnc] = FakeToolRegistry.Found(ToolId.HeifEnc, "/tools/heif-enc", "1.24.0");
        await shell.Tools.RescanToolsAsync();
        session.Pump();

        Assert.True(shell.AreToolsReady);
        Assert.Equal(["ok", "ok", "ok"], VisibleStatusIcons(session));
        session.Log.AssertNoBindingErrors();
    }

    /// <summary>启动探测完成前侧栏、导航状态点与依赖页卡片都显示中性的"检测中"，不先闪一下红色"缺失"。</summary>
    [AvaloniaTheory]
    [InlineData("zh")]
    [InlineData("en")]
    public async Task ToolStatus_WhileStartupProbeRuns_IsNeutralChecking_ThenShowsResults(string language)
    {
        var probe = new TaskCompletionSource();
        var registry = new FakeToolRegistry { Gate = probe.Task };
        registry.Infos[ToolId.ExifTool] = FakeToolRegistry.Found(ToolId.ExifTool, "/tools/exiftool", "13.59");
        registry.Infos[ToolId.Ffmpeg] = FakeToolRegistry.Found(ToolId.Ffmpeg, "/tools/ffmpeg", "8.1.2", ToolCapabilities.Hdr | ToolCapabilities.Libx264);
        using var session = new ShellSession(language, configure: services => services.AddSingleton<IToolRegistry>(registry));
        var shell = session.Shell;

        Assert.All(shell.Tools.Cards, card => Assert.True(card.IsProbing));
        Assert.True(shell.AreToolsProbing);
        Assert.False(shell.AreToolsMissing);
        Assert.Equal(["probing"], VisibleNavDots(session));
        Assert.Equal(["probing", "probing", "probing"], VisibleStatusIcons(session));
        Assert.All(ToolIcons(session, "probing"), icon => Assert.Equal(session.Localizer["ShellToolProbing"], ToolTip.GetTip(icon)));
        Screenshots.Save(session, $"shell-sidebar-tools-probing-{language}");

        session.Navigate(AppPage.Tools);
        var cardDots = session.Descendants<Border>().Where(b => b.Classes.Contains("status-dot") && b.Classes.Contains("large")).ToList();
        Assert.Equal(3, cardDots.Count);
        Assert.All(cardDots, dot => Assert.Equal(["large", "probing"], dot.Classes.Where(c => c is "large" or "ok" or "warn" or "bad" or "probing").Order()));
        var pills = session.Descendants<Border>().Where(b => b.Classes.Contains("status-pill")).ToList();
        Assert.All(pills, pill =>
        {
            Assert.Contains("muted", pill.Classes);
            Assert.DoesNotContain("danger", pill.Classes);
            Assert.Equal(session.Localizer["ToolProbing"], pill.GetVisualDescendants().OfType<TextBlock>().Single().Text);
        });
        Screenshots.Save(session, $"tools-probing-{language}");

        probe.SetResult();
        await session.WaitUntilAsync(() => !shell.AreToolsProbing);
        Assert.Equal([ToolHealth.Ready, ToolHealth.Ready, ToolHealth.Missing], shell.ToolStatuses.Select(s => s.Health));
        Assert.Equal(["bad"], VisibleNavDots(session));
        Assert.Equal(["ok", "ok", "bad"], VisibleStatusIcons(session));
        session.Log.AssertNoBindingErrors();
    }

    [Fact]
    public void ToolHealth_MissingOutranksAttention_AndOverallIsTheWorst()
    {
        Assert.Equal(ToolHealth.Missing, ToolStatusItem.Evaluate(isReady: false, needsAttention: true));
        Assert.Equal(ToolHealth.Attention, ToolStatusItem.Evaluate(isReady: true, needsAttention: true));
        Assert.Equal(ToolHealth.Ready, ToolStatusItem.Evaluate(isReady: true, needsAttention: false));

        ToolStatusItem Item(ToolHealth health) => new("x") { Health = health };
        Assert.Equal(ToolHealth.Ready, ToolStatusItem.Worst([Item(ToolHealth.Ready), Item(ToolHealth.Ready)]));
        Assert.Equal(ToolHealth.Attention, ToolStatusItem.Worst([Item(ToolHealth.Ready), Item(ToolHealth.Attention)]));
        Assert.Equal(ToolHealth.Missing, ToolStatusItem.Worst([Item(ToolHealth.Attention), Item(ToolHealth.Missing)]));

        // 没有探测结果时不下结论；已确认的问题优先于"检测中"
        Assert.Equal(ToolHealth.Probing, new ToolStatusItem("x").Health);
        Assert.Equal(ToolHealth.Probing, ToolStatusItem.Evaluate(isProbing: true, isReady: false, needsAttention: false));
        Assert.Equal(ToolHealth.Missing, ToolStatusItem.Evaluate(isProbing: false, isReady: false, needsAttention: false));
        Assert.Equal(ToolHealth.Probing, ToolStatusItem.Worst([Item(ToolHealth.Ready), Item(ToolHealth.Probing)]));
        Assert.Equal(ToolHealth.Attention, ToolStatusItem.Worst([Item(ToolHealth.Probing), Item(ToolHealth.Attention)]));
        Assert.Equal(ToolHealth.Missing, ToolStatusItem.Worst([Item(ToolHealth.Probing), Item(ToolHealth.Missing)]));
    }

    private static void Hover(ShellSession session, Control control)
    {
        session.Pump();
        var center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), session.Window)
            ?? throw new InvalidOperationException("控件不在窗口内。");
        session.Window.MouseMove(center);
        session.Pump();
    }

    private static ContentPresenter Presenter(Button button) =>
        button.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");

    private static Color? BackgroundOf(Button button) => (Presenter(button).Background as ISolidColorBrush)?.Color;

    private static Color? ForegroundOf(Button button) => (Presenter(button).Foreground as ISolidColorBrush)?.Color;

    private static Color? Token(ShellSession session, string key) =>
        session.Window.TryFindResource(key, session.Window.ActualThemeVariant, out var value) && value is ISolidColorBrush brush
            ? brush.Color
            : throw new InvalidOperationException($"找不到画刷 {key}");

    /// <summary>不受可用宽度限制时的文字宽度。</summary>
    private static double NaturalWidth(TextBlock text)
    {
        var probe = new TextBlock { Text = text.Text, FontSize = text.FontSize, FontWeight = text.FontWeight, FontFamily = text.FontFamily };
        probe.Measure(Size.Infinity);
        return probe.DesiredSize.Width;
    }

    private static string[] VisibleNavDots(ShellSession session) =>
    [
        .. session.Descendants<Border>()
            .Where(b => b.Classes.Contains("status-dot") && !b.Classes.Contains("large") && b.IsEffectivelyVisible)
            .Select(b => b.Classes.First(c => c is "ok" or "warn" or "bad" or "probing"))
    ];

    private static string[] VisibleStatusIcons(ShellSession session) =>
    [
        .. session.Descendants<SymbolIcon>()
            .Where(i => i.Classes.Contains("tool-status") && i.IsEffectivelyVisible)
            .Select(i => i.Classes.First(c => c is "ok" or "warn" or "bad" or "probing"))
    ];

    private static IEnumerable<SymbolIcon> ToolIcons(ShellSession session, string state) =>
        session.Descendants<SymbolIcon>().Where(i => i.Classes.Contains("tool-status") && i.Classes.Contains(state) && i.IsEffectivelyVisible);
}
