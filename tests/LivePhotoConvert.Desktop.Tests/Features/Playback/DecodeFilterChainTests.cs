using Avalonia;
using LivePhotoConvert.Core.External;
using LivePhotoConvert.Desktop.Features.Playback;

namespace LivePhotoConvert.Desktop.Tests.Features.Playback;

public class DecodeFilterChainTests
{
    private static readonly VideoStreamInfo Sdr = new()
    {
        Codec = "hevc", Width = 1920, Height = 1440, ColorRange = "tv", ColorSpace = "bt709", ColorPrimaries = "bt709", ColorTransfer = "bt709", FrameRate = 30
    };

    private static readonly VideoStreamInfo Hlg = new()
    {
        Codec = "hevc", Width = 1920, Height = 1440, Rotation = 90, ColorRange = "tv", ColorSpace = "bt2020nc", ColorPrimaries = "bt2020", ColorTransfer = "arib-std-b67", PixelFormat = "yuv420p10le"
    };

    [Fact]
    public void BuildSdr_LimitedRange_ScalesWithLanczosAndExplicitMatrix()
    {
        Assert.Equal(
            "scale=w=640:h=480:flags=lanczos+accurate_rnd+full_chroma_int:in_color_matrix=bt709:in_range=limited,format=bgra,showinfo=checksum=0",
            DecodeFilterChain.Build(Sdr, new PixelSize(640, 480)));
    }

    [Fact]
    public void BuildSdr_FullRange_DeclaresFullInputRange()
    {
        var chain = DecodeFilterChain.Build(Sdr with { ColorRange = "pc" }, new PixelSize(640, 480));

        Assert.Contains(":in_range=full,", chain);
    }

    [Theory]
    [InlineData(null, 1920, 1080, "bt709", "auto")]
    [InlineData(null, 640, 480, "auto", "auto")]
    [InlineData("smpte170m", 640, 480, "bt601", "auto")]
    [InlineData("bt470bg", 640, 480, "bt601", "auto")]
    [InlineData("bt2020nc", 1920, 1080, "bt2020", "auto")]
    public void BuildSdr_UnknownOrLegacyMatrix_MapsToSwscaleNames(string? colorSpace, int width, int height, string matrix, string range)
    {
        var chain = DecodeFilterChain.Build(new VideoStreamInfo { Codec = "h264", Width = width, Height = height, ColorSpace = colorSpace }, new PixelSize(320, 240));

        Assert.Contains($":in_color_matrix={matrix}:in_range={range},", chain);
    }

    [Fact]
    public void BuildHdr_Hlg_ScalesInsideZscaleThenToneMaps()
    {
        Assert.Equal(
            "zscale=w=480:h=640:f=lanczos:rin=limited:min=bt2020nc:pin=bt2020:tin=arib-std-b67:t=linear:npl=203,format=gbrpf32le,"
            + "zscale=p=bt709,tonemap=tonemap=mobius:desat=0,zscale=t=bt709:m=bt709:r=pc,format=gbrp,format=bgra,showinfo=checksum=0",
            DecodeFilterChain.Build(Hlg, new PixelSize(480, 640)));
    }

    [Fact]
    public void BuildHdr_PqWithUnknownProperties_OmitsUndeclaredInputs()
    {
        var chain = DecodeFilterChain.Build(new VideoStreamInfo { Codec = "hevc", Width = 3840, Height = 2160, ColorTransfer = "smpte2084" }, new PixelSize(1920, 1080));

        Assert.StartsWith("zscale=w=1920:h=1080:f=lanczos:tin=smpte2084:t=linear:npl=203,", chain);
        Assert.DoesNotContain("rin=", chain);
    }

    [Fact]
    public void IsSupported_HdrWithoutZscale_IsFalse()
    {
        Assert.False(DecodeFilterChain.IsSupported(Hlg, new HashSet<string> { "scale", "tonemap", "showinfo" }));
        Assert.False(DecodeFilterChain.IsSupported(Hlg, new HashSet<string> { "scale", "zscale", "showinfo" }));
        Assert.True(DecodeFilterChain.IsSupported(Hlg, new HashSet<string> { "zscale", "tonemap" }));
        Assert.True(DecodeFilterChain.IsSupported(Sdr, new HashSet<string>()));
    }

