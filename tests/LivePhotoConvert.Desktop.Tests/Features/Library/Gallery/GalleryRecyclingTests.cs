using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using LivePhotoConvert.Desktop.Controls;
using LivePhotoConvert.Desktop.Features.Dialogs;
using LivePhotoConvert.Desktop.Features.Library;
using LivePhotoConvert.Desktop.Features.Library.Thumbnails;
using LivePhotoConvert.Desktop.Features.Playback;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Models;
using LivePhotoConvert.Desktop.Tests.Features.Library.Thumbnails;
using LivePhotoConvert.Desktop.Tests.Features.Playback;
using LivePhotoConvert.Desktop.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace LivePhotoConvert.Desktop.Tests.Features.Library.Gallery;

/// <summary>
/// 画廊行容器与卡片控件的复用：滚动往返与重排后控件与数据一致、钉住计数平衡、驻留不超预算；
/// 回收即停悬浮播放；复用过的卡片上选择、双击预览与人工裁决照常工作。
/// </summary>
[Collection(ProcessStateCollection.Name)]
public sealed class GalleryRecyclingTests
{
    private const int CardCount = 1_500;

    [AvaloniaFact]
    public async Task ScrollingDownAndBack_ReusesCardControls_AndKeepsThemInSyncWithRows()
    {
        using var store = new SyntheticStore();
        using var session = SyntheticGallery.Open(store, budgetMb: 64, CardCount);
        await SyntheticGallery.ScanAsync(session, CardCount);
        var gallery = new Gallery(session);
        await SyntheticGallery.PumpUntilLoadedAsync(session, gallery.List);
        gallery.Check("初始");

        // 第一趟向下：池子长到同时显示的卡片数为止
        const int steps = 40;
        for (var i = 1; i <= steps; i++)
        {
            await gallery.ScrollToAsync(gallery.Scroll.Viewport.Height * 0.5 * i, render: i % 8 == 0);
            gallery.Check($"向下 {i}");
        }

        var created = gallery.List.CardControlsCreated;
        Assert.True(created <= gallery.PeakAttached + GalleryList.MaxIdleCards, $"新建 {created} 个，同时显示最多 {gallery.PeakAttached} 张");

        // 回到顶部再向下一趟：只换数据上下文，不再新建卡片控件
        var times = new List<double>();
        long allocated = 0;
        for (var i = steps; i >= 0; i--)
        {
            await gallery.ScrollToAsync(gallery.Scroll.Viewport.Height * 0.5 * i, render: i % 8 == 0);
            gallery.Check($"向上 {i}");
        }

        for (var i = 1; i <= steps; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            gallery.Scroll.Offset = new Vector(0, gallery.Scroll.Viewport.Height * 0.5 * i);
            session.Window.UpdateLayout();
            times.Add(watch.Elapsed.TotalMilliseconds);
            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
            await SyntheticGallery.PumpUntilLoadedAsync(session, gallery.List, render: i % 8 == 0);
            gallery.Check($"再向下 {i}");
        }

        Assert.Equal(created, gallery.List.CardControlsCreated);
        session.Log.AssertNoBindingErrors();
        times.Sort();
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"每步半屏：布局中位 {times[steps / 2]:F1} ms、P90 {times[steps * 9 / 10]:F1} ms，界面线程分配每步 {allocated / steps / 1024.0:F0} KB；" +
            $"卡片控件共新建 {created} 个，同时显示最多 {gallery.PeakAttached} 张，空闲 {gallery.List.IdleCardCount} 个");
    }

    /// <summary>重排改变行的组成（换宽度、换行高档位）：行内卡片原地换数据，多出的归还、不足的借用，页面切换不重建。</summary>
    [AvaloniaFact]
    public async Task Relayout_AndPageSwitch_KeepControlsInSync_AndPinsBalanced()
    {
        using var store = new SyntheticStore();
        using var session = SyntheticGallery.Open(store, budgetMb: 64, 400);
        await SyntheticGallery.ScanAsync(session, 400);
        var library = session.Shell.Library;
        var pipeline = Assert.IsType<ThumbnailPipeline>(library.Thumbnails);
        var gallery = new Gallery(session);
        await gallery.ScrollToAsync(gallery.Scroll.Viewport.Height * 3, render: true);
        gallery.Check("滚动后");

        foreach (var scale in new[] { "Small", "Large", "Medium" })
        {
            library.Layout.SetScaleModeCommand.Execute(scale);
            await SyntheticGallery.PumpUntilLoadedAsync(session, gallery.List);
            gallery.Check($"行高 {scale}");
        }

        session.Window.Width -= 420;
        session.Pump();
        library.Layout.ApplyViewportWidth(library.Layout.ViewportWidth - 420);
        await SyntheticGallery.PumpUntilLoadedAsync(session, gallery.List);
        gallery.Check("变窄");
        Assert.True(gallery.List.IdleCardCount <= GalleryList.MaxIdleCards);

        var created = gallery.List.CardControlsCreated;
        session.Navigate(AppPage.Tasks);
        Assert.Equal(0, pipeline.AttachedCount);
        Assert.Equal(0, pipeline.PinnedBytes);
        session.Navigate(AppPage.Library);
        await SyntheticGallery.PumpUntilLoadedAsync(session, gallery.List);
        gallery.Check("切回");
        Assert.Equal(created, gallery.List.CardControlsCreated);
        session.Log.AssertNoBindingErrors();
    }

    /// <summary>行被回收、卡片控件归还列表时停止悬浮播放；这里绕开滚动与重排，只触发回收。</summary>
    [AvaloniaFact]
    public async Task ReturningCardToPool_StopsHoverPlayback()
    {
        var players = new FakePlayers();
        using var store = new SyntheticStore();
        using var session = SyntheticGallery.Open(store, budgetMb: 64, 40, services => services.AddSingleton(_ => players.CreateService()));
        await SyntheticGallery.ScanAsync(session, 40);
        var library = session.Shell.Library;
        var control = session.Descendants<PhotoCardControl>().First(c => c.IsEffectivelyVisible);
        var preview = control.GetVisualDescendants().OfType<Panel>().Single(p => p.Name == "PreviewArea");
        session.Window.MouseMove(preview.TranslatePoint(new Point(preview.Bounds.Width / 2, preview.Bounds.Height / 2), session.Window)!.Value);
        session.Pump();
        var hover = players.Created[0];
        await session.WaitUntilAsync(() => hover.State.Status == PlayerStatus.Playing);
        Assert.NotNull(control.PlaybackFrame);

        var items = library.Layout.Items.ToList();
        library.Layout.Items.Reset([]);
        session.Pump();

        Assert.False(control.IsAttachedToVisualTree());
        Assert.Equal(PlayerStatus.Idle, hover.State.Status);
        Assert.Null(control.PlaybackFrame);
        library.Layout.Items.Reset(items);
        session.Pump();
        Assert.DoesNotContain(session.Descendants<PhotoCardControl>(), c => c.PlaybackFrame is not null);
    }

    /// <summary>滚动往返后复用的卡片：单击选择、双击预览、点击裁决按钮都作用于它当前显示的卡片。</summary>
    [AvaloniaFact]
    public async Task ReusedCards_StillSelectOpenQuickLookAndArbitrate()
    {
        using var store = new SyntheticStore();
        using var session = SyntheticGallery.Open(store, budgetMb: 64, 300);
        await SyntheticGallery.ScanAsync(session, 300);
        var library = session.Shell.Library;
        var gallery = new Gallery(session);
        for (var i = 1; i <= 12; i++)
        {
            await gallery.ScrollToAsync(gallery.Scroll.Viewport.Height * 0.7 * i, render: false);
        }

        await gallery.ScrollToAsync(gallery.Scroll.Viewport.Height * 2, render: true);
        var created = gallery.List.CardControlsCreated;
        gallery.Check("往返后");

        var (plain, plainCard) = gallery.Visible(c => !c.RequiresPairReview);
        Click(session, plain, new Point(plain.Bounds.Width / 2, plainCard.PreviewHeight / 2));
        Assert.Equal([plainCard], library.Layout.DisplayedCards.Where(c => c.IsSelected));

        Click(session, plain, new Point(plain.Bounds.Width / 2, plainCard.PreviewHeight / 2), clickCount: 2);
        var quickLook = Assert.IsType<QuickLookDialogViewModel>(session.Dialogs.Current);
        Assert.Same(plainCard, quickLook.Card);
        session.PressEscape();
        Assert.Null(session.Dialogs.Current);

        var (review, reviewCard) = gallery.Visible(c => c.RequiresPairReview);
        session.Click(GallerySelectionViewTests.StatusBadge(review));
        var arbitrate = Assert.IsType<ArbitrateDialogViewModel>(session.Dialogs.Current);
        Assert.Same(reviewCard, arbitrate.TargetCard);
        Assert.Equal([plainCard], library.Layout.DisplayedCards.Where(c => c.IsSelected));
        session.PressEscape();
        Assert.Null(session.Dialogs.Current);

        Assert.Equal(created, gallery.List.CardControlsCreated);
        session.Log.AssertNoBindingErrors();
    }

    private static void Click(ShellSession session, Control control, Point local, int clickCount = 1)
    {
        session.Pump();
        var point = control.TranslatePoint(local, session.Window)!.Value;
        session.Window.MouseMove(point);
        for (var i = 0; i < clickCount; i++)
        {
            session.Window.MouseDown(point, MouseButton.Left);
            session.Window.MouseUp(point, MouseButton.Left);
        }

        // 移开指针：下一次点击不会与这一次合并为双击
        session.Window.MouseMove(new Point(1, 1));
        session.Pump();
    }

    /// <summary>画廊列表与检查：每个已实例化的行容器显示的正是它的行，卡片控件与数据逐位一致。</summary>
    private sealed class Gallery
    {
        private readonly ShellSession _session;
        private readonly ThumbnailPipeline _pipeline;
        private readonly LibraryView _view;
        private readonly HashSet<PhotoCardItemViewModel> _shown = [];

        public Gallery(ShellSession session)
        {
            _session = session;
            _pipeline = Assert.IsType<ThumbnailPipeline>(session.Shell.Library.Thumbnails);
            _view = session.Descendants<LibraryView>().Single();
            List = Assert.IsType<GalleryList>(session.Descendants<ListBox>().Single(l => l.Name == "GalleryListBox"));
            Scroll = List.GetVisualDescendants().OfType<ScrollViewer>().First();
        }

        public GalleryList List { get; }

        public ScrollViewer Scroll { get; }

        public int PeakAttached { get; private set; }

        public async Task ScrollToAsync(double y, bool render)
        {
            Scroll.Offset = new Vector(0, Math.Min(y, Math.Max(0, Scroll.Extent.Height - Scroll.Viewport.Height)));
            await SyntheticGallery.PumpUntilLoadedAsync(_session, List, render);
        }

        public (PhotoCardControl Control, PhotoCardItemViewModel Card) Visible(Func<PhotoCardItemViewModel, bool> predicate)
        {
            var viewport = new Rect(Scroll.Bounds.Size);
            foreach (var control in _session.Descendants<PhotoCardControl>())
            {
                if (control.DataContext is PhotoCardItemViewModel card && predicate(card) &&
                    control.TranslatePoint(default, Scroll) is { } top && viewport.Contains(new Rect(top, control.Bounds.Size)))
                {
                    return (control, card);
                }
            }

            throw new InvalidOperationException("视口中没有符合条件的卡片。");
        }

        public void Check(string where)
        {
            var rows = List.GetRealizedContainers().OfType<GalleryRowPresenter>().ToList();
            Assert.NotEmpty(rows);
            foreach (var row in rows)
            {
                var item = Assert.IsType<PhotoGridRowViewModel>(List.ItemFromContainer(row));
                Assert.Same(item, row.Row);
                Assert.Equal<object?>(item.Cards, row.Cards.Select(c => c.DataContext));
                Assert.All(row.Cards, c => Assert.Same(row, c.GetVisualParent()));
                // 布局取整后宽度与排版结果相差不到 1 像素
                Assert.All(row.Cards, c => Assert.Equal(((PhotoCardItemViewModel)c.DataContext!).DisplayWidth, c.Bounds.Width, 1.0));
            }

            var attached = SyntheticGallery.Attached(List);
            PeakAttached = Math.Max(PeakAttached, attached.Count);
            Assert.Equal(attached.Count, _session.Descendants<PhotoCardControl>().Count());
            Assert.Equal(List.CardControlCount, attached.Count + List.IdleCardCount);
            Assert.Equal(attached.Count, _view.ThumbnailBinder!.AttachedCardCount);
            Assert.Equal(attached.Count, _pipeline.AttachedCount);
            Assert.True(_pipeline.ResidentBytes <= _pipeline.BudgetBytes + _pipeline.PinnedBytes,
                $"{where}: 驻留 {_pipeline.ResidentBytes} 超过预算 {_pipeline.BudgetBytes} + 钉住 {_pipeline.PinnedBytes}");
            _shown.IntersectWith(attached);
            Assert.All(_shown, c => Assert.True(c.DisplayImage is not null, $"{where}: {c.FileName} 已显示过缩略图却回退为占位图"));
            _shown.UnionWith(attached.Where(c => c.DisplayImage is not null));
        }
    }
}
