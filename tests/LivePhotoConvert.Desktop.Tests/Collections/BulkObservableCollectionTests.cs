using System.Collections.Specialized;
using LivePhotoConvert.Desktop.Collections;

namespace LivePhotoConvert.Desktop.Tests.Collections;

public class BulkObservableCollectionTests
{
    [Fact]
    public void Reset_FiresSingleResetEvent()
    {
        var collection = new BulkObservableCollection<string>(["a", "b", "c"]);
        int eventCount = 0;
        NotifyCollectionChangedEventArgs? lastArgs = null;

        collection.CollectionChanged += (s, e) =>
        {
            eventCount++;
            lastArgs = e;
        };

        collection.Reset(["x", "y", "z", "w"]);

        Assert.Equal(1, eventCount);
        Assert.NotNull(lastArgs);
        Assert.Equal(NotifyCollectionChangedAction.Reset, lastArgs.Action);
        Assert.Equal(4, collection.Count);
        Assert.Equal(["x", "y", "z", "w"], collection);
    }

    [Fact]
    public void AddRange_FiresSingleResetEvent()
    {
        var collection = new BulkObservableCollection<int>([1, 2]);
        int eventCount = 0;
        NotifyCollectionChangedEventArgs? lastArgs = null;

        collection.CollectionChanged += (s, e) =>
        {
            eventCount++;
            lastArgs = e;
        };

        collection.AddRange([3, 4, 5]);

        Assert.Equal(1, eventCount);
        Assert.NotNull(lastArgs);
        Assert.Equal(NotifyCollectionChangedAction.Reset, lastArgs.Action);
        Assert.Equal(5, collection.Count);
        Assert.Equal([1, 2, 3, 4, 5], collection);
    }
}
