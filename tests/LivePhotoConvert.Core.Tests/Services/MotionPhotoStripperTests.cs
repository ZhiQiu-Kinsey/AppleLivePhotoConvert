using LivePhotoConvert.Core.External;
using LivePhotoConvert.Core.Media;
using LivePhotoConvert.Core.Metadata;
using LivePhotoConvert.Core.Pipeline;
using LivePhotoConvert.Core.Services;

namespace LivePhotoConvert.Core.Tests.Services;

public class MotionPhotoStripperTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly FakeMetadataService _metadata = new();
    private readonly FakeImageConverter _images = new() { HeicSize = 900 };

    private MotionPhotoStripper CreateStripper() => new(_metadata, _images);

    private Task<BatchReport> StripAsync(IReadOnlyList<string> files, string? output = null, bool convert = true) =>
        CreateStripper().StripAsync(
            new StripRequest { Files = files, Output = output is null ? null : new OutputOptions(output), ConvertToHeic = convert },
            cancellationToken: Token);

    [Fact]
    public async Task AnalyzeAsync_IsReadOnlyAndEstimatesSavings()
    {
        using var temp = new TempDirectory();
        var motion = temp.CreateFile("MVIMG.jpg", SyntheticMedia.MotionPhoto(SyntheticMedia.Jpeg(10_000), SyntheticMedia.Mp4(50_000)));
        var plain = temp.CreateFile("plain.jpg", SyntheticMedia.Jpeg(10_000));
        var before = Directory.EnumerateFiles(temp.Root).ToDictionary(f => f, File.ReadAllBytes);

        var candidates = await CreateStripper().AnalyzeAsync([motion, plain], Token);

        Assert.Equal(50_000, candidates[0].VideoBytes);
        Assert.Null(candidates[1].EmbeddedVideo);
        Assert.True(candidates[0].EstimateFinalBytes(convertToHeic: false) < candidates[0].OriginalBytes);
        Assert.All(before, entry => Assert.Equal(entry.Value, File.ReadAllBytes(entry.Key)));
    }

    [Fact]
    public async Task EstimateFinalBytes_UsesPhotoBytesAndGivenHeicRatio()
    {
        using var temp = new TempDirectory();
        var motion = temp.CreateFile("MVIMG.jpg", SyntheticMedia.MotionPhoto(SyntheticMedia.Jpeg(10_000), SyntheticMedia.Mp4(50_000)));
        var heic = temp.CreateFile("IMG.heic", SyntheticMedia.Heic(4_000));

        var candidates = await CreateStripper().AnalyzeAsync([motion, heic], Token);
        var candidate = candidates[0];

        Assert.Equal(candidate.ImageBytes - candidate.VideoBytes, candidate.PhotoBytes);
        Assert.Equal(candidate.PhotoBytes, candidate.EstimateFinalBytes(convertToHeic: false, heicSizeRatio: 0.3));
        Assert.Equal((long)(candidate.PhotoBytes * 0.3), candidate.EstimateFinalBytes(convertToHeic: true, heicSizeRatio: 0.3));
        Assert.Equal(candidate.EstimateFinalBytes(true, StripCandidate.DefaultHeicSizeRatio), candidate.EstimateFinalBytes(convertToHeic: true));
        // 已是 HEIC 的照片不转码，比例不起作用
        Assert.Equal(candidates[1].PhotoBytes, candidates[1].EstimateFinalBytes(convertToHeic: true, heicSizeRatio: 0.3));
        Assert.Throws<ArgumentOutOfRangeException>(() => candidate.EstimateFinalBytes(true, 0));
    }

    [Fact]
    public async Task InPlace_StripsVideoAndConvertsToHeic_DeletingOnlyTheOriginal()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateFile("MVIMG.jpg", SyntheticMedia.MotionPhoto(SyntheticMedia.Jpeg(10_000), SyntheticMedia.Mp4(50_000)));

        var report = await StripAsync([source]);

        var outcome = Assert.Single(report.Items);
        Assert.Equal(temp.Combine("MVIMG.heic"), Assert.Single(outcome.Outputs));
        Assert.Equal(["MVIMG.heic"], temp.FileNames());
        Assert.True(outcome.BytesSaved > 50_000);
    }

    [Fact]
    public async Task InPlace_WithoutConversion_ReplacesOriginalAtomicallyAndRemovesBackup()
    {
        using var temp = new TempDirectory();
        var cover = SyntheticMedia.Jpeg(10_000);
        var source = temp.CreateFile("MVIMG.jpg", SyntheticMedia.MotionPhoto(cover, SyntheticMedia.Mp4(50_000)));

        await StripAsync([source], convert: false);

        Assert.Equal(["MVIMG.jpg"], temp.FileNames());
        Assert.Null(MotionPhotoLayout.Locate(source));
        Assert.True(new FileInfo(source).Length < 20_000);
    }

    [Fact]
    public async Task InPlace_JpgAndJpegWithSameStem_BothSurviveAsSeparateHeics()
    {
        using var temp = new TempDirectory();
        var jpg = temp.CreateFile("IMG_1.jpg", SyntheticMedia.MotionPhoto());
        var jpeg = temp.CreateFile("IMG_1.jpeg", SyntheticMedia.MotionPhoto());
        var unrelated = temp.CreateFile("IMG_1.heic", SyntheticMedia.Heic(7777));

        var report = await StripAsync([jpg, jpeg]);

        Assert.Equal(2, report.Succeeded);
        Assert.Equal(7777, new FileInfo(unrelated).Length);
        Assert.Equal(["IMG_1.heic", "IMG_1_1.heic", "IMG_1_2.heic"], temp.FileNames());
    }

    [Fact]
    public async Task UltraHdrStill_IsNotConvertedSoHdrIsPreserved()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateFile("PXL.jpg", SyntheticMedia.UltraHdrStill(SyntheticMedia.Jpeg(700)));
        var original = await File.ReadAllBytesAsync(source, Token);

        var report = await StripAsync([source]);

        var item = Assert.Single(report.Items);
        Assert.Equal(OutcomeKind.Skipped, item.Kind);
        Assert.Equal(OutcomeReason.GainMapPreserved, item.Reason);
        Assert.Equal(original, await File.ReadAllBytesAsync(source, Token));
        Assert.Empty(_images.HeicConversions);
    }

    [Fact]
    public async Task UltraHdrMotionPhoto_StripsVideoButKeepsJpegWithGainMap()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateFile("PXL.jpg", SyntheticMedia.MotionPhotoWithGainMap(SyntheticMedia.Jpeg(700)));

        await StripAsync([source]);

        var layout = MotionPhotoLayout.Inspect(source);
        Assert.Null(layout.Video);
        Assert.True(layout.HasGainMap);
        Assert.Empty(_images.HeicConversions);
    }

    [Fact]
    public async Task StaleOffset_IsNotCut()
    {
        using var temp = new TempDirectory();
        var bytes = SyntheticMedia.MotionPhoto(SyntheticMedia.Jpeg(30_000), SyntheticMedia.Mp4(5000));
        var edited = bytes[..^5000];
        var source = temp.CreateFile("edited.jpg", edited);

        await StripAsync([source], convert: false);

        Assert.Equal(edited, await File.ReadAllBytesAsync(source, Token));
    }

    [Fact]
    public async Task CompanionVideo_OnlyRemovedWhenValidated()
    {
        using var temp = new TempDirectory();
        var photo = temp.CreateFile("DSC_0001.jpg", SyntheticMedia.Jpeg());
        var longRecording = temp.CreateFile("DSC_0001.mov", SyntheticMedia.Mov());
        _metadata.Set(longRecording, new MediaMetadata { Path = longRecording, Duration = TimeSpan.FromMinutes(10) });

        var candidates = await CreateStripper().AnalyzeAsync([photo], Token);
        await StripAsync([photo], convert: false);

        Assert.Null(candidates[0].CompanionVideo);
        Assert.True(File.Exists(longRecording));
    }

    [Fact]
    public async Task CompanionVideo_ValidatedLivePhotoPair_IsDetected()
    {
        using var temp = new TempDirectory();
        var photo = temp.CreateFile("IMG_0001.heic", SyntheticMedia.Heic());
        var video = temp.CreateFile("IMG_0001.mov", SyntheticMedia.Mov());
        _metadata.Set(photo, new MediaMetadata { Path = photo, ContentIdentifier = "UUID" });
        _metadata.Set(video, new MediaMetadata { Path = video, ContentIdentifier = "UUID" });

        var candidate = Assert.Single(await CreateStripper().AnalyzeAsync([photo], Token));

        Assert.Equal(video, candidate.CompanionVideo);
        Assert.False(candidate.WillConvert(convertToHeic: true));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HeicNotSmaller_KeepsOriginalFormat_WithSameBytesAsStripOnly(bool inPlace)
    {
        using var temp = new TempDirectory();
        var motion = SyntheticMedia.MotionPhoto(SyntheticMedia.Jpeg(10_000), SyntheticMedia.Mp4(50_000));
        var stripOnly = temp.CreateFile("ref/MVIMG.jpg", motion);
        var source = temp.CreateFile("in/MVIMG.jpg", motion);
        await StripAsync([stripOnly], convert: false);
        var expected = await File.ReadAllBytesAsync(stripOnly, Token);
        var images = new FakeImageConverter { HeicSize = expected.Length + 1 };

        var report = await new MotionPhotoStripper(_metadata, images).StripAsync(
            new StripRequest { Files = [source], Output = inPlace ? null : new OutputOptions(temp.Combine("out")) },
            cancellationToken: Token);

        var outcome = Assert.Single(report.Items);
        Assert.Equal(OutcomeKind.Succeeded, outcome.Kind);
        Assert.True(outcome.KeptOriginalFormat);
        Assert.Single(images.HeicConversions);
        var output = Assert.Single(outcome.Outputs);
        Assert.Equal(".jpg", Path.GetExtension(output));
        Assert.Equal(expected, await File.ReadAllBytesAsync(output, Token));
        Assert.Equal(motion.Length - expected.Length, outcome.BytesSaved);
        Assert.Equal(["MVIMG.jpg"], temp.FileNames(inPlace ? "in" : "out"));
        Assert.DoesNotContain(Directory.EnumerateFiles(temp.Root, "*", SearchOption.AllDirectories), f => Path.GetFileName(f).StartsWith("~lpc", StringComparison.Ordinal));
        if (!inPlace)
        {
            Assert.Equal(motion, await File.ReadAllBytesAsync(source, Token));
        }
    }

    [Fact]
    public async Task HeicOfEqualSize_KeepsOriginalFormat_SmallerHeicIsUsed()
    {
        using var temp = new TempDirectory();
        var motion = SyntheticMedia.MotionPhoto(SyntheticMedia.Jpeg(10_000), SyntheticMedia.Mp4(50_000));
        var reference = temp.CreateFile("ref/MVIMG.jpg", motion);
        await StripAsync([reference], convert: false);
        var stripped = (int)new FileInfo(reference).Length;

        var equal = temp.CreateFile("equal/MVIMG.jpg", motion);
        var kept = await new MotionPhotoStripper(_metadata, new FakeImageConverter { HeicSize = stripped })
            .StripAsync(new StripRequest { Files = [equal] }, cancellationToken: Token);
        var smaller = temp.CreateFile("smaller/MVIMG.jpg", motion);
        var converted = await new MotionPhotoStripper(_metadata, new FakeImageConverter { HeicSize = stripped - 1 })
            .StripAsync(new StripRequest { Files = [smaller] }, cancellationToken: Token);

        Assert.True(Assert.Single(kept.Items).KeptOriginalFormat);
        Assert.Equal(["MVIMG.jpg"], temp.FileNames("equal"));
        Assert.False(Assert.Single(converted.Items).KeptOriginalFormat);
        Assert.Equal(["MVIMG.heic"], temp.FileNames("smaller"));
    }

    [Fact]
    public async Task HeicNotSmaller_WithoutVideo_LeavesInPlaceSourceUntouched()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateFile("IMG.jpg", SyntheticMedia.Jpeg(4_000));
        var original = await File.ReadAllBytesAsync(source, Token);

        var report = await new MotionPhotoStripper(_metadata, new FakeImageConverter { HeicSize = 100_000 })
            .StripAsync(new StripRequest { Files = [source] }, cancellationToken: Token);

        var outcome = Assert.Single(report.Items);
        Assert.True(outcome.KeptOriginalFormat);
        Assert.Equal(0, outcome.BytesSaved);
        Assert.Equal(original, await File.ReadAllBytesAsync(source, Token));
        Assert.Equal(["IMG.jpg"], temp.FileNames());
    }

    [Fact]
    public async Task ExportMode_NeverModifiesSources()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateFile("in/MVIMG.jpg", SyntheticMedia.MotionPhoto());
        var original = await File.ReadAllBytesAsync(source, Token);

        var report = await StripAsync([source], output: temp.Combine("in"));

        Assert.Equal(1, report.Succeeded);
        Assert.Equal(original, await File.ReadAllBytesAsync(source, Token));
        Assert.Equal(["MVIMG.heic", "MVIMG.jpg"], temp.FileNames("in"));
    }

    [Fact]
    public async Task ConversionProducingInvalidImage_KeepsOriginal()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateFile("MVIMG.jpg", SyntheticMedia.MotionPhoto());
        var original = await File.ReadAllBytesAsync(source, Token);
        var stripper = new MotionPhotoStripper(_metadata, new BrokenConverter());

        var report = await stripper.StripAsync(new StripRequest { Files = [source] }, cancellationToken: Token);

        var item = Assert.Single(report.Items);
        Assert.Equal(OutcomeKind.Failed, item.Kind);
        Assert.Equal(OutcomeReason.VerificationFailed, item.Reason);
        Assert.NotNull(item.Detail);
        Assert.Equal(original, await File.ReadAllBytesAsync(source, Token));
        Assert.Equal(["MVIMG.jpg"], temp.FileNames());
    }

    [Fact]
    public async Task EmptyFile_FailsWithoutSideEffects()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateFile("empty.jpg", []);

        var report = await StripAsync([source]);

        var item = Assert.Single(report.Items);
        Assert.Equal(OutcomeKind.Failed, item.Kind);
        Assert.Equal([new OutcomeCause(OutcomeReason.SourceMissingOrEmpty, "empty.jpg")], item.Causes);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task AnalyzeAsync_FileVanished_DegradesOnlyThatFile()
    {
        using var temp = new TempDirectory();
        var motion = temp.CreateFile("MVIMG.jpg", SyntheticMedia.MotionPhoto());
        var missing = temp.Combine("gone.jpg");

        var candidates = await CreateStripper().AnalyzeAsync([missing, motion], Token);

        Assert.NotNull(candidates[0].AnalysisError);
        Assert.Equal(new OutcomeCause(OutcomeReason.SourceMissingOrEmpty, "gone.jpg"), candidates[0].AnalysisCause);
        Assert.Null(candidates[1].AnalysisCause);
        Assert.False(candidates[0].HasVideo);
        Assert.False(candidates[0].WillConvert(convertToHeic: true));
        Assert.Null(candidates[1].AnalysisError);
        Assert.NotNull(candidates[1].EmbeddedVideo);
    }

    [Fact]
    public async Task AnalyzeAsync_XmpReadFails_DegradesOnlyThatFile()
    {
        using var temp = new TempDirectory();
        var broken = temp.CreateFile("broken.heic", SyntheticMedia.Heic());
        var motion = temp.CreateFile("MVIMG.jpg", SyntheticMedia.MotionPhoto());
        _metadata.FailXmpReads = path => path.EndsWith("broken.heic", StringComparison.Ordinal);

        var candidates = await CreateStripper().AnalyzeAsync([broken, motion], Token);

        Assert.Contains("broken.heic", candidates[0].AnalysisError);
        Assert.NotNull(candidates[1].EmbeddedVideo);
        // 未归类的读取失败给出原因码，界面据此本地化；原文仍保留在 AnalysisError
        Assert.Equal(new OutcomeCause(OutcomeReason.SourceUnreadable, "broken.heic"), candidates[0].AnalysisCause);
    }

    [Fact]
    public async Task StripAsync_AnalysisFailures_UseTheSameCauseAsAnalysis()
    {
        using var temp = new TempDirectory();
        var broken = temp.CreateFile("broken.heic", SyntheticMedia.Heic());
        var missing = temp.Combine("gone.jpg");
        _metadata.FailXmpReads = path => path.EndsWith("broken.heic", StringComparison.Ordinal);

        var candidates = await CreateStripper().AnalyzeAsync([broken, missing], Token);
        var report = await StripAsync([broken, missing]);

        foreach (var candidate in candidates)
        {
            var item = Assert.Single(report.Items, i => i.Source == candidate.ImagePath);
            Assert.Equal(OutcomeKind.Failed, item.Kind);
            Assert.Equal([candidate.AnalysisCause!], item.Causes);
            Assert.Equal(candidate.AnalysisError, item.Detail);
        }
    }

    [Fact]
    public void AnalysisCause_KeepsClassifiedExceptions()
    {
        var toolMissing = StripCandidate.Unavailable("/in/a.heic", new ToolNotFoundException("exiftool"));
        var directoryGone = StripCandidate.Unavailable("/in/b.jpg", new DirectoryNotFoundException("x"));
        var denied = StripCandidate.Unavailable("/in/c.jpg", new UnauthorizedAccessException("x"));

        Assert.Equal(new OutcomeCause(OutcomeReason.ToolMissing, "exiftool"), toolMissing.AnalysisCause);
        Assert.Equal(new OutcomeCause(OutcomeReason.SourceMissingOrEmpty, "b.jpg"), directoryGone.AnalysisCause);
        Assert.Equal(new OutcomeCause(OutcomeReason.SourceUnreadable, "c.jpg"), denied.AnalysisCause);
    }

    [Fact]
    public async Task AnalyzeAsync_MetadataBatchReadFails_KeepsCompanionVideosUntouched()
    {
        using var temp = new TempDirectory();
        var photo = temp.CreateFile("IMG_0001.heic", SyntheticMedia.Heic());
        var video = temp.CreateFile("IMG_0001.mov", SyntheticMedia.Mov());
        _metadata.BeforeRead = _ => throw new InvalidOperationException("ExifTool 崩溃");

        var candidates = await CreateStripper().AnalyzeAsync([photo], Token);
        var report = await StripAsync([photo]);

        Assert.Null(Assert.Single(candidates).CompanionVideo);
        Assert.Null(candidates[0].AnalysisError);
        Assert.True(File.Exists(video));
        var item = Assert.Single(report.Items);
        Assert.Equal(OutcomeKind.Skipped, item.Kind);
        Assert.Equal(OutcomeReason.NothingToStrip, item.Reason);
    }

    [Fact]
    public async Task AnalyzeAsync_CompanionWithDifferentCase_ReturnsRealFileName()
    {
        using var temp = new TempDirectory();
        var photo = temp.CreateFile("IMG_0001.heic", SyntheticMedia.Heic());
        var video = temp.CreateFile("img_0001.MOV", SyntheticMedia.Mov());
        temp.CreateFile("IMG_0001.mp4", SyntheticMedia.Mp4());
        _metadata.Set(photo, new MediaMetadata { Path = photo, ContentIdentifier = "UUID" });
        _metadata.Set(video, new MediaMetadata { Path = video, ContentIdentifier = "UUID" });

        var candidate = Assert.Single(await CreateStripper().AnalyzeAsync([photo], Token));

        Assert.Equal(video, candidate.CompanionVideo);
    }

    [Fact]
    public async Task AnalyzeAsync_DoesNotRunHeavyWorkOnCallingThread()
    {
        using var temp = new TempDirectory();
        var photo = temp.CreateFile("IMG_0001.heic", SyntheticMedia.Heic());
        temp.CreateFile("IMG_0001.mov", SyntheticMedia.Mov());
        using var gate = new ManualResetEventSlim();
        _metadata.BeforeRead = _ => gate.Wait(TimeSpan.FromSeconds(10), Token);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var analysis = CreateStripper().AnalyzeAsync([photo], Token);
        var callDuration = stopwatch.Elapsed;
        gate.Set();
        await analysis;

        Assert.True(callDuration < TimeSpan.FromSeconds(5), $"调用方被阻塞了 {callDuration.TotalSeconds:F1} 秒");
    }

    [Fact]
    public async Task StripAsync_FileVanishedBeforeAnalysis_FailsOnlyThatItem()
    {
        using var temp = new TempDirectory();
        var motion = temp.CreateFile("MVIMG.jpg", SyntheticMedia.MotionPhoto());

        var report = await StripAsync([temp.Combine("gone.jpg"), motion], convert: false);

        Assert.Equal(1, report.Succeeded);
        var failed = Assert.Single(report.Items, item => item.Kind == OutcomeKind.Failed);
        Assert.Equal([new OutcomeCause(OutcomeReason.SourceMissingOrEmpty, "gone.jpg")], failed.Causes);
        Assert.NotNull(failed.Detail);
    }

    [Fact]
    public async Task StripAsync_AnalysisReadFails_ReportsUnreadableSourceWithDetail()
    {
        using var temp = new TempDirectory();
        var broken = temp.CreateFile("broken.heic", SyntheticMedia.Heic());
        _metadata.FailXmpReads = _ => true;

        var report = await StripAsync([broken]);

        var failed = Assert.Single(report.Items);
        Assert.Equal(new OutcomeCause(OutcomeReason.SourceUnreadable, "broken.heic"), Assert.Single(failed.Causes));
        Assert.Contains("broken.heic", failed.Detail);
    }

    [Fact]
    public async Task InPlace_HeicMotionPhotoWithMpvd_TruncatesExactlyAtBoxStart()
    {
        using var temp = new TempDirectory();
        var heic = SyntheticMedia.Heic(3000);
        var source = temp.CreateFile("MVIMG.heic", SyntheticMedia.HeicMotionPhoto(heic, SyntheticMedia.Mp4(4000)));

        var candidates = await CreateStripper().AnalyzeAsync([source], Token);
        var report = await StripAsync([source], convert: false);

        Assert.Equal(4000 + 8, Assert.Single(candidates).VideoBytes);
        Assert.Equal(1, report.Succeeded);
        Assert.Equal(heic, await File.ReadAllBytesAsync(source, Token));
    }

    [Fact]
    public async Task InPlace_KeepsSourceTimestampOnResult()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateFile("MVIMG.jpg", SyntheticMedia.MotionPhoto());
        var old = new DateTime(2020, 5, 1, 8, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, old);

        var report = await StripAsync([source]);

        Assert.Equal(old, File.GetLastWriteTimeUtc(Assert.Single(Assert.Single(report.Items).Outputs)));
    }

    private sealed class BrokenConverter : Core.Abstractions.IImageConverter
    {
        public Task ConvertToJpegAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default) =>
            File.WriteAllBytesAsync(destinationPath, [0, 0, 0], cancellationToken);

        public Task ConvertToHeicAsync(string sourcePath, string destinationPath, int quality, CancellationToken cancellationToken = default) =>
            File.WriteAllBytesAsync(destinationPath, [0, 0, 0], cancellationToken);
    }
}
