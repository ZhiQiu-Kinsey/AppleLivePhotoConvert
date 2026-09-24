using System.Buffers;
using System.Formats.Tar;
using System.IO.Compression;
using LivePhotoConvert.Core.Io;
using SharpCompress.Factories;
using SharpCompress.Readers;

namespace LivePhotoConvert.Core.External.Tools;

/// <summary>
/// 把下载包中选中的条目解到目标目录。
/// </summary>
/// <remarks>
/// 每个条目（包括不会解出的条目）都先做路径检查，任何一个不安全就整包拒绝：
/// 被篡改的包即使恶意条目不在选中范围内也不应被信任。
/// </remarks>
/// <summary>强制使用系统 tar 但它不可用或解不了该包（Windows 10 的 tar.exe 不含 liblzma）。</summary>
internal sealed class SystemTarUnsupportedException(string message) : Exception(message);

/// <summary>7z 的解码方式。</summary>
internal enum SevenZipBackend
{
    /// <summary>优先系统 tar，不可用或失败时用 SharpCompress。</summary>
    Auto,
    Managed,
    SystemTar
}

internal static class ToolArchiveExtractor
{
    private const int BufferSize = 81920;

    /// <returns>解出的文件数</returns>
    /// <exception cref="UnsafeArchiveException">包含越界路径、链接或加密条目</exception>
    /// <exception cref="InvalidDataException">压缩包损坏</exception>
    public static async Task<int> ExtractAsync(
        ToolArchiveFormat format,
        string archivePath,
        string destinationDirectory,
        string root,
        IReadOnlyList<string>? include,
        CancellationToken cancellationToken,
        SevenZipBackend sevenZipBackend = SevenZipBackend.Auto)
    {
        Directory.CreateDirectory(destinationDirectory);
        var normalizedRoot = root.Length == 0 ? string.Empty : ToolArchivePath.Normalize(root);
        IReadOnlyList<string>? normalizedInclude = include is null ? null : [.. include.Select(ToolArchivePath.Normalize)];
        var selector = new EntrySelector(destinationDirectory, normalizedRoot, normalizedInclude);
        try
        {
            return format switch
            {
                ToolArchiveFormat.Zip => await ExtractZipAsync(archivePath, selector, cancellationToken),
                ToolArchiveFormat.Tgz => await ExtractTgzAsync(archivePath, selector, cancellationToken),
                ToolArchiveFormat.SevenZip => await ExtractSevenZipAsync(archivePath, selector, sevenZipBackend, cancellationToken),
                _ => throw new NotSupportedException($"不支持的压缩格式 {format}。")
            };
        }
        catch (Exception ex) when (ex is EndOfStreamException
                                   || ex is not (UnsafeArchiveException or SystemTarUnsupportedException or OperationCanceledException or InvalidDataException or IOException or UnauthorizedAccessException))
        {
            // 解压库对损坏数据抛出的异常类型不统一（截断时是 EndOfStreamException），统一归为数据错误；其余 IO 异常是磁盘问题，原样抛出
            throw new InvalidDataException($"压缩包损坏或格式不受支持：{ex.Message}", ex);
        }
    }

    private static async Task<int> ExtractZipAsync(string archivePath, EntrySelector selector, CancellationToken cancellationToken)
    {
        await using var archive = await ZipFile.OpenReadAsync(archivePath, cancellationToken);
        var count = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isDirectory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
            if (IsZipSymbolicLink(entry))
            {
                throw new UnsafeArchiveException($"压缩包含有符号链接：{entry.FullName}");
            }

            if (selector.Select(entry.FullName) is not { } target || isDirectory)
            {
                continue;
            }

            if (entry.IsEncrypted)
            {
                throw new UnsafeArchiveException($"压缩包含有加密条目：{entry.FullName}");
            }

            await using var source = await entry.OpenAsync(cancellationToken);
            await WriteFileAsync(source, target, cancellationToken);
            count++;
        }

