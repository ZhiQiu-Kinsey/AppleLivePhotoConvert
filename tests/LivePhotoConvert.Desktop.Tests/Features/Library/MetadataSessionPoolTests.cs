using LivePhotoConvert.Core.Abstractions;
using LivePhotoConvert.Core.External;
using LivePhotoConvert.Core.Metadata;
using LivePhotoConvert.Desktop.Features.Library;
using LivePhotoConvert.Desktop.Features.Tasks;
using LivePhotoConvert.Desktop.Tests.Features.Dialogs;
using Microsoft.Extensions.Time.Testing;

namespace LivePhotoConvert.Desktop.Tests.Features.Library;

/// <summary>预估与对比共用的 ExifTool 会话：并发借用共享一个会话，空闲超时或换工具路径后释放。</summary>
public class MetadataSessionPoolTests
{
    private static readonly ToolPaths Other = new("/custom/exiftool", null, null);

    [Fact]
    public void ConcurrentLeases_ShareOneSession_AndIdleTimerStartsAfterLastRelease()
    {
        var engines = new CountingEngines(new LossyStandInEncoder());
        var time = new FakeTimeProvider();
        using var pool = new MetadataSessionPool(engines, time);

        var first = pool.Acquire(ToolPaths.Auto);
        var second = pool.Acquire(ToolPaths.Auto);
        Assert.Same(first.Service, second.Service);
        first.Dispose();
        first.Dispose();
        time.Advance(MetadataSessionPool.IdleTimeout * 2);
        Assert.Equal(0, engines.MetadataDisposed);

        second.Dispose();
        time.Advance(MetadataSessionPool.IdleTimeout);
        Assert.Equal(1, engines.MetadataCreated);
        Assert.Equal(1, engines.MetadataDisposed);
    }

    [Fact]
    public void ReacquireBeforeTimeout_CancelsIdleRelease()
    {
        var engines = new CountingEngines(new LossyStandInEncoder());
        var time = new FakeTimeProvider();
        using var pool = new MetadataSessionPool(engines, time);

        pool.Acquire(ToolPaths.Auto).Dispose();
        time.Advance(MetadataSessionPool.IdleTimeout / 2);
        using (pool.Acquire(ToolPaths.Auto))
        {
            time.Advance(MetadataSessionPool.IdleTimeout);
            Assert.Equal(0, engines.MetadataDisposed);
        }

        Assert.Equal(1, engines.MetadataCreated);
    }

    [Fact]
    public void ToolPathChange_RetiresOldSessionWhenItsLastLeaseEnds()
    {
        var engines = new CountingEngines(new LossyStandInEncoder());
        using var pool = new MetadataSessionPool(engines, new FakeTimeProvider());

        var old = pool.Acquire(ToolPaths.Auto);
        using var fresh = pool.Acquire(Other);
        Assert.NotSame(old.Service, fresh.Service);
        Assert.Equal(0, engines.MetadataDisposed);

        old.Dispose();
        Assert.Equal(1, engines.MetadataDisposed);
    }

    [Fact]
    public void CreationFailure_KeepsExistingSession()
    {
        var engines = new FailingForOtherPaths();
        using var pool = new MetadataSessionPool(engines, new FakeTimeProvider());
        var service = pool.Acquire(ToolPaths.Auto);
        service.Dispose();

        Assert.Throws<ToolNotFoundException>(() => pool.Acquire(Other));

        using var again = pool.Acquire(ToolPaths.Auto);
        Assert.Same(service.Service, again.Service);
        Assert.Equal(1, engines.Created);
    }

    [Fact]
    public void Dispose_ReleasesCurrentSession_AndRejectsNewLeases()
    {
        var engines = new CountingEngines(new LossyStandInEncoder());
        var pool = new MetadataSessionPool(engines, new FakeTimeProvider());
        pool.Acquire(ToolPaths.Auto).Dispose();

        pool.Dispose();

        Assert.Equal(1, engines.MetadataDisposed);
        Assert.Throws<ObjectDisposedException>(() => pool.Acquire(ToolPaths.Auto));
    }

    private sealed class FailingForOtherPaths : IConversionEngines
    {
        private readonly CountingEngines _inner = new(new LossyStandInEncoder());

        public int Created => _inner.MetadataCreated;

        public IMetadataService CreateMetadata(ToolPaths tools, int parallelism) =>
            tools == ToolPaths.Auto ? _inner.CreateMetadata(tools, parallelism) : throw new ToolNotFoundException("exiftool");

        public IImageConverter CreateImageConverter(ToolPaths tools) => _inner.CreateImageConverter(tools);

        public IVideoConverter CreateVideoConverter(ToolPaths tools) => throw new NotSupportedException();

        public IAppleGainMapDecoder? CreateGainMapDecoder(ToolPaths tools) => null;
    }
}
