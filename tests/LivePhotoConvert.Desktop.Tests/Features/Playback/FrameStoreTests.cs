using System.Buffers;
using LivePhotoConvert.Desktop.Features.Playback;

namespace LivePhotoConvert.Desktop.Tests.Features.Playback;

public class FrameStoreTests
{
    private const int FrameBytes = 16;
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(100);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void CapacityFor_SubtractsDisplaySurfacesFromBudget()
    {
        var frame1080 = 1920 * 1080 * 4;

        Assert.Equal(30, FrameStore.CapacityFor(frame1080, PlaybackBudget.QuickLook));
        Assert.Equal(10, FrameStore.CapacityFor(frame1080, PlaybackBudget.Hover));
        // 帧再大也至少保留当前帧与下一帧
        Assert.Equal(FrameStore.MinimumCapacity, FrameStore.CapacityFor(64 * 1024 * 1024, PlaybackBudget.Hover));
    }

    [Fact]
    public void FitsEntirely_DecidesBetweenFullCacheAndRing()
    {
        // 悬浮卡片 600×450：3 秒 30fps 共 90 帧放得下
        Assert.True(FrameStore.FitsEntirely(600 * 450 * 4, 90, PlaybackBudget.Hover));
        // QuickLook 1080p 同样 90 帧放不下，走环形缓冲
        Assert.False(FrameStore.FitsEntirely(1920 * 1080 * 4, 90, PlaybackBudget.QuickLook));
    }

    [Fact]
    public async Task WholeClipFits_CachesEverythingAndLoopsByPts()
    {
        using var store = new FrameStore(FrameBytes, capacity: 10);
        await ProduceAsync(store, frames: 5);

        Assert.True(store.CompletePass());
        Assert.True(store.IsFullyCached);
        Assert.False(store.IsStreaming);
        Assert.Equal(TimeSpan.FromMilliseconds(500), store.LoopDuration);

        long[] shown = [.. Enumerable.Range(0, 16).Select(i => store.Resolve(Step * i)!.Value.Frame.Sequence)];
        Assert.Equal([0, 1, 2, 3, 4, 0, 1, 2, 3, 4, 0, 1, 2, 3, 4, 0], shown);
        // 帧间任意位置取不晚于该位置的最后一帧
        Assert.Equal(2, store.Resolve(TimeSpan.FromMilliseconds(1_299))!.Value.Frame.Sequence);
        Assert.Equal(5, store.Count);
    }

    [Fact]
    public async Task ClipExceedsBudget_StreamsThroughRingAndContinuesSeamlesslyIntoNextPass()
    {
        using var store = new FrameStore(FrameBytes, capacity: 3);
        var maxCount = 0;
        var producer = Task.Run(async () =>
        {
            // 与播放器的解码循环一致：每轮结束立即开始下一轮
            for (var pass = 0; pass < 3; pass++)
            {
                await ProduceAsync(store, frames: 5, pass);
                if (!store.CompletePass() || store.IsFullyCached)
                {
                    return;
                }
            }
        }, Token);

        List<(long Sequence, TimeSpan Pts, byte Tag)> shown = [];
        for (var i = 0; i < 15; i++)
        {
            var position = Step * i;
            var lookup = await WaitForAsync(store, position);
            maxCount = Math.Max(maxCount, store.Count);
            shown.Add((lookup.Frame.Sequence, lookup.Frame.Pts, lookup.Frame.Pixels[0]));
        }

        await producer.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.True(store.IsStreaming);
        Assert.False(store.IsFullyCached);
        Assert.Equal(TimeSpan.FromMilliseconds(500), store.LoopDuration);
        Assert.InRange(maxCount, 1, 3);
        Assert.Equal(Enumerable.Range(0, 15).Select(i => (long)i), shown.Select(s => s.Sequence));
        Assert.Equal(Enumerable.Range(0, 15).Select(i => Step * i), shown.Select(s => s.Pts));
        // 像素标记 = 轮次 × 10 + 帧号：第二、三轮按原顺序重放
        Assert.Equal([0, 1, 2, 3, 4, 10, 11, 12, 13, 14, 20, 21, 22, 23, 24], shown.Select(s => (int)s.Tag));
    }

