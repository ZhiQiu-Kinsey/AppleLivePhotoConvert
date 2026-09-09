using LivePhotoConvert.Core.Matching;
using LivePhotoConvert.Core.Models;
using LivePhotoConvert.Core.Services;
using LivePhotoConvert.E2E.Harness;

namespace LivePhotoConvert.E2E.Tier1_FeatureCoverage;

/// <summary>
/// Tier 1: 源文件处理策略覆盖测试 (Source Action Feature Tests)
/// 验证 Keep（保留）、Move（移至子文件夹）、Delete（物理删除）及失败不误删的安全隔离。
/// </summary>
public class SourceActionFeatureTests
{
    [Fact]
    public async Task Merge_WithSourceActionKeep_LeavesSourceFilesUntouched()
    {
        using var context = new E2ETestContext();
        var photoBytes = SyntheticMediaFactory.CreateJpeg();
        var videoBytes = SyntheticMediaFactory.CreateMov();

        var photoPath = context.CreateInputFile("KEEP_01.jpg", photoBytes);
        var videoPath = context.CreateInputFile("KEEP_01.mov", videoBytes);

        var testExif = new TestExifTool();
        var testImg = new TestImageConverter();
        var testVideo = new TestVideoConverter();
        var merger = new MotionPhotoMerger(testExif, testImg, testVideo);

        var pairing = MediaPairMatcher.Match([photoPath, videoPath]);
        var options = new MergeOptions
        {
            InputDirectory = context.InputDirectory,
            OutputDirectory = context.OutputDirectory,
            SourceFileAction = SourceFileAction.Keep,
            SkipValidation = true
        };

        var report = await merger.MergeAsync(pairing, options, TestContext.Current.CancellationToken);
        Assert.Equal(1, report.Succeeded);
        Assert.Equal(0, report.CleanedFileCount);

        // 源文件依然存在
        Assert.True(File.Exists(photoPath));
        Assert.True(File.Exists(videoPath));
    }

    [Fact]
    public async Task Merge_WithSourceActionMove_MovesSourceFilesToSubfolder()
    {
        using var context = new E2ETestContext();
        var photoBytes = SyntheticMediaFactory.CreateJpeg();
        var videoBytes = SyntheticMediaFactory.CreateMov();

        var photoPath = context.CreateInputFile("MOVE_01.jpg", photoBytes);
        var videoPath = context.CreateInputFile("MOVE_01.mov", videoBytes);

        var testExif = new TestExifTool();
        var testImg = new TestImageConverter();
        var testVideo = new TestVideoConverter();
        var merger = new MotionPhotoMerger(testExif, testImg, testVideo);

        var pairing = MediaPairMatcher.Match([photoPath, videoPath]);
        var options = new MergeOptions
        {
            InputDirectory = context.InputDirectory,
            OutputDirectory = context.OutputDirectory,
            SourceFileAction = SourceFileAction.Move,
            SkipValidation = true
        };

        var report = await merger.MergeAsync(pairing, options, TestContext.Current.CancellationToken);
        Assert.Equal(1, report.Succeeded);
        Assert.Equal(2, report.CleanedFileCount);

        // 原路径不再存在
        Assert.False(File.Exists(photoPath));
        Assert.False(File.Exists(videoPath));

        // 移入输入目录下的已合成子目录
        var movedPhoto = Path.Combine(context.InputDirectory, "已合成", "MOVE_01.jpg");
        var movedVideo = Path.Combine(context.InputDirectory, "已合成", "MOVE_01.mov");
        Assert.True(File.Exists(movedPhoto));
        Assert.True(File.Exists(movedVideo));
    }

    [Fact]
    public async Task Merge_WithSourceActionDelete_DeletesSourceFilesAfterSuccess()
    {
        using var context = new E2ETestContext();
        var photoBytes = SyntheticMediaFactory.CreateJpeg();
        var videoBytes = SyntheticMediaFactory.CreateMov();

        var photoPath = context.CreateInputFile("DEL_01.jpg", photoBytes);
        var videoPath = context.CreateInputFile("DEL_01.mov", videoBytes);

        var testExif = new TestExifTool();
        var testImg = new TestImageConverter();
        var testVideo = new TestVideoConverter();
        var merger = new MotionPhotoMerger(testExif, testImg, testVideo);

        var pairing = MediaPairMatcher.Match([photoPath, videoPath]);
        var options = new MergeOptions
        {
            InputDirectory = context.InputDirectory,
            OutputDirectory = context.OutputDirectory,
            SourceFileAction = SourceFileAction.Delete,
            SkipValidation = true
        };

        var report = await merger.MergeAsync(pairing, options, TestContext.Current.CancellationToken);
        Assert.Equal(1, report.Succeeded);
        Assert.Equal(2, report.CleanedFileCount);

        // 源文件被物理删除
        Assert.False(File.Exists(photoPath));
        Assert.False(File.Exists(videoPath));
    }

    [Fact]
    public async Task Merge_WhenProcessingFails_NeverDeletesOrMovesSourceFiles()
    {
        using var context = new E2ETestContext();
        var photoBytes = SyntheticMediaFactory.CreateJpeg();
        var videoBytes = SyntheticMediaFactory.CreateMov();

        var photoPath = context.CreateInputFile("FAIL_SAFE.jpg", photoBytes);
        var videoPath = context.CreateInputFile("FAIL_SAFE.mov", videoBytes);

        var testExif = new TestExifTool();
        // 单边 ContentIdentifier 导致校验失败
        testExif.ContentIdentifiers[(photoPath, Core.Abstractions.ContentIdentifierKind.Photo)] = "ID-ONLY-PHOTO";

        var testImg = new TestImageConverter();
        var testVideo = new TestVideoConverter();
        var merger = new MotionPhotoMerger(testExif, testImg, testVideo);

        var pairing = MediaPairMatcher.Match([photoPath, videoPath]);
        var options = new MergeOptions
        {
            InputDirectory = context.InputDirectory,
            OutputDirectory = context.OutputDirectory,
            SourceFileAction = SourceFileAction.Delete, // 即使选了删除
            SkipValidation = false
        };

        var report = await merger.MergeAsync(pairing, options, TestContext.Current.CancellationToken);
        Assert.Equal(0, report.Succeeded);
        Assert.Single(report.SkippedItems);
        Assert.Equal(0, report.CleanedFileCount);

        // 校验失败的文件绝对不能被删除！
        Assert.True(File.Exists(photoPath));
        Assert.True(File.Exists(videoPath));
    }
}
