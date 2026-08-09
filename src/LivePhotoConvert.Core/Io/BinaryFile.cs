using System.Buffers;

namespace LivePhotoConvert.Core.Io;

/// <summary>
/// 动态照片的字节级读写
/// </summary>
public static class BinaryFile
{
    /// <summary>
    /// 流式复制的缓冲区大小
    /// </summary>
    private const int BufferSize = 1024 * 1024;

    /// <summary>
    /// 把照片和视频按「照片在前、视频在后」拼接成一个文件
    /// </summary>
    /// <param name="photoPath">照片路径</param>
    /// <param name="videoPath">视频路径</param>
    /// <param name="outputPath">输出路径</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>照片长度与拼接后的总长度</returns>
    public static async Task<(long PhotoLength, long TotalLength)> ConcatAsync(string photoPath, string videoPath, string outputPath, CancellationToken cancellationToken = default)
    {
        await using var output = OpenWrite(outputPath);
        await using var photo = OpenRead(photoPath);
        await using var video = OpenRead(videoPath);
        // 由输入长度精确计算，不读取 output.Length，避免受输出流缓冲影响
        var photoLength = photo.Length;
        var totalLength = photoLength + video.Length;
        await photo.CopyToAsync(output, BufferSize, cancellationToken);
        await video.CopyToAsync(output, BufferSize, cancellationToken);
        return (photoLength, totalLength);
    }

    /// <summary>
    /// 把源文件的一段字节复制到新文件
    /// </summary>
    /// <remarks>分块流式复制，不会因为源文件过大而一次性占用等量内存。</remarks>
    /// <param name="sourcePath">源文件路径</param>
    /// <param name="destinationPath">目标文件路径</param>
    /// <param name="offset">起始偏移量</param>
    /// <param name="length">复制长度</param>
    /// <param name="cancellationToken">取消令牌</param>
    public static async Task CopySegmentAsync(string sourcePath, string destinationPath, long offset, long length, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        await using var source = OpenRead(sourcePath);
        if (offset + length > source.Length)
        {
            throw new InvalidDataException($"请求的数据段超出文件范围：偏移 {offset} + 长度 {length} 大于文件长度 {source.Length}。");
        }
        await using var destination = OpenWrite(destinationPath);
        source.Seek(offset, SeekOrigin.Begin);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            var remaining = length;
            while (remaining > 0)
            {
                var wanted = (int)Math.Min(buffer.Length, remaining);
                var read = await source.ReadAsync(buffer.AsMemory(0, wanted), cancellationToken);
                if (read == 0)
                {
                    throw new EndOfStreamException($"读取 {sourcePath} 时提前到达文件末尾，仍有 {remaining} 字节未读取。");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// 以顺序读方式打开文件
    /// </summary>
    /// <param name="path">文件路径</param>
    /// <returns>文件流</returns>
    private static FileStream OpenRead(string path) => new(path, new FileStreamOptions
    {
        Mode = FileMode.Open,
        Access = FileAccess.Read,
        Share = FileShare.Read,
        BufferSize = BufferSize,
        Options = FileOptions.Asynchronous | FileOptions.SequentialScan
    });

    /// <summary>
    /// 以顺序写方式创建文件
    /// </summary>
    /// <param name="path">文件路径</param>
    /// <returns>文件流</returns>
    private static FileStream OpenWrite(string path) => new(path, new FileStreamOptions
    {
        Mode = FileMode.Create,
        Access = FileAccess.Write,
        Share = FileShare.None,
        BufferSize = BufferSize,
        Options = FileOptions.Asynchronous | FileOptions.SequentialScan
    });
}
