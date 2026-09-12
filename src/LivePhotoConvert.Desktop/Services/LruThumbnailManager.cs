using Avalonia.Media.Imaging;
using Avalonia.Threading;
using LivePhotoConvert.Desktop.Models;
namespace LivePhotoConvert.Desktop.Services;

/// <summary>
/// 内存位图 LRU 缓存管理器：将内存中活跃的 Bitmap 数量严格限制在上限内，杜绝大相册内存膨胀。
/// </summary>
public sealed class LruThumbnailManager
{
    // 960px 足够覆盖高 DPI 卡片；同步降低活跃张数，把总像素预算维持在可控范围。
    internal const int ThumbnailMaxSize = 960;
    private const int MaxActiveBitmaps = 48;
    private readonly LinkedList<PhotoCardItemViewModel> _lruList = new();
    private readonly HashSet<PhotoCardItemViewModel> _activeSet = new();
    private readonly ThumbnailReader _reader;
    private readonly Lock _lock = new();
    private readonly List<Bitmap> _pendingDisposals = [];
    private readonly DispatcherTimer _disposeTimer;

    public LruThumbnailManager(ThumbnailReader reader)
    {
        _reader = reader;
        _disposeTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _disposeTimer.Tick += (_, _) => FlushPendingDisposals();
    }

    public void SetThumbnail(PhotoCardItemViewModel card, Bitmap bitmap)
    {
        lock (_lock)
        {
            if (card.Thumbnail is not null)
            {
                // 卡片已有缩略图：新传入的位图未被使用，必须释放避免非托管内存泄漏
                bitmap.Dispose();
                _lruList.Remove(card);
                _lruList.AddLast(card);
                return;
            }

            card.Thumbnail = bitmap;

            _lruList.AddLast(card);
            _activeSet.Add(card);

            EvictOldestIfNeeded();
        }
    }

    public void RequestThumbnail(PhotoCardItemViewModel card, byte[] jpgBytes)
    {
        lock (_lock)
        {
            if (card.Thumbnail is not null)
            {
                _lruList.Remove(card);
                _lruList.AddLast(card);
                return;
            }

            using var ms = new MemoryStream(jpgBytes);
            card.Thumbnail = new Bitmap(ms);

            _lruList.AddLast(card);
            _activeSet.Add(card);

            EvictOldestIfNeeded();
        }
    }

    public (int Width, int Height)? EnsureThumbnailLoaded(PhotoCardItemViewModel card)
    {
        lock (_lock)
        {
            if (card.Thumbnail is not null)
            {
                _lruList.Remove(card);
                _lruList.AddLast(card);
                return null;
            }

            var res = _reader.TryGetFromCacheOnly(card.PhotoPath, ThumbnailMaxSize);
            if (res is not null)
            {
                using var ms = new MemoryStream(res.ImageBytes);
                card.Thumbnail = new Bitmap(ms);
                if (string.IsNullOrEmpty(card.ResolutionText))
                {
                    card.ResolutionText = $"{res.Width}×{res.Height}";
                }

                _lruList.AddLast(card);
                _activeSet.Add(card);

                EvictOldestIfNeeded();
                return (res.Width, res.Height);
            }

            return null;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            foreach (var card in _activeSet)
            {
                // 释放旧位图的非托管显存，避免大相册重扫时成批泄漏
                DetachAndQueueDispose(card);
            }
            _lruList.Clear();
            _activeSet.Clear();
        }
    }

    private void EvictOldestIfNeeded()
    {
        while (_lruList.Count > MaxActiveBitmaps)
        {
            var oldest = _lruList.First!.Value;
            _lruList.RemoveFirst();
            _activeSet.Remove(oldest);
            // 释放被剔除卡片的旧位图，释放非托管显存
            DetachAndQueueDispose(oldest);
        }
    }

    /// <summary>
    /// 先从 ViewModel 解除所有图片引用，再延迟到当前布局/渲染帧完成后释放底层位图。
    /// Avalonia 的虚拟化容器可能在同一帧内继续 Measure 已移出视口的 Image。
    /// </summary>
    private void DetachAndQueueDispose(PhotoCardItemViewModel card)
    {
        var old = card.Thumbnail;
        if (old is null) return;

        if (ReferenceEquals(card.DisplayImage, old))
        {
            card.DisplayImage = null;
        }
        card.Thumbnail = null;
        _pendingDisposals.Add(old);
        _disposeTimer.Stop();
        _disposeTimer.Start();
    }

    private void FlushPendingDisposals()
    {
        List<Bitmap> pending;
        lock (_lock)
        {
            _disposeTimer.Stop();
            pending = [.. _pendingDisposals];
            _pendingDisposals.Clear();
        }

        foreach (var bitmap in pending)
        {
            bitmap.Dispose();
        }
    }
}
