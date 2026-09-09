using LivePhotoConvert.Core.Abstractions;
using LivePhotoConvert.Core.Matching;
using LivePhotoConvert.Core.Models;
using LivePhotoConvert.Core.Services;

namespace LivePhotoConvert.Core.Tests;

/// <summary>
/// 对 M2 Core 的 ForceAcceptedPairs 和 MotionPhotoMerger 进行全方位对抗性与压力测试。
/// 验证路径大小写、斜杠归一化、极端时差/时长绕过、候选优先级仲裁以及未匹配对的人工注入。
/// </summary>
public class ForceAcceptedPairsAdversarialTests
{
    #region 1. MediaPairPathEqualityComparer 纯算法比对测试

    [Fact]
    public void Comparer_PathCasingDifferences_ShouldBeEqualAndSameHashCode()
    {
        var pairUpper = new MediaPair(@"C:\PHOTOS\SUB\IMG_0001.JPG", @"C:\PHOTOS\SUB\IMG_0001.MOV");
        var pairLower = new MediaPair(@"c:\photos\sub\img_0001.jpg", @"c:\photos\sub\img_0001.mov");
        var pairMixed = new MediaPair(@"C:\PhOtOs\sUb\ImG_0001.JpG", @"C:\pHoToS\SuB\iMg_0001.MoV");

        var comparer = MediaPairPathEqualityComparer.Instance;

        Assert.True(comparer.Equals(pairUpper, pairLower));
        Assert.True(comparer.Equals(pairLower, pairMixed));
        Assert.True(comparer.Equals(pairUpper, pairMixed));

        Assert.Equal(comparer.GetHashCode(pairUpper), comparer.GetHashCode(pairLower));
        Assert.Equal(comparer.GetHashCode(pairLower), comparer.GetHashCode(pairMixed));
    }

    [Fact]
    public void Comparer_SlashNormalization_ShouldBeEqualAndSameHashCode()
    {
        var pairBackslash = new MediaPair(@"C:\photos\sub\IMG_0001.jpg", @"C:\photos\sub\IMG_0001.mov");
        var pairSlash = new MediaPair("C:/photos/sub/IMG_0001.jpg", "C:/photos/sub/IMG_0001.mov");
        var pairMixedSlash = new MediaPair(@"C:/photos\sub/IMG_0001.jpg", @"C:\photos/sub\IMG_0001.mov");

        var comparer = MediaPairPathEqualityComparer.Instance;

        Assert.True(comparer.Equals(pairBackslash, pairSlash));
        Assert.True(comparer.Equals(pairSlash, pairMixedSlash));
        Assert.True(comparer.Equals(pairBackslash, pairMixedSlash));

        Assert.Equal(comparer.GetHashCode(pairBackslash), comparer.GetHashCode(pairSlash));
        Assert.Equal(comparer.GetHashCode(pairSlash), comparer.GetHashCode(pairMixedSlash));
    }

    [Fact]
    public void Comparer_IgnoresIsContentIdentifierMatchedFlag()
    {
        var pair1 = new MediaPair(@"C:\photos\IMG_0001.jpg", @"C:\photos\IMG_0001.mov", IsContentIdentifierMatched: true);
        var pair2 = new MediaPair(@"C:\photos\IMG_0001.jpg", @"C:\photos\IMG_0001.mov", IsContentIdentifierMatched: false);

        var comparer = MediaPairPathEqualityComparer.Instance;

        Assert.True(comparer.Equals(pair1, pair2));
        Assert.Equal(comparer.GetHashCode(pair1), comparer.GetHashCode(pair2));
    }

    [Fact]
    public void Comparer_NullHandling_ReturnsExpected()
    {
        var pair = new MediaPair(@"C:\photos\IMG_0001.jpg", @"C:\photos\IMG_0001.mov");
        var comparer = MediaPairPathEqualityComparer.Instance;

        Assert.True(comparer.Equals(null, null));
        Assert.False(comparer.Equals(pair, null));
        Assert.False(comparer.Equals(null, pair));
        Assert.True(comparer.Equals(pair, pair));
    }

    #endregion

    #region 2. 路径大小写与斜杠在 MergeAsync 完整流水线中的放行测试

