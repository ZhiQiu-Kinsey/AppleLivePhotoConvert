using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using LivePhotoConvert.Desktop.Controls;
using LivePhotoConvert.Desktop.Features.Library;
using LivePhotoConvert.Desktop.Features.Playback;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Models;
using LivePhotoConvert.Desktop.Tests.Features.Library.Thumbnails;
using LivePhotoConvert.Desktop.Tests.Features.Playback;
using LivePhotoConvert.Desktop.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace LivePhotoConvert.Desktop.Tests.Features.Library;

/// <summary>画廊悬浮播放：延迟开始、离开/滚动/重排/回收/页面隐藏即停，整窗只有一个播放器。</summary>
[Collection(ProcessStateCollection.Name)]
public sealed class GalleryHoverPlaybackTests : IDisposable
{
    private readonly TestSandbox _album = new();
    private readonly FakePlayers _players = new();

    public void Dispose() => _album.Dispose();

    [AvaloniaFact]
    public async Task Hover_StartsAfterDelay_ShowsFrame_AndLeavingRestoresThumbnail()
    {
        using var session = await OpenAsync(6);
        var hover = HoverPlayer();
        var (control, card) = VideoCard(session);

        var moved = System.Diagnostics.Stopwatch.StartNew();
        MoveToPreview(session, control);
        // 延迟由真实时间的计时器控制：只有移动与处理消息确实在延迟内完成时，才能断言尚未开始
        if (moved.Elapsed < GalleryHoverPlayback.DefaultStartDelay)
        {
            Assert.Empty(hover.Plays);
        }

        Assert.Same(card, session.Shell.Library.FocusedCard);
        // 首帧在下一个渲染节拍才赋给卡片
        await session.WaitUntilAsync(() => hover.Plays.Count == 1 && control.PlaybackFrame is not null);

        var play = hover.Plays[0];
        Assert.Equal(card.Video, play.Source);
        Assert.Equal(PlaybackBudget.Hover, play.Budget);
        Assert.Equal(session.Window.RenderScaling, play.Scaling);
        Assert.Equal(GalleryHoverPlayback.CoverTarget(control.PreviewSize, card.AspectRatio), play.Target);
        Assert.Same(hover.Surface, control.PlaybackFrame);
        Assert.NotNull(card.DisplayImage);

        // 在同一张卡片内移动不重新开始
        MoveToPreview(session, control, 0.3);
        Assert.Single(hover.Plays);

        // 移到信息栏：离开预览区即停，缩略图重新露出
        MoveTo(session, control, new Point(control.Bounds.Width / 2, control.Bounds.Height - 8));
        Assert.Equal(PlayerStatus.Idle, hover.State.Status);
        Assert.Null(control.PlaybackFrame);
        Assert.NotNull(card.DisplayImage);
        session.Log.AssertNoBindingErrors();
    }

    [AvaloniaFact]
    public async Task InfoBar_DoesNotStartPlayback()
    {
        using var session = await OpenAsync(3);
        var (control, _) = VideoCard(session);

        MoveTo(session, control, new Point(control.Bounds.Width / 2, control.Bounds.Height - 8));
        await Task.Delay(GalleryHoverPlayback.DefaultStartDelay * 2, TestContext.Current.CancellationToken);
        session.Pump();

        Assert.Empty(HoverPlayer().Plays);
    }

    [AvaloniaFact]
    public async Task LeavingBeforeDelay_CancelsPendingStart()
    {
        using var session = await OpenAsync(3);
        var (control, _) = VideoCard(session);

        MoveToPreview(session, control);
        MoveTo(session, control, new Point(control.Bounds.Width / 2, control.Bounds.Height - 8));
        await Task.Delay(GalleryHoverPlayback.DefaultStartDelay * 2, TestContext.Current.CancellationToken);
        session.Pump();

        Assert.Empty(HoverPlayer().Plays);
    }

