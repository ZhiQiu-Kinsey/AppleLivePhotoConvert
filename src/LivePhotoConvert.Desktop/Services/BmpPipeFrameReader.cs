using System.Buffers.Binary;
using Avalonia.Media.Imaging;
namespace LivePhotoConvert.Desktop.Services;

/// <summary>
/// 从 FFmpeg image2pipe 连续读取无压缩 BMP 帧。
/// BMP 文件头自带单帧长度，避免依赖有损 MJPEG 的边界标记。
/// </summary>
internal static class BmpPipeFrameReader
{
    private const int HeaderLength = 14;
    private const int MaxFrameBytes = 64 * 1024 * 1024;

    public static async ValueTask<Bitmap?> ReadNextAsync(Stream stream, CancellationToken token)
    {
        byte[]? frameData = await ReadNextBytesAsync(stream, token);
        if (frameData is null) return null;

        using var frameStream = new MemoryStream(frameData, writable: false);
        return new Bitmap(frameStream);
    }

    internal static async ValueTask<byte[]?> ReadNextBytesAsync(Stream stream, CancellationToken token)
    {
        byte[] header = new byte[HeaderLength];
        int headerBytes = await ReadAvailableAsync(stream, header, token);
        if (headerBytes == 0) return null;
        if (headerBytes != HeaderLength || header[0] != (byte)'B' || header[1] != (byte)'M')
        {
            throw new InvalidDataException("FFmpeg 返回了无效的 BMP 帧头。");
        }

        int frameLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(2, 4));
        if (frameLength < HeaderLength || frameLength > MaxFrameBytes)
        {
            throw new InvalidDataException("FFmpeg 返回的 BMP 帧长度无效。");
        }

        byte[] frameData = GC.AllocateUninitializedArray<byte>(frameLength);
        header.CopyTo(frameData, 0);
        await stream.ReadExactlyAsync(frameData.AsMemory(HeaderLength), token);
        return frameData;
    }

    private static async ValueTask<int> ReadAvailableAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[total..], token);
            if (read == 0) break;
            total += read;
        }
        return total;
    }
}
