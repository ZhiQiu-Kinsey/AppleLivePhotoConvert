using Avalonia;

namespace LivePhotoConvert.Desktop.Features.Library.Thumbnails;

/// <summary>
/// 画廊的几何常量：排版、视口估算、缩略图分档与 XAML 模板共用一份，避免各处估算互相偏离。
/// </summary>
public static class GalleryMetrics
{
    public const double SmallRowHeight = 180;
    public const double MediumRowHeight = 250;
    public const double LargeRowHeight = 320;

    /// <summary>完整行为铺满宽度可以比目标行高高出的上限；缩略图按这个上限取像素，放大时也不发虚。</summary>
    public const double MaxRowHeightFactor = 1.3;

    /// <summary>卡片底部信息栏高度。</summary>
    public const double InfoBarHeight = 78;

    /// <summary>卡片四周外边距（单侧）。</summary>
    public const double CardMargin = 4;

    /// <summary>同一行相邻卡片的间距。</summary>
    public const double CardSpacing = 8;

    /// <summary>列表项（行、组标题）之间的纵向间距。</summary>
    public const double RowSpacing = 12;

    /// <summary>组标题固定高度，使滚动估算不依赖字体度量。</summary>
    public const double GroupHeaderHeight = 40;

    /// <summary>卡片在预览区之外占用的宽度（左右外边距）。</summary>
    public const double CardHorizontalChrome = CardMargin * 2;

    /// <summary>卡片在预览区之外占用的高度（信息栏 + 上下外边距）。</summary>
    public const double CardVerticalChrome = InfoBarHeight + CardMargin * 2;

    public static Thickness CardMarginThickness { get; } = new(CardMargin);

    public static double TargetRowHeight(string? scaleMode) => scaleMode switch
    {
        "Small" => SmallRowHeight,
        "Large" => LargeRowHeight,
        _ => MediumRowHeight,
    };

    public static double MaxRowHeight(string? scaleMode) => TargetRowHeight(scaleMode) * MaxRowHeightFactor;

    /// <summary>一行卡片在列表中占用的总高度（含行距）。</summary>
    public static double RowExtent(double previewHeight) => previewHeight + CardVerticalChrome + RowSpacing;

    /// <summary>组标题在列表中占用的总高度（含行距）。</summary>
    public const double GroupHeaderExtent = GroupHeaderHeight + RowSpacing;

    /// <summary>列表项右侧留给悬浮滚动条的宽度：滚动条展开时不遮挡组标题右端的计数与行尾卡片。</summary>
    public const double ScrollBarGutter = 14;

    /// <summary>低于此宽度（含卡片边距）的卡片收起实况徽章文字、时长角标与设备信息；配对状态按信息栏实际宽度另行取舍。</summary>
    public const double CompactCardWidth = 230;

    /// <summary>低于此宽度的卡片再隐藏分辨率。</summary>
    public const double TinyCardWidth = 160;

    /// <summary>尚未测量到视口宽度时的排版宽度。</summary>
    public const double DefaultViewportWidth = 900;

    /// <summary>列表项外边距：右侧让出滚动条，底部为行距。</summary>
    public static Thickness ItemMargin { get; } = new(0, 0, ScrollBarGutter, RowSpacing);

    /// <summary>视口宽度中可供一行卡片（含卡片边距与间距）使用的宽度。</summary>
    public static double LayoutWidth(double viewportWidth) => Math.Max(1, viewportWidth - ScrollBarGutter);
}
