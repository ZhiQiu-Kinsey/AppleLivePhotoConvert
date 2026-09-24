using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using LivePhotoConvert.Core.Media;
using LivePhotoConvert.Core.Services;
using LivePhotoConvert.Desktop.Features.Tasks;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Models;

namespace LivePhotoConvert.Desktop.Features.Library.Gallery;

/// <summary>扫描入口；默认为 <see cref="LibraryScanner.ScanAsync"/>，测试可替换为可计数或抛错的实现。</summary>
public delegate Task<LibraryScanResult> LibraryScanFunc(string root, IProgress<LibraryScanProgress>? progress, CancellationToken cancellationToken);

/// <summary>扫描后的后台补全：逐条返回升级后的条目。</summary>
public interface ILibraryEnricher
{
    IAsyncEnumerable<LibraryItem> EnrichAsync(IReadOnlyList<LibraryItem> items, CancellationToken cancellationToken);
}

/// <summary>有 ExifTool 时读取 XMP，把视频直接追加在尾部的 HEIC 升级为动态照片；没有时什么也不做。</summary>
public sealed class ExifToolLibraryEnricher(SettingsStore settings, IToolAvailability tools, IConversionEngines engines) : ILibraryEnricher
{
    public async IAsyncEnumerable<LibraryItem> EnrichAsync(IReadOnlyList<LibraryItem> items, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        List<LibraryItem> stills = [.. items.Where(i => i.Kind == LibraryItemKind.Still && MediaFileTypes.IsHeic(i.Photo.Path))];
        if (stills.Count == 0)
        {
            yield break;
        }

        var paths = ToolPaths.From(settings.Current);
        // 探测与创建会查找并启动外部进程，之后都留在线程池上
        if (!await Task.Run(() => tools.IsAvailable(RequiredTool.ExifTool, paths), cancellationToken).ConfigureAwait(false))
        {
            yield break;
        }

        await using var metadata = engines.CreateMetadata(paths, 1);
        await foreach (var item in LibraryScanner.EnrichHeicAsync(stills, metadata, cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }
    }
}

public enum LibraryScanError
{
    None,
    NotFound,
    AccessDenied,
    Failed
}

/// <summary>
/// 图库条目的唯一来源：在界面线程之外扫描目录，一次产出全部类型的卡片；切换动作只筛选，不重新扫描。
/// 新的扫描使旧扫描与其后台补全作废（纪元号），扫描异常转为错误状态而不是抛给界面。
/// </summary>
public sealed partial class LibraryCatalog : ObservableObject
{
    private readonly ILocalizer _localizer;
    private readonly ILibraryEnricher _enricher;
    private readonly LibraryScanFunc _scan;
    private CancellationTokenSource? _cts;
    private int _epoch;
    private Dictionary<string, PhotoCardItemViewModel> _byPath = [];

    public LibraryCatalog(ILocalizer localizer, ILibraryEnricher enricher, LibraryScanFunc? scan = null)
    {
        _localizer = localizer;
        _enricher = enricher;
        _scan = scan ?? LibraryScanner.ScanAsync;
    }

