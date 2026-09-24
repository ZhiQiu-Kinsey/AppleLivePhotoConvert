using Avalonia.Headless.XUnit;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Tests.Features.Library.Thumbnails;
using LivePhotoConvert.Desktop.Tests.Harness;

namespace LivePhotoConvert.Desktop.Tests.Features.Library.Gallery;

/// <summary>
/// 卡片外观截图：宽窄不同的卡片、待裁决与选中态，浅色与深色各一份，另有英文长文件名一份，供人工比对。
/// </summary>
[Collection(ProcessStateCollection.Name)]
public sealed class PhotoCardScreenshotTests
{
    [AvaloniaTheory]
    [InlineData(ThemeService.Light, "zh", false)]
    [InlineData(ThemeService.Dark, "en", false)]
    [InlineData(ThemeService.Light, "en", true)]
    public async Task Cards_InAllWidthsAndStates(string theme, string language, bool longNames)
    {
        const int count = 60;
        using var store = new SyntheticStore();
        using var session = SyntheticGallery.Open(store, budgetMb: 192, count, theme: theme, language: language,
            name: longNames ? i => $"IMG_20250101_{i:D6}_HDR" : null);
        var suffix = $"{theme.ToLowerInvariant()}-{language}{(longNames ? "-long" : "")}";
        await SyntheticGallery.ScanAsync(session, count);
        var library = session.Shell.Library;
        var list = session.Descendants<Avalonia.Controls.ListBox>().Single(l => l.Name == "GalleryListBox");
        library.Layout.DisplayedCards[1].IsSelected = true;
        library.Layout.DisplayedCards[4].IsSelected = true;
        await SyntheticGallery.PumpUntilLoadedAsync(session, list);
        Screenshots.Save(session, $"gallery-cards-medium-{suffix}");

        library.Layout.SetScaleModeCommand.Execute("Small");
        await SyntheticGallery.PumpUntilLoadedAsync(session, list);
        Screenshots.Save(session, $"gallery-cards-small-{suffix}");
        session.Log.AssertNoBindingErrors();
    }
}
