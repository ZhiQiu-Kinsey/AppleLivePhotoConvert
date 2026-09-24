using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using LivePhotoConvert.Desktop.Controls;
using LivePhotoConvert.Desktop.Features.Library;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Models;
using LivePhotoConvert.Desktop.Tests.Harness;

namespace LivePhotoConvert.Desktop.Tests.Features.Library;

/// <summary>检查器的信息层级与收起：默认窗口下关键结果与主按钮不用滚动即可看到，窄窗口自动收起为窄条。</summary>
[Collection(ProcessStateCollection.Name)]
public sealed class InspectorLayoutTests : IDisposable
{
    private readonly TestSandbox _album = new();

    public void Dispose() => _album.Dispose();

    /// <summary>默认窗口尺寸下，适用项统计、瘦身预估与对比按钮、各动作的主要参数都在首屏，主按钮完整可见。</summary>
    [AvaloniaTheory]
    [InlineData(ConversionAction.ToAndroid, "zh")]
    [InlineData(ConversionAction.ToApple, "zh")]
    [InlineData(ConversionAction.Extract, "zh")]
    [InlineData(ConversionAction.Strip, "zh")]
    [InlineData(ConversionAction.Strip, "en")]
    [InlineData(ConversionAction.ToAndroid, "en")]
    public void DefaultWindow_KeyResultsAndPrimaryButton_AreVisibleWithoutScrolling(ConversionAction action, string language)
    {
        using var session = new ShellSession(language, settings: s =>
        {
            s.Action = action;
            s.StripConvertToHeic = true;
            s.InPlaceStrip = false;
        });
        var view = session.Descendants<InspectorView>().Single();
        var scroll = view.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.IsEffectivelyVisible);
        Assert.Equal(new Size(1280, 820), session.Window.ClientSize);
        Assert.False(session.Shell.Inspector.IsCollapsed);

