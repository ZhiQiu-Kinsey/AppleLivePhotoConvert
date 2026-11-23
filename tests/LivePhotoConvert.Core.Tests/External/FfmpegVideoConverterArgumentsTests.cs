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
        var arguments = FfmpegVideoConverter.BuildTranscodeArguments("/in.mov", "/out.mp4", Hlg, hevc: true, bakeOrientation: false, toneMapToSdr: false, VideoContainer.Mp4);

        Assert.Equal("libx265", ValueAfter(arguments, "-c:v"));
        Assert.Equal("yuv420p10le", ValueAfter(arguments, "-pix_fmt"));
        Assert.Equal("hvc1", ValueAfter(arguments, "-tag:v"));
        Assert.Equal("bt2020", ValueAfter(arguments, "-color_primaries"));
        Assert.Equal("arib-std-b67", ValueAfter(arguments, "-color_trc"));
        Assert.Equal("bt2020nc", ValueAfter(arguments, "-colorspace"));
        Assert.Equal("tv", ValueAfter(arguments, "-color_range"));
        // 帧上的原色与传递特性必须显式声明：FFmpeg 7.1 起编码器取帧属性，-color_trc 会被帧上的 unknown 覆盖
        Assert.Equal("setparams=color_primaries=bt2020:color_trc=arib-std-b67:colorspace=bt2020nc,scale=out_range=tv,format=yuv420p10le", ValueAfter(arguments, "-vf"));
        Assert.Equal(
            "log-level=error:colorprim=bt2020:transfer=arib-std-b67:colormatrix=bt2020nc:range=limited",
            ValueAfter(arguments, "-x265-params"));
    }

    [Fact]
    public void Transcode_Sdr_KeepsH264WithoutHvc1AndPassesKnownColors()
    {
        var bt709 = H264 with { ColorSpace = "bt709", ColorPrimaries = "bt709", ColorTransfer = "bt709" };

        var arguments = FfmpegVideoConverter.BuildTranscodeArguments("/in.mov", "/out.mp4", bt709, hevc: false, bakeOrientation: false, toneMapToSdr: false, VideoContainer.Mp4);

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
        var arguments = FfmpegVideoConverter.BuildTranscodeArguments("/in.mov", "/out.mp4", H264, hevc: false, bakeOrientation: false, toneMapToSdr: false, VideoContainer.Mp4);

        Assert.DoesNotContain("-color_primaries", arguments);
        Assert.DoesNotContain("-color_trc", arguments);
        Assert.DoesNotContain("-colorspace", arguments);
    }

    [Fact]
    public void Transcode_ToneMapToSdr_ReplacesColorTripletWithBt709()
    {
        // 一加 / OPPO 的 HLG 视频：转为苹果必须映射成 SDR，否则 iOS 按 HDR 渲染视频帧而发灰
        var arguments = FfmpegVideoConverter.BuildTranscodeArguments("/in.mov", "/out.MOV", Hlg, hevc: false, bakeOrientation: false, toneMapToSdr: true, VideoContainer.Mov);

        Assert.Equal("yuv420p", ValueAfter(arguments, "-pix_fmt"));
        Assert.Equal("bt709", ValueAfter(arguments, "-color_primaries"));
        Assert.Equal("bt709", ValueAfter(arguments, "-color_trc"));
        Assert.Equal("bt709", ValueAfter(arguments, "-colorspace"));
        Assert.DoesNotContain("-tag:v", arguments);
        Assert.Contains("tonemap", ValueAfter(arguments, "-vf"));
        // 滤镜里的 p=bt2020 是输入色域，输出三元组必须全部落到 bt709
        Assert.DoesNotContain("bt2020", ValueAfter(arguments, "-color_primaries"));
    }

    [Fact]
    public void ToneMapFilter_DeclaresSourceColorsExplicitly()
    {
        // 码流 VUI 缺失、只有容器 colr 时 zscale 读不到帧属性，输入属性与输出色域都必须写明
        var filter = FfmpegVideoConverter.BuildToneMapFilter(Hlg with { ColorRange = "tv" });

        Assert.Equal(
            "zscale=rin=limited:min=bt2020nc:pin=bt2020:tin=arib-std-b67:t=linear:npl=100:p=bt2020,format=gbrpf32le,tonemap=hable:desat=0,"
            + "zscale=t=bt709:m=bt709:p=bt709:r=limited,format=yuv420p",
            filter);
    }

    [Fact]
    public void ToneMapFilter_UnknownPrimaries_AssumesBt2020()
    {
        var filter = FfmpegVideoConverter.BuildToneMapFilter(Hlg with { ColorPrimaries = null, ColorSpace = null, ColorRange = null });

        Assert.StartsWith("zscale=pin=bt2020:tin=arib-std-b67:t=linear:npl=100:p=bt2020,", filter);
    }

    [Fact]
    public void Transcode_ToneMapRequestedForSdrSource_LeavesSourceColorsAlone()
    {
        // 小米等 SDR 源：套用色调映射会改变画面观感，只保留源的三元组
        var bt709 = H264 with { ColorSpace = "bt709", ColorPrimaries = "bt709", ColorTransfer = "bt709" };

        var arguments = FfmpegVideoConverter.BuildTranscodeArguments("/in.mov", "/out.MOV", bt709, hevc: false, bakeOrientation: false, toneMapToSdr: true, VideoContainer.Mov);

        Assert.Equal("setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709,scale=out_range=tv,format=yuv420p", ValueAfter(arguments, "-vf"));
        Assert.Equal("bt709", ValueAfter(arguments, "-color_primaries"));
        Assert.DoesNotContain("tonemap", string.Join(' ', arguments));
    }

    [Fact]
    public void Transcode_UnknownColors_DoNotDeclareFrameParameters()
    {
        var arguments = FfmpegVideoConverter.BuildTranscodeArguments("/in.mov", "/out.mp4", H264, hevc: false, bakeOrientation: false, toneMapToSdr: false, VideoContainer.Mp4);

        Assert.Equal("scale=out_range=tv,format=yuv420p", ValueAfter(arguments, "-vf"));
    }

    [Fact]
    public void Transcode_PreservesDisplayMatrixUnlessBaking()
    {
        var preserve = FfmpegVideoConverter.BuildTranscodeArguments("/in.mov", "/out.mp4", H264, hevc: false, bakeOrientation: false, toneMapToSdr: false, VideoContainer.Mp4);
        var bake = FfmpegVideoConverter.BuildTranscodeArguments("/in.mov", "/out.mp4", H264, hevc: false, bakeOrientation: true, toneMapToSdr: false, VideoContainer.Mp4);

        // -autorotate 是输入选项，必须位于 -i 之前
        // FFmpeg 7 起只接受 -noautorotate；旧式 "-autorotate 0" 会报「输入选项用于输出文件」
        Assert.Contains("-noautorotate", preserve);
        Assert.DoesNotContain("-autorotate", preserve);
        Assert.True(preserve.ToList().IndexOf("-noautorotate") < preserve.ToList().IndexOf("-i"));
        Assert.DoesNotContain("-noautorotate", bake);
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
}
