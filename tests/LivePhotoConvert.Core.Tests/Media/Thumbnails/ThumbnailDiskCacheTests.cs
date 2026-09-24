using LivePhotoConvert.Core.Media.Thumbnails;

namespace LivePhotoConvert.Core.Tests.Media.Thumbnails;

public class ThumbnailDiskCacheTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    private static ThumbnailKey Key(int index, int tier = 256) =>
        ThumbnailKey.Create($"/album/IMG_{index:D4}.jpg", 1000 + index, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), tier);

    private static byte[] Payload(int length, byte fill) => Enumerable.Repeat(fill, length).ToArray();

    [Fact]
    public void TryPut_ThenTryGet_ReturnsWrittenFile()
    {
        using var dir = new TempDirectory();
        var cache = new ThumbnailDiskCache(dir.Root, 1_000_000);
        var key = Key(1);

        Assert.False(cache.TryGet(key, out _));
        Assert.True(cache.TryPut(key, Payload(100, 7)));

        Assert.True(cache.TryGet(key, out var path));
        Assert.Equal(cache.GetPath(key), path);
        Assert.Equal(Payload(100, 7), File.ReadAllBytes(path));
        Assert.Equal(key.Hash[..2], Path.GetFileName(Path.GetDirectoryName(path)));
    }

    [Fact]
    public async Task TryPut_ConcurrentSameKey_NoExceptionAndNoLeftovers()
    {
        using var dir = new TempDirectory();
        var cache = new ThumbnailDiskCache(dir.Root, 100_000_000);
        var key = Key(2);
        var payloads = Enumerable.Range(0, 32).Select(i => Payload(4096 + i, (byte)i)).ToArray();

        var results = await Task.WhenAll(payloads.Select(p => Task.Run(() => cache.TryPut(key, p))));

        Assert.All(results, Assert.True);
        var files = Directory.GetFiles(dir.Root, "*", SearchOption.AllDirectories);
        var single = Assert.Single(files);
        Assert.Equal(cache.GetPath(key), single);
        Assert.Contains(payloads, p => p.AsSpan().SequenceEqual(File.ReadAllBytes(single)));
    }

    [Fact]
    public void Trim_OverCapacity_DeletesLeastRecentlyUsedDownTo80Percent()
    {
        using var dir = new TempDirectory();
        var time = new ManualTimeProvider(Now);
        var cache = new ThumbnailDiskCache(dir.Root, 1_000_000, time);
        var keys = Enumerable.Range(0, 10).Select(i => Key(i)).ToArray();
        for (var i = 0; i < keys.Length; i++)
        {
            Assert.True(cache.TryPut(keys[i], Payload(1000, (byte)i)));
            // 下标越小访问越早
            File.SetLastWriteTimeUtc(cache.GetPath(keys[i]), Now.UtcDateTime.AddHours(-100 + i));
        }

        cache.CapacityBytes = 5000;
        var result = cache.Trim();

        Assert.Equal(10_000, result.BytesBefore);
        Assert.Equal(4000, result.BytesAfter);
        Assert.Equal(6, result.FilesDeleted);
        Assert.Equal(4000, cache.ApproximateSizeBytes);
        for (var i = 0; i < keys.Length; i++)
        {
            Assert.Equal(i >= 6, File.Exists(cache.GetPath(keys[i])));
        }
    }

    [Fact]
    public void TryGet_RefreshesAccessTimeOnlyAfterTouchInterval()
    {
        using var dir = new TempDirectory();
        var time = new ManualTimeProvider(Now);
        var cache = new ThumbnailDiskCache(dir.Root, 1_000_000, time) { TouchInterval = TimeSpan.FromHours(1) };
        var key = Key(3);
        cache.TryPut(key, Payload(10, 1));
        var path = cache.GetPath(key);

        var recent = Now.UtcDateTime.AddMinutes(-10);
        File.SetLastWriteTimeUtc(path, recent);
        Assert.True(cache.TryGet(key, out _));
        Assert.Equal(recent, File.GetLastWriteTimeUtc(path));

        File.SetLastWriteTimeUtc(path, Now.UtcDateTime.AddHours(-2));
        Assert.True(cache.TryGet(key, out _));
        Assert.Equal(Now.UtcDateTime, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void TryPut_OverCapacity_TrimsAutomatically()
    {
        using var dir = new TempDirectory();
        var cache = new ThumbnailDiskCache(dir.Root, 5000);

        for (var i = 0; i < 20; i++)
        {
            Assert.True(cache.TryPut(Key(i), Payload(1000, (byte)i)));
        }

        var total = Directory.GetFiles(dir.Root, "*.jpg", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        Assert.True(total <= 5000, $"缓存占用 {total} 超过上限");
        Assert.True(File.Exists(cache.GetPath(Key(19))));
    }

    [Fact]
    public void Trim_RemovesStaleTempFilesButKeepsFreshOnes()
    {
        using var dir = new TempDirectory();
        var time = new ManualTimeProvider(Now);
        var cache = new ThumbnailDiskCache(dir.Root, 1_000_000, time);
        var stale = dir.CreateFile(Path.Combine("ab", "stale.jpg.1.tmp"), [1]);
        var fresh = dir.CreateFile(Path.Combine("ab", "fresh.jpg.2.tmp"), [1]);
        File.SetLastWriteTimeUtc(stale, Now.UtcDateTime.AddHours(-3));
        File.SetLastWriteTimeUtc(fresh, Now.UtcDateTime.AddMinutes(-1));

        cache.Trim();

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        using var dir = new TempDirectory();
        var cache = new ThumbnailDiskCache(dir.Root, 1_000_000);
        cache.TryPut(Key(1), Payload(10, 1));
        cache.TryPut(Key(2, 512), Payload(10, 2));

        cache.Clear();

        Assert.Empty(Directory.GetFiles(dir.Root, "*", SearchOption.AllDirectories));
        Assert.Equal(0, cache.ApproximateSizeBytes);
        Assert.False(cache.TryGet(Key(1), out _));
    }

    [Fact]
    public void Trim_MissingRoot_ReturnsEmpty()
    {
        using var dir = new TempDirectory();
        var cache = new ThumbnailDiskCache(dir.Combine("missing"), 1000);

        Assert.Equal(new ThumbnailCacheTrimResult(0, 0, 0), cache.Trim());
    }
}
