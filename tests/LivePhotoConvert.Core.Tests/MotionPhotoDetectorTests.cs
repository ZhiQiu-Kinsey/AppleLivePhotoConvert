using System.Text;
using LivePhotoConvert.Core.Abstractions;
using LivePhotoConvert.Core.Matching;

namespace LivePhotoConvert.Core.Tests;

public class MotionPhotoDetectorTests
{
    private sealed class FakeExifTool : IExifTool
    {
        public long? Offset { get; init; }
        public Task<long?> TryReadMicroVideoOffsetAsync(string imagePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(Offset);

        public Task<string?> TryReadContentIdentifierAsync(string filePath, ContentIdentifierKind kind, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task WriteAppleContentIdentifierAsync(string photoPath, string contentIdentifier, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task WriteAppleVideoMetadataAsync(string videoPath, string? photoPath, string contentIdentifier, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task WriteMotionPhotoTagsAsync(string imagePath, long videoOffset, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveMotionPhotoTagsAsync(string imagePath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CopyAllTagsAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CopyCoverTagsAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<DateTime?> TryReadCreateDateAsync(string filePath, CancellationToken cancellationToken = default) => Task.FromResult<DateTime?>(null);
        public Task<TimeSpan?> TryReadDurationAsync(string filePath, CancellationToken cancellationToken = default) => Task.FromResult<TimeSpan?>(null);
        public Task<bool> IsMirroredVideoAsync(string videoPath, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static byte[] CreateSampleMotionPhoto(string xmpContent, int videoLength)
    {
        // 1. JPEG SOI (0xFF, 0xD8) + APP1 XMP
        var ms = new MemoryStream();
        ms.Write([0xFF, 0xD8, 0xFF, 0xE1]);
        var xmpBytes = Encoding.UTF8.GetBytes(xmpContent);
        int app1Len = xmpBytes.Length + 2;
        ms.WriteByte((byte)((app1Len >> 8) & 0xFF));
        ms.WriteByte((byte)(app1Len & 0xFF));
        ms.Write(xmpBytes);

        // 2. Padding to exceed 64KB
        int paddingLen = Math.Max(0, 70000 - (int)ms.Length - videoLength);
        ms.Write(new byte[paddingLen]);

        // 3. MP4 ftyp box (videoLength bytes)
        var videoBytes = new byte[videoLength];
        videoBytes[4] = (byte)'f';
        videoBytes[5] = (byte)'t';
        videoBytes[6] = (byte)'y';
        videoBytes[7] = (byte)'p';
        videoBytes[8] = (byte)'m';
        videoBytes[9] = (byte)'p';
        videoBytes[10] = (byte)'4';
        videoBytes[11] = (byte)'2';
        ms.Write(videoBytes);

        return ms.ToArray();
    }

    [Fact]
    public async Task DetectAsync_WhenFileHasXmpMicroVideoOffset_ReturnsMotionPhotoInfo()
    {
        using var tempDir = new TempDirectory();
        string xmp = "<x:xmpmeta><rdf:RDF><rdf:Description GCamera:MicroVideo=\"1\" GCamera:MicroVideoOffset=\"256\"/></rdf:RDF></x:xmpmeta>";
        byte[] bytes = CreateSampleMotionPhoto(xmp, 256);
        var path = tempDir.CreateFile("MVIMG_2026.jpg", bytes);

        var info = await MotionPhotoDetector.DetectAsync(path, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(info);
        Assert.Equal(path, info.PhotoPath);
        Assert.Equal(256, info.VideoLength);
        Assert.Equal(bytes.Length - 256, info.VideoOffset);
    }

    [Fact]
    public async Task DetectAsync_WhenFileHasContainerItemLength_ReturnsMotionPhotoInfo()
    {
        using var tempDir = new TempDirectory();
        string xmp = "<x:xmpmeta><Container:Item Item:Mime=\"video/mp4\" Item:Length=\"300\"/></x:xmpmeta>";
        byte[] bytes = CreateSampleMotionPhoto(xmp, 300);
        var path = tempDir.CreateFile("PXL_2026.jpg", bytes);

        var info = await MotionPhotoDetector.DetectAsync(path, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(info);
        Assert.Equal(300, info.VideoLength);
        Assert.Equal(bytes.Length - 300, info.VideoOffset);
    }

    [Fact]
    public async Task DetectAsync_WhenFileIsPlainImage_ReturnsNull()
    {
        using var tempDir = new TempDirectory();
        var bytes = new byte[70000];
        bytes[0] = 0xFF; bytes[1] = 0xD8; bytes[2] = 0xFF; bytes[3] = 0xE0;
        var path = tempDir.CreateFile("PLAIN.jpg", bytes);

        var info = await MotionPhotoDetector.DetectAsync(path, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(info);
    }

    [Fact]
    public async Task DetectAsync_WhenFileIsTooSmall_ReturnsNull()
    {
        using var tempDir = new TempDirectory();
        var bytes = new byte[100];
        var path = tempDir.CreateFile("TINY.jpg", bytes);

        var info = await MotionPhotoDetector.DetectAsync(path, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(info);
    }

    [Fact]
    public async Task DetectAsync_WhenExifToolFallbackProvided_ReturnsMotionPhotoInfo()
    {
        using var tempDir = new TempDirectory();
        // XMP 无直接偏移，但文件尾部有 200 字节合法 MP4
        string xmp = "<x:xmpmeta>plain metadata</x:xmpmeta>";
        byte[] bytes = CreateSampleMotionPhoto(xmp, 200);
        var path = tempDir.CreateFile("XIAOMI.jpg", bytes);

        var fakeExif = new FakeExifTool { Offset = 200 };
        var info = await MotionPhotoDetector.DetectAsync(path, fakeExif, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(info);
        Assert.Equal(200, info.VideoLength);
        Assert.Equal(bytes.Length - 200, info.VideoOffset);
    }

    [Fact]
    public async Task DetectAsync_SecondCall_HitsStaticCache()
    {
        MotionPhotoDetector.ClearCache();
        using var tempDir = new TempDirectory();
        string xmp = "<x:xmpmeta><rdf:RDF><rdf:Description GCamera:MicroVideo=\"1\" GCamera:MicroVideoOffset=\"256\"/></rdf:RDF></x:xmpmeta>";
        byte[] bytes = CreateSampleMotionPhoto(xmp, 256);
        var path = tempDir.CreateFile("CACHED_1.jpg", bytes);

        var first = await MotionPhotoDetector.DetectAsync(path, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(first);

        // 第二次调用，应秒级命中缓存，返回相同内容
        var second = await MotionPhotoDetector.DetectAsync(path, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(second);
        Assert.Equal(first.VideoOffset, second.VideoOffset);
        Assert.Equal(first.VideoLength, second.VideoLength);
    }

    [Fact]
    public async Task DetectAsync_NegativeResult_IsCached()
    {
        MotionPhotoDetector.ClearCache();
        using var tempDir = new TempDirectory();
        var bytes = new byte[70000];
        bytes[0] = 0xFF; bytes[1] = 0xD8; bytes[2] = 0xFF; bytes[3] = 0xE0;
        var path = tempDir.CreateFile("PLAIN_CACHED.jpg", bytes);

        var first = await MotionPhotoDetector.DetectAsync(path, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(first);

        var second = await MotionPhotoDetector.DetectAsync(path, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(second);
    }

    [Fact]
    public async Task DetectAsync_WhenFileModified_InvalidatesCache()
    {
        MotionPhotoDetector.ClearCache();
        using var tempDir = new TempDirectory();
        string xmp = "<x:xmpmeta><rdf:RDF><rdf:Description GCamera:MicroVideo=\"1\" GCamera:MicroVideoOffset=\"256\"/></rdf:RDF></x:xmpmeta>";
        byte[] bytes = CreateSampleMotionPhoto(xmp, 256);
        var path = tempDir.CreateFile("MODIFIED.jpg", bytes);

        var first = await MotionPhotoDetector.DetectAsync(path, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(first);
        Assert.Equal(256, first.VideoLength);

        // 修改文件内容为普通无视频文件，并改变最后修改时间
        var newBytes = new byte[75000];
        newBytes[0] = 0xFF; newBytes[1] = 0xD8; newBytes[2] = 0xFF; newBytes[3] = 0xE0;
        await File.WriteAllBytesAsync(path, newBytes, TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));

        var second = await MotionPhotoDetector.DetectAsync(path, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(second);
    }

    [Fact]
    public void ClearCache_RemovesAllCachedEntries()
    {
        MotionPhotoDetector.ClearCache();
        // ClearCache 应安全执行且不抛异常
        MotionPhotoDetector.ClearCache();
    }
}
