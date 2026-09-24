using System.Diagnostics;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using LivePhotoConvert.Desktop.Features.Playback;
using LivePhotoConvert.Desktop.Tests.Harness;

namespace LivePhotoConvert.Desktop.Tests.Features.Playback;

/// <summary>播放器：状态、按 PTS 换帧、双缓冲、停止与切换时的进程生命周期。</summary>
/// <remarks>用例在界面线程上等待真实解码数秒，与其它界面用例并行时会交错重建 Avalonia 应用，故不并行。</remarks>
[Collection(ProcessStateCollection.Name)]
public class LivePhotoPlayerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [AvaloniaFact]
    public async Task Play_MissingFfmpeg_ReportsFfmpegNotFound()
    {
        using var sandbox = new TestSandbox();
        var clip = sandbox.CreateInputFile("a.mov", [1, 2, 3]);
        using var player = new LivePhotoPlayer(new ManualFrameScheduler(), () => null);

        await player.PlayAsync(new VideoSource(clip, 0, 3, false), new PixelSize(100, 100), 1, PlaybackBudget.Hover);

        Assert.Equal(PlayerState.Failed(PlaybackError.FfmpegNotFound), player.State);
        Assert.Null(player.Surface);
    }

    [AvaloniaFact]
    public async Task Play_MissingSource_ReportsSourceNotFound()
    {
        using var player = new LivePhotoPlayer(new ManualFrameScheduler(), () => new PlaybackTools("/not/used/ffmpeg"));
        var states = new List<PlayerStatus>();
        player.StateChanged += (_, _) => states.Add(player.State.Status);

        await player.PlayAsync(new VideoSource(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mov"), 0, 0, false), new PixelSize(100, 100), 1, PlaybackBudget.Hover);

        Assert.Equal(PlaybackError.SourceNotFound, player.State.Error);
        Assert.Equal([PlayerStatus.Loading, PlayerStatus.Error], states);
    }

    [AvaloniaFact]
    public async Task Play_HdrWithoutZscale_ReportsToneMapUnavailableWithoutDecoding()
    {
        var ffmpeg = await PlaybackSamples.RequireHdrToolchainAsync();
        using var sandbox = new TestSandbox();
        var clip = Path.Combine(sandbox.InputDirectory, "hlg.mov");
        await PlaybackSamples.HlgAsync(ffmpeg, clip);
        var scheduler = new ManualFrameScheduler();
        using var player = new LivePhotoPlayer(scheduler, () => new PlaybackTools(ffmpeg, new HashSet<string> { "scale", "showinfo", "tonemap" }));

        await player.PlayAsync(new VideoSource(clip, 0, 0, false), new PixelSize(100, 100), 1, PlaybackBudget.Hover);

        Assert.Equal(PlaybackError.HdrToneMapUnavailable, player.State.Error);
        Assert.Null(player.DecoderProcessId);
        Assert.Equal(0, scheduler.Pending);
    }

    [AvaloniaFact]
    public async Task Play_AdvancesByPtsThroughDoubleBufferedSurfaces()
    {
        var ffmpeg = PlaybackSamples.RequireFfmpeg();
        using var sandbox = new TestSandbox();
        var clip = Path.Combine(sandbox.InputDirectory, "clip.mov");
        await PlaybackSamples.SdrAsync(ffmpeg, clip);
        var scheduler = new ManualFrameScheduler();
        using var player = new LivePhotoPlayer(scheduler, () => new PlaybackTools(ffmpeg));
        var invalidations = 0;
        player.SurfaceInvalidated += (_, _) => invalidations++;

        var play = player.PlayAsync(new VideoSource(clip, 0, 0, false), new PixelSize(80, 60), 2.0, PlaybackBudget.Hover);
        await PumpAsync(scheduler, TimeSpan.Zero, () => play.IsCompleted);
        await play;

        Assert.Equal(PlayerStatus.Playing, player.State.Status);
        var first = player.Surface!;
        Assert.Equal(new PixelSize(160, 120), first.PixelSize);
        Assert.Equal(1, invalidations);

        // 同一帧的持续时间内不重绘
        scheduler.Tick(TimeSpan.FromMilliseconds(20));
        Assert.Same(first, player.Surface);
        Assert.Equal(1, invalidations);

        // 第二帧（PTS 33.3ms）写入后台位图后交换，前台位图保持不变
        var firstPixels = CopyPixels(first);
        await PumpAsync(scheduler, TimeSpan.FromMilliseconds(40), () => !ReferenceEquals(player.Surface, first));
        var second = player.Surface!;
        Assert.NotSame(first, second);
        Assert.Equal(firstPixels, CopyPixels(first));

        // 第三帧写回原来的位图，此时正在显示的第二帧不被改写
        var secondPixels = CopyPixels(second);
        await PumpAsync(scheduler, TimeSpan.FromMilliseconds(70), () => !ReferenceEquals(player.Surface, second));
        Assert.Same(first, player.Surface);
        Assert.Equal(secondPixels, CopyPixels(second));
        Assert.NotEqual(firstPixels, CopyPixels(first));

        // 小尺寸整段放得进悬浮预算：解码一轮后全缓存循环，FFmpeg 随即退出
        await WaitAsync(() => player.Store is { IsFullyCached: true });
        Assert.Equal(60, player.Store!.Count);
        Assert.InRange(player.Store.LoopDuration!.Value, TimeSpan.FromMilliseconds(1_999), TimeSpan.FromMilliseconds(2_001));
        scheduler.Tick(TimeSpan.FromMilliseconds(2_000 + 5));
        Assert.Equal(firstPixels, CopyPixels(player.Surface!));
    }

    [AvaloniaFact]
    public async Task Play_OverBudget_StreamsAndLoopsIntoNextPass()
    {
        var ffmpeg = PlaybackSamples.RequireFfmpeg();
        using var sandbox = new TestSandbox();
        var clip = Path.Combine(sandbox.InputDirectory, "clip.mov");
        await PlaybackSamples.SdrAsync(ffmpeg, clip, seconds: 1);
        var scheduler = new ManualFrameScheduler();
        using var player = new LivePhotoPlayer(scheduler, () => new PlaybackTools(ffmpeg));
        // 160×120 帧 76,800 字节：预算只够 2 张显示位图 + 4 帧
        var budget = new PlaybackBudget(160 * 120 * 4 * 6);

        var play = player.PlayAsync(new VideoSource(clip, 0, 0, false), new PixelSize(160, 120), 1.0, budget);
        await PumpAsync(scheduler, TimeSpan.Zero, () => play.IsCompleted);
        Assert.Equal(4, player.Store!.Capacity);

        var invalidations = 0;
        player.SurfaceInvalidated += (_, _) => invalidations++;
        var now = TimeSpan.Zero;
        var deadline = Stopwatch.StartNew();
        // 一轮 30 帧；换帧超过 35 次说明已无缝进入第二轮
        while (invalidations <= 35)
        {
            Assert.True(deadline.Elapsed < Timeout, "等待循环播放超时");
            now += TimeSpan.FromMilliseconds(10);
            scheduler.Tick(now);
            Assert.InRange(player.Store!.Count, 0, 4);
            await Task.Delay(2);
        }

        Assert.True(player.Store!.IsStreaming);
        Assert.True(player.Store.Pass >= 1);
        Assert.Equal(PlayerStatus.Playing, player.State.Status);
    }

    [AvaloniaFact]
    public async Task Stop_EndsFfmpegAndReleasesSurface()
    {
        var ffmpeg = PlaybackSamples.RequireFfmpeg();
        using var sandbox = new TestSandbox();
        var clip = Path.Combine(sandbox.InputDirectory, "long.mov");
        await PlaybackSamples.SdrAsync(ffmpeg, clip, seconds: 10, size: "640x480");
        var scheduler = new ManualFrameScheduler();
        using var player = new LivePhotoPlayer(scheduler, () => new PlaybackTools(ffmpeg));

        var play = player.PlayAsync(new VideoSource(clip, 0, 0, false), new PixelSize(640, 480), 1.0, new PlaybackBudget(640 * 480 * 4 * 6));
        await PumpAsync(scheduler, TimeSpan.Zero, () => play.IsCompleted);
        var processId = player.DecoderProcessId!.Value;
        var store = player.Store!;
        var surface = player.Surface!;
        var invalidated = false;
        player.SurfaceInvalidated += (_, _) => invalidated = player.Surface is null;

        player.Stop();

        Assert.Equal(PlayerState.Idle, player.State);
        Assert.Null(player.Surface);
        Assert.True(invalidated);
        Assert.Equal(0, store.Count);
        // 停止后界面可能还在引用旧位图：同步阶段不释放
        using (surface.Lock())
        {
        }

        await player.StopAsync();
        Assert.True(await PlaybackSamples.WaitGoneAsync(processId, TimeSpan.FromSeconds(5)));
        // 停止前已登记的刷新回调不得再动界面，也不再续订
        scheduler.Tick(TimeSpan.FromSeconds(1));
        Assert.Null(player.Surface);
        Assert.Equal(0, scheduler.Pending);
    }

    [AvaloniaFact]
    public async Task Play_Again_StopsPreviousFfmpegFirst()
    {
        var ffmpeg = PlaybackSamples.RequireFfmpeg();
        using var sandbox = new TestSandbox();
        var clip = Path.Combine(sandbox.InputDirectory, "long.mov");
        await PlaybackSamples.SdrAsync(ffmpeg, clip, seconds: 10, size: "640x480");
        var scheduler = new ManualFrameScheduler();
        using var player = new LivePhotoPlayer(scheduler, () => new PlaybackTools(ffmpeg));
        var budget = new PlaybackBudget(640 * 480 * 4 * 6);
        var source = new VideoSource(clip, 0, 0, false);

        var first = player.PlayAsync(source, new PixelSize(640, 480), 1.0, budget);
        await PumpAsync(scheduler, TimeSpan.Zero, () => first.IsCompleted);
        var firstProcess = player.DecoderProcessId!.Value;

        var second = player.PlayAsync(source, new PixelSize(320, 240), 1.0, budget);
        await PumpAsync(scheduler, TimeSpan.Zero, () => second.IsCompleted);

        Assert.True(PlaybackSamples.IsGone(firstProcess));
        Assert.Equal(PlayerStatus.Playing, player.State.Status);
        Assert.Equal(new PixelSize(320, 240), player.Surface!.PixelSize);
        Assert.NotEqual(firstProcess, player.DecoderProcessId);
        await player.StopAsync();
    }

    [AvaloniaFact]
    public async Task Play_CanceledToken_StopsPlayback()
    {
        var ffmpeg = PlaybackSamples.RequireFfmpeg();
        using var sandbox = new TestSandbox();
        var clip = Path.Combine(sandbox.InputDirectory, "long.mov");
        await PlaybackSamples.SdrAsync(ffmpeg, clip, seconds: 10, size: "640x480");
        var scheduler = new ManualFrameScheduler();
        using var player = new LivePhotoPlayer(scheduler, () => new PlaybackTools(ffmpeg));
        using var cancellation = new CancellationTokenSource();

        var play = player.PlayAsync(new VideoSource(clip, 0, 0, false), new PixelSize(640, 480), 1.0, new PlaybackBudget(640 * 480 * 4 * 6), cancellation.Token);
        await PumpAsync(scheduler, TimeSpan.Zero, () => play.IsCompleted);
        var processId = player.DecoderProcessId!.Value;

        await cancellation.CancelAsync();
        await WaitAsync(() => player.State == PlayerState.Idle);

        Assert.Null(player.Surface);
        Assert.True(await PlaybackSamples.WaitGoneAsync(processId, TimeSpan.FromSeconds(5)));
    }

    private static async Task PumpAsync(ManualFrameScheduler scheduler, TimeSpan now, Func<bool> done)
    {
        var deadline = Stopwatch.StartNew();
        while (!done())
        {
            Assert.True(deadline.Elapsed < Timeout, "等待播放器超时");
            scheduler.Tick(now);
            await Task.Delay(5);
        }
    }

    private static async Task WaitAsync(Func<bool> done)
    {
        var deadline = Stopwatch.StartNew();
        while (!done())
        {
            Assert.True(deadline.Elapsed < Timeout, "等待播放器超时");
            await Task.Delay(5);
        }
    }

    private static byte[] CopyPixels(WriteableBitmap bitmap)
    {
        using var buffer = bitmap.Lock();
        var bytes = new byte[buffer.RowBytes * buffer.Size.Height];
        System.Runtime.InteropServices.Marshal.Copy(buffer.Address, bytes, 0, bytes.Length);
        return bytes;
    }
}
