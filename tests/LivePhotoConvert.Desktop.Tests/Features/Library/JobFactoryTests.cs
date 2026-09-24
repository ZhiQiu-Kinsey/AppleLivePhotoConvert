using LivePhotoConvert.Core.Pairing;
using LivePhotoConvert.Core.Pipeline;
using LivePhotoConvert.Core.Services;
using LivePhotoConvert.Core.Tests.Support;
using LivePhotoConvert.Core.Media;
using LivePhotoConvert.Desktop.Features.Library;
using LivePhotoConvert.Desktop.Features.Playback;
using LivePhotoConvert.Desktop.Features.Tasks;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Tests.Harness;
using LivePhotoConvert.Desktop.Tests.Features.Tasks;

namespace LivePhotoConvert.Desktop.Tests.Features.Library;

/// <summary>检查器把图库卡片与设置固定成任务；执行由任务中心承担。</summary>
public class JobFactoryTests
{
    [Fact]
    public void Applicable_FiltersCardsByScannedKind()
    {
        var pair = Cards.ApplePair("IMG_1");
        var motion = Cards.MotionPhoto("MVIMG_2");
        var still = Cards.Still("IMG_3");
        PhotoCards all = [pair, motion, still];

        Assert.Equal([pair], JobFactory.Applicable(ConversionAction.ToAndroid, all));
        Assert.Equal([motion], JobFactory.Applicable(ConversionAction.ToApple, all));
        Assert.Equal([motion], JobFactory.Applicable(ConversionAction.Extract, all));
        Assert.Equal([pair, motion], JobFactory.Applicable(ConversionAction.Strip, all));
    }

    /// <summary>动态照片的视频由播放器按区段直接读取，不切临时文件；卡片仍是动态照片，不会被当成实况对再合成。</summary>
    [Fact]
    public void MotionPhotoVideo_IsReadInPlace_AndCardStaysMotionPhoto()
    {
        using var album = new TestSandbox();
        var video = SyntheticMedia.Mp4(6000);
        var bytes = SyntheticMedia.MotionPhoto(video: video);
        var path = album.CreateInputFile("MVIMG_2.jpg", bytes);
        var offset = bytes.Length - video.Length;
        var motion = Cards.Of(new LibraryItem(LibraryItemKind.MotionPhoto, Cards.File(path, bytes.Length))
        {
            Embedded = new EmbeddedVideo(offset, video.Length, offset)
        });

        Assert.Equal(new VideoSource(path, offset, video.Length, IsEmbedded: true), motion.Video);
        Assert.Equal($"subfile,,start,{offset},end,{bytes.Length},,:{Path.GetFullPath(path)}", motion.Video!.ToFfmpegInput());
        Assert.True(motion.IsMotionPhoto);
        Assert.Empty(JobFactory.Applicable(ConversionAction.ToAndroid, [motion]));
        Assert.Equal(bytes.Length, JobFactory.SourceBytes([motion]));
    }

    [Fact]
    public void SourceBytes_ComeFromScanWithoutTouchingDisk()
    {
        // 卡片指向的文件并不存在：体积来自扫描结果
        Assert.Equal(8000, JobFactory.SourceBytes([Cards.ApplePair("IMG_1")]));
        Assert.Equal(9000, JobFactory.SourceBytes([Cards.MotionPhoto("MVIMG_2")]));
        Assert.Equal(17000, JobFactory.SourceBytes([Cards.ApplePair("IMG_1"), Cards.MotionPhoto("MVIMG_2")]));
    }

    [Fact]
    public void Build_ToAndroid_PassesAllCandidatesAndForcesTheScannedPair()
    {
        var photo = Cards.File("/album/IMG_5.heic");
        var jpg = "/album/IMG_5.jpg";
        var mov = Cards.File("/album/IMG_5.mov");
        MediaPair[] candidates = [new(photo.Path, mov.Path), new(photo.Path, "/album/IMG_5.mp4"), new(jpg, mov.Path)];
        var card = Cards.Of(new LivePhotoConvert.Core.Media.LibraryItem(LivePhotoConvert.Core.Media.LibraryItemKind.ApplePair, photo)
        {
            Video = mov,
            PairCandidates = candidates,
            PairValidation = LivePhotoConvert.Core.Pairing.PairValidationResult.Reject(new OutcomeCause(OutcomeReason.PairCaptureTimeTooFar, 9.0, 3.0))
        });
        card.IsForceAccepted = true;

        var job = JobFactory.Build(ConversionAction.ToAndroid, [card], new DesktopSettings(), "/album");

        Assert.Equal(candidates, job.Inputs.Pairs);
        Assert.Equal([candidates[0]], job.Inputs.ForceAccepted);
        Assert.Equal(1, job.ItemCount);
    }

    [Theory]
    [InlineData(ConversionAction.ToAndroid, false, new[] { RequiredTool.ExifTool, RequiredTool.Ffmpeg })]
    [InlineData(ConversionAction.ToApple, false, new[] { RequiredTool.ExifTool, RequiredTool.Ffmpeg, RequiredTool.HeicEncoder })]
    [InlineData(ConversionAction.Extract, true, new[] { RequiredTool.ExifTool })]
    [InlineData(ConversionAction.Strip, true, new[] { RequiredTool.ExifTool, RequiredTool.HeicEncoder })]
    [InlineData(ConversionAction.Strip, false, new[] { RequiredTool.ExifTool })]
    public void RequiredTools_MatchTheEnginesTheRunnerCreates(ConversionAction action, bool stripToHeic, RequiredTool[] expected)
    {
        Assert.Equal(expected, JobFactory.RequiredTools(action, stripToHeic));
    }

