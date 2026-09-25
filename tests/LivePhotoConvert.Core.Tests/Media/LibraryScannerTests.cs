using LivePhotoConvert.Core.Media;
using LivePhotoConvert.Core.Metadata;
using LivePhotoConvert.Core.Pairing;
using LivePhotoConvert.Core.Pipeline;
using LivePhotoConvert.Core.Services;

namespace LivePhotoConvert.Core.Tests.Media;

public class LibraryScannerTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static Task<LibraryScanResult> ScanAsync(string root) => LibraryScanner.ScanAsync(root, cancellationToken: Token);

    [Fact]
    public async Task ApplePair_ProducesOneItemWithWholeFileVideo()
    {
        using var temp = new TempDirectory();
        var photo = temp.CreateFile("100APPLE/IMG_0001.HEIC", SyntheticImages.Heif(4032, 3024, rotation: 3, dateTimeOriginal: "2024:05:06 07:08:09"));
        var video = temp.CreateFile("100APPLE/IMG_0001.MOV", SyntheticMedia.Mov(5000));

        var result = await ScanAsync(temp.Root);

        var item = Assert.Single(result.Items);
        Assert.Equal(LibraryItemKind.ApplePair, item.Kind);
        Assert.Equal(photo, item.Photo.Path);
        Assert.Equal(video, item.Video?.Path);
        Assert.Equal(new VideoSource(video, 0, 5000, IsEmbedded: false), item.VideoSource);
        Assert.Equal(new FileInfo(photo).Length + 5000, item.SourceBytes);
        Assert.Equal((3024, 4032), (item.Header?.Width, item.Header?.Height));
        Assert.Equal(new DateTime(2024, 5, 6, 7, 8, 9), item.CaptureTimeLocal);
        Assert.Equal(CaptureTimeSource.Exif, item.CaptureTimeSource);
        Assert.Equal(new MediaPair(photo, video), Assert.Single(item.PairCandidates));
        Assert.Equal(2, result.TotalFiles);
        Assert.Equal(0, result.IgnoredFiles);
        Assert.Equal(0, result.InaccessibleEntries);
    }

    [Fact]
    public async Task SameStemFormats_ProduceOneItemAndKeepAllCandidates()
    {
        using var temp = new TempDirectory();
        var heic = temp.CreateFile("IMG_0002.heic", SyntheticImages.Heif(400, 300));
        var jpg = temp.CreateFile("IMG_0002.jpg", SyntheticImages.Jpeg(400, 300));
        var mov = temp.CreateFile("IMG_0002.mov", SyntheticMedia.Mov());
        var mp4 = temp.CreateFile("IMG_0002.mp4", SyntheticMedia.Mp4());

        var item = Assert.Single((await ScanAsync(temp.Root)).Items);

        Assert.Equal(LibraryItemKind.ApplePair, item.Kind);
        Assert.Equal(heic, item.Photo.Path);
        Assert.Equal(mov, item.Video?.Path);
        Assert.Equal([new(heic, mov), new(heic, mp4), new(jpg, mov), new(jpg, mp4)], item.PairCandidates);
    }

    [Fact]
    public async Task JpegMotionPhoto_LocatesEmbeddedVideo()
    {
        using var temp = new TempDirectory();
        var video = SyntheticMedia.Mp4(6000);
        var bytes = SyntheticMedia.MotionPhoto(SyntheticImages.Jpeg(4000, 3000, orientation: 6), video);
        var path = temp.CreateFile("PXL_20240506_070809123.MP.jpg", bytes);

        var item = Assert.Single((await ScanAsync(temp.Root)).Items);

        Assert.Equal(LibraryItemKind.MotionPhoto, item.Kind);
        Assert.Equal(new VideoSource(path, bytes.Length - video.Length, video.Length, IsEmbedded: true), item.VideoSource);
        Assert.Equal(bytes.Length, item.SourceBytes);
        Assert.Equal((3000, 4000), (item.Header?.Width, item.Header?.Height));
        Assert.Equal(CaptureTimeSource.FileName, item.CaptureTimeSource);
        Assert.Equal(new DateTime(2024, 5, 6, 7, 8, 9), item.CaptureTimeLocal);
    }

    [Fact]
    public async Task HeicMpvdMotionPhoto_IsDetectedWithoutExifTool()
    {
        using var temp = new TempDirectory();
        var heic = SyntheticImages.Heif(4000, 3000);
        var video = SyntheticMedia.Mp4(3000);
        temp.CreateFile("MVIMG_0001.heic", SyntheticMedia.HeicMotionPhoto(heic, video));

        var item = Assert.Single((await ScanAsync(temp.Root)).Items);

        Assert.Equal(LibraryItemKind.MotionPhoto, item.Kind);
        Assert.Equal(new EmbeddedVideo(heic.Length + 8, video.Length, heic.Length), item.Embedded);
        Assert.Equal(4000, item.Header?.Width);
    }

    /// <summary>苹果实况的 HEIC 增益图、安卓动态照片的 Ultra HDR 与 HEIC tmap 都算 HDR；普通照片不算。</summary>
    [Fact]
    public async Task HdrPhotos_AreFlaggedForAppleAndAndroidItems()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("IMG_0001.heic", SyntheticImages.Heif(400, 300, appleGainMap: true));
        temp.CreateFile("IMG_0001.mov", SyntheticMedia.Mov());
        temp.CreateFile("IMG_0002.heic", SyntheticImages.Heif(400, 300));
        temp.CreateFile("IMG_0002.mov", SyntheticMedia.Mov());
        temp.CreateFile("MVIMG_0003.jpg", SyntheticMedia.MotionPhotoWithGainMap(SyntheticMedia.Jpeg(700)));
        temp.CreateFile("MVIMG_0004.jpg", SyntheticMedia.MotionPhoto());
        temp.CreateFile("MVIMG_0005.heic", SyntheticMedia.HeicMotionPhoto(SyntheticImages.Heif(400, 300, toneMapItem: true), SyntheticMedia.Mp4(3000)));

        var items = (await ScanAsync(temp.Root)).Items.ToDictionary(item => Path.GetFileNameWithoutExtension(item.Photo.Path));

        Assert.Equal(LibraryItemKind.ApplePair, items["IMG_0001"].Kind);
        Assert.Equal(LibraryItemKind.MotionPhoto, items["MVIMG_0003"].Kind);
        Assert.Equal(LibraryItemKind.MotionPhoto, items["MVIMG_0005"].Kind);
        Assert.Equal(["IMG_0001", "MVIMG_0003", "MVIMG_0005"], items.Where(kv => kv.Value.IsHdr).Select(kv => kv.Key).Order());
    }

    [Fact]
    public async Task SamsungTrailer_IsMotionPhoto()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("20240506_070809.jpg", SyntheticMedia.SamsungMotionPhoto(SyntheticImages.Jpeg(640, 480), SyntheticMedia.Mp4(4000)));

        var item = Assert.Single((await ScanAsync(temp.Root)).Items);

        Assert.Equal(LibraryItemKind.MotionPhoto, item.Kind);
        Assert.Equal(4000, item.Embedded?.Length);
    }

    [Fact]
    public async Task PlainPhotos_AreStills_AndOrphanVideosAreNotItems()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("plain.jpg", SyntheticImages.Jpeg(640, 480));
        temp.CreateFile("hdr.jpg", SyntheticMedia.UltraHdrStill(SyntheticMedia.Jpeg(700)));
        temp.CreateFile("screen.png", Png(320, 640));
        temp.CreateFile("orphan.mov", SyntheticMedia.Mov());
        temp.CreateFile("notes.txt", [1, 2, 3]);

        var result = await ScanAsync(temp.Root);

        Assert.Equal(["hdr.jpg", "plain.jpg", "screen.png"], result.Items.Select(item => Path.GetFileName(item.Photo.Path)));
        Assert.All(result.Items, item => Assert.Equal(LibraryItemKind.Still, item.Kind));
        Assert.All(result.Items, item => Assert.Null(item.VideoSource));
        Assert.True(result.Items[0].HasGainMap);
        Assert.Equal((320, 640), (result.Items[2].Header?.Width, result.Items[2].Header?.Height));
        Assert.Equal(5, result.TotalFiles);
        Assert.Equal(1, result.IgnoredFiles);
    }

    [Fact]
    public async Task StagingBackupAndHiddenFiles_AreIgnoredAndCounted()
    {
        using var temp = new TempDirectory();
        var kept = temp.CreateFile("IMG_0004.jpg", SyntheticImages.Jpeg(640, 480));
        temp.CreateFile("~lpc-t0000000000000001-abc.jpg", SyntheticImages.Jpeg(640, 480));
        temp.CreateFile("IMG_0005.jpg.livephoto_backup", SyntheticImages.Jpeg(640, 480));
        temp.CreateFile("IMG_0006.LIVEPHOTO_BACKUP", SyntheticImages.Jpeg(640, 480));
        if (!OperatingSystem.IsWindows())
        {
            // Unix 上以点开头即为隐藏（含 macOS 的 AppleDouble 伴生文件）
            temp.CreateFile("._IMG_0004.jpg", SyntheticImages.Jpeg(640, 480));
            temp.CreateFile(".thumbnails/IMG_0007.jpg", SyntheticImages.Jpeg(640, 480));
        }
        else
        {
            var thumbs = temp.CreateFile("Thumbs.db", [1, 2, 3]);
            File.SetAttributes(thumbs, FileAttributes.Hidden | FileAttributes.System);
            var inHidden = temp.CreateFile("hidden/IMG_0007.jpg", SyntheticImages.Jpeg(640, 480));
            var hiddenDirectory = new DirectoryInfo(Path.GetDirectoryName(inHidden)!);
            hiddenDirectory.Attributes |= FileAttributes.Hidden;
        }

        var result = await ScanAsync(temp.Root);

        Assert.Equal(kept, Assert.Single(result.Items).Photo.Path);
        // 隐藏目录不进入，其中的文件不计数；隐藏文件本身计为已忽略
        Assert.Equal(4, result.IgnoredFiles);
        Assert.Equal(5, result.TotalFiles);
    }

    [Fact]
    public async Task UnreadableSubdirectory_IsSkippedAndCounted()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows 上以 ACL 控制权限，此处只验证 Unix 权限位。");
            return;
        }

        using var temp = new TempDirectory();
        temp.CreateFile("ok/IMG_0001.jpg", SyntheticImages.Jpeg(640, 480));
        temp.CreateFile("locked/IMG_0002.jpg", SyntheticImages.Jpeg(640, 480));
        var locked = temp.Combine("locked");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            if (CanList(locked))
            {
                Assert.Skip("当前用户（如 root）不受目录权限限制。");
            }

            var result = await ScanAsync(temp.Root);

            Assert.Equal("IMG_0001.jpg", Path.GetFileName(Assert.Single(result.Items).Photo.Path));
            Assert.Equal(1, result.InaccessibleEntries);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task MissingRoot_Throws()
    {
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => ScanAsync(Path.Combine(Path.GetTempPath(), $"lpc-missing-{Guid.NewGuid():N}")));
    }

    [Fact]
    public async Task CanceledToken_Throws()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("IMG_0001.jpg", SyntheticImages.Jpeg(640, 480));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LibraryScanner.ScanAsync(temp.Root, cancellationToken: new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task CancellationDuringScan_StopsWithOperationCanceled()
    {
        using var temp = new TempDirectory();
        var jpeg = SyntheticImages.Jpeg(64, 48);
        for (var i = 0; i < 700; i++)
        {
            temp.CreateFile($"d{i % 7}/IMG_{i:D4}.jpg", jpeg);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var progress = new SynchronousProgress(_ => cts.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LibraryScanner.ScanAsync(temp.Root, progress, cts.Token));
        Assert.NotEmpty(progress.Reports);
    }

    [Fact]
    public async Task Progress_EndsWithAllItemsAnalyzed()
    {
        using var temp = new TempDirectory();
        for (var i = 0; i < 300; i++)
        {
            temp.CreateFile($"IMG_{i:D4}.jpg", SyntheticImages.Jpeg(64, 48));
        }

        temp.CreateFile("IMG_0000.mov", SyntheticMedia.Mov());
        var progress = new SynchronousProgress(_ => { });

        await LibraryScanner.ScanAsync(temp.Root, progress, Token);

        Assert.Equal(new LibraryScanProgress(301, 300, 300), progress.Reports[^1]);
        Assert.Contains(progress.Reports, report => report.ItemsTotal == 0 && report.FilesFound == 256);
    }

    [Fact]
    public async Task Results_AreSortedByPathAndStableAcrossScans()
    {
        using var temp = new TempDirectory();
        var names = Enumerable.Range(0, 60).Select(i => $"{(char)('a' + i % 5)}/IMG_{59 - i:D4}.jpg").ToArray();
        Random.Shared.Shuffle(names);
        foreach (var name in names)
        {
            temp.CreateFile(name, SyntheticImages.Jpeg(64, 48));
        }

        temp.CreateFile("a/IMG_0000.mov", SyntheticMedia.Mov());
        temp.CreateFile("b/MVIMG.jpg", SyntheticMedia.MotionPhoto());

        var first = (await ScanAsync(temp.Root)).Items.Select(item => item.Photo.Path).ToList();
        var second = (await ScanAsync(temp.Root)).Items.Select(item => item.Photo.Path).ToList();

        Assert.Equal(61, first.Count);
        Assert.Equal(first, second);
        Assert.Equal(first.Order(StringComparer.Ordinal), first);
    }

    [Fact]
    public async Task CaptureTime_FallsBackFromExifToFileNameToLastWrite()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("a.jpg", SyntheticImages.Jpeg(64, 48, dateTimeOriginal: "2021:02:03 04:05:06", offsetTimeOriginal: "+09:00"));
        temp.CreateFile("IMG_20230405_060708.jpg", SyntheticImages.Jpeg(64, 48));
        var plain = temp.CreateFile("z.png", Png(10, 10));
        var lastWrite = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(plain, lastWrite);

        var items = (await ScanAsync(temp.Root)).Items;

        Assert.Equal(
            [
                (new DateTime(2023, 4, 5, 6, 7, 8), CaptureTimeSource.FileName),
                (new DateTime(2021, 2, 3, 4, 5, 6), CaptureTimeSource.Exif),
                (lastWrite.ToLocalTime(), CaptureTimeSource.LastWrite)
            ],
            items.Select(item => (item.CaptureTimeLocal, item.CaptureTimeSource)));
        Assert.Equal(TimeSpan.FromHours(9), items[1].Header?.OffsetTimeOriginal);
        Assert.Equal(lastWrite, items[2].Photo.LastWriteTimeUtc);
    }

    /// <summary>
    /// 一组配对输入：照片 EXIF（拍摄时间、偏移、Apple 配对标识）与视频头部（mvhd 创建时间与时长、Keys 拍摄时间与配对标识）。
    /// </summary>
    public sealed record PairCase(
        string PhotoTime = "", string PhotoOffset = "", string PhotoId = "",
        string MvhdUtc = "", double Duration = 0, string KeysDate = "", string VideoId = "", bool Accepted = true);

    private static readonly Dictionary<string, PairCase> PairCases = new()
    {
        ["两侧带偏移，相差 1 秒"] = new(PhotoTime: "2024:05:06 07:08:09", PhotoOffset: "+08:00", MvhdUtc: "2024-05-05T23:08:10Z", Duration: 2),
        ["两侧带偏移，相差 11 秒"] = new(PhotoTime: "2024:05:06 07:08:09", PhotoOffset: "+08:00", MvhdUtc: "2024-05-05T23:08:20Z", Duration: 2, Accepted: false),
        ["Keys 拍摄时间优先于 mvhd"] = new(PhotoTime: "2024:05:06 07:08:09", PhotoOffset: "+08:00", MvhdUtc: "2024-05-05T20:00:00Z", KeysDate: "2024-05-06T07:08:10+0800", Duration: 2),
        ["只有照片有拍摄时间"] = new(PhotoTime: "2024:05:06 07:08:09", PhotoOffset: "+08:00", Duration: 2, Accepted: false),
        ["只有视频有拍摄时间"] = new(MvhdUtc: "2024-05-05T23:08:10Z", Duration: 2, Accepted: false),
        ["视频时长超过 30 秒"] = new(PhotoTime: "2024:05:06 07:08:09", PhotoOffset: "+08:00", MvhdUtc: "2024-05-05T23:08:09Z", Duration: 45, Accepted: false),
        ["照片无偏移，按本机时区比较"] = new(PhotoTime: "local:2024-05-05T23:08:11Z", MvhdUtc: "2024-05-05T23:08:09Z", Duration: 2),
        ["配对标识一致，忽略时间差"] = new(PhotoTime: "2024:05:06 07:08:09", PhotoOffset: "+08:00", PhotoId: "ID-1", MvhdUtc: "2024-05-06T05:00:00Z", VideoId: "ID-1"),
        ["配对标识不一致"] = new(PhotoId: "ID-1", VideoId: "ID-2", Accepted: false),
        ["只有照片有配对标识"] = new(PhotoId: "ID-1", Accepted: false),
        ["缺少全部信号，按文件名匹配"] = new()
    };

    public static TheoryData<string> PairCaseNames => [.. PairCases.Keys];

    [Theory]
    [MemberData(nameof(PairCaseNames))]
    public async Task PairJudgement_EqualsPairValidatorOnSameInputs(string name)
    {
        var @case = PairCases[name];
        using var temp = new TempDirectory();
        var (photo, video) = CreatePair(temp, @case);

        var item = Assert.Single((await ScanAsync(temp.Root)).Items);

        var expected = PairValidator.Validate(ExpectedPhotoMetadata(photo, @case), ExpectedVideoMetadata(video, @case));
        Assert.Equal(@case.Accepted, expected.IsAccepted);
        Assert.Equal(expected.IsAccepted, item.PairValidation?.IsAccepted);
        Assert.Equal(expected.Causes, item.PairValidation?.Causes);
        Assert.Equal(!expected.IsAccepted, item.RequiresPairReview);
    }

    [Theory]
    [MemberData(nameof(PairCaseNames))]
    public async Task PairJudgement_EqualsMergerVerdictWithExifTool(string name)
    {
        var exiftool = ExternalTools.RequireExifTool();
        var @case = PairCases[name];
        using var temp = new TempDirectory();
        CreatePair(temp, @case);
        var item = Assert.Single((await ScanAsync(temp.Root)).Items);

        await using var metadata = ExifToolMetadataService.Create(exiftool, maxSessions: 1);
        var report = await new MotionPhotoMerger(metadata, new FakeImageConverter(), new FakeVideoConverter()).MergeAsync(
            new MergeRequest { Candidates = item.PairCandidates, Output = new OutputOptions(temp.Combine("out")) },
            cancellationToken: Token);

        var outcome = Assert.Single(report.Items);
        Assert.Equal(item.RequiresPairReview, outcome.Kind == OutcomeKind.Skipped);
        if (item.RequiresPairReview)
        {
            Assert.Equal(item.PairValidation?.Causes, outcome.Causes);
        }
    }

    [Fact]
    public async Task PairCandidates_SelectsFirstAcceptedLikeMerger()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("IMG_0001.heic", SyntheticImages.Heif(400, 300, dateTimeOriginal: "2024:05:06 07:08:09", offsetTimeOriginal: "+08:00"));
        temp.CreateFile("IMG_0001.mov", SyntheticImages.Mov(new DateTime(2024, 5, 5, 22, 0, 0, DateTimeKind.Utc), 2));
        var mp4 = temp.CreateFile("IMG_0001.mp4", SyntheticImages.Mov(new DateTime(2024, 5, 5, 23, 8, 10, DateTimeKind.Utc), 2));

        var item = Assert.Single((await ScanAsync(temp.Root)).Items);

        Assert.Equal(mp4, item.Video?.Path);
        Assert.False(item.RequiresPairReview);
        Assert.Equal(TimeSpan.FromSeconds(1), item.PairTimeDelta);
        Assert.Equal(2, item.PairCandidates.Count);
    }

    [Fact]
    public async Task PairCandidates_AllRejected_ReportsSameReasonsAsMerger()
    {
        using var temp = new TempDirectory();
        var photo = temp.CreateFile("IMG_0001.jpg", SyntheticImages.Jpeg(64, 48, dateTimeOriginal: "2024:05:06 07:08:09", offsetTimeOriginal: "+08:00"));
        var mov = temp.CreateFile("IMG_0001.mov", SyntheticImages.Mov(null, 2));
        var mp4 = temp.CreateFile("IMG_0001.mp4", SyntheticImages.Mov(new DateTime(2024, 5, 5, 22, 0, 0, DateTimeKind.Utc), 2));

        var item = Assert.Single((await ScanAsync(temp.Root)).Items);

        var metadata = new FakeMetadataService();
        metadata.Set(photo, new MediaMetadata { Path = photo, CaptureTime = new CaptureTime(new DateTime(2024, 5, 6, 7, 8, 9), TimeSpan.FromHours(8)) });
        metadata.Set(mov, new MediaMetadata { Path = mov, Duration = TimeSpan.FromSeconds(2) });
        metadata.Set(mp4, new MediaMetadata { Path = mp4, Duration = TimeSpan.FromSeconds(2), CaptureTime = new CaptureTime(new DateTime(2024, 5, 5, 22, 0, 0), TimeSpan.Zero) });
        var report = await new MotionPhotoMerger(metadata, new FakeImageConverter(), new FakeVideoConverter()).MergeAsync(
            new MergeRequest { Candidates = item.PairCandidates, Output = new OutputOptions(temp.Combine("out")) },
            cancellationToken: Token);

        Assert.True(item.RequiresPairReview);
        Assert.Equal(mov, item.Video?.Path);
        Assert.Null(item.PairTimeDelta);
        var outcome = Assert.Single(report.Items);
        Assert.Equal(OutcomeKind.Skipped, outcome.Kind);
        Assert.Equal(outcome.Causes, item.PairValidation?.Causes);
    }

    private static (string Photo, string Video) CreatePair(TempDirectory temp, PairCase @case)
    {
        var photo = temp.CreateFile("IMG_0001.jpg", SyntheticImages.Jpeg(
            64, 48,
            dateTimeOriginal: PhotoTimeText(@case),
            offsetTimeOriginal: Optional(@case.PhotoOffset),
            contentIdentifier: Optional(@case.PhotoId)));
        var video = temp.CreateFile("IMG_0001.mov", SyntheticImages.Mov(
            Optional(@case.MvhdUtc) is { } utc ? DateTime.Parse(utc, null, System.Globalization.DateTimeStyles.AdjustToUniversal) : null,
            @case.Duration,
            keysCreationDate: Optional(@case.KeysDate),
            contentIdentifier: Optional(@case.VideoId)));
        return (photo, video);
    }

    /// <summary><c>local:</c> 前缀表示把该 UTC 时刻换算为本机时区的墙上时间写入 EXIF（不带偏移）。</summary>
    private static string? PhotoTimeText(PairCase @case) =>
        @case.PhotoTime.StartsWith("local:", StringComparison.Ordinal)
            ? DateTime.Parse(@case.PhotoTime[6..], null, System.Globalization.DateTimeStyles.AdjustToUniversal).ToLocalTime().ToString("yyyy:MM:dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)
            : Optional(@case.PhotoTime);

    private static MediaMetadata ExpectedPhotoMetadata(string path, PairCase @case) => new()
    {
        Path = path,
        CaptureTime = PhotoTimeText(@case) is { } text && CaptureTime.TryParse(text, out var time)
            ? time with { Offset = CaptureTime.ParseOffset(@case.PhotoOffset) }
            : null,
        ContentIdentifier = Optional(@case.PhotoId)
    };

    /// <summary>与 ExifTool 的取值一致：Keys 拍摄时间优先；mvhd 为 UTC，按本机时区换算并带偏移。</summary>
    private static MediaMetadata ExpectedVideoMetadata(string path, PairCase @case)
    {
        CaptureTime? time = null;
        if (Optional(@case.KeysDate) is { } keys)
        {
            time = new CaptureTime(DateTime.Parse(keys[..19], System.Globalization.CultureInfo.InvariantCulture), TimeSpan.FromHours(int.Parse(keys[^5..^2], System.Globalization.CultureInfo.InvariantCulture)));
        }
        else if (Optional(@case.MvhdUtc) is { } mvhd)
        {
            var utc = DateTime.Parse(mvhd, null, System.Globalization.DateTimeStyles.AdjustToUniversal);
            var offset = TimeZoneInfo.Local.GetUtcOffset(utc);
            time = new CaptureTime(DateTime.SpecifyKind(utc + offset, DateTimeKind.Unspecified), offset);
        }

        return new MediaMetadata
        {
            Path = path,
            CaptureTime = time,
            ContentIdentifier = Optional(@case.VideoId),
            Duration = @case.Duration > 0 ? TimeSpan.FromSeconds(@case.Duration) : null
        };
    }

    private static string? Optional(string value) => value.Length == 0 ? null : value;

    [Fact]
    public async Task UnreadableHeader_StillListsPhoto()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("broken.jpg", [0xFF, 0xD8, 0xFF, 0xE1, 0x00]);
        temp.CreateFile("empty.heic", []);

        var result = await ScanAsync(temp.Root);

        Assert.Equal(2, result.Items.Count);
        Assert.All(result.Items, item => Assert.Equal((LibraryItemKind.Still, (ImageHeader?)null), (item.Kind, item.Header)));
    }

    [Fact]
    public async Task EnrichHeic_UpgradesHeicWithAppendedVideoDeclaredInXmp()
    {
        using var temp = new TempDirectory();
        var heic = SyntheticImages.Heif(4000, 3000);
        var video = SyntheticMedia.Mp4(5000);
        var trailer = temp.CreateFile("IMG_0001.heic", [.. heic, .. video]);
        temp.CreateFile("IMG_0002.heic", heic);
        temp.CreateFile("IMG_0003.jpg", SyntheticImages.Jpeg(64, 48));
        var metadata = new FakeMetadataService();
        metadata.Xmp[trailer] = MotionPhotoXmp.Apply(null, video.Length, 0);

        var scanned = await ScanAsync(temp.Root);
        Assert.Equal(3, scanned.Items.Count);
        Assert.All(scanned.Items, item => Assert.Equal(LibraryItemKind.Still, item.Kind));

        var upgraded = await LibraryScanner.EnrichHeicAsync(scanned.Items, metadata, Token).ToListAsync(Token);

        var item = Assert.Single(upgraded);
        Assert.Equal(trailer, item.Photo.Path);
        Assert.Equal(LibraryItemKind.MotionPhoto, item.Kind);
        Assert.Equal(new VideoSource(trailer, heic.Length, video.Length, IsEmbedded: true), item.VideoSource);
        Assert.Equal(scanned.Items[0].Header, item.Header);
        Assert.Equal([trailer], metadata.XmpReads);
    }

    [Fact]
    public async Task EnrichHeic_XmpReadFailure_IsSkipped()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("IMG_0001.heic", [.. SyntheticImages.Heif(400, 300), .. SyntheticMedia.Mp4()]);
        var metadata = new FakeMetadataService { FailXmpReads = _ => true };

        var scanned = await ScanAsync(temp.Root);

        Assert.Empty(await LibraryScanner.EnrichHeicAsync(scanned.Items, metadata, Token).ToListAsync(Token));
    }

    [Fact]
    public async Task EnrichHeic_Canceled_Throws()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("IMG_0001.heic", [.. SyntheticImages.Heif(400, 300), .. SyntheticMedia.Mp4()]);
        var scanned = await ScanAsync(temp.Root);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await LibraryScanner.EnrichHeicAsync(scanned.Items, new FakeMetadataService(), new CancellationToken(canceled: true)).ToListAsync(Token));
    }

    public static TheoryData<string, bool> TrailerCases => new()
    {
        { "clean", false },
        { "appended-mp4", true },
        { "junk", true },
        { "mpvd", false },
        { "jpeg", false }
    };

    [Theory]
    [MemberData(nameof(TrailerCases))]
    public void HasUnclaimedTrailer_DetectsDataAfterTopLevelBoxes(string kind, bool expected)
    {
        var heic = SyntheticImages.Heif(400, 300);
        byte[] bytes = kind switch
        {
            "clean" => heic,
            "appended-mp4" => [.. heic, .. SyntheticMedia.Mp4()],
            "junk" => [.. heic, 1, 2, 3, 4, 5],
            "mpvd" => SyntheticMedia.HeicMotionPhoto(heic, SyntheticMedia.Mp4()),
            _ => SyntheticImages.Jpeg(64, 48)
        };

        Assert.Equal(expected, LibraryScanner.HasUnclaimedTrailer(new MemoryStream(bytes)));
    }

    private static bool CanList(string directory)
    {
        try
        {
            _ = Directory.EnumerateFileSystemEntries(directory).FirstOrDefault();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static byte[] Png(int width, int height)
    {
        var png = new byte[33];
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R'];
        signature.CopyTo(png, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16), (uint)width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20), (uint)height);
        return png;
    }

    /// <summary>在扫描线程上同步回调，便于在进度中途取消。</summary>
    private sealed class SynchronousProgress(Action<LibraryScanProgress> onReport) : IProgress<LibraryScanProgress>
    {
        private readonly Lock _gate = new();

        public List<LibraryScanProgress> Reports { get; } = [];

        public void Report(LibraryScanProgress value)
        {
            lock (_gate)
            {
                Reports.Add(value);
            }

            onReport(value);
        }
    }
}
