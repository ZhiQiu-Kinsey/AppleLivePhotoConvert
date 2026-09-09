using LivePhotoConvert.Core.Models;
using LivePhotoConvert.Core.Services;
using LivePhotoConvert.E2E.Harness;

namespace LivePhotoConvert.E2E.Tier2_BoundaryAnalysis;

/// <summary>
/// Tier 2: 元数据畸变与损坏边界测试 (Corrupted Metadata Boundary Tests)
/// 验证当 XMP 偏移非法（为0、超过文件大小、负数）或视频魔数损坏时，系统能够安全阻断并记录失败。
/// </summary>
public class CorruptedMetadataBoundaryTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-500)]
    [InlineData(9999999)] // 远大于实际物理大小
    public async Task Split_WhenMicroVideoOffsetIsInvalid_RecordsFailureSafely(long invalidOffset)
    {
        using var context = new E2ETestContext();
        var (content, _) = SyntheticMediaFactory.CreateMotionPhoto();
        var path = context.CreateInputFile("BAD_OFFSET.jpg", content);

        var testExif = new TestExifTool();
        testExif.MicroVideoOffsets[path] = invalidOffset;

        var splitter = new MotionPhotoSplitter(testExif);
        var options = new SplitOptions
        {
            InputDirectory = context.InputDirectory,
            OutputDirectory = context.OutputDirectory
        };

        var report = await splitter.SplitAsync(options, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Total);
        Assert.Equal(0, report.Succeeded);
        Assert.Single(report.Failures);
    }

    [Fact]
    public async Task Split_WhenVideoPayloadCorrupted_RecordsFailure()
    {
        using var context = new E2ETestContext();
        // 头部为 JPEG，尾部却不是合法的 MP4/MOV，而是截断数据
        var photo = SyntheticMediaFactory.CreateJpeg();
        var corruptedVideo = SyntheticMediaFactory.CreateCorrupted(CorruptedFileType.TruncatedMp4);

        var combined = new byte[photo.Length + corruptedVideo.Length];
        Buffer.BlockCopy(photo, 0, combined, 0, photo.Length);
        Buffer.BlockCopy(corruptedVideo, 0, combined, photo.Length, corruptedVideo.Length);

        var path = context.CreateInputFile("CORRUPT_VIDEO.jpg", combined);

        var testExif = new TestExifTool();
        testExif.MicroVideoOffsets[path] = corruptedVideo.Length;

        var splitter = new MotionPhotoSplitter(testExif);
        var options = new SplitOptions
        {
            InputDirectory = context.InputDirectory,
            OutputDirectory = context.OutputDirectory
        };

        var report = await splitter.SplitAsync(options, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Total);
        Assert.Equal(0, report.Succeeded);
        Assert.Single(report.Failures);
    }
}
