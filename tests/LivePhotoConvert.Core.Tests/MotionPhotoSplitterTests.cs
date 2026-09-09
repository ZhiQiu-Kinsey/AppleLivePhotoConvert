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
        tempDir.CreateFile("MVIMG_20230520.jpg", combinedBytes);

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
    /// 测试 Apple 拆分模式：验证 JPEG 封面转为 HEIC，视频被封装为 MOV，且照片与视频写入了相同的 ContentIdentifier 配对 UUID
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
        tempDir.CreateFile("IMG_1234.jpg", combinedBytes);

        var outputDir = tempDir.Combine("output");

        var fakeExif = new FakeSplitterExifTool { MicroVideoOffset = 250 };
        var fakeVideo = new FakeSplitterVideoConverter();
        var fakeImage = new FakeSplitterImageConverter();
        var splitter = new MotionPhotoSplitter(fakeExif, fakeVideo, fakeImage);

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

        // Apple 实况照片始终输出大写扩展名 .HEIC + .MOV
        var extractedPhoto = Path.Combine(outputDir, "IMG_1234.HEIC");
        var extractedVideo = Path.Combine(outputDir, "IMG_1234.MOV");

        Assert.True(File.Exists(extractedPhoto));
        Assert.True(File.Exists(extractedVideo));
        Assert.NotNull(fakeExif.WrittenAppleContentIdentifier);
        Assert.NotNull(fakeExif.WrittenAppleVideoContentIdentifier);
        Assert.Equal(fakeExif.WrittenAppleContentIdentifier, fakeExif.WrittenAppleVideoContentIdentifier);
        Assert.Equal(extractedPhoto, fakeExif.WrittenAppleVideoPhotoPath);
        Assert.True(fakeImage.ConvertToHeicCalled, "JPEG 封面应被转为 HEIC");
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
    /// 测试默认 Keep 清理策略：拆分成功后原始动态照片文件完整保留在原输入目录，CleanedFileCount 为 0
    /// </summary>
    [Fact]
    public async Task SplitAsync_WithSourceFileActionKeep_ShouldLeaveSourceFilesIntact()
    {
        using var tempDir = new TempDirectory();

        var photoBytes = new byte[100];
        photoBytes[0] = 0xFF; photoBytes[1] = 0xD8; photoBytes[2] = 0xFF;
        var videoBytes = new byte[200];
        videoBytes[4] = (byte)'f'; videoBytes[5] = (byte)'t'; videoBytes[6] = (byte)'y'; videoBytes[7] = (byte)'p';
        videoBytes[8] = (byte)'m'; videoBytes[9] = (byte)'p'; videoBytes[10] = (byte)'4'; videoBytes[11] = (byte)'2';

        var combinedBytes = photoBytes.Concat(videoBytes).ToArray();
        var motionPhotoPath = tempDir.CreateFile("KEEP_TEST.jpg", combinedBytes);
        var outputDir = tempDir.Combine("output");

        var fakeExif = new FakeSplitterExifTool { MicroVideoOffset = 200 };
        var splitter = new MotionPhotoSplitter(fakeExif);

        var options = new SplitOptions
        {
            InputDirectory = tempDir.Root,
            OutputDirectory = outputDir,
            TargetFormat = SplitTargetFormat.Android,
            SourceFileAction = SourceFileAction.Keep
        };

        var report = await splitter.SplitAsync(options, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Total);
        Assert.Equal(1, report.Succeeded);
        Assert.Equal(0, report.CleanedFileCount);
        Assert.Empty(report.CleanupFailures);
        Assert.True(File.Exists(motionPhotoPath), "Keep 策略下原文件必须保留");
    }

    /// <summary>
    /// 测试 Move / MoveToSubfolder 清理策略：拆分成功后原动态照片被移动到“已拆分”子目录下
    /// </summary>
    [Fact]
    public async Task SplitAsync_WithSourceFileActionMove_ShouldMoveSourceFilesToSplittedSubfolder()
    {
        using var tempDir = new TempDirectory();

        var photoBytes = new byte[100];
        photoBytes[0] = 0xFF; photoBytes[1] = 0xD8; photoBytes[2] = 0xFF;
        var videoBytes = new byte[200];
        videoBytes[4] = (byte)'f'; videoBytes[5] = (byte)'t'; videoBytes[6] = (byte)'y'; videoBytes[7] = (byte)'p';
        videoBytes[8] = (byte)'m'; videoBytes[9] = (byte)'p'; videoBytes[10] = (byte)'4'; videoBytes[11] = (byte)'2';

        var combinedBytes = photoBytes.Concat(videoBytes).ToArray();
        var motionPhotoPath = tempDir.CreateFile("MOVE_TEST.jpg", combinedBytes);
        var outputDir = tempDir.Combine("output");

        var fakeExif = new FakeSplitterExifTool { MicroVideoOffset = 200 };
        var splitter = new MotionPhotoSplitter(fakeExif);

        var options = new SplitOptions
        {
            InputDirectory = tempDir.Root,
            OutputDirectory = outputDir,
            TargetFormat = SplitTargetFormat.Android,
            SourceFileAction = SourceFileAction.MoveToSubfolder
        };

        var report = await splitter.SplitAsync(options, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Total);
        Assert.Equal(1, report.Succeeded);
        Assert.Equal(1, report.CleanedFileCount);
        Assert.Empty(report.CleanupFailures);
        Assert.False(File.Exists(motionPhotoPath), "原输入目录中的原文件应被移走");
        var movedPath = tempDir.Combine(SourceFileCleaner.SplitFolderName, "MOVE_TEST.jpg");
        Assert.True(File.Exists(movedPath), $"原文件应移入 \"{SourceFileCleaner.SplitFolderName}\" 子目录");
    }

    /// <summary>
    /// 测试 Delete 清理策略：拆分成功后原动态照片被物理删除
    /// </summary>
    [Fact]
    public async Task SplitAsync_WithSourceFileActionDelete_ShouldDeleteSourceFiles()
    {
        using var tempDir = new TempDirectory();

        var photoBytes = new byte[100];
        photoBytes[0] = 0xFF; photoBytes[1] = 0xD8; photoBytes[2] = 0xFF;
        var videoBytes = new byte[200];
        videoBytes[4] = (byte)'f'; videoBytes[5] = (byte)'t'; videoBytes[6] = (byte)'y'; videoBytes[7] = (byte)'p';
        videoBytes[8] = (byte)'m'; videoBytes[9] = (byte)'p'; videoBytes[10] = (byte)'4'; videoBytes[11] = (byte)'2';

        var combinedBytes = photoBytes.Concat(videoBytes).ToArray();
        var motionPhotoPath = tempDir.CreateFile("DELETE_TEST.jpg", combinedBytes);
        var outputDir = tempDir.Combine("output");

        var fakeExif = new FakeSplitterExifTool { MicroVideoOffset = 200 };
        var splitter = new MotionPhotoSplitter(fakeExif);

        var options = new SplitOptions
        {
            InputDirectory = tempDir.Root,
            OutputDirectory = outputDir,
            TargetFormat = SplitTargetFormat.Android,
            SourceFileAction = SourceFileAction.Delete
        };

        var report = await splitter.SplitAsync(options, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Total);
        Assert.Equal(1, report.Succeeded);
        Assert.Equal(1, report.CleanedFileCount);
        Assert.Empty(report.CleanupFailures);
        Assert.False(File.Exists(motionPhotoPath), "Delete 策略下原文件应被删除");
    }

    /// <summary>
    /// 测试当文件非实况照片而被跳过时，即使配置了 Move 策略也绝对不清理原文件
    /// </summary>
    [Fact]
    public async Task SplitAsync_WithSourceFileActionMove_WhenSkipped_ShouldNotCleanSourceFiles()
    {
        using var tempDir = new TempDirectory();
        var normalPhoto = tempDir.CreateFile("REGULAR.jpg", [1, 2, 3, 4]);
        var outputDir = tempDir.Combine("output");

        var fakeExif = new FakeSplitterExifTool { MicroVideoOffset = null };
        var splitter = new MotionPhotoSplitter(fakeExif);

        var options = new SplitOptions
        {
            InputDirectory = tempDir.Root,
            OutputDirectory = outputDir,
            TargetFormat = SplitTargetFormat.Android,
            SourceFileAction = SourceFileAction.Move
        };

        var report = await splitter.SplitAsync(options, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Total);
        Assert.Equal(0, report.Succeeded);
        Assert.Equal(1, report.Skipped);
        Assert.Equal(0, report.CleanedFileCount);
        Assert.Empty(report.CleanupFailures);
        Assert.True(File.Exists(normalPhoto), "被跳过的普通图片绝对不应被移动或清理");
        Assert.False(File.Exists(tempDir.Combine(SourceFileCleaner.SplitFolderName, "REGULAR.jpg")), "子目录中不应存在未拆分的文件");
        Assert.Empty(Directory.GetFiles(tempDir.Combine(SourceFileCleaner.SplitFolderName)));
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
    /// 模拟测试用图片格式转换器桩
    /// </summary>
    private sealed class FakeSplitterImageConverter : IImageConverter
    {
        public bool ConvertToHeicCalled { get; private set; }

        public Task ConvertToJpegAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
        {
            File.Copy(sourcePath, destinationPath, overwrite: true);
            return Task.CompletedTask;
        }

        public Task ConvertToHeicAsync(string sourcePath, string destinationPath, int quality = 90, CancellationToken cancellationToken = default)
        {
            ConvertToHeicCalled = true;
            File.Copy(sourcePath, destinationPath, overwrite: true);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// 模拟测试用 ExifTool 桩
    /// </summary>
    private sealed class FakeSplitterExifTool : IExifTool
    {
        public long? MicroVideoOffset { get; init; }
        public string? WrittenAppleContentIdentifier { get; private set; }
        public string? WrittenAppleVideoContentIdentifier { get; private set; }
        public string? WrittenAppleVideoPhotoPath { get; private set; }

        public Task WriteMotionPhotoTagsAsync(string imagePath, long videoOffset, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RemoveMotionPhotoTagsAsync(string imagePath, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task CopyAllTagsAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task CopyCoverTagsAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default) =>
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

        public Task WriteAppleVideoMetadataAsync(string videoPath, string? photoPath, string contentIdentifier, CancellationToken cancellationToken = default)
        {
            WrittenAppleVideoContentIdentifier = contentIdentifier;
            WrittenAppleVideoPhotoPath = photoPath;
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

