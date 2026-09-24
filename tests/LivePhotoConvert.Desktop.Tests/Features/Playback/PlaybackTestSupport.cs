using System.Diagnostics;
using Avalonia;
using LivePhotoConvert.Core.External;
using LivePhotoConvert.Desktop.Features.Playback;

namespace LivePhotoConvert.Desktop.Tests.Features.Playback;

/// <summary>可控时钟：测试决定何时、以什么时间戳触发刷新回调。</summary>
internal sealed class ManualFrameScheduler : IFrameScheduler
{
    private List<Action<TimeSpan>> _pending = [];

    public int Pending => _pending.Count;

    public void RequestFrame(Action<TimeSpan> callback) => _pending.Add(callback);

    public void Tick(TimeSpan now)
    {
        var batch = _pending;
        _pending = [];
        foreach (var callback in batch)
        {
            callback(now);
        }
    }
}

/// <summary>用本机 FFmpeg 的 lavfi 测试源生成样片；缺少 FFmpeg、libx265 或 zscale 时跳过。</summary>
internal static class PlaybackSamples
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static string RequireFfmpeg() =>
        ToolLocator.Find(FfmpegVideoConverter.ExecutableName) ?? Skip("未安装 FFmpeg，跳过集成测试。");

    /// <summary>生成与解码 HDR 样片需要 libx265、zscale 与 tonemap。</summary>
    public static async Task<string> RequireHdrToolchainAsync()
    {
        var ffmpeg = RequireFfmpeg();
        var encoders = await ProcessRunner.RunAsync(ffmpeg, ["-nostdin", "-hide_banner", "-encoders"], Token);
        if (!encoders.StandardOutput.Contains(" libx265 ", StringComparison.Ordinal))
        {
            Skip("本机 FFmpeg 不带 libx265，跳过 HDR 集成测试。");
        }

        if (!FfmpegFilters.SupportsHdrToneMapping(await FfmpegFilters.GetAsync(ffmpeg, Token)))
        {
            Skip("本机 FFmpeg 不带 zscale/tonemap，跳过 HDR 集成测试。");
        }

        return ffmpeg;
    }

    /// <summary>testsrc2 的 H.264 片段（BT.709 有限范围）。</summary>
    public static Task SdrAsync(string ffmpeg, string path, double seconds = 2, int rate = 30, string size = "320x240") =>
        RunAsync(ffmpeg,
        [
            "-f", "lavfi", "-i", $"testsrc2=size={size}:rate={rate}", "-t", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p", "-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709", path
        ]);

    /// <summary>
    /// 可变帧率：60fps 源每 3 帧丢 1 帧，帧间隔在 1/60 与 2/60 秒间交替，2 秒共 80 帧。
    /// </summary>
    public static Task VariableFrameRateAsync(string ffmpeg, string path) =>
        RunAsync(ffmpeg,
        [
            "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=60", "-t", "2",
            "-vf", @"select='not(eq(mod(n\,3)\,2))'", "-fps_mode", "vfr",
            "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p", path
        ]);

    /// <summary>
    /// 真正的 HLG 片段：把 testsrc2 按参考白 203 cd/m² 放进 BT.2020 HLG，未做色调映射直接显示会明显发灰。
    /// </summary>
    public static Task HlgAsync(string ffmpeg, string path) =>
        RunAsync(ffmpeg,
        [
            "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=30", "-t", "1",
            "-vf", "format=gbrp,zscale=tin=bt709:pin=bt709:min=gbr:rin=pc:t=linear:p=bt709:npl=203,format=gbrpf32le,"
                   + "zscale=tin=linear:pin=bt709:t=linear:p=bt2020,zscale=tin=linear:pin=bt2020:p=bt2020:t=arib-std-b67:m=bt2020nc:r=tv:npl=203,format=yuv420p10le",
            "-c:v", "libx265", "-preset", "veryfast", "-pix_fmt", "yuv420p10le", "-x265-params", "log-level=error",
            "-color_primaries", "bt2020", "-color_trc", "arib-std-b67", "-colorspace", "bt2020nc", "-tag:v", "hvc1", path
        ]);

    /// <summary>10-bit HEVC 纯灰画面；10-bit 没有 yuvj 格式，范围只能靠标注区分。</summary>
    public static Task GrayAsync(string ffmpeg, string path, bool fullRange) =>
        RunAsync(ffmpeg,
        [
            "-f", "lavfi", "-i", "color=c=0x404040:s=320x240:r=30", "-t", "0.5",
            "-vf", $"scale=out_range={(fullRange ? "pc" : "tv")}:out_color_matrix=bt709,format=yuv420p10le",
            "-c:v", "libx265", "-preset", "veryfast", "-pix_fmt", "yuv420p10le", "-x265-params", $"log-level=error:range={(fullRange ? "full" : "limited")}",
            "-color_range", fullRange ? "pc" : "tv", "-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709", path
        ]);

    /// <summary>流复制并写入显示矩阵（逆时针 90°）。</summary>
    public static async Task RotateAsync(string ffmpeg, string source, string destination)
    {
        var rotated = await ProcessRunner.RunAsync(ffmpeg,
            ["-nostdin", "-loglevel", "error", "-y", "-display_rotation", "90", "-i", source, "-c", "copy", destination], Token);
        if (!rotated.Success)
        {
            // FFmpeg 6.1 之前没有 -display_rotation，旧的 rotate 标签是顺时针角度
            rotated = await ProcessRunner.RunAsync(ffmpeg,
                ["-nostdin", "-loglevel", "error", "-y", "-i", source, "-c", "copy", "-metadata:s:v:0", "rotate=270", destination], Token);
        }

        Assert.True(rotated.Success, rotated.StandardError);
    }

    public static async Task<VideoStreamInfo> ProbeAsync(string ffmpeg, string input) =>
        await VideoStreamProbe.ProbeAsync(ffmpeg, input, cancellationToken: Token)
        ?? throw new Xunit.Sdk.XunitException($"{input} 探测不到视频流");

    /// <summary>解码全部帧，返回时间戳与首帧像素。</summary>
    public static async Task<(List<FrameTiming> Timings, byte[] FirstFrame, int ExitCode)> DecodeAllAsync(string ffmpeg, string input, VideoStreamInfo info, PixelSize size)
    {
        await using var decoder = RawVideoDecoder.Start(ffmpeg, input, info, size);
        var buffer = new byte[decoder.FrameBytes];
        byte[]? first = null;
        List<FrameTiming> timings = [];
        while (await decoder.ReadFrameAsync(buffer, Token) is { } timing)
        {
            first ??= [.. buffer];
            timings.Add(timing);
        }

        var exitCode = await decoder.WaitForExitAsync(Token);
        Assert.True(first is not null, decoder.ErrorSummary);
        return (timings, first, exitCode);
    }

    /// <summary>BGRA 帧的平均亮度（BT.709 权重）与平均饱和跨度（max-min），发灰的画面跨度明显偏小。</summary>
    public static (double Luma, double Spread) Stats(ReadOnlySpan<byte> bgra)
    {
        double luma = 0, spread = 0;
        var pixels = bgra.Length / 4;
        for (var i = 0; i < bgra.Length; i += 4)
        {
            int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
            luma += (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
            spread += Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
        }

        return (luma / pixels, spread / pixels);
    }

    /// <summary>进程已不存在或已退出。</summary>
    public static bool IsGone(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    public static async Task<bool> WaitGoneAsync(int processId, TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            if (IsGone(processId))
            {
                return true;
            }

            await Task.Delay(20, Token);
        }

        return IsGone(processId);
    }

    private static async Task RunAsync(string ffmpeg, string[] arguments)
    {
        var result = await ProcessRunner.RunAsync(ffmpeg, ["-nostdin", "-loglevel", "error", "-y", .. arguments], Token);
        Assert.True(result.Success, result.StandardError);
    }

    private static string Skip(string reason)
    {
        Assert.Skip(reason);
        return string.Empty;
    }
}
