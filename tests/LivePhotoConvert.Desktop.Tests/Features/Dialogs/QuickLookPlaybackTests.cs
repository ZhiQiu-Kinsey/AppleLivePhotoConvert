using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using LivePhotoConvert.Desktop.Features.Dialogs;
using LivePhotoConvert.Desktop.Features.Playback;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Models;
using LivePhotoConvert.Desktop.Tests.Features.Playback;
using LivePhotoConvert.Desktop.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace LivePhotoConvert.Desktop.Tests.Features.Dialogs;

/// <summary>QuickLook 的播放失败提示：两种语言的文案、缺工具时前往依赖页，以及 FFmpeg 路径取自设置。</summary>
[Collection(ProcessStateCollection.Name)]
public sealed class QuickLookPlaybackTests : IDisposable
{
    private static readonly PlaybackError[] Errors =
    [
        PlaybackError.FfmpegNotFound,
        PlaybackError.SourceNotFound,
        PlaybackError.NoVideoStream,
        PlaybackError.HdrToneMapUnavailable,
        PlaybackError.DecodeFailed
    ];

    private readonly TestSandbox _album = new();
    private readonly FakePlayers _players = new();

    public void Dispose() => _album.Dispose();

    [Fact]
    public void EveryPlaybackError_HasItsOwnText()
    {
        var keys = Errors.Select(PlaybackTexts.ErrorKey).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.Equal(
            [PlaybackError.FfmpegNotFound, PlaybackError.HdrToneMapUnavailable],
            Errors.Where(PlaybackTexts.IsToolProblem));
    }

    [AvaloniaTheory]
    [InlineData("zh")]
    [InlineData("en")]
    public async Task PlaybackErrors_ShowLocalizedText_AndToolProblemsOfferTheToolsPage(string language)
    {
        using var session = await OpenQuickLookAsync(language);
        var quickLook = (QuickLookDialogViewModel)session.Dialogs.Current!;
        var player = _players.Created[^1];
        var banner = session.Descendants<Border>().Single(b => b.Name == "PlaybackErrorBanner");
        var toolsButton = session.Descendants<Button>().Single(b => b.Name == "OpenToolsButton");

        foreach (var error in Errors)
        {
            player.NextError = error;
            quickLook.NextItemCommand.Execute(null);
            session.Pump();

            var text = quickLook.PlaybackStatusText;
            Assert.Equal(session.Localizer[PlaybackTexts.ErrorKey(error)], text);
            Assert.NotEqual(PlaybackTexts.ErrorKey(error), text);
            Assert.Equal(language == "zh", text.Any(c => c is >= '一' and <= '鿿'));
            Assert.True(banner.IsEffectivelyVisible, $"{error} 没有显示失败提示");
            Assert.Equal(PlaybackTexts.IsToolProblem(error), toolsButton.IsEffectivelyVisible);
            if (error == PlaybackError.HdrToneMapUnavailable)
            {
                Screenshots.Save(session, $"quicklook-hdr-tonemap-unavailable-{language}");
            }
        }

        // 切到能播放的卡片后提示消失
        quickLook.NextItemCommand.Execute(null);
        session.Pump();
        Assert.False(banner.IsEffectivelyVisible);

        player.NextError = PlaybackError.HdrToneMapUnavailable;
        quickLook.NextItemCommand.Execute(null);
        // 按钮要等失败提示布局可见后才能命中；点击后弹窗经异步延续关闭，慢机器上不能立即断言
        await session.WaitUntilAsync(() => toolsButton.IsEffectivelyVisible && toolsButton.Bounds.Width > 0);
        session.Click(toolsButton);
        await session.WaitUntilAsync(() => quickLook.IsClosed);
        Assert.Null(session.Dialogs.Current);
        Assert.Equal(AppPage.Tools, session.Shell.CurrentPage);
        session.Log.AssertNoBindingErrors();
    }

