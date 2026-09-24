using Avalonia.Media.Imaging;
using Avalonia.Threading;
using LivePhotoConvert.Core.Media;
using LivePhotoConvert.Core.Media.Thumbnails;
using LivePhotoConvert.Core.Services;
using LivePhotoConvert.Desktop.Features.Library.Gallery;
using LivePhotoConvert.Desktop.Models;

namespace LivePhotoConvert.Desktop.Features.Library.Thumbnails;

/// <summary>
/// 画廊缩略图：已实例化的卡片按引用计数钉住，其余位图按字节预算做 LRU 驱逐。除 <see cref="LoadPreviewAsync"/> 外只在界面线程调用。
/// </summary>
public interface IThumbnailPipeline
{
    /// <summary>引用计数加一；未加载或尺寸已过期时按当前代次入队。</summary>
    void Acquire(PhotoCardItemViewModel card);

    /// <summary>引用计数减一；归零时出队并解除钉住，位图留在预算内等待复用或驱逐。</summary>
    void Release(PhotoCardItemViewModel card);

    /// <summary>滚动静止或重排后调用：之后入队的请求优先，旧代次对无引用卡片的结果被丢弃。</summary>
    int NextGeneration();

    /// <summary>
    /// 按最大行高与屏幕缩放比选择档位；方形裁切时竖图按铺满方框所需的高度解码。
    /// 变化时尺寸需求改变的卡片重新入队，新图到达前保留旧图。
    /// </summary>
    void Configure(double maxRowHeightDip, double renderScaling, bool squareCrop = false);

    /// <summary>重扫时调用：纪元加一，丢弃在途结果并释放无引用的位图。</summary>
    void Reset();

    /// <summary>
    /// 按指定像素高度取一张独立位图（经磁盘缓存与生成引擎），调用方拥有并负责释放。取消时抛出 <see cref="OperationCanceledException"/>。
    /// </summary>
    Task<Bitmap?> LoadPreviewAsync(PhotoCardItemViewModel card, int heightPx, CancellationToken cancellationToken);

    long ResidentBytes { get; }

    /// <summary>位图字节预算；调小时立即按 LRU 驱逐未钉住的位图，直到回到预算内或只剩钉住的位图。</summary>
    long BudgetBytes { get; set; }
}

/// <summary>缩略图的缓存查询与生成；默认包装 <see cref="ThumbnailGenerator"/>，测试替换为可控实现。</summary>
internal interface IThumbnailStore
{
    ThumbnailResult? TryGetCached(ThumbnailRequest request);

    Task<ThumbnailResult?> GetAsync(ThumbnailRequest request, CancellationToken cancellationToken);
}

/// <summary>把缩略图 JPEG 流解码为指定像素高度的位图。</summary>
internal delegate Bitmap ThumbnailBitmapDecoder(Stream stream, int heightPx);

public sealed class ThumbnailPipeline : IThumbnailPipeline, IDisposable
{
    public const long DefaultBudgetBytes = 192L * 1024 * 1024;

    /// <summary>未配置前按中等行高、100% 缩放取档。</summary>
    private const double DefaultMaxRowHeight = GalleryMetrics.MediumRowHeight * GalleryMetrics.MaxRowHeightFactor;

    private const int CacheWorkers = 1;

    /// <summary>生成走完整解码，CPU 与内存都重；与引擎的解码并发上限一致即可，再多只会排队。</summary>
    private const int GenerateWorkers = 2;

    /// <summary>被替换或驱逐的位图延迟释放，让已提交的渲染帧先放开对它的引用。</summary>
    private static readonly TimeSpan DisposeDelay = TimeSpan.FromMilliseconds(150);

    private readonly IThumbnailStore _store;
    private readonly ThumbnailBitmapDecoder _decode;
    private readonly ByteBudget<PhotoCardItemViewModel> _budget;
    private readonly Dictionary<PhotoCardItemViewModel, Entry> _entries = [];
    private readonly List<Bitmap> _pendingDisposals = [];
    private readonly CancellationTokenSource _shutdown = new();

    // 队列与信号跨线程访问，其余状态只在界面线程读写
    private readonly Lock _queueLock = new();
    private readonly SortedSet<WorkItem> _cacheQueue = new(WorkItemPriority.Instance);
    private readonly SortedSet<WorkItem> _generateQueue = new(WorkItemPriority.Instance);
    private readonly SemaphoreSlim _cacheSignal = new(0);
    private readonly SemaphoreSlim _generateSignal = new(0);