    /// <summary>滚轮滚动时指针不动，卡片从指针下移走：播放必须停止，不能在别的位置残留视频帧。</summary>
    [AvaloniaFact]
    public async Task Scrolling_StopsHoverPlayback()
    {
        using var session = await OpenAsync(40);
        var hover = HoverPlayer();
        var (control, _) = VideoCard(session);
        MoveToPreview(session, control);
        await session.WaitUntilAsync(() => hover.State.Status == PlayerStatus.Playing);

        var scroll = Gallery(session).GetVisualDescendants().OfType<ScrollViewer>().First();
        scroll.Offset = new Vector(0, 180);
        session.Pump();

        Assert.Equal(PlayerStatus.Idle, hover.State.Status);
        Assert.Null(control.PlaybackFrame);
        Assert.DoesNotContain(session.Descendants<PhotoCardControl>(), c => c.PlaybackFrame is not null);
    }

    [AvaloniaFact]
    public async Task HiddenPage_StopsHoverPlayback_AndOnlyOnePlayerExists()
    {
        using var session = await OpenAsync(6);
        var hover = HoverPlayer();
        var cards = session.Descendants<PhotoCardControl>().Where(c => c.DataContext is PhotoCardItemViewModel { Video: not null }).Take(2).ToList();

        MoveToPreview(session, cards[0]);
        await session.WaitUntilAsync(() => hover.Plays.Count == 1);
        MoveToPreview(session, cards[1]);
        Assert.Null(cards[0].PlaybackFrame);
        await session.WaitUntilAsync(() => hover.Plays.Count == 2);
        Assert.Single(_players.Created);
        Assert.Single(session.Descendants<PhotoCardControl>(), c => c.PlaybackFrame is not null);

        session.Navigate(AppPage.Tasks);

        Assert.Equal(PlayerStatus.Idle, hover.State.Status);
        Assert.Null(cards[1].PlaybackFrame);
    }

    /// <summary>虚拟化回收：容器改绑到别的卡片时，旧卡片的视频不能留在新卡片上。</summary>
    [AvaloniaFact]
    public async Task RecycledContainer_StopsHoverPlayback()
    {
        using var session = await OpenAsync(6);
        var hover = HoverPlayer();
        var (control, card) = VideoCard(session);
        MoveToPreview(session, control);
        await session.WaitUntilAsync(() => hover.State.Status == PlayerStatus.Playing);

        control.DataContext = session.Shell.Library.AllCards.First(c => !ReferenceEquals(c, card));
        session.Pump();

        Assert.Equal(PlayerStatus.Idle, hover.State.Status);
        Assert.Null(control.PlaybackFrame);
    }

    [AvaloniaFact]
    public async Task Relayout_StopsHoverPlayback()
    {
        using var session = await OpenAsync(6);
        var hover = HoverPlayer();
        var (control, _) = VideoCard(session);
        MoveToPreview(session, control);
        await session.WaitUntilAsync(() => hover.State.Status == PlayerStatus.Playing);

        session.Shell.Library.Layout.SetScaleModeCommand.Execute("Large");
        session.Pump();

        Assert.Equal(PlayerStatus.Idle, hover.State.Status);
    }

    /// <summary>QuickLook 独占：打开即停悬浮；切换卡片先停旧播放；关闭后释放自己的播放器，悬浮恢复可用。</summary>
    [AvaloniaFact]
    public async Task QuickLook_StopsHover_PlaysExclusively_AndReleasesOnClose()
    {
        using var session = await OpenAsync(4);
        var library = session.Shell.Library;
        var service = library.Playback;
        var hover = HoverPlayer();
        var (control, card) = VideoCard(session);
        MoveToPreview(session, control);
        await session.WaitUntilAsync(() => hover.State.Status == PlayerStatus.Playing);

        library.OpenQuickLookCommand.Execute(card);
        session.Pump();
        Assert.Equal(PlayerStatus.Idle, hover.State.Status);
        Assert.Null(control.PlaybackFrame);

        var quickLook = Assert.IsType<Desktop.Features.Dialogs.QuickLookDialogViewModel>(session.Dialogs.Current);
        await session.WaitUntilAsync(() => _players.Created.Count == 2 && _players.Created[1].Plays.Count == 1);
        var player = _players.Created[1];
        Assert.Same(player, quickLook.Player);
        Assert.Equal(card.Video, player.Plays[0].Source);
        Assert.Equal(PlaybackBudget.QuickLook, player.Plays[0].Budget);
        Assert.Same(player.Surface, quickLook.CurrentDisplayImage);
        Assert.False(service.CanPlay(hover));
        Assert.Equal(session.Localizer["QuickLookPlaying"], quickLook.PlaybackStatusText);

        // 暂停与继续交给播放器
        quickLook.TogglePlayCommand.Execute(null);
        Assert.True(player.IsPaused);
        Assert.Equal(session.Localizer["QuickLookPaused"], quickLook.PlaybackStatusText);
        quickLook.TogglePlayCommand.Execute(null);
        Assert.False(player.IsPaused);

        var stopsBefore = player.StopCalls;
        quickLook.NextItemCommand.Execute(null);
        Assert.True(player.StopCalls > stopsBefore);
        Assert.Equal(2, player.Plays.Count);
        Assert.Equal(quickLook.Card.Video, player.Plays[1].Source);

        session.PressEscape();
        Assert.True(quickLook.IsClosed);
        Assert.True(player.IsDisposed);
        Assert.Equal(1, player.StopAsyncCalls);
        Assert.DoesNotContain(player, service.Players);
        Assert.True(service.CanPlay(hover));
        Assert.Null(quickLook.CurrentDisplayImage);
    }

