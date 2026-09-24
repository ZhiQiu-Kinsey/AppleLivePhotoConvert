using LivePhotoConvert.Core.Pairing;
using LivePhotoConvert.Core.Pipeline;
using LivePhotoConvert.Core.Services;
using LivePhotoConvert.Core.Tests.Support;
using LivePhotoConvert.Desktop.Features.Library;
using LivePhotoConvert.Desktop.Features.Tasks;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Tests.Harness;
using LivePhotoConvert.Desktop.Tests.Features.Tasks;

namespace LivePhotoConvert.Desktop.Tests.Features.Library;

/// <summary>检查器把图库卡片与设置固定成任务；执行由任务中心承担。</summary>
public class JobFactoryTests
{
    [Theory]
    [InlineData(ConversionAction.ToAndroid, ScanModes.ApplePairs)]
    [InlineData(ConversionAction.ToApple, ScanModes.MotionPhotos)]
    [InlineData(ConversionAction.Extract, ScanModes.All)]
    [InlineData(ConversionAction.Strip, ScanModes.All)]
    public void ScanModes_MapEachActionToTheScannerModeListingItsInputs(ConversionAction action, int expected)
    {
        Assert.Equal(expected, ScanModes.For(action));
    }

    [Fact]
    public void Applicable_FiltersCardsPerAction()
    {
        var pair = Cards.ApplePair("IMG_1");
        var motion = Cards.MotionPhoto("MVIMG_2");
        var still = Cards.Still("IMG_3");
        PhotoCards all = [pair, motion, still];

        Assert.Equal([pair], JobFactory.Applicable(ConversionAction.ToAndroid, all));
        Assert.Equal([motion], JobFactory.Applicable(ConversionAction.ToApple, all));
        Assert.Equal([motion], JobFactory.Applicable(ConversionAction.Extract, all));
        Assert.Equal([pair, motion], JobFactory.Applicable(ConversionAction.Strip, all));

        // 动态照片播放时会补上临时视频路径，它仍然不是苹果实况对
        motion.VideoPath = "/tmp/cache/MVIMG_2.mp4";
        Assert.Equal([pair], JobFactory.Applicable(ConversionAction.ToAndroid, all));
    }

    [Fact]
    public void SourceBytes_CountsPairVideoButNotPlaybackCacheOfMotionPhotos()
    {
        using var album = new TestSandbox();
        var photo = album.CreateInputFile("IMG_1.heic", new byte[300]);
        var video = album.CreateInputFile("IMG_1.mov", new byte[700]);
        var motionPath = album.CreateInputFile("MVIMG_2.jpg", new byte[1000]);
        var cache = album.CreateInputFile("cache.mp4", new byte[900]);

        var pair = new Desktop.Models.PhotoCardItemViewModel { Key = "a", PhotoPath = photo, VideoPath = video };
        var motion = new Desktop.Models.PhotoCardItemViewModel { Key = "b", PhotoPath = motionPath, VideoPath = cache, IsMotionPhoto = true };

        Assert.Equal(1000, JobFactory.SourceBytes([pair]));
        Assert.Equal(1000, JobFactory.SourceBytes([motion]));
        Assert.Equal(0, JobFactory.SourceBytes([Cards.ApplePair("missing")]));
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

        var estimate = await new StripEstimator(engines).EstimateAsync([motion, still], ToolPaths.Auto, convertToHeic: true, TestContext.Current.CancellationToken);

        Assert.Equal(1, estimate.Count);
        Assert.Equal(new FileInfo(motion).Length, estimate.OriginalBytes);
        Assert.True(estimate.SavedBytes >= 40_000, $"至少释放内嵌视频的体积，实际 {estimate.SavedBytes}");
        Assert.InRange(estimate.SavedPercent, 1, 100);
        Assert.Equal(0, engines.VideoConvertersCreated);
    }
}

/// <summary>集合表达式可直接构造的卡片列表别名。</summary>
internal sealed class PhotoCards : List<Desktop.Models.PhotoCardItemViewModel>;
