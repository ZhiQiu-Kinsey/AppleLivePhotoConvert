using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ImageMagick;
using LivePhotoConvert.Core.Media.Thumbnails;

namespace LivePhotoConvert.Core.Platform;

/// <summary>
/// Windows Shell 缩略图（IShellItemImageFactory）：命中系统缩略图缓存时无需解码原图。
/// 所有 COM 调用在一条专用 MTA 线程上串行执行：线程池线程的套间由运行时决定，
/// 在其上逐次 CoInitializeEx(STA) 会失败或与他人的初始化冲突，逐次初始化/反初始化也有开销。
/// </summary>
public sealed partial class WindowsShellThumbnailSource : IThumbnailSource, IDisposable
{
    private const int SiigbfBiggerSizeOk = 0x01;
    private const int SiigbfThumbnailOnly = 0x08;
    private const uint CoinitMultithreaded = 0x0;
    private const uint DibRgbColors = 0;
    private const uint BiRgb = 0;
    private const int MaxDimension = 16384;

    /// <summary>BITMAPINFOHEADER 之后预留颜色表/位域掩码空间：第一次 GetDIBits 可能在头后写入掩码。</summary>
    private const int ColorTableBytes = 256 * 4;

    private static readonly Guid ShellItemImageFactoryId = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    private readonly BlockingCollection<WorkItem> _queue = [];

    [SupportedOSPlatform("windows")]
    public WindowsShellThumbnailSource()
    {
        var thread = new Thread(Run) { IsBackground = true, Name = "LivePhotoConvert Shell Thumbnail" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    /// <summary>当前平台支持时创建实例，否则返回 <c>null</c>。</summary>
    public static IThumbnailSource? TryCreate() =>
        OperatingSystem.IsWindows() ? new WindowsShellThumbnailSource() : null;

    public Task<IMagickImage<byte>?> TryGetAsync(string path, int width, int height, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path) || width <= 0 || height <= 0)
        {
            return Task.FromResult<IMagickImage<byte>?>(null);
        }

        var item = new WorkItem(path, Math.Min(width, MaxDimension), Math.Min(height, MaxDimension));
        item.Registration = cancellationToken.Register(
            static state => ((WorkItem)state!).Completion.TrySetCanceled(),
            item);
        try
        {
            _queue.Add(item, CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // 已释放
            item.Registration.Dispose();
            return Task.FromResult<IMagickImage<byte>?>(null);
        }

        return item.Completion.Task;
    }

    public void Dispose() => _queue.CompleteAdding();

    private void Run()
    {
        // 线程已设为 MTA，这里显式初始化以获得可配对的 CoUninitialize；S_FALSE 同样需要配对
        var initialized = CoInitializeEx(0, CoinitMultithreaded) >= 0;
        try
        {
            foreach (var item in _queue.GetConsumingEnumerable())
            {
                if (item.Completion.Task.IsCompleted)
                {
                    continue;
                }

                IMagickImage<byte>? image;
                try
                {
                    image = Extract(item.Path, item.Width, item.Height);
                }
                catch (Exception)
                {
                    // 任何单个文件的失败都不能终止这条共享线程
                    image = null;
                }

                if (!item.Completion.TrySetResult(image))
                {
                    image?.Dispose();
                }

                item.Registration.Dispose();
            }
        }
        finally
        {
            if (initialized)
            {
                CoUninitialize();
            }
        }
    }

    private static unsafe IMagickImage<byte>? Extract(string path, int width, int height)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var iid = ShellItemImageFactoryId;
        nint factory = 0;
        if (SHCreateItemFromParsingName(path, 0, &iid, &factory) < 0 || factory == 0)
        {
            return null;
        }

        nint bitmap = 0;
        try
        {
            // IShellItemImageFactory 虚表：0 QueryInterface，1 AddRef，2 Release，3 GetImage
            var vtable = *(nint**)factory;
            var getImage = (delegate* unmanaged[Stdcall]<nint, NativeSize, int, nint*, int>)vtable[3];

            // THUMBNAILONLY：没有缩略图处理程序（如未装 HEIF 扩展）时失败，而不是返回文件图标；
            // BIGGERSIZEOK：允许返回系统缓存里更大的图，由调用方高质量缩小
            var flags = SiigbfThumbnailOnly | SiigbfBiggerSizeOk;
            if (getImage(factory, new NativeSize(width, height), flags, &bitmap) < 0 || bitmap == 0)
            {
                return null;
            }

            return ReadBitmap(bitmap);
        }
        finally
        {
            if (bitmap != 0)
            {
                DeleteObject(bitmap);
            }

            var release = (delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)factory)[2];
            release(factory);
        }
    }

    private static unsafe IMagickImage<byte>? ReadBitmap(nint bitmap)
    {
        var dc = GetDC(0);
        if (dc == 0)
        {
            return null;
        }

        byte[]? pixels = null;
        try
        {
            var infoSize = sizeof(BitmapInfoHeader) + ColorTableBytes;
            var info = stackalloc byte[infoSize];
            new Span<byte>(info, infoSize).Clear();
            var header = (BitmapInfoHeader*)info;
            header->Size = (uint)sizeof(BitmapInfoHeader);
            if (GetDIBits(dc, bitmap, 0, 0, null, info, DibRgbColors) == 0)
            {
                return null;
            }

            var width = header->Width;
            var height = Math.Abs(header->Height);
            if (width <= 0 || height <= 0 || width > MaxDimension || height > MaxDimension)
            {
                return null;
            }

            // 第一次调用填回的是位图自身格式（可能是 24 位）；不改成 32 位就按 BGRA 解析会整体错位。
            // 负高度要求自上而下的行序，与 Magick 的像素顺序一致
            header->BitCount = 32;
            header->Compression = BiRgb;
            header->Height = -height;
            header->Planes = 1;
            header->SizeImage = 0;
            header->ClrUsed = 0;
            header->ClrImportant = 0;

            var length = width * height * 4;
            pixels = ArrayPool<byte>.Shared.Rent(length);
            fixed (byte* bits = pixels)
            {
                if (GetDIBits(dc, bitmap, 0, (uint)height, bits, info, DibRgbColors) != height)
                {
                    return null;
                }
            }

            var image = new MagickImage(
                pixels.AsSpan(0, length),
                new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.BGRA));
            // Shell 位图的 alpha 常为 0 或预乘值，照片缩略图一律视为不透明
            image.Alpha(AlphaOption.Off);
            return image;
        }
        finally
        {
            if (pixels is not null)
            {
                ArrayPool<byte>.Shared.Return(pixels);
            }

            ReleaseDC(0, dc);
        }
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int SHCreateItemFromParsingName(string path, nint bindContext, Guid* riid, nint* result);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint handle);

    [LibraryImport("gdi32.dll")]
    private static unsafe partial int GetDIBits(nint dc, nint bitmap, uint start, uint lines, void* bits, void* info, uint usage);

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint window);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint window, nint dc);

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint flags);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeSize(int width, int height)
    {
        public readonly int Width = width;
        public readonly int Height = height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    private sealed class WorkItem(string path, int width, int height)
    {
        public string Path { get; } = path;

        public int Width { get; } = width;

        public int Height { get; } = height;

        public TaskCompletionSource<IMagickImage<byte>?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationTokenRegistration Registration { get; set; }
    }
}
