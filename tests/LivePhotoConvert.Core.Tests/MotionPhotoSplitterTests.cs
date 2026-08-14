using LivePhotoConvert.Core.Abstractions;
using LivePhotoConvert.Core.Models;
using LivePhotoConvert.Core.Services;

namespace LivePhotoConvert.Core.Tests;

/// <summary>
/// 动态照片拆分器 (MotionPhotoSplitter) 的单元测试（验证 Android 模式解包、Apple 模式配对重构及非实况图片跳过）
/// </summary>
public class MotionPhotoSplitterTests
{
    /// <summary>
    /// 测试 Android 拆分模式：验证二进制流能正确切分为封面图片与内嵌 MP4 视频，且各部分大小与魔数准确
    /// </summary>
    [Fact]
    public async Task SplitAsync_AndroidFormat_ShouldSplit_Into_Photo_And_Mp4()
    {
        using var tempDir = new TempDirectory();

        // 构造一个模拟动态照片：前 100 字节 JPEG 头部魔数，后 200 字节 MP4 头部魔数
        var photoBytes = new byte[100];
        photoBytes[0] = 0xFF; photoBytes[1] = 0xD8; photoBytes[2] = 0xFF;
        var videoBytes = new byte[200];
        // MP4 ftypmp42 头部
        videoBytes[4] = (byte)'f'; videoBytes[5] = (byte)'t'; videoBytes[6] = (byte)'y'; videoBytes[7] = (byte)'p';
        videoBytes[8] = (byte)'m'; videoBytes[9] = (byte)'p'; videoBytes[10] = (byte)'4'; videoBytes[11] = (byte)'2';

        var combinedBytes = photoBytes.Concat(videoBytes).ToArray();
        var motionPhotoPath = tempDir.CreateFile("MVIMG_20230520.jpg", combinedBytes);

        var outputDir = tempDir.Combine("output");

        var fakeExif = new FakeSplitterExifTool { MicroVideoOffset = 200 };
        var splitter = new MotionPhotoSplitter(fakeExif);

        var options = new SplitOptions
        {
            InputDirectory = tempDir.Root,
            OutputDirectory = outputDir,
            TargetFormat = SplitTargetFormat.Android,
            Overwrite = false
        };

        var report = await splitter.SplitAsync(options, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Total);
        Assert.Equal(1, report.Succeeded);
        Assert.Equal(0, report.Skipped);
        Assert.Empty(report.Failures);

        var extractedPhoto = Path.Combine(outputDir, "MVIMG_20230520.jpg");
        var extractedVideo = Path.Combine(outputDir, "MVIMG_20230520.mp4");

        Assert.True(File.Exists(extractedPhoto));
        Assert.True(File.Exists(extractedVideo));
        Assert.Equal(100, new FileInfo(extractedPhoto).Length);
        Assert.Equal(200, new FileInfo(extractedVideo).Length);
    }

    /// <summary>
    /// 测试 Apple 拆分模式：验证视频被封装为 MOV，且照片与视频写入了相同的 ContentIdentifier 配对 UUID
    /// </summary>
    [Fact]
    public async Task SplitAsync_AppleFormat_ShouldSplit_Into_Photo_And_Mov_With_Metadata()
    {
        using var tempDir = new TempDirectory();

        var photoBytes = new byte[120];
        photoBytes[0] = 0xFF; photoBytes[1] = 0xD8; photoBytes[2] = 0xFF;
        var videoBytes = new byte[250];
        videoBytes[4] = (byte)'f'; videoBytes[5] = (byte)'t'; videoBytes[6] = (byte)'y'; videoBytes[7] = (byte)'p';

        var combinedBytes = photoBytes.Concat(videoBytes).ToArray();
        var motionPhotoPath = tempDir.CreateFile("IMG_1234.jpg", combinedBytes);

        var outputDir = tempDir.Combine("output");

        var fakeExif = new FakeSplitterExifTool { MicroVideoOffset = 250 };
        var fakeVideo = new FakeSplitterVideoConverter();
        var splitter = new MotionPhotoSplitter(fakeExif, fakeVideo);

        var options = new SplitOptions
        {
            InputDirectory = tempDir.Root,
            OutputDirectory = outputDir,
            TargetFormat = SplitTargetFormat.Apple,
            Overwrite = false
        };

        var report = await splitter.SplitAsync(options, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Total);
        Assert.Equal(1, report.Succeeded);
        Assert.Empty(report.Failures);

        var extractedPhoto = Path.Combine(outputDir, "IMG_1234.jpg");
        var extractedVideo = Path.Combine(outputDir, "IMG_1234.mov");

        Assert.True(File.Exists(extractedPhoto));
        Assert.True(File.Exists(extractedVideo));
        Assert.NotNull(fakeExif.WrittenAppleContentIdentifier);
        Assert.NotNull(fakeExif.WrittenAppleVideoContentIdentifier);
        Assert.Equal(fakeExif.WrittenAppleContentIdentifier, fakeExif.WrittenAppleVideoContentIdentifier);
    }

