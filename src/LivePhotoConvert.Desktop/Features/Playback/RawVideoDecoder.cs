using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Avalonia;
using LivePhotoConvert.Core.External;

namespace LivePhotoConvert.Desktop.Features.Playback;

/// <summary>一帧在本轮播放时间轴上的位置（首帧为 0）与持续时间。</summary>
public readonly record struct FrameTiming(TimeSpan Pts, TimeSpan Duration);

/// <summary>
/// 用 FFmpeg 把视频解码为 BGRA 原始帧流：像素从标准输出按固定帧长读取，时间戳从标准错误的 showinfo 解析。
/// </summary>
/// <remarks>
/// 两路输出由独立任务并发读取，任何一路写满管道都不会卡住另一路；
/// <see cref="Kill"/> 可从任意线程调用，结束整个进程树，阻塞中的读取随即因管道关闭而返回。
/// </remarks>
public sealed class RawVideoDecoder : IAsyncDisposable
{
    private const int ErrorTailLines = 12;
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(5);

    private readonly Process _process;
    private readonly Stream _output;
    private readonly Channel<ShowInfoFrame> _timings = Channel.CreateUnbounded<ShowInfoFrame>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly Queue<string> _errorTail = new();
    private readonly Lock _lock = new();
    private readonly Task _errorPump;
    private readonly TimeSpan _nominalFrameDuration;
    private TimeSpan? _origin;
    private TimeSpan _lastPts = TimeSpan.MinValue;
    private TimeSpan _lastDuration;
    private bool _killed;
    private bool _disposed;

    private RawVideoDecoder(Process process, PixelSize outputSize, TimeSpan nominalFrameDuration)
    {
        _process = process;
        _output = process.StandardOutput.BaseStream;
        OutputSize = outputSize;
        FrameBytes = checked(outputSize.Width * outputSize.Height * 4);
        _nominalFrameDuration = nominalFrameDuration;
        _lastDuration = nominalFrameDuration;
        ProcessId = process.Id;
        _errorPump = Task.Run(PumpErrorsAsync);
    }

    public PixelSize OutputSize { get; }

    /// <summary>每帧字节数（宽 × 高 × 4，无行填充）。</summary>
    public int FrameBytes { get; }

    public int ProcessId { get; }

    /// <summary>标准错误中最后的非时间戳输出，用于诊断失败原因。</summary>
    public string ErrorSummary
    {
        get
        {
            lock (_lock)
            {
                return string.Join('\n', _errorTail);
            }
        }
    }

