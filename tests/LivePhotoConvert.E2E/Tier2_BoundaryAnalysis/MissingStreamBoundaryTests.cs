using LivePhotoConvert.Core.Matching;
using LivePhotoConvert.Core.Models;
using LivePhotoConvert.Core.Services;
using LivePhotoConvert.E2E.Harness;

namespace LivePhotoConvert.E2E.Tier2_BoundaryAnalysis;

/// <summary>
/// Tier 2: 伴侣流缺失边界测试 (Missing Stream Boundary Tests)
/// 验证只有图片无视频、只有视频无图片、以及普通无微视频相片作为输入时的系统行为。
/// </summary>
public class MissingStreamBoundaryTests
{
    [Fact]
    public void Matcher_WhenOnlyPhotoOrOnlyVideo_CorrectlyIdentifiesUnpairedStreams()
    {
        using var context = new E2ETestContext();
        var photoPath = context.CreateInputFile("SOLO_PHOTO.jpg", SyntheticMediaFactory.CreateJpeg());
        var videoPath = context.CreateInputFile("SOLO_VIDEO.mov", SyntheticMediaFactory.CreateMov());

        var pairing = MediaPairMatcher.Match([photoPath, videoPath]);

        Assert.Empty(pairing.Pairs);
        Assert.Single(pairing.PhotosWithoutVideo);
        Assert.Single(pairing.VideosWithoutPhoto);
        Assert.Equal(photoPath, pairing.PhotosWithoutVideo[0]);
        Assert.Equal(videoPath, pairing.VideosWithoutPhoto[0]);
    }

    [Fact]
    public async Task Split_WhenFileHasNoEmbeddedVideo_MarksAsSkippedWithoutError()
    {
        using var context = new E2ETestContext();
        var normalJpeg = SyntheticMediaFactory.CreateJpeg(2048);
        var path = context.CreateInputFile("NORMAL_PIC.jpg", normalJpeg);

        var testExif = new TestExifTool();
        // 确保未配置 MicroVideoOffset
        testExif.MicroVideoOffsets.TryRemove(path, out _);

        var splitter = new MotionPhotoSplitter(testExif);
        var options = new SplitOptions
        {
            InputDirectory = context.InputDirectory,
            OutputDirectory = context.OutputDirectory
        };

        var report = await splitter.SplitAsync(options, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Total);
        Assert.Equal(0, report.Succeeded);
        Assert.Equal(1, report.Skipped);
        Assert.Empty(report.Failures);
    }

    [Fact]
    public async Task Strip_WhenFileHasNoEmbeddedVideoAndAlreadyHeic_MarksAsSkipped()
    {
        using var context = new E2ETestContext();
        var heicBytes = SyntheticMediaFactory.CreateHeic();
        var path = context.CreateInputFile("NORMAL_HEIC.heic", heicBytes);

        var testExif = new TestExifTool();
        testExif.MicroVideoOffsets.TryRemove(path, out _);
        var testImg = new TestImageConverter();

        var stripper = new MotionPhotoStripper(testExif, testImg);
        var options = new StripOptions
        {
            InputDirectory = context.InputDirectory,
            OutputDirectory = context.OutputDirectory,
            ConvertToHeic = true
        };

        var report = await stripper.StripAsync(options, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Total);
        Assert.Equal(0, report.StrippedCount);
        Assert.Equal(1, report.Skipped);
        Assert.Empty(report.Failures);
    }
}
