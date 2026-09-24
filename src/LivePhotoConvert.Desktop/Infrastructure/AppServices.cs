using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using LivePhotoConvert.Desktop.Services;
using LivePhotoConvert.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace LivePhotoConvert.Desktop.Infrastructure;

/// <summary>
/// 组合根：全部显式注册为工厂委托，不依赖反射选择构造函数，Native AOT 下行为确定。
/// </summary>
public static class AppServices
{
    /// <param name="configure">在默认注册之后执行，可替换任意服务（例如测试把设置文件指向临时目录）。</param>
    public static ServiceProvider Build(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();

        services.AddSingleton(_ => new SettingsStore(SettingsStore.DefaultFilePath));
        services.AddSingleton<ILocalizer>(_ => new Localizer());
        services.AddSingleton(_ => new ThemeService());
        services.AddSingleton<IDialogService>(_ => new DialogService());
        services.AddSingleton<INavigator>(_ => new Navigator());
        services.AddSingleton<IFilePicker>(_ => new FilePicker(() =>
            (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow));
        services.AddSingleton<IShellLauncher>(_ => new ShellLauncher());
        services.AddSingleton(sp => new WindowPlacementTracker(sp.GetRequiredService<SettingsStore>()));
        services.AddSingleton(sp => new CompletionEffects(
            sp.GetRequiredService<SettingsStore>(),
            sp.GetRequiredService<IShellLauncher>()));
        services.AddSingleton(sp =>
        {
            // 播放宿主本阶段仍是静态实例，只在这里接上设置中的 FFmpeg 路径
            var settings = sp.GetRequiredService<SettingsStore>();
            var host = PlaybackHost.Instance;
            host.CustomFfmpegPathProvider = () => settings.Current.FfmpegPath;
            return host;
        });

        services.AddSingleton(sp => new ReportViewModel(
            sp.GetRequiredService<ILocalizer>(),
            sp.GetRequiredService<IShellLauncher>(),
            sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<INavigator>()));
        services.AddSingleton(sp => new ConvertViewModel(
            sp.GetRequiredService<SettingsStore>(),
            sp.GetRequiredService<ILocalizer>(),
            sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<IFilePicker>(),
            sp.GetRequiredService<IShellLauncher>(),
            sp.GetRequiredService<PlaybackHost>(),
            sp.GetRequiredService<ReportViewModel>(),
            sp.GetRequiredService<INavigator>(),
            sp.GetRequiredService<CompletionEffects>()));
        services.AddSingleton(sp => new StripViewModel(
            sp.GetRequiredService<SettingsStore>(),
            sp.GetRequiredService<ILocalizer>(),
            sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<IFilePicker>(),
            sp.GetRequiredService<IShellLauncher>(),
            sp.GetRequiredService<CompletionEffects>()));
        services.AddSingleton(sp => new ToolsViewModel(
            sp.GetRequiredService<SettingsStore>(),
            sp.GetRequiredService<ILocalizer>(),
            sp.GetRequiredService<IFilePicker>()));
        services.AddSingleton(sp => new SettingsViewModel(
            sp.GetRequiredService<SettingsStore>(),
            sp.GetRequiredService<ILocalizer>(),
            sp.GetRequiredService<ThemeService>(),
            sp.GetRequiredService<IShellLauncher>(),
            sp.GetRequiredService<IDialogService>()));
        services.AddSingleton(sp => new MainWindowViewModel(
            sp.GetRequiredService<INavigator>(),
            sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<ConvertViewModel>(),
            sp.GetRequiredService<StripViewModel>(),
            sp.GetRequiredService<ToolsViewModel>(),
            sp.GetRequiredService<ReportViewModel>(),
            sp.GetRequiredService<SettingsViewModel>()));

        services.AddSingleton(sp => new AppLifetime(
            sp.GetRequiredService<SettingsStore>(),
            sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<ILocalizer>(),
            sp.GetRequiredService<PlaybackHost>(),
            [sp.GetRequiredService<ConvertViewModel>(), sp.GetRequiredService<StripViewModel>()]));

        configure?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = false, ValidateScopes = false });
    }
}
