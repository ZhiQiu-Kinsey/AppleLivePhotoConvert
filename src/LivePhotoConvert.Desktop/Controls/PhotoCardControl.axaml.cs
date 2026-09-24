using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using LivePhotoConvert.Desktop.Features.Library;
using LivePhotoConvert.Desktop.Models;
using LivePhotoConvert.Desktop.Services;

namespace LivePhotoConvert.Desktop.Controls;

public partial class PhotoCardControl : UserControl
{
    public PhotoCardControl()
    {
        InitializeComponent();
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        if (DataContext is PhotoCardItemViewModel card)
        {
            PlaybackHost.Instance.OnPointerEnter(card);
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (DataContext is PhotoCardItemViewModel card)
        {
            PlaybackHost.Instance.OnPointerLeave(card);
        }
    }

    /// <summary>双击打开大图预览；命令属于画廊（与标题栏按钮相同，经所在列表的数据上下文取得）。</summary>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.ClickCount == 2 && DataContext is PhotoCardItemViewModel card &&
            this.FindAncestorOfType<ListBox>()?.DataContext is LibraryViewModel library)
        {
            library.OpenQuickLookCommand.Execute(card);
            e.Handled = true;
        }
    }
}
