using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ImageMagick;
using LivePhotoConvert.Desktop.Controls;

namespace LivePhotoConvert.Desktop.Features.Dialogs;

/// <summary>
/// 对比两侧图片的解码：显示位图按控件物理像素解码；放大镜与放大视图的细节从原图裁切，
/// 每侧只缓存指针附近有限的原图块，不让整张原图按原始分辨率常驻内存。
/// 两侧都摆正方向并转换到 sRGB，差异只来自编码本身。
/// </summary>
public sealed class CompareImageSource : ICompareDetailSource, IDisposable
{
    public const int TileSize = 256;

    /// <summary>每侧缓存的原图块上限；12MP 照片约可容纳一半画面，移动放大镜时大多命中缓存。</summary>
    public const long TileBudgetBytes = 24L * 1024 * 1024;

    private readonly Side _before;
    private readonly Side _after;
    private volatile bool _disposed;

    private CompareImageSource(Side before, Side after)
    {
        _before = before;
        _after = after;
    }

    /// <summary>读取两侧的尺寸与方向（只读文件头）。</summary>
    public static Task<CompareImageSource> OpenAsync(string beforePath, string afterPath, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            // 两侧、显示与细节共用一把解码锁：同一时间只展开一张全分辨率图片，峰值内存不随并发请求叠加
            var decodeGate = new Lock();
            return new CompareImageSource(Side.Open(beforePath, decodeGate), Side.Open(afterPath, decodeGate));
        }, cancellationToken);

    public PixelSize SourceSize => _before.Size;

    /// <summary>两侧自解码以来缓存的原图块字节数。</summary>
    internal long CachedBytes => _before.CachedBytes + _after.CachedBytes;

    /// <summary>为取细节而完整解码原图的次数。</summary>
    internal int FullDecodes => _before.FullDecodes + _after.FullDecodes;

    /// <summary>按目标像素尺寸解码两侧的显示位图（只缩小不放大）；两侧依次解码以压低峰值内存。</summary>
    public Task<(Bitmap Before, Bitmap After)> DecodeDisplayAsync(PixelSize target, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var before = _before.DecodeFit(target);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                return (before, _after.DecodeFit(target));
            }
            catch
            {
                before.Dispose();
                throw;
            }
        }, cancellationToken);

    public Task<CompareDetail?> GetDetailAsync(PixelRect region, PixelSize maxOutputSize, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            if (_disposed)
            {
                return null;
            }

            var clamped = region.Intersect(new PixelRect(SourceSize));
            if (clamped.Width <= 0 || clamped.Height <= 0)
            {
                return null;
            }

            var output = FitWithin(clamped.Size, maxOutputSize);
            var before = _before.ReadRegion(clamped, output, cancellationToken);
            try
            {
                var after = _after.ReadRegion(Map(clamped, SourceSize, _after.Size), output, cancellationToken);
                return (CompareDetail?)new CompareDetail(clamped, before, after);
            }
            catch
            {
                before.Dispose();
                throw;
            }
        }, cancellationToken);

    public void Dispose()
    {
        _disposed = true;
        _before.Clear();
        _after.Clear();
    }

    /// <summary>等比缩小到不超过上限；不放大。</summary>
    internal static PixelSize FitWithin(PixelSize size, PixelSize max)
    {
        if (max.Width <= 0 || max.Height <= 0 || size.Width <= max.Width && size.Height <= max.Height)
        {
            return size;
        }

        var scale = Math.Min((double)max.Width / size.Width, (double)max.Height / size.Height);
        return new PixelSize(Math.Max(1, (int)Math.Round(size.Width * scale)), Math.Max(1, (int)Math.Round(size.Height * scale)));
    }

    /// <summary>
    /// libjpeg 只能按 n/8 缩放，Magick 按 提示/原图 取最接近的档位，直接传目标尺寸可能落到比目标还小的档位；
    /// 这里换算成不小于目标的最小档位的精确尺寸。需要原尺寸时不给提示（给了反而会放大解码）。
    /// </summary>
    internal static PixelSize? JpegSizeHint(PixelSize stored, PixelSize target)
    {
        var ratio = Math.Max((double)target.Width / stored.Width, (double)target.Height / stored.Height);
        var eighths = (int)Math.Ceiling(ratio * 8 - 1e-9);
        if (eighths >= 8 || eighths <= 0)
        {
            return null;
        }

        return new PixelSize((int)Math.Ceiling(stored.Width * eighths / 8.0), (int)Math.Ceiling(stored.Height * eighths / 8.0));
    }

    /// <summary>产物尺寸与原图不同（例如编码器对齐到偶数）时按比例换算同一位置。</summary>
    internal static PixelRect Map(PixelRect region, PixelSize from, PixelSize to)
    {
        if (from == to || from.Width <= 0 || from.Height <= 0)
        {
            return region;
        }

        var sx = (double)to.Width / from.Width;
        var sy = (double)to.Height / from.Height;
        var x = (int)Math.Floor(region.X * sx);
        var y = (int)Math.Floor(region.Y * sy);
        var right = Math.Min(to.Width, (int)Math.Ceiling(region.Right * sx));
        var bottom = Math.Min(to.Height, (int)Math.Ceiling(region.Bottom * sy));
        return new PixelRect(x, y, Math.Max(1, right - x), Math.Max(1, bottom - y));
    }

    private static Bitmap CreateBitmap(byte[] bgra, PixelSize size)
    {
        var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Unpremul, handle.AddrOfPinnedObject(), size, new Vector(96, 96), size.Width * 4);
        }
        finally
        {
            handle.Free();
        }
    }

    private sealed class Side(string path, PixelSize size, OrientationType orientation, bool isJpeg, Lock decodeGate)
    {
        private readonly Lock _gate = new();
        private Dictionary<(int X, int Y), byte[]> _tiles = [];
        private int _fullDecodes;

        public PixelSize Size { get; } = size;

        public int FullDecodes => Volatile.Read(ref _fullDecodes);

        public long CachedBytes
        {
            get
            {
                lock (_gate)
                {
                    return _tiles.Values.Sum(tile => (long)tile.Length);
                }
            }
        }

        public static Side Open(string path, Lock decodeGate)
        {
            using var ping = new MagickImage();
            ping.Ping(path);
            var width = (int)ping.Width;
            var height = (int)ping.Height;
            if (IsTransposed(ping.Orientation))
            {
                (width, height) = (height, width);
            }

            return new Side(path, new PixelSize(width, height), ping.Orientation, ping.Format is MagickFormat.Jpeg or MagickFormat.Jpg or MagickFormat.Pjpeg, decodeGate);
        }

        public Bitmap DecodeFit(PixelSize target)
        {
            var settings = new MagickReadSettings();
            if (isJpeg && JpegSizeHint(IsTransposed(orientation) ? new PixelSize(Size.Height, Size.Width) : Size,
                    IsTransposed(orientation) ? new PixelSize(target.Height, target.Width) : target) is { } hint)
            {
                // libjpeg 按 n/8 缩放解码，不必先展开全分辨率
                settings.SetDefine(MagickFormat.Jpeg, "size", $"{hint.Width}x{hint.Height}");
            }

            lock (decodeGate)
            {
                using var image = Load(settings);
                // 按原图比例定尺寸：缩放解码的宽高各自向上取整，比例会有一像素的偏差
                var fit = FitWithin(Size, target);
                if (fit.Width != image.Width || fit.Height != image.Height)
                {
                    image.FilterType = FilterType.Lanczos;
                    image.Resize(new MagickGeometry((uint)fit.Width, (uint)fit.Height) { IgnoreAspectRatio = true });
                }

                var size = new PixelSize((int)image.Width, (int)image.Height);
                return CreateBitmap(Export(image, new PixelRect(size)), size);
            }
        }

        public Bitmap ReadRegion(PixelRect region, PixelSize output, CancellationToken cancellationToken)
        {
            var pixels = TryCompose(region);
            if (pixels is null)
            {
                lock (decodeGate)
                {
                    // 排队期间别的请求可能已经解码并填好了这一块
                    pixels = TryCompose(region);
                    if (pixels is null)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        using var image = Load(new MagickReadSettings());
                        Interlocked.Increment(ref _fullDecodes);
                        region = region.Intersect(new PixelRect(0, 0, (int)image.Width, (int)image.Height));
                        pixels = Export(image, region);
                        FillTiles(image, region);
                    }
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return output == region.Size ? CreateBitmap(pixels, region.Size) : Resize(pixels, region.Size, output);
        }

        public void Clear()
        {
            lock (_gate)
            {
                _tiles = [];
            }
        }

        private MagickImage Load(MagickReadSettings settings)
        {
            var image = new MagickImage(path, settings);
            try
            {
                image.AutoOrient();
                ToSrgb(image);
                return image;
            }
            catch
            {
                image.Dispose();
                throw;
            }
        }

        private static void ToSrgb(MagickImage image)
        {
            if (image.GetColorProfile() is { } profile)
            {
                if (profile.Description?.Contains("sRGB", StringComparison.OrdinalIgnoreCase) != true)
                {
                    image.TransformColorSpace(ColorProfiles.SRGB);
                }
            }
            else if (image.ColorSpace != ColorSpace.sRGB)
            {
                image.ColorSpace = ColorSpace.sRGB;
            }
        }

        private static bool IsTransposed(OrientationType orientation) =>
            orientation is OrientationType.LeftTop or OrientationType.RightTop or OrientationType.RightBottom or OrientationType.LeftBottom;

        private static byte[] Export(MagickImage image, PixelRect rect)
        {
            // 像素集合持有原生缓存视图，不释放会让整张解码后的像素一直留在原生内存里
            using var pixels = image.GetPixelsUnsafe();
            return Export(pixels, rect);
        }

        private static byte[] Export(IUnsafePixelCollection<byte> pixels, PixelRect rect) =>
            pixels.ToByteArray(rect.X, rect.Y, (uint)rect.Width, (uint)rect.Height, PixelMapping.BGRA)
            ?? throw new InvalidOperationException("无法读取图片像素。");

        private static Bitmap Resize(byte[] pixels, PixelSize from, PixelSize to)
        {
            using var image = new MagickImage();
            image.ReadPixels(pixels, new PixelReadSettings((uint)from.Width, (uint)from.Height, StorageType.Char, PixelMapping.BGRA));
            image.FilterType = FilterType.Lanczos;
            image.Resize(new MagickGeometry((uint)to.Width, (uint)to.Height) { IgnoreAspectRatio = true });
            return CreateBitmap(Export(image, new PixelRect(to)), to);
        }

        /// <summary>区域完全落在已缓存的块内时拼出像素，否则返回 <c>null</c>。</summary>
        private byte[]? TryCompose(PixelRect region)
        {
            lock (_gate)
            {
                var (x0, y0, x1, y1) = TileRange(region);
                for (var ty = y0; ty <= y1; ty++)
                {
                    for (var tx = x0; tx <= x1; tx++)
                    {
                        if (!_tiles.ContainsKey((tx, ty)))
                        {
                            return null;
                        }
                    }
                }

                var result = new byte[region.Width * region.Height * 4];
                for (var ty = y0; ty <= y1; ty++)
                {
                    for (var tx = x0; tx <= x1; tx++)
                    {
                        var tile = _tiles[(tx, ty)];
                        var tileRect = TileRect(tx, ty);
                        var overlap = tileRect.Intersect(region);
                        for (var row = overlap.Y; row < overlap.Bottom; row++)
                        {
                            Buffer.BlockCopy(
                                tile, ((row - tileRect.Y) * tileRect.Width + overlap.X - tileRect.X) * 4,
                                result, ((row - region.Y) * region.Width + overlap.X - region.X) * 4,
                                overlap.Width * 4);
                        }
                    }
                }

                return result;
            }
        }

        /// <summary>整图解码后，以本次区域为中心由近及远保留预算内的块；旧块整体替换。</summary>
        private void FillTiles(MagickImage image, PixelRect region)
        {
            var columns = (Size.Width + TileSize - 1) / TileSize;
            var rows = (Size.Height + TileSize - 1) / TileSize;
            var budget = (int)(TileBudgetBytes / (TileSize * TileSize * 4));
            var center = new Point(region.X + region.Width / 2.0, region.Y + region.Height / 2.0);
            var chosen = Enumerable.Range(0, columns * rows)
                .Select(i => (X: i % columns, Y: i / columns))
                .OrderBy(t =>
                {
                    var rect = TileRect(t.X, t.Y);
                    var dx = rect.X + rect.Width / 2.0 - center.X;
                    var dy = rect.Y + rect.Height / 2.0 - center.Y;
                    return dx * dx + dy * dy;
                })
                .Take(budget);

            Dictionary<(int X, int Y), byte[]> previous;
            lock (_gate)
            {
                previous = _tiles;
            }

            var tiles = new Dictionary<(int X, int Y), byte[]>();
            using var pixels = image.GetPixelsUnsafe();
            foreach (var key in chosen)
            {
                var rect = TileRect(key.X, key.Y).Intersect(new PixelRect(0, 0, (int)image.Width, (int)image.Height));
                if (rect.Width <= 0 || rect.Height <= 0 || rect != TileRect(key.X, key.Y))
                {
                    continue;
                }

                tiles[key] = previous.TryGetValue(key, out var existing) ? existing : Export(pixels, rect);
            }

            lock (_gate)
            {
                _tiles = tiles;
            }
        }

        private (int X0, int Y0, int X1, int Y1) TileRange(PixelRect region) =>
            (region.X / TileSize, region.Y / TileSize, (region.Right - 1) / TileSize, (region.Bottom - 1) / TileSize);

        private PixelRect TileRect(int tx, int ty)
        {
            var x = tx * TileSize;
            var y = ty * TileSize;
            return new PixelRect(x, y, Math.Min(TileSize, Size.Width - x), Math.Min(TileSize, Size.Height - y));
        }
    }
}
