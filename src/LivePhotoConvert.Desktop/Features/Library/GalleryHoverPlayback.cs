using Avalonia;
using Avalonia.Threading;
using LivePhotoConvert.Desktop.Controls;
using LivePhotoConvert.Desktop.Features.Playback;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Models;

namespace LivePhotoConvert.Desktop.Features.Library;

/// <summary>
/// 画廊的悬浮播放：指针停在卡片预览区一小段时间后播放该卡片的视频，离开、滚动、重排或卡片被回收时立即停止。
/// 整个画廊只有一个播放器，同一时刻只播放一张卡片。
/// </summary>
/// <remarks>只在界面线程调用。</remarks>
public sealed class GalleryHoverPlayback : IDisposable
{
    /// <summary>指针划过画廊时不为途经的每张卡片启动 FFmpeg。</summary>
    public static readonly TimeSpan DefaultStartDelay = TimeSpan.FromMilliseconds(100);

    private readonly PlaybackService _service;
    private readonly ILivePhotoPlayer _player;
    private readonly Func<double> _scaling;
    private readonly DispatcherTimer _delay;
    private PhotoCardControl? _control;
    private PhotoCardItemViewModel? _card;
    private bool _playing;
    private bool _disposed;

    /// <param name="service">播放器来源</param>
    /// <param name="scheduler">所在窗口的刷新节拍</param>
    /// <param name="scaling">所在窗口当前的 RenderScaling</param>
    /// <param name="startDelay">指针停留多久后开始播放</param>
    public GalleryHoverPlayback(PlaybackService service, IFrameScheduler scheduler, Func<double> scaling, TimeSpan? startDelay = null)
    {
        _service = service;
        _scaling = scaling;
        _player = service.CreatePlayer(scheduler);
        _player.SurfaceInvalidated += OnSurfaceInvalidated;
        _delay = new DispatcherTimer { Interval = startDelay ?? DefaultStartDelay };
        _delay.Tick += (_, _) => StartPending();
        service.StoppingAll += OnStoppingAll;
    }

    /// <summary>正在悬浮（等待或播放）的卡片。</summary>
    public PhotoCardItemViewModel? Card => _card;

    /// <summary>已开始播放（未必已出首帧）。</summary>
    public bool IsPlaying => _playing;

    internal ILivePhotoPlayer Player => _player;

    /// <summary>指针位于某张卡片的预览区；同一张卡片重复调用不会重新开始。</summary>
    public void Hover(PhotoCardControl control)
    {
        if (_disposed)
        {
            return;
        }

        var card = control.DataContext as PhotoCardItemViewModel;
        if (ReferenceEquals(control, _control) && ReferenceEquals(card, _card))
        {
            return;
        }

        Stop();
        if (card?.Video is null)
        {
            return;
        }

        (_control, _card) = (control, card);
        control.DataContextChanged += OnControlInvalidated;
        control.DetachedFromVisualTree += OnControlInvalidated;
        _delay.Start();
    }

    /// <summary>指针离开全部卡片的预览区。</summary>
    public void Leave() => Stop();

    /// <summary>取消等待并停止播放，卡片恢复显示缩略图。</summary>
    public void Stop()
    {
        _delay.Stop();
        _playing = false;
        if (_control is { } control)
        {
            control.DataContextChanged -= OnControlInvalidated;
            control.DetachedFromVisualTree -= OnControlInvalidated;
            control.ShowPlaybackFrame(null);
        }

        (_control, _card) = (null, null);
        _player.Stop();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
        _service.StoppingAll -= OnStoppingAll;
        _player.SurfaceInvalidated -= OnSurfaceInvalidated;
        _service.ReleaseAsync(_player).LogFaults("悬浮播放");
    }

    private void StartPending()
    {
        _delay.Stop();
        if (_control is not { } control || _card is not { Video: { } video } card || !_service.CanPlay(_player))
        {
            return;
        }

        if (CoverTarget(control.PreviewSize, card.AspectRatio) is not { } target)
        {
            return;
        }

        _playing = true;
        _player.PlayAsync(video, target, _scaling(), PlaybackBudget.Hover).LogFaults("悬浮播放");
    }

    /// <summary>
    /// 预览区按 UniformToFill 裁切显示：解码尺寸取能盖满预览区的大小，否则裁切后像素不足而发虚。
    /// </summary>
    internal static PixelSize? CoverTarget(Size area, double aspectRatio)
    {
        if (!(area.Width >= 1 && area.Height >= 1) || !(double.IsFinite(aspectRatio) && aspectRatio > 0))
        {
            return null;
        }

        var width = Math.Max(area.Width, area.Height * aspectRatio);
        var height = Math.Max(area.Height, area.Width / aspectRatio);
        return new PixelSize((int)Math.Ceiling(width), (int)Math.Ceiling(height));
    }

    private void OnSurfaceInvalidated(object? sender, EventArgs e)
    {
        if (_playing)
        {
            _control?.ShowPlaybackFrame(_player.Surface);
        }
    }

    private void OnControlInvalidated(object? sender, EventArgs e) => Stop();

    private void OnStoppingAll(object? sender, EventArgs e) => Stop();
}
