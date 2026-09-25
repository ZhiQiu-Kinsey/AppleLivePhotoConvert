using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using LivePhotoConvert.Core.Tests.Support;
using LivePhotoConvert.Desktop.Controls;
using LivePhotoConvert.Desktop.Features.Dialogs;
using LivePhotoConvert.Desktop.Features.Library;
using LivePhotoConvert.Desktop.Features.Library.Gallery;
using LivePhotoConvert.Desktop.Features.Library.Thumbnails;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Models;
using LivePhotoConvert.Desktop.Tests.Features.Library.Thumbnails;
using LivePhotoConvert.Desktop.Tests.Harness;

namespace LivePhotoConvert.Desktop.Tests.Features.Library.Gallery;

/// <summary>真实外壳中的画廊：页面切换释放钉住、重排后按锚点保持滚动、语言切换刷新文案、预览序列只含显示中的卡片。</summary>
[Collection(ProcessStateCollection.Name)]
public sealed class GalleryViewTests : IDisposable
{
    private readonly TestSandbox _album = new();

    public void Dispose() => _album.Dispose();

    [AvaloniaFact]
    public async Task HiddenPage_ReleasesPins_AndShowingAgainReacquires()
    {
        SampleAlbum.WriteApplePairs(_album.InputDirectory, 12);
        using var session = new ShellSession();
        var library = session.Shell.Library;
        var pipeline = Assert.IsType<ThumbnailPipeline>(library.Thumbnails);
        var view = session.Descendants<LibraryView>().Single();
        await ScanAsync(session, 12);
        await session.WaitUntilAsync(() => pipeline.AttachedCount > 0 && Attached(session).All(c => c.DisplayImage is not null));
        Assert.True(pipeline.PinnedBytes > 0);

        session.Navigate(AppPage.Tasks);
        Assert.Null(view.ThumbnailBinder);
        Assert.Equal(0, pipeline.AttachedCount);
        Assert.Equal(0, pipeline.PinnedBytes);
        Assert.True(pipeline.ResidentBytes > 0, "放开钉住不等于立即释放：预算内的位图留待切回时复用");

        session.Navigate(AppPage.Library);
        Assert.NotNull(view.ThumbnailBinder);
        Assert.True(pipeline.AttachedCount > 0);
        Assert.Equal(Attached(session).Count, pipeline.AttachedCount);
        Assert.All(Attached(session), c => Assert.NotNull(c.DisplayImage));
        session.Log.AssertNoBindingErrors();
    }

    /// <summary>视口宽度变化引起重排：原来位于视口顶部的卡片重排后仍在同一位置。</summary>
    [AvaloniaFact]
    public async Task Relayout_KeepsTheTopCardInPlace()
    {
        SampleAlbum.WriteApplePairs(_album.InputDirectory, 90);
        using var session = new ShellSession();
        var library = session.Shell.Library;
        await ScanAsync(session, 90);
        var list = session.Descendants<ListBox>().Single(l => l.Name == "GalleryListBox");
        var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().First();

        scroll.Offset = new Vector(0, scroll.Extent.Height * 0.45);
        session.Pump();
        var (key, top) = TopItem(list, scroll);
        var card = library.Layout.DisplayedCards.First(c => c.Key == key);
        var widthBefore = library.Layout.ViewportWidth;

        // 保持在检查器自动收起的阈值之上，只让画廊变窄
        session.Window.Width -= 180;
        session.Pump();
        Assert.False(session.Shell.Inspector.IsCollapsed);
        await session.WaitUntilAsync(() => Math.Abs(library.Layout.ViewportWidth - widthBefore) > 100);
        session.Pump();
        session.Pump();

        var index = library.Layout.IndexOf(key);
        var container = list.ContainerFromIndex(index);
        Assert.NotNull(container);
        Assert.Contains(card, Assert.IsType<PhotoGridRowViewModel>(list.ItemFromContainer(container!)).Cards);
        var after = container!.TranslatePoint(default, scroll)!.Value.Y;
        Assert.InRange(after - top, -2, 2);
        session.Log.AssertNoBindingErrors();
    }

