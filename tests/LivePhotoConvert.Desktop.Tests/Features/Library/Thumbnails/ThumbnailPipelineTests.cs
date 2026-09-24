using System.Collections.Concurrent;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using LivePhotoConvert.Core.Media;
using LivePhotoConvert.Core.Media.Thumbnails;
using LivePhotoConvert.Desktop.Features.Dialogs;
using LivePhotoConvert.Desktop.Features.Library.Thumbnails;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Models;

namespace LivePhotoConvert.Desktop.Tests.Features.Library.Thumbnails;

public class ThumbnailPipelineTests
{
    private static readonly DateTime Modified = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

    /// <summary>默认配置（中等行高、100% 缩放）下一张 4:3 位图的字节数。</summary>
    private static readonly long DefaultBitmapBytes = FakeDecoder.BytesFor(325);

    [Theory]
    [InlineData(325, 1.0, 384, 325)]
    [InlineData(325, 1.5, 512, 488)]
    [InlineData(416, 3.0, 1024, 1024)]
    [InlineData(234, 1.0, 256, 234)]
    [InlineData(325, 2.0, 768, 650)]
    public void SelectTarget_PicksSmallestTierCoveringRequiredPixels(double maxRowHeight, double scaling, int tier, int decodeHeight)
    {
        Assert.Equal((tier, decodeHeight), ThumbnailPipeline.SelectTarget(maxRowHeight, scaling));
        Assert.Equal(tier, ThumbnailPipeline.SelectTier(maxRowHeight, scaling));
    }

    [Fact]
    public void GalleryMetrics_MaxRowHeight_MatchesTierTable()
    {
        Assert.Equal(325, GalleryMetrics.MaxRowHeight("Medium"), precision: 6);
        Assert.Equal(416, GalleryMetrics.MaxRowHeight("Large"), precision: 6);
        Assert.Equal(234, GalleryMetrics.MaxRowHeight("Small"), precision: 6);
        Assert.Equal(GalleryMetrics.MediumRowHeight, GalleryMetrics.TargetRowHeight("Unknown"));
        Assert.Equal(86, GalleryMetrics.CardVerticalChrome);
        Assert.Equal(250 + 86 + 12, GalleryMetrics.RowExtent(250));
    }

    [AvaloniaFact]
    public async Task CacheHit_DecodesOffUiThread_AtRowPixelHeight()
    {
        using var fixture = new PipelineFixture();
        var card = Card("hit");
        fixture.Store.Cached.Add(card.PhotoFile!.Path);

        fixture.Pipeline.Acquire(card);
        await WaitUntil(() => card.Thumbnail is not null);

        Assert.Same(card.Thumbnail, card.DisplayImage);
        var call = Assert.Single(fixture.Decoder.Calls);
        Assert.False(call.OnUiThread, "解码不应在界面线程");
        Assert.Equal("cache", call.Tag);
        Assert.Equal(325, call.HeightPx);
        Assert.Empty(fixture.Store.Generated);
        Assert.Equal(DefaultBitmapBytes, fixture.Pipeline.ResidentBytes);
        Assert.Equal(DefaultBitmapBytes, fixture.Pipeline.PinnedBytes);
    }

    [AvaloniaFact]
    public async Task CacheMiss_GeneratesWithScannedHeader_OffUiThread()
    {
        using var fixture = new PipelineFixture();
        var card = Card("miss");

        fixture.Pipeline.Acquire(card);
        await WaitUntil(() => card.Thumbnail is not null);

        var request = Assert.Single(fixture.Store.Generated);
        Assert.Equal(384, request.Tier);
        Assert.Equal((4000, 3000, 6), request.Header);
        Assert.Equal(1234, request.Length);
        Assert.False(fixture.Store.GeneratedOnUiThread);
        Assert.All(fixture.Decoder.Calls, c => Assert.False(c.OnUiThread));
    }

