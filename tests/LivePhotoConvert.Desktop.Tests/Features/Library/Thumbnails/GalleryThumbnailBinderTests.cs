using LivePhotoConvert.Desktop.Tests.Harness;
using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using LivePhotoConvert.Desktop.Features.Library.Thumbnails;
using LivePhotoConvert.Desktop.Models;
using LivePhotoConvert.Desktop.Tests.Features.Library;

namespace LivePhotoConvert.Desktop.Tests.Features.Library.Thumbnails;

[Collection(ProcessStateCollection.Name)]
public class GalleryThumbnailBinderTests
{
    [AvaloniaFact]
    public void RealizedRows_AcquireTheirCards_AndReleaseWhenItemsChange()
    {
        var pipeline = new CountingPipeline();
        var a = Card("a");
        var b = Card("b");
        var c = Card("c");
        var row = Row("r0", a, b);
        var items = new ObservableCollection<object> { new TimelineHeaderItemViewModel("h", default) { Title = "t" }, row };
        var list = new ListBox { ItemsSource = items, Width = 300, Height = 300 };
        var window = new Window { Content = list, Width = 300, Height = 300 };
        window.Show();

        using var binder = new GalleryThumbnailBinder(list, pipeline);
        Assert.Equal(2, binder.AttachedCardCount);
        Assert.Equal(1, pipeline.Count(a));

        // 行内原地替换：仍在行内的卡片不经历归零
        row.Cards.Add(c);
        row.Cards.Remove(a);
        Assert.Equal(0, pipeline.Count(a));
        Assert.Equal(1, pipeline.Count(b));
        Assert.Equal(1, pipeline.Count(c));
        Assert.Equal(0, pipeline.ZeroCrossings(b));

        items.Remove(row);
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Assert.Equal(0, pipeline.Count(b));
        Assert.Equal(0, pipeline.Count(c));
        Assert.Equal(0, binder.AttachedCardCount);
        window.Close();
    }

    [AvaloniaFact]
    public void Dispose_ReleasesEverything()
    {
        var pipeline = new CountingPipeline();
        var a = Card("a");
        var list = new ListBox { ItemsSource = new[] { Row("r", a) } };
        var window = new Window { Content = list, Width = 300, Height = 300 };
        window.Show();

        var binder = new GalleryThumbnailBinder(list, pipeline);
        Assert.Equal(1, pipeline.Count(a));
        binder.Dispose();
        Assert.Equal(0, pipeline.Count(a));
        window.Close();
    }

    private static PhotoCardItemViewModel Card(string name) => Cards.Still(name);

    private static PhotoGridRowViewModel Row(string key, params PhotoCardItemViewModel[] cards)
    {
        var row = new PhotoGridRowViewModel(key);
        foreach (var card in cards)
        {
            row.Cards.Add(card);
        }

        return row;
    }

    private sealed class CountingPipeline : IThumbnailPipeline
    {
        private readonly Dictionary<PhotoCardItemViewModel, int> _counts = [];
        private readonly Dictionary<PhotoCardItemViewModel, int> _zeroCrossings = [];

        public long ResidentBytes => 0;

        public int Count(PhotoCardItemViewModel card) => _counts.GetValueOrDefault(card);

        public int ZeroCrossings(PhotoCardItemViewModel card) => _zeroCrossings.GetValueOrDefault(card);

        public void Acquire(PhotoCardItemViewModel card) => _counts[card] = Count(card) + 1;

        public void Release(PhotoCardItemViewModel card)
        {
            var count = Count(card) - 1;
            Assert.True(count >= 0, "Release 多于 Acquire");
            _counts[card] = count;
            if (count == 0)
            {
                _zeroCrossings[card] = ZeroCrossings(card) + 1;
            }
        }

        public int NextGeneration() => 0;

        public void Configure(double maxRowHeightDip, double renderScaling, bool squareCrop)
        {
        }

        public void Reset()
        {
        }

        public Task<Avalonia.Media.Imaging.Bitmap?> LoadPreviewAsync(PhotoCardItemViewModel card, int heightPx, CancellationToken cancellationToken) =>
            Task.FromResult<Avalonia.Media.Imaging.Bitmap?>(null);
    }
}
