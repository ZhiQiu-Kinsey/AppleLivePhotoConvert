using ImageMagick;
using ImageMagick.Drawing;
using LivePhotoConvert.Core.Tests.Support;

namespace LivePhotoConvert.Desktop.Tests.Harness;

/// <summary>用真实编码的图片构造相册，画廊缩略图与对比预览才有可解码的内容。</summary>
public static class SampleAlbum
{
    private static readonly string[] Palette = ["#E4572E", "#17BEBB", "#FFC914", "#2E282A", "#76B041", "#3F88C5", "#A23B72", "#F18F01"];

    public static string WriteJpeg(string path, int index = 0, int width = 480, int height = 360)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var image = new MagickImage(new MagickColor(Palette[index % Palette.Length]), (uint)width, (uint)height);
        new Drawables()
            .FillColor(MagickColors.White)
            .Circle(width / 2.0, height / 2.0, width / 2.0 + height / 5.0, height / 2.0)
            .Draw(image);
        image.Write(path, MagickFormat.Jpeg);
        return path;
    }

    /// <summary>写入 count 组苹果实况对（真实 JPEG + 结构合法的 MOV 占位）。</summary>
    public static void WriteApplePairs(string directory, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var stem = Path.Combine(directory, $"IMG_{i + 1:D4}");
            // 横竖混排，便于审查等高排版
            WriteJpeg(stem + ".jpg", i, i % 3 == 2 ? 360 : 480, i % 3 == 2 ? 480 : 360);
            File.WriteAllBytes(stem + ".mov", SyntheticMedia.Mov(8000));
        }
    }
}
