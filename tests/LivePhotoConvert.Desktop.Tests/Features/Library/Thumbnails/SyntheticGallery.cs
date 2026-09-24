using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using LivePhotoConvert.Core.Media;
using LivePhotoConvert.Core.Media.Thumbnails;
using LivePhotoConvert.Desktop.Features.Library.Gallery;
using LivePhotoConvert.Desktop.Features.Library.Thumbnails;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Models;
using LivePhotoConvert.Desktop.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace LivePhotoConvert.Desktop.Tests.Features.Library.Thumbnails;

/// <summary>
/// 大相册画廊的测试宿主：假扫描结果 + 假缩略图（不做真实解码），在真实外壳里滚动，只衡量画廊自身的开销。
/// </summary>
internal static class SyntheticGallery
{
    public static ShellSession Open(SyntheticStore store, int budgetMb, int cardCount, Action<IServiceCollection>? configure = null,
        string theme = ThemeService.Light, string language = "zh", Func<int, string>? name = null)
    {
        var items = Items(cardCount, name);
        var session = new ShellSession(
            language,
            theme,
            configure: services =>
            {
                services.AddSingleton(sp => new LibraryCatalog(
                    sp.GetRequiredService<ILocalizer>(), new NoEnrichment(),
                    (_, _, _) => Task.FromResult(new LibraryScanResult(items, cardCount * 2, 0))));
                services.AddSingleton<IThumbnailPipeline>(sp => new ThumbnailPipeline(
                    store, sp.GetRequiredService<SettingsStore>().Current.Gallery.ThumbnailBudgetBytes, SyntheticStore.Decode));
                configure?.Invoke(services);
            },
            settings: s => s.Gallery.ThumbnailBudgetMb = budgetMb);
        // 接近常见桌面窗口：每屏实例化的卡片更多，钉住字节与驱逐压力更接近实际
        session.Window.Width = 1600;
        session.Window.Height = 1100;
        session.Pump();
        return session;
    }

    public static async Task ScanAsync(ShellSession session, int cardCount)
    {
        var library = session.Shell.Library;
        library.AlbumDirectory = "/album";
        await library.RefreshAlbumAsync();
        await session.WaitUntilAsync(() => library.Layout.DisplayedCards.Count == cardCount, timeoutSeconds: 30);
        session.Pump();
    }

    /// <summary>实况对，横竖混排，每 11 张有一张待裁决；时间递增使分组与排序稳定。</summary>
    public static List<LibraryItem> Items(int count, Func<int, string>? name = null) =>
        [.. Enumerable.Range(0, count).Select(i => Cards.ApplePairItem(
            name?.Invoke(i) ?? $"IMG_{i:D5}",
            requiresReview: i % 11 == 4,
            aspect: (i % 7) switch { 0 => 0.75, 3 => 16.0 / 9.0, 5 => 1.0, _ => 4.0 / 3.0 },
            taken: new DateTime(2025, 1, 1).AddMinutes(i * 7)))];

    public static List<PhotoCardItemViewModel> Attached(ListBox list) =>
        [.. list.GetRealizedContainers().Select(list.ItemFromContainer).OfType<PhotoGridRowViewModel>().SelectMany(r => r.Cards)];

    /// <summary>
    /// 让新位置的行实例化并等缩略图到齐。每张缩略图都要回到界面线程交付后 worker 才继续，
    /// 这里只推进调度器、按需渲染一帧：整窗渲染的代价远大于交付本身。
    /// </summary>
    public static async Task PumpUntilLoadedAsync(ShellSession session, ListBox list, bool render = true)
    {
        Dispatcher.UIThread.RunJobs();
        session.Window.UpdateLayout();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        // 重排瞬间可能没有已实例化的行，空集合不算加载完成
        while (Attached(list) is not { Count: > 0 } cards || !cards.All(c => c.DisplayImage is not null))
        {
            Assert.True(DateTime.UtcNow < deadline, "等待缩略图超时");
            await Task.Delay(1, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
        }

        if (render)
        {
            session.Pump();
        }
    }

    private sealed class NoEnrichment : ILibraryEnricher
    {
        public async IAsyncEnumerable<LibraryItem> EnrichAsync(IReadOnlyList<LibraryItem> items, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}

/// <summary>三分之二命中“磁盘缓存”（共用一个占位文件），其余走生成通道；解码直接按请求高度造 4:3 位图。</summary>
internal sealed class SyntheticStore : IThumbnailStore, IDisposable
{
    private readonly string _cacheFile = Path.Combine(Path.GetTempPath(), $"lpc_stress_thumb_{Guid.NewGuid():N}.jpg");
    private int _requests;

    public SyntheticStore() => File.WriteAllText(_cacheFile, "cache");

    public int Requests => Volatile.Read(ref _requests);

    public static long BytesAt(int height) => (long)Width(height) * height * 4;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte[]> Pixels = new();

    /// <summary>
    /// 按请求高度造一张渐变位图。用不可变位图（与真实解码结果同类）：可写位图每次绘制都要重新取快照、重建缩放层级，
    /// 会让渲染开销远离实际。
    /// </summary>
    public static Bitmap Decode(Stream stream, int heightPx)
    {
        var size = new PixelSize(Width(heightPx), heightPx);
        var pixels = Pixels.GetOrAdd(heightPx, _ => Gradient(size));
        unsafe
        {
            fixed (byte* data = pixels)
            {
                return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, (nint)data, size, new Vector(96, 96), size.Width * 4);
            }
        }
    }

    private static byte[] Gradient(PixelSize size)
    {
        var pixels = new byte[size.Width * size.Height * 4];
        for (var y = 0; y < size.Height; y++)
        {
            for (var x = 0; x < size.Width; x++)
            {
                var i = (y * size.Width + x) * 4;
                pixels[i] = (byte)(160 + 80 * x / size.Width);
                pixels[i + 1] = (byte)(120 + 100 * y / size.Height);
                pixels[i + 2] = 90;
                pixels[i + 3] = 255;
            }
        }

        return pixels;
    }

    public static bool IsDisposed(Bitmap bitmap)
    {
        try
        {
            _ = bitmap.PixelSize;
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    public ThumbnailResult? TryGetCached(ThumbnailRequest request)
    {
        Interlocked.Increment(ref _requests);
        return request.Path[^6] % 3 == 0 ? null : new ThumbnailResult(ThumbnailOrigin.Cache, _cacheFile, null);
    }

    public Task<ThumbnailResult?> GetAsync(ThumbnailRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<ThumbnailResult?>(new ThumbnailResult(ThumbnailOrigin.Decoded, null, Encoding.UTF8.GetBytes("generated")));

    public void Dispose() => File.Delete(_cacheFile);

    private static int Width(int height) => (int)Math.Round(height * 4.0 / 3.0);
}
