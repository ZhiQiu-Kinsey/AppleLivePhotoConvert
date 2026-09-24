using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using LivePhotoConvert.Desktop.Features.Library;
using LivePhotoConvert.Desktop.Models;

namespace LivePhotoConvert.Desktop.Controls;

public partial class PhotoCardControl : UserControl
{
    public PhotoCardControl()
    {
        InitializeComponent();
    }

    /// <summary>预览区（缩略图与悬浮播放的区域）的显示尺寸。</summary>
    internal Size PreviewSize => PreviewArea.Bounds.Size;

    /// <summary>指针是否落在预览区内；信息栏不触发悬浮播放。</summary>
    internal bool IsInPreview(PointerEventArgs e)
    {
        var point = e.GetPosition(PreviewArea);
        return new Rect(PreviewArea.Bounds.Size).Contains(point);
    }

    /// <summary>当前显示的悬浮播放帧；未播放时为 null。</summary>
    internal Bitmap? PlaybackFrame => PlaybackImage.Source as Bitmap;

    /// <summary>显示播放器的当前帧；传 null 时隐藏，露出缩略图。</summary>
    internal void ShowPlaybackFrame(Bitmap? frame)
    {
        PlaybackImage.Source = frame;
        PlaybackImage.IsVisible = frame is not null;
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
