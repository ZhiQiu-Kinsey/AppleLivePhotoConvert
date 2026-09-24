using LivePhotoConvert.Core.Io;

namespace LivePhotoConvert.Core.Pipeline;

/// <summary>
/// 一次批处理的中间文件目录（转码后的封面、换容器后的视频等），释放时整体删除。
/// </summary>
public sealed class TempWorkspace : IDisposable
{
    private const string DirectoryPrefix = "temp-";

    public TempWorkspace()
    {
        Directory = Path.Combine(Root, $"{DirectoryPrefix}{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(Directory);
    }

    public static string Root => Path.Combine(Path.GetTempPath(), "LivePhotoConvert");

    public string Directory { get; }

    public string NewFile(string extension) => Path.Combine(Directory, $"{Guid.NewGuid():N}{extension}");

    public void Dispose() => FileHelper.TryDeleteDirectory(Directory);

    /// <summary>
    /// 删除早于指定时间的工作目录（上次进程异常退出的残留）；传入 <see cref="TimeSpan.Zero"/> 删除全部。
    /// 正在被其它实例使用的目录删除失败时跳过。
    /// </summary>
    public static void DeleteOrphans(TimeSpan olderThan)
    {
        if (!System.IO.Directory.Exists(Root))
        {
            return;
        }

        var threshold = DateTime.UtcNow - olderThan;
        foreach (var directory in System.IO.Directory.EnumerateDirectories(Root, DirectoryPrefix + "*"))
        {
            if (System.IO.Directory.GetCreationTimeUtc(directory) <= threshold)
            {
                FileHelper.TryDeleteDirectory(directory);
            }
        }
    }
}
