using System.Collections.Concurrent;
using LivePhotoConvert.Core.Io;

namespace LivePhotoConvert.Core.Pipeline;

/// <summary>
/// 一次批处理的中间文件目录（转码后的封面、换容器后的视频等），释放时整体删除。
/// </summary>
public sealed class TempWorkspace : IDisposable
{
    private const string DirectoryPrefix = "temp-";

    /// <summary>
    /// 本进程创建且尚未删除的工作目录。退出清理只动这些目录：
    /// 所有实例共用同一个根目录，按时间或通配删除会删掉其它正在运行的实例的工作目录。
    /// </summary>
    private static readonly ConcurrentDictionary<string, byte> Owned = new(StringComparer.Ordinal);

    public TempWorkspace()
    {
        Directory = Path.Combine(Root, $"{DirectoryPrefix}{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(Directory);
        Owned.TryAdd(Directory, 0);
    }

    public static string Root => Path.Combine(Path.GetTempPath(), "LivePhotoConvert");

    public string Directory { get; }

    public string NewFile(string extension) => Path.Combine(Directory, $"{Guid.NewGuid():N}{extension}");

    public void Dispose() => TryDeleteOwned(Directory);

    /// <summary>
    /// 删除本进程创建、尚未随批处理释放的工作目录；供应用退出时调用。删除失败的目录留给下次启动的孤儿清理。
    /// </summary>
    public static void DeleteOwned()
    {
        foreach (var directory in Owned.Keys)
        {
            TryDeleteOwned(directory);
        }
    }

    /// <summary>
    /// 删除早于指定时间的工作目录（上次进程异常退出的残留）。
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
            if (!Owned.ContainsKey(directory) && System.IO.Directory.GetCreationTimeUtc(directory) <= threshold)
            {
                FileHelper.TryDeleteDirectory(directory);
            }
        }
    }

    private static void TryDeleteOwned(string directory)
    {
        FileHelper.TryDeleteDirectory(directory);
        if (!System.IO.Directory.Exists(directory))
        {
            Owned.TryRemove(directory, out _);
        }
    }
}
