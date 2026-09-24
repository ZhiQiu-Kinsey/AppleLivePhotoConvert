using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using LivePhotoConvert.Desktop.Features.Playback;
using LivePhotoConvert.Desktop.Infrastructure;

namespace LivePhotoConvert.Desktop.Features.Dialogs;

public partial class QuickLookDialog : UserControl
{
    public QuickLookDialog()
    {
        InitializeComponent();
        MediaCanvas.SizeChanged += (_, _) => ReportViewport();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        AttachPlayer();
        Focus();
    }

    // 数据上下文可能晚于 Loaded 才到达，两处都尝试；重复调用无副作用
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (IsLoaded)
        {
            AttachPlayer();
        }
    }

    private void AttachPlayer()
    {
        ReportViewport();
        if (DataContext is QuickLookDialogViewModel vm && TopLevel.GetTopLevel(this) is { } top)
        {
            vm.AttachPlayer(new TopLevelFrameScheduler(top));
        }
    }

    /// <summary>预览按画布实际显示的物理像素加载。</summary>
    private void ReportViewport()
    {
        if (DataContext is QuickLookDialogViewModel vm && TopLevel.GetTopLevel(this) is { } top)
        {
            vm.SetViewport(MediaCanvas.Bounds.Width, MediaCanvas.Bounds.Height, top.RenderScaling);
        }
    }

    // Esc 由主窗口统一处理，这里只负责播放与切换
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || DataContext is not QuickLookDialogViewModel vm)
        {
            return;
        }

        if (AppShortcuts.TogglePlay.Matches(e))
        {
            vm.TogglePlayCommand.Execute(null);
            e.Handled = true;
        }
        else if (AppShortcuts.Previous.Matches(e))
        {
            vm.PrevItemCommand.Execute(null);
            e.Handled = true;
        }
        else if (AppShortcuts.Next.Matches(e))
        {
            vm.NextItemCommand.Execute(null);
            e.Handled = true;
        }
    }
}
