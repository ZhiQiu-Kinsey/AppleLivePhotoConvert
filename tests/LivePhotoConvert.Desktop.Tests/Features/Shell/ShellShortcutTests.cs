using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using LivePhotoConvert.Desktop.Controls;
using LivePhotoConvert.Desktop.Features.Dialogs;
using LivePhotoConvert.Desktop.Features.Library;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Tests.Features.Library;
using LivePhotoConvert.Desktop.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace LivePhotoConvert.Desktop.Tests.Features.Shell;

/// <summary>在真实外壳中按键：全局快捷键的触发条件、弹窗打开时不生效，以及提示文本与定义一致。</summary>
[Collection(ProcessStateCollection.Name)]
public sealed class ShellShortcutTests : IDisposable
{
    private readonly TestSandbox _album = new();

    public void Dispose() => _album.Dispose();

    [AvaloniaFact]
    public async Task CtrlO_OpensAlbumPickerFromAnyPage_AndCommandKeyIsEquivalent()
    {
        using var session = new ShellSession();
        var picker = session.Host.FilePicker;
        session.Navigate(AppPage.Tasks);

        Press(session, Key.O, RawInputModifiers.Control);
        Assert.Equal([session.Localizer["SelectAlbumFolderBtn"]], picker.Titles);
        Assert.Equal(AppPage.Library, session.Shell.CurrentPage);

        Press(session, Key.O, RawInputModifiers.Meta);
        Assert.Equal(2, picker.Titles.Count);

        SampleAlbum.WriteApplePairs(_album.InputDirectory, 2);
        picker.NextResult = _album.InputDirectory;
        Press(session, Key.O, RawInputModifiers.Control);
        var library = session.Shell.Library;
        await session.WaitUntilAsync(() => library.AllCards.Count == 2);
        Assert.Equal(_album.InputDirectory, library.AlbumDirectory);
        Assert.Equal(_album.InputDirectory, session.Host.Settings.Current.LastScanDirectory);
    }

    [AvaloniaFact]
    public async Task F5_RescansCurrentAlbum_OnlyOnLibraryPage()
    {
        using var session = await OpenAlbumAsync(2);
        var library = session.Shell.Library;
        var scans = library.Catalog.ScanCount;

        SampleAlbum.WriteApplePairs(_album.InputDirectory, 3);
        Press(session, Key.F5);
        await session.WaitUntilAsync(() => library.AllCards.Count == 3);
        Assert.Equal(scans + 1, library.Catalog.ScanCount);

        session.Navigate(AppPage.Settings);
        Press(session, Key.F5);
        Assert.Equal(scans + 1, library.Catalog.ScanCount);
    }

    [AvaloniaFact]
    public async Task Enter_StartsCurrentAction_UnlessFocusNeedsItOrActionCannotRun()
    {
        var tools = new FakeToolAvailability();
        tools.Missing.Add(RequiredTool.ExifTool);
        using var session = await OpenAlbumAsync(2, services => services.AddSingleton<IToolAvailability>(tools));
        var inspector = session.Shell.Inspector;
        var libraryView = session.Descendants<LibraryView>().Single();

        // 焦点在画廊：开始动作，缺少工具时弹出提示即说明已经开始检查
        libraryView.Focus();
        Press(session, Key.Enter);
        await AssertStartedAsync(session);

        // 焦点在输入框：回车属于输入框
        var box = new TextBox();
        ((Panel)libraryView.GetVisualParent()!).Children.Add(box);
        session.Pump();
        box.Focus();
        Press(session, Key.Enter);
        Assert.Null(session.Dialogs.Current);
        ((Panel)libraryView.GetVisualParent()!).Children.Remove(box);

        // 用键盘移到动作磁贴上：回车按下该磁贴；用鼠标点过磁贴后，回车仍然开始动作
        var tile = session.Descendants<InspectorView>().Single().GetVisualDescendants().OfType<Button>()
            .First(b => ReferenceEquals(b.Command, inspector.SetActionCommand) && (string?)b.CommandParameter == "ToAndroid");
        tile.Focus(NavigationMethod.Tab);
        session.Pump();
        Press(session, Key.Enter);
        Assert.Null(session.Dialogs.Current);

        libraryView.Focus();
        session.Click(tile);
        Assert.True(tile.IsFocused);
        Press(session, Key.Enter);
        await AssertStartedAsync(session);

        // 当前动作不可执行（苹果实况对不适用于还原）：不触发
        var probes = tools.Probes;
        inspector.Action = ConversionAction.ToApple;
        session.Pump();
        Assert.False(inspector.StartCommand.CanExecute(null));
        libraryView.Focus();
        Press(session, Key.Enter);
        Assert.Null(session.Dialogs.Current);
        Assert.Equal(probes, tools.Probes);
    }

