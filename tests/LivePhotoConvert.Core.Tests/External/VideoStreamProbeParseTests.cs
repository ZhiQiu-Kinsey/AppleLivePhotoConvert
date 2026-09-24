using LivePhotoConvert.Core.External;

namespace LivePhotoConvert.Core.Tests.External;

/// <summary>
/// 夹具为 FFmpeg 6.1 的真实标准错误（路径已替换为 /data），生成命令：
/// <list type="bullet">
/// <item>hlg-hevc-hvc1.mov：<c>-f lavfi -i testsrc2 -c:v libx265 -pix_fmt yuv420p10le -color_primaries bt2020 -color_trc arib-std-b67 -colorspace bt2020nc -tag:v hvc1</c></item>
/// <item>pq-hevc-hev1.mp4：同上但 <c>-color_trc smpte2084</c>、默认 hev1</item>
/// <item>frame-pq-hdr10：PQ 源带 <c>master-display</c>/<c>max-cll</c>，用 <c>-frames:v 1 -vf showinfo -f null -</c> 探测</item>
/// <item>sdr-*：libx264 yuv420p，分别不带色彩、<c>bt709 + -color_range pc</c>、只带 <c>-colorspace bt709</c>、yuvj420p 29.97fps</item>
/// <item>rotation-*：<c>-display_rotation 90 / -90</c> 流复制</item>
/// <item>subfile-hlg：HLG 视频拼在随机字节之间，路径含逗号、冒号、空格与中文</item>
/// </list>
/// synthetic-* 按 FFmpeg 源码的输出格式构造，覆盖本机无法生成的杜比视界配置记录、旧版 rotate 标签与流级 HDR10 元数据。
/// </summary>
public class VideoStreamProbeParseTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "External", "Fixtures", name));

    private static VideoStreamInfo ParseFixture(string name) =>
        VideoStreamProbe.Parse(Fixture(name)) ?? throw new Xunit.Sdk.XunitException($"{name} 未解析出视频流");

    [Fact]
    public void Parse_HlgHevc_ReadsCodecTagDepthAndColorTriplet()
    {
        var info = ParseFixture("hlg-hevc-hvc1.mov.txt");

        Assert.Equal("hevc", info.Codec);
        Assert.Equal("Main 10", info.Profile);
        Assert.Equal("hvc1", info.CodecTag);
        Assert.Equal("yuv420p10le", info.PixelFormat);
        Assert.Equal(10, info.BitDepth);
        Assert.Equal((320, 240), (info.Width, info.Height));
        Assert.Equal("tv", info.ColorRange);
        Assert.Equal("bt2020nc", info.ColorSpace);
        Assert.Equal("bt2020", info.ColorPrimaries);
        Assert.Equal("arib-std-b67", info.ColorTransfer);
        Assert.Equal(30, info.FrameRate);
        Assert.Equal(TimeSpan.FromSeconds(1), info.Duration);
        Assert.Equal(0, info.Rotation);
        Assert.True(info.IsHdr);
        Assert.True(info.IsHevc);
        Assert.False(info.HasDolbyVision);
        Assert.Null(info.MasteringDisplay);
    }

    [Fact]
    public void Parse_PqHevc_IsHdrWithHev1Tag()
    {
        var info = ParseFixture("pq-hevc-hev1.mp4.txt");

        Assert.Equal("hev1", info.CodecTag);
        Assert.Equal("smpte2084", info.ColorTransfer);
        Assert.True(info.IsHdr);
    }

    [Fact]
    public void Parse_UnspecifiedColor_LeavesColorFieldsNull()
    {
        var info = ParseFixture("sdr-h264-unspecified.mp4.txt");

        Assert.Equal("h264", info.Codec);
        Assert.Equal("High", info.Profile);
        Assert.Equal("avc1", info.CodecTag);
        Assert.Equal(8, info.BitDepth);
        Assert.Null(info.ColorRange);
        Assert.Null(info.ColorSpace);
        Assert.Null(info.ColorPrimaries);
        Assert.Null(info.ColorTransfer);
        Assert.False(info.IsHdr);
        Assert.True(info.IsH264);
    }

    [Fact]
    public void Parse_SingleColorName_MeansAllThreeEqual()
    {
        var info = ParseFixture("sdr-h264-bt709-full.mp4.txt");

        Assert.Equal("yuvj420p", info.PixelFormat);
        Assert.Equal(8, info.BitDepth);
        Assert.Equal("pc", info.ColorRange);
        Assert.Equal(("bt709", "bt709", "bt709"), (info.ColorSpace, info.ColorPrimaries, info.ColorTransfer));
    }

    [Fact]
    public void Parse_PartialTriplet_TreatsUnknownAsNull()
    {
        var info = ParseFixture("sdr-h264-partial-color.mp4.txt");

        Assert.Equal("bt709", info.ColorSpace);
        Assert.Null(info.ColorPrimaries);
        Assert.Null(info.ColorTransfer);
    }

    [Fact]
    public void Parse_RangeOnlyAndFractionalFrameRate()
    {
        var info = ParseFixture("sdr-h264-yuvj-2997.mov.txt");

        Assert.Equal("pc", info.ColorRange);
        Assert.Null(info.ColorSpace);
        Assert.Equal(29.97, info.FrameRate);
    }

    [Fact]
    public void Parse_InterlacedMatroska_IgnoresFieldOrderAndHasNoCodecTag()
    {
        var info = ParseFixture("h264-422-10bit-tff.mkv.txt");

        Assert.Equal("High 4:2:2", info.Profile);
        Assert.Null(info.CodecTag);
        Assert.Equal(10, info.BitDepth);
        Assert.Equal("tv", info.ColorRange);
        Assert.Null(info.ColorSpace);
        Assert.Equal(TimeSpan.FromSeconds(0.4), info.Duration);
        Assert.Equal(25, info.FrameRate);
    }

    [Theory]
    // displaymatrix 是逆时针角度，Rotation 按顺时针（与 ExifTool 一致）
    [InlineData("rotation-ccw90.mp4.txt", 270)]
    [InlineData("rotation-cw90-hlg.mov.txt", 90)]
    [InlineData("synthetic-android-hdr10-ffmpeg4.txt", 90)]
    public void Parse_Rotation_IsClockwiseDegrees(string fixture, int expected)
    {
        var info = ParseFixture(fixture);

        Assert.Equal(expected, info.Rotation);
        Assert.Equal((info.Height, info.Width), (info.DisplayWidth, info.DisplayHeight));
    }

    [Fact]
    public void Parse_Subfile_ReadsEmbeddedStream()
    {
        var info = ParseFixture("subfile-hlg.txt");

        Assert.Equal("hvc1", info.CodecTag);
        Assert.True(info.IsHdr);
    }

    [Fact]
    public void Parse_AudioOnly_ReturnsNull()
    {
        Assert.Null(VideoStreamProbe.Parse(Fixture("audio-only.m4a.txt")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/data/missing.mp4: No such file or directory\n")]
    public void Parse_NoInput_ReturnsNull(string standardError)
    {
        Assert.Null(VideoStreamProbe.Parse(standardError));
    }

    [Fact]
    public void Parse_FrameProbe_ReadsHdr10StaticMetadataAndIgnoresOutputStream()
    {
        var info = ParseFixture("frame-pq-hdr10.txt");

        // 输出段的 wrapped_avframe 流不能覆盖输入流
        Assert.Equal("hevc", info.Codec);
        Assert.Equal("hev1", info.CodecTag);
        var mastering = Assert.IsType<MasteringDisplay>(info.MasteringDisplay);
        Assert.Equal(new Chromaticity(0.68, 0.32), mastering.Red);
        Assert.Equal(new Chromaticity(0.15, 0.06), mastering.Blue);
        Assert.Equal(1000, mastering.MaxLuminance);
        Assert.Equal(0.0001, mastering.MinLuminance);
        Assert.Equal("G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,1)", mastering.ToX265Parameter());
        Assert.Equal(new ContentLightLevel(1000, 400), info.ContentLightLevel);
    }

    [Fact]
    public void Parse_StreamLevelHdr10Metadata_Ffmpeg4Layout()
    {
        var info = ParseFixture("synthetic-android-hdr10-ffmpeg4.txt");

        Assert.Equal((3840, 2160), (info.Width, info.Height));
        Assert.Equal(29.97, info.FrameRate);
        Assert.Equal(TimeSpan.FromSeconds(3.03), info.Duration);
        Assert.Equal("smpte2084", info.ColorTransfer);
        Assert.Equal(0.005, info.MasteringDisplay?.MinLuminance);
        Assert.Equal(new ContentLightLevel(1000, 180), info.ContentLightLevel);
    }

    [Fact]
    public void Parse_IphoneDolbyVision_Ffmpeg7Layout()
    {
        var info = ParseFixture("synthetic-iphone-dovi-ffmpeg7.txt");

        Assert.True(info.HasDolbyVision);
        Assert.Equal(8, info.DolbyVisionProfile);
        Assert.Equal(90, info.Rotation);
        Assert.Equal((1440, 1920), (info.DisplayWidth, info.DisplayHeight));
        Assert.Equal("arib-std-b67", info.ColorTransfer);
        Assert.Equal(29.98, info.FrameRate);
        Assert.True(info.IsHdr);
    }

    [Fact]
    public void Parse_OnlyFirstInputIsRead()
    {
        const string stderr = """
            Input #0, mov,mp4,m4a,3gp,3g2,mj2, from '/data/a.mp4':
              Duration: N/A, start: 0.000000, bitrate: N/A
              Stream #0:0[0x1](und): Video: h264 (High) (avc1 / 0x31637661), yuv420p(tv, bt709, progressive), 64x48, 30 fps, 30 tbr, 15360 tbn (default)
            Input #1, mov,mp4,m4a,3gp,3g2,mj2, from '/data/b.mp4':
              Stream #1:0[0x1](und): Video: hevc (Main 10) (hvc1 / 0x31637668), yuv420p10le(tv, bt2020nc/bt2020/smpte2084, progressive), 64x48, 30 fps, 30 tbr, 15360 tbn (default)
            """;

        var info = VideoStreamProbe.Parse(stderr);

        Assert.NotNull(info);
        Assert.Equal("h264", info.Codec);
        Assert.Null(info.Duration);
    }

    [Fact]
    public void Parse_BitsPerRawSampleAndChromaLocation()
    {
        const string stderr = """
            Input #0, matroska,webm, from '/data/a.mkv':
              Stream #0:0: Video: hevc (Main 12), yuv420p12le(10 bpc, pc, bt2020nc/bt2020/arib-std-b67, progressive, left), 64x48, 25 fps, 25 tbr, 1k tbn
            """;

        var info = VideoStreamProbe.Parse(stderr);

        Assert.NotNull(info);
        Assert.Equal(10, info.BitDepth);
        Assert.Equal("pc", info.ColorRange);
        Assert.Equal("arib-std-b67", info.ColorTransfer);
    }

    [Theory]
    [InlineData("yuv420p", 8)]
    [InlineData("yuvj420p", 8)]
    [InlineData("nv12", 8)]
    [InlineData("yuv420p10le", 10)]
    [InlineData("yuv422p10be", 10)]
    [InlineData("yuv444p12le", 12)]
    [InlineData("gbrp16le", 16)]
    [InlineData("gray10le", 10)]
    [InlineData("p010le", 10)]
    [InlineData("p016le", 16)]
    [InlineData("rgb48le", 16)]
    [InlineData("none", null)]
    public void BitDepthOf_PixelFormatName(string pixelFormat, int? expected)
    {
        Assert.Equal(expected, VideoStreamProbe.BitDepthOf(pixelFormat));
    }

    [Fact]
    public void SubfileInput_UsesAbsolutePathAndExclusiveEnd()
    {
        var path = Path.Combine(Path.GetTempPath(), "a,b 中文", "x.jpg");

        Assert.Equal($"subfile,,start,100,end,350,,:{path}", VideoStreamProbe.SubfileInput(path, 100, 250));
    }

    [Fact]
    public void FileInput_RelativePath_BecomesAbsolute()
    {
        Assert.True(Path.IsPathFullyQualified(VideoStreamProbe.FileInput("clip:1.mp4".Replace(':', OperatingSystem.IsWindows() ? '_' : ':'))));
    }
}