    [Fact]
    public void FfmpegFilters_Parse_ReadsFilterNames()
    {
        const string output = """
            Filters:
              T.. = Timeline support
              .S. = Slice threading
              ..C = Command support
              A = Audio input/output
              V = Video input/output
              N = Dynamic number and/or type of input/output
              | = Source or sink filter
             ... abench            A->A       Benchmark part of a filtergraph.
             ..C libplacebo        N->V       Apply various GPU filters from libplacebo
             ..C scale             V->V       Scale the input video size and/or convert the image format.
             ... showinfo          V->V       Show textual information for each video frame.
             .S. tonemap           V->V       Conversion to/from different dynamic ranges.
             .SC zscale            V->V       Apply resizing, colorspace and bit depth conversion.
             ... nullsrc           |->V       Null video source, return unprocessed video frames.
            """;

        var filters = FfmpegFilters.Parse(output);

        Assert.Equal(["abench", "libplacebo", "nullsrc", "scale", "showinfo", "tonemap", "zscale"], filters.Order(StringComparer.Ordinal));
        Assert.True(FfmpegFilters.SupportsHdrToneMapping(filters));
    }

    [Fact]
    public void BuildArguments_PassesEachItemSeparately()
    {
        const string input = "subfile,,start,10,end,20,,:/相册 a,b/动态 照片.jpg";

        var arguments = RawVideoDecoder.BuildArguments(input, Sdr, new PixelSize(640, 480)).ToList();

        Assert.Equal(["-nostdin", "-hide_banner", "-nostats", "-loglevel", "info"], arguments.Take(5));
        Assert.Equal(input, arguments[arguments.IndexOf("-i") + 1]);
        Assert.Equal("passthrough", arguments[arguments.IndexOf("-fps_mode") + 1]);
        Assert.Equal("rawvideo", arguments[arguments.IndexOf("-f") + 1]);
        Assert.Equal("bgra", arguments[arguments.IndexOf("-pix_fmt") + 1]);
        Assert.EndsWith(",showinfo=checksum=0", arguments[arguments.IndexOf("-vf") + 1]);
        Assert.Equal("-", arguments[^1]);
        Assert.DoesNotContain("-hwaccel", arguments);
    }

    [Fact]
    public void ToFfmpegInput_EmbeddedVideo_UsesSubfile()
    {
        var path = Path.Combine(Path.GetTempPath(), "a b", "动态,1.jpg");

        Assert.Equal($"subfile,,start,100,end,300,,:{path}", new VideoSource(path, 100, 200, true).ToFfmpegInput());
        Assert.Equal(path, new VideoSource(path, 0, 200, false).ToFfmpegInput());
    }
}

public class PlaybackGeometryTests
{
    [Theory]
    // 显示尺寸 × 缩放
    [InlineData(1920, 1440, 400, 300, 1.5, 600, 450)]
    [InlineData(1440, 1920, 300, 400, 2.0, 600, 800)]
    // 目标比例不同：等比缩进框内
    [InlineData(1920, 1080, 500, 500, 1.0, 500, 282)]
    // 不放大
    [InlineData(640, 480, 1600, 1200, 2.0, 640, 480)]
    // 奇数源尺寸：不放大且取偶数
    [InlineData(1079, 721, 2000, 2000, 1.0, 1078, 720)]
    // 缩放后为奇数：取最近的偶数
    [InlineData(1920, 1080, 301, 301, 1.0, 302, 170)]
    [InlineData(100, 100, 1, 1, 1.0, 2, 2)]
    public void ComputeOutputSize_FitsKeepsRatioEvenAndNoUpscale(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight, double scaling, int width, int height)
    {
        Assert.Equal(new PixelSize(width, height), PlaybackGeometry.ComputeOutputSize(sourceWidth, sourceHeight, new PixelSize(targetWidth, targetHeight), scaling));
    }

    [Fact]
    public void ComputeOutputSize_RotatedSource_UsesDisplayOrientation()
    {
        var info = new VideoStreamInfo { Codec = "hevc", Width = 1920, Height = 1440, Rotation = 90 };

        Assert.Equal(new PixelSize(300, 400), PlaybackGeometry.ComputeOutputSize(info.DisplayWidth, info.DisplayHeight, new PixelSize(300, 400), 1.0));
    }

    [Fact]
    public void ComputeOutputSize_InvalidScaling_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PlaybackGeometry.ComputeOutputSize(100, 100, new PixelSize(10, 10), double.NaN));
}
