using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace LivePhotoConvert.Desktop.Features.Playback;

/// <summary>
/// 双缓冲显示位图：新帧写入后台位图后再与前台交换，界面正在引用的前台位图既不被改写也不被释放。
/// </summary>
internal sealed class SurfacePair(PixelSize size) : IDisposable
{
    private WriteableBitmap? _front;
    private WriteableBitmap? _back;

    public PixelSize Size => size;

    /// <summary>当前应显示的位图；尚未呈现过任何帧时为 null。</summary>
    public WriteableBitmap? Front => _front;

    /// <summary>把一帧 BGRA 像素（无行填充）呈现为新的前台位图。</summary>
    public WriteableBitmap Present(ReadOnlySpan<byte> pixels)
    {
        var target = _back ??= Create(size);
        using (var buffer = target.Lock())
        {
            var rowBytes = size.Width * 4;
            if (buffer.RowBytes == rowBytes)
            {
                pixels[..(rowBytes * size.Height)].CopyTo(AsSpan(buffer.Address, rowBytes * size.Height));
            }
            else
            {
                for (var y = 0; y < size.Height; y++)
                {
                    pixels.Slice(y * rowBytes, rowBytes).CopyTo(AsSpan(buffer.Address + (y * buffer.RowBytes), rowBytes));
                }
            }
        }

        (_front, _back) = (target, _front);
        return target;
    }

    public void Dispose()
    {
        _front?.Dispose();
        _back?.Dispose();
        _front = null;
        _back = null;
    }

    private static unsafe Span<byte> AsSpan(nint address, int length) => new((void*)address, length);

    private static WriteableBitmap Create(PixelSize size) =>
        // FFmpeg 输出的 alpha 恒为 255，标为不透明可省去合成时的混合
        new(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
}
