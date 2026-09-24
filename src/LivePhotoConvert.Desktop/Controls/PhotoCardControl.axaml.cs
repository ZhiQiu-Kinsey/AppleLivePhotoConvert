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

    /// <summary>
    /// 单击选择（Ctrl 切换、Shift 范围），双击打开大图预览；命令属于画廊，经所在列表的数据上下文取得。
    /// 徽章与按钮区域的点击留给它们自己（悬停提示、人工裁决），不改变选择。
    /// </summary>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var hit = HitTest(e.Source);
        if (e.Handled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed ||
            DataContext is not PhotoCardItemViewModel card || hit == Hit.Chrome ||
            this.FindAncestorOfType<ListBox>()?.DataContext is not LibraryViewModel library)
        {
            return;
        }

        if (e.ClickCount == 2 && hit == Hit.Card)
        {
            library.OpenQuickLookCommand.Execute(card);
        }
        else if (e.ClickCount == 1 || hit == Hit.Check)
        {
            var modifiers = e.KeyModifiers;
            library.ClickCard(card,
                toggle: hit == Hit.Check || modifiers.HasFlag(KeyModifiers.Control) || modifiers.HasFlag(KeyModifiers.Meta),
                range: modifiers.HasFlag(KeyModifiers.Shift));
        }

        // 不交给列表项：行容器的选中态对画廊没有意义，还会抢走焦点
        e.Handled = true;
    }

    /// <summary>勾选角标按复选框处理（只切换这一张）；徽章与按钮不参与选择。</summary>
    private Hit HitTest(object? source)
    {
        for (var element = source as Visual; element is not null && !ReferenceEquals(element, this); element = element.GetVisualParent())
        {
            if (element.Classes.Contains("card-check"))
            {
                return Hit.Check;
            }

            if (element is Button || element.Classes.Contains("card-media-badge") || element.Classes.Contains("card-status"))
            {
                return Hit.Chrome;
            }
        }

        return Hit.Card;
    }

    private enum Hit
    {
        Card,
        Check,
        Chrome,
    }
}
