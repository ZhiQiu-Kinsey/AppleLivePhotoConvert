using LivePhotoConvert.Core.Models;
using LivePhotoConvert.Core.Services;
using LivePhotoConvert.E2E.Harness;

namespace LivePhotoConvert.E2E.Tier1_FeatureCoverage;

/// <summary>
/// Tier 1: 空间瘦身特性端到端覆盖测试 (Stripping Feature Tests)
/// 验证剥离动态照片中内嵌视频、就地修改模式、导出模式与非实况跳过机制。
/// </summary>
public class StrippingFeatureTests
{
    [Fact]
    public async Task Strip_ExportToOutputDirectory_RemovesEmbeddedVideo()
    {
        using var context = new E2ETestContext();
        var (content, videoLength) = SyntheticMediaFactory.CreateMotionPhoto();
        var motionPhotoPath = context.CreateInputFile("MVIMG_STRIP_1.jpg", content);

        var testExif = new TestExifTool();
        testExif.MicroVideoOffsets[motionPhotoPath] = videoLength;
        var testImg = new TestImageConverter();

        var stripper = new MotionPhotoStripper(testExif, testImg);
        var options = new StripOptions
        {
            InputDirectory = context.InputDirectory,
            OutputDirectory = context.OutputDirectory,
            ConvertToHeic = false, // 纯剥离不转 HEIC
            Overwrite = true
        };

        var report = await stripper.StripAsync(options, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Total);
        Assert.Equal(1, report.StrippedCount);
        Assert.Equal(0, report.Skipped);
        Assert.Equal(2048, report.SavedBytes);

        var strippedFile = Path.Combine(context.OutputDirectory, "MVIMG_STRIP_1.jpg");
        Assert.True(File.Exists(strippedFile));
        Assert.Equal(1024, new FileInfo(strippedFile).Length);
    }

    [Fact]
    public async Task Strip_InPlaceMode_ModifiesSourceFileAtomically()
    {
        using var context = new E2ETestContext();
        var (content, videoLength) = SyntheticMediaFactory.CreateMotionPhoto(1024, 4096);
        var motionPhotoPath = context.CreateInputFile("MVIMG_INPLACE.jpg", content);

        var testExif = new TestExifTool();
        testExif.MicroVideoOffsets[motionPhotoPath] = videoLength;
        var testImg = new TestImageConverter();

        var stripper = new MotionPhotoStripper(testExif, testImg);
        var options = new StripOptions
        {
            InputDirectory = context.InputDirectory,
            OutputDirectory = null, // 就地覆盖模式
            ConvertToHeic = false,
            Overwrite = true
        };

        var report = await stripper.StripAsync(options, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Total);
        Assert.Equal(1, report.StrippedCount);
        Assert.Equal(4096, report.SavedBytes);

        // 原文件体积应该变为 1024
        var fileInfo = new FileInfo(motionPhotoPath);
        Assert.Equal(1024, fileInfo.Length);
    }

    [Fact]
    public async Task Strip_WhenPlainHeicWithoutVideo_SkipsGracefully()
    {
        using var context = new E2ETestContext();
        var plainHeic = SyntheticMediaFactory.CreateHeic(512);
        var plainPath = context.CreateInputFile("PLAIN.heic", plainHeic);

        var testExif = new TestExifTool();
        testExif.MicroVideoOffsets.TryRemove(plainPath, out _);
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
        Assert.Equal(0, report.SavedBytes);
    }
}
