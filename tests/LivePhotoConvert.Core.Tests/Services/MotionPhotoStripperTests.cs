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

    private sealed class BrokenConverter : Core.Abstractions.IImageConverter
    {
        public Task ConvertToJpegAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default) =>
            File.WriteAllBytesAsync(destinationPath, [0, 0, 0], cancellationToken);

        public Task ConvertToHeicAsync(string sourcePath, string destinationPath, int quality, CancellationToken cancellationToken = default) =>
            File.WriteAllBytesAsync(destinationPath, [0, 0, 0], cancellationToken);
    }
}
