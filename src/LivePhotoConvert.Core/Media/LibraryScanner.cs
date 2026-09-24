using System.Buffers.Binary;
using System.Collections.Frozen;
using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using LivePhotoConvert.Core.Metadata;
using LivePhotoConvert.Core.Pairing;
using LivePhotoConvert.Core.Pipeline;

namespace LivePhotoConvert.Core.Media;

/// <summary>
/// 图库统一扫描：一次遍历产出带类型标记的条目（苹果实况对 / 安卓动态照片 / 普通照片），切换动作只需筛选。
/// </summary>
/// <remarks>
/// 只读文件头部，不调用外部工具。HEIC 动态照片中视频直接追加在尾部、只能靠 XMP 定位的少数情形，
/// 由 <see cref="EnrichHeicAsync"/> 借助 ExifTool 延迟补全。
/// </remarks>
public static class LibraryScanner
{
    /// <summary>
    /// 分析阶段的并行度：以小块随机读为主，随核数增长但设上限，避免机械盘与网络盘因寻道过多反而变慢。
    /// </summary>
    public static int DefaultParallelism { get; } = Math.Clamp(Environment.ProcessorCount, 2, 8);

    private const int ProgressInterval = 256;

    private const uint Ftyp = 0x66747970; // "ftyp"

    /// <summary>顶层 box 数量上限，与 <see cref="MotionPhotoLayout"/> 一致。</summary>
    private const int MaxTopLevelBoxes = 1024;

    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> PhotoExtensions =
        MediaFileTypes.PhotoExtensions.GetAlternateLookup<ReadOnlySpan<char>>();

    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> VideoExtensions =
        MediaFileTypes.VideoExtensions.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>
    /// 扫描目录（含子目录）。无权限的子目录跳过并计数，不抛出异常。
    /// </summary>
    /// <exception cref="DirectoryNotFoundException">根目录不存在</exception>
    public static Task<LibraryScanResult> ScanAsync(string root, IProgress<LibraryScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        return Task.Run(() => Scan(root, progress, DefaultParallelism, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// 找出尾部带有未归属数据、可能是「视频直接追加在末尾」的 HEIC 普通照片，读取 XMP 后重新定位视频，
    /// 逐条返回升级为 <see cref="LibraryItemKind.MotionPhoto"/> 的条目。读取 XMP 失败（如未安装 ExifTool）的文件直接跳过。
    /// </summary>
    public static async IAsyncEnumerable<LibraryItem> EnrichHeicAsync(
        IReadOnlyList<LibraryItem> stills,
        IMetadataService metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stills);
        ArgumentNullException.ThrowIfNull(metadata);

        // 调用方多在 UI 线程迭代，文件读取一律回到线程池执行
        var candidates = await Task.Run(() => FindTrailerCandidates(stills, cancellationToken), cancellationToken).ConfigureAwait(false);
        foreach (var item in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? xmp;
            try
            {
                xmp = await metadata.ReadXmpAsync(item.Photo.Path, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                continue;
            }

            if (xmp is not null && MotionPhotoLayout.Inspect(item.Photo.Path, xmp) is { Video: { } video } layout)
            {
                yield return item with { Kind = LibraryItemKind.MotionPhoto, Embedded = video, HasGainMap = layout.HasGainMap };
            }
        }
    }

    internal static LibraryScanResult Scan(string root, IProgress<LibraryScanProgress>? progress, int parallelism, CancellationToken cancellationToken)
    {
        var fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot))
        {
            throw new DirectoryNotFoundException($"目录不存在：{fullRoot}");
        }

        var files = new List<LibraryFile>();
        int totalFiles, ignoredFiles, inaccessible;
        using (var enumerator = new MediaFileEnumerator(fullRoot))
        {
            while (enumerator.MoveNext())
            {
                cancellationToken.ThrowIfCancellationRequested();
                files.Add(enumerator.Current);
                if (files.Count % ProgressInterval == 0)
                {
                    progress?.Report(new LibraryScanProgress(enumerator.FilesSeen, 0, 0));
                }
            }

            totalFiles = enumerator.FilesSeen;
            ignoredFiles = enumerator.FilesIgnored;
            inaccessible = enumerator.Errors;
        }

        // 枚举顺序取决于文件系统；先排序，使配对分组与结果在不同机器上一致
        files.Sort(static (a, b) => string.CompareOrdinal(a.Path, b.Path));
        var byPath = files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        var pairing = MediaPairMatcher.Match(files.Select(file => file.Path));

        var pending = new List<PendingItem>(files.Count);
        foreach (var group in pairing.Pairs.GroupBy(pair => pair.GroupKey, StringComparer.OrdinalIgnoreCase))
        {
            MediaPair[] candidates = [.. group];
            pending.Add(new PendingItem(byPath[candidates[0].PhotoPath], candidates));
        }

        foreach (var photo in pairing.PhotosWithoutVideo)
        {
            pending.Add(new PendingItem(byPath[photo], []));
        }

        var items = new LibraryItem[pending.Count];
        var analyzed = 0;
        Parallel.For(0, pending.Count, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken }, i =>
        {
            items[i] = pending[i].Candidates.Count > 0 ? AnalyzePair(pending[i].Candidates, byPath) : AnalyzeSingle(pending[i].Photo);
            var done = Interlocked.Increment(ref analyzed);
            if (done % ProgressInterval == 0)
            {
                progress?.Report(new LibraryScanProgress(totalFiles, done, pending.Count));
            }
        });

        progress?.Report(new LibraryScanProgress(totalFiles, pending.Count, pending.Count));
        Array.Sort(items, static (a, b) => string.CompareOrdinal(a.Photo.Path, b.Photo.Path));
        return new LibraryScanResult(items, totalFiles, inaccessible) { IgnoredFiles = ignoredFiles };
    }

