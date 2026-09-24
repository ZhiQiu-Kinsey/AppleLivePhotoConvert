using System.Globalization;
using ImageMagick;
using ImageMagick.Drawing;
using LivePhotoConvert.Core.External;
using LivePhotoConvert.Core.Tests.Support;

namespace LivePhotoConvert.Desktop.Tests.Docs;

/// <summary>
/// 文档截图用的示例相册：程序绘制的风景插画（带 EXIF 拍摄时间），配真实编码的 MOV / MP4，
/// 画廊缩略图、QuickLook 播放与瘦身对比都能显示真实内容。内容由固定种子决定，重跑结果一致。
/// </summary>
internal static class DocsAlbum
{
    /// <summary>一个条目：苹果实况对或安卓动态照片。</summary>
    public sealed record Entry(string Stem, DateTime Taken, bool Portrait, bool AndroidMotionPhoto, bool TimeMismatch = false);

    /// <summary>三天的拍摄：两组 iPhone 实况（其中一对的视频时间与照片相差 2 分钟），一组安卓动态照片。</summary>
    public static IReadOnlyList<Entry> Entries { get; } =
    [
        new("IMG_4102", new DateTime(2026, 9, 14, 17, 2, 12), false, false),
        new("IMG_4103", new DateTime(2026, 9, 14, 17, 20, 45), true, false),
        new("IMG_4104", new DateTime(2026, 9, 14, 17, 48, 3), false, false),
        new("IMG_4105", new DateTime(2026, 9, 14, 18, 5, 37), false, false),
        new("IMG_4106", new DateTime(2026, 9, 14, 18, 31, 20), true, false),
        new("IMG_3988", new DateTime(2026, 8, 2, 9, 12, 8), false, false),
        new("IMG_3989", new DateTime(2026, 8, 2, 9, 40, 51), true, false),
        new("IMG_3990", new DateTime(2026, 8, 2, 10, 15, 30), false, false, TimeMismatch: true),
        new("IMG_3991", new DateTime(2026, 8, 2, 11, 3, 16), false, false),
        new("IMG_3992", new DateTime(2026, 8, 2, 11, 27, 44), true, false),
        new("MVIMG_20260720_183205", new DateTime(2026, 7, 20, 18, 32, 5), false, true),
        new("MVIMG_20260720_184417", new DateTime(2026, 7, 20, 18, 44, 17), true, true),
        new("MVIMG_20260720_190122", new DateTime(2026, 7, 20, 19, 1, 22), false, true),
        new("MVIMG_20260720_192938", new DateTime(2026, 7, 20, 19, 29, 38), false, true)
    ];

    public static int ApplePairCount => Entries.Count(e => !e.AndroidMotionPhoto);

    /// <summary>在 <paramref name="directory"/> 中写入全部条目；需要 FFmpeg（libx264）编码视频。</summary>
    public static async Task WriteAsync(string directory, string ffmpeg, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var scratch = Path.Combine(directory, ".work");
        Directory.CreateDirectory(scratch);
        try
        {
            for (var i = 0; i < Entries.Count; i++)
            {
                var entry = Entries[i];
                var (width, height) = entry.Portrait ? (3024, 4032) : (4032, 3024);
                if (entry.AndroidMotionPhoto)
                {
                    var cover = Path.Combine(scratch, entry.Stem + ".jpg");
                    var video = Path.Combine(scratch, entry.Stem + ".mp4");
                    Paint(cover, i, width, height, entry.Taken, "Xiaomi", "Xiaomi 14");
                    await EncodeClipAsync(ffmpeg, cover, video, null, cancellationToken);
                    var target = Path.Combine(directory, entry.Stem + ".jpg");
                    await File.WriteAllBytesAsync(target, SyntheticMedia.MotionPhoto(File.ReadAllBytes(cover), File.ReadAllBytes(video)), cancellationToken);
                    Stamp(target, entry.Taken);
                }
                else
                {
                    var photo = Path.Combine(directory, entry.Stem + ".JPG");
                    var video = Path.Combine(directory, entry.Stem + ".MOV");
                    Paint(photo, i, width, height, entry.Taken, "Apple", "iPhone 15 Pro");
                    // mvhd 按 UTC 存储、扫描时按本机时区换回当地时间：正常配对比照片晚 1 秒，时间不一致的那一对晚 2 分钟（触发人工裁决）
                    var creation = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(entry.Taken, DateTimeKind.Unspecified), TimeZoneInfo.Local)
                        .AddSeconds(entry.TimeMismatch ? 120 : 1);
                    await EncodeClipAsync(ffmpeg, photo, video, creation, cancellationToken);
                    Stamp(photo, entry.Taken);
                    Stamp(video, entry.Taken);
                }
            }
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>复制已生成的相册（只换目录名），并恢复文件时间。</summary>
    public static void CopyTo(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var entry in Entries)
        {
            foreach (var file in Directory.EnumerateFiles(destination, entry.Stem + ".*"))
            {
                Stamp(file, entry.Taken);
            }
        }
    }