    [AvaloniaFact]
    public async Task Release_RemovesQueuedRequests_AndNeverGeneratesThem()
    {
        using var fixture = new PipelineFixture();
        fixture.Store.Block(0);
        fixture.Store.Block(1);
        var cards = Enumerable.Range(0, 5).Select(i => Card($"c{i}")).ToArray();
        foreach (var card in cards)
        {
            fixture.Pipeline.Acquire(card);
        }

        // 两个生成 worker 各占一张（后附加的优先），其余三张排队
        await WaitUntil(() => fixture.Store.Generated.Count == 2 && fixture.Pipeline.PendingCount == 3);
        Assert.Equal(["c4", "c3"], fixture.Store.Generated.Select(r => Path.GetFileNameWithoutExtension(r.Path)));
        Assert.True(fixture.Pipeline.PendingCount <= fixture.Pipeline.AttachedCount);

        foreach (var card in cards.Take(3))
        {
            fixture.Pipeline.Release(card);
        }

        Assert.Equal(0, fixture.Pipeline.PendingCount);
        fixture.Store.Open(0);
        fixture.Store.Open(1);
        await WaitUntil(() => cards[3].Thumbnail is not null && cards[4].Thumbnail is not null);
        await Settle();

        Assert.Equal(2, fixture.Store.Generated.Count);
        Assert.All(cards.Take(3), c => Assert.Null(c.Thumbnail));
    }

    [AvaloniaFact]
    public async Task ResultForReleasedCard_FromOlderGeneration_IsDiscarded()
    {
        using var fixture = new PipelineFixture();
        fixture.Store.Block(0);
        var card = Card("stale");
        fixture.Pipeline.Acquire(card);
        await WaitUntil(() => fixture.Store.Generated.Count == 1);

        fixture.Pipeline.Release(card);
        fixture.Pipeline.NextGeneration();
        fixture.Store.Open(0);
        await WaitUntil(() => fixture.Decoder.Calls.Count == 1);
        await Settle();

        Assert.Null(card.Thumbnail);
        Assert.Equal(0, fixture.Pipeline.ResidentBytes);
        await WaitUntil(() => FakeDecoder.IsDisposed(fixture.Decoder.Calls[0].Bitmap));
    }

    [AvaloniaFact]
    public async Task ResultForReleasedCard_InCurrentGeneration_IsKeptUnpinnedWithinBudget()
    {
        using var fixture = new PipelineFixture();
        fixture.Store.Block(0);
        var card = Card("jitter");
        fixture.Pipeline.Acquire(card);
        await WaitUntil(() => fixture.Store.Generated.Count == 1);

        fixture.Pipeline.Release(card);
        fixture.Store.Open(0);
        await WaitUntil(() => card.Thumbnail is not null);

        Assert.Equal(DefaultBitmapBytes, fixture.Pipeline.ResidentBytes);
        Assert.Equal(0, fixture.Pipeline.PinnedBytes);

        // 回到视口时直接显示，不再请求
        fixture.Pipeline.Acquire(card);
        await Settle();
        Assert.Single(fixture.Store.Generated);
        Assert.Equal(DefaultBitmapBytes, fixture.Pipeline.PinnedBytes);
    }

    [AvaloniaFact]
    public async Task Reset_DiscardsInFlightResult_AndReloadsAttachedCard()
    {
        using var fixture = new PipelineFixture();
        fixture.Store.Block(0);
        var card = Card("rescan");
        fixture.Pipeline.Acquire(card);
        await WaitUntil(() => fixture.Store.Generated.Count == 1);

        fixture.Pipeline.Reset();
        await WaitUntil(() => card.Thumbnail is not null);
        Assert.Equal("gen:1", fixture.Decoder.TagOf(card.Thumbnail!));

        // 旧纪元的结果晚到：丢弃，不覆盖新图
        fixture.Store.Open(0);
        await WaitUntil(() => fixture.Decoder.Calls.Count == 2);
        await Settle();
        Assert.Equal("gen:1", fixture.Decoder.TagOf(card.Thumbnail!));
        var stale = fixture.Decoder.Calls.Single(c => c.Tag == "gen:0").Bitmap;
        await WaitUntil(() => FakeDecoder.IsDisposed(stale));
        Assert.False(FakeDecoder.IsDisposed(card.Thumbnail!));
    }

