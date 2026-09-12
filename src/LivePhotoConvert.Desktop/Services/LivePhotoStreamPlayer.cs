using System.Diagnostics;
using System.Globalization;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using LivePhotoConvert.Core.External;
namespace LivePhotoConvert.Desktop.Services;

/// <summary>
/// 实况照片单例按需流式解码播放器：
/// 仅针对当前 QuickLook 弹窗中激活的单个实况视频进行内存管道解码，
/// 采用 30 FPS 高帧率平滑循环回放，窗口关闭或切换照片时即时取消并释放全部帧位图内存，
/// 坚决不在画廊扫描期间并发执行任何视频解码。
/// </summary>
public sealed class LivePhotoStreamPlayer : IDisposable
{
    private const int MaxFrames = 180; // 6 秒 @ 30fps，足够覆盖 Live Photo 且限制非托管内存
    private readonly DispatcherTimer _timer;
    private List<Bitmap> _frames = [];
    private readonly List<Bitmap> _pendingDisposals = [];
    private readonly DispatcherTimer _disposeTimer;
    private readonly Lock _lock = new();

    private CancellationTokenSource? _cts;
    private Process? _activeProcess;
    private int _frameIndex;
    private bool _isPlaying;
    private Action<Bitmap>? _onFrameUpdated;
    private int _playGeneration;
    private bool _playbackStarted;
    private TimeSpan[] _frameTimestamps = [];
    private TimeSpan _playbackDuration;
    private readonly Stopwatch _playbackClock = new();

    public bool IsPlaying => _isPlaying;
    public int FrameCount
    {
        get
        {
            lock (_lock) return _frames.Count;
        }
    }

