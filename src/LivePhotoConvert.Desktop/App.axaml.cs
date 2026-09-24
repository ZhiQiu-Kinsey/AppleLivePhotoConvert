using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using LivePhotoConvert.Core.Services;
using LivePhotoConvert.Desktop.Features.Dialogs;
using LivePhotoConvert.Desktop.Features.Shell;
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
        }

        base.OnFrameworkInitializationCompleted();
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
