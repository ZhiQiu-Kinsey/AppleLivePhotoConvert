using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using ImageMagick;
using LivePhotoConvert.Core.Services;
using LivePhotoConvert.Desktop.Infrastructure;

namespace LivePhotoConvert.Desktop.Features.Dialogs;

/// <summary>
/// 瘦身前后的卷帘对比：原片与按当前 HEIC 质量重新编码后的画面。关闭时释放两张位图。
/// </summary>
public sealed partial class StripCompareDialogViewModel : DialogViewModel<bool>
{
    private const int PreviewMaxSize = 1600;

    private readonly ILocalizer _localizer;

    public StripCompareDialogViewModel(ILocalizer localizer, string photoPath, int heicQuality)
    {
        _localizer = localizer;
        PhotoPath = photoPath;
        FileName = Path.GetFileName(photoPath);
        HeicQuality = heicQuality > 0 ? heicQuality : ConversionDefaults.HeicQuality;
        LoadTask = LoadComparisonAsync();
    }

    public string PhotoPath { get; }

    public string FileName { get; }

    public int HeicQuality { get; }

    /// <summary>对比图生成任务；测试据此等待。</summary>
    internal Task LoadTask { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurtainPixelWidth))]
    [NotifyPropertyChangedFor(nameof(DividerMargin))]
    [NotifyPropertyChangedFor(nameof(DividerHeight))]
    [NotifyPropertyChangedFor(nameof(ThumbMargin))]
    [NotifyPropertyChangedFor(nameof(CurtainPercentageText))]
    [NotifyPropertyChangedFor(nameof(OverlayClipGeometry))]
    private double _curtainPosition = 50.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurtainPixelWidth))]
    [NotifyPropertyChangedFor(nameof(DividerMargin))]
    [NotifyPropertyChangedFor(nameof(DividerHeight))]
    [NotifyPropertyChangedFor(nameof(ThumbMargin))]
    [NotifyPropertyChangedFor(nameof(OverlayClipGeometry))]
    private Rect _imageRenderRect = new(0, 0, 700, 400);

    [ObservableProperty]
    private double _parentTotalWidth = 700.0;

    [ObservableProperty]
    private double _parentTotalHeight = 400.0;

    public double DividerX => ImageRenderRect.Left + (ImageRenderRect.Width * CurtainPosition / 100.0);
    public double CurtainPixelWidth => Math.Max(0, DividerX - ImageRenderRect.Left);
    public Thickness DividerMargin => new(DividerX, ImageRenderRect.Top, 0, 0);
    public double DividerHeight => Math.Max(1, ImageRenderRect.Height);
    public Thickness ThumbMargin => new(Math.Max(0, DividerX - 17), ImageRenderRect.Top + Math.Max(0, (ImageRenderRect.Height - 34) / 2.0), 0, 0);
    public string CurtainPercentageText => _localizer.Format("CurtainPositionFormat", CurtainPosition, 100 - CurtainPosition);

    public Avalonia.Media.RectangleGeometry OverlayClipGeometry =>
        new(new Rect(ImageRenderRect.Left, ImageRenderRect.Top, Math.Max(0, DividerX - ImageRenderRect.Left), Math.Max(1, ImageRenderRect.Height)));

    public void UpdateImageGeometry(Rect rect, double width, double height)
    {
        ParentTotalWidth = width;
        ParentTotalHeight = height;
        ImageRenderRect = rect;
        OnPropertyChanged(nameof(DividerX));
        OnPropertyChanged(nameof(CurtainPixelWidth));
        OnPropertyChanged(nameof(DividerMargin));
        OnPropertyChanged(nameof(DividerHeight));
        OnPropertyChanged(nameof(ThumbMargin));
        OnPropertyChanged(nameof(OverlayClipGeometry));
    }

    [ObservableProperty]
    private string _beforeSizeText = "—";

    [ObservableProperty]
    private string _afterSizeText = "—";

    [ObservableProperty]
    private string _savedPercentResult = string.Empty;

    [ObservableProperty]
    private Bitmap? _originalCompareBitmap;

    [ObservableProperty]
    private Bitmap? _strippedCompareBitmap;

    [ObservableProperty]
    private string _statusText = string.Empty;

    protected internal override void OnClosed()
    {
        var original = OriginalCompareBitmap;
        var stripped = StrippedCompareBitmap;
        OriginalCompareBitmap = null;
        StrippedCompareBitmap = null;
        original?.Dispose();
        stripped?.Dispose();
    }

    private async Task LoadComparisonAsync()
    {
        StatusText = _localizer["CompareLoading"];
        if (string.IsNullOrWhiteSpace(PhotoPath) || !File.Exists(PhotoPath))
        {
            StatusText = _localizer["CompareUnavailable"];
            return;
        }

        try
        {
            var result = await Task.Run(() => Render(PhotoPath, HeicQuality));
            Dispatcher.UIThread.Post(() => Apply(result));
        }
        catch (Exception ex)
        {
            ErrorLogger.Log(ex, "生成瘦身对比");
            Dispatcher.UIThread.Post(() => StatusText = _localizer["CompareUnavailable"]);
        }
    }

    private void Apply(RenderResult result)
    {
        // 生成期间弹窗已关闭：位图无人持有，立即释放
        if (IsClosed)
        {
            result.Original.Dispose();
            result.Stripped.Dispose();
            return;
        }

        OriginalCompareBitmap = result.Original;
        StrippedCompareBitmap = result.Stripped;
        double origMb = result.OriginalBytes / (1024.0 * 1024);
        double strippedMb = result.StrippedBytes / (1024.0 * 1024);
        double savedPct = result.OriginalBytes > 0 ? (1.0 - (double)result.StrippedBytes / result.OriginalBytes) * 100.0 : 0;
        BeforeSizeText = $"{origMb:F2} MB";
        AfterSizeText = $"{strippedMb:F2} MB";
        SavedPercentResult = _localizer.Format("StripSavedPctFormat", Math.Max(0, savedPct));
        StatusText = string.Empty;
    }

    private sealed record RenderResult(Bitmap Original, Bitmap Stripped, long OriginalBytes, long StrippedBytes);

    /// <summary>原片连同同名配对视频计入原始体积；瘦身后体积为按质量编码 HEIC 的实际大小。</summary>
    private static RenderResult Render(string photoPath, int quality)
    {
        long originalBytes = new FileInfo(photoPath).Length;
        var dir = Path.GetDirectoryName(photoPath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(photoPath);
        foreach (var ext in new[] { ".mov", ".MOV", ".mp4", ".MP4" })
        {
            var videoPath = Path.Combine(dir, stem + ext);
            if (File.Exists(videoPath))
            {
                originalBytes += new FileInfo(videoPath).Length;
                break;
            }
        }

        using var image = new MagickImage(photoPath);
        image.AutoOrient();
        // 限制预览尺寸，避免大图占用过多非托管内存
        image.Resize(new MagickGeometry(PreviewMaxSize, PreviewMaxSize) { IgnoreAspectRatio = false, Greater = true });

        using var origMem = new MemoryStream();
        image.Write(origMem, MagickFormat.Png);
        origMem.Position = 0;
        var origBmp = new Bitmap(origMem);

        Bitmap strippedBmp;
        long strippedBytes;
        try
        {
            image.Format = MagickFormat.Heic;
            image.Quality = (uint)quality;
            using var heicMem = new MemoryStream();
            image.Write(heicMem);
            strippedBytes = heicMem.Length;
            heicMem.Position = 0;
            strippedBmp = DecodeToBitmap(heicMem);
        }
        catch (MagickException)
        {
            // 没有 HEIC 编码器时用同质量 JPEG 近似画质，体积按经验比例估算
            image.Format = MagickFormat.Jpeg;
            image.Quality = (uint)quality;
            using var jpegMem = new MemoryStream();
            image.Write(jpegMem);
            strippedBytes = (long)(jpegMem.Length * 0.65);
            jpegMem.Position = 0;
            strippedBmp = DecodeToBitmap(jpegMem);
        }

        return new RenderResult(origBmp, strippedBmp, originalBytes, strippedBytes);
    }

    private static Bitmap DecodeToBitmap(Stream encoded)
    {
        using var decoded = new MagickImage(encoded);
        using var png = new MemoryStream();
        decoded.Write(png, MagickFormat.Png);
        png.Position = 0;
        return new Bitmap(png);
    }
}