    private CancellationTokenSource _epochCts = new();
    private int _epoch;
    private int _generation;
    private int _configVersion;
    private long _sequence;
    private long _requestId;
    private int _attached;
    private double _maxRowHeight = DefaultMaxRowHeight;
    private double _renderScaling = 1.0;
    private bool _squareCrop;
    private bool _workersStarted;
    private bool _disposeScheduled;

    public ThumbnailPipeline(ThumbnailGenerator generator, long budgetBytes = DefaultBudgetBytes)
        : this(new GeneratorStore(generator), budgetBytes, DecodeDefault)
    {
    }

    internal ThumbnailPipeline(IThumbnailStore store, long budgetBytes, ThumbnailBitmapDecoder decode)
    {
        _store = store;
        _decode = decode;
        _budget = new ByteBudget<PhotoCardItemViewModel>(budgetBytes);
        (Tier, DecodeHeightPx) = SelectTarget(DefaultMaxRowHeight, 1.0);
    }

    /// <summary>当前磁盘缓存档位（按行高显示的卡片）。</summary>
    public int Tier { get; private set; }

    /// <summary>当前内存位图高度（像素，按行高显示的卡片）：不超过档位，也不超过最大行高 × 缩放比。</summary>
    public int DecodeHeightPx { get; private set; }

    public long ResidentBytes => _budget.ResidentBytes;

    public long PinnedBytes => _budget.PinnedBytes;

    public long BudgetBytes
    {
        get => _budget.CapacityBytes;
        set
        {
            _budget.CapacityBytes = value;
            EvictOverBudget();
        }
    }

    /// <summary>引用计数大于零的卡片数。</summary>
    public int AttachedCount => _attached;

    /// <summary>排队中（尚未开始处理）的请求数。</summary>
    public int PendingCount
    {
        get
        {
            lock (_queueLock)
            {
                return _cacheQueue.Count + _generateQueue.Count;
            }
        }
    }

    public int Generation => _generation;

    /// <summary>磁盘缓存档位：不小于需求像素的最小档。</summary>
    public static int SelectTier(double maxRowHeightDip, double renderScaling) => SelectTarget(maxRowHeightDip, renderScaling).Tier;

    /// <summary>档位与内存解码高度。解码高度取需求像素，使显示时的缩放比落在 1 附近。</summary>
    public static (int Tier, int DecodeHeightPx) SelectTarget(double maxRowHeightDip, double renderScaling)
    {
        var scaling = double.IsFinite(renderScaling) && renderScaling > 0 ? renderScaling : 1.0;
        var height = double.IsFinite(maxRowHeightDip) && maxRowHeightDip > 0 ? maxRowHeightDip : DefaultMaxRowHeight;
        var required = (int)Math.Min(int.MaxValue, Math.Ceiling(height * scaling));
        var tier = ThumbnailTiers.Select(required);
        return (tier, Math.Min(tier, required));
    }

    public void Acquire(PhotoCardItemViewModel card)
    {
        if (!_entries.TryGetValue(card, out var entry))
        {
            entry = new Entry();
            _entries[card] = entry;
        }

        if (entry.RefCount++ > 0)
        {
            return;
        }

        _attached++;
        entry.Sequence = ++_sequence;
        if (entry.Bitmap is not null)
        {
            _budget.Pin(card);
        }

        if (entry.Pending is { } pending)
        {
            if (pending.ConfigVersion == _configVersion)
            {
                // 释放后又回到视口：在途请求继续有效
                pending.IsCanceled = false;
                return;
            }

            Supersede(entry);
        }

        if (NeedsLoad(entry))
        {
            Enqueue(card, entry);
        }
    }

    public void Release(PhotoCardItemViewModel card)
    {
        if (!_entries.TryGetValue(card, out var entry) || entry.RefCount == 0)
        {
            return;
        }

        if (--entry.RefCount > 0)
        {
            return;
        }

        _attached--;
        if (entry.Bitmap is not null)
        {
            _budget.Unpin(card);
        }

        if (entry.Pending is { } pending && !TryDequeue(pending))
        {
            // 已在处理：结果回来时按引用与代次决定去留，生成通道据此跳过
            pending.IsCanceled = true;
        }
        else
        {
            entry.Pending = null;
        }

        EvictOverBudget();
        Forget(card, entry);
    }

    public int NextGeneration() => ++_generation;

