using System.Globalization;
using LivePhotoConvert.Core.Io;

namespace LivePhotoConvert.Core.Pipeline;

/// <summary>
/// 输出目录已存在同名文件时的处理方式。
/// </summary>
public enum ConflictPolicy
{
    /// <summary>追加 _1、_2 序号，不动已有文件。</summary>
    AppendIndex,

    /// <summary>覆盖已有文件；本批次的源文件与本批次已写出的文件始终受保护。</summary>
    Overwrite
}

/// <summary>
/// 把暂存文件原子地放到最终位置。
/// </summary>
/// <remarks>
/// 所有输出先写到目标目录内以 <see cref="StagingPrefix"/> 开头的暂存文件，校验通过后再通过同卷重命名落盘，
/// 因此目标位置要么是完整的新文件，要么保持原样；写到一半的文件不会以最终文件名出现。
/// 同一批次内的文件名由本类统一分配，并发任务不会写到同一路径，也不会覆盖本批次的任何源文件。
/// </remarks>
public sealed class OutputCommitter
{
    public const string StagingPrefix = "~lpc-";

    /// <summary>就地替换时原文件的临时备份后缀；替换成功后立即删除。</summary>
    public const string BackupSuffix = ".livephoto_backup";

    private const int MaxIndex = 100_000;

    /// <summary>暂存文件名中创建时间的标记：残留判断看文件名里的创建时间，不受写入的文件时间影响。</summary>
    private const char CreatedMarker = 't';

    private readonly ConflictPolicy _policy;
    private readonly HashSet<string> _protected;
    private readonly HashSet<string> _claimed;
    private readonly Lock _gate = new();

    public OutputCommitter(ConflictPolicy policy, IEnumerable<string> protectedPaths)
    {
        _policy = policy;
        _protected = new HashSet<string>(protectedPaths.Select(Path.GetFullPath), PathComparer);
        _claimed = new HashSet<string>(PathComparer);
    }

