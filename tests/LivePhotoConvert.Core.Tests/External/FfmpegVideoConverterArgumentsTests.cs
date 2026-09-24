using LivePhotoConvert.Core.External;

namespace LivePhotoConvert.Core.Tests.External;

public class FfmpegVideoConverterArgumentsTests
{
    private static readonly VideoStreamInfo Hlg = new()
    {
        Codec = "hevc",
        CodecTag = "hev1",
        PixelFormat = "yuv420p10le",
        BitDepth = 10,
        ColorRange = "tv",
        ColorSpace = "bt2020nc",
        ColorPrimaries = "bt2020",
        ColorTransfer = "arib-std-b67"
    };

    private static readonly VideoStreamInfo H264 = new() { Codec = "h264", CodecTag = "avc1", PixelFormat = "yuv420p", BitDepth = 8 };

    private static string? ValueAfter(IReadOnlyList<string> arguments, string option)
    {
        var index = arguments.ToList().IndexOf(option);
        return index >= 0 && index + 1 < arguments.Count ? arguments[index + 1] : null;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Remux_Hevc_TagsHvc1(bool mov)
    {
        var arguments = FfmpegVideoConverter.BuildRemuxArguments("/in.mp4", "/out", Hlg, mov ? VideoContainer.Mov : VideoContainer.Mp4);

        Assert.Equal("copy", ValueAfter(arguments, "-c:v"));
        Assert.Equal("hvc1", ValueAfter(arguments, "-tag:v"));
        Assert.Equal("-nostdin", arguments[0]);
    }

    [Fact]
    public void Remux_H264OrUnknown_DoesNotTag()
    {
        Assert.DoesNotContain("-tag:v", FfmpegVideoConverter.BuildRemuxArguments("/in.mp4", "/out.mp4", H264, VideoContainer.Mp4));
        Assert.DoesNotContain("-tag:v", FfmpegVideoConverter.BuildRemuxArguments("/in.mp4", "/out.mp4", null, VideoContainer.Mp4));
    }

    [Fact]
    public void Remux_Mov_UsesPcmAudioAndMovMuxer()
    {
        var arguments = FfmpegVideoConverter.BuildRemuxArguments("/in.mp4", "/out.tmp", H264, VideoContainer.Mov);

        Assert.Equal("pcm_s16le", ValueAfter(arguments, "-c:a"));
        Assert.Equal("mov", ValueAfter(arguments, "-f"));
        Assert.Equal("/out.tmp", arguments[^1]);
    }

    [Fact]
    public void Transcode_Hdr_Uses10BitX265WithColorMetadata()
    {
        var arguments = FfmpegVideoConverter.BuildTranscodeArguments("/in.mov", "/out.mp4", Hlg, hevc: true, bakeOrientation: false, VideoContainer.Mp4);

        Assert.Equal("libx265", ValueAfter(arguments, "-c:v"));
        Assert.Equal("yuv420p10le", ValueAfter(arguments, "-pix_fmt"));
        Assert.Equal("hvc1", ValueAfter(arguments, "-tag:v"));
        Assert.Equal("bt2020", ValueAfter(arguments, "-color_primaries"));
        Assert.Equal("arib-std-b67", ValueAfter(arguments, "-color_trc"));
        Assert.Equal("bt2020nc", ValueAfter(arguments, "-colorspace"));
        Assert.Equal("tv", ValueAfter(arguments, "-color_range"));
        Assert.Equal("scale=out_range=tv,format=yuv420p10le", ValueAfter(arguments, "-vf"));
        Assert.Equal(
            "log-level=error:colorprim=bt2020:transfer=arib-std-b67:colormatrix=bt2020nc:range=limited",
            ValueAfter(arguments, "-x265-params"));
    }

    [Fact]
    public void Transcode_Sdr_KeepsH264WithoutHvc1AndPassesKnownColors()
    {
        var bt709 = H264 with { ColorSpace = "bt709", ColorPrimaries = "bt709", ColorTransfer = "bt709" };

        var arguments = FfmpegVideoConverter.BuildTranscodeArguments("/in.mov", "/out.mp4", bt709, hevc: false, bakeOrientation: false, VideoContainer.Mp4);

        Assert.Equal("libx264", ValueAfter(arguments, "-c:v"));
        Assert.Equal("yuv420p", ValueAfter(arguments, "-pix_fmt"));
        Assert.DoesNotContain("-tag:v", arguments);
        Assert.DoesNotContain("-x265-params", arguments);
        Assert.Equal("bt709", ValueAfter(arguments, "-color_primaries"));
        Assert.Equal("bt709", ValueAfter(arguments, "-colorspace"));
    }

    [Fact]
    public void Transcode_UnknownColors_AreNotInvented()
    {
        var arguments = FfmpegVideoConverter.BuildTranscodeArguments("/in.mov", "/out.mp4", H264, hevc: false, bakeOrientation: false, VideoContainer.Mp4);

        Assert.DoesNotContain("-color_primaries", arguments);
        Assert.DoesNotContain("-color_trc", arguments);
        Assert.DoesNotContain("-colorspace", arguments);
    }

    [Fact]
    public void Transcode_PreservesDisplayMatrixUnlessBaking()
    {
        var preserve = FfmpegVideoConverter.BuildTranscodeArguments("/in.mov", "/out.mp4", H264, hevc: false, bakeOrientation: false, VideoContainer.Mp4);
        var bake = FfmpegVideoConverter.BuildTranscodeArguments("/in.mov", "/out.mp4", H264, hevc: false, bakeOrientation: true, VideoContainer.Mp4);

        // -autorotate 是输入选项，必须位于 -i 之前
        Assert.Equal("0", ValueAfter(preserve, "-autorotate"));
        Assert.True(preserve.ToList().IndexOf("-autorotate") < preserve.ToList().IndexOf("-i"));
        Assert.DoesNotContain("-autorotate", bake);
    }

    [Fact]
    public void X265Parameters_IncludeHdr10StaticMetadata()
    {
        var pq = Hlg with
        {
            ColorTransfer = "smpte2084",
            MasteringDisplay = new MasteringDisplay(new(0.68, 0.32), new(0.265, 0.69), new(0.15, 0.06), new(0.3127, 0.329), 0.005, 1000),
            ContentLightLevel = new ContentLightLevel(1000, 400)
        };

        Assert.Equal(
            "log-level=error:colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc:range=limited"
            + ":master-display=G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,50):max-cll=1000,400",
            FfmpegVideoConverter.BuildX265Parameters(pq));
    }

    [Fact]
    public void X265Parameters_SkipNamesX265DoesNotKnow()
    {
        var odd = Hlg with { ColorPrimaries = "ebu3213" };

        Assert.DoesNotContain("colorprim", FfmpegVideoConverter.BuildX265Parameters(odd));
    }

    [Fact]
    public void ParseEncoders_ReadsNamesAndSkipsLegend()
    {
        const string output = """
            Encoders:
             V..... = Video
             A..... = Audio
             .F.... = Frame-level multithreading
             ------
             V....D libx264              libx264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 (codec h264)
             V....D libx265              libx265 H.265 / HEVC (codec hevc)
             A....D aac                  AAC (Advanced Audio Coding)
            """;

        var encoders = FfmpegVideoConverter.ParseEncoders(output);

        Assert.Equal(["aac", "libx264", "libx265"], encoders.Order(StringComparer.Ordinal));
    }
}
