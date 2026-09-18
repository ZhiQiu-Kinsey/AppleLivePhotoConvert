using System.Collections.Concurrent;
using System.Globalization;
using LivePhotoConvert.Core.Matching;
using LivePhotoConvert.Desktop.Converters;
using LivePhotoConvert.Desktop.Models;
namespace LivePhotoConvert.Desktop.Services;

/// <summary>
/// 相册高速扫描器与配对构建器
/// </summary>
public sealed class AlbumScanner
{
    public sealed record ScanResult(
        List<TimelineGroup> Groups,
        int TotalScannedFiles,
        int ReadyPairsCount,
        int SuspiciousCount,
        int FilteredCount,
        long TotalBytes)
    {
        public static ScanResult Empty => new([], 0, 0, 0, 0, 0);
    }

    /// <summary>
    /// 异步扫描指定目录中的实况照片与配对
    /// </summary>
    /// <param name="directory">目录路径</param>
    /// <param name="conversionDirection">转换方向（0=苹果转安卓, 1=安卓转苹果, 2=提取独立文件）</param>
    /// <param name="cancellationToken">取消令牌</param>
    public static async Task<ScanResult> ScanDirectoryAsync(
        string directory,
        int conversionDirection = 0,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(async () =>
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return new ScanResult([], 0, 0, 0, 0, 0);
            }

            var allFiles = Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(".livephoto_backup", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (allFiles.Count == 0)
            {
                return new ScanResult([], 0, 0, 0, 0, 0);
            }

            var loc = LocalizationService.Instance;
            string dateFormat = loc.GetString("DateGroupFormat");
            var culture = loc.CurrentCulture;

            var cardList = new List<PhotoCardItemViewModel>();
            int suspicious = 0;
            long totalBytes = 0;

            if (conversionDirection == 1)
            {
                // ── 模式 1：安卓转苹果 ──
                // 核心输入为单文件安卓动态照片（.jpg/.jpeg/.heic 内嵌 MP4 微视频流）
                var candidateFiles = allFiles
                    .Where(f =>
                    {
                        var ext = Path.GetExtension(f);
                        return ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                               ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                               ext.Equals(".heic", StringComparison.OrdinalIgnoreCase);
                    })
                    .ToList();

                var motionCards = new ConcurrentBag<PhotoCardItemViewModel>();

                await Parallel.ForEachAsync(
                    candidateFiles,
                    new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = cancellationToken },
                    async (file, token) =>
                    {
                        var info = await MotionPhotoDetector.DetectAsync(file, cancellationToken: token);
                        if (info is null) return;

                        FileInfo fi = new(file);
                        long totalLen = fi.Exists ? fi.Length : 0;
                        long vBytes = info.VideoLength;
                        long pBytes = Math.Max(0, totalLen - vBytes);
                        DateTime dt = fi.Exists ? fi.LastWriteTime : DateTime.Now;

                        string fileName = Path.GetFileNameWithoutExtension(file);
                        string dirName = Path.GetFileName(Path.GetDirectoryName(file) ?? string.Empty);
                        string locSummary = string.IsNullOrWhiteSpace(dirName) ? loc.GetString("LocalAlbumFallback") : dirName;

                        var (aspectRatio, resolutionText) = SniffPhotoDimensions(file);

                        var card = new PhotoCardItemViewModel
                        {
                            Key = file,
                            PhotoPath = file,
                            VideoPath = null, // 按需在悬停或 QuickLook 时切片
                            IsMotionPhoto = true,
                            EmbeddedVideoOffset = info.VideoOffset,
                            EmbeddedVideoLength = info.VideoLength,
                            FileName = fileName,
                            DateTaken = dt,
                            FormattedDate = dt.ToString(dateFormat, culture),
                            FormattedTime = dt.ToString("HH:mm:ss", culture),
                            LocationSummary = locSummary,
                            DeviceInfo = "Motion Photo",
                            ResolutionText = resolutionText,
                            DurationText = loc.GetString("CardDurationLive"),
                            PhotoSizeText = FormatBytes(pBytes),
                            VideoSizeText = FormatBytes(vBytes),
                            AspectRatio = aspectRatio,
                            PairingStatusText = loc.GetString("PairStatusLocked"),
                            HasSuspiciousWarning = false,
                            WarningReason = string.Empty,
                            IsSelected = true
                        };

                        motionCards.Add(card);
                    });

                cardList.AddRange(motionCards.OrderBy(c => c.DateTaken));

                foreach (var c in cardList)
                {
                    totalBytes += c.EmbeddedVideoOffset + c.EmbeddedVideoLength;
                }
            }
            else if (conversionDirection == 0)
            {
                // ── 模式 0：苹果转安卓 ──
                // 核心输入为苹果实况对（HEIC/JPG + MOV）
                var pairingResult = MediaPairMatcher.Match(allFiles);

                foreach (var pair in pairingResult.Pairs)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    FileInfo photoInfo = new(pair.PhotoPath);
                    FileInfo videoInfo = new(pair.VideoPath);

                    long pBytes = photoInfo.Exists ? photoInfo.Length : 0;
                    long vBytes = videoInfo.Exists ? videoInfo.Length : 0;
                    totalBytes += pBytes + vBytes;

                    DateTime dt = photoInfo.Exists ? photoInfo.LastWriteTime : DateTime.Now;
                    string fileName = Path.GetFileNameWithoutExtension(pair.PhotoPath);
                    string dirName = Path.GetFileName(Path.GetDirectoryName(pair.PhotoPath) ?? string.Empty);
                    string locSummary = string.IsNullOrWhiteSpace(dirName) ? loc.GetString("LocalAlbumFallback") : dirName;
                    string ext = photoInfo.Extension.TrimStart('.').ToUpperInvariant();

                    bool isSuspicious = false;
                    string warningReason = string.Empty;
                    if (videoInfo.Exists && photoInfo.Exists)
                    {
                        var diff = (photoInfo.LastWriteTime - videoInfo.LastWriteTime).Duration();
                        if (diff.TotalSeconds > 3.0)
                        {
                            isSuspicious = true;
                            warningReason = loc.GetFormat("TimeDiffWarningFormat", diff.TotalSeconds);
                            suspicious++;
                        }
                    }

                    var (aspectRatio, resolutionText) = SniffPhotoDimensions(pair.PhotoPath);

                    PhotoCardItemViewModel card = new()
                    {
                        Key = pair.PhotoPath,
                        PhotoPath = pair.PhotoPath,
                        VideoPath = pair.VideoPath,
                        Pair = pair,
                        FileName = fileName,
                        DateTaken = dt,
                        FormattedDate = dt.ToString(dateFormat, culture),
                        FormattedTime = dt.ToString("HH:mm:ss", culture),
                        LocationSummary = locSummary,
                        DeviceInfo = ext,
                        ResolutionText = resolutionText,
                        DurationText = loc.GetString("CardDurationLive"),
                        PhotoSizeText = FormatBytes(pBytes),
                        VideoSizeText = FormatBytes(vBytes),
                        AspectRatio = aspectRatio,
                        PairingStatusText = isSuspicious ? loc.GetString("PairStatusPending") : loc.GetString("PairStatusLocked"),
                        HasSuspiciousWarning = isSuspicious,
                        WarningReason = warningReason,
                        IsSelected = !isSuspicious
                    };

                    cardList.Add(card);
                }
            }
            else // conversionDirection == 2: 提取独立单文件（同时支持苹果配对与安卓单文件）
            {
                var pairingResult = MediaPairMatcher.Match(allFiles);
                var pairedPhotos = new HashSet<string>(pairingResult.Pairs.Select(p => p.PhotoPath), StringComparer.OrdinalIgnoreCase);

                foreach (var pair in pairingResult.Pairs)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    FileInfo photoInfo = new(pair.PhotoPath);
                    FileInfo videoInfo = new(pair.VideoPath);

                    long pBytes = photoInfo.Exists ? photoInfo.Length : 0;
                    long vBytes = videoInfo.Exists ? videoInfo.Length : 0;
                    totalBytes += pBytes + vBytes;

                    DateTime dt = photoInfo.Exists ? photoInfo.LastWriteTime : DateTime.Now;
                    string fileName = Path.GetFileNameWithoutExtension(pair.PhotoPath);
                    string dirName = Path.GetFileName(Path.GetDirectoryName(pair.PhotoPath) ?? string.Empty);
                    string locSummary = string.IsNullOrWhiteSpace(dirName) ? loc.GetString("LocalAlbumFallback") : dirName;
                    string ext = photoInfo.Extension.TrimStart('.').ToUpperInvariant();

                    var (aspectRatio, resolutionText) = SniffPhotoDimensions(pair.PhotoPath);

                    PhotoCardItemViewModel card = new()
                    {
                        Key = pair.PhotoPath,
                        PhotoPath = pair.PhotoPath,
                        VideoPath = pair.VideoPath,
                        Pair = pair,
                        FileName = fileName,
                        DateTaken = dt,
                        FormattedDate = dt.ToString(dateFormat, culture),
                        FormattedTime = dt.ToString("HH:mm:ss", culture),
                        LocationSummary = locSummary,
                        DeviceInfo = ext,
                        ResolutionText = resolutionText,
                        DurationText = loc.GetString("CardDurationLive"),
                        PhotoSizeText = FormatBytes(pBytes),
                        VideoSizeText = FormatBytes(vBytes),
                        AspectRatio = aspectRatio,
                        PairingStatusText = loc.GetString("PairStatusLocked"),
                        HasSuspiciousWarning = false,
                        WarningReason = string.Empty,
                        IsSelected = true
                    };

                    cardList.Add(card);
                }

