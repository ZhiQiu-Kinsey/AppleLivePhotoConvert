using LivePhotoConvert.Core.Abstractions;
using LivePhotoConvert.Core.Models;
using LivePhotoConvert.Core.Services;

namespace LivePhotoConvert.Core.Tests;

/// <summary>
/// 配对校验器的测试
/// </summary>
public class PairValidatorTests
{
    private static MediaPair MakePair(string name = "IMG_0001") =>
        new($@"D:\in\{name}.heic", $@"D:\in\{name}.mov");

    /// <summary>
    /// ContentIdentifier 一致时应直接通过
    /// </summary>
    [Fact]
    public async Task Should_Accept_When_ContentIdentifier_Matches()
    {
        var exifTool = new FakeExifTool
        {
            ContentIdentifiers = { ["photo"] = "ABC-123", ["video"] = "ABC-123" }
        };
        var validator = new PairValidator(exifTool);

        var result = await validator.ValidateAsync(MakePair(), TestContext.Current.CancellationToken);

        Assert.True(result.IsAccepted);
        Assert.Contains(result.Reasons, r => r.Contains("ContentIdentifier 一致"));
    }

    /// <summary>
    /// 当传入 ContentIdentifier 字典缓存时，应直接使用缓存而不调用 ExifTool
    /// </summary>
    [Fact]
    public async Task Should_Use_Cached_ContentIdentifiers_Without_Calling_ExifTool()
    {
        var pair = MakePair("IMG_0001");
        var photoCi = new Dictionary<string, string> { [pair.PhotoPath] = "CACHED-123" };
        var videoCi = new Dictionary<string, string> { [pair.VideoPath] = "CACHED-123" };

        var exifTool = new FakeExifTool(); // 未在 FakeExifTool 中设置任何 ContentIdentifier
        var validator = new PairValidator(exifTool, photoCi, videoCi);

        var result = await validator.ValidateAsync(pair, TestContext.Current.CancellationToken);

        Assert.True(result.IsAccepted);
        Assert.Contains(result.Reasons, r => r.Contains("CACHED-123"));
        Assert.Equal(0, exifTool.ReadContentIdentifierCallCount);
    }

    /// <summary>
    /// ContentIdentifier 不一致时应直接拒绝
    /// </summary>
    [Fact]
    public async Task Should_Reject_When_ContentIdentifier_Mismatches()
    {
        var exifTool = new FakeExifTool
        {
            ContentIdentifiers = { ["photo"] = "ABC-123", ["video"] = "XYZ-789" }
        };
        var validator = new PairValidator(exifTool);

        var result = await validator.ValidateAsync(MakePair(), TestContext.Current.CancellationToken);

        Assert.False(result.IsAccepted);
        Assert.Contains(result.Reasons, r => r.Contains("ContentIdentifier 不匹配"));
    }

    /// <summary>
    /// 仅照片或仅视频单边含 ContentIdentifier 时应拒绝，避免把不相关的照片与视频错配合成
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Should_Reject_When_Only_One_Side_Has_ContentIdentifier(bool photoHasId, bool videoHasId)
    {
        var exifTool = new FakeExifTool();
        if (photoHasId)
        {
            exifTool.ContentIdentifiers["photo"] = "ABC-123";
        }
        if (videoHasId)
        {
            exifTool.ContentIdentifiers["video"] = "ABC-123";
        }
        var validator = new PairValidator(exifTool);

        var result = await validator.ValidateAsync(MakePair(), TestContext.Current.CancellationToken);

        Assert.False(result.IsAccepted);
        Assert.Contains(result.Reasons, r => r.Contains("仅"));
    }

    /// <summary>
    /// 无 ContentIdentifier 但拍摄时间差在 3 秒内应通过
    /// </summary>
    [Fact]
    public async Task Should_Accept_When_No_ContentId_But_Timestamp_Within_Threshold()
    {
        var baseTime = new DateTime(2024, 6, 15, 14, 30, 0);
        var exifTool = new FakeExifTool
        {
            PhotoCreateDate = baseTime,
            VideoCreateDate = baseTime.AddSeconds(2),
            VideoDuration = TimeSpan.FromSeconds(2.5)
        };
        var validator = new PairValidator(exifTool);

        var result = await validator.ValidateAsync(MakePair(), TestContext.Current.CancellationToken);

        Assert.True(result.IsAccepted);
    }

    /// <summary>
    /// 无 ContentIdentifier，拍摄时间差超过 3 秒即拒绝（即使视频时长正常）
    /// </summary>
    [Fact]
    public async Task Should_Reject_When_Timestamp_Exceeds_Threshold_Even_If_Duration_Normal()
    {
        var baseTime = new DateTime(2024, 6, 15, 14, 30, 0);
        var exifTool = new FakeExifTool
        {
            PhotoCreateDate = baseTime,
            VideoCreateDate = baseTime.AddSeconds(10),
            VideoDuration = TimeSpan.FromSeconds(2.5)
        };
        var validator = new PairValidator(exifTool);

        var result = await validator.ValidateAsync(MakePair(), TestContext.Current.CancellationToken);

        Assert.False(result.IsAccepted);
        Assert.Contains(result.Reasons, r => r.Contains("拍摄时间差"));
    }

