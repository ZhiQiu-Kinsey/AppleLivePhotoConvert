using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LivePhotoConvert.Core.Media;
using LivePhotoConvert.Core.Media.Thumbnails;
using LivePhotoConvert.Desktop.Features.Library.Gallery;
using LivePhotoConvert.Desktop.Features.Library.Thumbnails;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Models;
using LivePhotoConvert.Desktop.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace LivePhotoConvert.Desktop.Tests.Features.Library.Thumbnails;

/// <summary>
/// 1 万张卡片的画廊压力：假扫描结果 + 假缩略图（不做真实解码），在真实外壳里自顶到底滚动，检查预算、释放时序与占位回退。
/// </summary>
[Collection(ProcessStateCollection.Name)]
public sealed class GalleryStressTests
{
    private const int CardCount = 10_000;

    [AvaloniaFact]
    public async Task TenThousandCards_ScrollTopToBottom_StaysWithinBudget_WithoutDisposedBitmapsOrPlaceholderRegression()
    {
        using var store = new SyntheticStore();
        using var session = Open(store, budgetMb: 64);
        var library = session.Shell.Library;
        var pipeline = Assert.IsType<ThumbnailPipeline>(library.Thumbnails);
        var list = session.Descendants<ListBox>().Single(l => l.Name == "GalleryListBox");
        var view = session.Descendants<Desktop.Features.Library.LibraryView>().Single();

        var disposedErrors = 0;
        void OnFirstChance(object? sender, FirstChanceExceptionEventArgs e)
        {
            if (e.Exception is ObjectDisposedException)
            {
                Interlocked.Increment(ref disposedErrors);
            }
        }

        AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
        try
        {
            var scanWatch = Stopwatch.StartNew();
            library.AlbumDirectory = "/album";
            await library.RefreshAlbumAsync();
            await session.WaitUntilAsync(() => library.Layout.DisplayedCards.Count == CardCount, timeoutSeconds: 30);
            session.Pump();
            scanWatch.Stop();

            var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().First();
            var shownWhileAttached = new HashSet<PhotoCardItemViewModel>();
            long peakResident = 0, peakPinned = 0, peakOverBudget = 0;
            var steps = 0;

            void Check(string where)
            {
                var attached = Attached(list);
                Assert.Equal(attached.Count, view.ThumbnailBinder!.AttachedCardCount);
                Assert.Equal(attached.Count, pipeline.AttachedCount);
                Assert.True(pipeline.ResidentBytes <= pipeline.BudgetBytes + pipeline.PinnedBytes,
                    $"{where}: 驻留 {pipeline.ResidentBytes} 超过预算 {pipeline.BudgetBytes} + 钉住 {pipeline.PinnedBytes}");
                Assert.True(pipeline.PendingCount <= pipeline.AttachedCount, $"{where}: 队列 {pipeline.PendingCount} 超过已实例化卡片 {pipeline.AttachedCount}");
                shownWhileAttached.IntersectWith(attached);
                foreach (var card in shownWhileAttached)
                {
                    Assert.True(card.DisplayImage is not null, $"{where}: {card.FileName} 已显示过缩略图却回退为占位图");
                }

                shownWhileAttached.UnionWith(attached.Where(c => c.DisplayImage is not null));
                peakResident = Math.Max(peakResident, pipeline.ResidentBytes);
                peakPinned = Math.Max(peakPinned, pipeline.PinnedBytes);
                peakOverBudget = Math.Max(peakOverBudget, pipeline.ResidentBytes - pipeline.BudgetBytes);
            }

            // 每步跨 2.5 屏（相当于拖动滚动条），每一步等缩略图到齐：驱逐在整个滚动过程中持续发生；隔一步渲染一帧，
            // 检查被驱逐的位图不会在渲染中被使用
            var scrollWatch = Stopwatch.StartNew();
            var y = 0.0;
            while (true)
            {
                scroll.Offset = new Vector(0, y);
                await PumpUntilLoadedAsync(session, list, render: steps % 2 == 0);
                Check($"滚动 {y:F0}");
                steps++;
                var bottom = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
                if (y >= bottom - 1)
                {
                    break;
                }

                y = Math.Min(y + scroll.Viewport.Height * 2.5, bottom);
            }

            session.Pump();
            scrollWatch.Stop();
            var lastRow = Assert.IsType<PhotoGridRowViewModel>(list.ItemFromContainer(list.GetRealizedContainers().OrderBy(list.IndexFromContainer).Last()));
            Assert.Contains(library.Layout.DisplayedCards[^1], lastRow.Cards);
            Assert.True(store.Requests > CardCount / 4, "应为每一步实例化的卡片加载缩略图");
            Assert.True(library.AllCards.Count(c => c.Thumbnail is not null) * SyntheticStore.BytesAt(pipeline.DecodeHeightPx) <= pipeline.BudgetBytes + peakPinned,
                "滚过的卡片不应全部留在内存");

            // 等延迟释放执行完，被驱逐的位图不再被任何卡片引用
            await Task.Delay(300, TestContext.Current.CancellationToken);
            session.Pump();
            Assert.Equal(0, disposedErrors);
            Assert.All(Attached(list), c => Assert.False(SyntheticStore.IsDisposed(c.DisplayImage!)));
            session.Log.AssertNoBindingErrors();

            TestContext.Current.TestOutputHelper?.WriteLine(
                $"1 万张卡片：扫描到排版 {scanWatch.ElapsedMilliseconds} ms；滚动 {steps} 步共 {scrollWatch.ElapsedMilliseconds} ms（每步 {scrollWatch.ElapsedMilliseconds / (double)steps:F1} ms）；" +
                $"请求 {store.Requests} 次；驻留峰值 {peakResident / 1024 / 1024} MB，钉住峰值 {peakPinned / 1024 / 1024} MB，预算 {pipeline.BudgetBytes / 1024 / 1024} MB，超出预算峰值 {Math.Max(0, peakOverBudget) / 1024 / 1024} MB");
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= OnFirstChance;
        }
    }

