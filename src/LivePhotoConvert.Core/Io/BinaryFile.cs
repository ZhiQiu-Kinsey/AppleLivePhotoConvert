using System.Buffers;

namespace LivePhotoConvert.Core.Io;

/// <summary>
/// 动态照片的字节级读写与高性能流式拼接/拆分服务
/// </summary>
public static class BinaryFile
{
    /// <summary>
    /// 流式顺序复制时采用的默认大块缓冲区大小 (1MB)
    /// </summary>
    private const int BufferSize = 1024 * 1024;

    /// <summary>
    /// 把照片和视频按「照片在前、视频在后」的二进制结构高速拼接成一个动态照片文件
    /// </summary>
    /// <remarks>
    /// 1. 采用计算输入流长度预分配输出磁盘空间（SetLength / PreallocationSize），有效降低磁盘碎片并提升写入吞吐；<br/>
    /// 2. 照片长度与总长度由输入流精确获得，返回值用于向 XMP 写入精确的 MicroVideoOffset。
    /// </remarks>
    /// <param name="photoPath">封面照片文件路径</param>
    /// <param name="videoPath">内嵌微视频文件路径</param>
    /// <param name="outputPath">拼接输出的目标文件路径</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>包含 (PhotoLength: 照片字节长度, TotalLength: 拼接后总字节长度) 的元组</returns>
    public static async Task<(long PhotoLength, long TotalLength)> ConcatAsync(string photoPath, string videoPath, string outputPath, CancellationToken cancellationToken = default)
    {
        await using var photo = OpenRead(photoPath);
        await using var video = OpenRead(videoPath);

        // 由输入长度精确计算，不读取 output.Length，避免受输出流缓冲影响
        var photoLength = photo.Length;
        var totalLength = photoLength + video.Length;

        await using var output = OpenWrite(outputPath, totalLength);

        await photo.CopyToAsync(output, BufferSize, cancellationToken);
        await video.CopyToAsync(output, BufferSize, cancellationToken);

        return (photoLength, totalLength);
    }

    /// <summary>
    /// 将源文件中指定偏移和长度的二进制字节段分块复制到目标新文件（用于动态照片的高性能无损解包）
    /// </summary>
    /// <remarks>
    /// 1. 基于 <see cref="ArrayPool{T}.Shared"/> 租借动态缓冲区，在分块复制过程中不产生任何托管堆垃圾；<br/>
    /// 2. 预先设置目标文件长度，避免 NTFS 反复扩展文件；<br/>
    /// 3. 分块复制不会受源文件或视频体积大小影响而占用等量内存。
    /// </remarks>
    /// <param name="sourcePath">源文件路径</param>
    /// <param name="destinationPath">目标输出文件路径</param>
    /// <param name="offset">数据段起始偏移量（字节）</param>
    /// <param name="length">需要复制的数据段长度（字节）</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <exception cref="InvalidDataException">请求的偏移与长度超出源文件边界</exception>
    /// <exception cref="EndOfStreamException">读取过程中源文件流意外提前终止</exception>
    public static async Task CopySegmentAsync(string sourcePath, string destinationPath, long offset, long length, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        await using var source = OpenRead(sourcePath);
        if (offset + length > source.Length)
        {
            throw new InvalidDataException($"请求的数据段超出文件范围：偏移 {offset} + 长度 {length} 大于文件长度 {source.Length}。");
        }

        await using var destination = OpenWrite(destinationPath, length);
        source.Seek(offset, SeekOrigin.Begin);

        // 动态根据数据段大小租借缓冲区，小数据段租借小块，大数据段上限 1MB
        var rentSize = (int)Math.Min(BufferSize, Math.Max(4096, length));
        var buffer = ArrayPool<byte>.Shared.Rent(rentSize);
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
    /// 以异步顺序只读模式打开文件流（针对大文件顺序读取进行 OS 缓存优化）
    /// </summary>
    /// <param name="path">文件路径</param>
    /// <returns>配置了 SequentialScan 的只读 FileStream</returns>
    private static FileStream OpenRead(string path) => new(path, new FileStreamOptions
    {
        Mode = FileMode.Open,
        Access = FileAccess.Read,
        Share = FileShare.Read,
        BufferSize = BufferSize,
        Options = FileOptions.Asynchronous | FileOptions.SequentialScan
    });

    /// <summary>
    /// 以异步顺序写入模式创建文件流（支持预分配物理空间以避免磁盘碎片）
    /// </summary>
    /// <param name="path">目标文件路径</param>
    /// <param name="preallocationSize">预分配的字节大小（大于 0 时生效）</param>
    /// <returns>配置了 SequentialScan 的写入 FileStream</returns>
    private static FileStream OpenWrite(string path, long preallocationSize = 0)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = BufferSize,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            PreallocationSize = preallocationSize > 0 ? preallocationSize : 0
        };

        var stream = new FileStream(path, options);
        if (preallocationSize > 0)
        {
            try
            {
                stream.SetLength(preallocationSize);
                stream.Position = 0;
            }
            catch
            {
                // 部分虚拟文件系统或特殊网络挂载不支持预先 SetLength，忽略
            }
        }

        return stream;
    }
}


