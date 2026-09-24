using System.ComponentModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoConvert.Core.Media.Thumbnails;
using LivePhotoConvert.Core.Services;
using LivePhotoConvert.Desktop.Features.Library.Thumbnails;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Models;
using LivePhotoConvert.Desktop.Services;

namespace LivePhotoConvert.Desktop.Features.Dialogs;

/// <summary>
/// 大图预览与实况播放；关闭时停止播放并释放帧。
/// 显示中的卡片通过缩略图管线钉住，占位缩略图不会被驱逐；高清图由本弹窗独占并在切换或关闭时释放。
/// </summary>
public sealed partial class QuickLookDialogViewModel : DialogViewModel<bool>
{
    /// <summary>画布尺寸未知（视图尚未布局）时的预览高度。</summary>
    internal const int DefaultPreviewHeightPx = 1024;

    /// <summary>画布变大后新需求比已加载的预览高出这么多才重新加载，避免窗口微调时反复解码。</summary>
    private const double ReloadThreshold = 1.1;

    private readonly ILocalizer _localizer;
    private readonly IThumbnailPipeline _thumbnails;
    private readonly Func<int, PhotoCardItemViewModel?> _cardAt;
    private readonly int _count;
    private int _index;
    private readonly LivePhotoStreamPlayer _streamPlayer = new();
    private Bitmap? _ownedPhotoPreview;
    private CancellationTokenSource? _previewCts;
    private PhotoCardItemViewModel? _acquiredCard;
    private int _previewGeneration;
    private int _previewHeightPx;
    private bool _hasPresentedVideoFrame;
    private (double Width, double Height, double Scaling) _viewport;

    [ObservableProperty]
    private PhotoCardItemViewModel _card;

    [ObservableProperty]
    private Bitmap? _currentDisplayImage;

    [ObservableProperty]
    private bool _hasVideo;

    [ObservableProperty]
    private bool _isPlaying = true;

    [ObservableProperty]
    private string _playbackStatusText = string.Empty;

    [ObservableProperty]
    private string _playButtonText;

    [ObservableProperty]
    private string _navigationIndexText;

    /// <param name="cardAt">按序号取卡片；序号越界或卡片已失效时返回 null。</param>
    /// <param name="count">可浏览的卡片总数，左右切换在此范围内循环。</param>
    /// <param name="startIndex">首张卡片的序号。</param>
    public QuickLookDialogViewModel(ILocalizer localizer, IThumbnailPipeline thumbnails, Func<int, PhotoCardItemViewModel?> cardAt, int count, int startIndex)
    {
        _localizer = localizer;
        _thumbnails = thumbnails;
        _cardAt = cardAt;
        _count = Math.Max(1, count);
        _playButtonText = localizer["QuickLookPause"];
        _card = cardAt(startIndex) ?? throw new ArgumentOutOfRangeException(nameof(startIndex));
        _navigationIndexText = string.Empty;
        ShowIndex(startIndex);
    }

    /// <summary>切换到指定序号的卡片；卡片不存在时保持当前画面。</summary>
    public void ShowIndex(int index)
    {
        if (IsClosed || _cardAt(index) is not { } card)
        {
            return;
        }

        _index = index;
        SetCard(card, $"{index + 1} / {_count}");
    }

    private void SetCard(PhotoCardItemViewModel card, string indexText)
    {
        _streamPlayer.Stop();
        int generation = ++_previewGeneration;
        ReleaseOwnedPhotoPreview();
        _hasPresentedVideoFrame = false;
        AttachCard(card);

        Card = card;
        NavigationIndexText = indexText;

        // 只借用卡片的静态缩略图：它已被钉住；悬浮播放的帧归播放器所有，随时可能被回收
        CurrentDisplayImage = card.Thumbnail;
        LoadHighResolutionPhotoPreview(card, generation);

        if (card.IsMotionPhoto)
        {
            HasVideo = true;
            IsPlaying = true;
            PlayButtonText = _localizer["QuickLookPause"];
            PlaybackStatusText = _localizer["QuickLookLoading"];
            _ = PlayMotionPhotoAsync(card);
            return;
        }

        // 视频是否存在由扫描确定，界面线程不再查询磁盘
        HasVideo = card.VideoPath is not null;
        if (card.VideoPath is { } videoPath)
        {
            IsPlaying = true;
            PlayButtonText = _localizer["QuickLookPause"];
            PlaybackStatusText = _localizer["QuickLookPlaying"];
            _streamPlayer.Play(videoPath, OnFrame);
        }
        else
        {
            IsPlaying = false;
            PlayButtonText = _localizer["QuickLookStatic"];
            PlaybackStatusText = _localizer["QuickLookStatic"];
        }
    }

    /// <summary>
    /// 画布的逻辑尺寸与屏幕缩放比。预览按画布实际显示的像素加载（Uniform 缩放后的高度 × 缩放比），
    /// 高分屏上不会被固定档位限制而偏软；画布变大超过阈值时重新加载。
    /// </summary>
    public void SetViewport(double width, double height, double renderScaling)
    {
        if (!(double.IsFinite(width) && double.IsFinite(height) && double.IsFinite(renderScaling)) || width <= 0 || height <= 0 || renderScaling <= 0)
        {
            return;
        }

        _viewport = (width, height, renderScaling);
        if (!IsClosed && PreviewHeightFor(Card) > _previewHeightPx * ReloadThreshold)
        {
            // 旧预览留到新图到达再替换，画面不会先退回缩略图
            _previewCts?.Cancel();
            LoadHighResolutionPhotoPreview(Card, ++_previewGeneration);
        }
    }

