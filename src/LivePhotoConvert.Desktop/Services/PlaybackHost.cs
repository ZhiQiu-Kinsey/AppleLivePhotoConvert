using System.Diagnostics;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using LivePhotoConvert.Core.External;
using LivePhotoConvert.Desktop.Models;
namespace LivePhotoConvert.Desktop.Services;

/// <summary>
/// 画廊交互与悬浮预览调度器。
/// 首帧到达即开始播放，后续帧在后台持续解码；仅针对当前悬浮的单张卡片。
/// </summary>
public sealed class PlaybackHost
{
    private static readonly TimeSpan HoverDebounce = TimeSpan.FromMilliseconds(80);
    internal const int MaxFrameCacheSets = 1;
    internal const int MaxPreviewFrames = 60;

    public static PlaybackHost Instance { get; } = new();

    private readonly DispatcherTimer _cycleTimer;
    private readonly Lock _frameLock = new();
    private readonly Lock _preloadLock = new();
    // 预热会启动 FFmpeg 并生成多张非托管 Bitmap，只允许一个后台预热任务。
    private readonly SemaphoreSlim _preloadGate = new(1, 1);
    private readonly HashSet<PhotoCardItemViewModel> _preloading = [];
    private readonly Dictionary<PhotoCardItemViewModel, CancellationTokenSource> _preloadCts = [];
    private readonly LinkedList<PhotoCardItemViewModel> _frameCacheLru = [];
    private readonly List<Bitmap> _pendingFrameDisposals = [];
    private readonly DispatcherTimer _frameDisposeTimer;
    private CancellationTokenSource? _hoverCts;
    private PhotoCardItemViewModel? _activeCard;
    private List<Bitmap>? _activeFrames;
    private bool _decodeCompleted;
    private int _frameIndex;
    private int _hoverGeneration;

    /// <summary>鼠标进入卡片（开始悬浮预览）时触发。</summary>
    public event EventHandler<PhotoCardItemViewModel>? CardFocused;

    /// <summary>由宿主注入的自定义 FFmpeg 可执行文件路径解析委托。</summary>
    public Func<string?>? CustomFfmpegPathProvider { get; set; }