    /// <summary>
    /// 测试当输入图片为普通非动态照片时，拆分器能够安全跳过且不报错
    /// </summary>
    [Fact]
    public async Task SplitAsync_WhenNotMotionPhoto_ShouldSkipFile()
    {
        using var tempDir = new TempDirectory();
        tempDir.CreateFile("regular_photo.jpg", [1, 2, 3, 4]);

        var outputDir = tempDir.Combine("output");

        var fakeExif = new FakeSplitterExifTool { MicroVideoOffset = null };
        var splitter = new MotionPhotoSplitter(fakeExif);

        var options = new SplitOptions
        {
            InputDirectory = tempDir.Root,
            OutputDirectory = outputDir,
            TargetFormat = SplitTargetFormat.Android
        };

        var report = await splitter.SplitAsync(options, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Total);
        Assert.Equal(0, report.Succeeded);
        Assert.Equal(1, report.Skipped);
        Assert.Empty(report.Failures);
    }

    /// <summary>
    /// 模拟测试用视频转换器桩
    /// </summary>
    private sealed class FakeSplitterVideoConverter : IVideoConverter
    {
        public Task ConvertToMp4Async(string sourcePath, string destinationPath, bool forceTranscode = false, CancellationToken cancellationToken = default)
        {
            File.Copy(sourcePath, destinationPath, overwrite: true);
            return Task.CompletedTask;
        }

        public Task RemuxToMovAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
        {
            File.Copy(sourcePath, destinationPath, overwrite: true);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// 模拟测试用 ExifTool 桩
    /// </summary>
    private sealed class FakeSplitterExifTool : IExifTool
    {
        public long? MicroVideoOffset { get; set; }
        public string? WrittenAppleContentIdentifier { get; private set; }
        public string? WrittenAppleVideoContentIdentifier { get; private set; }

        public Task WriteMotionPhotoTagsAsync(string imagePath, long videoOffset, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RemoveMotionPhotoTagsAsync(string imagePath, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<long?> TryReadMicroVideoOffsetAsync(string imagePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(MicroVideoOffset);

        public Task<string?> TryReadContentIdentifierAsync(string filePath, ContentIdentifierKind kind, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task WriteAppleContentIdentifierAsync(string photoPath, string contentIdentifier, CancellationToken cancellationToken = default)
        {
            WrittenAppleContentIdentifier = contentIdentifier;
            return Task.CompletedTask;
        }

        public Task WriteAppleVideoMetadataAsync(string videoPath, string contentIdentifier, CancellationToken cancellationToken = default)
        {
            WrittenAppleVideoContentIdentifier = contentIdentifier;
            return Task.CompletedTask;
        }

        public Task<DateTime?> TryReadCreateDateAsync(string filePath, CancellationToken cancellationToken = default) =>
            Task.FromResult<DateTime?>(null);

        public Task<TimeSpan?> TryReadDurationAsync(string filePath, CancellationToken cancellationToken = default) =>
            Task.FromResult<TimeSpan?>(null);

        public Task<bool> IsMirroredVideoAsync(string videoPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

