using System.Runtime.InteropServices;
using System.Text;
using ImageMagick;
using LivePhotoConvert.Core.Media;
using LivePhotoConvert.Core.Media.Thumbnails;

namespace LivePhotoConvert.Core.Platform;

/// <summary>
/// macOS 快速查看缩略图（QLThumbnailGenerator）：访达已经生成过的缓存可以直接用，不必再解码原图。
/// 只接受照片扩展名，避免把文本预览或文件图标当成照片。
/// </summary>
public sealed class QuickLookThumbnailSource : IThumbnailSource
{
    private const int MaxDimension = 16384;

    /// <summary>只请求真正的缩略图，不要文件类型图标。</summary>
    private const nuint ThumbnailRepresentation = 1u << 2;

    /// <summary>kCGImageAlphaPremultipliedLast | kCGBitmapByteOrder32Big，内存顺序是 RGBA。</summary>
    private const uint BitmapInfoRgba = 1 | (4u << 12);

    private static readonly IntPtr BlockIsa = OperatingSystem.IsMacOS() ? Native.BlockClass() : IntPtr.Zero;

    /// <summary>当前平台支持时创建实例，否则返回 <c>null</c>。</summary>
    public static IThumbnailSource? TryCreate() =>
        OperatingSystem.IsMacOS() ? new QuickLookThumbnailSource() : null;

    public Task<IMagickImage<byte>?> TryGetAsync(string path, int width, int height, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsMacOS() || !MediaFileTypes.IsPhoto(path) || width <= 0 || height <= 0 || !File.Exists(path))
        {
            return Task.FromResult<IMagickImage<byte>?>(null);
        }