        string[] keys = action switch
        {
            ConversionAction.ToAndroid => ["NamingFormatTitle", "PreserveHdrTitle", "SourceActionTitle", "InspectorOutputTitle"],
            ConversionAction.ToApple => ["HeicQualityTitle", "SourceActionTitle", "InspectorOutputTitle"],
            ConversionAction.Extract => ["SourceActionTitle", "InspectorOutputTitle"],
            _ => ["SpaceEstimateTitle", "SavedSizeLabel", "StripCompareBtn", "InPlaceStripTitle", "StripConvertHeicTitle", "HeicQualityTitle", "InspectorOutputTitle"]
        };
        foreach (var key in keys)
        {
            var text = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == session.Localizer[key] && t.IsEffectivelyVisible);
            AssertInsideViewport(scroll, text, key);
        }

        var start = view.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, session.Shell.Inspector.StartCommand) && b.IsEffectivelyVisible);
        var bottom = start.TranslatePoint(new Point(0, start.Bounds.Height), session.Window)!.Value.Y;
        Assert.True(bottom <= session.Window.ClientSize.Height, $"主按钮超出窗口：{bottom:F0}");
        session.Log.AssertNoBindingErrors();
    }

    [AvaloniaFact]
    public void CollapseButton_ShowsRail_GalleryWidens_AndChoiceIsRemembered()
    {
        using var session = new ShellSession();
        var inspector = session.Shell.Inspector;
        var view = session.Descendants<InspectorView>().Single();
        var gallery = session.Descendants<LibraryView>().Single();
        var galleryWidth = gallery.Bounds.Width;
        Assert.Equal(320, view.Bounds.Width);

        session.Click(Named(view, "CollapseButton"));

        Assert.True(inspector.IsCollapsed);
        Assert.True(session.Host.Settings.Current.Inspector.IsCollapsed);
        Assert.Equal(52, view.Bounds.Width);
        Assert.Equal(galleryWidth + 320 - 52, gallery.Bounds.Width, 0.5);
        // 窄条上的动作按钮与主按钮仍然可用
        var strip = RailButton(view, inspector.SetActionCommand, nameof(ConversionAction.Strip));
        session.Click(strip);
        Assert.Equal(ConversionAction.Strip, inspector.Action);
        Assert.Contains("active", strip.Classes);
        Assert.Equal(session.Localizer["ActionStrip"], ToolTip.GetTip(strip));
        Screenshots.Save(session, "inspector-rail-light-zh");

        session.Click(Named(view, "ExpandButton"));
        Assert.False(inspector.IsCollapsed);
        Assert.False(session.Host.Settings.Current.Inspector.IsCollapsed);
        Assert.Equal(320, view.Bounds.Width);
        session.Log.AssertNoBindingErrors();
    }

    [AvaloniaFact]
    public void SavedCollapsedChoice_IsRestoredOnStartup()
    {
        using var session = new ShellSession(settings: s => s.Inspector.IsCollapsed = true);
        var view = session.Descendants<InspectorView>().Single();

        Assert.True(session.Shell.Inspector.IsCollapsed);
        Assert.Equal(52, view.Bounds.Width);
        Assert.True(Named(view, "ExpandButton").IsEffectivelyVisible);
        Assert.False(Named(view, "CollapseButton").IsEffectivelyVisible);
    }

    /// <summary>窗口变窄时自动收起、变宽时恢复用户的选择；自动收起不改写保存的选择。画廊随可用宽度重排。</summary>
    [AvaloniaTheory]
    [InlineData("zh")]
    [InlineData("en")]
    public async Task NarrowWindow_AutoCollapses_ManualExpandHolds_AndWideWindowRestoresChoice(string language)
    {
        SampleAlbum.WriteApplePairs(_album.InputDirectory, 6);
        using var session = new ShellSession(language);
        var inspector = session.Shell.Inspector;
        var library = session.Shell.Library;
        library.AlbumDirectory = _album.InputDirectory;
        await library.RefreshAlbumAsync();
        await session.WaitUntilAsync(() => library.AllCards.Count == 6 && library.Layout.ViewportWidth > 0);
        var view = session.Descendants<InspectorView>().Single();

        session.Window.Width = 960;
        session.Pump();
        Assert.True(inspector.IsNarrowLayout);
        Assert.True(inspector.IsCollapsed);
        Assert.False(session.Host.Settings.Current.Inspector.IsCollapsed);
        Assert.Equal(52, view.Bounds.Width);
        // 画廊宽度变化经防抖后触发重排
        var narrowGallery = session.Descendants<LibraryView>().Single().Bounds.Width;
        await session.WaitUntilAsync(() => Math.Abs(library.Layout.ViewportWidth - ExpectedViewport(session)) < 1);
        // 变窄后行数增加，视口外的卡片不会实例化，只要求已显示的卡片都有缩略图
        await session.WaitUntilAsync(() => session.Descendants<PhotoCardControl>().Select(c => c.DataContext).OfType<PhotoCardItemViewModel>().ToList()
            is { Count: > 0 } shown && shown.All(c => c.DisplayImage is not null));
        Screenshots.Save(session, $"inspector-narrow-{language}");

        session.Click(Named(view, "ExpandButton"));
        Assert.False(inspector.IsCollapsed);
        Assert.Equal(320, view.Bounds.Width);
        Assert.True(session.Descendants<LibraryView>().Single().Bounds.Width < narrowGallery);

        session.Window.Width = 1280;
        session.Pump();
        Assert.False(inspector.IsNarrowLayout);
        Assert.False(inspector.IsCollapsed);

        // 宽窗口下手动收起的选择，在窗口变窄再变宽后仍然保留
        session.Click(Named(view, "CollapseButton"));
        session.Window.Width = 960;
        session.Pump();
        session.Window.Width = 1280;
        session.Pump();
        Assert.True(inspector.IsCollapsed);
        Assert.True(session.Host.Settings.Current.Inspector.IsCollapsed);
        session.Log.AssertNoBindingErrors();
    }

    [AvaloniaTheory]
    [InlineData(ConversionAction.ToAndroid)]
    [InlineData(ConversionAction.Strip)]
    public void OutputGroup_TogglesFromHeader_ShowsLocationWhenFolded_AndIsRemembered(ConversionAction action)
    {
        using var session = new ShellSession(settings: s =>
        {
            s.Action = action;
            s.InPlaceStrip = false;
            s.OutputDirectory = _album.OutputDirectory;
            s.StripOutputDirectory = _album.OutputDirectory;
        });
        var inspector = session.Shell.Inspector;
        var view = session.Descendants<InspectorView>().Single();
        var header = Named(view, "OutputGroupHeader");
        var summary = header.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("group-summary"));
        var keepSubfolders = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == session.Localizer["KeepSubfoldersLabel"]);

        Assert.False(inspector.IsOutputExpanded);
        Assert.True(summary.IsEffectivelyVisible);
        Assert.Equal(_album.OutputDirectory, summary.Text);
        Assert.False(keepSubfolders.IsEffectivelyVisible);

        session.Click(header);
        Assert.True(inspector.IsOutputExpanded);
        Assert.True(session.Host.Settings.Current.Inspector.IsOutputExpanded);
        Assert.True(keepSubfolders.IsEffectivelyVisible);
        Assert.False(summary.IsEffectivelyVisible);

        session.Click(header);
        Assert.False(session.Host.Settings.Current.Inspector.IsOutputExpanded);
        session.Log.AssertNoBindingErrors();
    }

    [AvaloniaFact]
    public void Screenshots_InspectorInBothThemesAndLanguages()
    {
        foreach (var theme in new[] { ThemeService.Light, ThemeService.Dark })
        {
            foreach (var language in new[] { "zh", "en" })
            {
                foreach (var action in new[] { ConversionAction.ToAndroid, ConversionAction.Strip })
                {
                    using var session = new ShellSession(language, theme, settings: s => s.Action = action);
                    Screenshots.Save(session, $"inspector-{action.ToString().ToLowerInvariant()}-{theme.ToLowerInvariant()}-{language}");
                    session.Log.AssertNoBindingErrors();
                }
            }
        }
    }

    private static void AssertInsideViewport(ScrollViewer scroll, Control control, string what)
    {
        var top = control.TranslatePoint(default, scroll)!.Value.Y;
        var bottom = top + control.Bounds.Height;
        Assert.True(top >= -0.5 && bottom <= scroll.Viewport.Height + 0.5,
            $"{what} 不在首屏：{top:F0}~{bottom:F0}，可视高度 {scroll.Viewport.Height:F0}");
    }

    /// <summary>画廊列表的实际可用宽度（与 LibraryView 上报给排版的值同源）。</summary>
    private static double ExpectedViewport(ShellSession session)
    {
        var list = session.Descendants<ListBox>().Single(l => l.Name == "GalleryListBox");
        var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        return scroll is { Viewport.Width: > 0 } ? scroll.Viewport.Width : list.Bounds.Width;
    }

    private static Button Named(InspectorView view, string name) =>
        view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == name);

    private static Button RailButton(InspectorView view, System.Windows.Input.ICommand command, string parameter) =>
        view.GetVisualDescendants().OfType<Button>()
            .Single(b => b.Classes.Contains("rail-btn") && ReferenceEquals(b.Command, command) && (string?)b.CommandParameter == parameter);
}