    /// <summary>卡片在当前画布中显示所需的像素高度，不超过最高缩略图档位。</summary>
    public int PreviewHeightFor(PhotoCardItemViewModel card)
    {
        var (width, height, scaling) = _viewport;
        if (width <= 0)
        {
            return DefaultPreviewHeightPx;
        }

        var shownHeight = Math.Min(height, width / Math.Max(0.01, card.AspectRatio));
        return (int)Math.Clamp(Math.Ceiling(shownHeight * scaling), 1, ThumbnailTiers.Largest);
    }

    [RelayCommand]
    public void TogglePlay()
    {
        if (!HasVideo) return;

        _streamPlayer.TogglePlay();
        IsPlaying = _streamPlayer.IsPlaying;
        PlayButtonText = IsPlaying ? _localizer["QuickLookPause"] : _localizer["QuickLookLoop"];
        PlaybackStatusText = IsPlaying ? _localizer["QuickLookPlaying"] : _localizer["QuickLookPaused"];
    }

    [RelayCommand]
    public void PrevItem() => ShowIndex((_index - 1 + _count) % _count);

    [RelayCommand]
    public void NextItem() => ShowIndex((_index + 1) % _count);

    protected internal override void OnClosed()
    {
        ++_previewGeneration;
        _streamPlayer.Stop();
        _streamPlayer.Dispose();
        ReleaseOwnedPhotoPreview();
        CurrentDisplayImage = null;
        AttachCard(null);
    }

    /// <summary>钉住正在显示的卡片并跟随其缩略图替换（换档时旧图会被释放）。</summary>
    private void AttachCard(PhotoCardItemViewModel? card)
    {
        if (ReferenceEquals(_acquiredCard, card))
        {
            return;
        }

        if (_acquiredCard is { } previous)
        {
            previous.PropertyChanged -= OnCardPropertyChanged;
            _thumbnails.Release(previous);
        }

        _acquiredCard = card;
        if (card is not null)
        {
            _thumbnails.Acquire(card);
            card.PropertyChanged += OnCardPropertyChanged;
        }
    }

    private void OnCardPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PhotoCardItemViewModel.Thumbnail) || !ReferenceEquals(sender, Card) || IsClosed)
        {
            return;
        }

        // 高清图或视频帧已在显示时不回退到缩略图
        if (_ownedPhotoPreview is null && !_hasPresentedVideoFrame)
        {
            CurrentDisplayImage = Card.Thumbnail;
        }
    }

    private void OnFrame(Bitmap frame)
    {
        _hasPresentedVideoFrame = true;
        CurrentDisplayImage = frame;
    }

    /// <summary>未被等待：异常在这里记录，不能逃逸成未观察的任务异常。</summary>
    private async Task PlayMotionPhotoAsync(PhotoCardItemViewModel card)
    {
        try
        {
            var extracted = await Task.Run(() => MotionPhotoVideoCache.EnsureVideoExtractedAsync(card));
            if (!string.IsNullOrEmpty(extracted) && ReferenceEquals(Card, card) && !IsClosed)
            {
                PlaybackStatusText = _localizer["QuickLookPlaying"];
                _streamPlayer.Play(extracted, OnFrame);
            }
        }
        catch (Exception ex)
        {
            ErrorLogger.Log(ex, "预览动态照片");
        }
    }

    private void LoadHighResolutionPhotoPreview(PhotoCardItemViewModel card, int generation)
    {
        _previewHeightPx = PreviewHeightFor(card);
        _ = LoadHighResolutionPhotoPreviewAsync(card, _previewHeightPx, generation);
    }

    private async Task LoadHighResolutionPhotoPreviewAsync(PhotoCardItemViewModel card, int heightPx, int generation)
    {
        _previewCts = new CancellationTokenSource();
        var token = _previewCts.Token;
        Bitmap? preview;
        try
        {
            preview = await _thumbnails.LoadPreviewAsync(card, heightPx, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            // 高清图失败时继续显示缩略图
            ErrorLogger.Log(ex, "预览大图");
            return;
        }

        if (preview is null)
        {
            return;
        }

        if (generation != _previewGeneration || IsClosed || !ReferenceEquals(Card, card))
        {
            preview.Dispose();
            return;
        }

        var previous = _ownedPhotoPreview;
        _ownedPhotoPreview = preview;
        if (!_hasPresentedVideoFrame)
        {
            CurrentDisplayImage = preview;
        }

        DisposeLater(previous);
    }

    /// <summary>让已提交的渲染帧先放开对旧位图的引用。</summary>
    private static void DisposeLater(Bitmap? bitmap)
    {
        if (bitmap is not null)
        {
            DispatcherTimer.RunOnce(bitmap.Dispose, TimeSpan.FromMilliseconds(160), DispatcherPriority.Background);
        }
    }

    private void ReleaseOwnedPhotoPreview()
    {
        _previewCts?.Cancel();
        _previewCts = null;

        var oldPreview = _ownedPhotoPreview;
        _ownedPhotoPreview = null;
        if (oldPreview is null) return;

        if (ReferenceEquals(CurrentDisplayImage, oldPreview))
        {
            CurrentDisplayImage = Card.Thumbnail;
        }

        DisposeLater(oldPreview);
    }
}
