using System.Buffers;

namespace LivePhotoConvert.Desktop.Features.Playback;

/// <summary>
/// 单一尺寸的帧缓冲池，随一次播放创建、随播放结束丢弃。
/// </summary>
/// <remarks>
/// 不用 <see cref="ArrayPool{T}.Shared"/>：它按 2 的幂分桶，1080p BGRA 帧（约 7.9 MiB）会拿到 16 MiB 的数组，
/// 实际占用翻倍；且归还后按核心各保留多份大数组，播放停止后内存仍被池占住。
/// </remarks>
public sealed class FrameBufferPool(int bufferLength, int maxRetained) : ArrayPool<byte>, IDisposable
{
    private readonly Stack<byte[]> _free = new();
    private readonly Lock _lock = new();
    private bool _disposed;

    public int BufferLength => bufferLength;

    /// <summary>累计新分配的数组数，用于验证复用。</summary>
    public int Allocations { get; private set; }

    public override byte[] Rent(int minimumLength)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minimumLength, bufferLength);
        lock (_lock)
        {
            if (_free.TryPop(out var array))
            {
                return array;
            }

            Allocations++;
        }

        return GC.AllocateUninitializedArray<byte>(bufferLength);
    }

    public override void Return(byte[] array, bool clearArray = false)
    {
        if (array.Length != bufferLength)
        {
            return;
        }

        lock (_lock)
        {
            // 释放后归还的数组直接交给 GC
            if (!_disposed && _free.Count < maxRetained)
            {
                _free.Push(array);
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _free.Clear();
        }
    }
}
