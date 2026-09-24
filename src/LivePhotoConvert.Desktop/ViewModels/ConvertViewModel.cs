using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Media.Imaging;
using LivePhotoConvert.Core.Abstractions;
using LivePhotoConvert.Core.External;
using LivePhotoConvert.Core.Metadata;
using LivePhotoConvert.Core.Pairing;
using LivePhotoConvert.Core.Pipeline;
using LivePhotoConvert.Core.Services;
using LivePhotoConvert.Desktop.Collections;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Models;
using LivePhotoConvert.Desktop.Services;
using LivePhotoConvert.Desktop.ViewModels.Dialogs;

namespace LivePhotoConvert.Desktop.ViewModels;

public sealed partial class ConvertViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;
    private readonly ILocalizer _localizer;
    private List<TimelineGroup> _groups = [];
    private List<PhotoCardItemViewModel> _allCards = [];
    private readonly HashSet<string> _collapsedGroupKeys = [];
    private CancellationTokenSource? _batchCts;
    private readonly ThumbnailReader _thumbnailReader = new();
    private readonly LruThumbnailManager _lruThumbnailManager;
    private CancellationTokenSource? _thumbnailCts;
    private readonly Lock _thumbnailQueueLock = new();
    private readonly LinkedList<PhotoCardItemViewModel> _highPriorityThumbnailQueue = new();
    private readonly HashSet<PhotoCardItemViewModel> _highPrioritySet = new();
    private readonly Queue<PhotoCardItemViewModel> _backgroundThumbnailQueue = new();
    private readonly HashSet<PhotoCardItemViewModel> _processingCards = new();
    private SemaphoreSlim? _thumbnailWorkSignal;

    // 扫描生命周期与方向扫描结果高速缓存
    private readonly Dictionary<int, AlbumScanner.ScanResult> _dirScanCache = new();
    private string _lastScannedDirectory = string.Empty;
    private CancellationTokenSource? _scanCts;

    [ObservableProperty]
    private bool _isScanning;

    // 弹窗请求回调
    public Action<ViewModelBase>? OnShowModal { get; init; }
    public Action? OnCloseModal { get; init; }

    /// <summary>批次转换完成后回调真实报告明细与汇总徐章（由 MainWindowViewModel 接线到批次报告页）。</summary>
    public Action<BatchReportModel>? OnBatchReportReady { get; set; }

    [ObservableProperty]
    private int _conversionDirection; // 0=苹果转安卓, 1=安卓转苹果, 2=提取独立封面与视频

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FunnelTotalText))]
    [NotifyPropertyChangedFor(nameof(HasScannedFiles))]
    private int _totalScannedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalLiveReadyCount))]
    [NotifyPropertyChangedFor(nameof(FunnelReadyText))]
    [NotifyPropertyChangedFor(nameof(CanStartConvert))]
    [NotifyPropertyChangedFor(nameof(FilterAllText))]
    [NotifyPropertyChangedFor(nameof(HasReadyItems))]
    private int _readyCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FunnelSuspiciousText))]
    [NotifyPropertyChangedFor(nameof(FilterAllText))]
    [NotifyPropertyChangedFor(nameof(FilterSuspiciousText))]
    [NotifyPropertyChangedFor(nameof(HasSuspiciousItems))]
    private int _suspiciousCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FunnelFilteredText))]
    [NotifyPropertyChangedFor(nameof(HasFilteredItems))]
    private int _filteredCount;

    public bool HasReadyItems => ReadyCount > 0;
    public bool HasSuspiciousItems => SuspiciousCount > 0;
    public bool HasFilteredItems => FilteredCount > 0;
    public bool HasScannedFiles => TotalScannedCount > 0;

    public int TotalLiveReadyCount => ReadyCount;
    public bool CanStartConvert => ReadyCount > 0 && !IsRunning;

    [ObservableProperty]
    private string _selectedSummaryText = string.Empty;

    [ObservableProperty]
    private string _selectedBadgeText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AlbumDirectorySummary))]
    [NotifyPropertyChangedFor(nameof(HasSelectedDirectory))]
    [NotifyPropertyChangedFor(nameof(EmptyStateTitle))]
    [NotifyPropertyChangedFor(nameof(EmptyStateSubtitle))]
    private string _albumDirectory = string.Empty;

    partial void OnAlbumDirectoryChanged(string? oldValue, string newValue)
    {
        if (!string.Equals(oldValue, newValue, StringComparison.OrdinalIgnoreCase))
        {
            _dirScanCache.Clear();
        }
    }

    public bool HasSelectedDirectory => !string.IsNullOrWhiteSpace(AlbumDirectory) && Directory.Exists(AlbumDirectory);
    public string AlbumDirectorySummary => string.IsNullOrWhiteSpace(AlbumDirectory)
        ? _localizer["AlbumSummaryNoDir"]
        : Path.GetFileName(AlbumDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    public bool HasPhotos => _allCards.Count > 0;

    public string EmptyStateTitle => _localizer[
        !HasSelectedDirectory ? "EmptyStateNoDirTitle" : "EmptyStateEmptyTitle"];

    public string EmptyStateSubtitle => _localizer[
        !HasSelectedDirectory ? "EmptyStateNoDirSubtitle" : "EmptyStateEmptySubtitle"];

    public string SelectAlbumFolderBtnText => _localizer["SelectAlbumFolderBtn"];

    public string FunnelTotalText => _localizer.Format("FunnelTotalFormat", TotalScannedCount);
    public string FunnelReadyText => _localizer.Format("FunnelReadyFormat", ReadyCount);
    public string FunnelSuspiciousText => _localizer.Format("FunnelSuspiciousFormat", SuspiciousCount);
    public string FunnelFilteredText => _localizer.Format("FunnelFilteredFormat", FilteredCount);

    // 筛选菜单计数：全部照片 = 就绪配对 + 待裁决配对；待裁决 = 需人工核验的配对数
    public string FilterAllText => _localizer.Format("FilterAllFormat", ReadyCount + SuspiciousCount);
    public string FilterSuspiciousText => _localizer.Format("FilterSuspiciousFormat", SuspiciousCount);

    /// <summary>选择按钮文字：多选态显示"完成选择"，否则显示"选择"（遵循 PRD 3.2 模式切换规范）。</summary>
    public string SelectionModeButtonText => _localizer[
        IsSelectionModeActive ? "ToolbarSelectActive" : "ToolbarSelect"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionModeButtonText))]
    private bool _isSelectionModeActive;

    [ObservableProperty]
    private string _sortMode = "DateTaken"; // DateTaken, DateCreated, DateModified, Name

    [ObservableProperty]
    private bool _isSortAscending;

    [ObservableProperty]
    private string _filterMode = "All"; // All, SuspiciousOnly, SelectedOnly

    [ObservableProperty]
    private bool _isFilterBannerVisible;

    [ObservableProperty]
    private string _filterBannerDesc = string.Empty;

    [ObservableProperty]
    private string _cropMode = "Natural"; // Natural (等高原始比例), Square (1:1)

    [ObservableProperty]
    private string _scaleMode = "Medium"; // Small (4 cols), Medium (3 cols), Large (2 cols)

    [ObservableProperty]
    private string _groupingMode = "Date"; // Date, Month, Year, None

    [ObservableProperty]
    private int _namingFormat; // 0=保持原名, 1=日期+原名, 2=纯时间戳

    [ObservableProperty]
    private string _liveFilenameDemo = "MVIMG_20260905_142033_IMG_0012.jpg";

    [ObservableProperty]
    private int _sourceAction; // 0=保留原片, 1=已拆分子目录, 2=移入回收站, 3=物理删除

    [ObservableProperty]
    private bool _isDeleteWarningVisible;

    [ObservableProperty]
    private bool _keepSubfolderHierarchy;

    [ObservableProperty]
    private bool _autoAppendIndex;

    [ObservableProperty]
    private int _heicQuality;

    [ObservableProperty]
    private string _outputDirectory;

    [ObservableProperty]
    private string _primaryButtonText = string.Empty;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _currentFileName = string.Empty;

    [ObservableProperty]
    private double _currentProgressPercent;

    [ObservableProperty]
    private string _progressRatioText = string.Empty;

    [ObservableProperty]
    private string _writingThroughputText = string.Empty;

    [ObservableProperty]
    private string _remainingTimeText = string.Empty;

    // 分组时间线展示流
    public ObservableCollection<TimelineGroup> DisplayGroups { get; } = [];
    public BulkObservableCollection<IGalleryDisplayItem> FlattenedDisplayItems { get; } = [];

    /// <summary>兼容旧绑定的参考卡片宽度；实际等高行宽度由每张图片的纵横比决定。</summary>
    [ObservableProperty]
    private double _cardWidth = 260;

    private double _lastParentWidth = 900;
    private readonly Avalonia.Threading.DispatcherTimer _layoutRefreshTimer;
    private readonly Avalonia.Threading.DispatcherTimer _scrollIdleTimer;
    private bool _isUserScrolling;
    private bool _hasPendingRelayout;

    public bool IsUserScrolling => _isUserScrolling;

    public Action? OnBeforeStreamRebuild { get; set; }
    public Action? OnAfterStreamRebuild { get; set; }

    /// <summary>依据当前父容器宽度与缩放模式更新卡片宽度。</summary>
    public void UpdateCardWidth(double parentWidth)
    {
        if (parentWidth > 50)
        {
            if (Math.Abs(_lastParentWidth - parentWidth) < 2) return;
            _lastParentWidth = parentWidth;
        }

        int columns = ScaleMode switch { "Small" => 4, "Large" => 2, _ => 3 };
        if (_lastParentWidth <= 0)
        {
            return;
        }
        // 扣除左右内边距与每列之间 16px 间距后均分
        double available = Math.Max(100, _lastParentWidth - 32);
        CardWidth = Math.Max(120, (available - (columns - 1) * 16) / columns);

        if (_groups.Count > 0)
        {
            RebuildFlattenedStream();
        }
    }

    public Func<Task<string?>>? RequestSelectFolder { get; set; }
    public Func<Task<string?>>? RequestSelectOutputFolder { get; set; }

    public ConvertViewModel(SettingsService settingsService, ILocalizer localizer)
    {
        _settingsService = settingsService;
        _localizer = localizer;
        _lruThumbnailManager = new LruThumbnailManager(_thumbnailReader);
        _layoutRefreshTimer = new Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _layoutRefreshTimer.Tick += (_, _) =>
        {
            _layoutRefreshTimer.Stop();
            if (_groups.Count > 0) RebuildFlattenedStream();
        };

        _scrollIdleTimer = new Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _scrollIdleTimer.Tick += (_, _) =>
        {
            _scrollIdleTimer.Stop();
            _isUserScrolling = false;
            if (_hasPendingRelayout)
            {
                _hasPendingRelayout = false;
                if (_groups.Count > 0)
                {
                    RebuildFlattenedStream();
                }
            }
        };

        PlaybackHost.Instance.OnQuickLookTriggered = OpenQuickLook;
        PlaybackHost.Instance.OnCardFocused = EnsureThumbnailLoaded;

        var s = _settingsService.Current;
        _namingFormat = s.NamingFormat;
        _sourceAction = s.SourceAction;
        _keepSubfolderHierarchy = s.KeepSubfolderHierarchy;
        _autoAppendIndex = !s.OverwriteSameName;
        _heicQuality = s.HeicQuality;
        if (!string.IsNullOrWhiteSpace(s.OutputDirectory))
        {
            _outputDirectory = s.OutputDirectory;
        }
        else
        {
            var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            _outputDirectory = !string.IsNullOrEmpty(pictures)
                ? Path.Combine(pictures, "LivePhotoConverted")
                : Path.Combine(AppContext.BaseDirectory, "output");
        }

        if (!string.IsNullOrWhiteSpace(s.LastScanDirectory) && Directory.Exists(s.LastScanDirectory))
        {
            _albumDirectory = s.LastScanDirectory;
        }

        UpdateNamingDemo();
        UpdateSelectionSummary();
        UpdateDirectionTexts();

        _localizer.LanguageChanged += (_, _) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_groups.Count > 0)
                {
                    // 分组标题含本地化日期格式
                    BuildGroups();
                    RebuildFlattenedStream();
                }
                UpdateSelectionSummary();
                UpdateDirectionTexts();
                OnPropertyChanged(nameof(EmptyStateTitle));
                OnPropertyChanged(nameof(EmptyStateSubtitle));
                OnPropertyChanged(nameof(SelectAlbumFolderBtnText));
                OnPropertyChanged(nameof(AlbumDirectorySummary));
                OnPropertyChanged(nameof(FunnelTotalText));
                OnPropertyChanged(nameof(FunnelReadyText));
                OnPropertyChanged(nameof(FunnelSuspiciousText));
                OnPropertyChanged(nameof(FunnelFilteredText));
                OnPropertyChanged(nameof(FilterAllText));
                OnPropertyChanged(nameof(FilterSuspiciousText));
            });
        };

        // 启动自愈：若已记住上次相册目录，冷启动即真实扫描并渐进加载缩略图
        if (HasSelectedDirectory)
        {
            _ = RefreshAlbumAsync();
        }
    }

    private void WireCardsAndGroups()
    {
        foreach (var grp in _groups)
        {
            grp.Header.OnToggleCollapse = h =>
            {
                if (h.IsCollapsed)
                {
                    _collapsedGroupKeys.Add(h.Key);
                }
                else
                {
                    _collapsedGroupKeys.Remove(h.Key);
                }
                RebuildFlattenedStream();
            };

            grp.Header.OnSelectGroup = h =>
            {
                foreach (var c in grp.AllCards)
                {
                    c.IsSelected = true;
                }
                UpdateSelectionSummary();
            };

            foreach (var card in grp.AllCards)
            {
                card.OnArbitrateRequested = OpenArbitrationDialog;
                card.OnQuickLookRequested = OpenQuickLook;
                card.OnPriorityLoadRequested = PrioritizeThumbnail;
            }
        }
    }

    [RelayCommand]
    public async Task SetDirection(object? dir)
    {
        int newDir = dir switch
        {
            int i => i,
            string s when int.TryParse(s, out int p) => p,
            _ => ConversionDirection
        };

        if (newDir != ConversionDirection)
        {
            ConversionDirection = newDir;
            UpdateDirectionTexts();
            if (!string.IsNullOrWhiteSpace(AlbumDirectory) && Directory.Exists(AlbumDirectory))
            {
                if (_scanCts is not null)
                {
                    await _scanCts.CancelAsync();
                }
                await RefreshAlbumAsync();
            }
        }
    }

    private void UpdateDirectionTexts()
    {
        PrimaryButtonText = ConversionDirection switch
        {
            0 => _localizer.Format("PrimaryBtnMergeFormat", ReadyCount, ReadyCount + SuspiciousCount),
            1 => _localizer.Format("PrimaryBtnAppleFormat", ReadyCount),
            2 => _localizer.Format("PrimaryBtnExtractFormat", ReadyCount),
            _ => _localizer["PrimaryBtnDefault"]
        };
    }

    [RelayCommand]
    public void ToggleSelectionMode()
    {
        IsSelectionModeActive = !IsSelectionModeActive;
        foreach (var grp in _groups)
        {
            foreach (var card in grp.AllCards)
            {
                card.IsSelectionModeActive = IsSelectionModeActive;
            }
        }
    }

    [RelayCommand]
    public void SetSortMode(string mode)
    {
        SortMode = mode;
        BuildGroups();
        RebuildFlattenedStream();
    }

    [RelayCommand]
    public void SetSortDirection(object? ascending)
    {
        IsSortAscending = ascending switch
        {
            bool b => b,
            string s => bool.TryParse(s, out _) || s == "1",
            _ => IsSortAscending
        };
        BuildGroups();
        RebuildFlattenedStream();
    }

    [RelayCommand]
    public void SetFilterMode(string mode)
    {
        FilterMode = mode;
        IsFilterBannerVisible = mode != "All";
        FilterBannerDesc = mode switch
        {
            "SuspiciousOnly" => _localizer.Format("FilterBannerSuspiciousFormat", SuspiciousCount),
            "SelectedOnly" => _localizer["FilterBannerSelected"],
            _ => string.Empty
        };

        foreach (var card in _groups.SelectMany(grp => grp.AllCards))
        {
            card.IsVisible = mode switch
            {
                "SuspiciousOnly" => card is { HasSuspiciousWarning: true, IsForceAccepted: false },
                "SelectedOnly" => card.IsSelected,
                _ => true
            };
        }

        RebuildFlattenedStream();
    }

    [RelayCommand]
    public void SetCropMode(string mode)
    {
        CropMode = mode;
        foreach (var grp in _groups)
        {
            foreach (var card in grp.AllCards)
            {
                card.CropMode = mode;
            }
        }
        RebuildFlattenedStream();
    }

    /// <summary>缩略图提供真实尺寸后批量刷新等高行，避免每张图片触发一次列表重建。</summary>
    private void ScheduleGalleryRelayout()
    {
        if (_groups.Count == 0) return;

        // 用户正在滚动或拖动滑块时，绝不在此期间重建列表，仅标记挂起待静止后处理
        if (_isUserScrolling)
        {
            _hasPendingRelayout = true;
            return;
        }

        _layoutRefreshTimer.Stop();
        _layoutRefreshTimer.Start();
    }

    private void UpdateCardAspect(PhotoCardItemViewModel card, int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        double ratio = (double)width / height;
        if (Math.Abs(card.AspectRatio - ratio) < 0.01) return;
        card.AspectRatio = ratio;
        // 如果卡片已有测量行高，立即原地更新显示宽度，UI 绑定直接平滑生效
        if (card.PreviewHeight > 0)
        {
            card.DisplayWidth = card.PreviewHeight * ratio + 8;
        }
        ScheduleGalleryRelayout();
    }

    private void EnsureThumbnailLoaded(PhotoCardItemViewModel card)
    {
        var size = _lruThumbnailManager.EnsureThumbnailLoaded(card);
        if (size.HasValue)
        {
            UpdateCardAspect(card, size.Value.Width, size.Value.Height);
        }
    }

    [RelayCommand]
    public void SetScaleMode(string scale)
    {
        ScaleMode = scale;
        UpdateCardWidth(_lastParentWidth);
        RebuildFlattenedStream();
    }

    [RelayCommand]
    public void SetGroupingMode(string grouping)
    {
        GroupingMode = grouping;
        BuildGroups();
        RebuildFlattenedStream();
    }

    [RelayCommand]
    public void SelectAllVisible(object? select)
    {
        bool sel = select switch
        {
            bool b => b,
            string s => bool.TryParse(s, out _) || s == "1",
            _ => true
        };
        foreach (var grp in _groups)
        {
            foreach (var card in grp.AllCards)
            {
                if (card.IsVisible)
                {
                    card.IsSelected = sel;
                }
            }
        }
        UpdateSelectionSummary();
    }

    [RelayCommand]
    public void ToggleAllCollapse()
    {
        bool anyExpanded = _groups.Any(g => !g.Header.IsCollapsed);
        foreach (var g in _groups)
        {
            g.Header.IsCollapsed = anyExpanded;
            if (anyExpanded)
            {
                _collapsedGroupKeys.Add(g.Header.Key);
            }
            else
            {
                _collapsedGroupKeys.Remove(g.Header.Key);
            }
        }
        RebuildFlattenedStream();
    }

    [RelayCommand]
    public async Task SelectAlbumFolderAsync()
    {
        if (RequestSelectFolder is null)
        {
            return;
        }

        string? folder = await RequestSelectFolder();
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        _dirScanCache.Clear();
        AlbumDirectory = folder;
        var s = _settingsService.Current;
        s.LastScanDirectory = folder;
        _settingsService.Save(s);

        await RefreshAlbumAsync();
    }

    /// <summary>
    /// 强制清空方向扫描缓存并全量重新读盘扫描相册
    /// </summary>
    [RelayCommand]
    public async Task RescanAlbumAsync()
    {
        _dirScanCache.Clear();
        await RefreshAlbumAsync();
    }

    [RelayCommand]
    public async Task SelectOutputFolderAsync()
    {
        var picker = RequestSelectOutputFolder ?? RequestSelectFolder;
        if (picker is null)
        {
            return;
        }

        string? folder = await picker();
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        OutputDirectory = folder;
        var s = _settingsService.Current;
        s.OutputDirectory = folder;
        _settingsService.Save(s);
    }

    [RelayCommand]
    public void OpenOutputFolder()
    {
        if (!string.IsNullOrWhiteSpace(OutputDirectory))
        {
            try
            {
                if (!Directory.Exists(OutputDirectory))
                {
                    Directory.CreateDirectory(OutputDirectory);
                }

                using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = OutputDirectory,
                    UseShellExecute = true
                });
            }
            catch
            {
                // ignored
            }
        }
    }

    [RelayCommand]
    public async Task RefreshAlbumAsync()
    {
        string target = AlbumDirectory;

        // 取消正在进行的扫描
        if (_scanCts is not null)
        {
            await _scanCts.CancelAsync();
            _scanCts.Dispose();
            _scanCts = null;
        }

        // 取消正在进行的缩略图加载
        if (_thumbnailCts is not null)
        {
            await _thumbnailCts.CancelAsync();
            _thumbnailCts.Dispose();
            _thumbnailCts = null;
        }

        if (string.IsNullOrWhiteSpace(target) || !Directory.Exists(target))
        {
            _dirScanCache.Clear();
            _allCards = [];
            _groups = [];
            TotalScannedCount = 0;
            ReadyCount = 0;
            SuspiciousCount = 0;
            FilteredCount = 0;
            IsScanning = false;
            OnPropertyChanged(nameof(HasPhotos));
            RebuildFlattenedStream();
            UpdateSelectionSummary();
            return;
        }

        // 仅当相册目录路径变更时才清空该缓存
        if (!string.Equals(target, _lastScannedDirectory, StringComparison.OrdinalIgnoreCase))
        {
            _dirScanCache.Clear();
            _lastScannedDirectory = target;
        }

        int direction = ConversionDirection;
        if (_dirScanCache.TryGetValue(direction, out var cachedResult))
        {
            IsScanning = false;
            ApplyScanResult(cachedResult);
            return;
        }

        var cts = new CancellationTokenSource();
        _scanCts = cts;
        CancellationToken token = cts.Token;
        IsScanning = true;

        try
        {
            var res = await AlbumScanner.ScanDirectoryAsync(_localizer, target, direction, token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            _dirScanCache[direction] = res;
            ApplyScanResult(res);
        }
        catch (OperationCanceledException)
        {
            // 正常取消
        }
        finally
        {
            if (_scanCts == cts)
            {
                _scanCts.Dispose();
                _scanCts = null;
                IsScanning = false;
            }
        }
    }

    private void ApplyScanResult(AlbumScanner.ScanResult res)
    {
        _allCards = res.Groups.SelectMany(g => g.AllCards).ToList();
        TotalScannedCount = res.TotalScannedFiles;
        ReadyCount = res.ReadyPairsCount;
        SuspiciousCount = res.SuspiciousCount;
        FilteredCount = res.FilteredCount;
        OnPropertyChanged(nameof(HasPhotos));

        BuildGroups();
        RebuildFlattenedStream();
        UpdateSelectionSummary();
        UpdateDirectionTexts();
        StartProgressiveThumbnailLoading();

        // 扫描结果可能在视图 Loaded 之后才返回，主动插队首屏卡片，避免必须经过鼠标才出现图片。
        int initialCount = Math.Min(_allCards.Count, 12);
        for (int i = 0; i < initialCount; i++)
        {
            PrioritizeThumbnail(_allCards[i]);
        }
    }

    /// <summary>
    /// 后台渐进式加载缩略图：多 Worker 并发、优先可视区插队解码，并优先读取磁盘缓存。
    /// </summary>
    private void StartProgressiveThumbnailLoading()
    {
        _thumbnailCts?.Cancel();
        _thumbnailCts?.Dispose();
        _thumbnailCts = new CancellationTokenSource();
        CancellationToken token = _thumbnailCts.Token;

        var cards = _groups.SelectMany(g => g.AllCards).ToList();
        if (cards.Count == 0)
        {
            return;
        }

        _lruThumbnailManager.Clear();

        lock (_thumbnailQueueLock)
        {
            _highPriorityThumbnailQueue.Clear();
            _highPrioritySet.Clear();
            _backgroundThumbnailQueue.Clear();
            _processingCards.Clear();
            _thumbnailWorkSignal?.Dispose();
            _thumbnailWorkSignal = new SemaphoreSlim(0);

            // 不再把整个相册压入后台队列。只有视口命中或控件真正挂载时才入队，
            // 避免打开大相册后后台持续解码上千张图片。
        }

        // 首屏极速从磁盘持久化缓存加载（严格在后台 Task.Run 中读取与解码，UI 仅负责接收派发的 Bitmap）
        int initialFastLimit = Math.Min(cards.Count, 16);
        var fastCards = cards.Take(initialFastLimit).ToList();
        _ = Task.Run(() =>
        {
            foreach (var card in fastCards)
            {
                if (token.IsCancellationRequested) break;
                if (card.Thumbnail is not null) continue;

                var fastResult = _thumbnailReader.TryGetFromCacheOnly(card.PhotoPath, LruThumbnailManager.ThumbnailMaxSize);
                if (fastResult is not null && !token.IsCancellationRequested)
                {
                    Bitmap? bmp = null;
                    try
                    {
                        using var ms = new MemoryStream(fastResult.ImageBytes);
                        bmp = new Bitmap(ms);
                    }
                    catch
                    {
                        // 忽略损坏的缓存
                    }

                    if (bmp is not null && !token.IsCancellationRequested)
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            if (token.IsCancellationRequested)
                            {
                                bmp.Dispose();
                                return;
                            }
                            if (string.IsNullOrEmpty(card.ResolutionText))
                            {
                                card.ResolutionText = $"{fastResult.Width}×{fastResult.Height}";
                            }
                            UpdateCardAspect(card, fastResult.Width, fastResult.Height);
                            _lruThumbnailManager.SetThumbnail(card, bmp);
                        });
                    }
                    else
                    {
                        bmp?.Dispose();
                    }
                }
            }
        }, token);

        // 3~4 个并发 Worker 并行解码管道
        // 缩略图解码是 CPU 密集型，限制为两个 worker，给 UI 合成和用户操作留出余量。
        const int workerCount = 2;
        var signal = _thumbnailWorkSignal;

        for (int w = 0; w < workerCount; w++)
        {
            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    PhotoCardItemViewModel? card = null;

                    lock (_thumbnailQueueLock)
                    {
                        while (_highPriorityThumbnailQueue.Count > 0)
                        {
                            var candidate = _highPriorityThumbnailQueue.First!.Value;
                            _highPriorityThumbnailQueue.RemoveFirst();
                            _highPrioritySet.Remove(candidate);

                            if (candidate.Thumbnail is null && !_processingCards.Contains(candidate))
                            {
                                card = candidate;
                                _processingCards.Add(card);
                                break;
                            }
                        }

                        if (card is null)
                        {
                            while (_backgroundThumbnailQueue.Count > 0)
                            {
                                var candidate = _backgroundThumbnailQueue.Dequeue();
                                if (candidate.Thumbnail is null && !_processingCards.Contains(candidate))
                                {
                                    card = candidate;
                                    _processingCards.Add(card);
                                    break;
                                }
                            }
                        }
                    }

                    if (card is null)
                    {
                        try
                        {
                            await signal.WaitAsync(token);
                            continue;
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (ObjectDisposedException)
                        {
                            break;
                        }
                    }

                    try
                    {
                        if (card.Thumbnail is null)
                        {
                            var result = _thumbnailReader.Read(card.PhotoPath, LruThumbnailManager.ThumbnailMaxSize);
                            if (result is not null && !token.IsCancellationRequested)
                            {
                                Bitmap? bmp = null;
                                try
                                {
                                    using var ms = new MemoryStream(result.ImageBytes);
                                    bmp = new Bitmap(ms);
                                }
                                catch (Exception ex)
                                {
                                    System.Diagnostics.Debug.WriteLine($"Bitmap decode error: {ex.Message}");
                                }

                                if (bmp is not null && !token.IsCancellationRequested)
                                {
                                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                                    {
                                        if (token.IsCancellationRequested)
                                        {
                                            bmp.Dispose();
                                            return;
                                        }
                                        if (string.IsNullOrEmpty(card.ResolutionText))
                                        {
                                            card.ResolutionText = $"{result.Width}×{result.Height}";
                                        }
                                        UpdateCardAspect(card, result.Width, result.Height);
                                        _lruThumbnailManager.SetThumbnail(card, bmp);
                                    });
                                }
                                else
                                {
                                    bmp?.Dispose();
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Thumbnail worker decode exception: {ex.Message}");
                    }
                    finally
                    {
                        lock (_thumbnailQueueLock)
                        {
                            _processingCards.Remove(card);
                        }
                    }
                }
            }, token);
        }
    }

    /// <summary>
    /// 卡片进入视口或被用户滑到时的高优先级插队加载。
    /// </summary>
    public void PrioritizeThumbnail(PhotoCardItemViewModel card)
    {
        if (card.Thumbnail is not null) return;

        // 1. 尝试直接从本地磁盘高速缓存载入（0 毫秒级极速命中）
        EnsureThumbnailLoaded(card);
        if (card.Thumbnail is not null) return;

        // 2. 未命中磁盘缓存，压入高优先级队列顶端并唤醒解码 Worker 插队处理
        lock (_thumbnailQueueLock)
        {
            if (_processingCards.Contains(card))
            {
                return;
            }

            if (_highPrioritySet.Add(card))
            {
                _highPriorityThumbnailQueue.AddFirst(card);
            }
            else
            {
                _highPriorityThumbnailQueue.Remove(card);
                _highPriorityThumbnailQueue.AddFirst(card);
            }

            if (_thumbnailWorkSignal is not null && _thumbnailWorkSignal.CurrentCount < 4)
            {
                try { _thumbnailWorkSignal.Release(); }
                catch
                {
                    // ignored
                }
            }
        }
    }

    /// <summary>
    /// 视口滚动事件响应：仅按需加载可视区域卡片位图，超出上限自动由 LRU 剔除。
    /// </summary>
    public void OnViewportScrolled(double offsetY, double viewportHeight)
    {
        if (viewportHeight <= 0) return;

        _isUserScrolling = true;
        _scrollIdleTimer.Stop();
        _scrollIdleTimer.Start();

        // 直接按已生成的行做命中，避免用固定行高估算造成预热错位。
        double cursor = 0;
        int remainingVideoPreloads = offsetY < 5 ? 1 : 0;
        foreach (var item in FlattenedDisplayItems)
        {
            if (item is TimelineHeaderItemViewModel)
            {
                cursor += 44;
                continue;
            }
            if (item is not PhotoGridRowViewModel row) continue;

            double rowHeight = row.RowHeight + 90; // 预览区 + 信息栏 + 行间距
            bool intersects = cursor + rowHeight >= Math.Max(0, offsetY - 180) &&
                              cursor <= offsetY + viewportHeight + 180;
            if (intersects)
            {
                for (int i = 0; i < row.Cards.Count; i++)
                {
                    var card = row.Cards[i];
                    PrioritizeThumbnail(card);
                    // 首屏只预热一段视频，与单组帧缓存预算一致；滚动期间不启动 FFmpeg。
                    if (remainingVideoPreloads > 0
                        && card.CachedFrames is null
                        && (!string.IsNullOrWhiteSpace(card.VideoPath) || card.IsMotionPhoto))
                    {
                        PlaybackHost.Instance.Preload(card);
                        remainingVideoPreloads--;
                    }
                }
            }
            cursor += rowHeight + 12;
            if (cursor > offsetY + viewportHeight + 260) break;
        }
    }

    partial void OnNamingFormatChanged(int value)
    {
        UpdateNamingDemo();
    }

    private void UpdateNamingDemo()
    {
        LiveFilenameDemo = NamingFormat switch
        {
            0 => "MVIMG_IMG_0012.jpg",
            1 => "MVIMG_20260905_142033_IMG_0012.jpg",
            2 => "MVIMG_20260905_142033.jpg",
            _ => "MVIMG_20260905_142033_IMG_0012.jpg"
        };
    }

    partial void OnSourceActionChanged(int value)
    {
        IsDeleteWarningVisible = value == 3;
    }

    [RelayCommand]
    public void TriggerBatchConvert()
    {
        if (IsRunning)
        {
            return;
        }

        // 1. 安全矩阵预检：磁盘容量预检
        var targetCards = _groups.SelectMany(g => g.AllCards).Where(c => c.IsSelected).ToList();
        if (targetCards.Count == 0)
        {
            targetCards = _groups.SelectMany(g => g.AllCards).ToList();
        }
        long totalBytes = ComputeTotalBytes(targetCards);
        var (hasSpace, req, avail) = SafetyGuard.CheckDiskSpace(OutputDirectory, totalBytes);
        if (!hasSpace)
        {
            LowDiskSpaceDialogViewModel lowDiskVm = new()
            {
                TargetDirectory = OutputDirectory,
                RequiredSpaceText = $"{req / (1024.0 * 1024.0):F1} MB",
                AvailableSpaceText = $"{avail / (1024.0 * 1024.0):F1} MB",
                OnDismiss = () => OnCloseModal?.Invoke()
            };
            OnShowModal?.Invoke(lowDiskVm);
            return;
        }

        // 2. 物理删除原片红屏防灾
        if (SourceAction == 3)
        {
            DeleteConfirmDialogViewModel deleteVm = new(_localizer)
            {
                AffectedCount = targetCards.Count,
                AffectedSizeText = FormatBytes(totalBytes),
                OnConfirmed = () =>
                {
                    OnCloseModal?.Invoke();
                    ExecuteBatchPipeline();
                },
                OnCancelled = () =>
                {
                    OnCloseModal?.Invoke();
                    SourceAction = 0; // 重置为保留原片
                }
            };
            OnShowModal?.Invoke(deleteVm);
            return;
        }

        ExecuteBatchPipeline();
    }

    private void ExecuteBatchPipeline()
    {
        if (IsRunning)
        {
            return;
        }

        // 启动前固定本批次的卡片与参数，运行期间切换方向或重新扫描不影响后台任务
        var cards = _groups.SelectMany(g => g.AllCards).Where(c => c.IsSelected).ToList();
        if (cards.Count == 0)
        {
            cards = _groups.SelectMany(g => g.AllCards).ToList();
        }

        var settings = _settingsService.Current;
        var plan = new BatchPlan(
            cards,
            ConversionDirection,
            new OutputOptions(OutputDirectory)
            {
                Conflict = AutoAppendIndex ? ConflictPolicy.AppendIndex : ConflictPolicy.Overwrite,
                PreserveHierarchyFrom = KeepSubfolderHierarchy && !string.IsNullOrEmpty(_lastScannedDirectory) ? _lastScannedDirectory : null
            },
            (MergeNamingFormat)NamingFormat,
            (SourceFileAction)SourceAction,
            HeicQuality,
            Math.Clamp(settings.Concurrency, 1, 8),
            NullIfBlank(settings.ExifToolPath),
            NullIfBlank(settings.FfmpegPath),
            NullIfBlank(settings.HeifEncPath));

        var cts = new CancellationTokenSource();
        _batchCts = cts;
        IsRunning = true;
        CurrentProgressPercent = 0;
        ProgressRatioText = string.Empty;
        CurrentFileName = string.Empty;
        _ = RunBatchAsync(plan, cts);
    }

    private sealed record BatchPlan(
        IReadOnlyList<PhotoCardItemViewModel> Cards,
        int Direction,
        OutputOptions Output,
        MergeNamingFormat Naming,
        SourceFileAction SourceAction,
        int HeicQuality,
        int Parallelism,
        string? ExifToolPath,
        string? FfmpegPath,
        string? HeifEncPath);

    private async Task RunBatchAsync(BatchPlan plan, CancellationTokenSource cts)
    {
        var modeName = _localizer[plan.Direction switch { 1 => "ReportModeApple", 2 => "ReportModeExtract", _ => "ReportModeMerge" }];
        var progress = new DesktopProgressReporter(value =>
        {
            CurrentProgressPercent = value.Total > 0 ? value.Completed * 100.0 / value.Total : 0;
            ProgressRatioText = $"{CurrentProgressPercent:F0}% ({value.Completed}/{value.Total})";
            CurrentFileName = value.CurrentItem;
        });

        BatchReportModel model;
        try
        {
            model = await Task.Run(() => ExecuteBatchAsync(plan, modeName, _localizer, progress, cts.Token));
        }
        catch (OperationCanceledException)
        {
            model = BatchReportMapper.ToModel(_localizer, new BatchReport([], TimeSpan.Zero, Canceled: true), modeName, string.Empty, string.Empty, plan.Output.Directory);
        }
        catch (Exception ex)
        {
            ErrorLogger.Log(ex, modeName);
            model = BatchReportMapper.FromError(_localizer, ex.Message, modeName, plan.Output.Directory);
        }
        finally
        {
            _batchCts = null;
            cts.Dispose();
            IsRunning = false;
        }

        OnBatchReportReady?.Invoke(model);
    }

    private static async Task<BatchReportModel> ExecuteBatchAsync(BatchPlan plan, string modeName, ILocalizer localizer, IProgress<BatchProgress> progress, CancellationToken cancellationToken)
    {
        await using var metadata = ExifToolMetadataService.Create(plan.ExifToolPath, plan.Parallelism);
        IImageConverter imageConverter = ToolLocator.Find(HeifEncImageConverter.ExecutableName, plan.HeifEncPath) is { } heifEnc
            ? HeifEncImageConverter.Create(heifEnc)
            : MagickImageConverter.Instance;

        if (plan.Direction == 0)
        {
            var cards = plan.Cards.Where(c => !c.IsMotionPhoto && !string.IsNullOrEmpty(c.VideoPath))
                                  .Select(c => (Card: c, Pair: c.Pair ?? new MediaPair(c.PhotoPath, c.VideoPath!)))
                                  .ToList();
            if (cards.Count == 0)
            {
                return BatchReportMapper.FromError(localizer, localizer["NoMergePairs"], modeName, plan.Output.Directory);
            }

            var pairs = cards.Select(x => x.Pair).ToList();
            var forced = cards.Where(x => x.Card.IsForceAccepted).Select(x => x.Pair).ToList();
            var merger = new MotionPhotoMerger(metadata, imageConverter, FfmpegVideoConverter.Create(plan.FfmpegPath));
            var report = await merger.MergeAsync(
                new MergeRequest
                {
                    Candidates = pairs,
                    ForceAccepted = forced,
                    Output = plan.Output,
                    Naming = plan.Naming,
                    SourceAction = plan.SourceAction,
                    Parallelism = plan.Parallelism
                },
                progress,
                cancellationToken);
            return BatchReportMapper.ToModel(localizer, report, modeName, localizer["MergeSuccessDesc"], "Motion Photo", plan.Output.Directory);
        }

        var files = plan.Cards.Where(c => c.IsMotionPhoto).Select(c => c.PhotoPath).ToList();
        if (files.Count == 0)
        {
            return BatchReportMapper.FromError(localizer, localizer["NoSplitCandidates"], modeName, plan.Output.Directory);
        }

        var target = plan.Direction == 1 ? SplitTarget.Apple : SplitTarget.Extract;
        var videoConverter = target == SplitTarget.Apple ? FfmpegVideoConverter.Create(plan.FfmpegPath) : null;
        var splitter = new MotionPhotoSplitter(metadata, imageConverter, videoConverter);
        var splitReport = await splitter.SplitAsync(
            new SplitRequest
            {
                Files = files,
                Output = plan.Output,
                Target = target,
                SourceAction = plan.SourceAction,
                HeicQuality = plan.HeicQuality,
                Parallelism = plan.Parallelism
            },
            progress,
            cancellationToken);
        var targetName = localizer[target == SplitTarget.Apple ? "SplitTargetApple" : "SplitTargetExtract"];
        return BatchReportMapper.ToModel(localizer, splitReport, modeName, localizer.Format("SplitSuccessDescFormat", targetName), targetName, plan.Output.Directory);
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    [RelayCommand]
    public void CancelBatch() => _batchCts?.Cancel();

    /// <summary>
    /// 试播：对当前视口内可见卡片按序触发悬停微动放映（遵循 PRD 3.2，仅处理可见项，杜绝全量并发 OOM）。
    /// </summary>
    [RelayCommand]
    public void PlayAllVisible()
    {
        foreach (var card in _groups.SelectMany(g => g.AllCards).Where(c => c.IsVisible))
        {
            PlaybackHost.Instance.OnPointerEnter(card);
        }
    }

    public void UpdateSelectionSummary()
    {
        int selectedCount = _groups.Sum(g => g.AllCards.Count(c => c.IsSelected));
        int total = _groups.Sum(g => g.AllCards.Count);
        SelectedBadgeText = _localizer.Format("SelectedBadgeFormat", selectedCount);
        SelectedSummaryText = _localizer.Format(
            "SelectedSummaryFormat",
            total,
            FormatBytes(ComputeTotalBytes(_groups.SelectMany(g => g.AllCards))));
        UpdateDirectionTexts();
    }

    /// <summary>累加卡片对应图片与视频文件的磁盘字节数。</summary>
    private static long ComputeTotalBytes(IEnumerable<PhotoCardItemViewModel> cards)
    {
        long sum = 0;
        foreach (var c in cards)
        {
            sum += SafeFileLength(c.PhotoPath);
            sum += SafeFileLength(c.VideoPath);
        }
        return sum;
    }

    private static long SafeFileLength(string? path)
    {
        try
        {
            return !string.IsNullOrEmpty(path) && File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static string FormatBytes(long bytes) =>
        Converters.ByteSizeConverter.Instance.Convert(bytes, typeof(string), null, CultureInfo.InvariantCulture) as string
        ?? $"{bytes} B";

    public void TriggerQuickLook()
    {
        var target = _groups.SelectMany(g => g.AllCards).FirstOrDefault(c => c.IsHoverPlaying)
                  ?? _groups.SelectMany(g => g.AllCards).FirstOrDefault(c => c.IsSelected)
                  ?? _groups.SelectMany(g => g.AllCards).FirstOrDefault();
        if (target != null)
        {
            OpenQuickLook(target);
        }
    }

    private List<PhotoCardItemViewModel> GetSortedCards()
    {
        return SortMode switch
        {
            "Name" => IsSortAscending
                ? _allCards.OrderBy(c => c.FileName, StringComparer.CurrentCultureIgnoreCase).ToList()
                : _allCards.OrderByDescending(c => c.FileName, StringComparer.CurrentCultureIgnoreCase).ToList(),
            _ => IsSortAscending
                ? _allCards.OrderBy(c => c.DateTaken).ThenBy(c => c.FileName).ToList()
                : _allCards.OrderByDescending(c => c.DateTaken).ThenBy(c => c.FileName).ToList()
        };
    }

    private void BuildGroups()
    {
        var sortedCards = GetSortedCards();
        var culture = _localizer.Culture;

        if (GroupingMode == "None")
        {
            var header = new TimelineHeaderItemViewModel
            {
                Key = "group_none",
                GroupDate = DateTime.MinValue,
                Title = string.Empty,
                LocationSummary = string.Empty,
                PhotoCount = sortedCards.Count,
                IsVisible = false
            };
            _groups =
            [
                new TimelineGroup
                {
                    Header = header,
                    AllCards = sortedCards
                }
            ];
            WireCardsAndGroups();
            return;
        }

        IEnumerable<IGrouping<DateTime, PhotoCardItemViewModel>> grouped = GroupingMode switch
        {
            "Month" => sortedCards.GroupBy(c => new DateTime(c.DateTaken.Year, c.DateTaken.Month, 1)),
            "Year" => sortedCards.GroupBy(c => new DateTime(c.DateTaken.Year, 1, 1)),
            _ => sortedCards.GroupBy(c => c.DateTaken.Date) // "Date"
        };

        // 依据排序正逆序排列各分组
        grouped = IsSortAscending
            ? grouped.OrderBy(g => g.Key)
            : grouped.OrderByDescending(g => g.Key);

        var newGroups = new List<TimelineGroup>();
        foreach (var g in grouped)
        {
            var groupCards = g.ToList();
            var firstCard = groupCards.First();

            string key = GroupingMode switch
            {
                "Month" => $"month_{g.Key:yyyy_MM}",
                "Year" => $"year_{g.Key:yyyy}",
                _ => $"date_{g.Key:yyyy_MM_dd}"
            };

            string title = GroupingMode switch
            {
                "Month" => g.Key.ToString(_localizer["GroupTitleMonthFormat"], culture),
                "Year" => g.Key.ToString(_localizer["GroupTitleYearFormat"], culture),
                _ => g.Key.ToString(_localizer["DateGroupFormat"], culture)
            };

            string locSummary = GroupingMode == "Date" ? firstCard.LocationSummary : string.Empty;
            bool isCollapsed = _collapsedGroupKeys.Contains(key);

            var header = new TimelineHeaderItemViewModel
            {
                Key = key,
                GroupDate = g.Key,
                Title = title,
                LocationSummary = locSummary,
                PhotoCount = groupCards.Count,
                IsCollapsed = isCollapsed,
                IsVisible = true
            };

            newGroups.Add(new TimelineGroup
            {
                Header = header,
                AllCards = groupCards
            });
        }

        _groups = newGroups;
        WireCardsAndGroups();
    }

    public void RebuildFlattenedStream()
    {
        OnBeforeStreamRebuild?.Invoke();

        DisplayGroups.Clear();

        var newItems = new List<IGalleryDisplayItem>();

        foreach (var group in _groups)
        {
            DisplayGroups.Add(group);

            if (group.Header.IsVisible)
            {
                newItems.Add(group.Header);
            }

            if (!group.Header.IsCollapsed)
            {
                var rows = group.BuildRows(ScaleMode, true, _lastParentWidth);
                foreach (var row in rows)
                {
                    newItems.Add(row);
                }
            }
        }

        FlattenedDisplayItems.Reset(newItems);

        OnAfterStreamRebuild?.Invoke();
    }

    private void OpenArbitrationDialog(PhotoCardItemViewModel card)
    {
        ArbitrateDialogViewModel arbitrateVm = new(_localizer)
        {
            TargetCard = card,
            OnWhitelistConfirmed = c =>
            {
                c.IsForceAccepted = true;
                ReadyCount++;
                SuspiciousCount = Math.Max(0, SuspiciousCount - 1);
                OnCloseModal?.Invoke();
                UpdateSelectionSummary();
            },
            OnRejectSplit = c =>
            {
                OnCloseModal?.Invoke();
            },
            OnDismiss = () => OnCloseModal?.Invoke()
        };
        OnShowModal?.Invoke(arbitrateVm);
    }

    private QuickLookDialogViewModel? _activeQuickLookVm;

    private void OpenQuickLook(PhotoCardItemViewModel card)
    {
        PlaybackHost.Instance.StopHoverPlayback();

        var allCards = _groups.SelectMany(g => g.AllCards).ToList();
        int idx = allCards.IndexOf(card);
        string indexText = allCards.Count > 0 ? $"{idx + 1} / {allCards.Count}" : string.Empty;

        if (_activeQuickLookVm is not null)
        {
            _activeQuickLookVm.SetCard(card, indexText);
            return;
        }

        var qlVm = new QuickLookDialogViewModel(_localizer, card, indexText)
        {
            OnClose = () =>
            {
                _activeQuickLookVm?.Cleanup();
                _activeQuickLookVm = null;
                OnCloseModal?.Invoke();
            },
            OnNavigate = offset =>
            {
                var currentCards = _groups.SelectMany(g => g.AllCards).ToList();
                if (currentCards.Count == 0) return;
                int curIdx = currentCards.IndexOf(_activeQuickLookVm?.Card ?? card);
                if (curIdx < 0) curIdx = 0;
                int nextIdx = (curIdx + offset + currentCards.Count) % currentCards.Count;
                var nextCard = currentCards[nextIdx];
                _activeQuickLookVm?.SetCard(nextCard, $"{nextIdx + 1} / {currentCards.Count}");
            }
        };

        _activeQuickLookVm = qlVm;
        OnShowModal?.Invoke(qlVm);
    }
}
