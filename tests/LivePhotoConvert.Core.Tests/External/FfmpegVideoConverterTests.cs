using LivePhotoConvert.Core.Abstractions;
using LivePhotoConvert.Core.External;

namespace LivePhotoConvert.Core.Tests.External;

/// <summary>
/// 真实 FFmpeg 集成测试：转换后重新探测输出，验证编码、位深、色彩、hvc1 标记与显示方向。
/// </summary>
public class FfmpegVideoConverterTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly VideoConversionOptions Force = new() { ForceTranscode = true };

    [Theory]
    [InlineData(VideoStreamProbe.TransferHlg)]
    [InlineData(VideoStreamProbe.TransferPq)]
    public async Task ConvertToMp4_ForcedTranscodeOfHdr_Keeps10BitColorsAndTagsHvc1(string transfer)
    {
        var ffmpeg = await FfmpegSamples.RequireLibx265Async(ExternalTools.RequireFfmpeg());
        using var temp = new TempDirectory();
        var source = temp.Combine("hdr.mov");
        await FfmpegSamples.HdrAsync(ffmpeg, source, transfer);
        var output = temp.Combine("out.mp4");

        await FfmpegVideoConverter.Create(ffmpeg).ConvertToMp4Async(source, output, Force, Token);

        var before = await FfmpegSamples.ProbeAsync(ffmpeg, source, readFrameMetadata: true);
        var after = await FfmpegSamples.ProbeAsync(ffmpeg, output, readFrameMetadata: true);
        Assert.NotEqual(await FfmpegSamples.VideoPacketHashAsync(ffmpeg, source), await FfmpegSamples.VideoPacketHashAsync(ffmpeg, output));
        Assert.Equal("hevc", after.Codec);
        Assert.Equal("hvc1", after.CodecTag);
        Assert.Equal(10, after.BitDepth);
        Assert.Equal(("bt2020", transfer, "bt2020nc"), (after.ColorPrimaries, after.ColorTransfer, after.ColorSpace));
        Assert.True(after.IsHdr);
        Assert.Equal(before.MasteringDisplay, after.MasteringDisplay);
        Assert.Equal(before.ContentLightLevel, after.ContentLightLevel);
        if (transfer == VideoStreamProbe.TransferPq)
        {
            Assert.Equal(FfmpegSamples.MasterDisplay, after.MasteringDisplay?.ToX265Parameter());
            Assert.Equal(new ContentLightLevel(1000, 400), after.ContentLightLevel);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HevcSource_IsStreamCopiedAndTaggedHvc1(bool mov)
    {
        var ffmpeg = await FfmpegSamples.RequireLibx265Async(ExternalTools.RequireFfmpeg());
        using var temp = new TempDirectory();
        var source = temp.Combine("hev1.mp4");
        await FfmpegSamples.HdrAsync(ffmpeg, source, VideoStreamProbe.TransferHlg);
        Assert.Equal("hev1", (await FfmpegSamples.ProbeAsync(ffmpeg, source)).CodecTag);
        var output = temp.Combine(mov ? "out.MOV" : "out.mp4");

        var converter = FfmpegVideoConverter.Create(ffmpeg);
        await (mov ? converter.RemuxToMovAsync(source, output, cancellationToken: Token) : converter.ConvertToMp4Async(source, output, cancellationToken: Token));

        var after = await FfmpegSamples.ProbeAsync(ffmpeg, output);
        Assert.Equal("hvc1", after.CodecTag);
        Assert.Equal(await FfmpegSamples.VideoPacketHashAsync(ffmpeg, source), await FfmpegSamples.VideoPacketHashAsync(ffmpeg, output));
        Assert.True(after.IsHdr);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task H264Source_IsNeverTaggedHvc1(bool mov, bool forceTranscode)
    {
        var ffmpeg = ExternalTools.RequireFfmpeg();
        using var temp = new TempDirectory();
        var source = temp.Combine("avc.mov");
        await FfmpegSamples.H264Async(ffmpeg, source);
        var output = temp.Combine(mov ? "out.MOV" : "out.mp4");
        var options = forceTranscode ? Force : null;

        var converter = FfmpegVideoConverter.Create(ffmpeg);
        await (mov ? converter.RemuxToMovAsync(source, output, options, Token) : converter.ConvertToMp4Async(source, output, options, Token));

        var after = await FfmpegSamples.ProbeAsync(ffmpeg, output);
        Assert.Equal("h264", after.Codec);
        Assert.Equal("avc1", after.CodecTag);
        Assert.Equal(8, after.BitDepth);
    }

    [Fact]
    public async Task SdrTranscode_PassesSourceColorTriplet()
    {
        var ffmpeg = ExternalTools.RequireFfmpeg();
        using var temp = new TempDirectory();
        var source = temp.Combine("bt709.mov");
        await FfmpegSamples.H264Async(ffmpeg, source, "-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709");
        var output = temp.Combine("out.mp4");

        await FfmpegVideoConverter.Create(ffmpeg).ConvertToMp4Async(source, output, Force, Token);

        var after = await FfmpegSamples.ProbeAsync(ffmpeg, output);
        Assert.Equal(("bt709", "bt709", "bt709"), (after.ColorPrimaries, after.ColorTransfer, after.ColorSpace));
        Assert.Equal("h264", after.Codec);
    }

    public static TheoryData<bool, bool, bool> RotationCases => new()
    {
        // mov, forceTranscode, bakeOrientation
        { true, false, false },
        { false, false, false },
        { false, true, false },
        { false, false, true }
    };

    [Theory]
    [MemberData(nameof(RotationCases))]
    public async Task RotatedSource_KeepsDisplayOrientation(bool mov, bool forceTranscode, bool bakeOrientation)
    {
        var ffmpeg = ExternalTools.RequireFfmpeg();
        using var temp = new TempDirectory();
        var plain = temp.Combine("plain.mp4");
        var source = temp.Combine("rotated.mov");
        await FfmpegSamples.H264Async(ffmpeg, plain);
        await FfmpegSamples.RotateAsync(ffmpeg, plain, source);
        var before = await FfmpegSamples.ProbeAsync(ffmpeg, source);
        Assert.Equal(270, before.Rotation);
        var output = temp.Combine(mov ? "out.MOV" : "out.mp4");
        var options = new VideoConversionOptions { ForceTranscode = forceTranscode, BakeOrientation = bakeOrientation };

        var converter = FfmpegVideoConverter.Create(ffmpeg);
        await (mov ? converter.RemuxToMovAsync(source, output, options, Token) : converter.ConvertToMp4Async(source, output, options, Token));

        var after = await FfmpegSamples.ProbeAsync(ffmpeg, output);
        Assert.Equal((before.DisplayWidth, before.DisplayHeight), (after.DisplayWidth, after.DisplayHeight));
        // 烧录方向后像素已转正、不再带显示矩阵；否则与流复制一样原样保留矩阵
        Assert.Equal(bakeOrientation ? 0 : before.Rotation, after.Rotation);
    }

    [Fact]
    public async Task HdrSource_WithoutLibx265_FailsWithHdrEncoderUnavailable()
    {
        var ffmpeg = await FfmpegSamples.RequireLibx265Async(ExternalTools.RequireFfmpeg());
        using var temp = new TempDirectory();
        var source = temp.Combine("hlg.mov");
        await FfmpegSamples.HdrAsync(ffmpeg, source, VideoStreamProbe.TransferHlg);
        var output = temp.Combine("out.mp4");
        var converter = FfmpegVideoConverter.Create(ffmpeg, availableEncoders: ["libx264", "aac"]);

        var error = await Assert.ThrowsAsync<VideoConversionException>(() => converter.ConvertToMp4Async(source, output, Force, Token));

        Assert.Equal(VideoConversionError.HdrEncoderUnavailable, error.Error);
        Assert.Contains("HLG", error.Message);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task HdrSource_WithoutLibx265_StillStreamCopies()
    {
        var ffmpeg = await FfmpegSamples.RequireLibx265Async(ExternalTools.RequireFfmpeg());
        using var temp = new TempDirectory();
        var source = temp.Combine("hlg.mp4");
        await FfmpegSamples.HdrAsync(ffmpeg, source, VideoStreamProbe.TransferHlg);
        var output = temp.Combine("out.MOV");

        await FfmpegVideoConverter.Create(ffmpeg, availableEncoders: []).RemuxToMovAsync(source, output, cancellationToken: Token);

        Assert.Equal("hvc1", (await FfmpegSamples.ProbeAsync(ffmpeg, output)).CodecTag);
    }

    [Fact]
    public async Task HdrSource_WithoutLibx265_DowngradesOnlyWhenAllowed()
    {
        var ffmpeg = await FfmpegSamples.RequireLibx265Async(ExternalTools.RequireFfmpeg());
        using var temp = new TempDirectory();
        var source = temp.Combine("hlg.mov");
        await FfmpegSamples.HdrAsync(ffmpeg, source, VideoStreamProbe.TransferHlg);
        var output = temp.Combine("out.mp4");

        await FfmpegVideoConverter.Create(ffmpeg, availableEncoders: ["libx264", "aac"])
            .ConvertToMp4Async(source, output, Force with { AllowHdrDowngrade = true }, Token);

        var after = await FfmpegSamples.ProbeAsync(ffmpeg, output);
        Assert.Equal("h264", after.Codec);
        Assert.Equal(8, after.BitDepth);
        Assert.Equal(VideoStreamProbe.TransferHlg, after.ColorTransfer);
    }
}
