using LivePhotoConvert.Core.External;
using LivePhotoConvert.Core.Abstractions;
using LivePhotoConvert.Core.Media;
using LivePhotoConvert.Core.Metadata;
using LivePhotoConvert.Core.Pairing;
using LivePhotoConvert.Core.Pipeline;
using LivePhotoConvert.Core.Services;

namespace LivePhotoConvert.Core.Tests.Services;

public class MotionPhotoMergerTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly FakeMetadataService _metadata = new();
    private readonly FakeImageConverter _images = new();
    private readonly FakeVideoConverter _videos = new();

    private MotionPhotoMerger CreateMerger() => new(_metadata, _images, _videos);

    private static MergeRequest Request(TempDirectory temp, IEnumerable<string> files, Action<MergeRequestBuilder>? configure = null)
    {
        var builder = new MergeRequestBuilder(MediaPairMatcher.Match(files).Pairs, temp.Combine("out"));
        configure?.Invoke(builder);
        return builder.Build();
    }

    [Fact]
    public async Task MergeAsync_JpegAndMov_ProducesVerifiableMotionPhoto()
    {
        using var temp = new TempDirectory();
        var photo = temp.CreateFile("IMG_0001.jpg", SyntheticMedia.Jpeg(3000));
        var video = temp.CreateFile("IMG_0001.mov", SyntheticMedia.Mov(5000));
        _metadata.Set(video, new MediaMetadata { Path = video, StillImageTimeUs = 1_234_567 });

        var report = await CreateMerger().MergeAsync(Request(temp, [photo, video]), cancellationToken: Token);

        var output = Assert.Single(Assert.Single(report.Items).Outputs);
        Assert.Equal(temp.Combine("out", "MVIMG_IMG_0001.jpg"), output);
        var layout = MotionPhotoLayout.Inspect(output);
        Assert.Equal(5000, layout.Video?.Length);
        Assert.Equal(MetadataScope.StillImageTime, _metadata.LastScope);
        Assert.Equal(1_234_567, Assert.Single(_metadata.MotionPhotoWrites).TimestampUs);
        Assert.True(File.Exists(photo) && File.Exists(video));
        Assert.Equal(["MVIMG_IMG_0001.jpg"], temp.FileNames("out"));
    }

    [Fact]
    public async Task MergeAsync_HeicCover_ConvertsAndCopiesMetadata()
    {
        using var temp = new TempDirectory();
        var photo = temp.CreateFile("IMG_0001.heic", SyntheticMedia.Heic());
        var video = temp.CreateFile("IMG_0001.mov", SyntheticMedia.Mov());

        var report = await CreateMerger().MergeAsync(Request(temp, [photo, video]), cancellationToken: Token);

        Assert.Equal(1, report.Succeeded);
        Assert.Equal([photo], _images.JpegConversions);
        Assert.Equal([photo], _metadata.CoverCopies);
    }

    [Fact]
    public async Task MergeAsync_MirroredMp4_IsTranscodedEvenThoughAlreadyMp4()
    {
        using var temp = new TempDirectory();
        var photo = temp.CreateFile("IMG_0001.jpg", SyntheticMedia.Jpeg());
        var video = temp.CreateFile("IMG_0001.mp4", SyntheticMedia.Mp4());
        _metadata.Set(video, new MediaMetadata { Path = video, IsMirrored = true });

        await CreateMerger().MergeAsync(Request(temp, [photo, video]), cancellationToken: Token);

        var (source, options) = Assert.Single(_videos.Mp4Conversions);
        Assert.Equal(video, source);
        Assert.True(options.BakeOrientation);
    }

    [Fact]
    public async Task MergeAsync_MultipleFormats_PicksHighestPriorityWithoutReportingSupersededCandidates()
    {
        using var temp = new TempDirectory();
        var heic = temp.CreateFile("IMG_0001.heic", SyntheticMedia.Heic());
        var jpg = temp.CreateFile("IMG_0001.jpg", SyntheticMedia.Jpeg());
        var mov = temp.CreateFile("IMG_0001.mov", SyntheticMedia.Mov());

        var report = await CreateMerger().MergeAsync(Request(temp, [heic, jpg, mov]), cancellationToken: Token);

        Assert.Equal(heic, Assert.Single(report.Items).Source);
        Assert.Equal(1, report.Succeeded);
    }

    [Fact]
    public async Task MergeAsync_ValidationFails_SkipsAndKeepsSources()
    {
        using var temp = new TempDirectory();
        var photo = temp.CreateFile("IMG_0001.jpg", SyntheticMedia.Jpeg());
        var video = temp.CreateFile("IMG_0001.mov", SyntheticMedia.Mov());
        _metadata.Set(photo, new MediaMetadata { Path = photo, ContentIdentifier = "ONLY-PHOTO" });

        var report = await CreateMerger().MergeAsync(Request(temp, [photo, video], b => b.SourceAction = SourceFileAction.Delete), cancellationToken: Token);

        var item = Assert.Single(report.Items);
        Assert.Equal(OutcomeKind.Skipped, item.Kind);
        Assert.Equal([(OutcomeCause)OutcomeReason.PairContentIdentifierPhotoOnly], item.Causes);
        Assert.Null(item.Detail);
        Assert.True(File.Exists(photo) && File.Exists(video));
        Assert.Empty(temp.FileNames("out"));
    }

    [Fact]
    public async Task MergeAsync_ForceAccepted_BypassesValidationAndWinsOverHigherPriority()
    {
        using var temp = new TempDirectory();
        var heic = temp.CreateFile("IMG_0001.heic", SyntheticMedia.Heic());
        var jpg = temp.CreateFile("IMG_0001.jpg", SyntheticMedia.Jpeg());
        var mov = temp.CreateFile("IMG_0001.mov", SyntheticMedia.Mov());
        _metadata.Set(mov, new MediaMetadata { Path = mov, Duration = TimeSpan.FromHours(1) });

        var report = await CreateMerger().MergeAsync(
            Request(temp, [heic, jpg, mov], b => b.ForceAccepted = [new MediaPair(jpg, mov)]),
            cancellationToken: Token);

        Assert.Equal(jpg, Assert.Single(report.Items, item => item.Kind == OutcomeKind.Succeeded).Source);
    }

    [Fact]
    public async Task MergeAsync_ForceAcceptedPairWithDifferentStems_IsInjected()
    {
        using var temp = new TempDirectory();
        var photo = temp.CreateFile("IMG_0001.jpg", SyntheticMedia.Jpeg());
        var video = temp.CreateFile("VID_9999.mov", SyntheticMedia.Mov());

        var report = await CreateMerger().MergeAsync(
            new MergeRequest { Candidates = [], ForceAccepted = [new MediaPair(photo, video)], Output = new OutputOptions(temp.Combine("out")) },
            cancellationToken: Token);

        Assert.Equal(1, report.Succeeded);
    }

    [Fact]
    public async Task MergeAsync_XiaomiCleanNaming_SameSecondGetsDistinctNames()
    {
        using var temp = new TempDirectory();
        string[] files =
        [
            temp.CreateFile("IMG_0001.jpg", SyntheticMedia.Jpeg()), temp.CreateFile("IMG_0001.mov", SyntheticMedia.Mov()),
            temp.CreateFile("IMG_0002.jpg", SyntheticMedia.Jpeg()), temp.CreateFile("IMG_0002.mov", SyntheticMedia.Mov())
        ];
        CaptureTime.TryParse("2024:05:01 14:03:03", out var sameSecond);
        foreach (var file in files)
        {
            _metadata.Set(file, new MediaMetadata { Path = file, CaptureTime = sameSecond });
        }

        var report = await CreateMerger().MergeAsync(
            Request(temp, files, b => { b.Naming = MergeNamingFormat.XiaomiClean; b.Conflict = ConflictPolicy.Overwrite; }),
            cancellationToken: Token);

        Assert.Equal(2, report.Succeeded);
        Assert.Equal(["MVIMG_20240501_140303.jpg", "MVIMG_20240501_140303_1.jpg"], temp.FileNames("out"));
    }

    [Fact]
    public async Task MergeAsync_NamingFallsBackToFileNameTime()
    {
        using var temp = new TempDirectory();
        var photo = temp.CreateFile("IMG_20230102_030405.jpg", SyntheticMedia.Jpeg());
        var video = temp.CreateFile("IMG_20230102_030405.mov", SyntheticMedia.Mov());

        await CreateMerger().MergeAsync(Request(temp, [photo, video], b => b.Naming = MergeNamingFormat.XiaomiWithOriginal), cancellationToken: Token);

        Assert.Equal(["MVIMG_20230102_030405_IMG_20230102_030405.jpg"], temp.FileNames("out"));
    }

    [Fact]
    public async Task MergeAsync_WriteFails_LeavesNoPartialOutputAndDoesNotCleanSources()
    {
        using var temp = new TempDirectory();
        var photo = temp.CreateFile("IMG_0001.jpg", SyntheticMedia.Jpeg());
        var video = temp.CreateFile("IMG_0001.mov", SyntheticMedia.Mov());
        _metadata.FailWrites = _ => true;

        var report = await CreateMerger().MergeAsync(Request(temp, [photo, video], b => b.SourceAction = SourceFileAction.Delete), cancellationToken: Token);

        var item = Assert.Single(report.Items);
        Assert.Equal(OutcomeKind.Failed, item.Kind);
        Assert.Equal(OutcomeReason.Unexpected, item.Reason);
        Assert.Contains("模拟 ExifTool 写入失败", item.Detail);
        Assert.True(File.Exists(photo) && File.Exists(video));
        Assert.Empty(temp.FileNames("out"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MergeAsync_ZeroByteInput_FailsWithoutOutputOrCleanup(bool emptyPhoto)
    {
        using var temp = new TempDirectory();
        var photo = temp.CreateFile("IMG_0001.jpg", emptyPhoto ? [] : SyntheticMedia.Jpeg());
        var video = temp.CreateFile("IMG_0001.mov", emptyPhoto ? SyntheticMedia.Mov() : []);

        var report = await CreateMerger().MergeAsync(Request(temp, [photo, video], b => b.SourceAction = SourceFileAction.Delete), cancellationToken: Token);

        var item = Assert.Single(report.Items);
        Assert.Equal(OutcomeKind.Failed, item.Kind);
        Assert.Equal([new OutcomeCause(OutcomeReason.SourceMissingOrEmpty, emptyPhoto ? "IMG_0001.jpg" : "IMG_0001.mov")], item.Causes);
        Assert.True(File.Exists(photo) && File.Exists(video));
        Assert.Empty(temp.FileNames("out"));
    }

    [Fact]
    public async Task MergeAsync_MoveAction_ArchivesSourcesOnlyAfterSuccess()
    {
        using var temp = new TempDirectory();
        var photo = temp.CreateFile("IMG_0001.jpg", SyntheticMedia.Jpeg());
        var video = temp.CreateFile("IMG_0001.mov", SyntheticMedia.Mov());

        var report = await CreateMerger().MergeAsync(Request(temp, [photo, video], b =>
        {
            b.SourceAction = SourceFileAction.Move;
            b.ArchiveFolderName = "Merged";
        }), cancellationToken: Token);

        Assert.Null(Assert.Single(report.Items).CleanupError);
        Assert.Equal(["IMG_0001.jpg", "IMG_0001.mov"], temp.FileNames("Merged"));
    }

    [Fact]
    public async Task MergeAsync_OutputIntoSourceDirectoryWithOverwrite_NeverTouchesSources()
    {
        using var temp = new TempDirectory();
        var photo = temp.CreateFile("MVIMG_IMG_0001.jpg", SyntheticMedia.Jpeg());
        var video = temp.CreateFile("MVIMG_IMG_0001.mov", SyntheticMedia.Mov());
        var original = await File.ReadAllBytesAsync(photo, Token);

        var report = await CreateMerger().MergeAsync(
            new MergeRequest
            {
                Candidates = MediaPairMatcher.Match([photo, video]).Pairs,
                Output = new OutputOptions(temp.Root) { Conflict = ConflictPolicy.Overwrite },
                Naming = MergeNamingFormat.Original
            },
            cancellationToken: Token);

        Assert.Equal(1, report.Succeeded);
        Assert.Equal(original, await File.ReadAllBytesAsync(photo, Token));
    }

    [Fact]
    public async Task MergeAsync_PreserveHierarchy_MirrorsSubfolders()
    {
        using var temp = new TempDirectory();
        var photo = temp.CreateFile("album/2024/IMG_0001.jpg", SyntheticMedia.Jpeg());
        var video = temp.CreateFile("album/2024/IMG_0001.mov", SyntheticMedia.Mov());

        await CreateMerger().MergeAsync(Request(temp, [photo, video], b => b.PreserveHierarchyFrom = temp.Combine("album")), cancellationToken: Token);

        Assert.True(File.Exists(temp.Combine("out", "2024", "MVIMG_IMG_0001.jpg")));
    }

    [Fact]
    public async Task MergeAsync_CanceledBeforeStart_ThrowsAndWritesNothing()
    {
        using var temp = new TempDirectory();
        var photo = temp.CreateFile("IMG_0001.jpg", SyntheticMedia.Jpeg());
        var video = temp.CreateFile("IMG_0001.mov", SyntheticMedia.Mov());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateMerger().MergeAsync(Request(temp, [photo, video]), cancellationToken: cts.Token));
        Assert.Empty(temp.FileNames("out"));
    }

    private sealed class MergeRequestBuilder(IReadOnlyList<MediaPair> candidates, string output)
    {
        public IReadOnlyCollection<MediaPair> ForceAccepted { get; set; } = [];
        public MergeNamingFormat Naming { get; set; }
        public SourceFileAction SourceAction { get; set; }
        public string? ArchiveFolderName { get; set; }
        public ConflictPolicy Conflict { get; set; }
        public string? PreserveHierarchyFrom { get; set; }

        public MergeRequest Build() => new()
        {
            Candidates = candidates,
            ForceAccepted = ForceAccepted,
            Output = new OutputOptions(output) { Conflict = Conflict, PreserveHierarchyFrom = PreserveHierarchyFrom },
            Naming = Naming,
            SourceAction = SourceAction,
            ArchiveFolderName = ArchiveFolderName,
            Parallelism = 4
        };
    }

    [Theory]
    [InlineData(VideoConversionError.EncodeFailed, OutcomeReason.VideoConversionFailed)]
    [InlineData(VideoConversionError.HdrEncoderUnavailable, OutcomeReason.HdrEncoderUnavailable)]
    public async Task MergeAsync_VideoConversionFails_ReportsReasonFromError(VideoConversionError error, OutcomeReason expected)
    {
        using var temp = new TempDirectory();
        var photo = temp.CreateFile("IMG_0001.jpg", SyntheticMedia.Jpeg());
        var video = temp.CreateFile("IMG_0001.mov", SyntheticMedia.Mov());
        _videos.Failure = new VideoConversionException(error, "FFmpeg 诊断信息");

        var report = await CreateMerger().MergeAsync(Request(temp, [photo, video]), cancellationToken: Token);

        var item = Assert.Single(report.Items);
        Assert.Equal(OutcomeKind.Failed, item.Kind);
        Assert.Equal([(OutcomeCause)expected], item.Causes);
        Assert.Equal("FFmpeg 诊断信息", item.Detail);
        Assert.Empty(temp.FileNames("out"));
    }

    [Fact]
    public async Task MergeAsync_ToolMissing_ReportsToolName()
    {
        using var temp = new TempDirectory();
        var photo = temp.CreateFile("IMG_0001.jpg", SyntheticMedia.Jpeg());
        var video = temp.CreateFile("IMG_0001.mov", SyntheticMedia.Mov());
        _videos.Failure = new ToolNotFoundException("ffmpeg");

        var report = await CreateMerger().MergeAsync(Request(temp, [photo, video]), cancellationToken: Token);

        Assert.Equal([new OutcomeCause(OutcomeReason.ToolMissing, "ffmpeg")], Assert.Single(report.Items).Causes);
    }

    [Fact]
    public async Task MergeAsync_AllCandidatesOfGroupRejected_ReportsDistinctCauses()
    {
        using var temp = new TempDirectory();
        var heic = temp.CreateFile("IMG_0001.heic", SyntheticMedia.Heic());
        var jpg = temp.CreateFile("IMG_0001.jpg", SyntheticMedia.Jpeg());
        var mov = temp.CreateFile("IMG_0001.mov", SyntheticMedia.Mov());
        Assert.True(CaptureTime.TryParse("2024:05:01 14:03:03", out var time));
        _metadata.Set(heic, new MediaMetadata { Path = heic, CaptureTime = time });
        _metadata.Set(jpg, new MediaMetadata { Path = jpg, CaptureTime = time });

        var report = await CreateMerger().MergeAsync(Request(temp, [heic, jpg, mov]), cancellationToken: Token);

        var item = Assert.Single(report.Items);
        Assert.Equal(OutcomeKind.Skipped, item.Kind);
        Assert.Equal([(OutcomeCause)OutcomeReason.PairCaptureTimePhotoOnly], item.Causes);
    }
}
