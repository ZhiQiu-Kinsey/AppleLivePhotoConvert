using System.Buffers;

namespace LivePhotoConvert.Core.Io;

/// <summary>
/// 动态照片的流式拼接与切片；不把整个文件读入内存。
/// </summary>
public static class BinaryFile
{
    private const int BufferSize = 1024 * 1024;

    /// <summary>
    /// 按「照片在前、视频在后」拼接为新文件。目标文件必须不存在。
    /// </summary>
    /// <returns>照片部分长度与总长度</returns>
    public static async Task<(long PhotoLength, long TotalLength)> ConcatAsync(string photoPath, string videoPath, string outputPath, CancellationToken cancellationToken = default)
    {
        await using var photo = OpenRead(photoPath);
        await using var video = OpenRead(videoPath);
        var photoLength = photo.Length;
        var totalLength = photoLength + video.Length;

        await using var output = CreateNew(outputPath, totalLength);
        await photo.CopyToAsync(output, BufferSize, cancellationToken);
        await video.CopyToAsync(output, BufferSize, cancellationToken);
        return (photoLength, totalLength);
    }

    /// <summary>
    /// 把源文件中的一段字节复制为新文件。目标文件必须不存在。
    /// </summary>
    /// <exception cref="InvalidDataException">请求的范围超出源文件</exception>
    public static async Task CopySegmentAsync(string sourcePath, string destinationPath, long offset, long length, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        await using var source = OpenRead(sourcePath);
        if (offset + length > source.Length)
        {
            throw new InvalidDataException($"请求的数据段超出文件范围：偏移 {offset} + 长度 {length} 大于文件长度 {source.Length}。");
        }

        await using var destination = CreateNew(destinationPath, length);
        source.Position = offset;
        var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Clamp(length, 4096, BufferSize));
        try
        {
            var remaining = length;
            while (remaining > 0)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
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

    private static FileStream OpenRead(string path) => new(path, new FileStreamOptions
    {
        Mode = FileMode.Open,
        Access = FileAccess.Read,
        Share = FileShare.Read,
        BufferSize = BufferSize,
        Options = FileOptions.Asynchronous | FileOptions.SequentialScan
    });

    /// <summary>
    /// 预分配目标长度以减少文件系统碎片；不支持预分配的文件系统会忽略该选项。
    /// </summary>
    private static FileStream CreateNew(string path, long length) => new(path, new FileStreamOptions
    {
        Mode = FileMode.CreateNew,
        Access = FileAccess.Write,
        Share = FileShare.None,
        BufferSize = BufferSize,
        Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        PreallocationSize = length
    });
}
