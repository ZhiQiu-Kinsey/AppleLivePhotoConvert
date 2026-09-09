using LivePhotoConvert.Core.Matching;
using LivePhotoConvert.Core.Models;
using LivePhotoConvert.Core.Services;
using LivePhotoConvert.E2E.Harness;

namespace LivePhotoConvert.E2E.Tier2_BoundaryAnalysis;

/// <summary>
/// Tier 2: 零字节与空流极端边界测试 (Zero Byte Boundary Tests)
/// 验证系统遇到零字节空文件时能够优雅阻断并记录错误，绝不发生未捕获异常或生成脏损坏文件。
/// </summary>
public class ZeroByteBoundaryTests
{
    [Fact]
    public async Task Merge_WhenPhotoIsZeroBytes_FailsGracefullyWithoutCrashing()
    {
        using var context = new E2ETestContext();
        var zeroPhoto = context.CreateInputFile("EMPTY_PHOTO.jpg", []);
        var validVideo = context.CreateInputFile("EMPTY_PHOTO.mov", SyntheticMediaFactory.CreateMov());

        var testExif = new TestExifTool();
        var testImg = new TestImageConverter();
        var testVideo = new TestVideoConverter();
        var merger = new MotionPhotoMerger(testExif, testImg, testVideo);

        var pairing = MediaPairMatcher.Match([zeroPhoto, validVideo]);
        var options = new MergeOptions
        {
            InputDirectory = context.InputDirectory,
            OutputDirectory = context.OutputDirectory,
            SkipValidation = true
        };

        var report = await merger.MergeAsync(pairing, options, TestContext.Current.CancellationToken);

        Assert.Equal(0, report.Succeeded);
        Assert.Single(report.Failures);
        Assert.Empty(context.GetOutputFileNames());
    }

    [Fact]
    public async Task Merge_WhenVideoIsZeroBytes_FailsGracefullyWithoutCrashing()
    {
        using var context = new E2ETestContext();
        var validPhoto = context.CreateInputFile("EMPTY_VIDEO.jpg", SyntheticMediaFactory.CreateJpeg());
        var zeroVideo = context.CreateInputFile("EMPTY_VIDEO.mov", []);

        var testExif = new TestExifTool();
        var testImg = new TestImageConverter();
        var testVideo = new TestVideoConverter();
        var merger = new MotionPhotoMerger(testExif, testImg, testVideo);

        var pairing = MediaPairMatcher.Match([validPhoto, zeroVideo]);
        var options = new MergeOptions
        {
            InputDirectory = context.InputDirectory,
            OutputDirectory = context.OutputDirectory,
            SkipValidation = true
        };

        var report = await merger.MergeAsync(pairing, options, TestContext.Current.CancellationToken);

        Assert.Equal(0, report.Succeeded);
        Assert.Single(report.Failures);
    }

    [Fact]
    public async Task Split_WhenFileIsZeroBytes_RecordsFailureWithoutException()
    {
        using var context = new E2ETestContext();
        context.CreateInputFile("ZERO_FILE.jpg", []);

        var testExif = new TestExifTool();
        var splitter = new MotionPhotoSplitter(testExif);
        var options = new SplitOptions
        {
            InputDirectory = context.InputDirectory,
            OutputDirectory = context.OutputDirectory
        };

        var report = await splitter.SplitAsync(options, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Total);
        Assert.Equal(0, report.Succeeded);
        Assert.Empty(context.GetOutputFileNames());
    }

    [Fact]
    public async Task Strip_WhenFileIsZeroBytes_RecordsFailureSafely()
    {
        using var context = new E2ETestContext();
        context.CreateInputFile("ZERO_STRIP.jpg", []);

        var testExif = new TestExifTool();
        var testImg = new TestImageConverter();
        var stripper = new MotionPhotoStripper(testExif, testImg);
        var options = new StripOptions
        {
            InputDirectory = context.InputDirectory,
            OutputDirectory = context.OutputDirectory
        };

        var report = await stripper.StripAsync(options, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Total);
        Assert.Equal(0, report.StrippedCount);
        Assert.Empty(context.GetOutputFileNames());
    }
}