    /// <summary>预算从 192MB 调到 64MB：立即驱逐到新预算内，只驱逐未钉住的位图，正在显示的卡片不受影响。</summary>
    [AvaloniaFact]
    public async Task LoweringBudget_EvictsUnpinnedBitmapsImmediately()
    {
        using var store = new SyntheticStore();
        using var session = Open(store, budgetMb: 192, cardCount: 1_500);
        var library = session.Shell.Library;
        var pipeline = Assert.IsType<ThumbnailPipeline>(library.Thumbnails);
        var list = session.Descendants<ListBox>().Single(l => l.Name == "GalleryListBox");
        library.AlbumDirectory = "/album";
        await library.RefreshAlbumAsync();
        await session.WaitUntilAsync(() => library.Layout.DisplayedCards.Count == 1_500, timeoutSeconds: 30);
        session.Pump();

        // 向下滚动若干屏，让 192MB 预算里积累足够多的离屏位图
        var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().First();
        for (var y = 0.0; pipeline.ResidentBytes - pipeline.PinnedBytes < 100L * 1024 * 1024; y += scroll.Viewport.Height * 0.9)
        {
            scroll.Offset = new Vector(0, y);
            await PumpUntilLoadedAsync(session, list);
            Assert.True(y < scroll.Extent.Height, "内容不足以填满预算");
        }

        var attached = Attached(list);
        var pinnedBitmaps = attached.Select(c => c.DisplayImage!).ToList();
        var pinned = pipeline.PinnedBytes;
        Assert.True(pipeline.ResidentBytes > 64L * 1024 * 1024);

        session.Shell.Settings.ThumbnailBudgetMb = 64;

        Assert.Equal(64L * 1024 * 1024, pipeline.BudgetBytes);
        Assert.True(pipeline.ResidentBytes <= Math.Max(pipeline.BudgetBytes, pinned), $"驻留 {pipeline.ResidentBytes} 未回到新预算内");
        Assert.Equal(pinned, pipeline.PinnedBytes);
        Assert.All(attached, c => Assert.NotNull(c.DisplayImage));
        Assert.Equal(pinnedBitmaps, attached.Select(c => c.DisplayImage!));
        await Task.Delay(300, TestContext.Current.CancellationToken);
        session.Pump();
        Assert.All(pinnedBitmaps, b => Assert.False(SyntheticStore.IsDisposed(b)));
        Assert.Equal(64, session.Host.Settings.Current.Gallery.ThumbnailBudgetMb);
    }