    /// <summary>
    /// 一律忽略大小写：macOS 与 exFAT/NTFS 卷在任何系统上都不区分大小写，而卷的属性无法可靠探测；
    /// 把只差大小写的路径误判为同一文件只会多追加一个序号，反过来却可能覆盖源文件。
    /// </summary>
    public static StringComparer PathComparer { get; } = StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// 在目标目录中生成暂存路径，保留扩展名以便外部工具按扩展名识别格式。
    /// </summary>
    public static string CreateStagingPath(string directory, string extension)
    {
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, NewStagingName(extension));
    }

    /// <summary>
    /// 删除目录中早于指定时间的暂存文件（上次进程异常退出的残留）。
    /// </summary>
    /// <remarks>
    /// 按文件名中记录的创建时间判断：暂存文件可能被写入源文件的旧时间，按修改时间会误删其它批次正在使用的文件。
    /// 文件名中没有创建时间的（旧版本残留）才按修改时间判断。
    /// </remarks>
    public static void DeleteStaleStagingFiles(string directory, TimeSpan olderThan)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        var threshold = DateTime.UtcNow - olderThan;
        foreach (var file in Directory.EnumerateFiles(directory, StagingPrefix + "*"))
        {
            var created = TryParseCreatedTime(Path.GetFileName(file)) ?? File.GetLastWriteTimeUtc(file);
            if (created < threshold)
            {
                FileHelper.TryDeleteFile(file);
            }
        }
    }

    /// <summary>
    /// 把暂存文件落盘为 <paramref name="fileName"/>，返回最终路径。
    /// </summary>
    /// <param name="timestamp">落盘后写入最终文件的时间；不在暂存阶段写入，以免暂存文件被当成过期残留</param>
    public string Commit(string stagingPath, string directory, string fileName, FileTimestamp? timestamp = null) =>
        CommitGroup([new StagedFile(stagingPath, fileName)], directory, timestamp)[0];

    /// <summary>
    /// 把一组暂存文件以相同序号后缀落盘（如照片与视频成对），任一文件失败时整组回滚。
    /// </summary>
    /// <remarks>
    /// 覆盖模式下单个文件的重命名覆盖本身是原子的；成组时后一项失败需要还原前一项覆盖掉的旧文件，
    /// 因此先把旧文件改名为同目录的暂存备份，整组成功后再删除。
    /// </remarks>
    public IReadOnlyList<string> CommitGroup(IReadOnlyList<StagedFile> files, string directory, FileTimestamp? timestamp = null)
    {
        ArgumentOutOfRangeException.ThrowIfZero(files.Count);
        for (var index = 0; index < MaxIndex; index++)
        {
            var targets = files.Select(file => Path.GetFullPath(Path.Combine(directory, WithIndex(file.FileName, index)))).ToArray();
            var overwrite = _policy == ConflictPolicy.Overwrite && index == 0;
            if (!TryClaim(targets, overwrite))
            {
                continue;
            }

            var moved = new List<(string From, string To)>(files.Count);
            var backups = new List<(string Original, string Backup)>();
            try
            {
                for (var i = 0; i < files.Count; i++)
                {
                    if (overwrite && files.Count > 1 && File.Exists(targets[i]))
                    {
                        var backup = Path.Combine(Path.GetDirectoryName(targets[i])!, NewStagingName(Path.GetExtension(targets[i])));
                        File.Move(targets[i], backup);
                        backups.Add((targets[i], backup));
                    }

                    File.Move(files[i].StagingPath, targets[i], overwrite);
                    moved.Add((files[i].StagingPath, targets[i]));
                }
            }
            catch (IOException) when (!overwrite && File.Exists(targets[moved.Count]) && File.Exists(files[moved.Count].StagingPath))
            {
                // 其它进程在认领之后抢先创建了同名文件：撤回已移动的文件，换下一个序号
                Rollback(moved);
                continue;
            }
            catch
            {
                Rollback(moved);
                RestoreBackups(backups);
                throw;
            }

            foreach (var (_, backup) in backups)
            {
                FileHelper.TryDeleteFile(backup);
            }

            timestamp?.ApplyTo(targets);
            return targets;
        }

        throw new IOException($"无法为 {files[0].FileName} 找到可用的输出文件名。");
    }

    /// <summary>
    /// 用暂存文件替换源文件。扩展名不变时通过 <see cref="File.Replace(string, string, string?)"/> 原子替换；
    /// 扩展名改变时先落盘新文件（不覆盖任何已有文件），再删除源文件，删除失败则撤销新文件。
    /// </summary>
    /// <param name="stagingPath">与源文件位于同一目录的暂存文件</param>
    /// <param name="sourcePath">被替换的源文件</param>
    /// <param name="extension">新文件的扩展名</param>
    /// <param name="timestamp">替换完成后写入最终文件的时间</param>
    /// <returns>替换后的文件路径</returns>
    public string ReplaceSource(string stagingPath, string sourcePath, string extension, FileTimestamp? timestamp = null)
    {
        var fullSource = Path.GetFullPath(sourcePath);
        var directory = Path.GetDirectoryName(fullSource)!;
        var target = Path.ChangeExtension(fullSource, extension);

        if (PathComparer.Equals(target, fullSource))
        {
            var backup = fullSource + BackupSuffix;
            File.Replace(stagingPath, fullSource, backup, ignoreMetadataErrors: true);
            FileHelper.TryDeleteFile(backup);
            timestamp?.ApplyTo(fullSource);
            return fullSource;
        }

        var committed = CommitGroup([new StagedFile(stagingPath, Path.GetFileName(target))], directory, timestamp);
        try
        {
            File.Delete(fullSource);
        }
        catch
        {
            FileHelper.TryDeleteFile(committed[0]);
            throw;
        }

        return committed[0];
    }

    /// <summary>
    /// 生成暂存文件名：<c>~lpc-t{创建时间 ticks}-{guid}{扩展名}</c>。
    /// </summary>
    internal static string NewStagingName(string extension) =>
        $"{StagingPrefix}{CreatedMarker}{DateTime.UtcNow.Ticks:x16}-{Guid.NewGuid():N}{extension}";

    internal static DateTime? TryParseCreatedTime(string fileName)
    {
        var name = fileName.AsSpan();
        if (!name.StartsWith(StagingPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        name = name[StagingPrefix.Length..];
        return name.Length > 18 && name[0] == CreatedMarker && name[17] == '-'
               && long.TryParse(name[1..17], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var ticks)
               && ticks >= 0 && ticks <= DateTime.MaxValue.Ticks
            ? new DateTime(ticks, DateTimeKind.Utc)
            : null;
    }

    private bool TryClaim(string[] targets, bool overwrite)
    {
        lock (_gate)
        {
            foreach (var target in targets)
            {
                if (_claimed.Contains(target) || _protected.Contains(target) || Directory.Exists(target) || (!overwrite && File.Exists(target)))
                {
                    return false;
                }
            }

            foreach (var target in targets)
            {
                _claimed.Add(target);
            }

            return true;
        }
    }

    private static void Rollback(List<(string From, string To)> moved)
    {
        foreach (var (from, to) in moved)
        {
            try
            {
                File.Move(to, from);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 回滚失败时保留已落盘的文件，至少不丢数据
            }
        }
    }

    /// <summary>
    /// 把覆盖前备份的旧文件还原到原名；此时新文件已退回暂存路径，即使没退回也以用户原有文件为准。
    /// </summary>
    private static void RestoreBackups(List<(string Original, string Backup)> backups)
    {
        foreach (var (original, backup) in backups)
        {
            try
            {
                File.Move(backup, original, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 还原失败时备份文件仍保留在同一目录
            }
        }
    }

    private static string WithIndex(string fileName, int index) =>
        index == 0 ? fileName : $"{Path.GetFileNameWithoutExtension(fileName)}_{index}{Path.GetExtension(fileName)}";
}

/// <summary>
/// 待落盘的暂存文件及其期望文件名。
/// </summary>
public readonly record struct StagedFile(string StagingPath, string FileName);