    [Fact]
    public async Task MergeAsync_ForceAcceptedPair_WithOppositePathCasing_BypassesValidation()
    {
        using var tempDir = new TempDirectory();
        var photoBytes = new byte[2048];
        var videoBytes = new byte[5000];

        // 磁盘上创建大写文件名
        var jpgPath = tempDir.CreateFile("TEST_CASE_01.JPG", photoBytes);
        var movPath = tempDir.CreateFile("TEST_CASE_01.MOV", videoBytes);
        var outputDir = tempDir.Combine("output");

        var pairing = MediaPairMatcher.Match([jpgPath, movPath]);
        Assert.Single(pairing.Pairs);

        // 模拟时差 100 秒（常规校验必败）
        var fakeExif = new AdversarialExifTool();
        fakeExif.FileDates[Path.GetFileName(jpgPath)] = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        fakeExif.FileDates[Path.GetFileName(movPath)] = new DateTime(2025, 1, 1, 0, 1, 40, DateTimeKind.Utc);

        var fakeImg = new AdversarialImageConverter();
        var fakeVideo = new AdversarialVideoConverter();
        var merger = new MotionPhotoMerger(fakeExif, fakeImg, fakeVideo);

        // 白名单中使用全小写路径
        var forcePair = new MediaPair(jpgPath.ToLowerInvariant(), movPath.ToLowerInvariant());

        var options = new MergeOptions
        {
            InputDirectory = tempDir.Root,
            OutputDirectory = outputDir,
            SourceFileAction = SourceFileAction.Keep,
            SkipValidation = false,
            // 采用普通的默认 HashSet（测试 IsForceAccepted 的回退比对逻辑）
            ForceAcceptedPairs = new HashSet<MediaPair> { forcePair }
        };

        var report = await merger.MergeAsync(pairing, options, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Total);
        Assert.Equal(1, report.Succeeded);
        Assert.Empty(report.SkippedItems);
        Assert.Empty(report.Failures);
    }

    [Fact]
    public async Task MergeAsync_ForceAcceptedPair_WithForwardSlashesAndMixedCase_BypassesValidation()
    {
        using var tempDir = new TempDirectory();
        var photoBytes = new byte[2048];
        var videoBytes = new byte[5000];

        var jpgPath = tempDir.CreateFile("MIXED_SLASH_01.jpg", photoBytes);
        var movPath = tempDir.CreateFile("MIXED_SLASH_01.mov", videoBytes);
        var outputDir = tempDir.Combine("output");

        var pairing = MediaPairMatcher.Match([jpgPath, movPath]);

        // 模拟常规校验必败（单边 ContentIdentifier）
        var fakeExif = new AdversarialExifTool
        {
            ContentIdentifiers = { ["photo"] = "CI-SOLO-PHOTO" }
        };

        var fakeImg = new AdversarialImageConverter();
        var fakeVideo = new AdversarialVideoConverter();
        var merger = new MotionPhotoMerger(fakeExif, fakeImg, fakeVideo);

        // 正斜杠 + 混合大小写
        var forcePhoto = jpgPath.Replace('\\', '/').ToUpperInvariant();
        var forceVideo = movPath.Replace('\\', '/').ToLowerInvariant();
        var forcePair = new MediaPair(forcePhoto, forceVideo);

        var options = new MergeOptions
        {
            InputDirectory = tempDir.Root,
            OutputDirectory = outputDir,
            SourceFileAction = SourceFileAction.Keep,
            SkipValidation = false,
            ForceAcceptedPairs = new HashSet<MediaPair>(MediaPairPathEqualityComparer.Instance) { forcePair }
        };

        var report = await merger.MergeAsync(pairing, options, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Total);
        Assert.Equal(1, report.Succeeded);
        Assert.Empty(report.SkippedItems);
    }

    #endregion

    #region 3. 极端时差、异常时长与元数据冲突的放行验证

