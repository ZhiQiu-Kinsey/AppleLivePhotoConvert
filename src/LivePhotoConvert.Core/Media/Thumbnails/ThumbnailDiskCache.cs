using System.Diagnostics.CodeAnalysis;
using System.IO.Enumeration;

namespace LivePhotoConvert.Core.Media.Thumbnails;

/// <summary>容量清理的结果。</summary>
public readonly record struct ThumbnailCacheTrimResult(long BytesBefore, long BytesAfter, int FilesDeleted);

/// <summary>
/// 缩略图磁盘缓存：一键一个 JPEG 文件，原子写入，按访问时间做容量淘汰。可被多个线程同时读写。
/// </summary>
public sealed class ThumbnailDiskCache
{
    private const string EntryExtension = ".jpg";
    private const string TempExtension = ".tmp";
    private const double TrimTargetRatio = 0.8;

    /// <summary>超过该时长的临时文件视为崩溃残留，清理时删除；正在写入的临时文件不会存在这么久。</summary>
    private static readonly TimeSpan StaleTempAge = TimeSpan.FromHours(1);

    private static readonly EnumerationOptions EnumerateAll = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    private readonly TimeProvider _time;
    private readonly Lock _trimLock = new();
    private long _approximateBytes = -1;

    public ThumbnailDiskCache(string root, long capacityBytes, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
        CapacityBytes = capacityBytes;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>默认缓存目录（本机应用数据），与启动时整体删除的遗留目录 cache/thumbs 分开。</summary>
    public static string DefaultRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify),
        "LivePhotoConvert", "cache", "thumbnails");

    public string Root { get; }

    /// <summary>容量上限（字节）。超出后删到上限的 80%，留出余量避免每次写入都触发清理。</summary>
    public long CapacityBytes
    {
        get => Volatile.Read(ref field);
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            Volatile.Write(ref field, value);
        }
    }

    /// <summary>
    /// 命中时刷新修改时间作为访问时间的最小间隔：NTFS 默认不更新访问时间，
    /// 而每次命中都写元数据会让滚动时的读缓存变成写操作。
    /// </summary>
    public TimeSpan TouchInterval { get; init; } = TimeSpan.FromHours(1);

    /// <summary>估算的缓存总字节数；尚未统计时为 -1。</summary>
    public long ApproximateSizeBytes => Interlocked.Read(ref _approximateBytes);

    public string GetPath(ThumbnailKey key) => Path.Combine(Root, key.RelativePath);

    /// <summary>
    /// 命中时返回文件路径。文件之后可能被清理删除，调用方应把打开失败当作未命中。
    /// </summary>
    public bool TryGet(ThumbnailKey key, [NotNullWhen(true)] out string? path)
    {
        path = GetPath(key);
        DateTime lastWrite;
        try
        {
            lastWrite = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            path = null;
            return false;
        }

        // 文件不存在时返回 1601-01-01，省掉一次 Exists
        if (lastWrite == DateTime.FromFileTimeUtc(0))
        {
            path = null;
            return false;
        }

        var now = _time.GetUtcNow().UtcDateTime;
        if (now - lastWrite >= TouchInterval)
        {
            try
            {
                File.SetLastWriteTimeUtc(path, now);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 刷新失败只影响淘汰顺序
            }
        }

        return true;
    }

    /// <summary>
    /// 原子写入：先写同目录临时文件，再改名覆盖。并发写同一键时最后一次改名胜出，读者只会看到完整文件。
    /// 写入失败（磁盘满、无权限）返回 <c>false</c>，不抛异常。
    /// </summary>
    public bool TryPut(ThumbnailKey key, ReadOnlySpan<byte> jpeg)
    {
        var path = GetPath(key);
        var temp = $"{path}.{Guid.NewGuid():N}{TempExtension}";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.None))
            {
                stream.Write(jpeg);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            // Windows 上目标正被读取或另一写入者同时改名时会失败；同键内容等价，已存在即视为成功
            return File.Exists(path);
        }

        if (Interlocked.Read(ref _approximateBytes) < 0 ||
            Interlocked.Add(ref _approximateBytes, jpeg.Length) > CapacityBytes)
        {
            TrimIfIdle();
        }

        return true;
    }

    /// <summary>
    /// 统计占用并在超出容量时按访问时间从旧到新删除，直到不超过容量的 80%。同时清理崩溃残留的临时文件。
    /// </summary>
    public ThumbnailCacheTrimResult Trim()
    {
        lock (_trimLock)
        {
            return TrimCore();
        }
    }

    /// <summary>删除全部缓存文件；正在被读取而删除失败的文件保留。</summary>
    public void Clear()
    {
        lock (_trimLock)
        {
            long remaining = 0;
            foreach (var entry in Enumerate())
            {
                if (!TryDelete(entry.Path) && !entry.IsTemp)
                {
                    remaining += entry.Length;
                }
            }

            Interlocked.Exchange(ref _approximateBytes, remaining);
        }
    }

    /// <summary>写入路径上的清理：已有线程在清理时直接跳过，不阻塞生成。</summary>
    private void TrimIfIdle()
    {
        if (!_trimLock.TryEnter())
        {
            return;
        }

        try
        {
            TrimCore();
        }
        finally
        {
            _trimLock.Exit();
        }
    }

    private ThumbnailCacheTrimResult TrimCore()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        List<Entry> entries = [];
        long total = 0;
        foreach (var entry in Enumerate())
        {
            if (entry.IsTemp)
            {
                if (now - entry.LastWriteTimeUtc > StaleTempAge)
                {
                    TryDelete(entry.Path);
                }

                continue;
            }

            entries.Add(entry);
            total += entry.Length;
        }

        var before = total;
        var deleted = 0;
        if (total > CapacityBytes)
        {
            var target = (long)(CapacityBytes * TrimTargetRatio);
            entries.Sort(static (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
            foreach (var entry in entries)
            {
                if (total <= target)
                {
                    break;
                }

                if (TryDelete(entry.Path))
                {
                    total -= entry.Length;
                    deleted++;
                }
            }
        }

        Interlocked.Exchange(ref _approximateBytes, total);
        return new ThumbnailCacheTrimResult(before, total, deleted);
    }

    private IEnumerable<Entry> Enumerate()
    {
        if (!Directory.Exists(Root))
        {
            return [];
        }

        return new FileSystemEnumerable<Entry>(
            Root,
            static (ref FileSystemEntry e) => new Entry(
                e.ToFullPath(),
                e.Length,
                e.LastWriteTimeUtc.UtcDateTime,
                e.FileName.EndsWith(TempExtension, StringComparison.OrdinalIgnoreCase)),
            EnumerateAll)
        {
            ShouldIncludePredicate = static (ref FileSystemEntry e) =>
                !e.IsDirectory &&
                (e.FileName.EndsWith(EntryExtension, StringComparison.OrdinalIgnoreCase) ||
                 e.FileName.EndsWith(TempExtension, StringComparison.OrdinalIgnoreCase)),
        };
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private readonly record struct Entry(string Path, long Length, DateTime LastWriteTimeUtc, bool IsTemp);
}
