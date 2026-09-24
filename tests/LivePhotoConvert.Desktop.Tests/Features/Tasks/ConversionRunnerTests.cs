using ImageMagick;
using LivePhotoConvert.Core.External;
using LivePhotoConvert.Core.Media;
using LivePhotoConvert.Core.Metadata;
using LivePhotoConvert.Core.Pairing;
using LivePhotoConvert.Core.Pipeline;
using LivePhotoConvert.Core.Services;
using LivePhotoConvert.Core.Tests.Support;
using LivePhotoConvert.Desktop.Features.Library;
using LivePhotoConvert.Desktop.Features.Tasks;
using LivePhotoConvert.Desktop.Tests.Harness;

namespace LivePhotoConvert.Desktop.Tests.Features.Tasks;

public class ConversionRunnerTests : IDisposable
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly TestSandbox _context = new();
    private readonly FakeEngines _engines = new();

    public void Dispose() => _context.Dispose();

    private Task<BatchReport> RunAsync(ConversionJob job, IProgress<BatchProgress>? progress = null) =>
        new ConversionRunner(_engines).RunAsync(job, progress, Token);

    [Fact]
    public async Task ToAndroid_MergesPairsWithRequestedOptions()
    {
        var photo = _context.CreateInputFile("IMG_0001.jpg", SyntheticMedia.Jpeg(3000));
        var video = _context.CreateInputFile("IMG_0001.mov", SyntheticMedia.Mov(5000));
        var pair = new MediaPair(photo, video);
        var job = new ConversionJob(
            ConversionAction.ToAndroid,
            new ConversionOptions { Output = new OutputOptions(_context.OutputDirectory), Naming = MergeNamingFormat.Original, SourceAction = SourceFileAction.Keep },
            new ConversionInputs { Pairs = [pair], ForceAccepted = [pair] }) { Parallelism = 3 };
        var reports = new List<BatchProgress>();

        var report = await RunAsync(job, new SynchronousProgress(reports.Add));

        var output = Assert.Single(Assert.Single(report.Items).Outputs);
        Assert.Equal(Path.Combine(_context.OutputDirectory, "MVIMG_IMG_0001.jpg"), output);
        Assert.Equal(5000, MotionPhotoLayout.Inspect(output).Video?.Length);
        Assert.Equal(1, _engines.VideoConvertersCreated);
        Assert.Equal(3, _engines.MetadataParallelism);
        Assert.Equal(new BatchProgress(1, 1, "IMG_0001.jpg"), Assert.Single(reports));
        Assert.True(File.Exists(photo) && File.Exists(video));
    }

    [Fact]
    public async Task ToApple_SplitsIntoHeicAndMovWithVideoConverter()
    {
        var source = _context.CreateInputFile("MVIMG_1.jpg", SyntheticMedia.MotionPhoto(SyntheticMedia.Jpeg(4000), SyntheticMedia.Mp4(6000)));

        var report = await RunAsync(Jobs.Files(ConversionAction.ToApple, _context.OutputDirectory, source) with
        {
            Options = new ConversionOptions { Output = new OutputOptions(_context.OutputDirectory), HeicQuality = 77 }
        });

        var outcome = Assert.Single(report.Items);
        Assert.Equal(OutcomeKind.Succeeded, outcome.Kind);
        Assert.Equal([".HEIC", ".MOV"], outcome.Outputs.Select(o => Path.GetExtension(o).ToUpperInvariant()).Order());
        Assert.Equal(1, _engines.VideoConvertersCreated);
        Assert.Single(_engines.Images.HeicConversions);
        Assert.Single(_engines.Metadata.ApplePhotoIdentifiers);
    }

    [Fact]
    public async Task Extract_SlicesLosslesslyWithoutCreatingVideoConverter()
    {
        var source = _context.CreateInputFile("MVIMG_2.jpg", SyntheticMedia.MotionPhoto(SyntheticMedia.Jpeg(4000), SyntheticMedia.Mp4(6000)));

        var report = await RunAsync(Jobs.Files(ConversionAction.Extract, _context.OutputDirectory, source));

        var outcome = Assert.Single(report.Items);
        Assert.Equal(OutcomeKind.Succeeded, outcome.Kind);
        Assert.Contains(outcome.Outputs, o => Path.GetExtension(o).Equals(".mp4", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, _engines.VideoConvertersCreated);
        Assert.Empty(_engines.Images.HeicConversions);
    }

    [Fact]
    public async Task Strip_WithoutOutput_ReplacesInPlace()
    {
        var source = _context.CreateInputFile("MVIMG_3.jpg", SyntheticMedia.MotionPhoto(SyntheticMedia.Jpeg(10_000), SyntheticMedia.Mp4(50_000)));

        var report = await RunAsync(Jobs.Files(ConversionAction.Strip, null, source));

        var outcome = Assert.Single(report.Items);
        Assert.Equal(Path.Combine(_context.InputDirectory, "MVIMG_3.heic"), Assert.Single(outcome.Outputs));
        Assert.True(outcome.BytesSaved > 50_000);
        Assert.Equal(["MVIMG_3.heic"], _context.GetInputFileNames());
        Assert.Equal(0, _engines.VideoConvertersCreated);
    }

    [Fact]
    public async Task Strip_WithOutputAndNoConversion_ExportsAndKeepsOriginal()
    {
        var source = _context.CreateInputFile("MVIMG_4.jpg", SyntheticMedia.MotionPhoto(SyntheticMedia.Jpeg(10_000), SyntheticMedia.Mp4(50_000)));
        var job = Jobs.Files(ConversionAction.Strip, _context.OutputDirectory, source) with
        {
            Options = new ConversionOptions { Output = new OutputOptions(_context.OutputDirectory), ConvertToHeic = false }
        };

        var report = await RunAsync(job);

        var output = Assert.Single(Assert.Single(report.Items).Outputs);
        Assert.Equal(".jpg", Path.GetExtension(output));
        Assert.Null(MotionPhotoLayout.Locate(output));
        Assert.True(File.Exists(source));
        Assert.Empty(_engines.Images.HeicConversions);
    }

    [Theory]
    [InlineData(ConversionAction.ToAndroid)]
    [InlineData(ConversionAction.ToApple)]
    [InlineData(ConversionAction.Extract)]
    public async Task ConversionsOtherThanStrip_RequireOutputDirectory(ConversionAction action)
    {
        var job = Jobs.Files(action, null, "a.jpg");

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(job));
    }

    [Fact]
    public async Task Cancellation_ReturnsPartialCanceledReport()
    {
        var files = Enumerable.Range(0, 5)
            .Select(i => _context.CreateInputFile($"MVIMG_{i}.jpg", SyntheticMedia.MotionPhoto()))
            .ToArray();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var progress = new SynchronousProgress(p =>
        {
            if (p.Completed == 2)
            {
                cts.Cancel();
            }
        });

        var report = await new ConversionRunner(_engines).RunAsync(
            Jobs.Files(ConversionAction.Extract, _context.OutputDirectory, files) with { Parallelism = 1 },
            progress,
            cts.Token);

        Assert.True(report.Canceled);
        Assert.Equal(2, report.Items.Count);
    }

    [Fact]
    public void ExternalToolEngines_ExplicitMissingTool_ThrowsToolNotFound()
    {
        var missing = new ToolPaths(Path.Combine(_context.RootDirectory, "no-exiftool"), Path.Combine(_context.RootDirectory, "no-ffmpeg"), null);

        Assert.Throws<ToolNotFoundException>(() => ExternalToolEngines.Instance.CreateMetadata(missing, 1));
        Assert.Throws<ToolNotFoundException>(() => ExternalToolEngines.Instance.CreateVideoConverter(missing));
    }

    [Fact]
    public async Task ExternalToolEngines_ExtractWithRealExifTool_ProducesCoverAndVideo()
    {
        if (ToolLocator.Find(ExifToolMetadataService.ExecutableName) is null)
        {
            Assert.Skip("未安装 ExifTool，跳过真实工具集成测试。");
        }

        // ExifTool 会校验 JPEG 结构，封面必须是真实编码的图片
        byte[] cover;
        using (var image = new MagickImage(MagickColors.OrangeRed, 96, 64))
        {
            cover = image.ToByteArray(MagickFormat.Jpeg);
        }

        var source = _context.CreateInputFile("MVIMG_real.jpg", SyntheticMedia.MotionPhoto(cover, SyntheticMedia.Mp4(6000)));

        var report = await new ConversionRunner(ExternalToolEngines.Instance).RunAsync(
            Jobs.Files(ConversionAction.Extract, _context.OutputDirectory, source) with { Parallelism = 1 },
            null,
            Token);

        var outcome = Assert.Single(report.Items);
        Assert.True(outcome.Kind == OutcomeKind.Succeeded, outcome.Message);
        Assert.Equal(2, outcome.Outputs.Count);
        Assert.All(outcome.Outputs, o => Assert.True(File.Exists(o)));
    }

    /// <summary>在汇报线程上同步回调；<see cref="Progress{T}"/> 会投递到同步上下文，时序不可控。</summary>
    private sealed class SynchronousProgress(Action<BatchProgress> onReport) : IProgress<BatchProgress>
    {
        public void Report(BatchProgress value) => onReport(value);
    }
}