    [AvaloniaFact]
    public async Task GlobalShortcuts_DoNothingWhileDialogIsOpen_AndEscClosesTheDialogFirst()
    {
        using var session = await OpenAlbumAsync(2);
        var library = session.Shell.Library;
        var scans = library.Catalog.ScanCount;
        var pending = session.Dialogs.ShowAsync(new ConfirmDialogViewModel { Title = "t", Message = "m", ConfirmText = "ok", CancelText = "cancel" });
        session.Pump();

        Press(session, Key.O, RawInputModifiers.Control);
        Press(session, Key.F5);
        Press(session, Key.D2, RawInputModifiers.Control);
        Press(session, Key.A, RawInputModifiers.Control);
        Assert.Empty(session.Host.FilePicker.Titles);
        Assert.Equal(scans, library.Catalog.ScanCount);
        Assert.Equal(AppPage.Library, session.Shell.CurrentPage);
        Assert.Equal(0, library.Selection.SelectedCount);
        Assert.IsType<ConfirmDialogViewModel>(session.Dialogs.Current);

        Press(session, Key.Escape);
        Assert.Null(session.Dialogs.Current);
        Assert.False(await pending);

        Press(session, Key.D2, RawInputModifiers.Control);
        Assert.Equal(AppPage.Tasks, session.Shell.CurrentPage);
    }

    [AvaloniaFact]
    public void CtrlDigits_SwitchPages()
    {
        using var session = new ShellSession();
        (Key Key, AppPage Page)[] steps =
            [(Key.D2, AppPage.Tasks), (Key.D3, AppPage.Tools), (Key.D4, AppPage.Settings), (Key.NumPad1, AppPage.Library)];
        foreach (var (key, page) in steps)
        {
            Press(session, key, RawInputModifiers.Control);
            Assert.Equal(page, session.Shell.CurrentPage);
        }

        // 不带 Ctrl 的数字键不导航
        Press(session, Key.D3);
        Assert.Equal(AppPage.Library, session.Shell.CurrentPage);
    }

    /// <summary>带快捷键的按钮：提示由文案加定义中的按键合成，切换语言后同步更新。</summary>
    [AvaloniaFact]
    public void ShortcutTips_ComposeLabelWithDefinedKey_InBothLanguages()
    {
        using var session = new ShellSession();
        AssertTips(session);
        Assert.Equal($"{session.Localizer["NavTools"]} (Ctrl+3)", Tip(session, AppShortcuts.NavigateTools).Single());
        Assert.Contains($"{session.Localizer["SelectAlbumFolderBtn"]} (Ctrl+O)", Tip(session, AppShortcuts.OpenAlbum));
        // 展开的检查器与收起后的窄条各有一个开始按钮
        var startTips = Tip(session, AppShortcuts.StartAction);
        Assert.Equal(2, startTips.Count);
        Assert.All(startTips, tip => Assert.EndsWith("(Enter)", tip));

        session.Localizer.SetLanguage("en");
        session.Pump();
        AssertTips(session);
        Assert.Equal("Engines (Ctrl+3)", Tip(session, AppShortcuts.NavigateTools).Single());
        UiTexts.AssertNoChinese(session, "快捷键提示（英文）");
        session.Log.AssertNoBindingErrors();
    }

