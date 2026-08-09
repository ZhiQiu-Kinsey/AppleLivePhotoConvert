using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.Versioning;

namespace LivePhotoConvert.Core.Tests;

[SupportedOSPlatform("windows")]
public class TerminalPreviewTests
{
    [Fact]
    public void Generate_Docs_Preview_Image()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var outputPath = Path.Combine(root, "docs", "preview.png");
        TerminalPreviewGenerator.Generate(outputPath);
        Assert.True(File.Exists(outputPath));
    }
}

[SupportedOSPlatform("windows")]
public static class TerminalPreviewGenerator
{
    public static void Generate(string outputPath)
    {
        const int width = 1000;
        const int height = 560;

        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        // 1. 现代沉浸式深色渐变背景
        using (var bgBrush = new LinearGradientBrush(new Rectangle(0, 0, width, height), Color.FromArgb(24, 26, 32), Color.FromArgb(14, 15, 19), 45f))
        {
            g.FillRectangle(bgBrush, 0, 0, width, height);
        }

        // 2. 绘制微光背景网格/圆点装饰
        using (var dotBrush = new SolidBrush(Color.FromArgb(18, 255, 255, 255)))
        {
            for (int x = 20; x < width; x += 30)
            {
                for (int y = 20; y < height; y += 30)
                {
                    g.FillEllipse(dotBrush, x, y, 2, 2);
                }
            }
        }

        // 3. 终端窗口区域 (居中放置带阴影与圆角)
        var termRect = new Rectangle(50, 40, 900, 480);
        var shadowRect = new Rectangle(termRect.X + 8, termRect.Y + 12, termRect.Width, termRect.Height);

        // 阴影
        using (var shadowBrush = new SolidBrush(Color.FromArgb(90, 0, 0, 0)))
        {
            FillRoundedRectangle(g, shadowBrush, shadowRect, 12);
        }

        // 终端背景 (Windows Terminal 纯黑半透感)
        using (var termBgBrush = new SolidBrush(Color.FromArgb(245, 18, 20, 24)))
        {
            FillRoundedRectangle(g, termBgBrush, termRect, 12);
        }

        // 终端外边框
        using (var borderPen = new Pen(Color.FromArgb(60, 255, 255, 255), 1f))
        {
            DrawRoundedRectangle(g, borderPen, termRect, 12);
        }

        // 4. Windows Terminal 顶部标题栏
        var titleBarRect = new Rectangle(termRect.X, termRect.Y, termRect.Width, 38);
        using (var titleBarBrush = new SolidBrush(Color.FromArgb(235, 28, 30, 36)))
        {
            FillTopRoundedRectangle(g, titleBarBrush, titleBarRect, 12);
        }

        // 顶部 Tab 标签
        var tabRect = new Rectangle(termRect.X + 12, termRect.Y + 6, 210, 32);
        using (var tabBrush = new SolidBrush(Color.FromArgb(245, 18, 20, 24)))
        {
            FillTopRoundedRectangle(g, tabBrush, tabRect, 6);
        }

        using var fontTitle = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular);
        using var fontCode = new Font("Consolas", 11.5f, FontStyle.Regular);
        using var fontCodeBold = new Font("Consolas", 11.5f, FontStyle.Bold);
        using var fontCn = new Font("Microsoft YaHei UI", 11f, FontStyle.Regular);
        using var fontCnBold = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold);

        // Tab 图标与文字
        using (var textBrush = new SolidBrush(Color.FromArgb(220, 225, 230)))
        {
            g.DrawString("⚡ LivePhotoConvert", fontTitle, textBrush, termRect.X + 26, termRect.Y + 12);
        }

        // 窗口控制按钮 (最小化、最大化、关闭)
        int btnY = termRect.Y + 12;
        int btnRight = termRect.Right - 20;
        using (var closePen = new Pen(Color.FromArgb(160, 160, 160), 1.2f))
        {
            // 关闭 X
            g.DrawLine(closePen, btnRight - 10, btnY, btnRight, btnY + 10);
            g.DrawLine(closePen, btnRight, btnY, btnRight - 10, btnY + 10);
            // 最大化 方框
            g.DrawRectangle(closePen, btnRight - 32, btnY, 10, 10);
            // 最小化 横线
            g.DrawLine(closePen, btnRight - 54, btnY + 5, btnRight - 44, btnY + 5);
        }

        // 5. 终端控制台内容 (真实 Spectre.Console 输出)
        float startX = termRect.X + 32;
        float startY = termRect.Y + 60;

        // 顶部分割标题：──────────────────────── 欢迎使用动态照片工具箱 ────────────────────────
        using (var cyanPen = new Pen(Color.FromArgb(0, 210, 210), 1.5f))
        using (var cyanBrush = new SolidBrush(Color.FromArgb(0, 220, 220)))
        {
            float lineY = startY + 12;
            g.DrawLine(cyanPen, startX, lineY, startX + 220, lineY);
            g.DrawString(" 欢迎使用动态照片工具箱 ", fontCnBold, cyanBrush, startX + 225, startY);
            g.DrawLine(cyanPen, startX + 475, lineY, startX + 830, lineY);
        }

        startY += 48;

        // 菜单提示文字：请选择要执行的操作 (使用方向键 ↑/↓ 选择，回车确认)：
        using (var yellowBrush = new SolidBrush(Color.FromArgb(240, 210, 60)))
        {
            g.DrawString("请选择要执行的操作 (使用方向键 ↑/↓ 选择，回车确认)：", fontCnBold, yellowBrush, startX, startY);
        }

        startY += 36;

        // 选项 1（选中项，高亮青色）： ● 1. 合成动态照片 (苹果实况照片 → 安卓动态照片)
        using (var activeBrush = new SolidBrush(Color.FromArgb(0, 235, 235)))
        using (var activeDotBrush = new SolidBrush(Color.FromArgb(0, 235, 235)))
        {
            g.FillEllipse(activeDotBrush, startX + 16, startY + 6, 8, 8);
            g.DrawString("1. 合成动态照片 (苹果实况照片 → 安卓动态照片)", fontCnBold, activeBrush, startX + 34, startY);
        }

        startY += 32;

        // 选项 2： ○ 2. 拆分动态照片 (安卓动态照片 → 照片 + 视频)
        using (var inactiveBrush = new SolidBrush(Color.FromArgb(210, 215, 225)))
        using (var circlePen = new Pen(Color.FromArgb(140, 145, 155), 1.5f))
        {
            g.DrawEllipse(circlePen, startX + 16, startY + 6, 8, 8);
            g.DrawString("2. 拆分动态照片 (安卓动态照片 → 照片 + 视频)", fontCn, inactiveBrush, startX + 34, startY);
        }

        startY += 32;

        // 选项 3： ○ 3. 检查并下载外部依赖工具 (ExifTool 与 FFmpeg)
        using (var inactiveBrush = new SolidBrush(Color.FromArgb(210, 215, 225)))
        using (var circlePen = new Pen(Color.FromArgb(140, 145, 155), 1.5f))
        {
            g.DrawEllipse(circlePen, startX + 16, startY + 6, 8, 8);
            g.DrawString("3. 检查并下载外部依赖工具 (ExifTool 与 FFmpeg)", fontCn, inactiveBrush, startX + 34, startY);
        }

        startY += 32;

        // 选项 4： ○ 4. 退出程序
        using (var inactiveBrush = new SolidBrush(Color.FromArgb(210, 215, 225)))
        using (var circlePen = new Pen(Color.FromArgb(140, 145, 155), 1.5f))
        {
            g.DrawEllipse(circlePen, startX + 16, startY + 6, 8, 8);
            g.DrawString("4. 退出程序", fontCn, inactiveBrush, startX + 34, startY);
        }

        startY += 52;

        // 底部环境与状态指示 (幽雅提示)
        using (var infoBoxBg = new SolidBrush(Color.FromArgb(40, 0, 210, 210)))
        using (var infoBoxPen = new Pen(Color.FromArgb(60, 0, 210, 210), 1f))
        using (var greenBrush = new SolidBrush(Color.FromArgb(80, 220, 130)))
        using (var greyBrush = new SolidBrush(Color.FromArgb(160, 165, 175)))
        {
            var infoRect = new RectangleF(startX, startY, 830, 42);
            FillRoundedRectangle(g, infoBoxBg, Rectangle.Round(infoRect), 6);
            DrawRoundedRectangle(g, infoBoxPen, Rectangle.Round(infoRect), 6);

            g.DrawString("√ ExifTool 已就绪", fontCn, greenBrush, startX + 16, startY + 10);
            g.DrawString("√ FFmpeg 已就绪", fontCn, greenBrush, startX + 180, startY + 10);
            g.DrawString("原生 Native AOT 引擎已加载 (极速流复制就绪)", fontCn, greyBrush, startX + 360, startY + 10);
        }

        // 保存产物
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        bmp.Save(outputPath, ImageFormat.Png);
    }

    private static void FillRoundedRectangle(Graphics g, Brush brush, Rectangle bounds, int cornerRadius)
    {
        using var path = CreateRoundedRectanglePath(bounds, cornerRadius);
        g.FillPath(brush, path);
    }

    private static void DrawRoundedRectangle(Graphics g, Pen pen, Rectangle bounds, int cornerRadius)
    {
        using var path = CreateRoundedRectanglePath(bounds, cornerRadius);
        g.DrawPath(pen, path);
    }

    private static void FillTopRoundedRectangle(Graphics g, Brush brush, Rectangle bounds, int cornerRadius)
    {
        using var path = new GraphicsPath();
        int d = cornerRadius * 2;
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddLine(bounds.Right, bounds.Bottom, bounds.X, bounds.Bottom);
        path.CloseFigure();
        g.FillPath(brush, path);
    }

    private static GraphicsPath CreateRoundedRectanglePath(Rectangle bounds, int cornerRadius)
    {
        var path = new GraphicsPath();
        int d = cornerRadius * 2;
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
