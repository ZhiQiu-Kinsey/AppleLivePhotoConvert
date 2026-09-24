using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using LivePhotoConvert.Desktop.Features.Library;
using LivePhotoConvert.Desktop.Features.Library.Thumbnails;
using LivePhotoConvert.Desktop.Models;
using LivePhotoConvert.Desktop.Tests.Harness;

namespace LivePhotoConvert.Desktop.Tests.Features.Library.Thumbnails;

/// <summary>真实外壳窗口 + 真实缩略图引擎：滚动全程检查驻留预算、占位回退与队列长度。</summary>
public sealed class GalleryScrollTests : IDisposable
{
    private const int PairCount = 180;
    private const int BudgetMb = 64;

    private readonly TestSandbox _album = new();

    public void Dispose() => _album.Dispose();

    [AvaloniaFact]
    public async Task ScrollingToBottom_KeepsAttachedCardsLoaded_AndStaysWithinBudget()
    {
        SampleAlbum.WriteApplePairs(_album.InputDirectory, PairCount);
        using var session = new ShellSession(settings: s => s.Gallery.ThumbnailBudgetMb = BudgetMb);
        var library = session.Shell.Library;
        var pipeline = Assert.IsType<ThumbnailPipeline>(library.Thumbnails);
        var list = session.Descendants<ListBox>().Single(l => l.Name == "GalleryListBox");
        var view = session.Descendants<LibraryView>().Single();

        // 实测容器事件时 DataContext 的状态：准备时已指向条目；回收时记录是否仍指向旧条目
        int preparedWithItem = 0, preparedOther = 0, clearingWithRow = 0, clearingOther = 0;
        list.ContainerPrepared += (_, e) =>
        {
            if (ReferenceEquals(e.Container.DataContext, list.ItemsView[e.Index])) preparedWithItem++;
            else preparedOther++;
        };
        list.ContainerClearing += (_, e) =>
        {
            if (e.Container.DataContext is PhotoGridRowViewModel) clearingWithRow++;
            else clearingOther++;
        };

        library.AlbumDirectory = _album.InputDirectory;
        await library.RefreshAlbumAsync();
        await session.WaitUntilAsync(() => library.AllCards.Count == PairCount);
        session.Pump();
        Assert.Equal(BudgetMb * 1024L * 1024, pipeline.BudgetBytes);

        var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().First();
        var shownWhileAttached = new HashSet<PhotoCardItemViewModel>();

        void CheckInvariants(string where)
        {
            var attached = AttachedCards(list);
            Assert.Equal(attached.Count, view.ThumbnailBinder!.AttachedCardCount);
            Assert.Equal(attached.Count, pipeline.AttachedCount);
            Assert.True(pipeline.PendingCount <= pipeline.AttachedCount,
                $"{where}: 队列 {pipeline.PendingCount} 超过已实例化卡片 {pipeline.AttachedCount}");
            if (pipeline.PinnedBytes <= pipeline.BudgetBytes)
            {
                Assert.True(pipeline.ResidentBytes <= pipeline.BudgetBytes,
                    $"{where}: 驻留 {pipeline.ResidentBytes} 超出预算 {pipeline.BudgetBytes}");
            }

            shownWhileAttached.IntersectWith(attached);
            foreach (var card in shownWhileAttached)
            {
                Assert.True(card.DisplayImage is not null, $"{where}: {card.FileName} 已显示过缩略图却回退为占位图");
            }

            shownWhileAttached.UnionWith(attached.Where(c => c.DisplayImage is not null));
        }

        // 快速滚动：不等缩略图，逐帧检查。虚拟化面板的总高度是估算值，边滚边更新，每步重新读取底部位置
        var y = 0.0;
        while (true)
        {
            scroll.Offset = new Vector(0, y);
            session.Pump();
            CheckInvariants($"快速滚动 {y:F0}");
            var bottom = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
            if (y >= bottom - 1)
            {
                break;
            }

            y = Math.Min(y + scroll.Viewport.Height * 0.9, bottom);
        }

        Assert.True(y > scroll.Viewport.Height * 5, $"样例相册应有多屏内容：extent={scroll.Extent} viewport={scroll.Viewport}");

        // 慢速滚动回顶：每一屏等缩略图到齐再检查
        for (; y >= 0; y -= scroll.Viewport.Height * 0.6)
        {
            scroll.Offset = new Vector(0, y);
            session.Pump();
            await session.WaitUntilAsync(() => AttachedCards(list).All(c => c.DisplayImage is not null), timeoutSeconds: 20);
            CheckInvariants($"慢速滚动 {y:F0}");
        }

        Assert.True(pipeline.ResidentBytes <= pipeline.BudgetBytes);
        Assert.True(library.AllCards.Count(c => c.Thumbnail is not null) < PairCount, "预算应迫使部分离屏位图被驱逐");
        Assert.Equal(0, preparedOther);
        Assert.True(preparedWithItem > 0);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"ContainerPrepared: DataContext 已是条目 {preparedWithItem} 次，其它 {preparedOther} 次；" +
            $"ContainerClearing: DataContext 仍是行 {clearingWithRow} 次，其它 {clearingOther} 次；" +
            $"驻留 {pipeline.ResidentBytes / 1024 / 1024}MB / 预算 {BudgetMb}MB");
        session.Log.AssertNoBindingErrors();
    }

    [AvaloniaFact]
    public async Task ScaleModeChange_ReconfiguresTier_WithoutDroppingShownThumbnails()
    {
        SampleAlbum.WriteApplePairs(_album.InputDirectory, 12);
        using var session = new ShellSession();
        var library = session.Shell.Library;
        var pipeline = Assert.IsType<ThumbnailPipeline>(library.Thumbnails);
        library.AlbumDirectory = _album.InputDirectory;
        await library.RefreshAlbumAsync();
        var list = session.Descendants<ListBox>().Single(l => l.Name == "GalleryListBox");
        await session.WaitUntilAsync(() => AttachedCards(list) is { Count: > 0 } cards && cards.All(c => c.DisplayImage is not null));
        Assert.Equal((384, 325), (pipeline.Tier, pipeline.DecodeHeightPx));

        library.SetScaleModeCommand.Execute("Large");
        session.Pump();
        Assert.Equal((512, 416), (pipeline.Tier, pipeline.DecodeHeightPx));
        Assert.All(AttachedCards(list), c => Assert.NotNull(c.DisplayImage));

        // 样例图高 360/480 像素：新档位下按原高解码，不在内存里放大
        await session.WaitUntilAsync(() => AttachedCards(list).All(c => c.Thumbnail is { PixelSize.Height: 360 or 416 }));
    }

    private static List<PhotoCardItemViewModel> AttachedCards(ListBox list) =>
        [.. list.GetRealizedContainers()
            .Select(list.ItemFromContainer)
            .OfType<PhotoGridRowViewModel>()
            .SelectMany(r => r.Cards)];
}
