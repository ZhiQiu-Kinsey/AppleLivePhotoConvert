using LivePhotoConvert.Core.External;
using LivePhotoConvert.Core.External.Tools;

namespace LivePhotoConvert.Core.Tests.External;

public class ToolRegistryTests
{
    private const string FiltersOutput = """
        Filters:
          T.. = Timeline support
          .S. = Slice threading
          ..C = Command support
          A = Audio input/output
          V = Video input/output
          N = Dynamic number and/or type of input/output
          | = Source or sink filter
         ... abench            A->A       Benchmark part of a filtergraph.
         ..C scale             V->V       Scale the input video size and/or convert the image format.
         .S. tonemap           V->V       Conversion to/from different dynamic ranges.
         .SC zscale            V->V       Apply resizing, colorspace and bit depth conversion.
         ... nullsrc           |->V       Null video source, return unprocessed video frames.
        """;

    private const string EncodersOutput = """
        Encoders:
         V..... = Video
         A..... = Audio
         S..... = Subtitle
         .F.... = Frame-level multithreading
         ------
         V....D a64multi             Multicolor charset for Commodore 64 (codec a64_multi)
         V....D libx264              libx264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 (codec h264)
         V....D libx265              libx265 H.265 / HEVC (codec hevc)
         A....D aac                  AAC (Advanced Audio Coding)
        """;

    private const string X265Help10Bit = """
        Encoder libx265 [libx265 H.265 / HEVC]:
            General capabilities: dr1 delay threads
            Threading capabilities: other
            Supported pixel formats: yuv420p yuvj420p yuv422p yuv444p gbrp yuv420p10le yuv422p10le gray
        libx265 AVOptions:
          -crf               <float>      E..V....... set the x265 crf (from -1 to FLT_MAX) (default -1)
        """;

    private const string X265Help8Bit = """
        Encoder libx265 [libx265 H.265 / HEVC]:
            Supported pixel formats: yuv420p yuvj420p yuv422p yuvj422p yuv444p yuvj444p gbrp gray
        """;

    [Theory]
    [InlineData(ToolId.Ffmpeg, "ffmpeg version 6.1.1-3ubuntu5 Copyright (c) 2000-2023 the FFmpeg developers\nbuilt with gcc 13", "6.1.1-3ubuntu5", "6.1.1")]
    [InlineData(ToolId.Ffmpeg, "ffmpeg version n7.0-7-gd38bf5e08e-20240407 Copyright (c) 2000-2024", "n7.0-7-gd38bf5e08e-20240407", "7.0")]
    [InlineData(ToolId.Ffmpeg, "ffmpeg version n8.1.2-50-g1a748fe2cd-20260831 Copyright", "n8.1.2-50-g1a748fe2cd-20260831", "8.1.2")]
    [InlineData(ToolId.Ffmpeg, "ffmpeg version N-126342-gf88b741dbf-20260831 Copyright", "N-126342-gf88b741dbf-20260831", null)]
    [InlineData(ToolId.ExifTool, "13.59\n", "13.59", "13.59")]
    [InlineData(ToolId.HeifEnc, "1.17.6\nlibheif: 1.17.6\nplugin path: /usr/lib", "1.17.6", "1.17.6")]
    [InlineData(ToolId.HeifEnc, "\nlibheif: 1.23.1\n", "1.23.1", "1.23.1")]
    public void ParseVersion_HandlesKnownFormats(ToolId tool, string output, string expectedText, string? expectedVersion)
    {
        var text = ToolOutputParser.ParseVersionText(tool, output);

        Assert.Equal(expectedText, text);
        Assert.Equal(expectedVersion, ToolOutputParser.ParseVersion(text)?.ToString());
    }