    public void Configure(double maxRowHeightDip, double renderScaling, bool squareCrop = false)
    {
        var (tier, decodeHeight) = SelectTarget(maxRowHeightDip, renderScaling);
        if (tier == Tier && decodeHeight == DecodeHeightPx && squareCrop == _squareCrop)
        {
            return;
        }

        (Tier, DecodeHeightPx) = (tier, decodeHeight);
        (_maxRowHeight, _renderScaling, _squareCrop) = (maxRowHeightDip, renderScaling, squareCrop);
        _configVersion++;
        foreach (var (card, entry) in _entries)
        {
            // 尺寸需求未变的位图（例如切换方形裁切时的横图）直接沿用
            if (entry.Pending is null && entry.Bitmap is not null && entry.BitmapTarget == TargetFor(card))
            {
                entry.BitmapConfig = _configVersion;
                continue;
            }

            if (entry.RefCount == 0)
            {
                continue;
            }

            Supersede(entry);
            Enqueue(card, entry);
        }
    }

    /// <summary>
    /// 卡片的档位与解码高度。方形裁切（UniformToFill）下竖图要铺满边长等于行高的方框，
    /// 显示高度是行高 ÷ 宽高比，按行高解码再放大会发虚。
    /// </summary>
    public (int Tier, int DecodeHeightPx) TargetFor(PhotoCardItemViewModel card)
    {
        if (!_squareCrop)
        {
            return (Tier, DecodeHeightPx);
        }

        var aspect = JustifiedLayoutEngine.SafeAspect(card.AspectRatio);
        return SelectTarget(_maxRowHeight / Math.Min(1, aspect), _renderScaling);
    }

    public void Reset()
    {
        _epoch++;
        _epochCts.Cancel();
        _epochCts = new CancellationTokenSource();
        lock (_queueLock)
        {
            _cacheQueue.Clear();
            _generateQueue.Clear();
        }

        foreach (var (card, entry) in _entries.ToList())
        {
            entry.Pending = null;
            entry.FailedConfig = -1;
            if (entry.RefCount == 0)
            {
                if (entry.Bitmap is not null)
                {
                    Evict(card, entry);
                }

                Forget(card, entry);
            }
            else if (NeedsLoad(entry))
            {
                Enqueue(card, entry);
            }
        }
    }

    public Task<Bitmap?> LoadPreviewAsync(PhotoCardItemViewModel card, int heightPx, CancellationToken cancellationToken)
    {
        var source = PhotoSource.Of(card);
        var height = Math.Max(1, heightPx);
        var tier = ThumbnailTiers.Select(height);
        return Task.Run(async () =>
        {
            if (TryCreateRequest(source, tier) is not { } request ||
                await _store.GetAsync(request, cancellationToken).ConfigureAwait(false) is not { } result)
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return TryDecode(result, Math.Min(height, tier));
        }, cancellationToken);
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _epochCts.Cancel();
    }

    private bool NeedsLoad(Entry entry) =>
        entry.Pending is null &&
        entry.FailedConfig != _configVersion &&
        (entry.Bitmap is null || entry.BitmapConfig != _configVersion);

    private void Enqueue(PhotoCardItemViewModel card, Entry entry)
    {
        var (tier, decodeHeight) = TargetFor(card);
        var item = new WorkItem(
            card,
            PhotoSource.Of(card),
            tier,
            decodeHeight,
            _configVersion,
            _generation,
            entry.Sequence,
            ++_requestId,
            _epoch,
            _epochCts.Token);
        entry.Pending = item;
        lock (_queueLock)
        {
            _cacheQueue.Add(item);
            item.Queue = _cacheQueue;
        }

        _cacheSignal.Release();
        EnsureWorkers();
    }

    /// <summary>作废卡片当前的请求：排队中的直接移出，在途的结果回来后因不再是当前请求而丢弃。</summary>
    private void Supersede(Entry entry)
    {
        if (entry.Pending is { } pending)
        {
            TryDequeue(pending);
            pending.IsCanceled = true;
            entry.Pending = null;
        }
    }

    private bool TryDequeue(WorkItem item)
    {
        lock (_queueLock)
        {
            if (item.Queue is not { } queue)
            {
                return false;
            }

            queue.Remove(item);
            item.Queue = null;
            return true;
        }
    }