    private PlaybackHost()
    {
        _cycleTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33) // ~30 FPS 原生高帧率平滑回放
        };
        _cycleTimer.Tick += OnCycleTick;
        _frameDisposeTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _frameDisposeTimer.Tick += (_, _) => FlushFrameDisposals();
    }

    /// <summary>
    /// 鼠标进入卡片：短暂防抖后按需提取视频帧，首帧到达即播放。
    /// </summary>
    public void OnPointerEnter(PhotoCardItemViewModel card)
    {
        CancelPreload(card);
        CancelPending();
        _cycleTimer.Stop();

        var generation = ++_hoverGeneration;

        if (_activeCard is not null && _activeCard != card)
        {
            RestoreStatic(_activeCard);
        }

        _activeCard = card;
        _activeFrames = null;
        _decodeCompleted = false;
        card.IsHoverPlaying = true;
        CardFocused?.Invoke(this, card);

        // 已缓存帧序列：直接启动循环播放
        if (card.CachedFrames is { Count: > 0 })
        {
            TouchFrameCache(card);
            _activeFrames = card.CachedFrames;
            _decodeCompleted = true;
            _frameIndex = 0;
            card.DisplayImage = card.CachedFrames[0];
            _cycleTimer.Start();
            return;
        }

        if (card.Video is null)
            return;

        // 短暂防抖后启动后台帧提取。首帧不会等待整个视频解码完成。
        _hoverCts = new CancellationTokenSource();
        var token = _hoverCts.Token;

        Task.Delay(HoverDebounce, token).ContinueWith(task =>
        {
            _ = ExtractFramesAsync(card, token, generation);
        }, token, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    /// <summary>
    /// 鼠标离开卡片：立即停止播放，恢复静态缩略图。
    /// </summary>
    public void OnPointerLeave(PhotoCardItemViewModel card)
    {
        ++_hoverGeneration;
        CancelPending();
        _cycleTimer.Stop();
        card.IsHoverPlaying = false;

        if (_activeCard == card)
        {
            RestoreStatic(card);
            _activeCard = null;
            _activeFrames = null;
            _decodeCompleted = false;
        }
    }

    public void StopHoverPlayback()
    {
        ++_hoverGeneration;
        CancelPending();
        _cycleTimer.Stop();

        if (_activeCard is not null)
        {
            RestoreStatic(_activeCard);
            _activeCard.IsHoverPlaying = false;
            _activeCard = null;
        }

        _activeFrames = null;
        _decodeCompleted = false;
    }

    /// <summary>停止悬浮播放并取消全部预热任务（退出程序时调用）。</summary>
    public void StopAll()
    {
        StopHoverPlayback();
        lock (_preloadLock)
        {
            foreach (var cts in _preloadCts.Values)
            {
                cts.Cancel();
            }
        }
    }

    /// <summary>
    /// 视口预热：后台提前解码可见短视频，悬浮时直接命中帧缓存。
    /// 并发限制为两个，避免预热拖慢首屏缩略图加载。
    /// </summary>
    public void Preload(PhotoCardItemViewModel card)
    {
        if (card.CachedFrames is { Count: > 0 } || card.Video is null)
        {
            return;
        }

        lock (_preloadLock)
        {
            if (!_preloading.Add(card))
            {
                return;
            }

            var cts = new CancellationTokenSource();
            _preloadCts[card] = cts;
            _ = Task.Run(async () =>
            {
                try
                {
                    await _preloadGate.WaitAsync(cts.Token);
                    try
                    {
                        await ExtractFramesAsync(card, cts.Token, 0, preload: true);
                    }
                    finally
                    {
                        _preloadGate.Release();
                    }
                }
                catch (OperationCanceledException)
                {
                    // 卡片被悬浮时取消预热，交给前台播放任务接管。
                }
                finally
                {
                    lock (_preloadLock)
                    {
                        _preloading.Remove(card);
                        if (_preloadCts.TryGetValue(card, out var source) && ReferenceEquals(source, cts))
                        {
                            _preloadCts.Remove(card);
                        }

                        cts.Dispose();
                    }
                }
            }, cts.Token);
        }
    }

    private void CancelPending()
    {
        _hoverCts?.Cancel();
        _hoverCts?.Dispose();
        _hoverCts = null;
    }

    private void CancelPreload(PhotoCardItemViewModel card)
    {
        lock (_preloadLock)
        {
            if (_preloadCts.TryGetValue(card, out var cts))
            {
                cts.Cancel();
            }
        }
    }

    private static void RestoreStatic(PhotoCardItemViewModel card)
    {
        if (card.Thumbnail is not null)
        {
            card.DisplayImage = card.Thumbnail;
        }
    }

    private async Task ExtractFramesAsync(PhotoCardItemViewModel card, CancellationToken token, int generation, bool preload = false)
    {
        // 动态照片切出的临时视频只在这里使用，不写回卡片：卡片仍是动态照片，不会被当成实况对再合成
        var videoPath = card.IsMotionPhoto
            ? await MotionPhotoVideoCache.EnsureVideoExtractedAsync(card, token)
            : card.VideoPath;
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
            return;

        string? ffmpegPath = ToolLocator.Find(FfmpegVideoConverter.ExecutableName, CustomFfmpegPathProvider?.Invoke());
        if (string.IsNullOrEmpty(ffmpegPath) || !File.Exists(ffmpegPath))
            return;

        Process? process = null;
        var frames = new List<Bitmap>();
        var firstFrameShown = false;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            foreach (var argument in BuildDecodeArguments(videoPath))
            {
                psi.ArgumentList.Add(argument);
            }

            process = Process.Start(psi);
            if (process is null) return;

            // 取消悬浮/预热时 FFmpeg 可能报告 Broken pipe；持续排空但不写入应用控制台。
            _ = process.StandardError.ReadToEndAsync(token);

            await using var stdout = process.StandardOutput.BaseStream;

            while (!token.IsCancellationRequested)
            {
                var bitmap = await BmpPipeFrameReader.ReadNextAsync(stdout, token);
                if (bitmap is null) break;

                bool reachedFrameLimit;
                lock (_frameLock)
                {
                    frames.Add(bitmap);
                    reachedFrameLimit = frames.Count >= MaxPreviewFrames;
                }

                // 首帧到达就提交到 UI，不再等待 FFmpeg 读完整个视频。
                if (!preload && !firstFrameShown)
                {
                    firstFrameShown = true;
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (card.IsHoverPlaying && _activeCard == card && _hoverGeneration == generation)
                        {
                            _activeFrames = frames;
                            _frameIndex = 0;
                            card.DisplayImage = bitmap;
                            _cycleTimer.Start();
                        }
                    });
                }

                if (reachedFrameLimit)
                {
                    break;
                }
            }

            if (token.IsCancellationRequested)
            {
                await DisposeFramesAsync(frames);
                return;
            }

            if (frames.Count > 0)
            {
                var retained = false;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (preload)
                    {
                        if (card.CachedFrames is null)
                        {
                            card.CachedFrames = frames;
                            RegisterFrameCache(card);
                            retained = true;
                        }
                    }
                    else if (card.IsHoverPlaying && _activeCard == card && _hoverGeneration == generation)
                    {
                        _activeFrames = frames;
                        _decodeCompleted = true;
                        card.CachedFrames = frames;
                        RegisterFrameCache(card);
                        retained = true;
                        _cycleTimer.Start();
                    }
                });

                if (!retained)
                {
                    await DisposeFramesAsync(frames);
                }
            }
        }
        catch (OperationCanceledException)
        {
            await DisposeFramesAsync(frames);
        }
        catch
        {
            await DisposeFramesAsync(frames);
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

    /// <summary>
    /// 生成悬浮预览的 FFmpeg 参数。必须保留源帧时间模式，避免 MOV 的名义帧率
    /// （例如 150/1）让 image2pipe 自动补帧并造成数倍慢动作。
    /// </summary>
    internal static string[] BuildDecodeArguments(string videoPath) =>
    [
        "-loglevel", "quiet",
        "-i", videoPath,
        "-map", "0:v:0",
        "-an", "-sn", "-dn",
        "-vf", "scale=720:-2:flags=lanczos",
        "-fps_mode", "passthrough",
        "-c:v", "bmp",
        "-pix_fmt", "bgr24",
        "-f", "image2pipe",
        "-"
    ];

    private async Task DisposeFramesAsync(List<Bitmap> frames)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            lock (_frameLock)
            {
                _pendingFrameDisposals.AddRange(frames);
                frames.Clear();
                _frameDisposeTimer.Stop();
                _frameDisposeTimer.Start();
            }
        });
    }

    private void FlushFrameDisposals()
    {
        List<Bitmap> pending;
        lock (_frameLock)
        {
            _frameDisposeTimer.Stop();
            pending = [.. _pendingFrameDisposals];
            _pendingFrameDisposals.Clear();
        }

        foreach (var frame in pending)
        {
            frame.Dispose();
        }
    }

    private void TouchFrameCache(PhotoCardItemViewModel card)
    {
        lock (_preloadLock)
        {
            _frameCacheLru.Remove(card);
            _frameCacheLru.AddLast(card);
        }
    }

    private void RegisterFrameCache(PhotoCardItemViewModel card)
    {
        List<Bitmap>? evictedFrames = null;
        lock (_preloadLock)
        {
            _frameCacheLru.Remove(card);
            _frameCacheLru.AddLast(card);

            while (_frameCacheLru.Count > MaxFrameCacheSets)
            {
                var node = _frameCacheLru.First!;
                if (ReferenceEquals(node.Value, _activeCard))
                {
                    _frameCacheLru.RemoveFirst();
                    _frameCacheLru.AddLast(node.Value);
                    continue;
                }

                _frameCacheLru.RemoveFirst();
                evictedFrames = node.Value.CachedFrames;
                node.Value.CachedFrames = null;
                break;
            }
        }

        if (evictedFrames is not null)
        {
            _ = DisposeFramesAsync(evictedFrames);
        }
    }

    private void OnCycleTick(object? sender, EventArgs e)
    {
        var card = _activeCard;
        if (card is null || !card.IsHoverPlaying)
        {
            _cycleTimer.Stop();
            return;
        }

        Bitmap? targetFrame;
        lock (_frameLock)
        {
            var frames = _activeFrames;
            if (frames is not { Count: > 0 })
            {
                _cycleTimer.Stop();
                return;
            }

            // 解码尚未完成时，先停在最后一帧，避免在已到达的帧之间反复跳回。
            if (_frameIndex + 1 < frames.Count)
            {
                _frameIndex++;
            }
            else if (_decodeCompleted)
            {
                _frameIndex = 0;
            }
            else
            {
                return;
            }

            targetFrame = frames[_frameIndex];
        }

        card.DisplayImage = targetFrame;
    }
}
