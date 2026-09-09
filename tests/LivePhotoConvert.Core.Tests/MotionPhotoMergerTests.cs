using LivePhotoConvert.Core.Abstractions;
using LivePhotoConvert.Core.Matching;
using LivePhotoConvert.Core.Models;
using LivePhotoConvert.Core.Services;

namespace LivePhotoConvert.Core.Tests;

/// <summary>
/// 动态照片合成器 (MotionPhotoMerger) 的单元测试
/// </summary>
public class MotionPhotoMergerTests
{
    /// <summary>
    /// 测试当同名存在多种格式候选（如 HEIC 和 JPG）时，选用更高优先级的 HEIC 进行合成，
    /// 被替代的同名候选不应计入 SkippedItems（防止误报校验失败）。
    /// </summary>
    [Fact]
    public async Task MergeAsync_WithMultipleFormatCandidates_ShouldNotAddSupersededCandidatesToSkippedItems()
    {
        using var tempDir = new TempDirectory();
        var photoBytes = new byte[2048];
        var videoBytes = new byte[5000];

        var heicPath = tempDir.CreateFile("IMG_0001.heic", photoBytes);
        var jpgPath = tempDir.CreateFile("IMG_0001.jpg", photoBytes);
        var movPath = tempDir.CreateFile("IMG_0001.mov", videoBytes);

        var outputDir = tempDir.Combine("output");

        var pairing = MediaPairMatcher.Match([heicPath, jpgPath, movPath]);
        Assert.Equal(2, pairing.Pairs.Count);

        var fakeExif = new FakeExifTool();
        var fakeImg = new FakeImageConverter();
        var fakeVideo = new FakeVideoConverter();

        var merger = new MotionPhotoMerger(fakeExif, fakeImg, fakeVideo);
        var options = new MergeOptions
        {
            InputDirectory = tempDir.Root,
            OutputDirectory = outputDir,
            SourceFileAction = SourceFileAction.Keep,
            SkipValidation = false
        };

        var report = await merger.MergeAsync(pairing, options, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Total);
        Assert.Equal(1, report.Succeeded);
        Assert.Empty(report.SkippedItems);
        Assert.Empty(report.Failures);
    }

    /// <summary>
    /// 测试当配对校验未通过时（例如单边存在 ContentIdentifier），
    /// 应正确记录到 SkippedItems 中并体现在 Total 总数与成功数统计中。
    /// </summary>
    [Fact]
    public async Task MergeAsync_WhenValidationFails_ShouldRecordToSkippedItemsAndReflectInTotal()
    {
        using var tempDir = new TempDirectory();
        var photoBytes = new byte[2048];
        var videoBytes = new byte[5000];

        var jpgPath = tempDir.CreateFile("IMG_0001.jpg", photoBytes);
        var movPath = tempDir.CreateFile("IMG_0001.mov", videoBytes);

        var outputDir = tempDir.Combine("output");

        var pairing = MediaPairMatcher.Match([jpgPath, movPath]);

        var fakeExif = new FakeExifTool
        {
            // 单边存在 ContentIdentifier 会导致校验失败
            ContentIdentifiers = { ["photo"] = "ID-PHOTO-ONLY" }
        };
        var fakeImg = new FakeImageConverter();
        var fakeVideo = new FakeVideoConverter();

        var merger = new MotionPhotoMerger(fakeExif, fakeImg, fakeVideo);
        var options = new MergeOptions
        {
            InputDirectory = tempDir.Root,
            OutputDirectory = outputDir,
            SourceFileAction = SourceFileAction.Keep,
            SkipValidation = false
        };

        var report = await merger.MergeAsync(pairing, options, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Total);
        Assert.Equal(0, report.Succeeded);
        Assert.Single(report.SkippedItems);
        Assert.Empty(report.Failures);
    }