    public static string? FindFfmpeg() => ToolLocator.Find(FfmpegVideoConverter.ExecutableName);

    /// <summary>
    /// 把照片编码成 2 秒的视频：画面即照片本身，叠加时域颗粒让体积接近手机实拍的量级（每段数百 KB），瘦身预估才有代表性。
    /// </summary>
    private static async Task EncodeClipAsync(string ffmpeg, string still, string output, DateTime? creationTime, CancellationToken cancellationToken)
    {
        List<string> arguments =
        [
            "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
            "-loop", "1", "-framerate", "30", "-t", "2", "-i", still,
            "-vf", "scale='if(gt(iw,ih),1280,-2)':'if(gt(iw,ih),-2,1280)',noise=alls=8:allf=t+u,format=yuv420p",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-an",
            "-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709",
            "-map_metadata", "-1"
        ];
        if (creationTime is { } time)
        {
            arguments.AddRange(["-metadata", "creation_time=" + time.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) + "Z"]);
        }

        arguments.Add(output);
        var result = await ProcessRunner.RunAsync(ffmpeg, arguments, cancellationToken);
        if (!result.Success)
        {
            throw new InvalidOperationException($"FFmpeg 编码示例视频失败：{result.StandardError}");
        }
    }

    private static void Stamp(string path, DateTime time)
    {
        File.SetCreationTime(path, time);
        File.SetLastWriteTime(path, time);
    }

    // ── 风景插画 ──

    private sealed record Palette(string SkyTop, string SkyBottom, string Sun, double SunAlpha, string[] Ridges, bool Stars = false, bool Pines = false, bool Lake = false);

    private static readonly Palette[] Palettes =
    [
        new("#2B1B4A", "#F28A5B", "#FFE3A3", 1.0, ["#8C4A6E", "#5E3160", "#2E1A3C"], Lake: true),
        new("#6FA8DC", "#F6D5C3", "#FFF6E5", 0.9, ["#94A9C6", "#6D86A8", "#445E80"], Pines: true),
        new("#2F7FD0", "#BFE3F7", "#FFFFFF", 0.8, ["#86AFCF", "#557FA2", "#2F5D46"], Pines: true),
        new("#0B1026", "#34467A", "#F4F1DE", 1.0, ["#26345A", "#18223F", "#0C1226"], Stars: true, Lake: true),
        new("#F2A65A", "#FCE8CF", "#FFF4D6", 1.0, ["#DB9560", "#B66C3E", "#83482A"]),
        new("#134E6F", "#FFA62B", "#FFE1A8", 1.0, ["#2B5E73", "#18445A", "#0A2C3C"], Pines: true),
        new("#9DB6AB", "#EAF1EC", "#FFFFFF", 0.35, ["#809E91", "#5E7F71", "#3B5A4D"], Pines: true, Lake: true),
        new("#5F4B8B", "#EFA38F", "#FFD9BD", 1.0, ["#80628F", "#5E4777", "#3A2B54"])
    ];

    private static void Paint(string path, int seed, int width, int height, DateTime taken, string make, string model)
    {
        var palette = Palettes[seed % Palettes.Length];
        var random = new Random(seed * 7919 + 17);
        using var image = new MagickImage($"gradient:{palette.SkyTop}-{palette.SkyBottom}",
            new MagickReadSettings { Width = (uint)width, Height = (uint)height });
        var horizon = height * (palette.Lake ? 0.66 : 0.72);

        if (palette.Stars)
        {
            var stars = new Drawables().FillColor(new MagickColor("#FFFFFFCC"));
            for (var i = 0; i < 140; i++)
            {
                var x = random.NextDouble() * width;
                var y = random.NextDouble() * horizon * 0.8;
                var r = (0.8 + random.NextDouble() * 1.6) * width / 1600.0;
                stars.Circle(x, y, x + r, y);
            }

            stars.Draw(image);
        }

        // 太阳 / 月亮：先画模糊的光晕层再画本体
        var sunX = width * (0.25 + random.NextDouble() * 0.5);
        var sunY = horizon - height * (0.12 + random.NextDouble() * 0.22);
        var sunR = Math.Min(width, height) * (0.07 + random.NextDouble() * 0.03);
        // 光晕在 1/8 尺寸上模糊后放大，避免在全尺寸上做大半径模糊
        const int glowScale = 8;
        using (var glow = new MagickImage(MagickColors.Transparent, (uint)(width / glowScale), (uint)(height / glowScale)))
        {
            var (gx, gy, gr) = (sunX / glowScale, sunY / glowScale, sunR / glowScale);
            new Drawables().FillColor(WithAlpha(palette.Sun, 0.45 * palette.SunAlpha)).Circle(gx, gy, gx + gr * 2.2, gy).Draw(glow);
            glow.Blur(0, gr * 0.9);
            glow.Resize(new MagickGeometry((uint)width, (uint)height) { IgnoreAspectRatio = true });
            image.Composite(glow, CompositeOperator.Over);
        }

        new Drawables().FillColor(WithAlpha(palette.Sun, palette.SunAlpha)).Circle(sunX, sunY, sunX + sunR, sunY).Draw(image);

        // 三层山脊，由远及近变暗变低
        for (var layer = 0; layer < palette.Ridges.Length; layer++)
        {
            var baseY = horizon - height * (0.20 - layer * 0.075);
            var amplitude = height * (0.07 - layer * 0.012);
            var ridge = RidgeLine(random, width, baseY, amplitude);
            var polygon = new List<PointD>(ridge) { new(width, height), new(0, height) };
            var drawables = new Drawables().FillColor(new MagickColor(palette.Ridges[layer])).Polygon(polygon);
            if (palette.Pines && layer == palette.Ridges.Length - 1)
            {
                for (var i = 0; i < 26; i++)
                {
                    var index = random.Next(ridge.Count);
                    var (x, y) = (ridge[index].X, ridge[index].Y + height * 0.003);
                    var h = height * (0.035 + random.NextDouble() * 0.045);
                    var w = h * 0.42;
                    drawables.Polygon(new PointD(x, y - h), new PointD(x + w / 2, y + h * 0.05), new PointD(x - w / 2, y + h * 0.05));
                }
            }

            drawables.Draw(image);
        }

        if (palette.Lake)
        {
            // 湖面：底部一带压暗，太阳倒影为几条错落的亮线
            var lakeTop = height * 0.80;
            new Drawables().FillColor(WithAlpha(palette.SkyTop, 0.72)).Rectangle(0, lakeTop, width, height).Draw(image);
            var streaks = new Drawables().FillColor(WithAlpha(palette.Sun, 0.55 * palette.SunAlpha));
            for (var i = 0; i < 9; i++)
            {
                var y = lakeTop + height * 0.012 + i * (height - lakeTop - height * 0.02) / 9.0;
                var half = sunR * (1.1 - i * 0.08) * (0.7 + random.NextDouble() * 0.5);
                streaks.Rectangle(sunX - half, y, sunX + half, y + height * (0.0025 + i * 0.0004));
            }

            streaks.Draw(image);
        }

        var exif = new ExifProfile();
        exif.SetValue(ExifTag.DateTimeOriginal, taken.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture));
        exif.SetValue(ExifTag.DateTimeDigitized, taken.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture));
        exif.SetValue(ExifTag.Make, make);
        exif.SetValue(ExifTag.Model, model);
        image.SetProfile(exif);
        image.Quality = 92;
        image.Write(path, MagickFormat.Jpeg);
    }

    /// <summary>几条正弦叠加加少量抖动，得到平滑但不规则的山脊线。</summary>
    private static List<PointD> RidgeLine(Random random, int width, double baseY, double amplitude)
    {
        var waves = Enumerable.Range(0, 3)
            .Select(i => (Frequency: (1.5 + random.NextDouble() * 2.5) * (i + 1) * Math.PI / width, Phase: random.NextDouble() * Math.PI * 2, Weight: 1.0 / (i + 1)))
            .ToArray();
        var points = new List<PointD>();
        for (var x = 0; x <= width; x += 16)
        {
            var y = baseY;
            foreach (var (frequency, phase, weight) in waves)
            {
                y -= amplitude * weight * Math.Sin(x * frequency + phase);
            }

            points.Add(new PointD(x, y + (random.NextDouble() - 0.5) * amplitude * 0.04));
        }

        return points;
    }

    private static MagickColor WithAlpha(string hex, double alpha)
    {
        var color = new MagickColor(hex);
        color.A = (byte)Math.Round(255 * Math.Clamp(alpha, 0, 1));
        return color;
    }
}