    /// <summary>
    /// 合成阶段对同组候选逐个执行 <see cref="PairValidator"/> 并取第一个通过的；扫描用头部信息构造同样的输入、按同样规则选定，
    /// 使图库的「待裁决」与任务的跳过结论一致。
    /// </summary>
    private static LibraryItem AnalyzePair(IReadOnlyList<MediaPair> candidates, Dictionary<string, LibraryFile> byPath)
    {
        var headers = new Dictionary<string, ImageHeader?>(StringComparer.Ordinal);
        var videos = new Dictionary<string, MediaMetadata>(StringComparer.Ordinal);
        var results = new PairValidationResult[candidates.Count];
        var selected = -1;
        for (var i = 0; i < candidates.Count; i++)
        {
            var (photoPath, videoPath) = (candidates[i].PhotoPath, candidates[i].VideoPath);
            if (!headers.TryGetValue(photoPath, out var header))
            {
                headers[photoPath] = header = ReadHeader(photoPath);
            }

            if (!videos.TryGetValue(videoPath, out var video))
            {
                videos[videoPath] = video = QuickTimeHeader.Read(videoPath).ToMetadata(videoPath);
            }

            results[i] = PairValidator.Validate(ToMetadata(photoPath, header), video);
            if (results[i].IsAccepted)
            {
                selected = i;
                break;
            }
        }

        var validation = selected >= 0
            ? results[selected]
            : PairValidationResult.Reject([.. results.SelectMany(result => result.Causes).Distinct()]);
        var pair = candidates[Math.Max(selected, 0)];
        var photo = byPath[pair.PhotoPath];
        var photoHeader = headers[pair.PhotoPath];
        var (captureTime, source) = ResolveCaptureTime(photo, photoHeader);
        var photoTime = photoHeader?.CaptureTime;
        var videoTime = videos[pair.VideoPath].CaptureTime;
        return new LibraryItem(LibraryItemKind.ApplePair, photo)
        {
            Video = byPath[pair.VideoPath],
            PairCandidates = candidates,
            Header = photoHeader,
            CaptureTimeLocal = captureTime,
            CaptureTimeSource = source,
            PairValidation = validation,
            PairTimeDelta = photoTime is { } p && videoTime is { } v ? p.DistanceTo(v) : null
        };
    }

    private static LibraryItem AnalyzeSingle(LibraryFile photo)
    {
        var extension = Path.GetExtension(photo.Path);
        ImageHeader? header = null;
        ImageLayout layout = default;
        try
        {
            // 同一句柄完成动态照片检测与头部读取，每张照片只打开一次；解析时在段间频繁定位，缓冲再大也会被丢弃，1KB 足够
            using var stream = new FileStream(photo.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024, FileOptions.RandomAccess);
            if (MediaFileTypes.MotionPhotoExtensions.Contains(extension))
            {
                layout = MotionPhotoLayout.Inspect(stream);
            }

            if (FastImageHeaderReader.TryReadHeader(stream, extension, out var read))
            {
                header = read;
            }
        }
        catch (Exception)
        {
            // 单个无法读取或结构畸形的文件按普通照片列出，不中断整批扫描
        }

        var (captureTime, source) = ResolveCaptureTime(photo, header);
        return new LibraryItem(layout.Video is null ? LibraryItemKind.Still : LibraryItemKind.MotionPhoto, photo)
        {
            Embedded = layout.Video,
            HasGainMap = layout.HasGainMap,
            Header = header,
            CaptureTimeLocal = captureTime,
            CaptureTimeSource = source
        };
    }

