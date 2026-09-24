using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using LivePhotoConvert.Core.External.Tools;
using LivePhotoConvert.Core.Services;
using LivePhotoConvert.Desktop.Features.Dialogs;
using LivePhotoConvert.Desktop.Features.Library.Thumbnails;
using LivePhotoConvert.Desktop.Features.Playback;
using LivePhotoConvert.Desktop.Features.Shell;
using LivePhotoConvert.Desktop.Features.Updates;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LivePhotoConvert.Desktop;

public class App : Application
{
    private ServiceProvider? _services;
    private bool _showingError;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = AppServices.Build();
            _services = services;

            // 主题与语言必须先于任何页面 VM 生效，否则首屏会残留默认语言的文案
            var settings = services.GetRequiredService<SettingsStore>();
            services.GetRequiredService<ThemeService>().Apply(settings.Current.Theme);
            services.GetRequiredService<ILocalizer>().SetLanguage(settings.Current.Language);

            Dispatcher.UIThread.UnhandledException += OnUiThreadUnhandledException;

            var window = new ShellWindow { DataContext = services.GetRequiredService<ShellViewModel>() };
            services.GetRequiredService<WindowPlacementTracker>().Attach(window);
            services.GetRequiredService<AppLifetime>().Attach(window);
            desktop.MainWindow = window;
            desktop.Exit += (_, _) => services.Dispose();

            _ = Task.Run(SafetyGuard.CleanOrphanTempDirectories);
            var installer = services.GetRequiredService<IToolInstaller>();
            _ = Task.Run(() => RecoverToolInstalls(installer));
            if (services.GetRequiredService<IUpdateService>().IsSupported)
            {
                _ = Task.Run(() => MigrateLegacyTools(installer.InstallRoot));
            }

            _ = services.GetRequiredService<UpdateCenter>().StartAutoCheck();
            _ = Task.Run(LegacyThumbnailCache.TryDelete);
            _ = Task.Run(LegacyMotionCache.TryDelete);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>上次在替换工具目录中途退出时，把备份目录改回来，否则该工具在下次安装前一直显示缺失。</summary>
    private static void RecoverToolInstalls(IToolInstaller installer)
    {
        try
        {
            installer.RecoverInterruptedInstalls();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLogger.Log(ex, "恢复中断的工具安装");
        }
    }

    /// <summary>
    /// 旧版装在程序目录下的工具复制到新的安装位置；源保留，程序目录下的副本随下次更新一起被替换。
    /// </summary>
    private static void MigrateLegacyTools(string installRoot)
    {
        try
        {
            ToolDirectories.MigrateLegacyTools(ToolDirectories.LegacyToolRoots(), installRoot,
                (path, ex) => ErrorLogger.Log(ex, $"迁移旧版工具目录 {path}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLogger.Log(ex, "迁移旧版工具目录");
        }
    }

    private async void OnUiThreadUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var logPath = ErrorLogger.Log(e.Exception, "界面线程未处理异常");
        e.Handled = true;

        // 连续异常只提示一次，避免弹窗排队淹没界面
        if (_showingError || _services is null)
        {
            return;
        }

        _showingError = true;
        try
        {
            var localizer = _services.GetRequiredService<ILocalizer>();
            await _services.GetRequiredService<IDialogService>().ShowAsync(new ConfirmDialogViewModel
            {
                Title = localizer["UnexpectedErrorTitle"],
                Message = localizer.Format("UnexpectedErrorFormat", e.Exception.Message, logPath),
                ConfirmText = localizer["ConfirmDialogOk"],
                IsSingleButton = true
            });
        }
        catch (Exception ex)
        {
            ErrorLogger.Log(ex, "显示错误提示");
        }
        finally
        {
            _showingError = false;
        }
    }
}
