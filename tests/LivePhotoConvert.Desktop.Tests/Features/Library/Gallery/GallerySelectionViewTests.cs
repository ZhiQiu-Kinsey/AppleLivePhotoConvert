using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using LivePhotoConvert.Desktop.Controls;
using LivePhotoConvert.Desktop.Features.Dialogs;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Models;
using LivePhotoConvert.Desktop.Tests.Harness;

namespace LivePhotoConvert.Desktop.Tests.Features.Library.Gallery;

/// <summary>真实外壳中用鼠标与键盘操作画廊选择：单击、Ctrl/Shift、徽章区域、Ctrl+A、Esc、空格预览与选中态的可见性。</summary>
[Collection(ProcessStateCollection.Name)]
public sealed class GallerySelectionViewTests : IDisposable
{
    private readonly TestSandbox _album = new();

    public void Dispose() => _album.Dispose();

    [AvaloniaFact]
    public async Task MouseClicks_SelectSingleToggleAndRangeInDisplayOrder()
    {
        using var session = await OpenAsync(8);
        var library = session.Shell.Library;
        var display = library.Layout.DisplayedCards;
        var events = 0;
        library.SelectionChanged += (_, _) => events++;

        ClickCard(session, display[0]);
        Assert.Equal([display[0]], Selected(library));
        Assert.Equal(1, events);
        Assert.Same(display[0], library.FocusedCard);

        ClickCard(session, display[2], RawInputModifiers.Control);
        Assert.Equal([display[0], display[2]], Selected(library));

        ClickCard(session, display[4], RawInputModifiers.Shift);
        Assert.Equal([display[2], display[3], display[4]], Selected(library));
        Assert.Equal(3, events);

        ClickCard(session, display[1]);
        Assert.Equal([display[1]], Selected(library));

        // 显示为选中：描边与勾选角标可见
        var control = CardControl(session, display[1]);
        Assert.True(Ring(control).IsEffectivelyVisible);
        Assert.Equal(1, Check(control).Opacity);
        Assert.False(Ring(CardControl(session, display[2])).IsVisible);
        session.Log.AssertNoBindingErrors();
    }

    /// <summary>配对状态徽章与勾选角标：前者不改变选择，后者按复选框只切换这一张。</summary>
    [AvaloniaFact]
    public async Task BadgeClicks_DoNotSelect_AndCheckMarkTogglesOnlyThatCard()
    {
        using var session = await OpenAsync(4);
        var library = session.Shell.Library;
        var display = library.Layout.DisplayedCards;
        ClickCard(session, display[0]);

        var status = CardControl(session, display[1]).GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("card-status") && b.IsEffectivelyVisible);
        session.Click(status);
        Assert.Equal([display[0]], Selected(library));