    /// <summary>QuickLook 的按钮提示与底栏说明取自定义，左右方向键切换卡片。</summary>
    [AvaloniaFact]
    public async Task QuickLook_TipsAndFooterUseDefinitions_AndArrowKeysNavigate()
    {
        using var session = await OpenAlbumAsync(3);
        var library = session.Shell.Library;
        var cards = library.Layout.DisplayedCards;
        library.OpenQuickLookCommand.Execute(cards[0]);
        session.Pump();
        var quickLook = Assert.IsType<QuickLookDialogViewModel>(session.Dialogs.Current);
        var dialog = session.Descendants<QuickLookDialog>().Single();

        var loc = session.Localizer;
        var tips = dialog.GetVisualDescendants().OfType<Button>().Select(b => ToolTip.GetTip(b) as string).ToList();
        Assert.Contains($"{loc["QuickLookPrevTip"]} (←)", tips);
        Assert.Contains($"{loc["QuickLookNextTip"]} (→)", tips);
        Assert.Contains($"{loc["DialogCloseTip"]} (Esc)", tips);
        Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == loc.Format("QuickLookShortcutsFormat", "Space", "←", "→", "Esc"));

        dialog.Focus();
        Press(session, Key.Right);
        Assert.Same(cards[1], quickLook.Card);
        Press(session, Key.Left);
        Assert.Same(cards[0], quickLook.Card);
        Press(session, Key.Escape);
        Assert.Null(session.Dialogs.Current);
    }

    /// <summary>工具探测在后台线程，提示随后才出现。</summary>
    private static async Task AssertStartedAsync(ShellSession session)
    {
        await session.WaitUntilAsync(() => session.Dialogs.Current is not null);
        var dialog = Assert.IsType<ConfirmDialogViewModel>(session.Dialogs.Current);
        Assert.Equal(session.Localizer["MissingToolsTitle"], dialog.Title);
        session.PressEscape();
        Assert.Null(session.Dialogs.Current);
    }

    private static void AssertTips(ShellSession session)
    {
        var tipped = session.AllElements().OfType<Control>().Where(c => ShortcutTip.GetShortcut(c) is not null).ToList();
        // 工具栏与空状态的选择目录、开始按钮、四个导航
        Assert.True(tipped.Count >= 7, $"带快捷键提示的控件数量异常：{tipped.Count}");
        foreach (var control in tipped)
        {
            var shortcut = ShortcutTip.GetShortcut(control)!;
            Assert.Contains(shortcut, AppShortcuts.All);
            Assert.False(string.IsNullOrWhiteSpace(ShortcutTip.GetLabel(control)), $"{control.GetType().Name} 的提示没有文案");
            Assert.Equal(ShortcutTip.Compose(ShortcutTip.GetLabel(control), shortcut), ToolTip.GetTip(control));
        }
    }

    private static List<string?> Tip(ShellSession session, AppShortcut shortcut) =>
        [.. session.AllElements().OfType<Control>().Where(c => ReferenceEquals(ShortcutTip.GetShortcut(c), shortcut)).Select(c => ToolTip.GetTip(c) as string)];

    private async Task<ShellSession> OpenAlbumAsync(int pairs, Action<IServiceCollection>? configure = null)
    {
        SampleAlbum.WriteApplePairs(_album.InputDirectory, pairs);
        var session = new ShellSession(configure: configure);
        var library = session.Shell.Library;
        await library.OpenAlbumAsync(_album.InputDirectory);
        await session.WaitUntilAsync(() => library.AllCards.Count == pairs);
        session.Pump();
        return session;
    }

    internal static void Press(ShellSession session, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        var (physical, symbol) = key switch
        {
            Key.O => (PhysicalKey.O, "o"),
            Key.A => (PhysicalKey.A, "a"),
            Key.F5 => (PhysicalKey.F5, null),
            Key.Enter => (PhysicalKey.Enter, null),
            Key.Escape => (PhysicalKey.Escape, null),
            Key.Left => (PhysicalKey.ArrowLeft, null),
            Key.Right => (PhysicalKey.ArrowRight, null),
            Key.D2 => (PhysicalKey.Digit2, "2"),
            Key.D3 => (PhysicalKey.Digit3, "3"),
            Key.D4 => (PhysicalKey.Digit4, "4"),
            Key.NumPad1 => (PhysicalKey.NumPad1, "1"),
            _ => throw new ArgumentOutOfRangeException(nameof(key))
        };
        session.Window.KeyPress(key, modifiers, physical, symbol);
        session.Window.KeyRelease(key, modifiers, physical, symbol);
        session.Pump();
    }
}