    private static ImageHeader? ReadHeader(string path)
    {
        try
        {
            return FastImageHeaderReader.TryReadHeader(path, out var header) ? header : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>照片侧只有拍摄时间与配对标识参与校验，与 ExifTool 读出的字段一一对应。</summary>
    private static MediaMetadata ToMetadata(string path, ImageHeader? header) => new()
    {
        Path = path,
        CaptureTime = header?.CaptureTime,
        ContentIdentifier = header?.ContentIdentifier
    };

    private static (DateTime Time, CaptureTimeSource Source) ResolveCaptureTime(LibraryFile photo, ImageHeader? header)
    {
        if (header?.CaptureTime is { } exif)
        {
            return (exif.LocalTime, CaptureTimeSource.Exif);
        }

        return FileNameDateTimeParser.TryParse(photo.Path, out var fromName)
            ? (fromName, CaptureTimeSource.FileName)
            : (photo.LastWriteTimeUtc.ToLocalTime(), CaptureTimeSource.LastWrite);
    }

    private static List<LibraryItem> FindTrailerCandidates(IReadOnlyList<LibraryItem> items, CancellationToken cancellationToken)
    {
        var flags = new bool[items.Count];
        Parallel.For(0, items.Count, new ParallelOptions { MaxDegreeOfParallelism = DefaultParallelism, CancellationToken = cancellationToken }, i =>
        {
            var item = items[i];
            flags[i] = item.Kind == LibraryItemKind.Still && MediaFileTypes.IsHeic(item.Photo.Path) && HasUnclaimedTrailer(item.Photo.Path);
        });

        return [.. items.Where((_, i) => flags[i])];
    }

    private static bool HasUnclaimedTrailer(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.RandomAccess);
            return HasUnclaimedTrailer(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 顶层 box 链没有恰好覆盖到文件末尾，或中途出现第二个 ftyp（直接追加的 MP4），说明尾部有不属于 HEIC 的数据。
    /// </summary>
    internal static bool HasUnclaimedTrailer(Stream stream)
    {
        var length = stream.Length;
        Span<byte> header = stackalloc byte[16];
        long offset = 0;
        for (var count = 0; count < MaxTopLevelBoxes && offset < length; count++)
        {
            if (length - offset < 8)
            {
                return true;
            }

            stream.Position = offset;
            if (stream.ReadAtLeast(header[..8], 8, throwOnEndOfStream: false) < 8)
            {
                return true;
            }

            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
            var headerLength = 8;
            if (size == 1)
            {
                if (stream.ReadAtLeast(header[8..], 8, throwOnEndOfStream: false) < 8 || BinaryPrimitives.ReadUInt64BigEndian(header[8..]) > long.MaxValue)
                {
                    return true;
                }

                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
                headerLength = 16;
            }
            else if (size == 0)
            {
                return false;
            }

            if (offset == 0 && type != Ftyp)
            {
                return false;
            }

            if (size < headerLength || size > length - offset || (offset > 0 && type == Ftyp))
            {
                return true;
            }

            offset += size;
        }

        return false;
    }

    private sealed record PendingItem(LibraryFile Photo, IReadOnlyList<MediaPair> Candidates);

    /// <summary>
    /// 直接在枚举时取大小与时间（Windows 上来自目录项本身，无需逐个 FileInfo），并统计无法进入的目录与被忽略的文件。
    /// </summary>
    private sealed class MediaFileEnumerator(string root) : FileSystemEnumerator<LibraryFile>(root, Options)
    {
        private const FileAttributes HiddenOrSystem = FileAttributes.Hidden | FileAttributes.System;

        // 不用 IgnoreInaccessible：它会静默吞掉错误，改在 ContinueOnError 中计数后继续。
        // 隐藏与系统属性不放进 AttributesToSkip，否则这些文件不经过筛选、无法计入“已忽略”
        private static readonly EnumerationOptions Options = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false
        };

        /// <summary>遇到的普通文件总数（不含隐藏或系统目录内的文件）。</summary>
        public int FilesSeen { get; private set; }

        /// <summary>不参与图库的文件：非媒体、隐藏或系统文件、本程序的暂存与备份。</summary>
        public int FilesIgnored { get; private set; }

        public int Errors { get; private set; }

        protected override bool ShouldRecurseIntoEntry(ref FileSystemEntry entry) => (entry.Attributes & HiddenOrSystem) == 0;

        protected override bool ShouldIncludeEntry(ref FileSystemEntry entry)
        {
            if (entry.IsDirectory)
            {
                return false;
            }

            FilesSeen++;
            // 本程序的暂存与就地替换备份属于进行中的操作，不是用户文件
            var name = entry.FileName;
            var extension = Path.GetExtension(name);
            var included = (entry.Attributes & HiddenOrSystem) == 0 &&
                           !name.StartsWith(OutputCommitter.StagingPrefix, StringComparison.Ordinal) &&
                           !name.EndsWith(OutputCommitter.BackupSuffix, StringComparison.OrdinalIgnoreCase) &&
                           (PhotoExtensions.Contains(extension) || VideoExtensions.Contains(extension));
            if (!included)
            {
                FilesIgnored++;
            }

            return included;
        }

        protected override LibraryFile TransformEntry(ref FileSystemEntry entry) =>
            new(entry.ToFullPath(), entry.Length, entry.LastWriteTimeUtc.UtcDateTime, entry.CreationTimeUtc.UtcDateTime);

        protected override bool ContinueOnError(int error)
        {
            Errors++;
            return true;
        }
    }
}