    [Fact]
    public async Task MergeAsync_ForceAcceptedPair_ExtremeTimeDifference_10Days_BypassesValidation()
    {
        using var tempDir = new TempDirectory();
        var photoBytes = new byte[2048];
        var videoBytes = new byte[5000];

        var jpgPath = tempDir.CreateFile("DIFF_10DAYS.jpg", photoBytes);
        var movPath = tempDir.CreateFile("DIFF_10DAYS.mov", videoBytes);
        var outputDir = tempDir.Combine("output");

        var pair = new MediaPair(jpgPath, movPath);
        var pairing = new PairingResult
        {
            Pairs = [pair],
            UnmatchedPhotoCount = 0,
            UnmatchedVideoCount = 0
        };

        // 构造相差 10 天的时间戳
        var fakeExif = new AdversarialExifTool();
        fakeExif.FileDates[Path.GetFileName(jpgPath)] = new DateTime(2020, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        fakeExif.FileDates[Path.GetFileName(movPath)] = new DateTime(2020, 1, 11, 12, 0, 0, DateTimeKind.Utc);

        var fakeImg = new AdversarialImageConverter();
        var fakeVideo = new AdversarialVideoConverter();
        var merger = new MotionPhotoMerger(fakeExif, fakeImg, fakeVideo);

        var options = new MergeOptions
        {
            InputDirectory = tempDir.Root,
            OutputDirectory = outputDir,
            SkipValidation = false,
            ForceAcceptedPairs = new HashSet<MediaPair> { pair }
        };

        var report = await merger.MergeAsync(pairing, options, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Total);
        Assert.Equal(1, report.Succeeded);
        Assert.Empty(report.SkippedItems);
    }

    [Fact]
    public async Task MergeAsync_ForceAcceptedPair_ExtremeDuration_1HourVideo_BypassesValidation()
    {
        using var tempDir = new TempDirectory();
        var photoBytes = new byte[2048];
        var videoBytes = new byte[5000];

        var jpgPath = tempDir.CreateFile("ONE_HOUR.jpg", photoBytes);
        var movPath = tempDir.CreateFile("ONE_HOUR.mov", videoBytes);
        var outputDir = tempDir.Combine("output");

        var pair = new MediaPair(jpgPath, movPath);
        var pairing = new PairingResult
        {
            Pairs = [pair],
            UnmatchedPhotoCount = 0,
            UnmatchedVideoCount = 0
        };

        // 模拟 1 小时视频（3600 秒，远超 30s 上限）
        var fakeExif = new AdversarialExifTool
        {
            Duration = TimeSpan.FromHours(1)
        };

        var fakeImg = new AdversarialImageConverter();
        var fakeVideo = new AdversarialVideoConverter();
        var merger = new MotionPhotoMerger(fakeExif, fakeImg, fakeVideo);

        var options = new MergeOptions
        {
            InputDirectory = tempDir.Root,
            OutputDirectory = outputDir,
            SkipValidation = false,
            ForceAcceptedPairs = new HashSet<MediaPair> { pair }
        };

        var report = await merger.MergeAsync(pairing, options, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Total);
        Assert.Equal(1, report.Succeeded);
        Assert.Empty(report.SkippedItems);
    }

    [Fact]
    public async Task MergeAsync_ForceAcceptedPair_ConflictingContentIdentifiers_BypassesValidation()
    {
        using var tempDir = new TempDirectory();
        var photoBytes = new byte[2048];
        var videoBytes = new byte[5000];

        var jpgPath = tempDir.CreateFile("CONFLICT_CI.jpg", photoBytes);
        var movPath = tempDir.CreateFile("CONFLICT_CI.mov", videoBytes);
        var outputDir = tempDir.Combine("output");

        var pair = new MediaPair(jpgPath, movPath);
        var pairing = new PairingResult
        {
            Pairs = [pair],
            UnmatchedPhotoCount = 0,
            UnmatchedVideoCount = 0
        };

        // 构造相互冲突的 ContentIdentifier
        var fakeExif = new AdversarialExifTool
        {
            ContentIdentifiers =
            {
                ["photo"] = "UUID-PHOTO-1111-2222",
                ["video"] = "UUID-VIDEO-9999-8888"
            }
        };

        var fakeImg = new AdversarialImageConverter();
        var fakeVideo = new AdversarialVideoConverter();
        var merger = new MotionPhotoMerger(fakeExif, fakeImg, fakeVideo);

        var options = new MergeOptions
        {
            InputDirectory = tempDir.Root,
            OutputDirectory = outputDir,
            SkipValidation = false,
            ForceAcceptedPairs = new HashSet<MediaPair> { pair }
        };

        var report = await merger.MergeAsync(pairing, options, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Total);
        Assert.Equal(1, report.Succeeded);
        Assert.Empty(report.SkippedItems);
    }

    [Fact]
    public async Task MergeAsync_ForceAcceptedPair_OneSidedCreateDate_BypassesValidation()
    {
        using var tempDir = new TempDirectory();
        var photoBytes = new byte[2048];
        var videoBytes = new byte[5000];

        var jpgPath = tempDir.CreateFile("ONESIDED_DATE.jpg", photoBytes);
        var movPath = tempDir.CreateFile("ONESIDED_DATE.mov", videoBytes);
        var outputDir = tempDir.Combine("output");

        var pair = new MediaPair(jpgPath, movPath);
        var pairing = new PairingResult
        {
            Pairs = [pair],
            UnmatchedPhotoCount = 0,
            UnmatchedVideoCount = 0
        };

        // 仅照片有拍摄时间，视频无时间（正常校验会判定“仅照片含拍摄时间，直接拒绝”）
        var fakeExif = new AdversarialExifTool();
        fakeExif.FileDates[Path.GetFileName(jpgPath)] = new DateTime(2024, 6, 1, 10, 0, 0, DateTimeKind.Utc);
        // mov 留空

        var fakeImg = new AdversarialImageConverter();
        var fakeVideo = new AdversarialVideoConverter();
        var merger = new MotionPhotoMerger(fakeExif, fakeImg, fakeVideo);

        var options = new MergeOptions
        {
            InputDirectory = tempDir.Root,
            OutputDirectory = outputDir,
            SkipValidation = false,
            ForceAcceptedPairs = new HashSet<MediaPair> { pair }
        };

        var report = await merger.MergeAsync(pairing, options, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Total);
        Assert.Equal(1, report.Succeeded);
        Assert.Empty(report.SkippedItems);
    }

    #endregion

    #region 4. 同名多候选优先级仲裁与安全清理验证

    [Fact]
    public async Task MergeAsync_PrioritizesWhitelistedPair_WhenHeicJpgPngAllPresent()
    {
        using var tempDir = new TempDirectory();
        var photoBytes = new byte[2048];
        var videoBytes = new byte[5000];

        // 构造同一主干的三种格式照片与一个 MOV 视频
        var heicPath = tempDir.CreateFile("TRIPLE_01.heic", photoBytes);
        var jpgPath = tempDir.CreateFile("TRIPLE_01.jpg", photoBytes);
        var pngPath = tempDir.CreateFile("TRIPLE_01.png", photoBytes);
        var movPath = tempDir.CreateFile("TRIPLE_01.mov", videoBytes);
        var outputDir = tempDir.Combine("output");

        var pairing = MediaPairMatcher.Match([heicPath, jpgPath, pngPath, movPath]);
        Assert.Equal(3, pairing.Pairs.Count);

        var fakeExif = new AdversarialExifTool();
        var fakeImg = new AdversarialImageConverter();
        var fakeVideo = new AdversarialVideoConverter();
        var merger = new MotionPhotoMerger(fakeExif, fakeImg, fakeVideo);

        // 人工指定最低排名的 PNG 对
        var pngPair = new MediaPair(pngPath, movPath);

        var options = new MergeOptions
        {
            InputDirectory = tempDir.Root,
            OutputDirectory = outputDir,
            SourceFileAction = SourceFileAction.Delete, // 删除测试清理隔离性
            SkipValidation = false,
            ForceAcceptedPairs = new HashSet<MediaPair> { pngPair }
        };

        var report = await merger.MergeAsync(pairing, options, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Total);
        Assert.Equal(1, report.Succeeded);
        Assert.Empty(report.SkippedItems);
        Assert.Empty(report.Failures);

        // 验证被删除的是白名单指定的 PNG 与视频，而 HEIC 与 JPG 未被误删
        Assert.False(File.Exists(pngPath), "白名单指定的 PNG 应被合成并由 Delete 策略清理");
        Assert.False(File.Exists(movPath), "MOV 视频应被清理");
        Assert.True(File.Exists(heicPath), "未被选中的 HEIC 必须完好保留");
        Assert.True(File.Exists(jpgPath), "未被选中的 JPG 必须完好保留");
    }

    [Fact]
    public async Task MergeAsync_MultipleVideos_PrioritizesWhitelistedVideo()
    {
        using var tempDir = new TempDirectory();
        var photoBytes = new byte[2048];
        var videoBytes = new byte[5000];

        var jpgPath = tempDir.CreateFile("MULTI_VID.jpg", photoBytes);
        var movPath = tempDir.CreateFile("MULTI_VID.mov", videoBytes);
        var mp4Path = tempDir.CreateFile("MULTI_VID.mp4", videoBytes);
        var outputDir = tempDir.Combine("output");

        var pairing = MediaPairMatcher.Match([jpgPath, movPath, mp4Path]);
        Assert.Equal(2, pairing.Pairs.Count);

        var fakeExif = new AdversarialExifTool();
        var fakeImg = new AdversarialImageConverter();
        var fakeVideo = new AdversarialVideoConverter();
        var merger = new MotionPhotoMerger(fakeExif, fakeImg, fakeVideo);

        // 默认规则下 MOV 优于 MP4。此处人工指定 MP4 对
        var mp4Pair = new MediaPair(jpgPath, mp4Path);

        var options = new MergeOptions
        {
            InputDirectory = tempDir.Root,
            OutputDirectory = outputDir,
            SourceFileAction = SourceFileAction.Delete,
            SkipValidation = false,
            ForceAcceptedPairs = new HashSet<MediaPair> { mp4Pair }
        };

        var report = await merger.MergeAsync(pairing, options, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Total);
        Assert.Equal(1, report.Succeeded);
        Assert.Empty(report.SkippedItems);

        Assert.False(File.Exists(jpgPath), "JPG 应被清理");
        Assert.False(File.Exists(mp4Path), "白名单指定的 MP4 应被清理");
        Assert.True(File.Exists(movPath), "未被选中的 MOV 必须完好保留");
    }

    #endregion

    #region 5. 人工仲裁非同名配对动态注入与合成测试

    [Fact]
    public async Task MergeAsync_ArbitratedPair_WithDifferentStems_DynamicallyInjectedAndMerged()
    {
        using var tempDir = new TempDirectory();
        var photoBytes = new byte[2048];
        var videoBytes = new byte[5000];

        // 两个主干完全不同的文件，自动匹配引擎无法匹配
        var photoPath = tempDir.CreateFile("VACATION_PHOTO.jpg", photoBytes);
        var videoPath = tempDir.CreateFile("SURPRISE_CLIP.mov", videoBytes);
        var outputDir = tempDir.Combine("output");

        var pairing = MediaPairMatcher.Match([photoPath, videoPath]);
        Assert.Empty(pairing.Pairs);
        Assert.Equal(1, pairing.UnmatchedPhotoCount);
        Assert.Equal(1, pairing.UnmatchedVideoCount);

        var fakeExif = new AdversarialExifTool();
        var fakeImg = new AdversarialImageConverter();
        var fakeVideo = new AdversarialVideoConverter();
        var merger = new MotionPhotoMerger(fakeExif, fakeImg, fakeVideo);

        // 用户在桌面仲裁弹窗中手动将其配对
        var arbitratedPair = new MediaPair(photoPath, videoPath);

        var options = new MergeOptions
        {
            InputDirectory = tempDir.Root,
            OutputDirectory = outputDir,
            SourceFileAction = SourceFileAction.Keep,
            SkipValidation = false,
            ForceAcceptedPairs = new HashSet<MediaPair> { arbitratedPair }
        };

        var report = await merger.MergeAsync(pairing, options, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Total);
        Assert.Equal(1, report.Succeeded);
        Assert.Empty(report.SkippedItems);
        Assert.Empty(report.Failures);

        var expectedOutput = Path.Combine(outputDir, "MVIMG_VACATION_PHOTO.jpg");
        Assert.True(File.Exists(expectedOutput), "应以照片主干成功输出合成动态照片");
    }

    #endregion

    #region 6. 边界与防御性测试 (Null / Empty / 异常集合)

    [Fact]
    public async Task MergeAsync_WhenForceAcceptedPairsIsNull_StandardValidationApplies()
    {
        using var tempDir = new TempDirectory();
        var photoPath = tempDir.CreateFile("NULL_TEST.jpg", new byte[2048]);
        var movPath = tempDir.CreateFile("NULL_TEST.mov", new byte[5000]);
        var outputDir = tempDir.Combine("output");

        var pairing = MediaPairMatcher.Match([photoPath, movPath]);

        var fakeExif = new AdversarialExifTool
        {
            ContentIdentifiers = { ["photo"] = "ONLY_PHOTO_CI" } // 导致常规校验失败
        };
        var fakeImg = new AdversarialImageConverter();
        var fakeVideo = new AdversarialVideoConverter();
        var merger = new MotionPhotoMerger(fakeExif, fakeImg, fakeVideo);

        var options = new MergeOptions
        {
            InputDirectory = tempDir.Root,
            OutputDirectory = outputDir,
            SkipValidation = false,
            ForceAcceptedPairs = null
        };

        var report = await merger.MergeAsync(pairing, options, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Total);
        Assert.Equal(0, report.Succeeded);
        Assert.Single(report.SkippedItems);
    }

    [Fact]
    public async Task MergeAsync_WhenForceAcceptedPairsIsEmpty_StandardValidationApplies()
    {
        using var tempDir = new TempDirectory();
        var photoPath = tempDir.CreateFile("EMPTY_TEST.jpg", new byte[2048]);
        var movPath = tempDir.CreateFile("EMPTY_TEST.mov", new byte[5000]);
        var outputDir = tempDir.Combine("output");

        var pairing = MediaPairMatcher.Match([photoPath, movPath]);

        var fakeExif = new AdversarialExifTool
        {
            ContentIdentifiers = { ["photo"] = "ONLY_PHOTO_CI" } // 导致常规校验失败
        };
        var fakeImg = new AdversarialImageConverter();
        var fakeVideo = new AdversarialVideoConverter();
        var merger = new MotionPhotoMerger(fakeExif, fakeImg, fakeVideo);

        var options = new MergeOptions
        {
            InputDirectory = tempDir.Root,
            OutputDirectory = outputDir,
            SkipValidation = false,
            ForceAcceptedPairs = new HashSet<MediaPair>() // 空集合
        };

        var report = await merger.MergeAsync(pairing, options, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Total);
        Assert.Equal(0, report.Succeeded);
        Assert.Single(report.SkippedItems);
    }

    [Fact]
    public async Task MergeAsync_ForceAcceptedPair_WithMissingFile_RecordsFailureGracefully()
    {
        using var tempDir = new TempDirectory();
        var photoPath = tempDir.Combine("NON_EXISTENT.jpg");
        var videoPath = tempDir.Combine("NON_EXISTENT.mov");
        var outputDir = tempDir.Combine("output");

        var pair = new MediaPair(photoPath, videoPath);
        var pairing = new PairingResult
        {
            Pairs = [pair],
            UnmatchedPhotoCount = 0,
            UnmatchedVideoCount = 0
        };

        var fakeExif = new AdversarialExifTool();
        var fakeImg = new AdversarialImageConverter();
        var fakeVideo = new AdversarialVideoConverter();
        var merger = new MotionPhotoMerger(fakeExif, fakeImg, fakeVideo);

        var options = new MergeOptions
        {
            InputDirectory = tempDir.Root,
            OutputDirectory = outputDir,
            SkipValidation = false,
            ForceAcceptedPairs = new HashSet<MediaPair> { pair }
        };

        var report = await merger.MergeAsync(pairing, options, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Total);
        Assert.Equal(0, report.Succeeded);
        Assert.Equal(1, report.Failed);
        Assert.Single(report.Failures);
    }

    [Fact]
    public async Task MergeAsync_ForceAcceptedPair_WithUnicodeCharacters_BypassesValidationAndSucceeds()
    {
        using var tempDir = new TempDirectory();
        var photoBytes = new byte[2048];
        var videoBytes = new byte[5000];

        var photoPath = tempDir.CreateFile("实况测试_北京故宫.jpg", photoBytes);
        var videoPath = tempDir.CreateFile("实况测试_北京故宫.mov", videoBytes);
        var outputDir = tempDir.Combine("output");

        var pairing = MediaPairMatcher.Match([photoPath, videoPath]);

        var fakeExif = new AdversarialExifTool
        {
            ContentIdentifiers = { ["photo"] = "UNICODE_ONLY_CI" }
        };
        var fakeImg = new AdversarialImageConverter();
        var fakeVideo = new AdversarialVideoConverter();
        var merger = new MotionPhotoMerger(fakeExif, fakeImg, fakeVideo);

        var forcePair = new MediaPair(photoPath, videoPath);
        var options = new MergeOptions
        {
            InputDirectory = tempDir.Root,
            OutputDirectory = outputDir,
            SkipValidation = false,
            ForceAcceptedPairs = new HashSet<MediaPair> { forcePair }
        };

        var report = await merger.MergeAsync(pairing, options, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Total);
        Assert.Equal(1, report.Succeeded);
        Assert.Empty(report.SkippedItems);
    }

    [Fact]
    public async Task MergeAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        using var tempDir = new TempDirectory();
        var photoPath = tempDir.CreateFile("CANCEL_TEST.jpg", new byte[2048]);
        var movPath = tempDir.CreateFile("CANCEL_TEST.mov", new byte[5000]);
        var outputDir = tempDir.Combine("output");

        var pairing = MediaPairMatcher.Match([photoPath, movPath]);

        var fakeExif = new AdversarialExifTool();
        var fakeImg = new AdversarialImageConverter();
        var fakeVideo = new AdversarialVideoConverter();
        var merger = new MotionPhotoMerger(fakeExif, fakeImg, fakeVideo);

        var cts = new CancellationTokenSource();
        cts.Cancel(); // 预先取消

        var options = new MergeOptions
        {
            InputDirectory = tempDir.Root,
            OutputDirectory = outputDir,
            SkipValidation = false,
            ForceAcceptedPairs = new HashSet<MediaPair> { new(photoPath, movPath) }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            merger.MergeAsync(pairing, options, cts.Token));
    }

