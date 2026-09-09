using LivePhotoConvert.Core.Matching;
using LivePhotoConvert.Core.Models;
using LivePhotoConvert.Core.Services;
using LivePhotoConvert.E2E.Harness;

namespace LivePhotoConvert.E2E.Tier1_FeatureCoverage;

/// <summary>
/// Tier 1: 白名单与人工裁决放行测试 (Whitelist & Bypass Tests)
/// 验证时差超过 3s 或时长超过 30s 的可疑照片在常规模式下被阻断拦截，
/// 而在白名单放行/校验跳过模式下能够成功合成。
/// </summary>
public class WhitelistBypassTests
{
    [Fact]
    public async Task Merge_WhenTimeDifferenceExceedsThreshold_StandardValidationRejects()
    {
        using var context = new E2ETestContext();
        var photoBytes = SyntheticMediaFactory.CreateJpeg();
        var videoBytes = SyntheticMediaFactory.CreateMov();

        var photoPath = context.CreateInputFile("SUSPICIOUS_01.jpg", photoBytes);
        var videoPath = context.CreateInputFile("SUSPICIOUS_01.mov", videoBytes);

        var testExif = new TestExifTool();
        // 模拟拍摄时间相差 6 秒（超过 3s 上限）
        testExif.CreateDates[photoPath] = new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);
        testExif.CreateDates[videoPath] = new DateTime(2026, 9, 5, 12, 0, 6, DateTimeKind.Utc);

        var testImg = new TestImageConverter();
        var testVideo = new TestVideoConverter();
        var merger = new MotionPhotoMerger(testExif, testImg, testVideo);

        var pairing = MediaPairMatcher.Match([photoPath, videoPath]);
        var options = new MergeOptions
        {
            InputDirectory = context.InputDirectory,
            OutputDirectory = context.OutputDirectory,
            SkipValidation = false // 启用严谨校验
        };

        var report = await merger.MergeAsync(pairing, options, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Total);
        Assert.Equal(0, report.Succeeded);
        Assert.Single(report.SkippedItems);
        Assert.Empty(report.Failures);
    }

    [Fact]
    public async Task Merge_WhenBypassEnabled_AllowsMergingSuspiciousPair()
    {
        using var context = new E2ETestContext();
        var photoBytes = SyntheticMediaFactory.CreateJpeg();
        var videoBytes = SyntheticMediaFactory.CreateMov();

        var photoPath = context.CreateInputFile("SUSPICIOUS_02.jpg", photoBytes);
        var videoPath = context.CreateInputFile("SUSPICIOUS_02.mov", videoBytes);

        var testExif = new TestExifTool();
        testExif.CreateDates[photoPath] = new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);
        testExif.CreateDates[videoPath] = new DateTime(2026, 9, 5, 12, 0, 8, DateTimeKind.Utc);

        var testImg = new TestImageConverter();
        var testVideo = new TestVideoConverter();
        var merger = new MotionPhotoMerger(testExif, testImg, testVideo);

        var pairing = MediaPairMatcher.Match([photoPath, videoPath]);
        var options = new MergeOptions
        {
            InputDirectory = context.InputDirectory,
            OutputDirectory = context.OutputDirectory,
            SkipValidation = true // 人工放行/跳过校验
        };

        var report = await merger.MergeAsync(pairing, options, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Total);
        Assert.Equal(1, report.Succeeded);
        Assert.Empty(report.SkippedItems);
    }

    [Fact]
    public async Task Merge_WhenDurationExceeds30Seconds_StandardValidationRejects_BypassAccepts()
    {
        using var context = new E2ETestContext();
        var photoBytes = SyntheticMediaFactory.CreateJpeg();
        var videoBytes = SyntheticMediaFactory.CreateMov();

        var photoPath = context.CreateInputFile("LONG_VIDEO.jpg", photoBytes);
        var videoPath = context.CreateInputFile("LONG_VIDEO.mov", videoBytes);

        var testExif = new TestExifTool();
        // 模拟视频时长 45 秒（超过 30s 阀值）
        testExif.Durations[videoPath] = TimeSpan.FromSeconds(45);

        var testImg = new TestImageConverter();
        var testVideo = new TestVideoConverter();
        var merger = new MotionPhotoMerger(testExif, testImg, testVideo);

        var pairing = MediaPairMatcher.Match([photoPath, videoPath]);

        // 1. 标准校验阻断
        var rejectOptions = new MergeOptions
        {
            InputDirectory = context.InputDirectory,
            OutputDirectory = context.OutputDirectory,
            SkipValidation = false
        };
        var rejectReport = await merger.MergeAsync(pairing, rejectOptions, TestContext.Current.CancellationToken);
        Assert.Equal(0, rejectReport.Succeeded);
        Assert.Single(rejectReport.SkippedItems);

        // 2. 白名单放行模式通过
        var acceptOptions = new MergeOptions
        {
            InputDirectory = context.InputDirectory,
            OutputDirectory = context.OutputDirectory,
            SkipValidation = true
        };
        var acceptReport = await merger.MergeAsync(pairing, acceptOptions, TestContext.Current.CancellationToken);
        Assert.Equal(1, acceptReport.Succeeded);
        Assert.Empty(acceptReport.SkippedItems);
    }
}
