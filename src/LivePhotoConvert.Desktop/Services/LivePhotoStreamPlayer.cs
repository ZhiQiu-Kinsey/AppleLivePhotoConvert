using System.Diagnostics;
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
    private readonly DispatcherTimer _timer;
    private readonly List<Bitmap> _frames = [];
    private readonly Lock _lock = new();

    private CancellationTokenSource? _cts;
    private Process? _activeProcess;
    private int _frameIndex;
    private bool _isPlaying;
    private Action<Bitmap>? _onFrameUpdated;

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
            Interval = TimeSpan.FromMilliseconds(33) // ~30 FPS
        };
        _timer.Tick += OnTimerTick;
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

        _isPlaying = true;
        _frameIndex = 0;

        Task.Run(() => DecodeStreamAsync(videoPath, token), token);
    }

    public void Pause()
    {
        _isPlaying = false;
        _timer.Stop();
    }

    public void Resume()
    {
        if (FrameCount > 0)
        {
            _isPlaying = true;
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
        _isPlaying = false;
        _timer.Stop();

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
            foreach (var frame in _frames)
            {
                frame.Dispose();
            }
            _frames.Clear();
            _frameIndex = 0;
        }
    }

    private async Task DecodeStreamAsync(string videoPath, CancellationToken token)
    {
        string? ffmpegPath = ToolLocator.Find(FfmpegVideoConverter.ExecutableName);
        if (string.IsNullOrEmpty(ffmpegPath) || !File.Exists(ffmpegPath))
        {
            return;
        }

        Process? process = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-v error -i \"{videoPath}\" -vf \"scale=960:-2\" -c:v mjpeg -q:v 3 -f image2pipe -",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            process = Process.Start(psi);
            if (process is null) return;
            _activeProcess = process;

            using var stdout = process.StandardOutput.BaseStream;
            using var ms = new MemoryStream();
            byte[] buffer = new byte[65536];
            int bytesRead;
            byte prev = 0;
            bool inFrame = false;

            while (!token.IsCancellationRequested &&
                   (bytesRead = await stdout.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
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
                            // 完整提取一帧 JPEG 点阵
                            byte[] frameData = ms.ToArray();
                            ms.SetLength(0);
                            inFrame = false;

                            using var frameMs = new MemoryStream(frameData);
                            var bmp = new Bitmap(frameMs);

                            bool isFirst;
                            lock (_lock)
                            {
                                _frames.Add(bmp);
                                isFirst = _frames.Count == 1;
                            }

                            if (isFirst)
                            {
                                Dispatcher.UIThread.Post(() =>
                                {
                                    if (_isPlaying)
                                    {
                                        _onFrameUpdated?.Invoke(bmp);
                                        _timer.Start();
                                    }
                                });
                            }
                        }
                    }
                    prev = b;
                }
            }

            if (!token.IsCancellationRequested)
            {
                await process.WaitForExitAsync(token);
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
            if (_frames.Count > 0)
            {
                _frameIndex = (_frameIndex + 1) % _frames.Count;
                targetFrame = _frames[_frameIndex];
            }
        }

        if (targetFrame is not null)
        {
            _onFrameUpdated?.Invoke(targetFrame);
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
