using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using LivePhotoConvert.Desktop.ViewModels;
using LivePhotoConvert.Desktop.Views;

namespace LivePhotoConvert.Desktop;

public class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainVm = new MainWindowViewModel();
            var mainWindow = new MainWindow
            {
                DataContext = mainVm
            };
            desktop.MainWindow = mainWindow;

            if (desktop.Args is { Length: > 0 } && desktop.Args.Contains("--snapshot"))
            {
                mainWindow.Loaded += async (_, _) =>
                {
                    await RunSnapshotsAsync(mainWindow, mainVm, desktop);
                };
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task RunSnapshotsAsync(MainWindow window, MainWindowViewModel vm, IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            var outDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "screenshots");
            if (!Directory.Exists(outDir))
            {
                outDir = Path.Combine(Directory.GetCurrentDirectory(), "docs", "screenshots");
            }
            Directory.CreateDirectory(outDir);

            // Wait for window to load and gallery scanning
            await Task.Delay(3500);

            CaptureElement(window, Path.Combine(outDir, "01_convert_light.png"));

            // Capture Strip
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                vm.SelectedTabIndex = 1;
                var sampleFile = @"F:\AppleLivePhotoTest\IMG_0090.HEIC";
                if (File.Exists(sampleFile))
                {
                    await vm.StripVm.LoadSinglePhotoComparisonAsync(sampleFile);
                }
            });
            await Task.Delay(2500);
            CaptureElement(window, Path.Combine(outDir, "05_strip.png"));

            // Capture Tools
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                vm.SelectedTabIndex = 2;
            });
            await Task.Delay(1000);
            CaptureElement(window, Path.Combine(outDir, "06_tools.png"));

            // Capture Report
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                vm.SelectedTabIndex = 3;
                if (vm.ReportVm.TotalCount == 0)
                {
                    vm.ReportVm.Populate(new BatchReportModel
                    {
                        SummaryBadge = "全部成功 (100%)",
                        TotalCount = 1158,
                        SuccessCount = 1158,
                        FailedCount = 0,
                        SkippedCount = 0,
                        Elapsed = TimeSpan.FromSeconds(42),
                        OutputDirectory = @"F:\AppleLivePhotoTest_out",
                        ModeName = "苹果实况 (HEIC+MOV) ➔ 安卓动态照片",
                        Records =
                        [
                            new ReportItemRecord("IMG_0090.HEIC", "成功", "已无损合成 Google XMP 动态照片 (内嵌视频 14.8MB)", false, "Success", "HEIC+MOV", "JPEG+MP4", "0.4s"),
                            new ReportItemRecord("C8EBAADF-2C4D-4EA4-B853-1FE25818FF32.JPG", "成功", "已写入小米 0x8897 动态照片标记", false, "Success", "JPG+MOV", "JPEG+MP4", "0.3s"),
                            new ReportItemRecord("IMG_0092.HEIC", "成功", "已完成元数据与色彩空间同步", false, "Success", "HEIC+MOV", "JPEG+MP4", "0.5s")
                        ]
                    });
                }
            });
            await Task.Delay(1000);
            CaptureElement(window, Path.Combine(outDir, "07_report.png"));

            // Capture Dark mode
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                vm.SetThemeCommand.Execute("Dark");
                vm.SelectedTabIndex = 0;
            });
            await Task.Delay(1000);
            CaptureElement(window, Path.Combine(outDir, "03_convert_dark.png"));

            // Restore Light mode
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                vm.SetThemeCommand.Execute("Light");
            });
            await Task.Delay(500);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"Snapshot error: {ex}");
        }
        finally
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => desktop.Shutdown());
        }
    }

    private static void CaptureElement(Visual element, string path)
    {
        var bounds = element.Bounds;
        var width = (int)Math.Max(1280, bounds.Width);
        var height = (int)Math.Max(820, bounds.Height);
        using var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        rtb.Render(element);
#pragma warning disable CS0618
        using var fs = File.Create(path);
        rtb.Save(fs);
#pragma warning restore CS0618
    }
}
