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
    /// 用于测试的图片转换器 Mock
    /// </summary>
    private sealed class FakeImageConverter : IImageConverter
    {
        public Task ConvertToJpegAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
        {
            File.WriteAllBytes(destinationPath, new byte[2048]);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// 用于测试的视频转换器 Mock
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
    /// 用于测试的 ExifTool 元数据读写 Mock
    /// </summary>
    private sealed class FakeExifTool : IExifTool
    {
        public Dictionary<string, string> ContentIdentifiers { get; } = new();

        public Task WriteMotionPhotoTagsAsync(string imagePath, long videoOffset, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RemoveMotionPhotoTagsAsync(string imagePath, CancellationToken cancellationToken = default) =>
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

        public Task WriteAppleVideoMetadataAsync(string videoPath, string contentIdentifier, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<DateTime?> TryReadCreateDateAsync(string filePath, CancellationToken cancellationToken = default) =>
            Task.FromResult<DateTime?>(null);

        public Task<TimeSpan?> TryReadDurationAsync(string filePath, CancellationToken cancellationToken = default) =>
            Task.FromResult<TimeSpan?>(TimeSpan.FromSeconds(2.5));

        public Task<bool> IsMirroredVideoAsync(string videoPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
