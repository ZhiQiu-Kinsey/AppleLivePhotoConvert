using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using LivePhotoConvert.Desktop.Controls;
using LivePhotoConvert.Desktop.Features.Library.Thumbnails;
using LivePhotoConvert.Desktop.Tests.Harness;

namespace LivePhotoConvert.Desktop.Tests.Features.Library.Gallery;

/// <summary>卡片信息栏为固定高度（同一行底部对齐），文字字号调整后内容仍须放得下。</summary>
[Collection(ProcessStateCollection.Name)]
public sealed class PhotoCardLayoutTests : IDisposable
{
    private readonly TestSandbox _album = new();

    public void Dispose() => _album.Dispose();

    [AvaloniaTheory]
    [InlineData("zh")]
    [InlineData("en")]
    public async Task InfoBarContent_FitsFixedHeight(string language)
    {
        SampleAlbum.WriteApplePairs(_album.InputDirectory, 2);
        using var session = new ShellSession(language);
        var library = session.Shell.Library;
        library.AlbumDirectory = _album.InputDirectory;
        await library.RefreshAlbumAsync();
        await session.WaitUntilAsync(() => library.AllCards.Count == 2);
        session.Pump();

        var cards = session.Descendants<PhotoCardControl>().ToList();
        Assert.NotEmpty(cards);
        foreach (var card in cards)
        {
            var infoBar = card.GetVisualDescendants().OfType<Border>().Single(b => b.Height == GalleryMetrics.InfoBarHeight);
            var content = (Control)infoBar.Child!;
            var available = infoBar.Bounds.Width - infoBar.Padding.Left - infoBar.Padding.Right;
            content.Measure(new Size(available, double.PositiveInfinity));
            var needed = content.DesiredSize.Height + infoBar.Padding.Top + infoBar.Padding.Bottom;
            Assert.True(needed <= GalleryMetrics.InfoBarHeight + 0.5, $"{language}: 信息栏需要 {needed:F1}px，固定高度 {GalleryMetrics.InfoBarHeight}px");
        }

        session.Log.AssertNoBindingErrors();
    }
}
