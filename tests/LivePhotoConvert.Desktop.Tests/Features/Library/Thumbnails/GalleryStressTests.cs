using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using LivePhotoConvert.Desktop.Controls;
using LivePhotoConvert.Desktop.Features.Library.Thumbnails;
using LivePhotoConvert.Desktop.Models;
using LivePhotoConvert.Desktop.Tests.Harness;

namespace LivePhotoConvert.Desktop.Tests.Features.Library.Thumbnails;

/// <summary>
/// 1 万张卡片的画廊压力：假扫描结果 + 假缩略图（不做真实解码），在真实外壳里自顶到底滚动，
/// 检查预算、释放时序、占位回退与卡片控件复用。
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
        var list = Assert.IsType<GalleryList>(session.Descendants<ListBox>().Single(l => l.Name == "GalleryListBox"));
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
            var seen = new HashSet<PhotoCardItemViewModel>();
            long peakResident = 0, peakPinned = 0, peakOverBudget = 0;
            var peakAttached = 0;
            var steps = 0;

            void Check(string where)
            {
                var attached = Attached(list);
                seen.UnionWith(attached);
                peakAttached = Math.Max(peakAttached, attached.Count);
                Assert.Equal(attached.Count, session.Descendants<PhotoCardControl>().Count());
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

            // 每步跨 6 屏（相当于拖动滚动条），行全部换新；每一步等缩略图到齐，驱逐在整个滚动过程中持续发生。
            // 每 4 步渲染一帧，检查被驱逐的位图不会在渲染中被使用（等待期间的渲染节拍也会渲染）
            var scrollWatch = Stopwatch.StartNew();
            var y = 0.0;
            while (true)
            {
                scroll.Offset = new Vector(0, y);
                await PumpUntilLoadedAsync(session, list, render: steps % 4 == 0);
                Check($"滚动 {y:F0}");
                steps++;
                var bottom = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
                if (y >= bottom - 1)
                {
                    break;
                }

                y = Math.Min(y + scroll.Viewport.Height * 6, bottom);
            }

            session.Pump();
            scrollWatch.Stop();
            var lastRow = Assert.IsType<PhotoGridRowViewModel>(list.ItemFromContainer(list.GetRealizedContainers().OrderBy(list.IndexFromContainer).Last()));
            Assert.Contains(library.Layout.DisplayedCards[^1], lastRow.Cards);
            Assert.True(seen.Count > CardCount / 10 && store.Requests >= seen.Count, $"应为每一步实例化的卡片加载缩略图：{seen.Count} 张，请求 {store.Requests} 次");
            Assert.True(list.CardControlsCreated <= peakAttached + GalleryList.MaxIdleCards,
                $"卡片控件应在行间复用：新建 {list.CardControlsCreated} 个，同时显示最多 {peakAttached} 张");
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
                $"请求 {store.Requests} 次；卡片控件新建 {list.CardControlsCreated} 个（同时显示最多 {peakAttached} 张）；驻留峰值 {peakResident / 1024 / 1024} MB，钉住峰值 {peakPinned / 1024 / 1024} MB，预算 {pipeline.BudgetBytes / 1024 / 1024} MB，超出预算峰值 {Math.Max(0, peakOverBudget) / 1024 / 1024} MB");
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

    private static ShellSession Open(SyntheticStore store, int budgetMb, int cardCount = CardCount) =>
        SyntheticGallery.Open(store, budgetMb, cardCount);

    private static Task PumpUntilLoadedAsync(ShellSession session, ListBox list, bool render = true) =>
        SyntheticGallery.PumpUntilLoadedAsync(session, list, render);

    private static List<PhotoCardItemViewModel> Attached(ListBox list) => SyntheticGallery.Attached(list);
}
