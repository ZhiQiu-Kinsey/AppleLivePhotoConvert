using Avalonia.Controls;
using Avalonia.Platform.Storage;
using LivePhotoConvert.Core.Services;

namespace LivePhotoConvert.Desktop.Infrastructure;

/// <summary>文件类型筛选项，模式如 "*.jpg"。</summary>
public sealed record FileTypeFilter(string Name, IReadOnlyList<string> Patterns);

/// <summary>系统文件与文件夹选择器；用户取消或选择器不可用时返回 null。</summary>
public interface IFilePicker
{
    Task<string?> PickFolderAsync(string title, string? suggestedStartLocation = null);

    Task<string?> PickFileAsync(string title, IReadOnlyList<FileTypeFilter>? filters = null, string? suggestedStartLocation = null);

    Task<string?> SaveFileAsync(string title, string suggestedFileName, IReadOnlyList<FileTypeFilter>? filters = null, string? suggestedStartLocation = null);
}

/// <summary>
/// 通过当前顶层窗口的 StorageProvider 打开系统选择器。
/// </summary>
public sealed class FilePicker(Func<TopLevel?> topLevelProvider) : IFilePicker
{
    public Task<string?> PickFolderAsync(string title, string? suggestedStartLocation = null) => TryAsync(async () =>
    {
        if (topLevelProvider()?.StorageProvider is not { CanPickFolder: true } provider)
        {
            return null;
        }

        var folders = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = await TryGetFolderAsync(provider, suggestedStartLocation)
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }, "选择文件夹");

    public Task<string?> PickFileAsync(string title, IReadOnlyList<FileTypeFilter>? filters = null, string? suggestedStartLocation = null) => TryAsync(async () =>
    {
        if (topLevelProvider()?.StorageProvider is not { CanOpen: true } provider)
        {
            return null;
        }

        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = ToPickerTypes(filters),
            SuggestedStartLocation = await TryGetFolderAsync(provider, suggestedStartLocation)
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }, "选择文件");

    public Task<string?> SaveFileAsync(string title, string suggestedFileName, IReadOnlyList<FileTypeFilter>? filters = null, string? suggestedStartLocation = null) => TryAsync(async () =>
    {
        if (topLevelProvider()?.StorageProvider is not { CanSave: true } provider)
        {
            return null;
        }

        var file = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedFileName,
            FileTypeChoices = ToPickerTypes(filters),
            ShowOverwritePrompt = true,
            SuggestedStartLocation = await TryGetFolderAsync(provider, suggestedStartLocation)
        });
        return file?.TryGetLocalPath();
    }, "选择保存位置");

    /// <summary>选择器操作失败（如用户取消底层对话框抛出的异常）时记录日志并返回 null，不让异常穿透到调用方。</summary>
    private static async Task<string?> TryAsync(Func<Task<string?>> action, string context)
    {
        try
        {
            return await action();
        }
        catch (Exception ex)
        {
            ErrorLogger.Log(ex, context);
            return null;
        }
    }

    private static List<FilePickerFileType>? ToPickerTypes(IReadOnlyList<FileTypeFilter>? filters) =>
        filters is { Count: > 0 }
            ? [.. filters.Select(f => new FilePickerFileType(f.Name) { Patterns = [.. f.Patterns] })]
            : null;

    /// <summary>起始位置不存在时交给系统默认位置，而不是让整个选择器失败。</summary>
    private static async Task<IStorageFolder?> TryGetFolderAsync(IStorageProvider provider, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var folder = File.Exists(path) ? Path.GetDirectoryName(path) : path;
            return !string.IsNullOrEmpty(folder) && Directory.Exists(folder)
                ? await provider.TryGetFolderFromPathAsync(new Uri(Path.GetFullPath(folder)))
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }
}