    /// <summary>
    /// 测试当源图片为 HEIC 格式时，合成流水线应将其转码为 JPEG 并完整复制元数据
    /// </summary>
    [Fact]
    public async Task MergeAsync_WhenPhotoIsHeic_ShouldConvertJpegAndCopyAllTags()
    {
        using var tempDir = new TempDirectory();
        var photoBytes = new byte[2048];
        var videoBytes = new byte[5000];

        // 构造 HEIC 头部
        photoBytes[4] = (byte)'f'; photoBytes[5] = (byte)'t'; photoBytes[6] = (byte)'y'; photoBytes[7] = (byte)'p';
        photoBytes[8] = (byte)'h'; photoBytes[9] = (byte)'e'; photoBytes[10] = (byte)'i'; photoBytes[11] = (byte)'c';

        var heicPath = tempDir.CreateFile("IMG_0001.heic", photoBytes);
        var movPath = tempDir.CreateFile("IMG_0001.mov", videoBytes);

        var outputDir = tempDir.Combine("output");
        var pairing = MediaPairMatcher.Match([heicPath, movPath]);

        var fakeExif = new FakeExifTool();
        var fakeImg = new FakeImageConverter();
        var fakeVideo = new FakeVideoConverter();

        var merger = new MotionPhotoMerger(fakeExif, fakeImg, fakeVideo);
        var options = new MergeOptions
        {
            InputDirectory = tempDir.Root,
            OutputDirectory = outputDir,
            SourceFileAction = SourceFileAction.Keep,
            SkipValidation = true
        };

        var report = await merger.MergeAsync(pairing, options, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Total);
        Assert.Equal(1, report.Succeeded);
        Assert.Empty(report.Failures);
        Assert.True(fakeExif.CopyCoverTagsCalled, "HEIC 封面转码 JPEG 时必须调用 CopyCoverTagsAsync 保证元数据不丢失且方向不被二次旋转");
    }

    [Theory]
    [InlineData(MergeNamingFormat.Original, "MVIMG_IMG_0001.jpg")]
    [InlineData(MergeNamingFormat.XiaomiWithOriginal, "MVIMG_20260905_124144_IMG_0001.jpg")]
    [InlineData(MergeNamingFormat.XiaomiClean, "MVIMG_20260905_124144.jpg")]
    public async Task MergeAsync_Should_Respect_NamingFormat_When_ExifDate_Available(
        MergeNamingFormat namingFormat,
        string expectedFileName)
    {
        using var tempDir = new TempDirectory();
        var photoPath = tempDir.CreateFile("IMG_0001.jpg", new byte[2048]);
        var movPath = tempDir.CreateFile("IMG_0001.mov", new byte[5000]);

        var outputDir = tempDir.Combine("output");
        var pairing = MediaPairMatcher.Match([photoPath, movPath]);

        var fakeExif = new FakeExifTool
        {
            CreateDate = new DateTime(2026, 9, 5, 12, 41, 44)
        };
        var fakeImg = new FakeImageConverter();
        var fakeVideo = new FakeVideoConverter();

        var merger = new MotionPhotoMerger(fakeExif, fakeImg, fakeVideo);
        var options = new MergeOptions
        {
            InputDirectory = tempDir.Root,
            OutputDirectory = outputDir,
            SourceFileAction = SourceFileAction.Keep,
            SkipValidation = true,
            NamingFormat = namingFormat
        };

        var report = await merger.MergeAsync(pairing, options, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Total);
        Assert.Equal(1, report.Succeeded);
        Assert.Empty(report.Failures);

        var expectedPath = Path.Combine(outputDir, expectedFileName);
        Assert.True(File.Exists(expectedPath), $"预期输出文件 {expectedFileName} 不存在，实际输出目录文件: {string.Join(", ", Directory.GetFiles(outputDir))}");
    }

    [Fact]
    public async Task MergeAsync_Should_Fallback_To_FileNameDate_When_Exif_Missing()
    {
        using var tempDir = new TempDirectory();
        var photoPath = tempDir.CreateFile("IMG_20240815_143000.jpg", new byte[2048]);
        var movPath = tempDir.CreateFile("IMG_20240815_143000.mov", new byte[5000]);

        var outputDir = tempDir.Combine("output");
        var pairing = MediaPairMatcher.Match([photoPath, movPath]);

        var fakeExif = new FakeExifTool
        {
            CreateDate = null // 无 EXIF 拍摄时间
        };
        var fakeImg = new FakeImageConverter();
        var fakeVideo = new FakeVideoConverter();

        var merger = new MotionPhotoMerger(fakeExif, fakeImg, fakeVideo);
        var options = new MergeOptions
        {
            InputDirectory = tempDir.Root,
            OutputDirectory = outputDir,
            SourceFileAction = SourceFileAction.Keep,
            SkipValidation = true,
            NamingFormat = MergeNamingFormat.XiaomiWithOriginal
        };

        var report = await merger.MergeAsync(pairing, options, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Total);
        Assert.Equal(1, report.Succeeded);
        Assert.Empty(report.Failures);

        var expectedPath = Path.Combine(outputDir, "MVIMG_20240815_143000_IMG_20240815_143000.jpg");
        Assert.True(File.Exists(expectedPath), $"应从文件名回退解析时间并生成正确格式: {string.Join(", ", Directory.GetFiles(outputDir))}");
    }

