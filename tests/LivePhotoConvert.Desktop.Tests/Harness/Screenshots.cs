using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;

namespace LivePhotoConvert.Desktop.Tests.Harness;

/// <summary>
/// 界面截图写到测试输出目录下的 screenshots/，供人工审查布局；只断言画面非空，不做像素比对（字体随平台不同）。
/// </summary>
public static class Screenshots
{
    public static string Directory { get; } = Path.Combine(AppContext.BaseDirectory, "screenshots");

    public static void Save(ShellSession session, string name)
    {
        var frame = session.Capture();
        if (frame is null)
        {
            // 平台没有产出帧时不判失败：截图只服务于人工审查
            TestContext.Current.TestOutputHelper?.WriteLine($"截图跳过（未渲染出帧）: {name}");
            return;
        }

        using (frame)
        {
            Assert.True(DistinctColors(frame) > 1, $"截图 {name} 是纯色画面");
            System.IO.Directory.CreateDirectory(Directory);
            var path = Path.Combine(Directory, name + ".png");
            frame.Save(path, PngBitmapEncoderOptions.Default);
            TestContext.Current.TestOutputHelper?.WriteLine($"截图: {path}");
        }
    }

    /// <summary>隔行采样统计像素颜色种数，达到上限即停止；用于判定画面是否有内容。</summary>
    public static int DistinctColors(WriteableBitmap bitmap, int stopAfter = 64)
    {
        using var buffer = bitmap.Lock();
        var colors = new HashSet<int>();
        var row = new int[buffer.Size.Width];
        var step = Math.Max(1, buffer.Size.Height / 200);
        for (var y = 0; y < buffer.Size.Height && colors.Count < stopAfter; y += step)
        {
            Marshal.Copy(buffer.Address + y * buffer.RowBytes, row, 0, row.Length);
            foreach (var pixel in row)
            {
                colors.Add(pixel);
            }
        }

        return colors.Count;
    }

    /// <summary>画面平均亮度（0~255），用于区分浅色与深色主题。</summary>
    public static double MeanLuminance(WriteableBitmap bitmap)
    {
        using var buffer = bitmap.Lock();
        var row = new int[buffer.Size.Width];
        double sum = 0;
        long count = 0;
        for (var y = 0; y < buffer.Size.Height; y += 4)
        {
            Marshal.Copy(buffer.Address + y * buffer.RowBytes, row, 0, row.Length);
            for (var x = 0; x < row.Length; x += 4)
            {
                // BGRA 与 RGBA 的 R、B 互换不影响这里的粗略亮度
                var p = row[x];
                sum += 0.3 * ((p >> 16) & 0xFF) + 0.59 * ((p >> 8) & 0xFF) + 0.11 * (p & 0xFF);
                count++;
            }
        }

        return count == 0 ? 0 : sum / count;
    }
}
