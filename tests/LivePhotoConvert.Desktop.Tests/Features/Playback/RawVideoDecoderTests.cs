using Avalonia;
using LivePhotoConvert.Core.Media;
using LivePhotoConvert.Core.Tests.Support;
using LivePhotoConvert.Desktop.Features.Playback;
using LivePhotoConvert.Desktop.Tests.Harness;

namespace LivePhotoConvert.Desktop.Tests.Features.Playback;

/// <summary>真实 FFmpeg 解码：帧数、时间戳、HDR 色调映射、subfile 与旋转。</summary>
public class RawVideoDecoderTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Decode_ConstantFrameRate_YieldsEveryFrameWithMonotonicPts()
    {
        var ffmpeg = PlaybackSamples.RequireFfmpeg();
        using var sandbox = new TestSandbox();
        var clip = Path.Combine(sandbox.InputDirectory, "clip 30fps.mp4");
        await PlaybackSamples.SdrAsync(ffmpeg, clip);
        var input = new VideoSource(clip, 0, new FileInfo(clip).Length, false).ToFfmpegInput();
        var info = await PlaybackSamples.ProbeAsync(ffmpeg, input);
        var size = PlaybackGeometry.ComputeOutputSize(info.DisplayWidth, info.DisplayHeight, new PixelSize(80, 60), 2.0);

        var (timings, first, exitCode) = await PlaybackSamples.DecodeAllAsync(ffmpeg, input, info, size);

        Assert.Equal(new PixelSize(160, 120), size);
        Assert.Equal(160 * 120 * 4, first.Length);
        Assert.Equal(0, exitCode);
        Assert.Equal(60, timings.Count);
        for (var i = 0; i < timings.Count; i++)
        {
            Assert.InRange((timings[i].Pts - TimeSpan.FromSeconds(i / 30.0)).Duration(), TimeSpan.Zero, TimeSpan.FromMilliseconds(1));
            Assert.InRange((timings[i].Duration - TimeSpan.FromSeconds(1 / 30.0)).Duration(), TimeSpan.Zero, TimeSpan.FromMilliseconds(1));
        }
    }

    [Fact]
    public async Task Decode_VariableFrameRate_KeepsSourceTimestamps()
    {
        var ffmpeg = PlaybackSamples.RequireFfmpeg();
        using var sandbox = new TestSandbox();
        var clip = Path.Combine(sandbox.InputDirectory, "vfr.mp4");
        await PlaybackSamples.VariableFrameRateAsync(ffmpeg, clip);
        var input = new VideoSource(clip, 0, 0, false).ToFfmpegInput();
        var info = await PlaybackSamples.ProbeAsync(ffmpeg, input);

        var (timings, _, _) = await PlaybackSamples.DecodeAllAsync(ffmpeg, input, info, new PixelSize(160, 120));

        // 保留的源帧序号 n 满足 n % 3 != 2，显示时刻为 n / 60 秒
        var expected = Enumerable.Range(0, 120).Where(n => n % 3 != 2).Select(n => TimeSpan.FromSeconds(n / 60.0)).ToList();
        Assert.Equal(expected.Count, timings.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.InRange((timings[i].Pts - expected[i]).Duration(), TimeSpan.Zero, TimeSpan.FromMilliseconds(1));
        }

        Assert.All(timings.Zip(timings.Skip(1)), pair => Assert.True(pair.Second.Pts > pair.First.Pts));
    }

    [Fact]
    public async Task Decode_Hlg_ToneMapsInsteadOfWashingOut()
    {
        var ffmpeg = await PlaybackSamples.RequireHdrToolchainAsync();
        using var sandbox = new TestSandbox();
        var hlg = Path.Combine(sandbox.InputDirectory, "hlg.mp4");
        var sdr = Path.Combine(sandbox.InputDirectory, "sdr.mp4");
        await PlaybackSamples.HlgAsync(ffmpeg, hlg);
        await PlaybackSamples.SdrAsync(ffmpeg, sdr, seconds: 1);
        var hlgInput = new VideoSource(hlg, 0, 0, false).ToFfmpegInput();
        var sdrInput = new VideoSource(sdr, 0, 0, false).ToFfmpegInput();
        var hlgInfo = await PlaybackSamples.ProbeAsync(ffmpeg, hlgInput);
        var sdrInfo = await PlaybackSamples.ProbeAsync(ffmpeg, sdrInput);
        var size = new PixelSize(160, 120);
        Assert.True(hlgInfo.IsHdr);

        var (_, toneMapped, _) = await PlaybackSamples.DecodeAllAsync(ffmpeg, hlgInput, hlgInfo, size);
        // 对照：不做色调映射直接当 SDR 显示
        var (_, naive, _) = await PlaybackSamples.DecodeAllAsync(ffmpeg, hlgInput, hlgInfo with { ColorTransfer = null }, size);
        var (_, reference, _) = await PlaybackSamples.DecodeAllAsync(ffmpeg, sdrInput, sdrInfo, size);

        var mapped = PlaybackSamples.Stats(toneMapped);
        var washed = PlaybackSamples.Stats(naive);
        var original = PlaybackSamples.Stats(reference);
        Assert.True(washed.Spread < original.Spread * 0.5, $"对照样片应明显发灰：{washed} vs {original}");
        Assert.True(mapped.Spread > original.Spread * 0.7, $"色调映射后饱和度不足：{mapped} vs {original}");
        Assert.InRange(mapped.Luma, original.Luma * 0.7, original.Luma * 1.15);
    }

    [Fact]
    public async Task Decode_FullAndLimitedRange10Bit_ProduceSameGray()
    {
        var ffmpeg = PlaybackSamples.RequireFfmpeg();
        using var sandbox = new TestSandbox();
        var full = Path.Combine(sandbox.InputDirectory, "full.mp4");
        var limited = Path.Combine(sandbox.InputDirectory, "limited.mp4");
        try
        {
            await PlaybackSamples.GrayAsync(ffmpeg, full, fullRange: true);
            await PlaybackSamples.GrayAsync(ffmpeg, limited, fullRange: false);
        }
        catch (Xunit.Sdk.XunitException) when (!File.Exists(full) || !File.Exists(limited))
        {
            Assert.Skip("本机 FFmpeg 无法生成 10-bit HEVC 样片。");
        }

        var fullInfo = await PlaybackSamples.ProbeAsync(ffmpeg, full);
        var limitedInfo = await PlaybackSamples.ProbeAsync(ffmpeg, limited);
        Assert.Equal("pc", fullInfo.ColorRange);

        var (_, fullFrame, _) = await PlaybackSamples.DecodeAllAsync(ffmpeg, full, fullInfo, new PixelSize(160, 120));
        var (_, limitedFrame, _) = await PlaybackSamples.DecodeAllAsync(ffmpeg, limited, limitedInfo, new PixelSize(160, 120));
        // 误当有限范围解释全范围会把 0x40 拉暗到约 0x36
        var (_, misread, _) = await PlaybackSamples.DecodeAllAsync(ffmpeg, full, fullInfo with { ColorRange = "tv" }, new PixelSize(160, 120));

        Assert.InRange(fullFrame[0], 0x3C, 0x42);
        Assert.InRange(Math.Abs(fullFrame[0] - limitedFrame[0]), 0, 2);
        Assert.True(Math.Abs(misread[0] - fullFrame[0]) >= 6, $"对照应偏暗：{misread[0]} vs {fullFrame[0]}");
    }

    [Fact]
    public async Task Decode_EmbeddedAfterJpeg_ReadsViaSubfile()
    {
        var ffmpeg = PlaybackSamples.RequireFfmpeg();
        using var sandbox = new TestSandbox();
        var clip = Path.Combine(sandbox.InputDirectory, "clip.mp4");
        await PlaybackSamples.SdrAsync(ffmpeg, clip);
        var jpeg = SyntheticMedia.Jpeg(payloadBytes: 5000);
        var video = await File.ReadAllBytesAsync(clip, Token);
        var name = OperatingSystem.IsWindows() ? "动态 照片,1.jpg" : "动态 照片,1:2.jpg";
        var motionPhoto = sandbox.CreateInputFile(Path.Combine("相册 a,b", name), [.. jpeg, .. video]);
        var embedded = new VideoSource(motionPhoto, jpeg.Length, video.Length, true).ToFfmpegInput();
        var info = await PlaybackSamples.ProbeAsync(ffmpeg, embedded);

        var (timings, first, _) = await PlaybackSamples.DecodeAllAsync(ffmpeg, embedded, info, new PixelSize(160, 120));
        var (_, standalone, _) = await PlaybackSamples.DecodeAllAsync(ffmpeg, clip, info, new PixelSize(160, 120));

        Assert.Equal(60, timings.Count);
        Assert.Equal(standalone, first);
    }

    [Fact]
    public async Task Decode_RotatedClip_OutputsDisplayOrientation()
    {
        var ffmpeg = PlaybackSamples.RequireFfmpeg();
        using var sandbox = new TestSandbox();
        var clip = Path.Combine(sandbox.InputDirectory, "clip.mp4");
        var rotated = Path.Combine(sandbox.InputDirectory, "rotated.mp4");
        await PlaybackSamples.SdrAsync(ffmpeg, clip, seconds: 0.5);
        await PlaybackSamples.RotateAsync(ffmpeg, clip, rotated);
        var info = await PlaybackSamples.ProbeAsync(ffmpeg, rotated);
        var plainInfo = await PlaybackSamples.ProbeAsync(ffmpeg, clip);
        var size = PlaybackGeometry.ComputeOutputSize(info.DisplayWidth, info.DisplayHeight, new PixelSize(120, 160), 1.0);

        var (_, rotatedFrame, _) = await PlaybackSamples.DecodeAllAsync(ffmpeg, rotated, info, size);
        var (_, plainFrame, _) = await PlaybackSamples.DecodeAllAsync(ffmpeg, clip, plainInfo, new PixelSize(160, 120));

        Assert.Equal(270, info.Rotation);
        Assert.Equal(new PixelSize(120, 160), size);
        // 显示矩阵逆时针 90°：显示坐标 (x, y) 对应原画面 (W-1-y, x)
        double difference = 0;
        for (var y = 0; y < 160; y++)
        {
            for (var x = 0; x < 120; x++)
            {
                var shown = ((y * 120) + x) * 4;
                var source = ((x * 160) + (159 - y)) * 4;
                for (var c = 0; c < 3; c++)
                {
                    difference += Math.Abs(rotatedFrame[shown + c] - plainFrame[source + c]);
                }
            }
        }

        Assert.True(difference / (120 * 160 * 3) < 4, $"方向不符，平均差 {difference / (120 * 160 * 3):F1}");
    }

    [Fact]
    public async Task Dispose_WhileDecoding_EndsProcess()
    {
        var ffmpeg = PlaybackSamples.RequireFfmpeg();
        using var sandbox = new TestSandbox();
        var clip = Path.Combine(sandbox.InputDirectory, "long.mp4");
        await PlaybackSamples.SdrAsync(ffmpeg, clip, seconds: 10, size: "640x480");
        var info = await PlaybackSamples.ProbeAsync(ffmpeg, clip);
        var decoder = RawVideoDecoder.Start(ffmpeg, clip, info, new PixelSize(640, 480));
        var buffer = new byte[decoder.FrameBytes];
        Assert.NotNull(await decoder.ReadFrameAsync(buffer, Token));
        Assert.False(decoder.HasExited);

        // 不再读取：FFmpeg 阻塞在写满的管道上，必须由 Dispose 结束
        await decoder.DisposeAsync();

        Assert.True(PlaybackSamples.IsGone(decoder.ProcessId));
    }

    [Fact]
    public async Task ReadFrame_Canceled_ThrowsAndDisposeStillEndsProcess()
    {
        var ffmpeg = PlaybackSamples.RequireFfmpeg();
        using var sandbox = new TestSandbox();
        var clip = Path.Combine(sandbox.InputDirectory, "long.mp4");
        await PlaybackSamples.SdrAsync(ffmpeg, clip, seconds: 10, size: "640x480");
        var info = await PlaybackSamples.ProbeAsync(ffmpeg, clip);
        await using var decoder = RawVideoDecoder.Start(ffmpeg, clip, info, new PixelSize(640, 480));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await decoder.ReadFrameAsync(new byte[decoder.FrameBytes], cancellation.Token));
        decoder.Kill();

        Assert.True(await PlaybackSamples.WaitGoneAsync(decoder.ProcessId, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Decode_MissingInput_ReportsError()
    {
        var ffmpeg = PlaybackSamples.RequireFfmpeg();
        var info = new Core.External.VideoStreamInfo { Codec = "h264", Width = 320, Height = 240 };
        await using var decoder = RawVideoDecoder.Start(ffmpeg, Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.mp4"), info, new PixelSize(160, 120));

        Assert.Null(await decoder.ReadFrameAsync(new byte[decoder.FrameBytes], Token));
        Assert.NotEqual(0, await decoder.WaitForExitAsync(Token));
        Assert.Contains("No such file", decoder.ErrorSummary, StringComparison.OrdinalIgnoreCase);
    }
}
