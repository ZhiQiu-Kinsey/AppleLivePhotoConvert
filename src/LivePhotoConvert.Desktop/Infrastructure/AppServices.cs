using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using LivePhotoConvert.Core.Media.Thumbnails;
using LivePhotoConvert.Core.Platform;
using LivePhotoConvert.Desktop.Features.Library;
using LivePhotoConvert.Desktop.Features.Library.Thumbnails;
using LivePhotoConvert.Desktop.Features.Settings;
using LivePhotoConvert.Desktop.Features.Shell;
using LivePhotoConvert.Desktop.Features.Tasks;
using LivePhotoConvert.Desktop.Features.Tools;
using LivePhotoConvert.Desktop.Services;
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

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IConversionEngines>(_ => ExternalToolEngines.Instance);
        services.AddSingleton<IConversionRunner>(sp => new ConversionRunner(sp.GetRequiredService<IConversionEngines>()));
        services.AddSingleton(sp => new TaskCenter(
            sp.GetRequiredService<IConversionRunner>(),
            sp.GetRequiredService<ILocalizer>(),
            sp.GetRequiredService<INavigator>(),
            sp.GetRequiredService<CompletionEffects>(),
            sp.GetRequiredService<IShellLauncher>(),
            sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<IFilePicker>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new TasksViewModel(
            sp.GetRequiredService<TaskCenter>(),
            sp.GetRequiredService<INavigator>()));
        services.AddSingleton<IToolAvailability>(_ => ToolAvailability.Instance);
        services.AddSingleton<IStripEstimator>(sp => new StripEstimator(sp.GetRequiredService<IConversionEngines>()));
        services.AddSingleton<IDiskSpaceGuard>(_ => DiskSpaceGuard.Instance);
        services.AddSingleton(sp => new ThumbnailDiskCache(
            ThumbnailDiskCache.DefaultRoot,
            sp.GetRequiredService<SettingsStore>().Current.Gallery.ThumbnailDiskCacheBytes));
        services.AddSingleton(sp => new ThumbnailGenerator(
            sp.GetRequiredService<ThumbnailDiskCache>(),
            WindowsShellThumbnailSource.TryCreate()));
        services.AddSingleton<IThumbnailPipeline>(sp => new ThumbnailPipeline(
            sp.GetRequiredService<ThumbnailGenerator>(),
            sp.GetRequiredService<SettingsStore>().Current.Gallery.ThumbnailBudgetBytes));
        services.AddSingleton(sp => new LibraryViewModel(
            sp.GetRequiredService<SettingsStore>(),
            sp.GetRequiredService<ILocalizer>(),
            sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<IFilePicker>(),
            sp.GetRequiredService<PlaybackHost>(),
            sp.GetRequiredService<IThumbnailPipeline>()));
        services.AddSingleton(sp => new InspectorViewModel(
            sp.GetRequiredService<LibraryViewModel>(),
            sp.GetRequiredService<SettingsStore>(),
            sp.GetRequiredService<ILocalizer>(),
            sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<IFilePicker>(),
            sp.GetRequiredService<IShellLauncher>(),
            sp.GetRequiredService<TaskCenter>(),
            sp.GetRequiredService<INavigator>(),
            sp.GetRequiredService<IToolAvailability>(),
            sp.GetRequiredService<IStripEstimator>(),
            sp.GetRequiredService<IDiskSpaceGuard>(),
            sp.GetRequiredService<TimeProvider>()));
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
        services.AddSingleton(sp => new ShellViewModel(
            sp.GetRequiredService<INavigator>(),
            sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<LibraryViewModel>(),
            sp.GetRequiredService<InspectorViewModel>(),
            sp.GetRequiredService<TasksViewModel>(),
            sp.GetRequiredService<ToolsViewModel>(),
            sp.GetRequiredService<SettingsViewModel>()));

        services.AddSingleton(sp => new AppLifetime(
            sp.GetRequiredService<SettingsStore>(),
            sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<ILocalizer>(),
            sp.GetRequiredService<PlaybackHost>(),
            [sp.GetRequiredService<TaskCenter>()]));

        configure?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = false, ValidateScopes = false });
    }
}
