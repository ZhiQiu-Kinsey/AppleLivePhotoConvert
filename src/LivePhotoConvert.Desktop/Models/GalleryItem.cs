using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoConvert.Core.Pairing;

namespace LivePhotoConvert.Desktop.Models;

/// <summary>
/// 扁平化相册流的统一抽象项接口
/// </summary>
public interface IGalleryDisplayItem
{
    string Key { get; }
}

/// <summary>
/// 相册时间线聚合标题项
/// </summary>
public sealed partial class TimelineHeaderItemViewModel : ObservableObject, IGalleryDisplayItem
{
    public required string Key { get; init; }
    public required DateTime GroupDate { get; init; }
    public required string Title { get; init; }
    public required string LocationSummary { get; init; }

    [ObservableProperty]
    private int _photoCount;

    [ObservableProperty]
    private bool _isCollapsed;

    [ObservableProperty]
    private bool _isVisible = true;

    public Action<TimelineHeaderItemViewModel>? OnToggleCollapse { get; set; }
    public Action<TimelineHeaderItemViewModel>? OnSelectGroup { get; set; }

    [RelayCommand]
    private void ToggleCollapse()
    {
        IsCollapsed = !IsCollapsed;
        OnToggleCollapse?.Invoke(this);
    }

    [RelayCommand]
    private void SelectGroup()
    {
        OnSelectGroup?.Invoke(this);
    }
}

/// <summary>
/// 虚拟化相册等高行项。每一行按原图比例计算宽度，行内卡片共享同一预览高度。
/// </summary>
public sealed class PhotoGridRowViewModel : ObservableObject, IGalleryDisplayItem
{
    public required string Key { get; init; }
    public ObservableCollection<PhotoCardItemViewModel> Cards { get; init; } = [];

    /// <summary>当前行的自适应预览高度，提前计算后保持 ListBox 测量稳定。</summary>
    public double RowHeight { get; set; } = 220;
}

/// <summary>
/// 单张实况照片展示卡片
/// </summary>
public sealed partial class PhotoCardItemViewModel : ObservableObject, IGalleryDisplayItem
{
    public required string Key { get; init; }
    public required string PhotoPath { get; init; }
    public string? VideoPath { get; set; }
    public MediaPair? Pair { get; init; }

    /// <summary>是否为包含内嵌微视频的单文件安卓/Google 动态照片</summary>
    public bool IsMotionPhoto { get; init; }
    public long EmbeddedVideoOffset { get; init; }
    public long EmbeddedVideoLength { get; init; }

    public string FileName { get; init; } = string.Empty;
    public string FormattedDate { get; init; } = string.Empty;
    public string FormattedTime { get; init; } = string.Empty;

    /// <summary>拍摄/落盘真实时间，用于分组日期与排序，杜绝 DateTime.Now 假数据。</summary>
    public DateTime DateTaken { get; init; }

    public string LocationSummary { get; init; } = string.Empty;
    public string DeviceInfo { get; init; } = string.Empty;

    // 真实像素分辨率：由后台 ThumbnailReader 解码后渐进回填
    [ObservableProperty]
    private string _resolutionText = string.Empty;

    public string DurationText { get; init; } = string.Empty;
    public string PhotoSizeText { get; init; } = string.Empty;
    public string VideoSizeText { get; init; } = string.Empty;
    public string SizeSummary => string.IsNullOrEmpty(VideoSizeText) ? PhotoSizeText : $"{PhotoSizeText} + {VideoSizeText}";
    public string FormatBadgeText
    {
        get
        {
            if (IsMotionPhoto)
            {
                return $"{Path.GetExtension(PhotoPath).TrimStart('.').ToUpperInvariant()}+MP4";
            }
            return string.IsNullOrEmpty(VideoPath)
                ? Path.GetExtension(PhotoPath).TrimStart('.').ToUpperInvariant()
                : $"{Path.GetExtension(PhotoPath).TrimStart('.').ToUpperInvariant()}+{Path.GetExtension(VideoPath).TrimStart('.').ToUpperInvariant()}";
        }
    }
    public string PairingStatusText { get; init; } = string.Empty;

    /// <summary>原图宽高比。扫描阶段可能未知，缩略图解码后会渐进修正。</summary>
    [ObservableProperty]
    private double _aspectRatio = 4.0 / 3.0;

    // 根据卡片宽度和原图比例预留预览高度，缩略图异步到达时不再触发布局跳动。
    [ObservableProperty]
    private double _previewHeight = 200;

    /// <summary>等高行布局中卡片的外部宽度（包含卡片边距）。</summary>
    [ObservableProperty]
    private double _displayWidth = 260;

    [ObservableProperty]
    private bool _isSelected = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNaturalCrop))]
    [NotifyPropertyChangedFor(nameof(IsSquareCrop))]
    private string _cropMode = "Natural";

    public bool IsNaturalCrop => CropMode == "Natural";
    public bool IsSquareCrop => CropMode == "Square";

    [ObservableProperty]
    private bool _isVisible = true;

    [ObservableProperty]
    private bool _isSelectionModeActive;

    [ObservableProperty]
    private bool _isHoverPlaying;

    [ObservableProperty]
    private Bitmap? _thumbnail;

    [ObservableProperty]
    private Bitmap? _displayImage;

    public List<Bitmap>? CachedFrames { get; set; }

    partial void OnThumbnailChanged(Bitmap? value)
    {
        if (value is null)
        {
            if (!IsHoverPlaying)
            {
                DisplayImage = null;
            }
        }
        else if (DisplayImage is null || !IsHoverPlaying)
        {
            DisplayImage = value;
        }
    }

    public bool HasSuspiciousWarning { get; init; }
    public string? WarningReason { get; init; }

    [ObservableProperty]
    private bool _isForceAccepted;

    public Action<PhotoCardItemViewModel>? OnArbitrateRequested { get; set; }
    public Action<PhotoCardItemViewModel>? OnQuickLookRequested { get; set; }
    public Action<PhotoCardItemViewModel>? OnPriorityLoadRequested { get; set; }

    public void RequestPriorityLoad()
    {
        OnPriorityLoadRequested?.Invoke(this);
    }

    [RelayCommand]
    public void ToggleSelect()
    {
        IsSelected = !IsSelected;
    }

    [RelayCommand]
    public void RequestArbitrate()
    {
        OnArbitrateRequested?.Invoke(this);
    }

    [RelayCommand]
    public void RequestQuickLook()
    {
        OnQuickLookRequested?.Invoke(this);
    }
}