    /// <summary>
    /// 测试当配置了 ForceAcceptedPairs 白名单时，能绕过时差与时长校验并成功完成合成。
    /// </summary>
    [Fact]
    public async Task MergeAsync_WithForceAcceptedPair_ShouldBypassValidationAndSucceed()
    {
        using var tempDir = new TempDirectory();
        var photoBytes = new byte[2048];
        var videoBytes = new byte[5000];

        var jpgPath = tempDir.CreateFile("IMG_0001.jpg", photoBytes);
        var movPath = tempDir.CreateFile("IMG_0001.mov", videoBytes);
        var outputDir = tempDir.Combine("output");

        var pair = new MediaPair(jpgPath, movPath);
        var pairing = new PairingResult
        {
            Pairs = [pair],
            UnmatchedPhotoCount = 0,
            UnmatchedVideoCount = 0
        };

        // 构造时差 10 秒（远超 3 秒阈值），若正常走 PairValidator 会被直接拒绝
        var fakeExif = new FakeExifTool();
        fakeExif.FileDates[Path.GetFileName(jpgPath)] = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        fakeExif.FileDates[Path.GetFileName(movPath)] = new DateTime(2024, 1, 1, 12, 0, 10, DateTimeKind.Utc);

        var fakeImg = new FakeImageConverter();
        var fakeVideo = new FakeVideoConverter();
        var merger = new MotionPhotoMerger(fakeExif, fakeImg, fakeVideo);

        var options = new MergeOptions
        {
            InputDirectory = tempDir.Root,
            OutputDirectory = outputDir,
            SourceFileAction = SourceFileAction.Keep,
            SkipValidation = false,
            ForceAcceptedPairs = new HashSet<MediaPair> { pair }
        };

        var report = await merger.MergeAsync(pairing, options, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Total);
        Assert.Equal(1, report.Succeeded);
        Assert.Empty(report.SkippedItems);
        Assert.Empty(report.Failures);
    }

    /// <summary>
    /// 测试当同名存在多种格式时（如 HEIC 与 JPG），白名单中的候选对（JPG）优先于默认规则优先级更高的候选对（HEIC）。
    /// </summary>
    [Fact]
    public async Task MergeAsync_WithForceAcceptedPair_ShouldPrioritizeWhitelistedCandidateOverHigherRankedFormat()
    {
        using var tempDir = new TempDirectory();
        var photoBytes = new byte[2048];
        var videoBytes = new byte[5000];

        var heicPath = tempDir.CreateFile("IMG_0001.heic", photoBytes);
        var jpgPath = tempDir.CreateFile("IMG_0001.jpg", photoBytes);
        var movPath = tempDir.CreateFile("IMG_0001.mov", videoBytes);
        var outputDir = tempDir.Combine("output");

        var pairing = MediaPairMatcher.Match([heicPath, jpgPath, movPath]);
        Assert.Equal(2, pairing.Pairs.Count);

        var fakeExif = new FakeExifTool();
        var fakeImg = new FakeImageConverter();
        var fakeVideo = new FakeVideoConverter();
        var merger = new MotionPhotoMerger(fakeExif, fakeImg, fakeVideo);

        var jpgPair = new MediaPair(jpgPath, movPath);
        var options = new MergeOptions
        {
            InputDirectory = tempDir.Root,
            OutputDirectory = outputDir,
            SourceFileAction = SourceFileAction.Delete,
            SkipValidation = false,
            ForceAcceptedPairs = new HashSet<MediaPair> { jpgPair }
        };

        var report = await merger.MergeAsync(pairing, options, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Total);
        Assert.Equal(1, report.Succeeded);
        Assert.Empty(report.SkippedItems);
        Assert.Empty(report.Failures);

        // 验证被删除的是白名单选中的 JPG 原始文件，而 HEIC 未被选中使用依然保留
        Assert.False(File.Exists(jpgPath), "白名单指定的 JPG 应被合成并由 Delete 策略清理");
        Assert.True(File.Exists(heicPath), "未被选中的 HEIC 原片应依然存在");
    }

