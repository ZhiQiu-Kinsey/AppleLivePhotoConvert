using ImageMagick;

namespace LivePhotoConvert.Core.Media.UltraHdr;

/// <summary>
/// 把 Apple HDR 增益图重编码为 ISO 21496-1 / hdrgm 语义的增益图。
/// </summary>
/// <remarks>
/// Apple 的像素 v 表示线性增益 1 + (H − 1)·L(v)（L 为 Rec.709 反 OETF），ISO 的像素 g 表示 log2(增益) 在
/// [GainMapMin, GainMapMax] 上的插值。两者不是同一条曲线，直接照搬像素误差可达 0.2 档，只调 Gamma 仍有 0.03～0.08 档；
/// 逐值查表换算后仅剩 8 位量化误差（不超过 0.006 档）。
/// </remarks>
public static class AppleGainMapConverter
{
    /// <summary>增益图体积只占主图约 3%，质量 90 已看不出块效应。</summary>
    public const int JpegQuality = 90;

    /// <summary>
    /// Apple 像素值 → ISO 像素值的 256 项查找表。
    /// </summary>
    /// <param name="headroom">线性余量 H，必须大于 1</param>
    public static byte[] BuildLookupTable(double headroom)
    {
        if (!double.IsFinite(headroom) || headroom <= 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(headroom), headroom, "HDR 余量必须大于 1。");
        }

        var table = new byte[256];
        var stops = Math.Log2(headroom);
        for (var v = 0; v < table.Length; v++)
        {
            var boost = 1.0 + (headroom - 1.0) * Rec709InverseOetf(v / 255.0);
            var g = Math.Log2(boost) / stops;
            table[v] = (byte)Math.Clamp(Math.Round(g * 255.0, MidpointRounding.ToEven), 0, 255);
        }

        return table;
    }

    /// <summary>
    /// Rec.709 反 OETF：Apple 的增益图用 Rec.709 传递函数编码（不是 sRGB）。
    /// </summary>
    public static double Rec709InverseOetf(double value) =>
        value < 0.081 ? value / 4.5 : Math.Pow((value + 0.099) / 1.099, 1.0 / 0.45);

    /// <summary>
    /// 读取 Apple 增益图，按查找表换算后写出灰度 JPEG（不带元数据，由 <see cref="UltraHdrJpegWriter"/> 注入）。
    /// </summary>
    /// <param name="appleGainMapPath">heif-dec 导出的增益图（与主图方向、比例一致）</param>
    /// <param name="destinationPath">输出 JPEG</param>
    /// <param name="headroom">线性余量 H</param>
    /// <param name="cancellationToken">取消令牌</param>
    public static Task ConvertAsync(string appleGainMapPath, string destinationPath, double headroom, CancellationToken cancellationToken = default)
    {
        var table = BuildLookupTable(headroom);
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] pixels;
            uint width;
            uint height;
            using (var source = new MagickImage(appleGainMapPath))
            {
                width = source.Width;
                height = source.Height;
                // 原始灰度样本：heif-dec 输出的是单通道 8 位图，不经过任何色彩转换
                source.Depth = 8;
                pixels = source.ToByteArray(MagickFormat.Gray);
            }

            if (pixels.Length != (long)width * height)
            {
                throw new InvalidDataException($"增益图像素数量异常：{pixels.Length}，期望 {width}×{height}。");
            }

            Remap(pixels, table);
            cancellationToken.ThrowIfCancellationRequested();

            using var output = new MagickImage(pixels, new MagickReadSettings { Format = MagickFormat.Gray, Width = width, Height = height, Depth = 8 });
            output.ColorType = ColorType.Grayscale;
            output.Quality = JpegQuality;
            output.Strip();
            output.Write(destinationPath, MagickFormat.Jpeg);
        }, cancellationToken);
    }

    internal static void Remap(Span<byte> pixels, ReadOnlySpan<byte> table)
    {
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = table[pixels[i]];
        }
    }
}
