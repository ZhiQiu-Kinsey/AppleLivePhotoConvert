using System.Security.Cryptography;
using LivePhotoConvert.Core.Abstractions;
using LivePhotoConvert.Core.Models;
using LivePhotoConvert.Core.Services;

namespace LivePhotoConvert.Core.Tests;

/// <summary>
/// 空间瘦身服务 (MotionPhotoStripper) 的单元测试（包含免转码只读分析与就地瘦身流水线）
/// </summary>
public class MotionPhotoStripperTests
{
    /// <summary>
    /// 测试 AnalyzeAsync 目录扫描分析：验证多类型候选（含视频 JPG、含视频 HEIC、纯 JPG）体积与节省空间预估的准确性
    /// </summary>
    [Fact]
    public async Task AnalyzeAsync_DirectoryWithMotionPhotos_ShouldCalculateAccurateEstimates()
    {
        using var tempDir = new TempDirectory();

        // 构造 photo1.jpg: 1000 字节图片 + 2000 字节视频 = 3000 字节
        var photo1Path = tempDir.CreateFile("photo1.jpg", new byte[3000]);
        // 构造 photo2.heic: 800 字节图片 + 1500 字节视频 = 2300 字节
        var photo2Path = tempDir.CreateFile("photo2.heic", new byte[2300]);
        // 构造 photo3.jpg: 500 字节纯图片无视频
        var photo3Path = tempDir.CreateFile("photo3.jpg", new byte[500]);

        var fakeExif = new FakeStripperExifTool();
        fakeExif.Offsets[photo1Path] = 2000;
        fakeExif.Offsets[photo2Path] = 1500;
        fakeExif.Offsets[photo3Path] = null;

        var fakeImageConverter = new FakeStripperImageConverter();
        var stripper = new MotionPhotoStripper(fakeExif, fakeImageConverter);

        var report = await stripper.AnalyzeAsync(tempDir.Root, convertToHeic: true, TestContext.Current.CancellationToken);

        Assert.Equal(3, report.Items.Count);

        // 验证总体统计
        // TotalOriginalBytes = 3000 + 2300 + 500 = 5800
        Assert.Equal(5800, report.TotalOriginalBytes);
        Assert.Equal(5800, report.OriginalTotalBytes);

        // TotalVideoBytes = 2000 + 1500 + 0 = 3500
        Assert.Equal(3500, report.TotalVideoBytes);
        Assert.Equal(3500, report.VideoTotalBytes);

        // photo1.jpg: cleanImage = 1000, heic = 1000 * 0.45 = 450
        // photo2.heic: cleanImage = 800, already heic = 800
        // photo3.jpg: cleanImage = 500, heic = 500 * 0.45 = 225
        // TotalEstimatedHeic = 450 + 800 + 225 = 1475
        Assert.Equal(1475, report.TotalEstimatedHeicBytes);
        Assert.Equal(1475, report.EstimatedHeicTotalBytes);

        // photo1: saved = 3000 - 450 = 2550
        // photo2: saved = 2300 - 800 = 1500
        // photo3: saved = 500 - 225 = 275
        // TotalEstimatedSaved = 2550 + 1500 + 275 = 4325
        Assert.Equal(4325, report.TotalEstimatedSavedBytes);
        Assert.Equal(4325, report.EstimatedSavedBytes);

        // 验证各项明细
        var item1 = report.Items.First(x => x.FilePath == photo1Path);
        Assert.Equal(photo1Path, item1.SourcePath);
        Assert.Equal(3000, item1.OriginalBytes);
        Assert.Equal(2000, item1.VideoBytes);
        Assert.Equal(2000, item1.EmbeddedVideoBytes);
        Assert.True(item1.HasEmbeddedVideo);
        Assert.True(item1.NeedsHeicConversion);
        Assert.Equal(450, item1.EstimatedFinalBytes);
        Assert.Equal(2550, item1.EstimatedSavedBytes);

        var item2 = report.Items.First(x => x.FilePath == photo2Path);
        Assert.Equal(2300, item2.OriginalBytes);
        Assert.Equal(1500, item2.VideoBytes);
        Assert.True(item2.HasEmbeddedVideo);
        Assert.False(item2.NeedsHeicConversion, "已是 HEIC 的无需再次转码");
        Assert.Equal(800, item2.EstimatedFinalBytes);
        Assert.Equal(1500, item2.EstimatedSavedBytes);

        var item3 = report.Items.First(x => x.FilePath == photo3Path);
        Assert.Equal(500, item3.OriginalBytes);
        Assert.Equal(0, item3.VideoBytes);
        Assert.Null(item3.EmbeddedVideoBytes);
        Assert.False(item3.HasEmbeddedVideo);
        Assert.True(item3.NeedsHeicConversion);
        Assert.Equal(225, item3.EstimatedFinalBytes);
        Assert.Equal(275, item3.EstimatedSavedBytes);
    }