    /// <summary>
    /// 测试白名单路径大小写不敏感与反斜杠差异匹配能力。
    /// </summary>
    [Fact]
    public async Task MergeAsync_WithForceAcceptedPair_CaseInsensitivePath_ShouldMatch()
    {
        using var tempDir = new TempDirectory();
        var photoBytes = new byte[2048];
        var videoBytes = new byte[5000];

        var jpgPath = tempDir.CreateFile("IMG_0001.jpg", photoBytes);
        var movPath = tempDir.CreateFile("IMG_0001.mov", videoBytes);
        var outputDir = tempDir.Combine("output");

        var pairing = MediaPairMatcher.Match([jpgPath, movPath]);

        // 模拟单边 ContentIdentifier 导致校验必败的场景
        var fakeExif = new FakeExifTool
        {
            ContentIdentifiers = { ["photo"] = "ID-PHOTO-ONLY" }
        };
        var fakeImg = new FakeImageConverter();
        var fakeVideo = new FakeVideoConverter();
        var merger = new MotionPhotoMerger(fakeExif, fakeImg, fakeVideo);

        // 使用全大写与正斜杠构造白名单对
        var forcePair = new MediaPair(jpgPath.ToUpperInvariant().Replace('\\', '/'), movPath.ToUpperInvariant().Replace('\\', '/'));

        var options = new MergeOptions
        {
            InputDirectory = tempDir.Root,
            OutputDirectory = outputDir,
            SourceFileAction = SourceFileAction.Keep,
            SkipValidation = false,
            ForceAcceptedPairs = new HashSet<MediaPair> { forcePair }
        };

        var report = await merger.MergeAsync(pairing, options, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Total);
        Assert.Equal(1, report.Succeeded);
        Assert.Empty(report.SkippedItems);
    }

    /// <summary>
    /// 用于测试的图片转换器桩
    /// </summary>
    private sealed class FakeImageConverter : IImageConverter
    {
        public Task ConvertToJpegAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
        {
            File.WriteAllBytes(destinationPath, new byte[2048]);
            return Task.CompletedTask;
        }

        public Task ConvertToHeicAsync(string sourcePath, string destinationPath, int quality = 90, CancellationToken cancellationToken = default)
        {
            File.WriteAllBytes(destinationPath, new byte[1024]);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// 用于测试的视频转换器桩
    /// </summary>
    private sealed class FakeVideoConverter : IVideoConverter
    {
        public Task ConvertToMp4Async(string sourcePath, string destinationPath, bool forceTranscode = false, CancellationToken cancellationToken = default)
        {
            File.WriteAllBytes(destinationPath, new byte[5000]);
            return Task.CompletedTask;
        }

        public Task RemuxToMovAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
        {
            File.WriteAllBytes(destinationPath, new byte[5000]);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// 用于测试的 ExifTool 元数据读写桩
    /// </summary>
    private sealed class FakeExifTool : IExifTool
    {
        public Dictionary<string, string> ContentIdentifiers { get; } = new();
        public Dictionary<string, DateTime> FileDates { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool CopyAllTagsCalled { get; private set; }
        public bool CopyCoverTagsCalled { get; private set; }

        public Task WriteMotionPhotoTagsAsync(string imagePath, long videoOffset, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RemoveMotionPhotoTagsAsync(string imagePath, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task CopyAllTagsAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
        {
            CopyAllTagsCalled = true;
            return Task.CompletedTask;
        }

        public Task CopyCoverTagsAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
        {
            CopyCoverTagsCalled = true;
            return Task.CompletedTask;
        }

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

        public DateTime? CreateDate { get; init; }

        public Task<DateTime?> TryReadCreateDateAsync(string filePath, CancellationToken cancellationToken = default)
        {
            if (FileDates.TryGetValue(Path.GetFileName(filePath), out var date))
            {
                return Task.FromResult<DateTime?>(date);
            }
            return Task.FromResult(CreateDate);
        }

        public Task<TimeSpan?> TryReadDurationAsync(string filePath, CancellationToken cancellationToken = default) =>
            Task.FromResult<TimeSpan?>(null);

        public Task<bool> IsMirroredVideoAsync(string videoPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
