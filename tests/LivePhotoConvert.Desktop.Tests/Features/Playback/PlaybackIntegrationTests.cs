using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using LivePhotoConvert.Core.Tests.Support;
using LivePhotoConvert.Desktop.Controls;
using LivePhotoConvert.Desktop.Features.Dialogs;
using LivePhotoConvert.Desktop.Features.Library;
using LivePhotoConvert.Desktop.Features.Playback;
using LivePhotoConvert.Desktop.Models;
using LivePhotoConvert.Desktop.Tests.Harness;

namespace LivePhotoConvert.Desktop.Tests.Features.Playback;

/// <summary>真实 FFmpeg 与真实外壳：悬浮播放实况对的 MOV、QuickLook 经 subfile 播放动态照片内嵌视频与 HLG 色调映射。缺工具时跳过。</summary>
[Collection(ProcessStateCollection.Name)]
public sealed class PlaybackIntegrationTests : IDisposable
{
    private readonly TestSandbox _album = new();

    public void Dispose() => _album.Dispose();

    [AvaloniaFact]
    public async Task Hover_RealMov_PlaysAtCoverSize_AndStopsOnLeave()
    {
        var ffmpeg = PlaybackSamples.RequireFfmpeg();
        SampleAlbum.WriteJpeg(Path.Combine(_album.InputDirectory, "IMG_0001.jpg"), 3, 480, 360);
        await PlaybackSamples.SdrAsync(ffmpeg, Path.Combine(_album.InputDirectory, "IMG_0001.mov"), seconds: 3, size: "480x360");
        using var session = new ShellSession();
        await ScanAsync(session, 1);
        var view = session.Descendants<LibraryView>().Single();
        var control = session.Descendants<PhotoCardControl>().Single();
        var card = (PhotoCardItemViewModel)control.DataContext!;

        MoveToPreview(session, control);
        var player = Assert.IsType<LivePhotoPlayer>(view.HoverPlayback!.Player);
        await session.WaitUntilAsync(() => control.PlaybackFrame is not null, timeoutSeconds: 20);

        Assert.Equal(PlayerStatus.Playing, player.State.Status);
        var expected = GalleryHoverPlayback.CoverTarget(control.PreviewSize, card.AspectRatio)!.Value;
        var frame = Assert.IsAssignableFrom<WriteableBitmap>(control.PlaybackFrame);
        Assert.InRange(frame.PixelSize.Width, expected.Width - 2, expected.Width);
        Assert.InRange(frame.PixelSize.Height, expected.Height - 2, expected.Height);
        Screenshots.Save(session, "hover-playback-real-mov");

        // 播放中换帧：界面跟随新的位图实例
        var shown = control.PlaybackFrame;
        await session.WaitUntilAsync(() => !ReferenceEquals(control.PlaybackFrame, shown), timeoutSeconds: 10);

        session.Window.MouseMove(new Point(2, 2));
        session.Pump();
        Assert.Null(control.PlaybackFrame);
        Assert.Equal(PlayerStatus.Idle, player.State.Status);
        await player.StopAsync();
        Assert.Null(player.DecoderProcessId);
        session.Log.AssertNoBindingErrors();
    }

    /// <summary>安卓动态照片：视频按照片内的偏移经 subfile 直接读取，不产生临时文件。</summary>
    [AvaloniaFact]
    public async Task QuickLook_MotionPhoto_PlaysEmbeddedVideoInPlace()
    {
        var ffmpeg = PlaybackSamples.RequireFfmpeg();
        var mp4 = Path.Combine(_album.RootDirectory, "clip.mp4");
        await PlaybackSamples.SdrAsync(ffmpeg, mp4, seconds: 2, size: "480x360");
        var cover = SampleAlbum.WriteJpeg(Path.Combine(_album.RootDirectory, "cover.jpg"), 5, 480, 360);
        File.WriteAllBytes(Path.Combine(_album.InputDirectory, "MVIMG_0001.jpg"), SyntheticMedia.MotionPhoto(File.ReadAllBytes(cover), File.ReadAllBytes(mp4)));
        var before = Directory.Exists(LegacyMotionCache.DefaultDirectory) ? Directory.GetFiles(LegacyMotionCache.DefaultDirectory).Length : 0;

        using var session = new ShellSession(settings: s => s.Action = ConversionAction.ToApple);
        await ScanAsync(session, 1);
        var card = session.Shell.Library.AllCards.Single();
        Assert.True(card.Video is { IsEmbedded: true });

        session.Shell.Library.OpenQuickLookCommand.Execute(card);
        var quickLook = Assert.IsType<QuickLookDialogViewModel>(session.Dialogs.Current);
        await session.WaitUntilAsync(() => quickLook.Player?.State.Status == PlayerStatus.Playing, timeoutSeconds: 20);

        Assert.Same(quickLook.Player!.Surface, quickLook.CurrentDisplayImage);
        Assert.Equal(session.Localizer["QuickLookPlaying"], quickLook.PlaybackStatusText);
        Assert.False(quickLook.HasPlaybackError);
        Assert.Equal(before, Directory.Exists(LegacyMotionCache.DefaultDirectory) ? Directory.GetFiles(LegacyMotionCache.DefaultDirectory).Length : 0);
        Screenshots.Save(session, "quicklook-motion-photo-subfile");

        var player = quickLook.Player;
        session.PressEscape();
        await session.WaitUntilAsync(() => !session.Shell.Library.Playback.Players.Contains(player));
        Assert.Null(((LivePhotoPlayer)player).DecoderProcessId);
    }