    /// <summary>
    /// 让新位置的行实例化并等缩略图到齐。每张缩略图都要回到界面线程交付后 worker 才继续，
    /// 这里只推进调度器、按需渲染一帧：整窗渲染的代价远大于交付本身。
    /// </summary>
    private static async Task PumpUntilLoadedAsync(ShellSession session, ListBox list, bool render = true)
    {
        Dispatcher.UIThread.RunJobs();
        session.Window.UpdateLayout();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!Attached(list).All(c => c.DisplayImage is not null))
        {
            Assert.True(DateTime.UtcNow < deadline, "等待缩略图超时");
            await Task.Delay(1, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
        }

        if (render)
        {
            session.Pump();
        }
    }

    private static ShellSession Open(SyntheticStore store, int budgetMb, int cardCount = CardCount)
    {
        var items = SyntheticItems(cardCount);
        var session = new ShellSession(
            configure: services =>
            {
                services.AddSingleton(sp => new LibraryCatalog(
                    sp.GetRequiredService<ILocalizer>(), new NoEnrichment(),
                    (_, _, _) => Task.FromResult(new LibraryScanResult(items, cardCount * 2, 0))));
                services.AddSingleton<IThumbnailPipeline>(sp => new ThumbnailPipeline(
                    store, sp.GetRequiredService<SettingsStore>().Current.Gallery.ThumbnailBudgetBytes, SyntheticStore.Decode));
            },
            settings: s => s.Gallery.ThumbnailBudgetMb = budgetMb);
        // 接近常见桌面窗口：每屏实例化的卡片更多，钉住字节与驱逐压力更接近实际
        session.Window.Width = 1600;
        session.Window.Height = 1100;
        session.Pump();
        return session;
    }

    /// <summary>实况对，横竖混排；时间递增使分组与排序稳定。</summary>
    private static List<LibraryItem> SyntheticItems(int count) =>
        [.. Enumerable.Range(0, count).Select(i => Cards.ApplePairItem(
            $"IMG_{i:D5}",
            aspect: (i % 7) switch { 0 => 0.75, 3 => 16.0 / 9.0, 5 => 1.0, _ => 4.0 / 3.0 },
            taken: new DateTime(2025, 1, 1).AddMinutes(i * 7)))];

    private static List<PhotoCardItemViewModel> Attached(ListBox list) =>
        [.. list.GetRealizedContainers().Select(list.ItemFromContainer).OfType<PhotoGridRowViewModel>().SelectMany(r => r.Cards)];

    private sealed class NoEnrichment : ILibraryEnricher
    {
        public async IAsyncEnumerable<LibraryItem> EnrichAsync(IReadOnlyList<LibraryItem> items, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    /// <summary>三分之二命中“磁盘缓存”（共用一个占位文件），其余走生成通道；解码直接按请求高度造 4:3 位图。</summary>
    private sealed class SyntheticStore : IThumbnailStore, IDisposable
    {
        private readonly string _cacheFile = Path.Combine(Path.GetTempPath(), $"lpc_stress_thumb_{Guid.NewGuid():N}.jpg");
        private int _requests;

        public SyntheticStore() => File.WriteAllText(_cacheFile, "cache");

        public int Requests => Volatile.Read(ref _requests);

        public static long BytesAt(int height) => (long)Width(height) * height * 4;

        public static Bitmap Decode(Stream stream, int heightPx) =>
            new WriteableBitmap(new PixelSize(Width(heightPx), heightPx), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

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

        public ThumbnailResult? TryGetCached(ThumbnailRequest request)
        {
            Interlocked.Increment(ref _requests);
            return request.Path[^6] % 3 == 0 ? null : new ThumbnailResult(ThumbnailOrigin.Cache, _cacheFile, null);
        }

        public Task<ThumbnailResult?> GetAsync(ThumbnailRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<ThumbnailResult?>(new ThumbnailResult(ThumbnailOrigin.Decoded, null, Encoding.UTF8.GetBytes("generated")));

        public void Dispose() => File.Delete(_cacheFile);

        private static int Width(int height) => (int)Math.Round(height * 4.0 / 3.0);
    }
}
