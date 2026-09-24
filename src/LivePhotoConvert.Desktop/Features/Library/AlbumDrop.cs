using Avalonia.Input;
using Avalonia.Platform.Storage;

namespace LivePhotoConvert.Desktop.Features.Library;

/// <summary>从拖入的条目中确定要打开的相册目录。</summary>
internal static class AlbumDrop
{
    /// <summary>
    /// 多项时取第一个文件夹；只有文件时取第一个文件所在的目录。只看条目类型，不访问磁盘：拖动经过时会频繁调用。
    /// </summary>
    public static string? Resolve(IEnumerable<(string Path, bool IsFolder)> items)
    {
        string? fileDirectory = null;
        foreach (var (path, isFolder) in items)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            if (isFolder)
            {
                return path;
            }

            fileDirectory ??= Path.GetDirectoryName(path);
        }

        return string.IsNullOrEmpty(fileDirectory) ? null : fileDirectory;
    }

    /// <summary>非本地条目（如浏览器拖出的链接）没有本地路径，忽略。</summary>
    public static string? Resolve(IDataTransfer? data) =>
        data?.TryGetFiles() is { } items
            ? Resolve(items.Select(item => (item.TryGetLocalPath() ?? string.Empty, item is IStorageFolder)))
            : null;
}
