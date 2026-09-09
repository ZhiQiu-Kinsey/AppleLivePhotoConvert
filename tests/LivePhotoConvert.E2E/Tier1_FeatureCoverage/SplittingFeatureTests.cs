using LivePhotoConvert.Core.Models;
using LivePhotoConvert.Core.Services;
using LivePhotoConvert.E2E.Harness;

namespace LivePhotoConvert.E2E.Tier1_FeatureCoverage;

/// <summary>
/// Tier 1: 拆分特性端到端覆盖测试 (Splitting Feature Tests)
/// 验证动态照片拆分为安卓无损独立图影 (JPG + MP4) 以及转换为苹果实况对 (.HEIC + .MOV 带配对 UUID)。
/// </summary>
public class SplittingFeatureTests
{
    [Fact]
    public async Task Split_AndroidFormat_ExtractsPhotoAndMp4Streams()
    {
        using var context = new E2ETestContext();
        var (content, videoLength) = SyntheticMediaFactory.CreateMotionPhoto();
        var motionPhotoPath = context.CreateInputFile("MVIMG_20260905_1001.jpg", content);

        var testExif = new TestExifTool();
        testExif.MicroVideoOffsets[motionPhotoPath] = videoLength;

        var splitter = new MotionPhotoSplitter(testExif);
        var options = new SplitOptions
        {
            InputDirectory = context.InputDirectory,
            OutputDirectory = context.OutputDirectory,
            TargetFormat = SplitTargetFormat.Android,
            Overwrite = true
        };

        var report = await splitter.SplitAsync(options, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Total);
        Assert.Equal(1, report.Succeeded);
        Assert.Equal(0, report.Skipped);
        Assert.Empty(report.Failures);

        var extractedPhoto = Path.Combine(context.OutputDirectory, "MVIMG_20260905_1001.jpg");
        var extractedVideo = Path.Combine(context.OutputDirectory, "MVIMG_20260905_1001.mp4");

        Assert.True(File.Exists(extractedPhoto));
        Assert.True(File.Exists(extractedVideo));

        Assert.Equal(1024, new FileInfo(extractedPhoto).Length);
        Assert.Equal(2048, new FileInfo(extractedVideo).Length);
    }

    [Fact]
    public async Task Split_AppleFormat_TranscodesToHeicAndMov_WithMatchingContentIdentifier()
    {
        using var context = new E2ETestContext();
        var (content, videoLength) = SyntheticMediaFactory.CreateMotionPhoto();
        var motionPhotoPath = context.CreateInputFile("MVIMG_20260905_2002.jpg", content);

        var testExif = new TestExifTool();
        testExif.MicroVideoOffsets[motionPhotoPath] = videoLength;
        var testVideo = new TestVideoConverter();
        var testImage = new TestImageConverter();

        var splitter = new MotionPhotoSplitter(testExif, testVideo, testImage);
        var options = new SplitOptions
        {
            InputDirectory = context.InputDirectory,
            OutputDirectory = context.OutputDirectory,
            TargetFormat = SplitTargetFormat.Apple,
            Overwrite = true
        };

        var report = await splitter.SplitAsync(options, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Total);
        Assert.Equal(1, report.Succeeded);
        Assert.Empty(report.Failures);

        // Apple 实况照片必须是大写扩展名 .HEIC + .MOV
        var extractedPhoto = Path.Combine(context.OutputDirectory, "MVIMG_20260905_2002.HEIC");
        var extractedVideo = Path.Combine(context.OutputDirectory, "MVIMG_20260905_2002.MOV");

        Assert.True(File.Exists(extractedPhoto));
        Assert.True(File.Exists(extractedVideo));

        // 验证写入了相同的配对 UUID (ContentIdentifier)
        Assert.Single(testExif.WriteApplePhotoCalls);
        Assert.Single(testExif.WriteAppleVideoCalls);

        var photoCall = testExif.WriteApplePhotoCalls.First();
        var videoCall = testExif.WriteAppleVideoCalls.First();

        Assert.Equal(photoCall.Id, videoCall.Id);
        Assert.True(Guid.TryParse(photoCall.Id, out _), "Apple ContentIdentifier must be a valid GUID");
        Assert.Equal(photoCall.Id, photoCall.Id.ToUpperInvariant());
    }

    [Fact]
    public async Task Split_WhenNonMotionPhoto_SkipsGracefully()
    {
        using var context = new E2ETestContext();
        var plainJpeg = SyntheticMediaFactory.CreateJpeg();
        var plainPhotoPath = context.CreateInputFile("NORMAL_PHOTO.jpg", plainJpeg);

        var testExif = new TestExifTool();
        // 不配置 MicroVideoOffset 且不包含视频标记
        testExif.MicroVideoOffsets.TryRemove(plainPhotoPath, out _);

        var splitter = new MotionPhotoSplitter(testExif);
        var options = new SplitOptions
        {
            InputDirectory = context.InputDirectory,
            OutputDirectory = context.OutputDirectory,
            TargetFormat = SplitTargetFormat.Android
        };

        var report = await splitter.SplitAsync(options, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Total);
        Assert.Equal(0, report.Succeeded);
        Assert.Equal(1, report.Skipped);
        Assert.Empty(report.Failures);
    }
}
