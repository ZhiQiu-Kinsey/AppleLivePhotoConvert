using LivePhotoConvert.Core.Io;

namespace LivePhotoConvert.Core.External.Tools;

/// <summary>工具安装目录的选择与迁移。</summary>
public static class ToolDirectories
{
    private const string MigrationStagingPrefix = ".migrate-";

    /// <summary>超过这个时间仍未改名就位的迁移暂存目录视为中途退出的残留；远长于一次复制，不会误删另一个实例正在用的目录。</summary>
    private static readonly TimeSpan StaleStagingAge = TimeSpan.FromHours(1);

    /// <summary>本地应用数据下的工具目录：安装版与可更新版的安装位置，也是程序目录不可写时的退路。</summary>
    public static string LocalAppDataToolDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
        "LivePhotoConvert",
        "tools");

    /// <summary>
    /// 工具安装根目录。
    /// </summary>
    /// <param name="programDirectoryIsReplaced">
    /// 程序目录会被更新器整体替换（安装版、可自动更新的便携版）时为 true：一律装到本地应用数据目录，否则每次更新都会丢失已下载的工具。
    /// 为 false 时（解压即用的便携部署、源码运行）优先程序目录下的 tools，无写权限时退到本地应用数据目录。
    /// </param>
    public static string GetWritableToolDirectory(bool programDirectoryIsReplaced = false)
    {
        if (!programDirectoryIsReplaced)
        {
            foreach (var programTools in ProgramToolRoots())
            {
                if (TryEnsureWritable(programTools) is { } writable)
                {
                    return writable;
                }
            }
        }

        var local = LocalAppDataToolDirectory;
        Directory.CreateDirectory(local);
        return local;
    }

    /// <summary>程序目录下的 tools（按进程路径与程序集目录，去重，不检查存在性）。</summary>
    public static IReadOnlyList<string> ProgramToolRoots()
    {
        var roots = new List<string>();
        foreach (var baseDirectory in new[] { Path.GetDirectoryName(Environment.ProcessPath), AppContext.BaseDirectory })
        {
            if (!string.IsNullOrEmpty(baseDirectory))
            {
                roots.Add(Path.GetFullPath(Path.Combine(baseDirectory, "tools")));
            }
        }

        return [.. roots.Distinct(PathComparer)];
    }

    /// <summary>可能存放已安装工具的根目录（去重，不检查存在性）。</summary>
    public static IEnumerable<string> CandidateToolRoots() =>
        ProgramToolRoots().Append(Path.GetFullPath(LocalAppDataToolDirectory)).Distinct(PathComparer);

    /// <summary>
    /// 程序目录由更新器管理时，旧版可能留下工具的位置：程序目录下的 tools，以及上一级目录下的 tools
    /// （把可更新的便携版解压到旧解压版目录时，旧工具位于新程序目录的上一级）。
    /// </summary>
    public static IReadOnlyList<string> LegacyToolRoots()
    {
        var roots = new List<string>(ProgramToolRoots());
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        if (!string.IsNullOrEmpty(parent))
        {
            roots.Add(Path.GetFullPath(Path.Combine(parent, "tools")));
        }

        return [.. roots.Distinct(PathComparer)];
    }

    /// <summary>
    /// 把旧位置的工具复制到新的安装根目录。只复制目标中还没有的条目，复制完整并逐文件核对大小后才改名就位；
    /// 源文件保留不动（程序目录下的副本会随下次更新一起被替换），失败只跳过该条目，不影响程序使用。
    /// 多个实例同时迁移时各用各的暂存目录，改名就位只有一个成功，其余视为已迁移。
    /// </summary>
    /// <param name="legacyRoots">旧的工具目录；不存在或与目标相同的会被忽略</param>
    /// <param name="targetRoot">新的安装根目录</param>
    /// <param name="onError">单个条目迁移失败时回调（条目路径, 异常）</param>
    /// <returns>本次迁移就位的条目名</returns>
    public static IReadOnlyList<string> MigrateLegacyTools(IEnumerable<string> legacyRoots, string targetRoot, Action<string, Exception>? onError = null)
    {
        var target = Path.GetFullPath(targetRoot);
        DeleteStaleStaging(target);
        var migrated = new List<string>();
        foreach (var legacy in legacyRoots.Select(Path.GetFullPath).Distinct(PathComparer))
        {
            if (PathComparer.Equals(legacy, target) || !Directory.Exists(legacy))
            {
                continue;
            }

            IEnumerable<string> entries;
            try
            {
                entries = [.. Directory.EnumerateFileSystemEntries(legacy)];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                onError?.Invoke(legacy, ex);
                continue;
            }

            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);
                // 安装器的暂存 / 备份目录与本方法自己的暂存目录都以点开头，不属于可用的工具
                if (name.StartsWith('.') || Exists(Path.Combine(target, name)))
                {
                    continue;
                }

                if (TryMigrateEntry(entry, target, name, onError))
                {
                    migrated.Add(name);
                }
            }
        }

        return migrated;
    }

    private static bool TryMigrateEntry(string source, string targetRoot, string name, Action<string, Exception>? onError)
    {
        var staging = Path.Combine(targetRoot, $"{MigrationStagingPrefix}{Guid.NewGuid():N}");
        var destination = Path.Combine(targetRoot, name);
        try
        {
            Directory.CreateDirectory(staging);
            var stagedEntry = Path.Combine(staging, name);
            if (Directory.Exists(source))
            {
                CopyDirectory(source, stagedEntry);
                VerifyCopy(source, stagedEntry);
                Directory.Move(stagedEntry, destination);
            }
            else
            {
                File.Copy(source, stagedEntry);
                VerifyFile(source, stagedEntry);
                File.Move(stagedEntry, destination);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 另一个实例或工具安装抢先放好了同名条目：不算失败
            if (!Exists(destination))
            {
                onError?.Invoke(source, ex);
            }

            return false;
        }
        finally
        {
            FileHelper.TryDeleteDirectory(staging);
        }
    }

    private static void DeleteStaleStaging(string targetRoot)
    {
        try
        {
            var staleBefore = DateTime.UtcNow - StaleStagingAge;
            foreach (var staging in Directory.EnumerateDirectories(targetRoot, $"{MigrationStagingPrefix}*"))
            {
                if (Directory.GetLastWriteTimeUtc(staging) < staleBefore)
                {
                    FileHelper.TryDeleteDirectory(staging);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 目标根目录尚不存在或不可读：没有残留可清
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
        }
    }

    /// <summary>复制中途源目录被改动或磁盘写满时，文件清单或大小会对不上。</summary>
    private static void VerifyCopy(string source, string copy)
    {
        var expected = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(source, f)).Order(StringComparer.Ordinal).ToList();
        var actual = Directory.EnumerateFiles(copy, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(copy, f)).Order(StringComparer.Ordinal).ToList();
        if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
        {
            throw new IOException($"复制后的文件清单与 {source} 不一致。");
        }

        foreach (var relative in expected)
        {
            VerifyFile(Path.Combine(source, relative), Path.Combine(copy, relative));
        }
    }

    private static void VerifyFile(string source, string copy)
    {
        if (new FileInfo(source).Length != new FileInfo(copy).Length)
        {
            throw new IOException($"复制后的 {copy} 大小与源文件不一致。");
        }
    }

    private static bool Exists(string path) => Directory.Exists(path) || File.Exists(path);

    private static StringComparer PathComparer => OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    private static string? TryEnsureWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".write_test_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return directory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }
}