    /// <summary>滚动停下后推进缩略图代次：之后入队的请求排在滚动途中的请求之前。</summary>
    [AvaloniaFact]
    public async Task ScrollingThenIdle_AdvancesThumbnailGeneration()
    {
        SampleAlbum.WriteApplePairs(_album.InputDirectory, 40);
        using var session = new ShellSession();
        var pipeline = Assert.IsType<ThumbnailPipeline>(session.Shell.Library.Thumbnails);
        await ScanAsync(session, 40);
        var scroll = session.Descendants<ListBox>().Single(l => l.Name == "GalleryListBox").GetVisualDescendants().OfType<ScrollViewer>().First();
        var before = pipeline.Generation;

        scroll.Offset = new Vector(0, scroll.Viewport.Height);
        session.Pump();

        await session.WaitUntilAsync(() => pipeline.Generation > before);
    }

    [AvaloniaFact]
    public async Task LanguageSwitch_RefreshesGroupTitlesAndCardLabels()
    {
        for (var i = 0; i < 3; i++)
        {
            var cover = File.ReadAllBytes(SampleAlbum.WriteJpeg(Path.Combine(_album.RootDirectory, $"cover{i}.jpg"), i));
            var path = _album.CreateInputFile($"MVIMG_2025051{i}_101010.jpg", SyntheticMedia.MotionPhoto(cover, SyntheticMedia.Mp4(4096)));
            File.SetLastWriteTime(path, new DateTime(2025, 5, 10 + i, 10, 10, 10));
        }

        using var session = new ShellSession(settings: s =>
        {
            s.Action = ConversionAction.ToApple;
            s.Gallery.Grouping = "Month";
        });
        var library = session.Shell.Library;
        await ScanAsync(session, 3);
        var header = Assert.Single(library.Layout.Items.OfType<TimelineHeaderItemViewModel>());
        Assert.Equal("2025年5月", header.Title);
        Assert.All(library.AllCards, c => Assert.Equal("动态照片", c.DeviceInfo));
        var shownZh = UiTexts.Collect(session).Select(t => t.Text).ToHashSet();
        Assert.Contains("2025年5月", shownZh);
        Assert.Contains("动态照片", shownZh);

        session.Shell.Settings.SetLanguageCommand.Execute("en");
        session.Pump();

        Assert.Equal(GalleryOrdering.FormatTitle(session.Localizer, GalleryGrouping.Month, header.Period), header.Title);
        Assert.DoesNotMatch(@"\p{IsCJKUnifiedIdeographs}", header.Title);
        Assert.All(library.AllCards, c => Assert.Equal("Motion Photo", c.DeviceInfo));
        Assert.All(library.AllCards, c => Assert.Equal(c.DateTaken.ToString(session.Localizer["DateGroupFormat"], session.Localizer.Culture), c.FormattedDate));
        var shownEn = UiTexts.Collect(session).Select(t => t.Text).ToHashSet();
        Assert.Contains(header.Title, shownEn);
        Assert.Contains("Motion Photo", shownEn);
        UiTexts.AssertNoChinese(session, "图库（英文）");
        session.Log.AssertNoBindingErrors();
    }

