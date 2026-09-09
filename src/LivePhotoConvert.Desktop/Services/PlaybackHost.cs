using System.Diagnostics;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using LivePhotoConvert.Core.External;
using LivePhotoConvert.Desktop.Models;

namespace LivePhotoConvert.Desktop.Services;

/// <summary>
/// 画廊交互与悬浮预览调度器：悬停 250ms 后按需提取微视频帧并循环播放，
/// 仅针对当前悬浮的单张卡片，离开即刻停止并恢复静态缩略图。
/// </summary>
public sealed class PlaybackHost
{
    public static PlaybackHost Instance { get; } = new();

    private readonly DispatcherTimer _cycleTimer;
    private CancellationTokenSource? _hoverCts;
    private PhotoCardItemViewModel? _activeCard;
    private int _frameIndex;

    public Action<PhotoCardItemViewModel>? OnQuickLookTriggered { get; set; }
    public Action<PhotoCardItemViewModel>? OnCardFocused { get; set; }

    private PlaybackHost()
    {
        _cycleTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33) // ~30 FPS 原生高帧率平滑回放
        };
        _cycleTimer.Tick += OnCycleTick;
    }

    /// <summary>
    /// 鼠标进入卡片：250ms 防抖后按需提取视频帧并循环播放。
    /// </summary>
    public void OnPointerEnter(PhotoCardItemViewModel card)
    {
        CancelPending();
        _cycleTimer.Stop();

        if (_activeCard is not null && _activeCard != card)
        {
            RestoreStatic(_activeCard);
        }

        _activeCard = card;
        card.IsHoverPlaying = true;
        OnCardFocused?.Invoke(card);

        // 已缓存帧序列：直接启动循环播放
        if (card.CachedFrames is { Count: > 0 })
        {
            _frameIndex = 0;
            card.DisplayImage = card.CachedFrames[0];
            _cycleTimer.Start();
            return;
        }

        // 无视频路径且非安卓动态照片则不提取
        if (string.IsNullOrWhiteSpace(card.VideoPath) && !card.IsMotionPhoto)
            return;

        // 250ms 防抖后启动后台帧提取
        _hoverCts = new CancellationTokenSource();
        var token = _hoverCts.Token;

        Task.Delay(250, token).ContinueWith(task =>
        {
            _ = ExtractFramesAsync(card, token);
        }, token, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    /// <summary>
    /// 鼠标离开卡片：立即停止播放，恢复静态缩略图。
    /// </summary>
    public void OnPointerLeave(PhotoCardItemViewModel card)
    {
        CancelPending();
        _cycleTimer.Stop();
        card.IsHoverPlaying = false;

        if (_activeCard == card)
        {
            RestoreStatic(card);
            _activeCard = null;
        }
    }

    public void StopHoverPlayback()
    {
        CancelPending();
        _cycleTimer.Stop();

        if (_activeCard is not null)
        {
            RestoreStatic(_activeCard);
            _activeCard.IsHoverPlaying = false;
            _activeCard = null;
        }
    }

    public void TriggerQuickLook(PhotoCardItemViewModel card)
    {
        OnQuickLookTriggered?.Invoke(card);
    }

    private void CancelPending()
    {
        _hoverCts?.Cancel();
        _hoverCts?.Dispose();
        _hoverCts = null;
    }

    private static void RestoreStatic(PhotoCardItemViewModel card)
    {
        if (card.Thumbnail is not null)
        {
            card.DisplayImage = card.Thumbnail;
        }
    }

    private async Task ExtractFramesAsync(PhotoCardItemViewModel card, CancellationToken token)
    {
        if (card.IsMotionPhoto && (string.IsNullOrWhiteSpace(card.VideoPath) || !File.Exists(card.VideoPath)))
        {
            var extracted = await MotionPhotoVideoCache.EnsureVideoExtractedAsync(card, token);
            if (string.IsNullOrEmpty(extracted))
                return;
        }

        if (string.IsNullOrWhiteSpace(card.VideoPath) || !File.Exists(card.VideoPath))
            return;

        string? ffmpegPath = ToolLocator.Find(FfmpegVideoConverter.ExecutableName);
        if (string.IsNullOrEmpty(ffmpegPath) || !File.Exists(ffmpegPath))
            return;

        Process? process = null;
        var frames = new List<Bitmap>();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-v error -i \"{card.VideoPath}\" -vf \"scale=480:-2:flags=fast_bilinear\" -c:v mjpeg -q:v 4 -f image2pipe -",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            process = Process.Start(psi);
            if (process is null) return;

            using var stdout = process.StandardOutput.BaseStream;
            using var ms = new MemoryStream();
            byte[] buffer = new byte[32768];
            int bytesRead;
            byte prev = 0;
            bool inFrame = false;

            while (!token.IsCancellationRequested &&
                   (bytesRead = await stdout.ReadAsync(buffer, token)) > 0)
            {
                for (int i = 0; i < bytesRead; i++)
                {
                    byte b = buffer[i];
                    if (!inFrame)
                    {
                        if (prev == 0xFF && b == 0xD8)
                        {
                            inFrame = true;
                            ms.WriteByte(0xFF);
                            ms.WriteByte(0xD8);
                        }
                    }
                    else
                    {
                        ms.WriteByte(b);
                        if (prev == 0xFF && b == 0xD9)
                        {
                            byte[] frameData = ms.ToArray();
                            ms.SetLength(0);
                            inFrame = false;

                            using var frameMs = new MemoryStream(frameData);
                            frames.Add(new Bitmap(frameMs));
                        }
                    }
                    prev = b;
                }
            }

            if (token.IsCancellationRequested)
            {
                foreach (var f in frames) f.Dispose();
                return;
            }

            if (frames.Count > 0)
            {
                card.CachedFrames = frames;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (card.IsHoverPlaying && _activeCard == card)
                    {
                        _frameIndex = 0;
                        card.DisplayImage = frames[0];
                        _cycleTimer.Start();
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
            foreach (var f in frames) f.Dispose();
        }
        catch
        {
            foreach (var f in frames) f.Dispose();
        }
        finally
        {
            try
            {
                if (process is { HasExited: false })
                {
                    process.Kill(entireProcessTree: true);
                }
                process?.Dispose();
            }
            catch
            {
                // ignored
            }
        }
    }

    private void OnCycleTick(object? sender, EventArgs e)
    {
        var card = _activeCard;
        if (card?.CachedFrames is not { Count: > 0 } || !card.IsHoverPlaying)
        {
            _cycleTimer.Stop();
            return;
        }

        _frameIndex = (_frameIndex + 1) % card.CachedFrames.Count;
        card.DisplayImage = card.CachedFrames[_frameIndex];
    }
}