    /// <summary>
    /// 测试 AnalyzeAsync 单文件直接分析模式
    /// </summary>
    [Fact]
    public async Task AnalyzeAsync_SingleFile_ShouldReturnSingleItemReport()
    {
        using var tempDir = new TempDirectory();
        var photoPath = tempDir.CreateFile("single.jpg", new byte[1000]);

        var fakeExif = new FakeStripperExifTool();
        fakeExif.Offsets[photoPath] = 400;

        var fakeImageConverter = new FakeStripperImageConverter();
        var stripper = new MotionPhotoStripper(fakeExif, fakeImageConverter);

        var report = await stripper.AnalyzeAsync(photoPath, TestContext.Current.CancellationToken);

        Assert.Single(report.Items);
        Assert.Equal(photoPath, report.Items[0].FilePath);
        Assert.Equal(1000, report.TotalOriginalBytes);
        Assert.Equal(400, report.TotalVideoBytes);
    }

    /// <summary>
    /// 测试 AnalyzeAsync 只读安全性铁律：分析前后源文件哈希、修改时间与大小绝对不发生任何改变
    /// </summary>
    [Fact]
    public async Task AnalyzeAsync_IsReadOnly_ShouldNotModifySourceFiles()
    {
        using var tempDir = new TempDirectory();
        var bytes1 = new byte[2000];
        Random.Shared.NextBytes(bytes1);
        var bytes2 = new byte[1500];
        Random.Shared.NextBytes(bytes2);

        var path1 = tempDir.CreateFile("safe1.jpg", bytes1);
        var path2 = tempDir.CreateFile("safe2.heic", bytes2);

        var hash1Before = SHA256.HashData(File.ReadAllBytes(path1));
        var hash2Before = SHA256.HashData(File.ReadAllBytes(path2));
        var writeTime1Before = File.GetLastWriteTimeUtc(path1);
        var writeTime2Before = File.GetLastWriteTimeUtc(path2);

        var fakeExif = new FakeStripperExifTool();
        fakeExif.Offsets[path1] = 800;
        fakeExif.Offsets[path2] = null;

        var fakeImageConverter = new FakeStripperImageConverter();
        var stripper = new MotionPhotoStripper(fakeExif, fakeImageConverter);

        var report = await stripper.AnalyzeAsync(tempDir.Root, TestContext.Current.CancellationToken);

        Assert.Equal(2, report.Items.Count);

        // 校验源文件内容与元属性完好无损
        Assert.Equal(hash1Before, SHA256.HashData(File.ReadAllBytes(path1)));
        Assert.Equal(hash2Before, SHA256.HashData(File.ReadAllBytes(path2)));
        Assert.Equal(writeTime1Before, File.GetLastWriteTimeUtc(path1));
        Assert.Equal(writeTime2Before, File.GetLastWriteTimeUtc(path2));

        // 校验目录内未产生任何临时残留文件
        var filesInDir = Directory.GetFiles(tempDir.Root);
        Assert.Equal(2, filesInDir.Length);
    }