    /// <summary>苹果实况的 HEIC 增益图与安卓动态照片的 Ultra HDR 都显示 HDR 徽章，普通照片不显示；切换语言后仍然正确。</summary>
    [AvaloniaFact]
    public async Task HdrCards_ShowHdrBadge_OnlyForHdrPhotos()
    {
        _album.CreateInputFile("IMG_0001.heic", SyntheticImages.Heif(400, 300, appleGainMap: true));
        _album.CreateInputFile("IMG_0001.mov", SyntheticMedia.Mov());
        _album.CreateInputFile("IMG_0002.heic", SyntheticImages.Heif(400, 300));
        _album.CreateInputFile("IMG_0002.mov", SyntheticMedia.Mov());
        _album.CreateInputFile("MVIMG_0003.jpg", SyntheticMedia.MotionPhotoWithGainMap(SyntheticMedia.Jpeg(700)));
        _album.CreateInputFile("MVIMG_0004.jpg", SyntheticMedia.MotionPhoto());
        // 瘦身同时列出苹果实况对与安卓动态照片
        using var session = new ShellSession(settings: s => s.Action = ConversionAction.Strip);
        await ScanAsync(session, 4);

        AssertHdrBadges(session);
        session.Shell.Settings.SetLanguageCommand.Execute("en");
        session.Pump();
        AssertHdrBadges(session);
        Screenshots.Save(session, "gallery-hdr-badges-en");
        session.Log.AssertNoBindingErrors();

        static void AssertHdrBadges(ShellSession session)
        {
            var hdrText = session.Localizer["CardHdrBadge"];
            var badged = session.Descendants<PhotoCardControl>()
                .Where(c => c.IsEffectivelyVisible && c.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == hdrText && t.IsEffectivelyVisible))
                .Select(c => ((PhotoCardItemViewModel)c.DataContext!).FileName)
                .Order()
                .ToList();
            Assert.Equal(["IMG_0001", "MVIMG_0003"], badged);
        }
    }

    /// <summary>预览序列只含画廊当前显示的卡片：筛选掉的卡片不能左右切换到。</summary>
    [AvaloniaFact]
    public async Task QuickLook_SequenceContainsOnlyDisplayedCards()
    {
        SampleAlbum.WriteApplePairs(_album.InputDirectory, 6);
        using var session = new ShellSession();
        var library = session.Shell.Library;
        await ScanAsync(session, 6);
        library.SelectAllVisible(false);
        library.AllCards[1].IsSelected = true;
        library.AllCards[4].IsSelected = true;
        library.SetFilterMode("SelectedOnly");
        session.Pump();
        Assert.Equal(2, library.Layout.DisplayedCards.Count);

        _ = library.OpenQuickLookCommand.ExecuteAsync(library.AllCards[4]);
        var quickLook = Assert.IsType<QuickLookDialogViewModel>(session.Dialogs.Current);
        var expectedIndex = library.Layout.DisplayedCards.ToList().IndexOf(library.AllCards[4]) + 1;
        Assert.Equal($"{expectedIndex} / 2", quickLook.NavigationIndexText);
        quickLook.NextItemCommand.Execute(null);
        Assert.Contains(quickLook.Card, library.Layout.DisplayedCards);
        Assert.Equal($"{expectedIndex % 2 + 1} / 2", quickLook.NavigationIndexText);

        session.PressEscape();
        Assert.Null(session.Dialogs.Current);
    }

    private async Task ScanAsync(ShellSession session, int expected)
    {
        var library = session.Shell.Library;
        library.AlbumDirectory = _album.InputDirectory;
        await library.RefreshAlbumAsync();
        await session.WaitUntilAsync(() => library.AllCards.Count == expected);
        session.Pump();
    }

    private static List<PhotoCardItemViewModel> Attached(ShellSession session) =>
        SyntheticGallery.Attached(session.Descendants<ListBox>().Single(l => l.Name == "GalleryListBox"));

    /// <summary>视口顶部的行（跨过视口上沿的那一行）的首张卡片键与其顶部位置。</summary>
    private static (string Key, double Top) TopItem(ListBox list, ScrollViewer scroll)
    {
        foreach (var container in list.GetRealizedContainers().OrderBy(list.IndexFromContainer))
        {
            var top = container.TranslatePoint(default, scroll)!.Value.Y;
            if (top + container.Bounds.Height > 0 && list.ItemFromContainer(container) is PhotoGridRowViewModel row)
            {
                return (row.Cards[0].Key, top);
            }
        }

        throw new InvalidOperationException("视口中没有行。");
    }
}
