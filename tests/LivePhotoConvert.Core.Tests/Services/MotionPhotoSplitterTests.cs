using LivePhotoConvert.Core.Media;
using LivePhotoConvert.Core.Metadata;
using LivePhotoConvert.Core.Pipeline;
using LivePhotoConvert.Core.Services;

namespace LivePhotoConvert.Core.Tests.Services;

public class MotionPhotoSplitterTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly FakeMetadataService _metadata = new();
    private readonly FakeImageConverter _images = new();
    private readonly FakeVideoConverter _videos = new();

    private Task<BatchReport> SplitAsync(TempDirectory temp, IReadOnlyList<string> files, SplitTarget target = SplitTarget.Extract, SourceFileAction action = SourceFileAction.Keep, string? output = null, ConflictPolicy conflict = ConflictPolicy.AppendIndex) =>
        new MotionPhotoSplitter(_metadata, _images, _videos).SplitAsync(
            new SplitRequest
            {
                Files = files,
                Output = new OutputOptions(output ?? temp.Combine("out")) { Conflict = conflict },
                Target = target,
                SourceAction = action
            },
            cancellationToken: Token);

    [Fact]
    public async Task Extract_ProducesByteExactCoverAndVideo()
    {
        using var temp = new TempDirectory();
        var video = SyntheticMedia.Mp4(5000);
        var source = temp.CreateFile("MVIMG_0001.jpg", SyntheticMedia.MotionPhoto(SyntheticMedia.Jpeg(3000), video));

        var report = await SplitAsync(temp, [source]);

        Assert.Equal(1, report.Succeeded);
        Assert.Equal(["MVIMG_0001.jpg", "MVIMG_0001.mp4"], temp.FileNames("out"));
        Assert.Equal(video, await File.ReadAllBytesAsync(temp.Combine("out", "MVIMG_0001.mp4"), Token));
        Assert.Null(MotionPhotoLayout.Locate(temp.Combine("out", "MVIMG_0001.jpg")));
    }

    [Fact]
    public async Task Extract_UltraHdrMotionPhoto_KeepsGainMapInCover()
    {
        using var temp = new TempDirectory();
        var gainMap = SyntheticMedia.Jpeg(777);
        var source = temp.CreateFile("PXL_0001.jpg", SyntheticMedia.MotionPhotoWithGainMap(gainMap));

        await SplitAsync(temp, [source]);

        var cover = temp.Combine("out", "PXL_0001.jpg");
        var layout = MotionPhotoLayout.Inspect(cover);
        Assert.Null(layout.Video);
        Assert.True(layout.HasGainMap);
        Assert.EndsWith(Convert.ToHexString(gainMap), Convert.ToHexString(await File.ReadAllBytesAsync(cover, Token)));
    }

    [Fact]
    public async Task Apple_WritesIdentifierPairAndCarriesCaptureMetadata()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateFile("MVIMG_0001.jpg", SyntheticMedia.MotionPhoto());
        CaptureTime.TryParse("2024:05:01 14:03:03+08:00", out var captureTime);
        _metadata.Set(source, new MediaMetadata { Path = source, CaptureTime = captureTime, Make = "Xiaomi" });

        var report = await SplitAsync(temp, [source], SplitTarget.Apple);

        var outputs = Assert.Single(report.Items).Outputs;
        Assert.Equal([temp.Combine("out", "MVIMG_0001.HEIC"), temp.Combine("out", "MVIMG_0001.MOV")], outputs);
        var photoId = Assert.Single(_metadata.ApplePhotoIdentifiers).Value;
        var videoTags = Assert.Single(_metadata.AppleVideoTags).Value;
        Assert.Equal(photoId, videoTags.ContentIdentifier);
        Assert.Equal(captureTime, videoTags.CaptureTime);
        Assert.Equal("Xiaomi", videoTags.Make);
        Assert.Single(_images.HeicConversions);
    }

    [Fact]
    public async Task PlainImage_IsSkippedAndNeverCleaned()
    {
        using var temp = new TempDirectory();
        var plain = temp.CreateFile("IMG_0001.jpg", SyntheticMedia.Jpeg());

        var report = await SplitAsync(temp, [plain], action: SourceFileAction.Delete);

        Assert.Equal(OutcomeKind.Skipped, Assert.Single(report.Items).Kind);
        Assert.True(File.Exists(plain));
    }

    [Fact]
    public async Task StaleOffset_IsSkippedInsteadOfCuttingAtWrongPosition()
    {
        using var temp = new TempDirectory();
        var bytes = SyntheticMedia.MotionPhoto(SyntheticMedia.Jpeg(30_000), SyntheticMedia.Mp4(5000));
        var source = temp.CreateFile("edited.jpg", bytes[..^5000]);

        var report = await SplitAsync(temp, [source], action: SourceFileAction.Delete);

        Assert.Equal(OutcomeKind.Skipped, Assert.Single(report.Items).Kind);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task OutputIntoSourceDirectoryWithOverwrite_NeverDeletesSource()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateFile("MVIMG_0001.jpg", SyntheticMedia.MotionPhoto());
        var original = await File.ReadAllBytesAsync(source, Token);

        var report = await SplitAsync(temp, [source], output: temp.Root, conflict: ConflictPolicy.Overwrite);

        Assert.Equal(1, report.Succeeded);
        Assert.Equal(original, await File.ReadAllBytesAsync(source, Token));
        Assert.Contains("MVIMG_0001_1.jpg", temp.FileNames());
    }

    [Fact]
    public async Task FailedItem_IsNeverCleaned()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateFile("MVIMG_0001.jpg", SyntheticMedia.MotionPhoto());
        _metadata.FailWrites = _ => true;

        var report = await SplitAsync(temp, [source], action: SourceFileAction.Delete);

        Assert.Equal(OutcomeKind.Failed, Assert.Single(report.Items).Kind);
        Assert.True(File.Exists(source));
        Assert.Empty(temp.FileNames("out"));
    }

    [Fact]
    public async Task MoveAction_ArchivesNextToSourceWithUniqueName()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateFile("MVIMG_0001.jpg", SyntheticMedia.MotionPhoto());
        temp.CreateFile(Path.Combine(SourceDisposition.SplitFolderName, "MVIMG_0001.jpg"), [1]);

        await SplitAsync(temp, [source], action: SourceFileAction.Move);

        Assert.Equal(["MVIMG_0001.jpg", "MVIMG_0001_1.jpg"], temp.FileNames(SourceDisposition.SplitFolderName));
    }

    [Fact]
    public async Task ManyFilesWithSameName_InDifferentFolders_AllSucceed()
    {
        using var temp = new TempDirectory();
        var files = Enumerable.Range(0, 30).Select(i => temp.CreateFile($"d{i}/MVIMG.jpg", SyntheticMedia.MotionPhoto())).ToList();

        var report = await SplitAsync(temp, files);

        Assert.Equal(30, report.Succeeded);
        Assert.Equal(60, temp.FileNames("out").Length);
    }

    [Fact]
    public async Task SamsungTrailer_IsExtracted()
    {
        using var temp = new TempDirectory();
        var video = SyntheticMedia.Mp4(4000);
        var source = temp.CreateFile("20240501_140303.jpg", SyntheticMedia.SamsungMotionPhoto(SyntheticMedia.Jpeg(), video));

        await SplitAsync(temp, [source]);

        Assert.Equal(video, await File.ReadAllBytesAsync(temp.Combine("out", "20240501_140303.mp4"), Token));
    }

    [Fact]
    public async Task OutputIntoSourceDirectoryWithOverwrite_SourceDifferingOnlyInCase_IsNeverOverwritten()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateFile("MVIMG_0001.JPG", SyntheticMedia.MotionPhoto());
        var original = await File.ReadAllBytesAsync(source, Token);

        var report = await SplitAsync(temp, [source], output: temp.Root, conflict: ConflictPolicy.Overwrite);

        Assert.Equal([temp.Combine("MVIMG_0001_1.jpg"), temp.Combine("MVIMG_0001_1.mp4")], Assert.Single(report.Items).Outputs);
        Assert.Equal(original, await File.ReadAllBytesAsync(source, Token));
    }

    [Fact]
    public async Task Extract_HeicMotionPhotoWithoutXmp_CoverIsExactlyTheHeic()
    {
        using var temp = new TempDirectory();
        var heic = SyntheticMedia.Heic(3000);
        var video = SyntheticMedia.Mp4(4000);
        var source = temp.CreateFile("MVIMG_0001.heic", SyntheticMedia.HeicMotionPhoto(heic, video));

        var report = await SplitAsync(temp, [source]);

        Assert.Equal(1, report.Succeeded);
        Assert.Equal(heic, await File.ReadAllBytesAsync(temp.Combine("out", "MVIMG_0001.heic"), Token));
        Assert.Equal(video, await File.ReadAllBytesAsync(temp.Combine("out", "MVIMG_0001.mp4"), Token));
    }

    [Fact]
    public async Task Extract_OutputsCarrySourceTimestamp()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateFile("MVIMG_0001.jpg", SyntheticMedia.MotionPhoto());
        var old = new DateTime(2020, 5, 1, 8, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, old);

        var report = await SplitAsync(temp, [source]);

        Assert.All(Assert.Single(report.Items).Outputs, output => Assert.Equal(old, File.GetLastWriteTimeUtc(output)));
    }
}
