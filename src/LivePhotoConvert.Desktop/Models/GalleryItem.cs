using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoConvert.Core.Media;
using LivePhotoConvert.Desktop.Converters;
using LivePhotoConvert.Desktop.Features.Library.Thumbnails;
using LivePhotoConvert.Desktop.Infrastructure;

namespace LivePhotoConvert.Desktop.Models;

/// <summary>画廊列表中的一项（组标题或一行卡片）。</summary>
public interface IGalleryDisplayItem
{
    string Key { get; }
}

/// <summary>
/// 分组标题。标题文案随语言切换重新格式化，折叠状态按分组键跨重排保留。
/// </summary>
public sealed partial class TimelineHeaderItemViewModel(string key, DateTime period) : ObservableObject, IGalleryDisplayItem
{
    public string Key { get; } = key;

    /// <summary>分组所代表的时段起点（日、月或年的第一天）。</summary>
    public DateTime Period { get; } = period;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _locationSummary = string.Empty;

    [ObservableProperty]
    private int _photoCount;

    [ObservableProperty]
    private bool _isCollapsed;
}

/// <summary>
/// 等高行。行对象跨重排复用：宽度变化只改高度与卡片尺寸，行组成变化时原地替换卡片。
/// </summary>
public sealed partial class PhotoGridRowViewModel(string key) : ObservableObject, IGalleryDisplayItem
{
    public string Key { get; } = key;

    public ObservableCollection<PhotoCardItemViewModel> Cards { get; } = [];

    /// <summary>行内卡片预览区的统一高度。</summary>
    [ObservableProperty]
    private double _rowHeight;
}

/// <summary>
/// 画廊卡片：包装一次扫描产出的 <see cref="LibraryItem"/>。文件信息全部来自扫描结果，界面线程不访问磁盘；
/// 日期与标签等文案在读取时按当前语言格式化。
/// </summary>
public sealed partial class PhotoCardItemViewModel : ObservableObject, IGalleryDisplayItem
{
    private static readonly string[] ItemDerivedProperties =
    [
        nameof(Kind), nameof(IsMotionPhoto), nameof(IsApplePair), nameof(Video), nameof(VideoPath),
        nameof(PhotoHeader), nameof(AspectRatio), nameof(ResolutionText), nameof(DateTaken), nameof(FormattedDate),
        nameof(FormattedTime), nameof(DeviceInfo), nameof(PhotoSizeText), nameof(VideoSizeText), nameof(SizeSummary),
        nameof(RequiresPairReview), nameof(WarningReason)
    ];

    private static readonly string[] LocalizedProperties =
        [nameof(FormattedDate), nameof(FormattedTime), nameof(LocationSummary), nameof(DeviceInfo), nameof(WarningReason)];

    private readonly ILocalizer _localizer;

    public PhotoCardItemViewModel(LibraryItem item, ILocalizer localizer)
    {
        _item = item;
        _localizer = localizer;
    }

    /// <summary>扫描结果；后台补全（如 HEIC 升级为动态照片）时整体替换。</summary>
    [ObservableProperty]
    private LibraryItem _item;

    partial void OnItemChanged(LibraryItem value)
    {
        foreach (var name in ItemDerivedProperties)
        {
            OnPropertyChanged(name);
        }
    }

    public string Key => Item.Photo.Path;

    public string PhotoPath => Item.Photo.Path;

    public LibraryFile PhotoFile => Item.Photo;

    public ImageHeader? PhotoHeader => Item.Header;

    public LibraryItemKind Kind => Item.Kind;

    public bool IsMotionPhoto => Item.Kind == LibraryItemKind.MotionPhoto;

    public bool IsApplePair => Item.Kind == LibraryItemKind.ApplePair;

    /// <summary>视频数据位置：实况对为整段视频文件，动态照片为照片内的区段。</summary>
    public VideoSource? Video => Item.VideoSource;

    /// <summary>
    /// 实况对的独立视频路径，仅供旧播放器使用（阶段 3 删除）。动态照片为 null：播放时切出的临时文件不属于图库条目。
    /// </summary>
    public string? VideoPath => IsApplePair ? Item.Video?.Path : null;

