using LivePhotoConvert.Core.Media.Thumbnails;
using LivePhotoConvert.Desktop.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace LivePhotoConvert.Desktop.Tests.Harness;

/// <summary>
/// 用产品组合根 <see cref="AppServices.Build"/> 构建桌面服务：设置文件指向临时目录，系统选择器与外壳调用替换为可控的替身。
/// </summary>
public sealed class DesktopTestHost : IDisposable
{
    public DesktopTestHost(Action<IServiceCollection>? configure = null)
    {
        Directory = Path.Combine(Path.GetTempPath(), $"lpc_desktop_{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(Directory);
        SettingsPath = Path.Combine(Directory, "settings.json");

        Provider = AppServices.Build(services =>
        {
            services.AddSingleton(_ => new SettingsStore(SettingsPath));
            // 缩略图缓存写到本用例的临时目录，不污染本机应用数据，也不受其它用例残留影响
            services.AddSingleton(_ => new ThumbnailDiskCache(ThumbnailCacheDirectory, 256L * 1024 * 1024));
            services.AddSingleton<IFilePicker>(FilePicker);
            services.AddSingleton<IShellLauncher>(Shell);
            configure?.Invoke(services);
        });
    }

    public string Directory { get; }

    public string SettingsPath { get; }

    public string ThumbnailCacheDirectory => Path.Combine(Directory, "thumbnails");

    public ServiceProvider Provider { get; }

    public FakeFilePicker FilePicker { get; } = new();

    public FakeShellLauncher Shell { get; } = new();

    public SettingsStore Settings => Get<SettingsStore>();

    public ILocalizer Localizer => Get<ILocalizer>();

    public T Get<T>() where T : notnull => Provider.GetRequiredService<T>();

    public string CreateFile(string fileName, string content = "stub")
    {
        var path = Path.Combine(Directory, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        Provider.Dispose();
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时目录清理失败不影响测试结果
        }
    }
}

/// <summary>返回预设路径的文件选择器替身，并记录每次调用的标题。</summary>
public sealed class FakeFilePicker : IFilePicker
{
    public string? NextResult { get; set; }

    public List<string> Titles { get; } = [];

    public Task<string?> PickFolderAsync(string title, string? suggestedStartLocation = null) => Answer(title);

    public Task<string?> PickFileAsync(string title, IReadOnlyList<FileTypeFilter>? filters = null, string? suggestedStartLocation = null) => Answer(title);

    public Task<string?> SaveFileAsync(string title, string suggestedFileName, IReadOnlyList<FileTypeFilter>? filters = null, string? suggestedStartLocation = null) => Answer(title);

    private Task<string?> Answer(string title)
    {
        Titles.Add(title);
        return Task.FromResult(NextResult);
    }
}

/// <summary>不启动任何外部进程的外壳替身。</summary>
public sealed class FakeShellLauncher : IShellLauncher
{
    public bool Result { get; set; } = true;

    public List<string?> Requests { get; } = [];

    public bool OpenFolder(string? path) => Record(path);

    public bool RevealFile(string? path) => Record(path);

    public bool OpenUri(string? uri) => Record(uri);

    private bool Record(string? target)
    {
        Requests.Add(target);
        return Result;
    }
}