    /// <summary>
    /// 测试当传入不存在的路径时抛出 DirectoryNotFoundException
    /// </summary>
    [Fact]
    public async Task AnalyzeAsync_WhenPathNotFound_ShouldThrowException()
    {
        var fakeExif = new FakeStripperExifTool();
        var fakeImageConverter = new FakeStripperImageConverter();
        var stripper = new MotionPhotoStripper(fakeExif, fakeImageConverter);

        var missingPath = Path.Combine(Path.GetTempPath(), $"nonexistent_{Guid.NewGuid():N}");

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            stripper.AnalyzeAsync(missingPath, TestContext.Current.CancellationToken).AsTask());
    }

    /// <summary>
    /// 测试 AnalyzeAsync 结合 StripOptions 且关闭 ConvertToHeic 时的预估计算
    /// </summary>
    [Fact]
    public async Task AnalyzeAsync_WithOptions_ShouldRespectConvertToHeicFalse()
    {
        using var tempDir = new TempDirectory();
        var photoPath = tempDir.CreateFile("photo.jpg", new byte[3000]);

        var fakeExif = new FakeStripperExifTool();
        fakeExif.Offsets[photoPath] = 1000;

        var fakeImageConverter = new FakeStripperImageConverter();
        var stripper = new MotionPhotoStripper(fakeExif, fakeImageConverter);

        var options = new StripOptions
        {
            InputDirectory = tempDir.Root,
            ConvertToHeic = false
        };

        var report = await stripper.AnalyzeAsync(options, TestContext.Current.CancellationToken);

        Assert.Single(report.Items);
        var item = report.Items[0];
        Assert.False(item.NeedsHeicConversion);
        Assert.Equal(2000, item.EstimatedFinalBytes); // 原图 3000 - 视频 1000 = 纯图片 2000
        Assert.Equal(1000, item.EstimatedSavedBytes);
        Assert.Equal(2000, report.TotalEstimatedHeicBytes);
        Assert.Equal(1000, report.TotalEstimatedSavedBytes);
    }

    /// <summary>
    /// 测试 StripAsync 就地模式：剥离视频并转码为 HEIC，原 JPEG 被原子替换
    /// </summary>
    [Fact]
    public async Task StripAsync_InPlace_ShouldStripVideoAndTranscodeHeic()
    {
        using var tempDir = new TempDirectory();
        var originalBytes = new byte[2000];
        var photoPath = tempDir.CreateFile("test.jpg", originalBytes);

        var fakeExif = new FakeStripperExifTool();
        fakeExif.Offsets[photoPath] = 800;

        var fakeImageConverter = new FakeStripperImageConverter();
        var stripper = new MotionPhotoStripper(fakeExif, fakeImageConverter);

        var options = new StripOptions
        {
            InputDirectory = tempDir.Root,
            OutputDirectory = null, // 就地修改
            ConvertToHeic = true,
            HeicQuality = 90
        };

        var report = await stripper.StripAsync(options, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Total);
        Assert.Equal(1, report.StrippedCount);
        Assert.Equal(1, report.ConvertedCount);
        Assert.Equal(0, report.Skipped);
        Assert.Empty(report.Failures);

        // 原 test.jpg 应被替换为 test.heic
        var heicPath = tempDir.Combine("test.heic");
        Assert.True(File.Exists(heicPath), "就地转换后应生成同名 test.heic 文件");
        Assert.False(File.Exists(photoPath), "原有的 test.jpg 文件应被安全清理");
    }

    /// <summary>
    /// 验证 AnalyzeAsync 正确识别苹果实况照片配对（HEIC + 同名伴随 MOV）并计入视频体积与节省预估
    /// </summary>
    [Fact]
    public async Task AnalyzeAsync_WithCompanionMovVideo_ShouldCalculateAccurateEstimates()
    {
        using var tempDir = new TempDirectory();
        // 构造 live1.heic (1500 字节) + live1.mov (3500 字节)
        var heicPath = tempDir.CreateFile("live1.heic", new byte[1500]);
        tempDir.CreateFile("live1.mov", new byte[3500]);

        var fakeExif = new FakeStripperExifTool();
        fakeExif.Offsets[heicPath] = null; // 苹果 HEIC 本身不含安卓 MicroVideo
        var fakeImageConverter = new FakeStripperImageConverter();
        var stripper = new MotionPhotoStripper(fakeExif, fakeImageConverter);

        var report = await stripper.AnalyzeAsync(tempDir.Root, convertToHeic: true, TestContext.Current.CancellationToken);

        Assert.Single(report.Items);
        // 总大小应为 1500 + 3500 = 5000 字节
        Assert.Equal(5000, report.TotalOriginalBytes);
        Assert.Equal(3500, report.TotalVideoBytes);
        // HEIC 不需要转 HEIC，最终大小保留 1500 字节
        Assert.Equal(1500, report.TotalEstimatedHeicBytes);
        // 预估节省 = 3500 字节（剥离 MOV）
        Assert.Equal(3500, report.TotalEstimatedSavedBytes);

        var item = report.Items[0];
        Assert.True(item.HasEmbeddedVideo);
        Assert.False(item.NeedsHeicConversion);
        Assert.Equal(1500, item.EstimatedFinalBytes);
        Assert.Equal(3500, item.EstimatedSavedBytes);
    }

    /// <summary>
    /// 验证 StripAsync 就地覆盖模式下正确删除苹果实况照片配对的伴随 MOV 视频并保留高质量静态 HEIC
    /// </summary>
    [Fact]
    public async Task StripAsync_InPlaceWithCompanionMovVideo_ShouldDeleteCompanionVideoAndSaveSpace()
    {
        using var tempDir = new TempDirectory();
        var heicPath = tempDir.CreateFile("live2.heic", new byte[2000]);
        var movPath = tempDir.CreateFile("live2.mov", new byte[4000]);

        var fakeExif = new FakeStripperExifTool();
        fakeExif.Offsets[heicPath] = null;
        var fakeImageConverter = new FakeStripperImageConverter();
        var stripper = new MotionPhotoStripper(fakeExif, fakeImageConverter);

        var options = new StripOptions
        {
            InputDirectory = tempDir.Root,
            OutputDirectory = null, // 就地覆盖模式
            ConvertToHeic = true,
            HeicQuality = 90
        };

        var report = await stripper.StripAsync(options, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Total);
        Assert.Equal(0, report.ConvertedCount);
        Assert.Equal(0, report.Skipped);
        Assert.Equal(4000, report.SavedBytes);

        // 验证 live2.heic 依然存在，而伴随的 live2.mov 已被安全删除
        Assert.True(File.Exists(heicPath), "静态 HEIC 照片必须完好留存");
        Assert.False(File.Exists(movPath), "冗余的伴随 MOV 视频文件应被删除释放空间");
    }

    /// <summary>
    /// 模拟测试用 ExifTool 桩
    /// </summary>
    private sealed class FakeStripperExifTool : IExifTool
    {
        public Dictionary<string, long?> Offsets { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task WriteMotionPhotoTagsAsync(string imagePath, long videoOffset, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RemoveMotionPhotoTagsAsync(string imagePath, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task CopyAllTagsAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task CopyCoverTagsAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<long?> TryReadMicroVideoOffsetAsync(string imagePath, CancellationToken cancellationToken = default)
        {
            if (Offsets.TryGetValue(imagePath, out var offset))
            {
                return Task.FromResult(offset);
            }
            return Task.FromResult<long?>(null);
        }

        public Task<string?> TryReadContentIdentifierAsync(string filePath, ContentIdentifierKind kind, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task WriteAppleContentIdentifierAsync(string photoPath, string contentIdentifier, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task WriteAppleVideoMetadataAsync(string videoPath, string? photoPath, string contentIdentifier, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<DateTime?> TryReadCreateDateAsync(string filePath, CancellationToken cancellationToken = default) =>
            Task.FromResult<DateTime?>(null);

        public Task<TimeSpan?> TryReadDurationAsync(string filePath, CancellationToken cancellationToken = default) =>
            Task.FromResult<TimeSpan?>(null);

        public Task<bool> IsMirroredVideoAsync(string videoPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// 模拟测试用图片转换器桩
    /// </summary>
    private sealed class FakeStripperImageConverter : IImageConverter
    {
        public Task ConvertToJpegAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
        {
            File.WriteAllBytes(destinationPath, new byte[1000]);
            return Task.CompletedTask;
        }

        public Task ConvertToHeicAsync(string sourcePath, string destinationPath, int quality = 90, CancellationToken cancellationToken = default)
        {
            File.WriteAllBytes(destinationPath, new byte[500]);
            return Task.CompletedTask;
        }
    }
}
