using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using LivePhotoConvert.Core.Media.Thumbnails;
using LivePhotoConvert.Desktop.Features.Library.Thumbnails;
using LivePhotoConvert.Desktop.Features.Settings;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Models;
using LivePhotoConvert.Desktop.Tests.Harness;

namespace LivePhotoConvert.Desktop.Tests.Features.Settings;

/// <summary>设置页“画廊”分组：内存预算即时生效、磁盘缓存上限与占用、清理缓存不影响正在显示的卡片。</summary>
[Collection(ProcessStateCollection.Name)]
public sealed class GallerySettingsTests : IDisposable
{
    private readonly TestSandbox _album = new();

    public void Dispose() => _album.Dispose();

    [AvaloniaFact]
    public void BudgetChange_IsSavedAndAppliedToPipelineImmediately()
    {
        using var session = new ShellSession();
        var settings = session.Shell.Settings;
        var pipeline = Assert.IsType<ThumbnailPipeline>(session.Shell.Library.Thumbnails);
        Assert.Equal(GalleryPreferences.DefaultThumbnailBudgetMb, settings.ThumbnailBudgetMb);
        Assert.Equal(GalleryPreferences.DefaultThumbnailBudgetMb * 1024L * 1024, pipeline.BudgetBytes);

        settings.ThumbnailBudgetMb = 512;

        Assert.Equal(512L * 1024 * 1024, pipeline.BudgetBytes);
        Assert.Equal(512, session.Host.Settings.Current.Gallery.ThumbnailBudgetMb);
    }

    [AvaloniaFact]
    public async Task DiskCacheLimitAndUsage_AreMeasuredInBackground_WhenSettingsPageOpens()
    {
        using var session = new ShellSession(settings: s => s.Gallery.ThumbnailDiskCacheMb = 2048);
        var settings = session.Shell.Settings;
        var cache = session.Host.Get<ThumbnailDiskCache>();
        var payload = new byte[300 * 1024];
        for (var i = 0; i < 4; i++)
        {
            Assert.True(cache.TryPut(ThumbnailKey.Create($"/album/{i}.jpg", 1, DateTime.UnixEpoch, 256), payload));
        }

        session.Navigate(AppPage.Settings);
        await settings.CacheUsageTask;
        session.Pump();
        Assert.Equal(session.Localizer.Format("ThumbnailCacheUsageFormat", "1.2 MB"), settings.CacheUsageText);
        Assert.Contains(settings.CacheUsageText, UiTexts.Collect(session).Select(t => t.Text));

        settings.ThumbnailDiskCacheMb = 128;
        Assert.Equal(128L * 1024 * 1024, cache.CapacityBytes);
        Assert.Equal(128, session.Host.Settings.Current.Gallery.ThumbnailDiskCacheMb);
        await settings.CacheUsageTask;
        Assert.False(settings.IsCacheBusy);
        session.Log.AssertNoBindingErrors();
    }

    /// <summary>清理只删磁盘文件：正在显示的卡片保留内存位图；之后需要新档位时重新生成并写回缓存。</summary>
    [AvaloniaFact]
    public async Task ClearCache_KeepsShownThumbnails_AndCacheIsRebuiltOnDemand()
    {
        SampleAlbum.WriteApplePairs(_album.InputDirectory, 6);
        using var session = new ShellSession();
        var library = session.Shell.Library;
        var cache = session.Host.Get<ThumbnailDiskCache>();
        library.AlbumDirectory = _album.InputDirectory;
        await library.RefreshAlbumAsync();
        await session.WaitUntilAsync(() => library.AllCards.Count == 6);
        var list = session.Descendants<ListBox>().Single(l => l.Name == "GalleryListBox");
        await session.WaitUntilAsync(() => Attached(list) is { Count: > 0 } cards && cards.All(c => c.DisplayImage is not null));
        var shown = Attached(list).ToDictionary(c => c, c => c.DisplayImage!);
        Assert.NotEmpty(CacheFiles(cache));

        await session.Shell.Settings.ClearThumbnailCacheCommand.ExecuteAsync(null);
        session.Pump();

        Assert.Empty(CacheFiles(cache));
        Assert.Equal(session.Localizer.Format("ThumbnailCacheUsageFormat", "0 B"), session.Shell.Settings.CacheUsageText);
        foreach (var (card, bitmap) in shown)
        {
            Assert.Same(bitmap, card.DisplayImage);
            Assert.True(bitmap.PixelSize.Height > 0, "位图不应被释放");
        }

        library.Layout.SetScaleModeCommand.Execute("Large");
        // 重排可能先回收全部行容器：没有已实例化卡片时条件不能算满足
        await session.WaitUntilAsync(() => Attached(list) is { Count: > 0 } cards && cards.All(c => c.Thumbnail is { } t && !shown.ContainsValue(t)));
        Assert.NotEmpty(CacheFiles(cache));
        session.Log.AssertNoBindingErrors();
    }

    [AvaloniaTheory]
    [InlineData("zh")]
    [InlineData("en")]
    public void GalleryGroup_IsShownWithLocalizedTexts(string language)
    {
        using var session = new ShellSession(language: language);
        session.Navigate(AppPage.Settings);

        var texts = UiTexts.Collect(session).Select(t => t.Text).ToHashSet();
        foreach (var key in (string[])["SettingsGalleryTitle", "ThumbnailBudgetTitle", "ThumbnailDiskCacheTitle", "ThumbnailCacheUsageTitle", "ClearThumbnailCacheBtn"])
        {
            Assert.Contains(session.Localizer[key], texts);
        }

        var inputs = session.Descendants<SettingsView>().Single().GetVisualDescendants().OfType<NumericUpDown>()
            .Where(n => n.Name is "ThumbnailBudgetInput" or "ThumbnailDiskCacheInput").ToList();
        Assert.Equal([64m, 1024m, 128m, 16384m], inputs.SelectMany(n => (decimal[])[n.Minimum, n.Maximum]));
        if (language == "en")
        {
            UiTexts.AssertNoChinese(session, "设置页（英文）");
        }

        session.Log.AssertNoBindingErrors();
    }

    private static List<PhotoCardItemViewModel> Attached(ListBox list) =>
        [.. list.GetRealizedContainers().Select(list.ItemFromContainer).OfType<PhotoGridRowViewModel>().SelectMany(r => r.Cards)];

    private static string[] CacheFiles(ThumbnailDiskCache cache) =>
        Directory.Exists(cache.Root) ? Directory.GetFiles(cache.Root, "*.jpg", SearchOption.AllDirectories) : [];
}
