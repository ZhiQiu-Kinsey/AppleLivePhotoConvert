using LivePhotoConvert.Core.Metadata;
using LivePhotoConvert.Core.Services;
using LivePhotoConvert.Desktop.Features.Tasks;

namespace LivePhotoConvert.Desktop.Features.Library;

/// <summary>
/// 预估与对比共用的元数据服务：连续的预估与样张编码复用同一个 ExifTool 常驻进程，
/// 空闲超过 <see cref="IdleTimeout"/> 才结束进程；工具路径变化后旧服务在最后一个使用者归还时释放。
/// </summary>
public sealed class MetadataSessionPool(IConversionEngines engines, TimeProvider time) : IDisposable
{
    /// <summary>选择变化引起的预估间隔通常是几秒；常驻进程只占几十 MB，空闲一段时间后再还给系统。</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2);

    private readonly Lock _gate = new();
    private Entry? _current;
    private bool _disposed;

    /// <summary>借出服务；使用完毕释放租约。</summary>
    /// <exception cref="FileNotFoundException">找不到 ExifTool</exception>
    public Lease Acquire(ToolPaths tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        Entry? retired = null;
        Entry entry;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_current is { } current && current.Tools == tools)
            {
                entry = current;
            }
            else
            {
                // 预估与对比都是一次一张地读写，一个会话足够；创建失败（找不到工具）时保留原会话
                entry = new Entry(tools, engines.CreateMetadata(tools, 1));
                if (_current is { } stale)
                {
                    stale.Retired = true;
                    retired = stale.Leases == 0 ? stale : null;
                }

                _current = entry;
            }

            entry.Leases++;
            entry.CancelIdleTimer();
        }

        retired?.DisposeService();
        return new Lease(this, entry);
    }

    public void Dispose()
    {
        Entry? current;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            current = _current;
            _current = null;
        }

        current?.DisposeService();
    }

    private void Release(Entry entry)
    {
        var dispose = false;
        lock (_gate)
        {
            entry.Leases--;
            if (entry.Leases > 0)
            {
                return;
            }

            if (entry.Retired || _disposed)
            {
                dispose = true;
            }
            else
            {
                entry.StartIdleTimer(time, IdleTimeout, () => Expire(entry));
            }
        }

        if (dispose)
        {
            entry.DisposeService();
        }
    }

    private void Expire(Entry entry)
    {
        lock (_gate)
        {
            if (entry.Leases > 0 || !ReferenceEquals(_current, entry))
            {
                return;
            }

            _current = null;
        }

        entry.DisposeService();
    }

    public sealed class Lease : IDisposable
    {
        private MetadataSessionPool? _pool;
        private readonly Entry _entry;

        internal Lease(MetadataSessionPool pool, Entry entry)
        {
            _pool = pool;
            _entry = entry;
        }

        public IMetadataService Service => _entry.Service;

        public void Dispose() => Interlocked.Exchange(ref _pool, null)?.Release(_entry);
    }

    internal sealed class Entry(ToolPaths tools, IMetadataService service)
    {
        private ITimer? _idleTimer;
        private int _disposed;

        public ToolPaths Tools { get; } = tools;

        public IMetadataService Service { get; } = service;

        public int Leases { get; set; }

        public bool Retired { get; set; }

        public void StartIdleTimer(TimeProvider time, TimeSpan delay, Action expire)
        {
            CancelIdleTimer();
            _idleTimer = time.CreateTimer(_ => expire(), null, delay, Timeout.InfiniteTimeSpan);
        }

        public void CancelIdleTimer()
        {
            _idleTimer?.Dispose();
            _idleTimer = null;
        }

        public void DisposeService()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            CancelIdleTimer();
            _ = DisposeAsync();
        }

        private async Task DisposeAsync()
        {
            try
            {
                await Service.DisposeAsync();
            }
            catch (Exception ex)
            {
                ErrorLogger.Log(ex, "释放 ExifTool 会话");
            }
        }
    }
}