    [Fact]
    public void ParseFilterAndEncoderNames_UseColumnLayout()
    {
        var filters = ToolOutputParser.ParseFilterNames(FiltersOutput);
        var encoders = ToolOutputParser.ParseEncoderNames(EncodersOutput);

        Assert.Equal(["abench", "nullsrc", "scale", "tonemap", "zscale"], filters.Order());
        Assert.Equal(["a64multi", "aac", "libx264", "libx265"], encoders.Order());
        Assert.Contains("yuv420p10le", ToolOutputParser.ParsePixelFormats(X265Help10Bit));
        Assert.DoesNotContain("yuv420p10le", ToolOutputParser.ParsePixelFormats(X265Help8Bit));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetAsync_DetectsFfmpegCapabilities(bool tenBit)
    {
        var runner = new FakeRunner(tenBit ? X265Help10Bit : X265Help8Bit);
        var registry = CreateRegistry(runner);

        var info = await registry.GetAsync(ToolId.Ffmpeg, TestContext.Current.CancellationToken);

        Assert.Equal("/tools/ffmpeg", info.Path);
        Assert.Equal(new Version(7, 0), info.Version);
        Assert.True(info.Has(ToolCapabilities.Zscale | ToolCapabilities.Tonemap | ToolCapabilities.Libx264 | ToolCapabilities.Libx265));
        Assert.Equal(tenBit, info.Has(ToolCapabilities.Hdr));
    }

    [Fact]
    public async Task GetAsync_ProbesOnceUntilInvalidated()
    {
        var runner = new FakeRunner(X265Help10Bit);
        var registry = CreateRegistry(runner);
        ToolId? invalidated = ToolId.HeifEnc;
        registry.Invalidated += (_, tool) => invalidated = tool;

        var first = registry.GetAsync(ToolId.Ffmpeg, TestContext.Current.CancellationToken);
        var second = registry.GetAsync(ToolId.Ffmpeg, TestContext.Current.CancellationToken);
        await Task.WhenAll(first, second);
        await registry.GetAsync(ToolId.Ffmpeg, TestContext.Current.CancellationToken);
        var callsBeforeInvalidate = runner.Calls;

        Assert.True(registry.TryGetCached(ToolId.Ffmpeg, out var cached));
        Assert.Same(await first, cached);

        registry.Invalidate();
        Assert.Null(invalidated);
        Assert.False(registry.TryGetCached(ToolId.Ffmpeg, out _));
        await registry.GetAsync(ToolId.Ffmpeg, TestContext.Current.CancellationToken);

        Assert.Equal(4, callsBeforeInvalidate);
        Assert.Equal(8, runner.Calls);
    }

    [Fact]
    public async Task GetAsync_InvalidExplicitPath_FallsBackAndReportsIt()
    {
        var registry = new ToolRegistry(
            _ => "/missing/exiftool",
            ToolManifest.Embedded,
            new ToolRegistryOptions
            {
                Locator = (_, explicitPath) => explicitPath is null ? "/usr/bin/exiftool" : null,
                Runner = (_, _, _) => Task.FromResult(new ProcessResult(0, "12.76\n", ""))
            });

        var info = await registry.GetAsync(ToolId.ExifTool, TestContext.Current.CancellationToken);

        Assert.Equal("/usr/bin/exiftool", info.Path);
        Assert.True(info.IsExplicitPathInvalid);
        Assert.Equal("12.76", info.VersionText);
    }

    [Fact]
    public async Task GetAsync_MissingTool_ReportsUnavailable()
    {
        var registry = new ToolRegistry(null, ToolManifest.Embedded, new ToolRegistryOptions { Locator = (_, _) => null });

        var info = await registry.GetAsync(ToolId.HeifEnc, TestContext.Current.CancellationToken);

        Assert.False(info.IsAvailable);
        Assert.Equal(ToolCapabilities.None, info.Capabilities);
    }

    [Fact]
    public async Task GetAsync_ProbeFailure_KeepsPathAndReportsError()
    {
        var registry = new ToolRegistry(null, ToolManifest.Embedded, new ToolRegistryOptions
        {
            Locator = (_, _) => "/tools/heif-enc",
            Runner = (_, _, _) => throw new TimeoutException("超时")
        });

        var info = await registry.GetAsync(ToolId.HeifEnc, TestContext.Current.CancellationToken);

        Assert.True(info.IsAvailable);
        Assert.Null(info.VersionText);
        Assert.Equal(ToolProbeFailure.Timeout, info.ProbeError?.Kind);
    }

    [Theory]
    [InlineData(nameof(TimeoutException), ToolProbeFailure.Timeout)]
    [InlineData(nameof(System.ComponentModel.Win32Exception), ToolProbeFailure.CannotStart)]
    [InlineData(nameof(InvalidOperationException), ToolProbeFailure.CannotStart)]
    [InlineData(nameof(InvalidDataException), ToolProbeFailure.Failed)]
    public async Task GetAsync_ProbeFailure_IsClassifiedForDisplay(string exceptionType, ToolProbeFailure expected)
    {
        Exception failure = exceptionType switch
        {
            nameof(TimeoutException) => new TimeoutException(),
            nameof(System.ComponentModel.Win32Exception) => new System.ComponentModel.Win32Exception(193),
            nameof(InvalidOperationException) => new InvalidOperationException(),
            _ => new InvalidDataException()
        };
        var registry = new ToolRegistry(null, ToolManifest.Embedded, new ToolRegistryOptions
        {
            Locator = (_, _) => "/tools/exiftool",
            Runner = (_, _, _) => throw failure
        });

        var info = await registry.GetAsync(ToolId.ExifTool, TestContext.Current.CancellationToken);

        Assert.Equal(expected, info.ProbeError?.Kind);
        // 原始异常保留给日志
        Assert.Same(failure, info.ProbeError?.Cause);
    }

    [Theory]
    [InlineData("6.1.1", "7.0", true)]
    [InlineData("7.0", "7.0", false)]
    [InlineData("8.1.2", "7.0", false)]
    [InlineData(null, "7.0", false)]
    [InlineData("6.1.1", null, false)]
    public void IsUpdateRecommended_ComparesNumericVersions(string? installed, string? recommended, bool expected)
    {
        var info = new ToolInfo(ToolId.Ffmpeg, "/x", installed, ToolOutputParser.ParseVersion(installed), ToolCapabilities.None, recommended, false, null);

        Assert.Equal(expected, info.IsUpdateRecommended);
    }

    [Fact]
    public async Task RealFfmpeg_ReportsVersionAndCapabilities()
    {
        var ffmpeg = ExternalTools.RequireFfmpeg();
        var registry = new ToolRegistry(tool => tool == ToolId.Ffmpeg ? ffmpeg : null);

        var info = await registry.GetAsync(ToolId.Ffmpeg, TestContext.Current.CancellationToken);

        Assert.Equal(Path.GetFullPath(ffmpeg), info.Path);
        Assert.Null(info.ProbeError);
        Assert.NotNull(info.VersionText);
        // tonemap 是 FFmpeg 内置滤镜，任何完整构建都有；其他能力取决于编译选项
        Assert.True(info.Has(ToolCapabilities.Tonemap));
        TestContext.Current.TestOutputHelper?.WriteLine($"{info.VersionText}: {info.Capabilities}");
    }

    [Fact]
    public async Task RealExifTool_ReportsNumericVersion()
    {
        var exiftool = ExternalTools.RequireExifTool();
        var registry = new ToolRegistry(tool => tool == ToolId.ExifTool ? exiftool : null);

        var info = await registry.GetAsync(ToolId.ExifTool, TestContext.Current.CancellationToken);

        Assert.NotNull(info.Version);
    }

    [Fact]
    public async Task RealHeifEnc_ReportsNumericVersion()
    {
        var heifEnc = ExternalTools.RequireHeifEnc();
        var registry = new ToolRegistry(tool => tool == ToolId.HeifEnc ? heifEnc : null);

        var info = await registry.GetAsync(ToolId.HeifEnc, TestContext.Current.CancellationToken);

        Assert.NotNull(info.Version);
    }

    private static ToolRegistry CreateRegistry(FakeRunner runner) => new(
        null,
        ToolManifest.Embedded,
        new ToolRegistryOptions { Locator = (definition, _) => "/tools/" + definition.Executable, Runner = runner.RunAsync });

    private sealed class FakeRunner(string x265Help)
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public async Task<ProcessResult> RunAsync(string path, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            await Task.Delay(20, cancellationToken);
            var output = string.Join(' ', arguments) switch
            {
                "-hide_banner -version" => "ffmpeg version n7.0-7-gd38bf5e08e-20240407 Copyright (c) 2000-2024",
                "-hide_banner -filters" => FiltersOutput,
                "-hide_banner -encoders" => EncodersOutput,
                "-hide_banner -h encoder=libx265" => x265Help,
                var other => throw new InvalidOperationException($"意外的参数 {other}")
            };
            return new ProcessResult(0, output, string.Empty);
        }
    }
}
