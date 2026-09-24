using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Models;
using LivePhotoConvert.Desktop.Services;

namespace LivePhotoConvert.Desktop.ViewModels.Dialogs;

/// <summary>
/// 大图预览与实况播放；关闭时停止播放并释放帧。
/// </summary>
public sealed partial class QuickLookDialogViewModel : DialogViewModel<bool>
{
    private const int QuickLookPhotoMaxSize = 1600;
    private readonly ILocalizer _localizer;
    private readonly Func<int, PhotoCardItemViewModel?> _cardAt;
    private readonly int _count;
    private int _index;
    private readonly LivePhotoStreamPlayer _streamPlayer = new();
    private readonly ThumbnailReader _thumbnailReader = new();
    private Bitmap? _ownedPhotoPreview;
    private int _previewGeneration;
    private bool _hasPresentedVideoFrame;

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
    public QuickLookDialogViewModel(ILocalizer localizer, Func<int, PhotoCardItemViewModel?> cardAt, int count, int startIndex)
    {
        _localizer = localizer;
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

        Card = card;
        NavigationIndexText = indexText;

        CurrentDisplayImage = card.DisplayImage ?? card.Thumbnail;
        LoadHighResolutionPhotoPreview(card, generation);

        if (card.IsMotionPhoto && (string.IsNullOrWhiteSpace(card.VideoPath) || !File.Exists(card.VideoPath)))
        {
            HasVideo = true;
            IsPlaying = true;
            PlayButtonText = _localizer["QuickLookPause"];
            PlaybackStatusText = _localizer["QuickLookLoading"];

            _ = Task.Run(async () =>
            {
                var extracted = await MotionPhotoVideoCache.EnsureVideoExtractedAsync(card);
                if (!string.IsNullOrEmpty(extracted) && Card == card)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (Card == card && !IsClosed)
                        {
                            PlaybackStatusText = _localizer["QuickLookPlaying"];
                            _streamPlayer.Play(extracted, frame =>
                            {
                                _hasPresentedVideoFrame = true;
                                CurrentDisplayImage = frame;
                            });
                        }
                    });
                }
            });
            return;
        }

        string? videoPath = card.VideoPath;
        bool hasValidVideo = !string.IsNullOrWhiteSpace(videoPath) && File.Exists(videoPath);
        HasVideo = hasValidVideo;

        if (hasValidVideo)
        {
            IsPlaying = true;
            PlayButtonText = _localizer["QuickLookPause"];
            PlaybackStatusText = _localizer["QuickLookPlaying"];

            _streamPlayer.Play(videoPath!, frame =>
            {
                _hasPresentedVideoFrame = true;
                CurrentDisplayImage = frame;
            });
        }
        else
        {
            IsPlaying = false;
            PlayButtonText = _localizer["QuickLookStatic"];
            PlaybackStatusText = _localizer["QuickLookStatic"];
        }
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
    }

    private void LoadHighResolutionPhotoPreview(PhotoCardItemViewModel card, int generation)
    {
        _ = Task.Run(() =>
        {
            var result = _thumbnailReader.Read(card.PhotoPath, QuickLookPhotoMaxSize);
            if (result is null || generation != _previewGeneration) return;

            Bitmap? preview = null;
            try
            {
                using var stream = new MemoryStream(result.ImageBytes);
                preview = new Bitmap(stream);
            }
            catch
            {
                preview?.Dispose();
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (generation != _previewGeneration || Card != card)
                {
                    preview.Dispose();
                    return;
                }

                _ownedPhotoPreview = preview;
                if (!_hasPresentedVideoFrame)
                {
                    CurrentDisplayImage = preview;
                }
            });
        });
    }

    private void ReleaseOwnedPhotoPreview()
    {
        var oldPreview = _ownedPhotoPreview;
        _ownedPhotoPreview = null;
        if (oldPreview is null) return;

        if (ReferenceEquals(CurrentDisplayImage, oldPreview))
        {
            CurrentDisplayImage = null;
        }

        DispatcherTimer.RunOnce(oldPreview.Dispose, TimeSpan.FromMilliseconds(160), DispatcherPriority.Background);
    }
}