    /// <summary>真实播放器：设置里指定的 FFmpeg 不可用时如实报告，而不是改用别处找到的 FFmpeg 或无声失败。</summary>
    [AvaloniaFact]
    public async Task RealPlayer_UsesFfmpegPathFromSettings()
    {
        SampleAlbum.WriteApplePairs(_album.InputDirectory, 2);
        using var session = new ShellSession(settings: s => s.FfmpegPath = Path.Combine(_album.RootDirectory, "missing", "ffmpeg"));
        var library = session.Shell.Library;
        library.AlbumDirectory = _album.InputDirectory;
        await library.RefreshAlbumAsync();
        await session.WaitUntilAsync(() => library.AllCards.Count == 2);

        library.OpenQuickLookCommand.Execute(library.AllCards[0]);
        var quickLook = Assert.IsType<QuickLookDialogViewModel>(session.Dialogs.Current);
        await session.WaitUntilAsync(() => quickLook.HasPlaybackError);

        Assert.Equal(session.Localizer["PlaybackErrorFfmpegMissing"], quickLook.PlaybackStatusText);
        Assert.True(quickLook.CanOpenTools);
        Assert.Equal(PlaybackError.FfmpegNotFound, quickLook.Player!.State.Error);
    }

    /// <summary>弹窗随内容改变尺寸：照片与视频比例不同时画布来回变化，解码只按变大后的尺寸重启一次，不振荡。</summary>
    [AvaloniaFact]
    public void CanvasSwingingBetweenPhotoAndVideoShapes_RestartsAtMostOnce()
    {
        using var host = new DesktopTestHost();
        PhotoCardItemViewModel[] cards = [Library.Cards.ApplePair("IMG_1"), Library.Cards.ApplePair("IMG_2")];
        var quickLook = new QuickLookDialogViewModel(host.Localizer, host.Get<Desktop.Features.Library.Thumbnails.IThumbnailPipeline>(), i => (uint)i < 2u ? cards[i] : null, 2, 0)
        {
            Playback = _players.CreateService()
        };
        quickLook.AttachPlayer(new ManualFrameScheduler());
        var player = Assert.IsType<FakePlayer>(quickLook.Player);
        Assert.Empty(player.Plays);

        for (var i = 0; i < 4; i++)
        {
            quickLook.SetViewport(1098, 480, 1);
            quickLook.SetViewport(743, 557, 1);
        }

        Assert.Equal(2, player.Plays.Count);
        Assert.Equal(new Avalonia.PixelSize(1098, 480), player.Plays[0].Target);
        Assert.Equal(new Avalonia.PixelSize(1098, 557), player.Plays[1].Target);

        // 换卡片从当前画布重新开始
        quickLook.SetViewport(900, 500, 1);
        quickLook.NextItemCommand.Execute(null);
        Assert.Equal(new Avalonia.PixelSize(900, 500), player.Plays[^1].Target);
        quickLook.CancelCommand.Execute(null);
        Assert.True(player.IsDisposed);
    }

    private async Task<ShellSession> OpenQuickLookAsync(string language)
    {
        SampleAlbum.WriteApplePairs(_album.InputDirectory, 8);
        var session = new ShellSession(language, configure: services => services.AddSingleton(_ => _players.CreateService()));
        var library = session.Shell.Library;
        library.AlbumDirectory = _album.InputDirectory;
        await library.RefreshAlbumAsync();
        await session.WaitUntilAsync(() => library.AllCards.Count == 8);
        library.OpenQuickLookCommand.Execute(library.AllCards[0]);
        var quickLook = Assert.IsType<QuickLookDialogViewModel>(session.Dialogs.Current);
        await session.WaitUntilAsync(() => quickLook.Player is FakePlayer { Plays.Count: 1 });
        await session.WaitUntilAsync(() => quickLook.CurrentDisplayImage is not null);
        return session;
    }
}
