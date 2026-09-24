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

        Assert.Equal(OutcomeKind.Skipped, Assert.Single(report.Items).Kind);
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

        Assert.Equal(OutcomeKind.Failed, Assert.Single(report.Items).Kind);
        Assert.Equal(original, await File.ReadAllBytesAsync(source, Token));
        Assert.Equal(["MVIMG.jpg"], temp.FileNames());
    }

    [Fact]
    public async Task EmptyFile_FailsWithoutSideEffects()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateFile("empty.jpg", []);

        var report = await StripAsync([source]);

        Assert.Equal(OutcomeKind.Failed, Assert.Single(report.Items).Kind);
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
        Assert.Equal(OutcomeKind.Skipped, Assert.Single(report.Items).Kind);
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
        Assert.Single(report.Items, item => item.Kind == OutcomeKind.Failed);
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