    [Theory]
    [InlineData(300, 200, 1.5, 300, 200)]
    [InlineData(200, 200, 1.5, 300, 200)]
    [InlineData(200, 200, 0.75, 200, 267)]
    [InlineData(120.4, 90.2, 4.0 / 3, 121, 91)]
    public void CoverTarget_CoversTheCroppedPreview(double width, double height, double aspect, int expectedWidth, int expectedHeight)
    {
        Assert.Equal(new PixelSize(expectedWidth, expectedHeight), GalleryHoverPlayback.CoverTarget(new Size(width, height), aspect));
    }

    [Fact]
    public void CoverTarget_WithoutArea_IsNull()
    {
        Assert.Null(GalleryHoverPlayback.CoverTarget(new Size(0, 200), 1.5));
        Assert.Null(GalleryHoverPlayback.CoverTarget(new Size(200, 200), double.NaN));
    }

    private async Task<ShellSession> OpenAsync(int pairs)
    {
        SampleAlbum.WriteApplePairs(_album.InputDirectory, pairs);
        var session = new ShellSession(configure: services => services.AddSingleton(_ => _players.CreateService()));
        var library = session.Shell.Library;
        library.AlbumDirectory = _album.InputDirectory;
        await library.RefreshAlbumAsync();
        await session.WaitUntilAsync(() => library.AllCards.Count == pairs && session.Descendants<PhotoCardControl>().Any(c => c.PreviewSize.Height > 0));
        // 缩略图就位且排版稳定后再悬停：起播延迟很短，否则视频帧可能先于缩略图到达，随后的重排也会停止播放
        await SyntheticGallery.PumpUntilLoadedAsync(session, Gallery(session));
        return session;
    }

    private FakePlayer HoverPlayer() => _players.Created[0];

    private static ListBox Gallery(ShellSession session) => session.Descendants<ListBox>().Single(l => l.Name == "GalleryListBox");

    private static (PhotoCardControl Control, PhotoCardItemViewModel Card) VideoCard(ShellSession session)
    {
        var control = session.Descendants<PhotoCardControl>().First(c => c.DataContext is PhotoCardItemViewModel { Video: not null } && c.IsEffectivelyVisible);
        return (control, (PhotoCardItemViewModel)control.DataContext!);
    }

    private static void MoveToPreview(ShellSession session, PhotoCardControl control, double fraction = 0.5)
    {
        var preview = control.GetVisualDescendants().OfType<Panel>().Single(p => p.Name == "PreviewArea");
        var point = preview.TranslatePoint(new Point(preview.Bounds.Width * fraction, preview.Bounds.Height * fraction), session.Window)!.Value;
        session.Window.MouseMove(point);
        session.Pump();
    }

    private static void MoveTo(ShellSession session, Visual visual, Point local)
    {
        session.Window.MouseMove(visual.TranslatePoint(local, session.Window)!.Value);
        session.Pump();
    }
}
