using System.Buffers;
using Avalonia;

namespace LivePhotoConvert.Desktop.Features.Playback;

/// <summary>缓存中的一帧。<see cref="Pts"/> 为连续播放时间轴上的位置（第 k 轮加上 k 倍循环时长）。</summary>
public sealed class StoredFrame(byte[] buffer, int length, TimeSpan pts, TimeSpan duration, long sequence)
{
    internal byte[] Buffer { get; } = buffer;

    public ReadOnlySpan<byte> Pixels => Buffer.AsSpan(0, length);

    public TimeSpan Pts { get; } = pts;

    public TimeSpan Duration { get; } = duration;

    /// <summary>入库顺序号；同一帧在全缓存循环中重复出现时不变，用于判断是否需要重绘。</summary>
    public long Sequence { get; } = sequence;
}

/// <summary>按播放位置查到的帧；<see cref="Position"/> 为实际采用的位置，解码跟不上时会小于请求值。</summary>
public readonly record struct FrameLookup(StoredFrame Frame, TimeSpan Position);

/// <summary>
/// 按字节预算管理解码帧：整段放得下就全部缓存循环播放，否则作为环形缓冲，只保留当前帧及其后已解码的帧。
/// </summary>
/// <remarks>
/// 模式由实际情况决定而非预估：第一轮解码结束前从未因空间不足驱逐过帧，即为全缓存；
/// 一旦驱逐，后续每轮由调用方重新解码，帧按「轮次 × 循环时长」接续在时间轴上，实现无缝循环。
/// 线程约定：生产者（解码线程）调用 <see cref="RentAsync"/>、<see cref="Commit"/>、<see cref="CancelRent"/>、<see cref="CompletePass"/>；
/// 消费者（UI 线程）调用 <see cref="Resolve"/> 与 <see cref="Dispose"/>。生产者只会驱逐游标（最近一次 Resolve 返回的帧）之前的帧，
/// 因此消费者在两次 Resolve 之间读取返回帧的像素是安全的。
/// </remarks>
public sealed class FrameStore : IDisposable
{
    /// <summary>当前帧加下一帧：环形缓冲最少需要的帧数。</summary>
    public const int MinimumCapacity = 2;

    /// <summary>显示用的两张位图也计入预算。</summary>
    public const int SurfaceFrames = 2;

    private readonly Lock _lock = new();
    private readonly ArrayPool<byte> _pool;
    private readonly bool _ownsPool;
    private readonly List<StoredFrame> _frames = [];
    private TaskCompletionSource? _spaceAvailable;
    private StoredFrame? _cursor;
    private long _nextSequence;
    private int _rented;
    private int _pass;
    private int _passFrames;
    private TimeSpan _passEnd;
    private TimeSpan _passOffset;
    private TimeSpan _loopDuration;
    private bool _evicted;
    private bool _fullyCached;
    private bool _disposed;

