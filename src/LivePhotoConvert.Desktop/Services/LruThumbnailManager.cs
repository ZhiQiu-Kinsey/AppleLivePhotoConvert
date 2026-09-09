using Avalonia.Media.Imaging;
using LivePhotoConvert.Desktop.Models;

namespace LivePhotoConvert.Desktop.Services;

/// <summary>
/// 内存位图 LRU 缓存管理器：将内存中活跃的 Bitmap 数量严格限制在上限内，杜绝大相册内存膨胀。
/// </summary>
public sealed class LruThumbnailManager
{
    private const int MaxActiveBitmaps = 280;
    private readonly LinkedList<PhotoCardItemViewModel> _lruList = new();
    private readonly HashSet<PhotoCardItemViewModel> _activeSet = new();
    private readonly ThumbnailReader _reader;
    private readonly Lock _lock = new();

    public LruThumbnailManager(ThumbnailReader reader)
    {
        _reader = reader;
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

    public void EnsureThumbnailLoaded(PhotoCardItemViewModel card)
    {
        lock (_lock)
        {
            if (card.Thumbnail is not null)
            {
                _lruList.Remove(card);
                _lruList.AddLast(card);
                return;
            }

            var res = _reader.TryGetFromCacheOnly(card.PhotoPath, 640);
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
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            foreach (var card in _activeSet)
            {
                // 释放旧位图的非托管显存，避免大相册重扫时成批泄漏
                var old = card.Thumbnail;
                card.Thumbnail = null;
                old?.Dispose();
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
            var old = oldest.Thumbnail;
            oldest.Thumbnail = null;
            old?.Dispose();
        }
    }
}
