using System.Runtime.CompilerServices;
using LivePhotoConvert.Core.Media;
using LivePhotoConvert.Core.Tests.Support;
using LivePhotoConvert.Desktop.Features.Library.Gallery;
using LivePhotoConvert.Desktop.Tests.Harness;

namespace LivePhotoConvert.Desktop.Tests.Features.Library.Gallery;

public class LibraryCatalogTests
{
    [Fact]
    public async Task Scan_ProducesOneCardPerPhotoOfEveryKind()
    {
        using var album = new TestSandbox();
        album.CreateInputFile("IMG_0001.heic", new byte[300]);
        album.CreateInputFile("IMG_0001.mov", new byte[700]);
        album.CreateInputFile("IMG_0001.mp4", new byte[900]);
        album.CreateInputFile("MVIMG_0002.jpg", SyntheticMedia.MotionPhoto());
        album.CreateInputFile("IMG_0003.jpg", SyntheticMedia.Jpeg());
        var catalog = new LibraryCatalog(Cards.Localizer, NoEnrichment.Instance);
        var replaced = 0;
        catalog.CardsReplaced += (_, _) => replaced++;

        await catalog.ScanAsync(album.InputDirectory);

        Assert.Equal(1, replaced);
        Assert.Equal(1, catalog.ScanCount);
        // 按照片路径排序
        Assert.Equal([LibraryItemKind.ApplePair, LibraryItemKind.Still, LibraryItemKind.MotionPhoto], catalog.Cards.Select(c => c.Kind));
        Assert.Equal(2, catalog.Cards[0].Item.PairCandidates.Count);
        Assert.Equal(5, catalog.TotalFiles);
        Assert.False(catalog.IsScanning);
        Assert.Equal(LibraryScanError.None, catalog.Error);
        Assert.False(catalog.HasStatus);
    }

    [Theory]
    [InlineData(typeof(UnauthorizedAccessException), LibraryScanError.AccessDenied, "ScanErrorAccessDeniedFormat")]
    [InlineData(typeof(DirectoryNotFoundException), LibraryScanError.NotFound, "ScanErrorNotFoundFormat")]
    [InlineData(typeof(IOException), LibraryScanError.Failed, "ScanErrorFailedFormat")]
    public async Task ScanFailure_BecomesErrorStateInsteadOfThrowing(Type exceptionType, LibraryScanError expected, string key)
    {
        var catalog = new LibraryCatalog(Cards.Localizer, NoEnrichment.Instance,
            (_, _, _) => Task.FromException<LibraryScanResult>((Exception)Activator.CreateInstance(exceptionType, "denied")!));

        await catalog.ScanAsync("/locked");

        Assert.Equal(expected, catalog.Error);
        Assert.Empty(catalog.Cards);
        Assert.False(catalog.IsScanning);
        Assert.True(catalog.HasStatus);
        Assert.Equal(Cards.Localizer.Format(key, expected == LibraryScanError.Failed ? "denied" : "/locked"), catalog.StatusText);
    }

    [Fact]
    public async Task InaccessibleSubfolders_AreReportedAsANotice()
    {
        var catalog = new LibraryCatalog(Cards.Localizer, NoEnrichment.Instance,
            (_, _, _) => Task.FromResult(new LibraryScanResult([Cards.ApplePairItem("IMG_1")], 2, InaccessibleEntries: 3)));

        await catalog.ScanAsync("/album");

        Assert.Equal(LibraryScanError.None, catalog.Error);
        Assert.Single(catalog.Cards);
        Assert.Equal(Cards.Localizer.Format("ScanSkippedInaccessibleFormat", 3), catalog.StatusText);
    }

    /// <summary>真实无权限子目录（root 不受目录权限限制时跳过）。</summary>
    [Fact]
    public async Task RealLockedSubfolder_DoesNotCrashAndIsCounted()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows 上以 ACL 控制权限，本用例只覆盖 Unix 权限位。");
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var album = new TestSandbox();
        album.CreateInputFile("ok/IMG_0001.jpg", SyntheticMedia.Jpeg());
        album.CreateInputFile("locked/IMG_0002.jpg", SyntheticMedia.Jpeg());
        var locked = Path.Combine(album.InputDirectory, "locked");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            try
            {
                _ = Directory.GetFiles(locked);
                Assert.Skip("当前用户（如 root）不受目录权限限制。");
            }
            catch (UnauthorizedAccessException)
            {
                // 权限生效
            }

            var catalog = new LibraryCatalog(Cards.Localizer, NoEnrichment.Instance);
            await catalog.ScanAsync(album.InputDirectory);

            Assert.Single(catalog.Cards);
            Assert.Equal(1, catalog.InaccessibleEntries);
            Assert.True(catalog.HasStatus);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task NewerScan_SupersedesTheOlderOne()
    {
        var gate = new TaskCompletionSource<LibraryScanResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var catalog = new LibraryCatalog(Cards.Localizer, NoEnrichment.Instance, (root, _, token) => ++calls == 1
            ? gate.Task.WaitAsync(token)
            : Task.FromResult(new LibraryScanResult([Cards.ApplePairItem("NEW")], 2, 0)));

        var first = catalog.ScanAsync("/old");
        var second = catalog.ScanAsync("/new");
        gate.SetResult(new LibraryScanResult([Cards.ApplePairItem("OLD")], 2, 0));
        await Task.WhenAll(first, second);

        Assert.Equal("NEW", Assert.Single(catalog.Cards).FileName);
        Assert.False(catalog.IsScanning);
        Assert.Equal(2, catalog.ScanCount);
    }

    [Fact]
    public async Task Enrichment_UpgradesCardsInPlace()
    {
        var still = new LibraryItem(LibraryItemKind.Still, Cards.File("/album/IMG_9.heic", 9000));
        var upgraded = still with { Kind = LibraryItemKind.MotionPhoto, Embedded = new EmbeddedVideo(5000, 4000, 5000) };
        var enricher = new ScriptedEnrichment(upgraded);
        var catalog = new LibraryCatalog(Cards.Localizer, enricher, (_, _, _) => Task.FromResult(new LibraryScanResult([still], 1, 0)));
        var upgrades = new List<string>();
        catalog.CardUpgraded += (_, card) => upgrades.Add(card.FileName);

        await catalog.ScanAsync("/album");
        var card = Assert.Single(catalog.Cards);
        Assert.False(card.IsMotionPhoto);

        enricher.Release();
        await catalog.EnrichmentTask;

        Assert.Same(card, Assert.Single(catalog.Cards));
        Assert.True(card.IsMotionPhoto);
        Assert.Equal(new VideoSource("/album/IMG_9.heic", 5000, 4000, IsEmbedded: true), card.Video);
        Assert.Equal(["IMG_9"], upgrades);
    }

    private sealed class NoEnrichment : ILibraryEnricher
    {
        public static NoEnrichment Instance { get; } = new();

        public async IAsyncEnumerable<LibraryItem> EnrichAsync(IReadOnlyList<LibraryItem> items, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class ScriptedEnrichment(params LibraryItem[] results) : ILibraryEnricher
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _gate.TrySetResult();

        public async IAsyncEnumerable<LibraryItem> EnrichAsync(IReadOnlyList<LibraryItem> items, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await _gate.Task.WaitAsync(cancellationToken);
            foreach (var result in results)
            {
                yield return result;
            }
        }
    }
}