    public LivePhotoStreamPlayer()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            // 播放进度由 Stopwatch 决定；定时器只负责在渲染帧附近刷新显示。
            Interval = TimeSpan.FromMilliseconds(8)
        };
        _timer.Tick += OnTimerTick;
        _disposeTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _disposeTimer.Tick += (_, _) => FlushPendingDisposals();
    }

    /// <summary>
    /// 开始按需解码并播放指定实况伴生视频。
    /// </summary>
    public void Play(string videoPath, Action<Bitmap> onFrameUpdated)
    {
        Stop();

        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
        {
            return;
        }

        _onFrameUpdated = onFrameUpdated;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        int generation = ++_playGeneration;

        _isPlaying = true;
        _frameIndex = 0;
        _playbackStarted = false;

        Task.Run(() => DecodeStreamAsync(videoPath, token, generation), token);
    }

    public void Pause()
    {
        _isPlaying = false;
        _timer.Stop();
        _playbackClock.Stop();
    }

    public void Resume()
    {
        if (FrameCount > 0)
        {
            _isPlaying = true;
            _playbackClock.Start();
            _timer.Start();
        }
    }

    public void TogglePlay()
    {
        if (_isPlaying)
        {
            Pause();
        }
        else
        {
            Resume();
        }
    }

    public void Stop()
    {
        ++_playGeneration;
        _isPlaying = false;
        _playbackStarted = false;
        _timer.Stop();
        _playbackClock.Reset();

        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
        catch
        {
            // ignored
        }
        _cts = null;

        try
        {
            if (_activeProcess is { HasExited: false })
            {
                _activeProcess.Kill(entireProcessTree: true);
                _activeProcess.Dispose();
            }
        }
        catch
        {
            // ignored
        }
        _activeProcess = null;

        lock (_lock)
        {
            var oldFrames = _frames;
            _frames = [];
            _frameTimestamps = [];
            _playbackDuration = TimeSpan.Zero;
            _frameIndex = 0;
            QueueDispose(oldFrames);
        }
    }

    private async Task DecodeStreamAsync(string videoPath, CancellationToken token, int generation)
    {
        string? ffmpegPath = ToolLocator.Find(FfmpegVideoConverter.ExecutableName);
        if (string.IsNullOrEmpty(ffmpegPath) || !File.Exists(ffmpegPath))
        {
            return;
        }

        Process? process = null;
        try
        {
            var timingTask = ProbeFrameTimestampsAsync(videoPath, token);

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-loglevel quiet -hwaccel auto -i \"{videoPath}\" -vf \"scale=1080:1080:force_original_aspect_ratio=decrease:flags=lanczos\" -fps_mode passthrough -c:v bmp -pix_fmt bgr24 -f image2pipe -",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            process = Process.Start(psi);
            if (process is null) return;
            _activeProcess = process;
            _ = process.StandardError.ReadToEndAsync();

            using var stdout = process.StandardOutput.BaseStream;

            while (!token.IsCancellationRequested)
            {
                var bmp = await BmpPipeFrameReader.ReadNextAsync(stdout, token);
                if (bmp is null) break;

                lock (_lock)
                {
                    if (token.IsCancellationRequested || generation != _playGeneration || _frames.Count >= MaxFrames)
                    {
                        bmp.Dispose();
                        continue;
                    }
                    _frames.Add(bmp);
                }
            }

            if (!token.IsCancellationRequested)
            {
                await process.WaitForExitAsync(token);
                var timestamps = await timingTask;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (generation != _playGeneration) return;
                    lock (_lock) PrepareTimeline(timestamps);
                    StartPlaybackOnUi(generation);
                });
            }
        }
        catch (OperationCanceledException)
        {
            // 用户切换卡片或关闭弹窗，正常取消
        }
        catch (Exception)
        {
            // 解码异常安全容错，保持封面静态展示
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

            if (_activeProcess == process)
            {
                _activeProcess = null;
            }
        }
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (!_isPlaying) return;

        Bitmap? targetFrame = null;
        lock (_lock)
        {
            if (_frames.Count > 0 && _playbackDuration > TimeSpan.Zero)
            {
                double elapsedTicks = _playbackClock.Elapsed.Ticks % (double)_playbackDuration.Ticks;
                int targetIndex = FindFrameIndex(TimeSpan.FromTicks((long)elapsedTicks));
                if (targetIndex != _frameIndex)
                {
                    _frameIndex = targetIndex;
                    targetFrame = _frames[_frameIndex];
                }
            }
        }

        if (targetFrame is not null)
        {
            _onFrameUpdated?.Invoke(targetFrame);
        }
    }

    private void StartPlaybackOnUi(int generation)
    {
        if (!_isPlaying || generation != _playGeneration || _playbackStarted) return;

        Bitmap? first = null;
        lock (_lock)
        {
            if (_frames.Count > 0) first = _frames[0];
        }

        if (first is null) return;
        _playbackStarted = true;
        _frameIndex = 0;
        _playbackClock.Restart();
        _onFrameUpdated?.Invoke(first);
        _timer.Start();
    }

    private void PrepareTimeline(TimeSpan[] timestamps)
    {
        int count = _frames.Count;
        if (count == 0) return;

        if (timestamps.Length >= count)
        {
            _frameTimestamps = timestamps[..count];
        }
        else
        {
            _frameTimestamps = new TimeSpan[count];
            for (int i = 0; i < count; i++)
            {
                _frameTimestamps[i] = TimeSpan.FromSeconds(i / 30d);
            }
        }

        var finalStep = _frameTimestamps.Length > 1
            ? _frameTimestamps[^1] - _frameTimestamps[^2]
            : TimeSpan.FromMilliseconds(33.333);
        if (finalStep <= TimeSpan.Zero || finalStep > TimeSpan.FromMilliseconds(250))
        {
            finalStep = TimeSpan.FromMilliseconds(33.333);
        }
        _playbackDuration = _frameTimestamps[^1] + finalStep;
    }

    private int FindFrameIndex(TimeSpan elapsed)
    {
        int low = 0;
        int high = Math.Min(_frameTimestamps.Length, _frames.Count) - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            if (_frameTimestamps[middle] <= elapsed) low = middle + 1;
            else high = middle - 1;
        }
        return Math.Clamp(high, 0, _frames.Count - 1);
    }

    private static async Task<TimeSpan[]> ProbeFrameTimestampsAsync(string videoPath, CancellationToken token)
    {
        string? ffprobePath = ToolLocator.Find("ffprobe.exe");
        if (string.IsNullOrEmpty(ffprobePath)) return [];

        try
        {
            using var probe = new Process();
            probe.StartInfo = new ProcessStartInfo
            {
                FileName = ffprobePath,
                Arguments = $"-v error -select_streams v:0 -show_entries frame=best_effort_timestamp_time -of csv=p=0 \"{videoPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            if (!probe.Start()) return [];
            _ = probe.StandardError.ReadToEndAsync();
            string value = await probe.StandardOutput.ReadToEndAsync(token);
            await probe.WaitForExitAsync(token);

            var timestamps = new List<double>();
            foreach (var line in value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var text = line.Trim().TrimEnd(',');
                if (double.TryParse(text, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var timestamp))
                {
                    timestamps.Add(timestamp);
                }
            }

            if (timestamps.Count >= 2)
            {
                double origin = timestamps[0];
                var timeline = new TimeSpan[timestamps.Count];
                for (int i = 0; i < timestamps.Count; i++)
                {
                    timeline[i] = TimeSpan.FromSeconds(Math.Max(0, timestamps[i] - origin));
                }
                return timeline;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // 没有 ffprobe 或元数据异常时回退到常见实况帧率。
        }

        return [];
    }

    private void QueueDispose(List<Bitmap> frames)
    {
        if (frames.Count == 0) return;
        _pendingDisposals.AddRange(frames);
        _disposeTimer.Stop();
        _disposeTimer.Start();
    }

    private void FlushPendingDisposals()
    {
        List<Bitmap> pending;
        lock (_lock)
        {
            _disposeTimer.Stop();
            pending = [.. _pendingDisposals];
            _pendingDisposals.Clear();
        }

        foreach (var frame in pending)
        {
            frame.Dispose();
        }
    }

    public void Dispose()
    {
        Stop();
        FlushPendingDisposals();
    }
}