    [AvaloniaFact]
    public async Task Eviction_DetachesBeforeDisposing_AndNeverTouchesPinnedCards()
    {
        using var fixture = new PipelineFixture(budgetBytes: DefaultBitmapBytes + 1);
        var first = Card("first");
        var second = Card("second");

        fixture.Pipeline.Acquire(first);
        await WaitUntil(() => first.Thumbnail is not null);
        var firstBitmap = first.Thumbnail!;

        // 模拟回收后仍残留在容器里的 Image：随卡片属性更新
        var image = new Image { Width = 100, Height = 75, Source = first.DisplayImage };
        first.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PhotoCardItemViewModel.DisplayImage))
            {
                image.Source = first.DisplayImage;
            }
        };
        var window = new Window { Width = 200, Height = 200, Content = image };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        fixture.Pipeline.Release(first);
        fixture.Pipeline.Acquire(second);
        await WaitUntil(() => second.Thumbnail is not null);

        // 驱逐：卡片与 Image 先放开位图，位图本身延迟到渲染帧之后释放
        Assert.Null(first.Thumbnail);
        Assert.Null(image.Source);
        Assert.False(FakeDecoder.IsDisposed(firstBitmap));
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        await WaitUntil(() => FakeDecoder.IsDisposed(firstBitmap));
        Assert.Equal(DefaultBitmapBytes, fixture.Pipeline.ResidentBytes);

        // 钉住的卡片即使超出预算也不驱逐
        var third = Card("third");
        fixture.Pipeline.Acquire(third);
        await WaitUntil(() => third.Thumbnail is not null);
        Assert.NotNull(second.Thumbnail);
        Assert.Equal(2 * DefaultBitmapBytes, fixture.Pipeline.PinnedBytes);
        Assert.Equal(fixture.Pipeline.PinnedBytes, fixture.Pipeline.ResidentBytes);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Configure_ReloadsAttachedCards_KeepingOldBitmapUntilNewArrives()
    {
        using var fixture = new PipelineFixture();
        var card = Card("dpi");
        fixture.Pipeline.Acquire(card);
        await WaitUntil(() => card.Thumbnail is not null);
        var old = card.Thumbnail!;

        fixture.Store.Block(1);
        fixture.Pipeline.Configure(GalleryMetrics.MaxRowHeight("Medium"), 1.5);
        Assert.Equal((512, 488), (fixture.Pipeline.Tier, fixture.Pipeline.DecodeHeightPx));
        await WaitUntil(() => fixture.Store.Generated.Count == 2);
        await Settle();

        Assert.Same(old, card.Thumbnail);
        Assert.False(FakeDecoder.IsDisposed(old));

        fixture.Store.Open(1);
        await WaitUntil(() => card.Thumbnail is { PixelSize.Height: 488 });
        Assert.Equal(512, fixture.Store.Generated[1].Tier);
        Assert.Equal(FakeDecoder.BytesFor(488), fixture.Pipeline.ResidentBytes);
        await WaitUntil(() => FakeDecoder.IsDisposed(old));
    }

    [AvaloniaFact]
    public async Task UnreadableSource_FallsBackToPlaceholder_WithoutRetryLoop()
    {
        using var fixture = new PipelineFixture();
        fixture.Decoder.Throw = true;
        var broken = Card("broken");
        var missing = new PhotoCardItemViewModel { Key = "missing", PhotoPath = Path.Combine(Path.GetTempPath(), $"missing_{Guid.NewGuid():N}.jpg") };

        fixture.Pipeline.Acquire(broken);
        fixture.Pipeline.Acquire(missing);
        await WaitUntil(() => fixture.Decoder.Attempts == 1);
        await Settle();

        Assert.Null(broken.Thumbnail);
        Assert.Null(missing.Thumbnail);
        Assert.Single(fixture.Store.Generated);
        Assert.Equal(0, fixture.Pipeline.PendingCount);
        Assert.Equal(0, fixture.Pipeline.ResidentBytes);
    }

    [AvaloniaFact]
    public async Task QuickLook_PinsPlaceholderAndOwnsPreview()
    {
        using var fixture = new PipelineFixture(budgetBytes: DefaultBitmapBytes + 1);
        var shown = Card("shown");
        fixture.Pipeline.Acquire(shown);
        await WaitUntil(() => shown.Thumbnail is not null);
        var placeholder = shown.Thumbnail!;
        fixture.Pipeline.Release(shown);

        var quickLook = new QuickLookDialogViewModel(new Localizer(), fixture.Pipeline, i => i == 0 ? shown : null, 1, 0);
        Assert.Same(placeholder, quickLook.CurrentDisplayImage);
        await WaitUntil(() => quickLook.CurrentDisplayImage is { PixelSize.Height: 1024 });
        var preview = quickLook.CurrentDisplayImage!;
        Assert.Equal(1024, fixture.Store.Generated.Last().Tier);

        // 画廊继续滚动：QuickLook 显示中的卡片不被驱逐
        var other = Card("other");
        fixture.Pipeline.Acquire(other);
        await WaitUntil(() => other.Thumbnail is not null);
        fixture.Pipeline.Release(other);
        await Settle();
        Assert.Same(placeholder, shown.Thumbnail);
        Assert.False(FakeDecoder.IsDisposed(placeholder));
        Assert.Null(other.Thumbnail);

        quickLook.Cancel();
        Assert.Null(quickLook.CurrentDisplayImage);
        await WaitUntil(() => FakeDecoder.IsDisposed(preview));

        // 关闭后解除钉住，可以正常驱逐
        var next = Card("next");
        fixture.Pipeline.Acquire(next);
        await WaitUntil(() => next.Thumbnail is not null);
        Assert.Null(shown.Thumbnail);
        await WaitUntil(() => FakeDecoder.IsDisposed(placeholder));
    }

    [AvaloniaFact]
    public async Task QuickLook_PicksUpThumbnailThatArrivesAfterOpening()
    {
        using var fixture = new PipelineFixture();
        fixture.Store.BlockTier(384);
        var card = Card("late");
        var quickLook = new QuickLookDialogViewModel(new Localizer(), fixture.Pipeline, _ => card, 1, 0);
        Assert.Null(quickLook.CurrentDisplayImage);

        // 卡片缩略图被阻塞时预览大图先到
        await WaitUntil(() => quickLook.CurrentDisplayImage is { PixelSize.Height: 1024 });
        fixture.Store.OpenTier();
        await WaitUntil(() => card.Thumbnail is not null);
        Assert.Equal(1024, quickLook.CurrentDisplayImage!.PixelSize.Height);

        quickLook.Cancel();
        Assert.Equal(0, fixture.Pipeline.PinnedBytes);
    }

    private static PhotoCardItemViewModel Card(string name)
    {
        var path = Path.GetFullPath($"/album/{name}.jpg");
        return new PhotoCardItemViewModel
        {
            Key = name,
            PhotoPath = path,
            PhotoFile = new LibraryFile(path, 1234, Modified, Modified),
            PhotoHeader = new ImageHeader(4000, 3000, 6),
        };
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutSeconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("等待缩略图管线状态超时。");
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>让已投递的结果全部回到界面线程。</summary>
    private static async Task Settle()
    {
        for (var i = 0; i < 5; i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private sealed class PipelineFixture : IDisposable
    {
        public PipelineFixture(long budgetBytes = ThumbnailPipeline.DefaultBudgetBytes)
        {
            Pipeline = new ThumbnailPipeline(Store, budgetBytes, Decoder.Decode);
        }

        public FakeStore Store { get; } = new();

        public FakeDecoder Decoder { get; } = new();

        public ThumbnailPipeline Pipeline { get; }

        public void Dispose()
        {
            Store.Dispose();
            Pipeline.Dispose();
        }
    }

    /// <summary>命中集合内的路径走缓存通道；其余生成时返回带序号的内容，可按调用序号阻塞。</summary>
    private sealed class FakeStore : IThumbnailStore, IDisposable
    {
        private readonly ConcurrentDictionary<int, TaskCompletionSource> _gates = new();
        private readonly List<ThumbnailRequest> _generated = [];
        private readonly string _cacheFile = Path.Combine(Path.GetTempPath(), $"lpc_fake_thumb_{Guid.NewGuid():N}.jpg");
        private int _calls;
        private int _blockedTier;
        private TaskCompletionSource? _tierGate;

        public FakeStore() => File.WriteAllText(_cacheFile, "cache");

        public HashSet<string> Cached { get; } = [];

        public bool GeneratedOnUiThread { get; private set; }

        public IReadOnlyList<ThumbnailRequest> Generated
        {
            get
            {
                lock (_generated)
                {
                    return [.. _generated];
                }
            }
        }

        public void Block(int call) => _gates[call] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Open(int call) => _gates[call].TrySetResult();

        public void BlockTier(int tier)
        {
            _blockedTier = tier;
            _tierGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void OpenTier() => _tierGate?.TrySetResult();

        public ThumbnailResult? TryGetCached(ThumbnailRequest request)
        {
            lock (Cached)
            {
                return Cached.Contains(request.Path) ? new ThumbnailResult(ThumbnailOrigin.Cache, _cacheFile, null) : null;
            }
        }

        public async Task<ThumbnailResult?> GetAsync(ThumbnailRequest request, CancellationToken cancellationToken)
        {
            GeneratedOnUiThread |= Dispatcher.UIThread.CheckAccess();
            int call;
            lock (_generated)
            {
                call = _calls++;
                _generated.Add(request);
            }

            if (_gates.TryGetValue(call, out var gate))
            {
                await gate.Task;
            }

            if (request.Tier == _blockedTier && _tierGate is { } tierGate)
            {
                await tierGate.Task;
            }

            return new ThumbnailResult(ThumbnailOrigin.Decoded, null, Encoding.UTF8.GetBytes($"gen:{call}"));
        }

        public void Dispose()
        {
            foreach (var gate in _gates.Values)
            {
                gate.TrySetResult();
            }

            _tierGate?.TrySetResult();

            File.Delete(_cacheFile);
        }
    }

    /// <summary>不做真实解码：按请求高度造 4:3 位图，记录调用线程与内容标记。</summary>
    private sealed class FakeDecoder
    {
        private readonly List<Call> _calls = [];
        private int _attempts;

        public bool Throw { get; set; }

        public int Attempts => Volatile.Read(ref _attempts);

        public IReadOnlyList<Call> Calls
        {
            get
            {
                lock (_calls)
                {
                    return [.. _calls];
                }
            }
        }

        public static long BytesFor(int height) => (long)Width(height) * height * 4;

        public static bool IsDisposed(Bitmap bitmap)
        {
            try
            {
                _ = bitmap.PixelSize;
                return false;
            }
            catch (ObjectDisposedException)
            {
                return true;
            }
        }

        public string TagOf(Bitmap bitmap) => Calls.Single(c => ReferenceEquals(c.Bitmap, bitmap)).Tag;

        public Bitmap Decode(Stream stream, int heightPx)
        {
            Interlocked.Increment(ref _attempts);
            if (Throw)
            {
                throw new InvalidOperationException("corrupt");
            }

            var tag = new StreamReader(stream, Encoding.UTF8).ReadToEnd();
            var bitmap = new WriteableBitmap(new PixelSize(Width(heightPx), heightPx), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            lock (_calls)
            {
                _calls.Add(new Call(heightPx, Dispatcher.UIThread.CheckAccess(), tag, bitmap));
            }

            return bitmap;
        }

        private static int Width(int height) => (int)Math.Round(height * 4.0 / 3.0);

        public sealed record Call(int HeightPx, bool OnUiThread, string Tag, Bitmap Bitmap);
    }
}
