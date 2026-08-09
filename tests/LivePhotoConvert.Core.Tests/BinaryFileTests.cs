using LivePhotoConvert.Core.Io;

namespace LivePhotoConvert.Core.Tests;

/// <summary>
/// 动态照片字节级读写的测试
/// </summary>
public class BinaryFileTests
{
    /// <summary>
    /// 测试拼接后，生成的文件内容是照片在前，视频在后。
    /// </summary>
    [Fact]
    public async Task ConcatenatedFile_Should_Have_Photo_First_Then_Video()
    {
        var token = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var photo = temp.CreateFile("photo.jpg", [1, 2, 3, 4, 5]);
        var video = temp.CreateFile("video.mp4", [10, 20, 30]);
        var output = temp.Combine("merged.jpg");

        var (photoLength, totalLength) = await BinaryFile.ConcatAsync(photo, video, output, token);

        Assert.Equal(5, photoLength);
        Assert.Equal(8, totalLength);
        Assert.Equal([1, 2, 3, 4, 5, 10, 20, 30], await File.ReadAllBytesAsync(output, token));
    }

    /// <summary>
    /// 测试文件的拼接与拆分操作可以完美还原原始的照片和视频文件。
    /// </summary>
    [Fact]
    public async Task Concat_And_Split_Should_Restore_Original_Photo_And_Video()
    {
        // 这条验证的是合成写入的偏移量与拆分读取的偏移量遵循同一套约定，
        // 两侧一旦不一致，用户拿到的就是无法播放的动态照片
        var token = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var photoBytes = CreatePattern(3000, seed: 7);
        var videoBytes = CreatePattern(1500, seed: 99);

        var photo = temp.CreateFile("photo.jpg", photoBytes);
        var video = temp.CreateFile("video.mp4", videoBytes);
        var merged = temp.Combine("merged.jpg");

        var (photoLength, totalLength) = await BinaryFile.ConcatAsync(photo, video, merged, token);

        // 合成时写入元数据的偏移量就是视频长度
        var videoOffset = totalLength - photoLength;
        Assert.Equal(videoBytes.Length, videoOffset);

        // 拆分时按这个偏移量反推两段数据的位置
        var restoredPhoto = temp.Combine("restored.jpg");
        var restoredVideo = temp.Combine("restored.mp4");
        var mergedLength = new FileInfo(merged).Length;
        await BinaryFile.CopySegmentAsync(merged, restoredPhoto, 0, mergedLength - videoOffset, token);
        await BinaryFile.CopySegmentAsync(merged, restoredVideo, mergedLength - videoOffset, videoOffset, token);

        Assert.Equal(photoBytes, await File.ReadAllBytesAsync(restoredPhoto, token));
        Assert.Equal(videoBytes, await File.ReadAllBytesAsync(restoredVideo, token));
    }

    /// <summary>
    /// 测试大于内部缓冲区的文件可以被完整复制而没有数据丢失。
    /// </summary>
    [Fact]
    public async Task Large_File_Larger_Than_Buffer_Should_Be_Copied_Completely()
    {
        // 缓冲区是 1MB，这里用 2.5MB 触发多轮读写循环
        var token = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var content = CreatePattern(2_500_000, seed: 3);
        var source = temp.CreateFile("large.bin", content);
        var destination = temp.Combine("copy.bin");

        await BinaryFile.CopySegmentAsync(source, destination, 0, content.Length, token);

        Assert.Equal(content, await File.ReadAllBytesAsync(destination, token));
    }

    /// <summary>
    /// 测试从文件中复制一个片段只会复制指定范围的字节。
    /// </summary>
    [Fact]
    public async Task Copying_A_Segment_Should_Only_Copy_The_Specified_Range()
    {
        var token = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var source = temp.CreateFile("data.bin", [0, 1, 2, 3, 4, 5, 6, 7, 8, 9]);
        var destination = temp.Combine("part.bin");

        await BinaryFile.CopySegmentAsync(source, destination, 3, 4, token);

        Assert.Equal([3, 4, 5, 6], await File.ReadAllBytesAsync(destination, token));
    }

    /// <summary>
    /// 测试尝试复制超出文件边界的片段会抛出 InvalidDataException。
    /// </summary>
    [Fact]
    public async Task Requesting_A_Segment_Beyond_File_Bounds_Should_Throw()
    {
        var token = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var source = temp.CreateFile("data.bin", [0, 1, 2]);
        var destination = temp.Combine("part.bin");

        await Assert.ThrowsAsync<InvalidDataException>(
            () => BinaryFile.CopySegmentAsync(source, destination, 1, 5, token));
    }

    /// <summary>
    /// 测试在复制片段时使用负偏移量会抛出异常。
    /// </summary>
    [Fact]
    public async Task Negative_Offset_Should_Throw()
    {
        var token = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var source = temp.CreateFile("data.bin", [0, 1, 2]);
        var destination = temp.Combine("part.bin");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => BinaryFile.CopySegmentAsync(source, destination, -1, 1, token));
    }

    /// <summary>
    /// 生成可重复的伪随机字节，便于比对
    /// </summary>
    /// <param name="length">长度</param>
    /// <param name="seed">随机种子</param>
    /// <returns>字节数组</returns>
    private static byte[] CreatePattern(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }
}