    /// <summary>HLG 片段经色调映射显示：画面不发灰（饱和跨度明显），与 SDR 源的观感一致。</summary>
    [AvaloniaFact]
    public async Task QuickLook_HlgVideo_IsToneMapped()
    {
        var ffmpeg = await PlaybackSamples.RequireHdrToolchainAsync();
        SampleAlbum.WriteJpeg(Path.Combine(_album.InputDirectory, "IMG_0002.jpg"), 2, 320, 240);
        await PlaybackSamples.HlgAsync(ffmpeg, Path.Combine(_album.InputDirectory, "IMG_0002.mov"));
        using var session = new ShellSession();
        await ScanAsync(session, 1);

        session.Shell.Library.OpenQuickLookCommand.Execute(session.Shell.Library.AllCards.Single());
        var quickLook = Assert.IsType<QuickLookDialogViewModel>(session.Dialogs.Current);
        await session.WaitUntilAsync(() => quickLook.Player?.State.Status == PlayerStatus.Playing, timeoutSeconds: 30);

        var frame = Assert.IsAssignableFrom<WriteableBitmap>(quickLook.Player!.Surface);
        byte[] pixels;
        using (var buffer = frame.Lock())
        {
            pixels = new byte[buffer.RowBytes * buffer.Size.Height];
            System.Runtime.InteropServices.Marshal.Copy(buffer.Address, pixels, 0, pixels.Length);
        }

        // 对照：同一测试图的 SDR 版本按同样尺寸解码
        var sdr = Path.Combine(_album.RootDirectory, "reference.mov");
        await PlaybackSamples.SdrAsync(ffmpeg, sdr, seconds: 1);
        var reference = await PlaybackSamples.DecodeAllAsync(ffmpeg, sdr, await PlaybackSamples.ProbeAsync(ffmpeg, sdr), frame.PixelSize);
        var mapped = PlaybackSamples.Stats(pixels);
        var original = PlaybackSamples.Stats(reference.FirstFrame);
        TestContext.Current.TestOutputHelper?.WriteLine($"HLG 映射后 {mapped}，SDR 对照 {original}");
        Assert.True(mapped.Spread > original.Spread * 0.6, $"色调映射后饱和度不足：{mapped} vs {original}");
        Assert.InRange(mapped.Luma, original.Luma * 0.6, original.Luma * 1.2);
        Screenshots.Save(session, "quicklook-hlg-tonemapped");
    }

    private async Task ScanAsync(ShellSession session, int expected)
    {
        var library = session.Shell.Library;
        library.AlbumDirectory = _album.InputDirectory;
        await library.RefreshAlbumAsync();
        await session.WaitUntilAsync(() => library.AllCards.Count == expected && session.Descendants<PhotoCardControl>().Any(c => c.PreviewSize.Height > 0), timeoutSeconds: 20);
        session.Pump();
    }

    private static void MoveToPreview(ShellSession session, PhotoCardControl control)
    {
        var preview = control.GetVisualDescendants().OfType<Panel>().Single(p => p.Name == "PreviewArea");
        session.Window.MouseMove(preview.TranslatePoint(new Point(preview.Bounds.Width / 2, preview.Bounds.Height / 2), session.Window)!.Value);
        session.Pump();
    }
}
