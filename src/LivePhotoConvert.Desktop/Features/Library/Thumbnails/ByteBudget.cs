namespace LivePhotoConvert.Desktop.Features.Library.Thumbnails;

/// <summary>
/// 按字节计的 LRU 驻留预算。钉住的条目（正在显示）永不被驱逐，因此钉住字节本身超出预算时驻留量可以超出容量。
/// 非线程安全，只在界面线程使用。
/// </summary>
public sealed class ByteBudget<TKey>(long capacityBytes) where TKey : notnull
{
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _entries = [];

    /// <summary>从旧到新：First 最久未使用。</summary>
    private readonly LinkedList<Entry> _lru = new();

    public long CapacityBytes
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            field = value;
        }
    } = capacityBytes >= 0 ? capacityBytes : throw new ArgumentOutOfRangeException(nameof(capacityBytes));

    public long ResidentBytes { get; private set; }

    public long PinnedBytes { get; private set; }

    public int Count => _entries.Count;

    public bool Contains(TKey key) => _entries.ContainsKey(key);

    public bool IsPinned(TKey key) => _entries.TryGetValue(key, out var node) && node.Value.Pins > 0;

    /// <summary>登记或更新条目的字节数并标记为最近使用；已有的钉住计数保留。</summary>
    public void Add(TKey key, long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        if (_entries.TryGetValue(key, out var node))
        {
            Account(node.Value, -1);
            node.Value.Bytes = bytes;
            Account(node.Value, +1);
            MoveToNewest(node);
            return;
        }

        var entry = new Entry(key) { Bytes = bytes };
        _entries[key] = _lru.AddLast(entry);
        Account(entry, +1);
    }

    public void Remove(TKey key)
    {
        if (_entries.Remove(key, out var node))
        {
            Account(node.Value, -1);
            _lru.Remove(node);
        }
    }

    /// <summary>钉住计数加一；未登记的键忽略（调用方在登记后再钉住）。</summary>
    public void Pin(TKey key)
    {
        if (!_entries.TryGetValue(key, out var node))
        {
            return;
        }

        if (node.Value.Pins++ == 0)
        {
            PinnedBytes += node.Value.Bytes;
        }

        MoveToNewest(node);
    }

    public void Unpin(TKey key)
    {
        if (!_entries.TryGetValue(key, out var node) || node.Value.Pins == 0)
        {
            return;
        }

        if (--node.Value.Pins == 0)
        {
            PinnedBytes -= node.Value.Bytes;
        }

        // 刚解除钉住的条目最可能被再次显示（小幅回滚），排在最新
        MoveToNewest(node);
    }

    public void Touch(TKey key)
    {
        if (_entries.TryGetValue(key, out var node))
        {
            MoveToNewest(node);
        }
    }

    /// <summary>
    /// 按 LRU 顺序移出未钉住的条目直到驻留量不超过容量，返回被移出的键（已从预算中注销）。
    /// 全部钉住时返回空列表。
    /// </summary>
    public IReadOnlyList<TKey> CollectEvictions()
    {
        if (ResidentBytes <= CapacityBytes)
        {
            return [];
        }

        List<TKey> evicted = [];
        var node = _lru.First;
        while (node is not null && ResidentBytes > CapacityBytes)
        {
            var next = node.Next;
            if (node.Value.Pins == 0)
            {
                evicted.Add(node.Value.Key);
                Remove(node.Value.Key);
            }

            node = next;
        }

        return evicted;
    }

    private void MoveToNewest(LinkedListNode<Entry> node)
    {
        if (node != _lru.Last)
        {
            _lru.Remove(node);
            _lru.AddLast(node);
        }
    }

    private void Account(Entry entry, int sign)
    {
        ResidentBytes += sign * entry.Bytes;
        if (entry.Pins > 0)
        {
            PinnedBytes += sign * entry.Bytes;
        }
    }

    private sealed class Entry(TKey key)
    {
        public TKey Key { get; } = key;

        public long Bytes { get; set; }

        public int Pins { get; set; }
    }
}