    /// <summary>启动解码进程。</summary>
    /// <param name="ffmpegPath">ffmpeg 可执行文件</param>
    /// <param name="input">由 <see cref="VideoSourceExtensions.ToFfmpegInput"/> 生成的输入地址</param>
    /// <param name="source">源视频流信息，决定 SDR/HDR 滤镜链</param>
    /// <param name="outputSize">输出像素尺寸（显示方向）</param>
    /// <exception cref="System.ComponentModel.Win32Exception">无法执行 FFmpeg</exception>
    public static RawVideoDecoder Start(string ffmpegPath, string input, VideoStreamInfo source, PixelSize outputSize)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in BuildArguments(input, source, outputSize))
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"无法启动 {ffmpegPath}。");
        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // 进程已立即退出，后续读取会得到结束
        }

        var nominal = source.FrameRate is > 0 and < 1000 ? TimeSpan.FromSeconds(1 / source.FrameRate.Value) : TimeSpan.FromSeconds(1 / 30.0);
        return new RawVideoDecoder(process, outputSize, nominal);
    }

    public static IReadOnlyList<string> BuildArguments(string input, VideoStreamInfo source, PixelSize outputSize) =>
    [
        // showinfo 按 info 级别输出，不能调低日志级别；-nostats 去掉以 \r 分隔、会与帧信息混在同一行的进度输出
        "-nostdin", "-hide_banner", "-nostats", "-loglevel", "info",
        "-i", input,
        "-map", "0:v:0", "-an", "-sn", "-dn",
        "-vf", DecodeFilterChain.Build(source, outputSize),
        "-fps_mode", "passthrough",
        "-f", "rawvideo", "-pix_fmt", "bgra",
        "-"
    ];

    /// <summary>
    /// 读取下一帧像素到 <paramref name="destination"/>（长度须为 <see cref="FrameBytes"/>）；读到结尾、不完整的尾帧或进程被结束时返回 null。
    /// </summary>
    public async ValueTask<FrameTiming?> ReadFrameAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        if (destination.Length != FrameBytes)
        {
            throw new ArgumentException($"缓冲区长度必须为 {FrameBytes}。", nameof(destination));
        }

        try
        {
            await _output.ReadExactlyAsync(destination, cancellationToken);
        }
        catch (EndOfStreamException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException && IsKilled)
        {
            return null;
        }

        // FFmpeg 先打印 showinfo 再写出该帧，通常已在队列中；标准错误提前结束时按帧率推算
        ShowInfoFrame? info = null;
        try
        {
            if (await _timings.Reader.WaitToReadAsync(cancellationToken) && _timings.Reader.TryRead(out var parsed))
            {
                info = parsed;
            }
        }
        catch (ChannelClosedException)
        {
        }

        return NextTiming(info);
    }

    /// <summary>等待进程退出并返回退出码。</summary>
    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        await _process.WaitForExitAsync(cancellationToken);
        // 退出后标准错误可能还有未读完的内容，读完再给出诊断信息
        await _errorPump.WaitAsync(ExitTimeout, cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        return _process.ExitCode;
    }

    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    private bool IsKilled
    {
        get
        {
            lock (_lock)
            {
                return _killed || _disposed;
            }
        }
    }

    /// <summary>立即结束 FFmpeg 进程树；可重复调用。</summary>
    public void Kill()
    {
        lock (_lock)
        {
            if (_killed || _disposed)
            {
                return;
            }

            _killed = true;
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // 进程已退出
            }
        }
    }

    /// <summary>结束进程（若仍在运行）并等待其退出，保证调用返回后不再有残留的 FFmpeg。</summary>
    public async ValueTask DisposeAsync()
    {
        if (!HasExited)
        {
            Kill();
        }

        await _process.WaitForExitAsync().WaitAsync(ExitTimeout).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await _errorPump.WaitAsync(ExitTimeout).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        lock (_lock)
        {
            _disposed = true;
        }

        _process.Dispose();
    }

    private FrameTiming NextTiming(ShowInfoFrame? info)
    {
        TimeSpan pts;
        if (info?.Pts is { } absolute)
        {
            _origin ??= absolute;
            pts = absolute - _origin.Value;
        }
        else
        {
            pts = _lastPts == TimeSpan.MinValue ? TimeSpan.Zero : _lastPts + _lastDuration;
        }

        // 播放时钟要求单调；乱序或重复的时间戳贴到上一帧之后
        if (_lastPts != TimeSpan.MinValue && pts <= _lastPts)
        {
            pts = _lastPts + TimeSpan.FromTicks(1);
        }

        var duration = info?.Duration ?? (_lastPts == TimeSpan.MinValue ? _nominalFrameDuration : _lastDuration);
        _lastPts = pts;
        _lastDuration = duration;
        return new FrameTiming(pts, duration);
    }

    private async Task PumpErrorsAsync()
    {
        var parser = new ShowInfoParser();
        try
        {
            var reader = _process.StandardError;
            while (await reader.ReadLineAsync() is { } line)
            {
                if (parser.TryParse(line, out var frame))
                {
                    _timings.Writer.TryWrite(frame);
                }
                else if (line.Length > 0 && !line.Contains("Parsed_showinfo", StringComparison.Ordinal) && !line.StartsWith(' '))
                {
                    lock (_lock)
                    {
                        if (_errorTail.Count == ErrorTailLines)
                        {
                            _errorTail.Dequeue();
                        }

                        _errorTail.Enqueue(line.Length > 400 ? line[..400] : line);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // 进程被结束时管道随之关闭
        }
        finally
        {
            _timings.Writer.TryComplete();
        }
    }
}