    #endregion

    #region Test Doubles

    private sealed class AdversarialImageConverter : IImageConverter
    {
        public Task ConvertToJpegAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
        {
            if (!File.Exists(sourcePath)) throw new FileNotFoundException("Image not found", sourcePath);
            File.WriteAllBytes(destinationPath, new byte[2048]);
            return Task.CompletedTask;
        }

        public Task ConvertToHeicAsync(string sourcePath, string destinationPath, int quality = 90, CancellationToken cancellationToken = default)
        {
            if (!File.Exists(sourcePath)) throw new FileNotFoundException("Image not found", sourcePath);
            File.WriteAllBytes(destinationPath, new byte[1024]);
            return Task.CompletedTask;
        }
    }

    private sealed class AdversarialVideoConverter : IVideoConverter
    {
        public Task ConvertToMp4Async(string sourcePath, string destinationPath, bool forceTranscode = false, CancellationToken cancellationToken = default)
        {
            if (!File.Exists(sourcePath)) throw new FileNotFoundException("Video not found", sourcePath);
            File.WriteAllBytes(destinationPath, new byte[5000]);
            return Task.CompletedTask;
        }

        public Task RemuxToMovAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
        {
            if (!File.Exists(sourcePath)) throw new FileNotFoundException("Video not found", sourcePath);
            File.WriteAllBytes(destinationPath, new byte[5000]);
            return Task.CompletedTask;
        }
    }