        return Task.Run(() =>
        {
            if (!OperatingSystem.IsMacOS())
            {
                return null;
            }

            return Generate(path, width, height, cancellationToken);
        }, cancellationToken);
    }

    private static IMagickImage<byte>? Generate(string path, int width, int height, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return null;
        }

        Native.EnsureFrameworks();
        var pool = Native.PoolPush();
        var request = IntPtr.Zero;
        var block = IntPtr.Zero;
        var descriptor = IntPtr.Zero;
        var state = new State();
        var handle = GCHandle.Alloc(state);
        try
        {
            request = CreateRequest(path, width, height);
            if (request == IntPtr.Zero)
            {
                return null;
            }

            (block, descriptor) = CreateBlock(handle);
            var generator = Native.Send(Native.Class("QLThumbnailGenerator"), "sharedGenerator");
            Native.Generate(generator, request, block);
            state.Wait(cancellationToken, () => Native.Send(generator, "cancelRequest:", request));
            return state.Image;
        }
        catch (OperationCanceledException)
        {
            state.Image?.Dispose();
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
        finally
        {
            if (request != IntPtr.Zero)
            {
                Native.Release(request);
            }

            Native.PoolPop(pool);
            handle.Free();
            if (block != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(block);
            }

            if (descriptor != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(descriptor);
            }
        }
    }

    private static IntPtr CreateRequest(string path, int width, int height)
    {
        var utf8 = Encoding.UTF8.GetBytes(path + "\0");
        var bytes = Marshal.AllocHGlobal(utf8.Length);
        try
        {
            Marshal.Copy(utf8, 0, bytes, utf8.Length);
            var text = Native.Send(Native.Class("NSString"), "stringWithUTF8String:", bytes);
            var url = Native.Send(Native.Class("NSURL"), "fileURLWithPath:", text);
            var request = Native.Init(
                Native.Send(Native.Class("QLThumbnailGenerationRequest"), "alloc"),
                "initWithFileAtURL:size:scale:representationTypes:",
                url,
                new CGSize(width, height),
                1,
                ThumbnailRepresentation);
            if (request != IntPtr.Zero)
            {
                Native.SendFlag(request, "setIconMode:", 0);
            }

            return request;
        }
        finally
        {
            Marshal.FreeHGlobal(bytes);
        }
    }

    private static (IntPtr Block, IntPtr Descriptor) CreateBlock(GCHandle handle)
    {
        var descriptor = Marshal.AllocHGlobal(16);
        Marshal.WriteInt64(descriptor, 0, 0);
        Marshal.WriteInt64(descriptor, 8, Marshal.SizeOf<BlockLiteral>());
        var block = Marshal.AllocHGlobal(Marshal.SizeOf<BlockLiteral>());
        Marshal.StructureToPtr(new BlockLiteral
        {
            Isa = BlockIsa,
            Invoke = &OnThumbnail,
            Descriptor = descriptor,
            Context = GCHandle.ToIntPtr(handle),
        }, block, false);
        return (block, descriptor);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void OnThumbnail(IntPtr block, IntPtr representation, IntPtr error)
    {
        var literal = Marshal.PtrToStructure<BlockLiteral>(block);
        var state = (State)GCHandle.FromIntPtr(literal.Context).Target!;
        try
        {
            if (representation != IntPtr.Zero && error == IntPtr.Zero)
            {
                state.Image = ReadImage(Native.Send(representation, "CGImage"));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            state.Image = null;
        }
        finally
        {
            state.Finish();
        }
    }

    private static IMagickImage<byte>? ReadImage(IntPtr image)
    {
        if (image == IntPtr.Zero)
        {
            return null;
        }

        var width = (int)Native.CGImageGetWidth(image);
        var height = (int)Native.CGImageGetHeight(image);
        if (width <= 0 || height <= 0 || width > MaxDimension || height > MaxDimension)
        {
            return null;
        }

        var length = checked(width * height * 4);
        var pixels = new byte[length];
        var space = Native.CGColorSpaceCreateDeviceRGB();
        try
        {
            unsafe
            {
                fixed (byte* buffer = pixels)
                {
                    var context = Native.CGBitmapContextCreate((IntPtr)buffer, width, height, 8, width * 4, space, BitmapInfoRgba);
                    if (context == IntPtr.Zero)
                    {
                        return null;
                    }

                    // 这一组位图参数画出的第一行就是图像顶部，和 Magick 的行序一致
                    Native.CGContextDrawImage(context, new CGRect { Width = width, Height = height }, image);
                    Native.CGContextRelease(context);
                }
            }
        }
        finally
        {
            Native.CGColorSpaceRelease(space);
        }

        var result = new MagickImage(pixels, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.RGBA));
        result.Alpha(AlphaOption.Off);
        return result;
    }

    private sealed class State
    {
        private readonly ManualResetEventSlim _done = new();

        public IMagickImage<byte>? Image { get; set; }

        public void Finish() => _done.Set();

        public void Wait(CancellationToken cancellationToken, Action cancel)
        {
            try
            {
                _done.Wait(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                cancel();
                _done.Wait(TimeSpan.FromSeconds(2));
                throw;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct BlockLiteral
    {
        public IntPtr Isa;
        public int Flags;
        public int Reserved;
        public delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void> Invoke;
        public IntPtr Descriptor;
        public IntPtr Context;
    }

    private static class Native
    {
        private const string ObjC = "/usr/lib/libobjc.A.dylib";
        private static int _frameworks;

        public static void EnsureFrameworks()
        {
            if (Interlocked.Exchange(ref _frameworks, 1) == 0)
            {
                NativeLibrary.Load("/System/Library/Frameworks/Foundation.framework/Foundation");
                NativeLibrary.Load("/System/Library/Frameworks/QuickLookThumbnailing.framework/QuickLookThumbnailing");
                NativeLibrary.Load("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics");
            }
        }

        public static IntPtr BlockClass()
        {
            EnsureFrameworks();
            return dlsym((IntPtr)(-2), "_NSConcreteStackBlock");
        }

        public static IntPtr Class(string name) => objc_getClass(name);

        public static IntPtr Send(IntPtr receiver, string selector) =>
            Send0(receiver, sel_registerName(selector));

        public static IntPtr Send(IntPtr receiver, string selector, IntPtr arg) =>
            Send1(receiver, sel_registerName(selector), arg);

        public static void SendFlag(IntPtr receiver, string selector, nint arg) =>
            SendNInt(receiver, sel_registerName(selector), arg);

        public static IntPtr Init(IntPtr receiver, string selector, IntPtr url, CGSize size, double scale, nuint types) =>
            InitRaw(receiver, sel_registerName(selector), url, size, scale, types);

        public static void Generate(IntPtr generator, IntPtr request, IntPtr block) =>
            GenerateRaw(generator, sel_registerName("generateBestRepresentationForRequest:completionHandler:"), request, block);

        public static IntPtr PoolPush() => objc_autoreleasePoolPush();

        public static void PoolPop(IntPtr pool) => objc_autoreleasePoolPop(pool);

        public static void Release(IntPtr obj) => objc_release(obj);

        [DllImport(ObjC)]
        private static extern IntPtr objc_getClass(string name);

        [DllImport(ObjC)]
        private static extern IntPtr sel_registerName(string name);

        [DllImport(ObjC)]
        private static extern IntPtr objc_autoreleasePoolPush();

        [DllImport(ObjC)]
        private static extern void objc_autoreleasePoolPop(IntPtr pool);

        [DllImport(ObjC)]
        private static extern void objc_release(IntPtr obj);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr Send0(IntPtr receiver, IntPtr selector);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr Send1(IntPtr receiver, IntPtr selector, IntPtr arg);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr SendNInt(IntPtr receiver, IntPtr selector, nint arg);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr InitRaw(IntPtr receiver, IntPtr selector, IntPtr url, CGSize size, double scale, nuint types);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern void GenerateRaw(IntPtr receiver, IntPtr selector, IntPtr request, IntPtr block);

        [DllImport("/usr/lib/libSystem.dylib")]
        private static extern IntPtr dlsym(IntPtr handle, string symbol);

        [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
        public static extern nint CGImageGetWidth(IntPtr image);

        [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
        public static extern nint CGImageGetHeight(IntPtr image);

        [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
        public static extern IntPtr CGColorSpaceCreateDeviceRGB();

        [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
        public static extern void CGColorSpaceRelease(IntPtr space);

        [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
        public static extern IntPtr CGBitmapContextCreate(IntPtr data, nint width, nint height, nint bitsPerComponent, nint bytesPerRow, IntPtr space, uint bitmapInfo);

        [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
        public static extern void CGContextDrawImage(IntPtr context, CGRect rect, IntPtr image);

        [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
        public static extern void CGContextRelease(IntPtr context);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct CGSize(double width, double height)
    {
        public readonly double Width = width;
        public readonly double Height = height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGRect
    {
        public double X;
        public double Y;
        public double Width;
        public double Height;
    }
}