    private bool TryTake(SortedSet<WorkItem> queue, out WorkItem item)
    {
        lock (_queueLock)
        {
            if (queue.Count == 0)
            {
                item = null!;
                return false;
            }

            item = queue.Min!;
            queue.Remove(item);
            item.Queue = null;
            return true;
        }
    }

    private void EnsureWorkers()
    {
        if (_workersStarted)
        {
            return;
        }

        _workersStarted = true;
        var shutdown = _shutdown.Token;
        for (var i = 0; i < CacheWorkers; i++)
        {
            _ = Task.Run(() => RunWorkerAsync(_cacheSignal, _cacheQueue, ProcessCachedAsync, shutdown), shutdown);
        }

        for (var i = 0; i < GenerateWorkers; i++)
        {
            _ = Task.Run(() => RunWorkerAsync(_generateSignal, _generateQueue, ProcessGenerateAsync, shutdown), shutdown);
        }
    }

    private async Task RunWorkerAsync(SemaphoreSlim signal, SortedSet<WorkItem> queue, Func<WorkItem, ValueTask> process, CancellationToken shutdown)
    {
        while (!shutdown.IsCancellationRequested)
        {
            try
            {
                await signal.WaitAsync(shutdown).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // 出队的请求会让信号多于队列长度，取不到时继续等待
            if (!TryTake(queue, out var item))
            {
                continue;
            }

            try
            {
                await process(item).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Complete(item, Outcome.Dropped);
            }
            catch (Exception ex)
            {
                // 单张失败只降级为占位图，不能让 worker 退出
                ErrorLogger.Log(ex, "缩略图加载");
                Complete(item, Outcome.Failed);
            }
        }
    }

    private async ValueTask ProcessCachedAsync(WorkItem item)
    {
        if (item.IsStale)
        {
            Complete(item, Outcome.Dropped);
            return;
        }

        if (TryCreateRequest(item.Source, item.Tier) is not { } request)
        {
            Complete(item, Outcome.Failed);
            return;
        }

        item.Request = request;
        Bitmap? cached = null;
        if (_store.TryGetCached(request) is { CachePath: { } path } &&
            TryOpenCacheFile(path) is { } stream)
        {
            using (stream)
            {
                cached = TryDecode(stream, item.DecodeHeightPx);
            }
        }

        if (cached is not null)
        {
            await DeliverAsync(item, cached).ConfigureAwait(false);
            return;
        }

        // 未命中（含缓存文件刚被清理或损坏）：转入生成通道
        bool forwarded;
        lock (_queueLock)
        {
            forwarded = !item.IsStale;
            if (forwarded)
            {
                _generateQueue.Add(item);
                item.Queue = _generateQueue;
            }
        }

        if (!forwarded)
        {
            Complete(item, Outcome.Dropped);
            return;
        }

        _generateSignal.Release();
    }

    private async ValueTask ProcessGenerateAsync(WorkItem item)
    {
        if (item.IsStale || item.Request is not { } request)
        {
            Complete(item, Outcome.Dropped);
            return;
        }

        var result = await _store.GetAsync(request, item.EpochToken).ConfigureAwait(false);
        if (result is not null && TryDecode(result, item.DecodeHeightPx) is { } bitmap)
        {
            await DeliverAsync(item, bitmap).ConfigureAwait(false);
        }
        else
        {
            Complete(item, Outcome.Failed);
        }
    }

    /// <summary>
    /// 等界面线程接收后 worker 才继续：连续滚动时界面忙，解码结果不会在调度队列里越积越多。
    /// 优先级高于输入，否则持续的滚轮输入会一直推迟可见卡片的显示。
    /// </summary>
    private async Task DeliverAsync(WorkItem item, Bitmap bitmap)
    {
        // 自建完成源并异步续接：worker 的下一轮（含解码）不会内联到界面线程上执行
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                Apply(item, bitmap, Outcome.Loaded);
            }
            finally
            {
                delivered.TrySetResult();
            }
        }, DispatcherPriority.Default);

        try
        {
            await delivered.Task.WaitAsync(_shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 退出时不再等待界面线程
        }
    }

    /// <summary>不带位图的结论只需投递，不必等待。</summary>
    private void Complete(WorkItem item, Outcome outcome) =>
        Dispatcher.UIThread.Post(() => Apply(item, null, outcome), DispatcherPriority.Default);

    private void Apply(WorkItem item, Bitmap? bitmap, Outcome outcome)
    {
        var card = item.Card;
        _entries.TryGetValue(card, out var entry);
        var isCurrent = entry is not null && ReferenceEquals(entry.Pending, item);
        if (!isCurrent || item.Epoch != _epoch)
        {
            DisposeLater(bitmap);
            return;
        }

        entry!.Pending = null;
        switch (outcome)
        {
            case Outcome.Dropped when entry.RefCount > 0:
                // 取消与重新附加赛跑：卡片仍在显示就重新排队
                if (NeedsLoad(entry))
                {
                    Enqueue(card, entry);
                }

                break;
            case Outcome.Failed:
                entry.FailedConfig = item.ConfigVersion;
                break;
            case Outcome.Loaded when bitmap is not null:
                if (entry.RefCount == 0 && !ShouldKeepDetached(item, entry, bitmap))
                {
                    DisposeLater(bitmap);
                    break;
                }

                SetBitmap(card, entry, bitmap, item.ConfigVersion, (item.Tier, item.DecodeHeightPx));
                EvictOverBudget();
                break;
        }

        Forget(card, entry);
    }

    /// <summary>无引用卡片的结果只在属于当前代次且放得进预算时保留，供小幅回滚时直接显示。</summary>
    private bool ShouldKeepDetached(WorkItem item, Entry entry, Bitmap bitmap)
    {
        if (item.Generation < _generation)
        {
            return false;
        }

        var existing = entry.Bitmap is { } old ? BytesOf(old) : 0;
        return _budget.ResidentBytes - existing + BytesOf(bitmap) <= _budget.CapacityBytes;
    }

    private void SetBitmap(PhotoCardItemViewModel card, Entry entry, Bitmap bitmap, int configVersion, (int, int) target)
    {
        var old = entry.Bitmap;
        var wasResident = _budget.Contains(card);
        entry.Bitmap = bitmap;
        entry.BitmapConfig = configVersion;
        entry.BitmapTarget = target;
        card.Thumbnail = bitmap;
        _budget.Add(card, BytesOf(bitmap));
        if (!wasResident && entry.RefCount > 0)
        {
            _budget.Pin(card);
        }

        DisposeLater(old);
    }

    private void EvictOverBudget()
    {
        foreach (var card in _budget.CollectEvictions())
        {
            if (_entries.TryGetValue(card, out var entry))
            {
                Evict(card, entry, alreadyRemoved: true);
                Forget(card, entry);
            }
        }
    }

    /// <summary>先让卡片放开位图，界面绑定随即切回占位，再延迟释放像素内存。</summary>
    private void Evict(PhotoCardItemViewModel card, Entry entry, bool alreadyRemoved = false)
    {
        var old = entry.Bitmap;
        entry.Bitmap = null;
        if (!alreadyRemoved)
        {
            _budget.Remove(card);
        }

        if (old is null)
        {
            return;
        }

        if (ReferenceEquals(card.DisplayImage, old))
        {
            card.DisplayImage = null;
        }

        if (ReferenceEquals(card.Thumbnail, old))
        {
            card.Thumbnail = null;
        }

        DisposeLater(old);
    }

    private void Forget(PhotoCardItemViewModel card, Entry entry)
    {
        if (entry is { RefCount: 0, Bitmap: null, Pending: null })
        {
            _entries.Remove(card);
        }
    }

    private void DisposeLater(Bitmap? bitmap)
    {
        if (bitmap is null)
        {
            return;
        }

        _pendingDisposals.Add(bitmap);
        if (_disposeScheduled)
        {
            return;
        }

        _disposeScheduled = true;
        // 优先级高于输入：持续滚动时也要按时释放，否则待释放的位图会在预算之外堆积
        DispatcherTimer.RunOnce(FlushDisposals, DisposeDelay, DispatcherPriority.Default);
    }

    private void FlushDisposals()
    {
        _disposeScheduled = false;
        var pending = _pendingDisposals.ToArray();
        _pendingDisposals.Clear();
        foreach (var bitmap in pending)
        {
            bitmap.Dispose();
        }
    }

    private static long BytesOf(Bitmap bitmap) => (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4;

    private static ThumbnailRequest? TryCreateRequest(PhotoSource source, int tier)
    {
        try
        {
            // 扫描时已取得大小与修改时间；缺失时在后台线程补一次 stat
            var request = source.File is { } file
                ? new ThumbnailRequest(file.Path, file.Length, file.LastWriteTimeUtc, tier)
                : ThumbnailRequest.ForFile(source.Path, tier);
            return source.Header is { Width: > 0, Height: > 0 } header
                ? request with { Header = (header.Width, header.Height, header.Orientation) }
                : request;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>缓存文件可能在查询后被容量清理删除，打不开就当未命中。</summary>
    private static FileStream? TryOpenCacheFile(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 16 * 1024, FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private Bitmap? TryDecode(ThumbnailResult result, int heightPx)
    {
        Stream stream;
        try
        {
            stream = result.OpenRead();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }

        using (stream)
        {
            return TryDecode(stream, heightPx);
        }
    }

    private Bitmap? TryDecode(Stream stream, int heightPx)
    {
        try
        {
            // 小图与超宽全景的缩略图矮于目标高度，按原高解码，不在内存里放大
            if (FastImageHeaderReader.TryReadDimensions(stream, string.Empty, out var dimensions) && dimensions.Height > 0)
            {
                heightPx = Math.Min(heightPx, dimensions.Height);
            }

            stream.Position = 0;
            return _decode(stream, heightPx);
        }
        catch (Exception)
        {
            // 损坏的缓存或 Skia 像素分配失败都只降级为占位图
            return null;
        }
    }

    private static Bitmap DecodeDefault(Stream stream, int heightPx) =>
        Bitmap.DecodeToHeight(stream, heightPx, BitmapInterpolationMode.HighQuality);

    private enum Outcome
    {
        Loaded,
        Failed,
        Dropped,
    }

    private sealed class Entry
    {
        public int RefCount;
        public long Sequence;
        public WorkItem? Pending;
        public Bitmap? Bitmap;
        public int BitmapConfig = -1;
        public (int Tier, int DecodeHeightPx) BitmapTarget;

        /// <summary>该配置下解码失败过，避免对损坏文件反复重试；配置变化或重扫后才重试。</summary>
        public int FailedConfig = -1;
    }

    /// <summary>入队时的快照，worker 不再读取卡片。</summary>
    private readonly record struct PhotoSource(string Path, LibraryFile? File, ImageHeader? Header)
    {
        public static PhotoSource Of(PhotoCardItemViewModel card) => new(card.PhotoPath, card.PhotoFile, card.PhotoHeader);
    }

    private sealed class WorkItem(
        PhotoCardItemViewModel card,
        PhotoSource source,
        int tier,
        int decodeHeightPx,
        int configVersion,
        int generation,
        long sequence,
        long id,
        int epoch,
        CancellationToken epochToken)
    {
        public PhotoCardItemViewModel Card { get; } = card;
        public PhotoSource Source { get; } = source;
        public int Tier { get; } = tier;
        public int DecodeHeightPx { get; } = decodeHeightPx;
        public int ConfigVersion { get; } = configVersion;
        public int Generation { get; } = generation;
        public long Sequence { get; } = sequence;
        public long Id { get; } = id;
        public int Epoch { get; } = epoch;
        public CancellationToken EpochToken { get; } = epochToken;

        /// <summary>由缓存通道构造，生成通道复用。</summary>
        public ThumbnailRequest? Request { get; set; }

        /// <summary>所在队列；处理中为 null。受队列锁保护。</summary>
        public SortedSet<WorkItem>? Queue { get; set; }

        public bool IsCanceled
        {
            get => Volatile.Read(ref field);
            set => Volatile.Write(ref field, value);
        }

        public bool IsStale => IsCanceled || EpochToken.IsCancellationRequested;
    }

    /// <summary>代次新的优先，同代次后附加的优先（最近滚入视口的卡片最可能正被看着）。</summary>
    private sealed class WorkItemPriority : IComparer<WorkItem>
    {
        public static WorkItemPriority Instance { get; } = new();

        public int Compare(WorkItem? x, WorkItem? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return 1;
            }

            if (y is null)
            {
                return -1;
            }

            var byGeneration = y.Generation.CompareTo(x.Generation);
            if (byGeneration != 0)
            {
                return byGeneration;
            }

            var bySequence = y.Sequence.CompareTo(x.Sequence);
            return bySequence != 0 ? bySequence : y.Id.CompareTo(x.Id);
        }
    }

    private sealed class GeneratorStore(ThumbnailGenerator generator) : IThumbnailStore
    {
        public ThumbnailResult? TryGetCached(ThumbnailRequest request) => generator.TryGetCached(request);

        public Task<ThumbnailResult?> GetAsync(ThumbnailRequest request, CancellationToken cancellationToken) =>
            generator.GetAsync(request, cancellationToken);
    }
}