        var check = Check(CardControl(session, display[2]));
        session.Click(check);
        Assert.Equal([display[0], display[2]], Selected(library));
    }

    [AvaloniaFact]
    public async Task Keyboard_CtrlASelectsDisplayed_EscClears_SpaceOpensQuickLook()
    {
        using var session = await OpenAsync(6);
        var library = session.Shell.Library;
        var display = library.Layout.DisplayedCards;
        ClickCard(session, display[2]);
        var events = 0;
        library.SelectionChanged += (_, _) => events++;

        Press(session, Key.A, RawInputModifiers.Control);
        Assert.Equal(display.Count, library.Selection.SelectedCount);
        Assert.Equal(1, events);

        Press(session, Key.Escape);
        Assert.Equal(0, library.Selection.SelectedCount);
        Assert.Equal(2, events);

        ClickCard(session, display[4]);
        Press(session, Key.Space);
        var quickLook = Assert.IsType<QuickLookDialogViewModel>(session.Dialogs.Current);
        Assert.Same(display[4], quickLook.Card);

        // 弹窗打开时 Esc 先关闭弹窗，选择保留
        Press(session, Key.Escape);
        Assert.Null(session.Dialogs.Current);
        Assert.Equal([display[4]], Selected(library));

        Press(session, Key.Escape);
        Assert.Empty(Selected(library));

        // 焦点在工具栏按钮上：空格属于按钮，不打开预览
        ClickCard(session, display[1]);
        var pick = session.Descendants<CompactToolbar>().Single().GetVisualDescendants().OfType<Button>()
            .First(b => ReferenceEquals(b.Command, library.SelectAlbumFolderCommand));
        pick.Focus();
        Press(session, Key.Space);
        Assert.Null(session.Dialogs.Current);
        Assert.Single(session.Host.FilePicker.Titles);
    }

    /// <summary>选中描边与角标使用主题 token，两种主题下与卡片底色、画布的对比度都不低于 3:1。</summary>
    [AvaloniaTheory]
    [InlineData(ThemeService.Light)]
    [InlineData(ThemeService.Dark)]
    public async Task SelectionColors_HaveEnoughContrastInBothThemes(string theme)
    {
        using var session = await OpenAsync(2, theme);
        var library = session.Shell.Library;
        var card = library.Layout.DisplayedCards[0];
        ClickCard(session, card);
        var control = CardControl(session, card);

        var ring = Color(Ring(control).BorderBrush);
        var check = Color(Check(control).Background);
        var mark = Color(Check(control).GetVisualDescendants().OfType<FluentIcons.Avalonia.SymbolIcon>().Single().Foreground);
        Assert.Equal(ring, check);
        Assert.True(Contrast(ring, Resource(control, "SurfaceBrush")) >= 3, $"{theme}: 描边与卡片底色对比度不足");
        Assert.True(Contrast(ring, Resource(control, "CanvasBrush")) >= 3, $"{theme}: 描边与画布对比度不足");
        Assert.True(Contrast(mark, check) >= 3, $"{theme}: 勾号与角标底色对比度不足");
    }

    private async Task<ShellSession> OpenAsync(int pairs, string theme = ThemeService.Light)
    {
        SampleAlbum.WriteApplePairs(_album.InputDirectory, pairs);
        var session = new ShellSession(theme: theme);
        var library = session.Shell.Library;
        library.AlbumDirectory = _album.InputDirectory;
        await library.RefreshAlbumAsync();
        await session.WaitUntilAsync(() => library.AllCards.Count == pairs);
        session.Pump();
        Assert.Equal(0, library.Selection.SelectedCount);
        return session;
    }

    private static PhotoCardItemViewModel[] Selected(Desktop.Features.Library.LibraryViewModel library) =>
        [.. library.Layout.DisplayedCards.Where(c => c.IsSelected)];

    private static PhotoCardControl CardControl(ShellSession session, PhotoCardItemViewModel card) =>
        session.Descendants<PhotoCardControl>().Where(c => ReferenceEquals(c.DataContext, card)).ToList() is [var single] ? single : throw new InvalidOperationException(
            $"{card.FileName}: 滚动 {session.Descendants<ListBox>().Single(l => l.Name == "GalleryListBox").GetVisualDescendants().OfType<ScrollViewer>().First().Offset} 控件 " +
            string.Join(",", session.Descendants<PhotoCardControl>().Select(c => (c.DataContext as PhotoCardItemViewModel)?.FileName + "@" + c.TranslatePoint(default, session.Window))));

    private static Border Ring(PhotoCardControl control) =>
        control.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("card-selection-ring"));

    private static Border Check(PhotoCardControl control) =>
        control.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("card-check"));

    /// <summary>点在预览区中部，避开四角的徽章与角标。</summary>
    private static void ClickCard(ShellSession session, PhotoCardItemViewModel card, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        session.Pump();
        var control = CardControl(session, card);
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, card.PreviewHeight / 2), session.Window)!.Value;
        session.Window.MouseMove(point, modifiers);
        session.Window.MouseDown(point, MouseButton.Left, modifiers);
        session.Window.MouseUp(point, MouseButton.Left, modifiers);
        // 移开指针：下一次点击不同卡片时不会被合并为双击
        session.Window.MouseMove(new Point(1, 1));
        session.Pump();
    }

    private static void Press(ShellSession session, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        var physical = key switch
        {
            Key.A => PhysicalKey.A,
            Key.Space => PhysicalKey.Space,
            _ => PhysicalKey.Escape
        };
        session.Window.KeyPress(key, modifiers, physical, key == Key.A ? "a" : null);
        session.Window.KeyRelease(key, modifiers, physical, key == Key.A ? "a" : null);
        session.Pump();
    }

    private static Color Color(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    private static Color Resource(Control control, string key)
    {
        Assert.True(control.TryFindResource(key, control.ActualThemeVariant, out var value));
        return Color(value as IBrush);
    }

    private static double Contrast(Color a, Color b)
    {
        static double Luminance(Color c)
        {
            static double Channel(byte v)
            {
                var s = v / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }

            return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        }

        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }
}