        return count;
    }

    private static async Task<int> ExtractTgzAsync(string archivePath, EntrySelector selector, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        await using var reader = new TarReader(gzip);
        var count = 0;
        while (await reader.GetNextEntryAsync(copyData: false, cancellationToken) is { } entry)
        {
            switch (entry.EntryType)
            {
                case TarEntryType.GlobalExtendedAttributes:
                    continue;
                case TarEntryType.Directory:
                    _ = selector.Select(entry.Name);
                    continue;
                case TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile:
                    break;
                default:
                    // 链接与设备文件可把后续写入导向目标目录之外
                    throw new UnsafeArchiveException($"压缩包含有不允许的条目类型 {entry.EntryType}：{entry.Name}");
            }

            if (selector.Select(entry.Name) is not { } target)
            {
                continue;
            }

            if (entry.DataStream is { } data)
            {
                await WriteFileAsync(data, target, cancellationToken);
            }
            else
            {
                await WriteFileAsync(Stream.Null, target, cancellationToken);
            }

            count++;
        }

        return count;
    }

    /// <summary>
    /// 7z：先用 SharpCompress 只解析头部，校验全部条目并确定要解出的文件；
    /// 有支持 7z 的系统 tar（libarchive）时交给它解码，否则用 SharpCompress 解码。
    /// </summary>
    /// <remarks>
    /// SharpCompress 1.0.0 对固实 7z 的每个条目都从固实块开头重新解码（顺序读取器也一样），
    /// 解压耗时随条目数平方增长：heif-enc 包约 25 秒，libarchive 约 1 秒。
    /// Windows 10 自带的 tar.exe 不含 liblzma、解不了 LZMA2，失败时回退到托管实现。
    /// </remarks>
    private static async Task<int> ExtractSevenZipAsync(string archivePath, EntrySelector selector, SevenZipBackend backend, CancellationToken cancellationToken)
    {
        var plan = PlanSevenZip(archivePath, selector);
        if (backend != SevenZipBackend.Managed)
        {
            if (LocateSystemTar() is { } tar && await TryExtractWithSystemTarAsync(tar, archivePath, selector.DestinationDirectory, plan, cancellationToken))
            {
                return plan.Selected.Count;
            }

            if (backend == SevenZipBackend.SystemTar)
            {
                throw new SystemTarUnsupportedException("系统 tar 不可用或无法解压此 7z 包。");
            }
        }

        return await ExtractSevenZipManagedAsync(archivePath, plan, cancellationToken);
    }

    private sealed record SevenZipPlan(IReadOnlyList<(string Entry, string Target)> Selected, HashSet<string> AllFiles);

    private static SevenZipPlan PlanSevenZip(string archivePath, EntrySelector selector)
    {
        using var file = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize);
        using var archive = new SevenZipFactory().Open(file, new ReaderOptions());
        var selected = new List<(string, string)>();
        var allFiles = new HashSet<string>(PathComparer);
        foreach (var entry in archive.Entries)
        {
            if (!string.IsNullOrEmpty(entry.LinkTarget))
            {
                throw new UnsafeArchiveException($"压缩包含有链接：{entry.Key}");
            }

            var key = entry.Key ?? throw new InvalidDataException("7z 条目缺少文件名。");
            var target = selector.Select(key);
            if (entry.IsDirectory)
            {
                continue;
            }

            if (!allFiles.Add(ToolArchivePath.Normalize(key)))
            {
                throw new InvalidDataException($"压缩包含有重复条目：{key}");
            }

            if (target is null)
            {
                continue;
            }

            if (entry.IsEncrypted)
            {
                throw new UnsafeArchiveException($"压缩包含有加密条目：{key}");
            }

            selected.Add((ToolArchivePath.Normalize(key), target));
        }

        return new SevenZipPlan(selected, allFiles);
    }

    /// <summary>
    /// 解到同级临时目录后逐项核对：只接受头部列出的普通文件，出现链接或清单外文件即整包拒绝。
    /// </summary>
    /// <returns>系统 tar 无法处理此包时返回 false，由调用方回退</returns>
    private static async Task<bool> TryExtractWithSystemTarAsync(string tar, string archivePath, string destinationDirectory, SevenZipPlan plan, CancellationToken cancellationToken)
    {
        var raw = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationDirectory)) + $".7z-{Guid.NewGuid():N}";
        Directory.CreateDirectory(raw);
        try
        {
            ProcessResult result;
            try
            {
                result = await ProcessRunner.RunAsync(tar, ["-x", "-f", archivePath, "-C", raw], cancellationToken, SystemTarTimeout);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or TimeoutException or InvalidOperationException)
            {
                return false;
            }

            if (!result.Success)
            {
                return false;
            }

            foreach (var item in new DirectoryInfo(raw).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
            {
                if (item.Attributes.HasFlag(FileAttributes.ReparsePoint) || item.LinkTarget is not null)
                {
                    throw new UnsafeArchiveException($"解出的内容含有链接：{item.Name}");
                }

                if (item is FileInfo && !plan.AllFiles.Contains(Path.GetRelativePath(raw, item.FullName).Replace('\\', '/')))
                {
                    throw new UnsafeArchiveException($"系统 tar 解出了头部未列出的文件：{item.Name}");
                }
            }

            foreach (var (entry, target) in plan.Selected)
            {
                var source = ToolArchivePath.Resolve(raw, entry);
                if (!File.Exists(source))
                {
                    throw new InvalidDataException($"系统 tar 没有解出 {entry}。");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(source, target);
            }

            return true;
        }
        finally
        {
            FileHelper.TryDeleteDirectory(raw);
        }
    }

    private static async Task<int> ExtractSevenZipManagedAsync(string archivePath, SevenZipPlan plan, CancellationToken cancellationToken)
    {
        var targets = plan.Selected.ToDictionary(item => item.Entry, item => item.Target, PathComparer);
        await using var file = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize);
        using var archive = new SevenZipFactory().Open(file, new ReaderOptions());
        using var reader = archive.ExtractAllEntries();
        var count = 0;
        while (reader.MoveToNextEntry())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = reader.Entry;
            if (entry.IsDirectory || entry.Key is not { } key || !targets.TryGetValue(ToolArchivePath.Normalize(key), out var target))
            {
                continue;
            }

            await using var source = reader.OpenEntryStream();
            await WriteFileAsync(source, target, cancellationToken);
            count++;
        }

        return count;
    }

    /// <summary>
    /// 系统自带、支持 7z 的 tar（libarchive）。只认固定的系统路径，避免 PATH 被劫持时执行到别的程序。
    /// </summary>
    internal static string? LocateSystemTar()
    {
        string candidate;
        if (OperatingSystem.IsWindows())
        {
            candidate = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");
        }
        else if (OperatingSystem.IsMacOS())
        {
            candidate = "/usr/bin/tar";
        }
        else
        {
            candidate = "/usr/bin/bsdtar";
        }

        return File.Exists(candidate) ? candidate : null;
    }

    private static readonly TimeSpan SystemTarTimeout = TimeSpan.FromMinutes(5);

    private static StringComparer PathComparer => OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    /// <summary>zip 的 Unix 外部属性高 16 位是 st_mode，S_IFLNK 表示符号链接。</summary>
    private static bool IsZipSymbolicLink(ZipArchiveEntry entry)
    {
        const int FileTypeMask = 0xF000;
        const int SymbolicLink = 0xA000;
        return ((entry.ExternalAttributes >> 16) & FileTypeMask) == SymbolicLink;
    }

    private static async Task WriteFileAsync(Stream source, string target, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        // CreateNew：同名条目重复出现说明包有问题，不能静默覆盖先解出的文件
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, useAsync: true);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private sealed class EntrySelector(string destinationDirectory, string root, IReadOnlyList<string>? include)
    {
        public string DestinationDirectory => destinationDirectory;

        /// <summary>检查条目路径（所有条目都检查，不只是被选中的）；选中时返回目标完整路径。</summary>
        public string? Select(string entryName) =>
            ToolArchivePath.Select(ToolArchivePath.Normalize(entryName), root, include) is { } relative
                ? ToolArchivePath.Resolve(destinationDirectory, relative)
                : null;
    }
}