    public string FileName => Path.GetFileNameWithoutExtension(PhotoPath);

    /// <summary>拍摄时间（当地时间），来源见 <see cref="LibraryItem.CaptureTimeSource"/>。</summary>
    public DateTime DateTaken => Item.CaptureTimeLocal;

    public string FormattedDate => DateTaken.ToString(_localizer["DateGroupFormat"], _localizer.Culture);

    public string FormattedTime => DateTaken.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public string LocationSummary
    {
        get
        {
            var directory = Path.GetFileName(Path.GetDirectoryName(PhotoPath));
            return string.IsNullOrWhiteSpace(directory) ? _localizer["LocalAlbumFallback"] : directory;
        }
    }

    public string DeviceInfo => IsMotionPhoto ? _localizer["CardMotionPhotoLabel"] : Extension(PhotoPath);

    /// <summary>转正后的原图宽高比，由扫描确定；缩略图到达不改变它。</summary>
    public double AspectRatio => Item.Header is { Width: > 0, Height: > 0 } header ? header.AspectRatio : 4.0 / 3.0;

    public string ResolutionText => Item.Header is { Width: > 0, Height: > 0 } header ? $"{header.Width}×{header.Height}" : string.Empty;

    public string PhotoSizeText => FormatBytes(Item.Embedded is { } embedded ? embedded.ImageEnd : Item.Photo.Length);

    public string VideoSizeText => Item switch
    {
        { Embedded: { } embedded, Kind: LibraryItemKind.MotionPhoto } => FormatBytes(embedded.Length),
        { Video: { } video, Kind: LibraryItemKind.ApplePair } => FormatBytes(video.Length),
        _ => string.Empty
    };

    public string SizeSummary => string.IsNullOrEmpty(VideoSizeText) ? PhotoSizeText : $"{PhotoSizeText} + {VideoSizeText}";

    /// <summary>配对未通过校验且尚未人工确认：合成前需要裁决。</summary>
    public bool RequiresPairReview => Item.RequiresPairReview && !IsForceAccepted;

    public string WarningReason => !RequiresPairReview
        ? string.Empty
        : Item.PairTimeDelta is { } delta
            ? _localizer.Format("TimeDiffWarningFormat", delta.TotalSeconds)
            : _localizer["CardTimeMismatch"];

    /// <summary>用户人工确认了这组配对，合成时跳过校验。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RequiresPairReview))]
    [NotifyPropertyChangedFor(nameof(WarningReason))]
    private bool _isForceAccepted;

    // 行布局预先算好尺寸，缩略图异步到达时不改变测量
    [ObservableProperty]
    private double _previewHeight = 200;

    /// <summary>等高行中卡片的外部宽度（含卡片边距）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCompact), nameof(IsTiny))]
    private double _displayWidth = 260;

    /// <summary>卡片较窄：状态徽章只显示图标、隐藏设备信息，让文件名保持可读。</summary>
    public bool IsCompact => DisplayWidth < GalleryMetrics.CompactCardWidth;

    /// <summary>卡片很窄：再隐藏分辨率。</summary>
    public bool IsTiny => DisplayWidth < GalleryMetrics.TinyCardWidth;

    /// <summary>默认不选中：未选中任何卡片时动作按全部就绪的卡片统计。</summary>
    [ObservableProperty]
    private bool _isSelected;

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

    [RelayCommand]
    public void ToggleSelect() => IsSelected = !IsSelected;

    /// <summary>语言切换后重新读取格式化文案。</summary>
    public void RefreshLocalizedTexts()
    {
        foreach (var name in LocalizedProperties)
        {
            OnPropertyChanged(name);
        }
    }

    private static string Extension(string path) => Path.GetExtension(path).TrimStart('.').ToUpperInvariant();

    private static string FormatBytes(long bytes) =>
        ByteSizeConverter.Instance.Convert(bytes, typeof(string), null, CultureInfo.InvariantCulture) as string ?? $"{bytes} B";
}
