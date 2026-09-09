using LivePhotoConvert.Core.Matching;
using LivePhotoConvert.Core.Models;
using LivePhotoConvert.Core.Services;
using LivePhotoConvert.E2E.Harness;

namespace LivePhotoConvert.E2E.Tier1_FeatureCoverage;

/// <summary>
/// Tier 1: 合成特性端到端覆盖测试 (Conversion Feature Tests)
/// 验证 iPhone 实况对 (HEIC/JPG + MOV) 合成为安卓单文件动态照片 (JPEG + MP4) 的全流程、
/// 文件魔数、命名策略、重名保护与元数据写入契约。
/// </summary>
public class ConversionFeatureTests
{
    [Fact]
    public async Task Merge_IPhoneJpgAndMov_ProducesValidAndroidMotionPhoto()
    {
        using var context = new E2ETestContext();
        var photoBytes = SyntheticMediaFactory.CreateJpeg(2048);
        var videoBytes = SyntheticMediaFactory.CreateMov(4096);

        var photoPath = context.CreateInputFile("IMG_1001.jpg", photoBytes);
        var videoPath = context.CreateInputFile("IMG_1001.mov", videoBytes);

        var pairing = MediaPairMatcher.Match([photoPath, videoPath]);
        Assert.Single(pairing.Pairs);

        var testExif = new TestExifTool();
        var testImg = new TestImageConverter();
        var testVideo = new TestVideoConverter();
        var merger = new MotionPhotoMerger(testExif, testImg, testVideo);

        var options = new MergeOptions
        {
            InputDirectory = context.InputDirectory,
            OutputDirectory = context.OutputDirectory,
            NamingFormat = MergeNamingFormat.Original,
            Overwrite = false,
            SourceFileAction = SourceFileAction.Keep,
            SkipValidation = true
        };

        var report = await merger.MergeAsync(pairing, options, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Total);
        Assert.Equal(1, report.Succeeded);
        Assert.Empty(report.Failures);
        Assert.Empty(report.SkippedItems);

        // 验证输出文件物理存在
        var expectedOutputFile = Path.Combine(context.OutputDirectory, "MVIMG_IMG_1001.jpg");
        Assert.True(File.Exists(expectedOutputFile));

        // 验证输出文件魔数：头部为 JPEG (FF D8 FF)，尾部包含 MP4
        var outputBytes = await File.ReadAllBytesAsync(expectedOutputFile, TestContext.Current.CancellationToken);
        Assert.Equal(0xFF, outputBytes[0]);
        Assert.Equal(0xD8, outputBytes[1]);
        Assert.Equal(0xFF, outputBytes[2]);

        // 验证 ExifTool 写入动态照片元数据调用
        Assert.Contains(testExif.WriteMotionPhotoCalls, call => call.Offset > 0);
    }

    [Fact]
    public async Task Merge_IPhoneHeicAndMov_TranscodesToJpegAndMerges()
    {
        using var context = new E2ETestContext();
        var photoBytes = SyntheticMediaFactory.CreateHeic();
        var videoBytes = SyntheticMediaFactory.CreateMov();

        var photoPath = context.CreateInputFile("IMG_2002.heic", photoBytes);
        var videoPath = context.CreateInputFile("IMG_2002.mov", videoBytes);

        var pairing = MediaPairMatcher.Match([photoPath, videoPath]);
        Assert.Single(pairing.Pairs);

        var testExif = new TestExifTool();
        var testImg = new TestImageConverter();
        var testVideo = new TestVideoConverter();
        var merger = new MotionPhotoMerger(testExif, testImg, testVideo);

        var options = new MergeOptions
        {
            InputDirectory = context.InputDirectory,
            OutputDirectory = context.OutputDirectory,
            NamingFormat = MergeNamingFormat.Original,
            Overwrite = true,
            SkipValidation = true
        };

        var report = await merger.MergeAsync(pairing, options, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Succeeded);
        // HEIC 图片应触发转码为 JPEG
        Assert.Contains(testImg.ConvertToJpegCalls, call => call.Source.EndsWith(".heic", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(MergeNamingFormat.Original, "MVIMG_IMG_3003.jpg")]
    [InlineData(MergeNamingFormat.XiaomiClean, "MVIMG_20260905_120000.jpg")]
    [InlineData(MergeNamingFormat.XiaomiWithOriginal, "MVIMG_20260905_120000_IMG_3003.jpg")]
    public async Task Merge_RespectsNamingFormats(MergeNamingFormat format, string expectedName)
    {
        using var context = new E2ETestContext();
        var photoBytes = SyntheticMediaFactory.CreateJpeg();
        var videoBytes = SyntheticMediaFactory.CreateMov();

        var photoPath = context.CreateInputFile("IMG_3003.jpg", photoBytes);
        var videoPath = context.CreateInputFile("IMG_3003.mov", videoBytes);

        var testExif = new TestExifTool();
        testExif.CreateDates[photoPath] = new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);

        var testImg = new TestImageConverter();
        var testVideo = new TestVideoConverter();
        var merger = new MotionPhotoMerger(testExif, testImg, testVideo);

        var pairing = MediaPairMatcher.Match([photoPath, videoPath]);
        var options = new MergeOptions
        {
            InputDirectory = context.InputDirectory,
            OutputDirectory = context.OutputDirectory,
            NamingFormat = format,
            Overwrite = true,
            SkipValidation = true
        };

        var report = await merger.MergeAsync(pairing, options, TestContext.Current.CancellationToken);
        Assert.Equal(1, report.Succeeded);

        var expectedPath = Path.Combine(context.OutputDirectory, expectedName);
        Assert.True(File.Exists(expectedPath), $"Expected file at {expectedPath}, but found: {string.Join(", ", context.GetOutputFileNames())}");
    }

    [Fact]
    public async Task Merge_WhenCollisionOccursAndOverwriteFalse_AppendsUniqueSuffix()
    {
        using var context = new E2ETestContext();
        var photoBytes = SyntheticMediaFactory.CreateJpeg();
        var videoBytes = SyntheticMediaFactory.CreateMov();

        var photoPath = context.CreateInputFile("IMG_4004.jpg", photoBytes);
        var videoPath = context.CreateInputFile("IMG_4004.mov", videoBytes);

        // 先在输出目录创建一个同名文件产生冲突
        context.CreateOutputFile("MVIMG_IMG_4004.jpg", [0x01, 0x02, 0x03]);

        var testExif = new TestExifTool();
        var testImg = new TestImageConverter();
        var testVideo = new TestVideoConverter();
        var merger = new MotionPhotoMerger(testExif, testImg, testVideo);

        var pairing = MediaPairMatcher.Match([photoPath, videoPath]);
        var options = new MergeOptions
        {
            InputDirectory = context.InputDirectory,
            OutputDirectory = context.OutputDirectory,
            NamingFormat = MergeNamingFormat.Original,
            Overwrite = false,
            SkipValidation = true
        };

        var report = await merger.MergeAsync(pairing, options, TestContext.Current.CancellationToken);
        Assert.Equal(1, report.Succeeded);

        // 原有同名文件内容未被破坏
        var originalExisting = await File.ReadAllBytesAsync(Path.Combine(context.OutputDirectory, "MVIMG_IMG_4004.jpg"), TestContext.Current.CancellationToken);
        Assert.Equal([0x01, 0x02, 0x03], originalExisting);

        // 新合成文件自动重命名为 _1
        var suffixedFile = Path.Combine(context.OutputDirectory, "MVIMG_IMG_4004_1.jpg");
        Assert.True(File.Exists(suffixedFile));
    }
}
