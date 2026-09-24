using System.Collections.Concurrent;
using System.Text;
using ImageMagick;
using LivePhotoConvert.Core.Abstractions;
using LivePhotoConvert.Core.Media.UltraHdr;

namespace LivePhotoConvert.Core.Tests.Support;

/// <summary>
/// 用 Magick 生成真实可解码的主图与增益图，供 Ultra HDR 组装相关测试使用。
/// </summary>
internal static class UltraHdrSamples
{
    public const string CustomXmp = """
        <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
        <rdf:Description rdf:about="" xmlns:lpc="https://example.invalid/lpc/1.0/" lpc:Marker="keep-me"/>
        </rdf:RDF></x:xmpmeta>
        """;

    /// <summary>带 Exif、自定义命名空间 XMP 与 sRGB ICC 的彩色 JPEG。</summary>
    public static void WritePrimary(string path, uint width = 64, uint height = 48)
    {
        using var image = new MagickImage(MagickColors.OrangeRed, width, height);
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Make, "Apple");
        image.SetProfile(exif);
        image.SetProfile(new XmpProfile(Encoding.UTF8.GetBytes(CustomXmp)));
        image.SetProfile(ColorProfiles.SRGB);
        image.Quality = 90;
        image.Write(path, MagickFormat.Jpeg);
    }

    /// <summary>水平渐变的单通道增益图。</summary>
    public static void WriteGainMap(string path, uint width = 32, uint height = 24, MagickFormat format = MagickFormat.Jpeg)
    {
        var pixels = new byte[width * height];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)(i % width * 255 / Math.Max(1, width - 1));
        }

        using var image = new MagickImage(pixels, new MagickReadSettings { Format = MagickFormat.Gray, Width = width, Height = height, Depth = 8 });
        image.ColorType = ColorType.Grayscale;
        image.Write(path, format);
    }

    /// <summary>组装一张 Ultra HDR JPEG，返回其路径。</summary>
    public static async Task<string> CreateUltraHdrAsync(TempDirectory temp, string name = "uhdr.jpg", double headroom = 4, CancellationToken cancellationToken = default)
    {
        var primary = temp.Combine($"{name}.primary.jpg");
        var gainMap = temp.Combine($"{name}.gainmap.jpg");
        WritePrimary(primary);
        WriteGainMap(gainMap);
        var output = temp.Combine(name);
        await UltraHdrJpegWriter.WriteAsync(primary, gainMap, GainMapMetadata.FromAppleHeadroom(headroom), output, cancellationToken);
        File.Delete(primary);
        File.Delete(gainMap);
        return output;
    }
}

/// <summary>
/// 增益图解码的内存实现：用 Magick 生成主图与增益图，或按设置返回空结果、抛出异常。
/// </summary>
internal sealed class FakeGainMapDecoder : IAppleGainMapDecoder
{
    public ConcurrentBag<string> Decoded { get; } = [];

    public bool ReturnNull { get; init; }

    public Exception? Failure { get; init; }

    public Task<AppleGainMapImages?> DecodeAsync(string heicPath, string outputDirectory, CancellationToken cancellationToken = default)
    {
        Decoded.Add(heicPath);
        if (Failure is not null)
        {
            throw Failure;
        }

        if (ReturnNull)
        {
            return Task.FromResult<AppleGainMapImages?>(null);
        }

        Directory.CreateDirectory(outputDirectory);
        var primary = Path.Combine(outputDirectory, "primary.jpg");
        var gainMap = Path.Combine(outputDirectory, "primary-gainmap.png");
        UltraHdrSamples.WritePrimary(primary);
        UltraHdrSamples.WriteGainMap(gainMap, format: MagickFormat.Png);
        return Task.FromResult<AppleGainMapImages?>(new AppleGainMapImages(primary, gainMap));
    }
}