    /// <param name="frameBytes">每帧字节数</param>
    /// <param name="capacity">最多同时持有的帧数</param>
    /// <param name="pool">帧缓冲来源；null 时创建只服务本实例的 <see cref="FrameBufferPool"/></param>
    public FrameStore(int frameBytes, int capacity, ArrayPool<byte>? pool = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, MinimumCapacity);
        FrameBytes = frameBytes;
        Capacity = capacity;
        _ownsPool = pool is null;
        _pool = pool ?? new FrameBufferPool(frameBytes, capacity);
    }

    public static FrameStore Create(PixelSize frameSize, PlaybackBudget budget)
    {
        var frameBytes = checked(frameSize.Width * frameSize.Height * 4);
        return new FrameStore(frameBytes, CapacityFor(frameBytes, budget));
    }

    /// <summary>预算扣除两张显示位图后能容纳的帧数，至少 <see cref="MinimumCapacity"/>。</summary>
    public static int CapacityFor(int frameBytes, PlaybackBudget budget)
    {
        var frames = (budget.Bytes / frameBytes) - SurfaceFrames;
        return (int)Math.Clamp(frames, MinimumCapacity, int.MaxValue);
    }

    /// <summary>预估帧数的整段视频能否全部缓存。</summary>
    public static bool FitsEntirely(int frameBytes, int frameCount, PlaybackBudget budget) => frameCount <= CapacityFor(frameBytes, budget);

    public int FrameBytes { get; }

    public int Capacity { get; }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _frames.Count;
            }
        }
    }

    /// <summary>第一轮已完整缓存，之后不再需要解码。</summary>
    public bool IsFullyCached
    {
        get
        {
            lock (_lock)
            {
                return _fullyCached;
            }
        }
    }

    /// <summary>发生过驱逐，需要每轮重新解码。</summary>
    public bool IsStreaming
    {
        get
        {
            lock (_lock)
            {
                return _evicted;
            }
        }
    }

    /// <summary>一轮的时长；第一轮结束前为 null。</summary>
    public TimeSpan? LoopDuration
    {
        get
        {
            lock (_lock)
            {
                return _pass > 0 ? _loopDuration : null;
            }
        }
    }

    /// <summary>当前生产的轮次（从 0 开始）。</summary>
    public int Pass
    {
        get
        {
            lock (_lock)
            {
                return _pass;
            }
        }
    }

    /// <summary>
    /// 取得一块空闲帧缓冲；已满时先驱逐游标之前的帧，没有可驱逐的就等消费者前进。已释放时返回 null。
    /// </summary>
    public async ValueTask<byte[]?> RentAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task wait;
            lock (_lock)
            {
                if (_disposed)
                {
                    return null;
                }

                if (_frames.Count + _rented < Capacity || TryEvictOne())
                {
                    _rented++;
                    break;
                }

                _spaceAvailable ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                wait = _spaceAvailable.Task;
            }

            await wait.WaitAsync(cancellationToken);
        }

        return _pool.Rent(FrameBytes);
    }

    /// <summary>归还未使用的缓冲（读到结尾或出错）。</summary>
    public void CancelRent(byte[] buffer)
    {
        lock (_lock)
        {
            _rented--;
            SignalSpace();
        }

        _pool.Return(buffer);
    }

    /// <summary>
    /// 加入本轮的一帧；<paramref name="timing"/> 为本轮时间轴（首帧为 0）。已释放时直接归还缓冲并返回 false。
    /// </summary>
    public bool Commit(byte[] buffer, FrameTiming timing)
    {
        lock (_lock)
        {
            _rented--;
            if (!_disposed)
            {
                var pts = _passOffset + timing.Pts;
                if (_frames.Count > 0 && pts <= _frames[^1].Pts)
                {
                    pts = _frames[^1].Pts + TimeSpan.FromTicks(1);
                }

                _frames.Add(new StoredFrame(buffer, FrameBytes, pts, timing.Duration, _nextSequence++));
                _passFrames++;
                _passEnd = timing.Pts + timing.Duration;
                return true;
            }
        }

        _pool.Return(buffer);
        return false;
    }

    /// <summary>
    /// 本轮解码结束。第一轮确定循环时长，且未驱逐过帧时进入全缓存；返回本轮是否有帧。
    /// </summary>
    public bool CompletePass()
    {
        lock (_lock)
        {
            if (_disposed || _passFrames == 0)
            {
                return false;
            }

            if (_pass == 0)
            {
                _loopDuration = _passEnd > TimeSpan.Zero ? _passEnd : TimeSpan.FromMilliseconds(1);
                _fullyCached = !_evicted;
            }

            _pass++;
            _passOffset = _loopDuration * _pass;
            _passFrames = 0;
            _passEnd = TimeSpan.Zero;
            return true;
        }
    }

    /// <summary>
    /// 取播放位置 <paramref name="position"/> 处应显示的帧，并把它设为游标。尚无帧时返回 null。
    /// </summary>
    /// <remarks>非全缓存时，位置超过已解码的最后一帧的结束时刻会被截住，调用方据此暂停时钟，解码追上后从原处继续而不跳帧。</remarks>
    public FrameLookup? Resolve(TimeSpan position)
    {
        lock (_lock)
        {
            if (_disposed || _frames.Count == 0)
            {
                return null;
            }

            if (position < TimeSpan.Zero)
            {
                position = TimeSpan.Zero;
            }

            TimeSpan lookup;
            if (_fullyCached)
            {
                lookup = TimeSpan.FromTicks(position.Ticks % _loopDuration.Ticks);
            }
            else
            {
                var last = _frames[^1];
                var available = last.Pts + last.Duration;
                if (position > available)
                {
                    position = available;
                }

                lookup = position;
            }

            var index = FindFrame(lookup);
            var frame = _frames[index];
            var advanced = !ReferenceEquals(frame, _cursor);
            _cursor = frame;
            if (advanced && index > 0 && !_fullyCached)
            {
                SignalSpace();
            }

            return new FrameLookup(frame, position);
        }
    }

    /// <summary>释放全部帧缓冲；之后到达的帧在 <see cref="Commit"/> 中直接归还。</summary>
    public void Dispose()
    {
        List<StoredFrame> released;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            released = [.. _frames];
            _frames.Clear();
            _cursor = null;
            SignalSpace();
        }

        foreach (var frame in released)
        {
            _pool.Return(frame.Buffer);
        }

        if (_ownsPool && _pool is FrameBufferPool owned)
        {
            owned.Dispose();
        }
    }

    /// <summary>最后一个 Pts 不晚于 <paramref name="position"/> 的帧；位置早于首帧时取首帧。</summary>
    private int FindFrame(TimeSpan position)
    {
        int low = 0, high = _frames.Count - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            if (_frames[middle].Pts <= position)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return Math.Max(high, 0);
    }

    private bool TryEvictOne()
    {
        if (_fullyCached || _cursor is null || _frames.Count == 0 || ReferenceEquals(_frames[0], _cursor) || _frames[0].Pts >= _cursor.Pts)
        {
            return false;
        }

        var evicted = _frames[0];
        _frames.RemoveAt(0);
        _evicted = true;
        _pool.Return(evicted.Buffer);
        return true;
    }

    private void SignalSpace()
    {
        var waiting = _spaceAvailable;
        _spaceAvailable = null;
        waiting?.TrySetResult();
    }
}