    [Fact]
    public async Task DisplayedFrame_IsNeverEvictedOrOverwritten()
    {
        using var store = new FrameStore(FrameBytes, capacity: 2);
        await ProduceAsync(store, frames: 2);
        var current = store.Resolve(TimeSpan.Zero)!.Value.Frame;

        // 已满且唯一可驱逐的候选是正在显示的帧：生产者必须等待
        var rent = store.RentAsync(Token).AsTask();
        await Task.Delay(100, Token);
        Assert.False(rent.IsCompleted);

        var next = store.Resolve(Step)!.Value.Frame;
        var buffer = await rent.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.NotNull(buffer);
        Array.Fill(buffer, (byte)0xEE);
        store.Commit(buffer, new FrameTiming(Step * 2, Step));

        // 驱逐的是已经过去的帧，当前帧像素保持原样
        Assert.Equal(2, store.Count);
        Assert.Equal(1, next.Pixels[0]);
        Assert.All(next.Pixels.ToArray(), b => Assert.Equal(1, b));
        Assert.Equal(0xEE, store.Resolve(Step * 2)!.Value.Frame.Pixels[0]);
        Assert.Equal(0, current.Sequence);
    }

    [Fact]
    public async Task DecoderBehind_ClampsPositionToDecodedEnd()
    {
        using var store = new FrameStore(FrameBytes, capacity: 10);
        await ProduceAsync(store, frames: 3);

        var lookup = store.Resolve(TimeSpan.FromSeconds(1))!.Value;

        Assert.Equal(2, lookup.Frame.Sequence);
        Assert.Equal(TimeSpan.FromMilliseconds(300), lookup.Position);
    }

    [Fact]
    public async Task Dispose_ReturnsAllBuffersAndLateFramesGoStraightBack()
    {
        var pool = new CountingPool();
        var store = new FrameStore(FrameBytes, capacity: 4, pool);
        await ProduceAsync(store, frames: 3);
        var outstanding = await store.RentAsync(Token);
        Assert.NotNull(outstanding);

        store.Dispose();

        Assert.Equal(0, store.Count);
        Assert.Null(store.Resolve(TimeSpan.Zero));
        Assert.Equal(3, pool.Returned);
        Assert.False(store.Commit(outstanding, new FrameTiming(Step * 3, Step)));
        Assert.Equal(pool.Rented, pool.Returned);
        Assert.Null(await store.RentAsync(Token));
    }

    [Fact]
    public async Task Dispose_ReleasesWaitingProducer()
    {
        using var store = new FrameStore(FrameBytes, capacity: 2);
        await ProduceAsync(store, frames: 2);
        var rent = store.RentAsync(Token).AsTask();

        store.Dispose();

        Assert.Null(await rent.WaitAsync(TimeSpan.FromSeconds(5), Token));
    }

    [Fact]
    public async Task StreamingReusesPooledBuffers()
    {
        var pool = new FrameBufferPool(FrameBytes, maxRetained: 3);
        using var store = new FrameStore(FrameBytes, capacity: 3, pool);
        var producer = Task.Run(() => ProduceAsync(store, frames: 30), Token);
        for (var i = 0; i < 30; i++)
        {
            await WaitForAsync(store, Step * i);
        }

        await producer.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.InRange(pool.Allocations, 3, 4);
    }

    /// <summary>产出一轮帧：像素全部填为「轮次 × 10 + 帧号」。</summary>
    private static async Task ProduceAsync(FrameStore store, int frames, int pass = 0)
    {
        for (var i = 0; i < frames; i++)
        {
            var buffer = await store.RentAsync(Token);
            if (buffer is null)
            {
                return;
            }

            Array.Fill(buffer, (byte)((pass * 10) + i));
            store.Commit(buffer, new FrameTiming(Step * i, Step));
        }
    }

    private static async Task<FrameLookup> WaitForAsync(FrameStore store, TimeSpan position)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            if (store.Resolve(position) is { } lookup && lookup.Frame.Pts == position)
            {
                return lookup;
            }

            Assert.True(DateTime.UtcNow < deadline, $"等待 {position} 处的帧超时");
            await Task.Delay(1, Token);
        }
    }

    private sealed class CountingPool : ArrayPool<byte>
    {
        public int Rented { get; private set; }

        public int Returned { get; private set; }

        public override byte[] Rent(int minimumLength)
        {
            Rented++;
            return new byte[minimumLength];
        }

        public override void Return(byte[] array, bool clearArray = false) => Returned++;
    }
}