    /// <summary>
    /// 无 ContentIdentifier，拍摄时间差超过 3 秒且视频超长时应拒绝
    /// </summary>
    [Fact]
    public async Task Should_Reject_When_Timestamp_Exceeds_Threshold_And_Video_Too_Long()
    {
        var baseTime = new DateTime(2024, 6, 15, 14, 30, 0);
        var exifTool = new FakeExifTool
        {
            PhotoCreateDate = baseTime,
            VideoCreateDate = baseTime.AddMinutes(5),
            VideoDuration = TimeSpan.FromSeconds(60)
        };
        var validator = new PairValidator(exifTool);

        var result = await validator.ValidateAsync(MakePair(), TestContext.Current.CancellationToken);

        Assert.False(result.IsAccepted);
        Assert.Contains(result.Reasons, r => r.Contains("拍摄时间差"));
    }

    /// <summary>
    /// 仅照片或仅视频单边含拍摄时间时应拒绝（真照片与真视频都应带拍摄时间）
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Should_Reject_When_Only_One_Side_Has_CreateDate(bool photoHasDate, bool videoHasDate)
    {
        var exifTool = new FakeExifTool
        {
            PhotoCreateDate = photoHasDate ? new DateTime(2024, 6, 15, 14, 30, 0) : null,
            VideoCreateDate = videoHasDate ? new DateTime(2024, 6, 15, 14, 30, 0) : null,
            VideoDuration = TimeSpan.FromSeconds(2.5)
        };
        var validator = new PairValidator(exifTool);

        var result = await validator.ValidateAsync(MakePair(), TestContext.Current.CancellationToken);

        Assert.False(result.IsAccepted);
        Assert.Contains(result.Reasons, r => r.Contains("拍摄时间"));
    }

    /// <summary>
    /// 无 ContentIdentifier、无时间差、但视频时长超过 30 秒时应拒绝
    /// </summary>
    [Fact]
    public async Task Should_Reject_When_Video_Too_Long()
    {
        var exifTool = new FakeExifTool
        {
            VideoDuration = TimeSpan.FromSeconds(60)
        };
        var validator = new PairValidator(exifTool);

        var result = await validator.ValidateAsync(MakePair(), TestContext.Current.CancellationToken);

        Assert.False(result.IsAccepted);
        Assert.Contains(result.Reasons, r => r.Contains("视频时长"));
    }

    /// <summary>
    /// 所有元数据均不可用时应降级通过（仅文件名匹配）
    /// </summary>
    [Fact]
    public async Task Should_Accept_When_All_Signals_Unavailable()
    {
        var exifTool = new FakeExifTool();
        var validator = new PairValidator(exifTool);

        var result = await validator.ValidateAsync(MakePair(), TestContext.Current.CancellationToken);

        Assert.True(result.IsAccepted);
        Assert.Contains(result.Reasons, r => r.Contains("降级"));
    }

    /// <summary>
    /// 当取消令牌被触发时，应正确抛出 OperationCanceledException 而不是被捕获吞掉
    /// </summary>
    [Fact]
    public async Task Should_Propagate_OperationCanceledException_When_Canceled()
    {
        var exifTool = new FakeExifTool();
        var validator = new PairValidator(exifTool);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            validator.ValidateAsync(MakePair(), cts.Token));
    }

    /// <summary>
    /// 用于测试的假 ExifTool 实现
    /// </summary>
    private sealed class FakeExifTool : IExifTool
    {
        public Dictionary<string, string> ContentIdentifiers { get; } = new();
        public int ReadContentIdentifierCallCount { get; private set; }
        public DateTime? PhotoCreateDate { get; init; }
        public DateTime? VideoCreateDate { get; init; }
        public TimeSpan? VideoDuration { get; init; }

        public Task WriteMotionPhotoTagsAsync(string imagePath, long videoOffset, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RemoveMotionPhotoTagsAsync(string imagePath, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<long?> TryReadMicroVideoOffsetAsync(string imagePath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<long?>(null);
        }

        public Task<string?> TryReadContentIdentifierAsync(string filePath, ContentIdentifierKind kind, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadContentIdentifierCallCount++;
            var key = kind == ContentIdentifierKind.Photo ? "photo" : "video";
            return Task.FromResult(ContentIdentifiers.TryGetValue(key, out var value) ? value : null);
        }

        public Task WriteAppleContentIdentifierAsync(string photoPath, string contentIdentifier, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task WriteAppleVideoMetadataAsync(string videoPath, string contentIdentifier, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<DateTime?> TryReadCreateDateAsync(string filePath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // 根据文件扩展名判断是照片还是视频
            var ext = Path.GetExtension(filePath);
            var isVideo = ext.Equals(".mov", StringComparison.OrdinalIgnoreCase) ||
                          ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase);
            return Task.FromResult(isVideo ? VideoCreateDate : PhotoCreateDate);
        }

        public Task<TimeSpan?> TryReadDurationAsync(string filePath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(VideoDuration);
        }

        public Task<bool> IsMirroredVideoAsync(string videoPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
