using Avalonia;
using LivePhotoConvert.Core.Services;
using Velopack;

namespace LivePhotoConvert.Desktop;

internal static class Program
{
    // Avalonia 初始化之前不要使用任何 Avalonia 或依赖 SynchronizationContext 的 API
    [STAThread]
    public static void Main(string[] args)
    {
        // 必须最先执行：安装、更新与卸载时 Velopack 以特殊参数启动本程序，处理完即退出；也负责在启动时安装已下载的更新。
        // 其它平台不使用安装器，不创建 Velopack 定位器（它会写日志到临时目录）。
        if (OperatingSystem.IsWindows())
        {
            VelopackApp.Build().Run();
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                ErrorLogger.Log(ex, e.IsTerminating ? "进程未处理异常（即将退出）" : "进程未处理异常");
            }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            ErrorLogger.Log(e.Exception, "未观察的任务异常");
            e.SetObserved();
        };

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // 可视化设计器也使用此方法，不要删除
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
