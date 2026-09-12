using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoConvert.Desktop.Models;
using LivePhotoConvert.Desktop.Services;

namespace LivePhotoConvert.Desktop.ViewModels.Dialogs;

public sealed partial class QuickLookDialogViewModel : ViewModelBase
{
    private const int QuickLookPhotoMaxSize = 1600;
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
    private string _playButtonText = LocalizationService.Instance.GetString("QuickLookPause");

    [ObservableProperty]
    private string _navigationIndexText;

    public Action? OnClose { get; init; }
    public Action<int>? OnNavigate { get; init; }

    public QuickLookDialogViewModel(PhotoCardItemViewModel initialCard, string initialIndexText = "")
    {
        _card = initialCard;
        _navigationIndexText = initialIndexText;
        SetCard(initialCard, initialIndexText);
    }

    public void SetCard(PhotoCardItemViewModel card, string indexText = "")
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
            PlayButtonText = LocalizationService.Instance.GetString("QuickLookPause");
            PlaybackStatusText = LocalizationService.Instance.GetString("QuickLookLoading");

            _ = Task.Run(async () =>
            {
                var extracted = await MotionPhotoVideoCache.EnsureVideoExtractedAsync(card);
                if (!string.IsNullOrEmpty(extracted) && Card == card)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (Card == card)
                        {
                            PlaybackStatusText = "实况播放中";
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
            PlayButtonText = LocalizationService.Instance.GetString("QuickLookPause");
            PlaybackStatusText = LocalizationService.Instance.GetString("QuickLookPlaying");

            _streamPlayer.Play(videoPath!, frame =>
            {
                _hasPresentedVideoFrame = true;
                CurrentDisplayImage = frame;
            });
        }
        else
        {
            IsPlaying = false;
            PlayButtonText = LocalizationService.Instance.GetString("QuickLookStatic");
            PlaybackStatusText = LocalizationService.Instance.GetString("QuickLookStatic");
        }
    }

    [RelayCommand]
    public void TogglePlay()
    {
        if (!HasVideo) return;

        _streamPlayer.TogglePlay();
        IsPlaying = _streamPlayer.IsPlaying;
        PlayButtonText = IsPlaying ? LocalizationService.Instance.GetString("QuickLookPause") : LocalizationService.Instance.GetString("QuickLookLoop");
        PlaybackStatusText = IsPlaying ? LocalizationService.Instance.GetString("QuickLookPlaying") : LocalizationService.Instance.GetString("QuickLookPaused");
    }

    [RelayCommand]
    public void PrevItem()
    {
        OnNavigate?.Invoke(-1);
    }

    [RelayCommand]
    public void NextItem()
    {
        OnNavigate?.Invoke(1);
    }

    [RelayCommand]
    public void Close()
    {
        Cleanup();
        OnClose?.Invoke();
    }

    public void Cleanup()
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
