using System.ComponentModel;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoConvert.Core.Media.Thumbnails;
using LivePhotoConvert.Core.Services;
using LivePhotoConvert.Desktop.Features.Library.Thumbnails;
using LivePhotoConvert.Desktop.Features.Playback;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Models;

namespace LivePhotoConvert.Desktop.Features.Dialogs;

/// <summary>
/// 大图预览与实况播放；关闭时停止播放并释放播放器。
/// 显示中的卡片通过缩略图管线钉住，占位缩略图不会被驱逐；高清图由本弹窗独占并在切换或关闭时释放。
/// 播放器在视图挂到窗口后创建（需要窗口的刷新节拍），存续期间独占播放。
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
    private ILivePhotoPlayer? _player;
    private Bitmap? _ownedPhotoPreview;
    private CancellationTokenSource? _previewCts;
    private PhotoCardItemViewModel? _acquiredCard;
    private int _previewGeneration;
    private int _previewHeightPx;
    private PixelSize? _playbackTarget;

    /// <summary>
    /// 当前卡片出现过的最大画布（逐维取最大）。弹窗按内容定尺寸，照片与视频比例不同会让画布在两种形状间来回变化，
    /// 按当前画布重启解码会形成振荡；取历史最大值后至多重启一两次。
    /// </summary>
    private (double Width, double Height) _playbackArea;
    private (double Width, double Height, double Scaling) _viewport;

    [ObservableProperty]
    private PhotoCardItemViewModel _card;

    [ObservableProperty]
    private Bitmap? _currentDisplayImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStatusBadgeVisible))]
    private bool _hasVideo;

    [ObservableProperty]
    private bool _isPlaying = true;

    [ObservableProperty]
    private string _playbackStatusText = string.Empty;

    [ObservableProperty]
    private string _playButtonText;

    [ObservableProperty]
    private string _navigationIndexText;

    /// <summary>当前卡片的视频无法播放；原因见 <see cref="PlaybackStatusText"/>。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStatusBadgeVisible))]
    private bool _hasPlaybackError;

    /// <summary>左上角的播放状态；失败时原因改在画面下方的提示条中显示。</summary>
    public bool IsStatusBadgeVisible => HasVideo && !HasPlaybackError;

    /// <summary>错误可在依赖页解决（缺 FFmpeg 或缺 HDR 色调映射滤镜）。</summary>
    [ObservableProperty]
    private bool _canOpenTools;

    /// <summary>播放器来源；为 null 时只显示照片。</summary>
    public PlaybackService? Playback { get; init; }

    /// <summary>前往依赖页；弹窗先关闭。</summary>
    public Action? OpenTools { get; init; }

    internal ILivePhotoPlayer? Player => _player;

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
        _player?.Stop();
        _playbackTarget = null;
        _playbackArea = (_viewport.Width, _viewport.Height);
        int generation = ++_previewGeneration;
        ReleaseOwnedPhotoPreview();
        AttachCard(card);

        Card = card;
        NavigationIndexText = indexText;

        // 只借用卡片的静态缩略图：它已被钉住，不会在显示期间被释放
        CurrentDisplayImage = card.Thumbnail;
        LoadHighResolutionPhotoPreview(card, generation);

        // 视频是否存在由扫描确定，界面线程不再查询磁盘；安卓动态照片直接读取照片内的视频区段
        HasVideo = card.Video is not null;
        (HasPlaybackError, CanOpenTools) = (false, false);
        if (HasVideo)
        {
            // 每张卡片都从播放开始，上一张的暂停不延续
            IsPlaying = true;
            _player?.IsPaused = false;
            PlaybackStatusText = _localizer["QuickLookLoading"];
            StartPlayback();
        }
        else
        {
            PlaybackStatusText = _localizer["QuickLookStatic"];
        }

        UpdatePlayButton();
    }

    /// <summary>视图已挂到窗口：按窗口的刷新节拍创建播放器，开始独占播放。</summary>
    public void AttachPlayer(IFrameScheduler scheduler)
    {
        if (IsClosed || Playback is null || _player is not null)
        {
            return;
        }

        var player = Playback.CreatePlayer(scheduler);
        Playback.EnterExclusive(player);
        player.IsPaused = !IsPlaying;
        player.SurfaceInvalidated += OnSurfaceInvalidated;
        player.StateChanged += OnPlayerStateChanged;
        _player = player;
        StartPlayback();
    }

    /// <summary>画布尺寸已知后才开始解码：解码尺寸取画布的物理像素。</summary>
    private void StartPlayback()
    {
        if (_player is not { } player || IsClosed || Card.Video is not { } video || PlaybackTargetFor() is not { } target)
        {
            return;
        }

        _playbackTarget = target;
        (HasPlaybackError, CanOpenTools) = (false, false);
        Observe(player.PlayAsync(video, target, _viewport.Scaling, PlaybackBudget.QuickLook), "预览实况视频");
    }

    private PixelSize? PlaybackTargetFor() => _playbackArea.Width >= 1 && _playbackArea.Height >= 1
        ? new PixelSize((int)Math.Ceiling(_playbackArea.Width), (int)Math.Ceiling(_playbackArea.Height))
        : null;

    private void OnSurfaceInvalidated(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, _player) && !IsClosed)
        {
            CurrentDisplayImage = _player!.Surface ?? _ownedPhotoPreview ?? Card.Thumbnail;
        }
    }

    private void OnPlayerStateChanged(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _player) || IsClosed || !HasVideo)
        {
            return;
        }

        var state = _player!.State;
        switch (state.Status)
        {
            case PlayerStatus.Loading:
                PlaybackStatusText = _localizer["QuickLookLoading"];
                break;
            case PlayerStatus.Playing:
                PlaybackStatusText = _localizer[IsPlaying ? "QuickLookPlaying" : "QuickLookPaused"];
                break;
            case PlayerStatus.Error:
                // 按钮回到「播放」，再按即重试
                IsPlaying = false;
                UpdatePlayButton();
                HasPlaybackError = true;
                CanOpenTools = OpenTools is not null && PlaybackTexts.IsToolProblem(state.Error);
                PlaybackStatusText = _localizer[PlaybackTexts.ErrorKey(state.Error)];
                if (state.Detail is { } detail)
                {
                    ErrorLogger.Log(new InvalidOperationException(detail), $"预览实况视频：{state.Error}");
                }

                break;
        }
    }

    private void UpdatePlayButton() =>
        PlayButtonText = _localizer[!HasVideo ? "QuickLookStatic" : IsPlaying ? "QuickLookPause" : "QuickLookLoop"];

    /// <summary>
    /// 画布的逻辑尺寸与屏幕缩放比。预览与视频都按画布实际显示的像素解码（Uniform 缩放后的尺寸 × 缩放比），
    /// 高分屏上不会被固定档位限制而偏软；画布变大超过阈值时重新加载。
    /// </summary>
    public void SetViewport(double width, double height, double renderScaling)
    {
        if (!(double.IsFinite(width) && double.IsFinite(height) && double.IsFinite(renderScaling)) || width <= 0 || height <= 0 || renderScaling <= 0)
        {
            return;
        }

        var previousScaling = _viewport.Scaling;
        _viewport = (width, height, renderScaling);
        if (IsClosed)
        {
            return;
        }

        if (PreviewHeightFor(Card) > _previewHeightPx * ReloadThreshold)
        {
            // 旧预览留到新图到达再替换，画面不会先退回缩略图
            _previewCts?.Cancel();
            LoadHighResolutionPhotoPreview(Card, ++_previewGeneration);
        }

        var previousArea = _playbackArea;
        _playbackArea = (Math.Max(previousArea.Width, width), Math.Max(previousArea.Height, height));

        // 首次得到画布尺寸时开始播放；之后只在明显变大时按新尺寸重新解码，缩小由界面缩放
        if (_playbackTarget is null)
        {
            StartPlayback();
        }
        else if (_player?.State.Status is PlayerStatus.Loading or PlayerStatus.Playing
                 && (_playbackArea.Width > previousArea.Width * ReloadThreshold
                     || _playbackArea.Height > previousArea.Height * ReloadThreshold
                     || renderScaling > previousScaling * ReloadThreshold))
        {
            StartPlayback();
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
        if (!HasVideo)
        {
            return;
        }

        IsPlaying = !IsPlaying;
        UpdatePlayButton();
        if (_player is not { } player)
        {
            return;
        }

        player.IsPaused = !IsPlaying;
        if (player.State.Status == PlayerStatus.Playing)
        {
            PlaybackStatusText = _localizer[IsPlaying ? "QuickLookPlaying" : "QuickLookPaused"];
        }
        else if (IsPlaying && player.State.Status is PlayerStatus.Error or PlayerStatus.Idle)
        {
            // 失败后再按播放即重试，例如刚在依赖页装好 FFmpeg
            StartPlayback();
        }
    }

    [RelayCommand]
    private void GoToTools()
    {
        var openTools = OpenTools;
        Cancel();
        openTools?.Invoke();
    }

    [RelayCommand]
    public void PrevItem() => ShowIndex((_index - 1 + _count) % _count);

    [RelayCommand]
    public void NextItem() => ShowIndex((_index + 1) % _count);

    protected internal override void OnClosed()
    {
        ++_previewGeneration;
        if (_player is { } player)
        {
            _player = null;
            player.SurfaceInvalidated -= OnSurfaceInvalidated;
            player.StateChanged -= OnPlayerStateChanged;
            Observe(Playback!.ReleaseAsync(player), "关闭预览");
        }

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
        if (_ownedPhotoPreview is null && _player?.Surface is null)
        {
            CurrentDisplayImage = Card.Thumbnail;
        }
    }

    /// <summary>未被等待：异常在这里记录，不能逃逸成未观察的任务异常。</summary>
    private static void Observe(Task task, string context) =>
        task.ContinueWith(t => ErrorLogger.Log(t.Exception!.GetBaseException(), context), CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

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
        if (_player?.Surface is null)
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