    /// <summary>当前目录的全部卡片（含普通照片），按照片路径排序。</summary>
    public IReadOnlyList<PhotoCardItemViewModel> Cards { get; private set; } = [];

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(HasStatus))]
    private LibraryScanError _error;

    /// <summary>错误的补充说明：目录路径或异常说明。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(HasStatus))]
    private string _errorDetail = string.Empty;

    /// <summary>枚举到的文件总数。</summary>
    [ObservableProperty]
    private int _totalFiles;

    /// <summary>因权限等原因跳过的子目录数。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(HasStatus))]
    private int _inaccessibleEntries;

    /// <summary>扫描错误或跳过了无权限子目录时的说明（界面语言）；正常时为空。</summary>
    public string StatusText => Error switch
    {
        LibraryScanError.NotFound => _localizer.Format("ScanErrorNotFoundFormat", ErrorDetail),
        LibraryScanError.AccessDenied => _localizer.Format("ScanErrorAccessDeniedFormat", ErrorDetail),
        LibraryScanError.Failed => _localizer.Format("ScanErrorFailedFormat", ErrorDetail),
        _ when InaccessibleEntries > 0 => _localizer.Format("ScanSkippedInaccessibleFormat", InaccessibleEntries),
        _ => string.Empty
    };

    public bool HasStatus => StatusText.Length > 0;

    /// <summary>语言切换后重新格式化说明文案。</summary>
    public void RefreshTexts() => OnPropertyChanged(nameof(StatusText));

    /// <summary>实际读盘扫描的次数。</summary>
    public int ScanCount { get; private set; }

    /// <summary>当前扫描的后台补全；测试据此等待补全结束。</summary>
    public Task EnrichmentTask { get; private set; } = Task.CompletedTask;

    /// <summary>卡片集合整体更换（重扫、清空）。</summary>
    public event EventHandler? CardsReplaced;

    /// <summary>后台补全升级了一张卡片的条目（例如普通 HEIC 识别为动态照片）。</summary>
    public event EventHandler<PhotoCardItemViewModel>? CardUpgraded;

    /// <summary>扫描目录并替换卡片；目录为空白时清空。被更新的扫描取代时静默返回。</summary>
    public async Task ScanAsync(string? directory)
    {
        var epoch = ++_epoch;
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            _cts.Dispose();
        }

        var cts = new CancellationTokenSource();
        _cts = cts;
        if (string.IsNullOrWhiteSpace(directory))
        {
            IsScanning = false;
            Replace([], 0, 0, LibraryScanError.None, string.Empty);
            return;
        }

        IsScanning = true;
        ScanCount++;
        try
        {
            var result = await _scan(directory, null, cts.Token);
            var localizer = _localizer;
            // 上万张卡片的构造也放在后台
            List<PhotoCardItemViewModel> cards = await Task.Run(() => result.Items.Select(item => new PhotoCardItemViewModel(item, localizer)).ToList(), cts.Token);
            if (epoch != _epoch)
            {
                return;
            }

            Replace(cards, result.TotalFiles, result.InaccessibleEntries, LibraryScanError.None, string.Empty);
            EnrichmentTask = EnrichAsync([.. result.Items], epoch, cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // 被更新的扫描取代
        }
        catch (Exception ex)
        {
            if (epoch == _epoch)
            {
                var error = ex switch
                {
                    DirectoryNotFoundException => LibraryScanError.NotFound,
                    UnauthorizedAccessException => LibraryScanError.AccessDenied,
                    _ => LibraryScanError.Failed
                };
                if (error == LibraryScanError.Failed)
                {
                    ErrorLogger.Log(ex, "扫描相册");
                }

                Replace([], 0, 0, error, error == LibraryScanError.Failed ? ErrorMessages.Describe(_localizer, ex) : directory);
            }
        }
        finally
        {
            if (epoch == _epoch)
            {
                IsScanning = false;
            }
        }
    }

    private void Replace(List<PhotoCardItemViewModel> cards, int totalFiles, int inaccessible, LibraryScanError error, string detail)
    {
        Cards = cards;
        _byPath = cards.ToDictionary(card => card.PhotoPath, StringComparer.Ordinal);
        TotalFiles = totalFiles;
        InaccessibleEntries = inaccessible;
        Error = error;
        ErrorDetail = detail;
        OnPropertyChanged(nameof(Cards));
        CardsReplaced?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>在界面线程上逐条应用补全结果；补全失败只记录日志，不影响已显示的卡片。</summary>
    private async Task EnrichAsync(IReadOnlyList<LibraryItem> items, int epoch, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var upgraded in _enricher.EnrichAsync(items, cancellationToken))
            {
                if (epoch != _epoch)
                {
                    return;
                }

                if (_byPath.TryGetValue(upgraded.Photo.Path, out var card))
                {
                    card.Item = upgraded;
                    CardUpgraded?.Invoke(this, card);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 被新的扫描取代
        }
        catch (Exception ex)
        {
            ErrorLogger.Log(ex, "补全 HEIC 动态照片");
        }
    }
}
