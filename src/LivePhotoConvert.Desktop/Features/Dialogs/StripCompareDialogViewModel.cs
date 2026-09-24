using System.Globalization;
using Avalonia;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoConvert.Core.Services;
using LivePhotoConvert.Desktop.Controls;
using LivePhotoConvert.Desktop.Converters;
using LivePhotoConvert.Desktop.Features.Library;
using LivePhotoConvert.Desktop.Infrastructure;

namespace LivePhotoConvert.Desktop.Features.Dialogs;

/// <summary>
/// 瘦身前后的卷帘对比：样张按瘦身任务的参数真实处理一遍，对比原片与真实产物的画面和体积。
/// 关闭即取消处理，并释放位图、原图块缓存与临时目录。
/// </summary>
public sealed partial class StripCompareDialogViewModel : DialogViewModel<bool>
{
    /// <summary>按钮缩放一档的倍数：1×、2×、4×、8×。</summary>
    public const double ZoomStep = 2;

    private readonly ILocalizer _localizer;
    private readonly IStripSampler _sampler;
    private readonly StripSampleOptions _options;
    private readonly CancellationTokenSource _cts = new();
    private CancellationTokenSource? _displayCts;
    private PixelSize _decodedTarget;
    private StripSample? _sample;
    private CompareImageSource? _images;

    public StripCompareDialogViewModel(ILocalizer localizer, IStripSampler sampler, string photoPath, StripSampleOptions options)
    {
        _localizer = localizer;
        _sampler = sampler;
        _options = options with { HeicQuality = options.HeicQuality > 0 ? options.HeicQuality : ConversionDefaults.HeicQuality };
        PhotoPath = photoPath;
        FileName = Path.GetFileName(photoPath);
        ParametersText = _options.ConvertToHeic
            ? localizer.Format("CompareParamsHeicFormat", HeicQuality)
            : localizer["CompareParamsKeep"];
        _statusText = localizer["CompareProcessing"];
        LoadTask = LoadAsync(_cts.Token);
    }

    public string PhotoPath { get; }

    public string FileName { get; }

    public int HeicQuality => _options.HeicQuality;

    public bool ConvertToHeic => _options.ConvertToHeic;

    /// <summary>任务参数摘要：是否转 HEIC 与质量。</summary>
    public string ParametersText { get; }

    /// <summary>样张处理与首次解码；测试据此等待。</summary>
    internal Task LoadTask { get; }

    /// <summary>最近一次显示位图解码；测试据此等待。</summary>
    internal Task DisplayTask { get; private set; } = Task.CompletedTask;

    internal StripSample? Sample => Volatile.Read(ref _sample);

    internal CompareImageSource? Images => _images;

    /// <summary>处理样张或解码对比图期间为真。</summary>
    [ObservableProperty]
    private bool _isBusy = true;

    [ObservableProperty]
    private bool _hasFailed;

    [ObservableProperty]
    private string _statusText;

    [ObservableProperty]
    private string _encoderText = string.Empty;

    /// <summary>HEIC 不比原格式小时的提示：任务会保留原格式、只剥离视频。</summary>
    [ObservableProperty]
    private string _keptFormatText = string.Empty;

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

    /// <summary>原片摆正后的像素尺寸，对比控件据此计算 1:1 与适配尺寸。</summary>
    [ObservableProperty]
    private PixelSize _sourcePixelSize;

    [ObservableProperty]
    private ICompareDetailSource? _detailSource;