                // 扫描未配对的图片，检查是否为安卓动态照片
                var remainingPhotos = allFiles
                    .Where(f => !pairedPhotos.Contains(f) &&
                                (f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                 f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                                 f.EndsWith(".heic", StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                var motionCards = new ConcurrentBag<PhotoCardItemViewModel>();

                await Parallel.ForEachAsync(
                    remainingPhotos,
                    new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = cancellationToken },
                    async (file, token) =>
                    {
                        var info = await MotionPhotoDetector.DetectAsync(file, cancellationToken: token);
                        if (info is null) return;

                        FileInfo fi = new(file);
                        long totalLen = fi.Exists ? fi.Length : 0;
                        long vBytes = info.VideoLength;
                        long pBytes = Math.Max(0, totalLen - vBytes);
                        DateTime dt = fi.Exists ? fi.LastWriteTime : DateTime.Now;

                        string fileName = Path.GetFileNameWithoutExtension(file);
                        string dirName = Path.GetFileName(Path.GetDirectoryName(file) ?? string.Empty);
                        string locSummary = string.IsNullOrWhiteSpace(dirName) ? loc.GetString("LocalAlbumFallback") : dirName;

                        var (aspectRatio, resolutionText) = SniffPhotoDimensions(file);

                        var card = new PhotoCardItemViewModel
                        {
                            Key = file,
                            PhotoPath = file,
                            VideoPath = null,
                            IsMotionPhoto = true,
                            EmbeddedVideoOffset = info.VideoOffset,
                            EmbeddedVideoLength = info.VideoLength,
                            FileName = fileName,
                            DateTaken = dt,
                            FormattedDate = dt.ToString(dateFormat, culture),
                            FormattedTime = dt.ToString("HH:mm:ss", culture),
                            LocationSummary = locSummary,
                            DeviceInfo = "Motion Photo",
                            ResolutionText = resolutionText,
                            DurationText = loc.GetString("CardDurationLive"),
                            PhotoSizeText = FormatBytes(pBytes),
                            VideoSizeText = FormatBytes(vBytes),
                            AspectRatio = aspectRatio,
                            PairingStatusText = loc.GetString("PairStatusLocked"),
                            HasSuspiciousWarning = false,
                            WarningReason = string.Empty,
                            IsSelected = true
                        };

                        motionCards.Add(card);
                    });

                foreach (var c in motionCards)
                {
                    totalBytes += c.EmbeddedVideoOffset + c.EmbeddedVideoLength;
                    cardList.Add(c);
                }
            }

            if (cardList.Count == 0)
            {
                return new ScanResult([], allFiles.Count, 0, 0, allFiles.Count, 0);
            }

            // 按日期聚合为 TimelineGroup
            var groups = cardList
                .GroupBy(c => c.FormattedDate)
                .Select((g, idx) =>
                {
                    var firstCard = g.First();
                    var header = new TimelineHeaderItemViewModel
                    {
                        Key = $"group_{idx}_{g.Key}",
                        GroupDate = firstCard.DateTaken,
                        Title = g.Key,
                        LocationSummary = firstCard.LocationSummary,
                        PhotoCount = g.Count()
                    };
                    return new TimelineGroup
                    {
                        Header = header,
                        AllCards = g.ToList()
                    };
                })
                .ToList();

            int filtered = Math.Max(0, allFiles.Count - cardList.Count);

            return new ScanResult(
                Groups: groups,
                TotalScannedFiles: allFiles.Count,
                ReadyPairsCount: Math.Max(0, cardList.Count - suspicious),
                SuspiciousCount: suspicious,
                FilteredCount: filtered,
                TotalBytes: totalBytes);
        }, cancellationToken);
    }

    private static (double AspectRatio, string ResolutionText) SniffPhotoDimensions(string photoPath)
    {
        if (FastImageHeaderReader.TryReadDimensions(photoPath, out var dims) && dims.Width > 0 && dims.Height > 0)
        {
            return (dims.AspectRatio, $"{dims.Width}×{dims.Height}");
        }
        return (4.0 / 3.0, string.Empty);
    }

    private static string FormatBytes(long bytes) =>
        ByteSizeConverter.Instance.Convert(bytes, typeof(string), null, CultureInfo.InvariantCulture) as string
        ?? $"{bytes} B";
}