    private sealed class AdversarialExifTool : IExifTool
    {
        public Dictionary<string, string> ContentIdentifiers { get; } = new();
        public Dictionary<string, DateTime> FileDates { get; } = new(StringComparer.OrdinalIgnoreCase);
        public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(2.5);

        public Task WriteMotionPhotoTagsAsync(string imagePath, long videoOffset, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RemoveMotionPhotoTagsAsync(string imagePath, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task CopyAllTagsAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task CopyCoverTagsAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<long?> TryReadMicroVideoOffsetAsync(string imagePath, CancellationToken cancellationToken = default) =>
            Task.FromResult<long?>(null);

        public Task<string?> TryReadContentIdentifierAsync(string filePath, ContentIdentifierKind kind, CancellationToken cancellationToken = default)
        {
            var key = kind == ContentIdentifierKind.Photo ? "photo" : "video";
            return Task.FromResult(ContentIdentifiers.GetValueOrDefault(key));
        }

        public Task WriteAppleContentIdentifierAsync(string photoPath, string contentIdentifier, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task WriteAppleVideoMetadataAsync(string videoPath, string? photoPath, string contentIdentifier, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<DateTime?> TryReadCreateDateAsync(string filePath, CancellationToken cancellationToken = default)
        {
            if (FileDates.TryGetValue(Path.GetFileName(filePath), out var date))
            {
                return Task.FromResult<DateTime?>(date);
            }
            return Task.FromResult<DateTime?>(null);
        }

        public Task<TimeSpan?> TryReadDurationAsync(string filePath, CancellationToken cancellationToken = default) =>
            Task.FromResult<TimeSpan?>(Duration);

        public Task<bool> IsMirroredVideoAsync(string videoPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    #endregion
}