    /// <summary>对比控件报告的显示所需像素尺寸（图片区域 × 屏幕缩放）。</summary>
    [ObservableProperty]
    private PixelSize _displayPixelSize;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurtainPercentageText))]
    private double _curtainPosition = 0.5;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomText))]
    [NotifyCanExecuteChangedFor(nameof(ZoomInCommand))]
    [NotifyCanExecuteChangedFor(nameof(ZoomOutCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetZoomCommand))]
    private double _zoom = CompareViewport.MinZoom;

    [ObservableProperty]
    private bool _isMagnifierEnabled;

    public string CurtainPercentageText => _localizer.Format("CurtainPositionFormat", CurtainPosition * 100, 100 - CurtainPosition * 100);

    public string ZoomText => (Zoom * 100).ToString("F0", CultureInfo.InvariantCulture) + "%";

    private bool CanZoomIn() => Zoom < CompareViewport.MaxZoom;

    private bool CanZoomOut() => Zoom > CompareViewport.MinZoom;

    [RelayCommand(CanExecute = nameof(CanZoomIn))]
    private void ZoomIn() => Zoom = CompareViewport.ClampZoom(Zoom * ZoomStep);

    [RelayCommand(CanExecute = nameof(CanZoomOut))]
    private void ZoomOut() => Zoom = CompareViewport.ClampZoom(Zoom / ZoomStep);

    [RelayCommand(CanExecute = nameof(CanZoomOut))]
    private void ResetZoom() => Zoom = CompareViewport.MinZoom;

    [RelayCommand]
    private void ToggleMagnifier() => IsMagnifierEnabled = !IsMagnifierEnabled;

    partial void OnDisplayPixelSizeChanged(PixelSize value) => RequestDisplayDecode();

    /// <summary>尺寸只在明显变大（显示会变糊）或明显变小（浪费内存）时重新解码。</summary>
    internal static bool NeedsRedecode(PixelSize decoded, PixelSize target) =>
        target.Width > decoded.Width * 1.1 || target.Height > decoded.Height * 1.1
        || target.Width < decoded.Width * 0.6 && target.Height < decoded.Height * 0.6;

    protected internal override void OnClosed()
    {
        _cts.Cancel();
        _displayCts?.Cancel();
        DetailSource = null;
        _images?.Dispose();
        _images = null;

        var original = OriginalCompareBitmap;
        var stripped = StrippedCompareBitmap;
        OriginalCompareBitmap = null;
        StrippedCompareBitmap = null;
        original?.Dispose();
        stripped?.Dispose();

        // 正在处理时由处理流程在取消后自行清理；已完成的产物在这里删除
        Interlocked.Exchange(ref _sample, null)?.Dispose();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var sample = await _sampler.SampleAsync(PhotoPath, _options, cancellationToken);
            Volatile.Write(ref _sample, sample);
            if (IsClosed)
            {
                Interlocked.Exchange(ref _sample, null)?.Dispose();
                return;
            }

            ApplySizes(sample);
            StatusText = _localizer["CompareLoading"];
            var images = await CompareImageSource.OpenAsync(sample.SourcePath, sample.ProductPath, cancellationToken);
            if (IsClosed)
            {
                images.Dispose();
                return;
            }

            _images = images;
            SourcePixelSize = images.SourceSize;
            DetailSource = images;
            RequestDisplayDecode();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    private void ApplySizes(StripSample sample)
    {
        BeforeSizeText = ByteSizeConverter.Format(sample.OriginalBytes);
        AfterSizeText = ByteSizeConverter.Format(sample.ProductBytes);
        var saved = sample.OriginalBytes > 0 ? Math.Max(0, sample.OriginalBytes - sample.ProductBytes) * 100.0 / sample.OriginalBytes : 0;
        SavedPercentResult = _localizer.Format("StripSavedPctFormat", saved);
        EncoderText = sample.Converted || sample.KeptOriginalFormat
            ? _localizer.Format("CompareEncoderFormat", sample.EncoderName)
            : _options.ConvertToHeic ? _localizer["CompareNotConverted"] : string.Empty;
        KeptFormatText = sample.KeptOriginalFormat ? _localizer["CompareKeptFormat"] : string.Empty;
    }

    private void RequestDisplayDecode()
    {
        var target = DisplayPixelSize;
        if (_images is not { } images || IsClosed || target.Width <= 0 || target.Height <= 0)
        {
            return;
        }

        if (_decodedTarget.Width > 0 && !NeedsRedecode(_decodedTarget, target))
        {
            return;
        }

        _displayCts?.Cancel();
        _displayCts?.Dispose();
        _displayCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _decodedTarget = target;
        DisplayTask = DecodeDisplayAsync(images, target, _displayCts.Token);
    }

    private async Task DecodeDisplayAsync(CompareImageSource images, PixelSize target, CancellationToken cancellationToken)
    {
        try
        {
            var (before, after) = await images.DecodeDisplayAsync(target, cancellationToken);
            if (cancellationToken.IsCancellationRequested || IsClosed)
            {
                before.Dispose();
                after.Dispose();
                return;
            }

            var oldBefore = OriginalCompareBitmap;
            var oldAfter = StrippedCompareBitmap;
            OriginalCompareBitmap = before;
            StrippedCompareBitmap = after;
            oldBefore?.Dispose();
            oldAfter?.Dispose();
            IsBusy = false;
            StatusText = string.Empty;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    private void Fail(Exception ex)
    {
        ErrorLogger.Log(ex, "生成瘦身对比");
        if (IsClosed)
        {
            return;
        }

        IsBusy = false;
        HasFailed = true;
        StatusText = _localizer.Format("CompareFailedFormat", ErrorMessages.Describe(_localizer, ex));
    }
}