    [Fact]
    public void Build_ToAndroid_SnapshotsPairsForcedPairsAndOptions()
    {
        var settings = new DesktopSettings
        {
            Concurrency = 20,
            FfmpegPath = "/opt/ffmpeg",
            OutputDirectory = "/export",
            NamingFormat = 2,
            ConflictPolicy = ConflictPolicy.Overwrite,
            SourceAction = 1,
            KeepSubfolderHierarchy = true
        };
        var forced = Cards.ApplePair("IMG_2", forced: true);

        var job = JobFactory.Build(ConversionAction.ToAndroid, [Cards.ApplePair("IMG_1"), forced, Cards.MotionPhoto("MVIMG_3")], settings, "/album");

        Assert.Equal(ConversionAction.ToAndroid, job.Action);
        Assert.Equal([new MediaPair("/album/IMG_1.heic", "/album/IMG_1.mov"), new MediaPair("/album/IMG_2.heic", "/album/IMG_2.mov")], job.Inputs.Pairs);
        Assert.Equal([new MediaPair("/album/IMG_2.heic", "/album/IMG_2.mov")], job.Inputs.ForceAccepted);
        Assert.Empty(job.Inputs.Files);
        Assert.Equal("/export", job.Options.Output?.Directory);
        Assert.Equal(ConflictPolicy.Overwrite, job.Options.Output?.Conflict);
        Assert.Equal("/album", job.Options.Output?.PreserveHierarchyFrom);
        Assert.Equal(MergeNamingFormat.XiaomiClean, job.Options.Naming);
        Assert.Equal(SourceFileAction.Move, job.Options.SourceAction);
        Assert.Equal(8, job.Parallelism);
        Assert.Equal("/opt/ffmpeg", job.Tools.Ffmpeg);
        Assert.Null(job.Tools.ExifTool);
    }

    [Theory]
    [InlineData(ConversionAction.ToApple)]
    [InlineData(ConversionAction.Extract)]
    public void Build_SplitActions_UseOnlyMotionPhotos(ConversionAction action)
    {
        var settings = new DesktopSettings { HeicQuality = 70, KeepSubfolderHierarchy = false, OutputDirectory = "/export" };

        var job = JobFactory.Build(action, [Cards.ApplePair("IMG_1"), Cards.MotionPhoto("MVIMG_3")], settings, "/album");

        Assert.Equal(action, job.Action);
        Assert.Equal(["/album/MVIMG_3.jpg"], job.Inputs.Files);
        Assert.Empty(job.Inputs.Pairs);
        Assert.Equal(70, job.Options.HeicQuality);
        Assert.Null(job.Options.Output?.PreserveHierarchyFrom);
    }

    [Fact]
    public void Build_Strip_ExportsOrReplacesInPlace()
    {
        PhotoCards cards = [Cards.ApplePair("IMG_1"), Cards.MotionPhoto("MVIMG_2")];
        var export = new DesktopSettings { StripOutputDirectory = "/slim", StripConvertToHeic = false, HeicQuality = 80 };

        var exported = JobFactory.Build(ConversionAction.Strip, cards, export, "/album");
        Assert.Equal(["/album/IMG_1.heic", "/album/MVIMG_2.jpg"], exported.Inputs.Files);
        Assert.Equal("/slim", exported.Options.Output?.Directory);
        Assert.Null(exported.Options.InPlaceLocation);
        Assert.False(exported.Options.ConvertToHeic);
        Assert.Equal(80, exported.Options.HeicQuality);
        Assert.Equal("/slim", exported.ResultLocation);

        var inPlace = JobFactory.Build(ConversionAction.Strip, cards, new DesktopSettings { InPlaceStrip = true }, "/album");
        Assert.Null(inPlace.Options.Output);
        Assert.Equal("/album", inPlace.Options.InPlaceLocation);
        Assert.True(inPlace.Options.ConvertToHeic);
        Assert.Equal("/album", JobFactory.ResolveTargetDirectory(ConversionAction.Strip, new DesktopSettings { InPlaceStrip = true }, "/album"));
    }

    [Fact]
    public void ResolveDirectories_FallBackToDefaultsWhenUnset()
    {
        var settings = new DesktopSettings();

        Assert.Equal(JobFactory.DefaultOutputDirectory, JobFactory.ResolveTargetDirectory(ConversionAction.ToApple, settings, "/album"));
        Assert.Equal(JobFactory.DefaultStripDirectory, JobFactory.ResolveTargetDirectory(ConversionAction.Strip, settings, "/album"));
        Assert.NotEqual(JobFactory.DefaultOutputDirectory, JobFactory.DefaultStripDirectory);
    }

    [Fact]
    public async Task StripEstimator_AnalyzesWithTaskEnginesAndCountsOnlyPhotosWithVideo()
    {
        using var album = new TestSandbox();
        var motion = album.CreateInputFile("MVIMG_1.jpg", SyntheticMedia.MotionPhoto(video: SyntheticMedia.Mp4(40_000)));
        var still = album.CreateInputFile("IMG_2.jpg", SyntheticMedia.Jpeg(4096));
        var engines = new FakeEngines();

        var estimate = await new StripEstimator(engines).EstimateAsync([motion, still], ToolPaths.Auto, convertToHeic: true, heicQuality: 90, TestContext.Current.CancellationToken);

        Assert.Equal(1, estimate.Count);
        Assert.Equal(new FileInfo(motion).Length, estimate.OriginalBytes);
        Assert.True(estimate.SavedBytes >= 40_000, $"至少释放内嵌视频的体积，实际 {estimate.SavedBytes}");
        Assert.InRange(estimate.SavedPercent, 1, 100);
        Assert.Equal(0, engines.VideoConvertersCreated);
    }
}

/// <summary>集合表达式可直接构造的卡片列表别名。</summary>
internal sealed class PhotoCards : List<Desktop.Models.PhotoCardItemViewModel>;
