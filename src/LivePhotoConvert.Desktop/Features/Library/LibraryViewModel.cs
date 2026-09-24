using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Media.Imaging;
using LivePhotoConvert.Desktop.Collections;
using LivePhotoConvert.Desktop.Features.Dialogs;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Models;
using LivePhotoConvert.Desktop.Services;

namespace LivePhotoConvert.Desktop.Features.Library;

/// <summary>
/// 图库画廊：扫描、分组、排序、缩放、选择、缩略图与预览。动作与参数由检查器负责。
/// </summary>
public sealed partial class LibraryViewModel : ViewModelBase
{
    private static readonly string[] SortModes = ["DateTaken", "DateCreated", "DateModified", "Name"];
    private static readonly string[] GroupingModes = ["Date", "Month", "Year", "None"];
    private static readonly string[] ScaleModes = ["Medium", "Small", "Large"];
    private static readonly string[] CropModes = ["Natural", "Square"];

    private readonly SettingsStore _settings;
    private readonly ILocalizer _localizer;
    private readonly IDialogService _dialogs;
    private readonly IFilePicker _filePicker;
    private readonly PlaybackHost _playback;
    private bool _suppressSelectionChanged;
    private List<TimelineGroup> _groups = [];
    private List<PhotoCardItemViewModel> _allCards = [];
    private readonly HashSet<string> _collapsedGroupKeys = [];
    private readonly ThumbnailReader _thumbnailReader = new();
    private readonly LruThumbnailManager _lruThumbnailManager;
    private CancellationTokenSource? _thumbnailCts;
    private readonly Lock _thumbnailQueueLock = new();
    private readonly LinkedList<PhotoCardItemViewModel> _highPriorityThumbnailQueue = new();
    private readonly HashSet<PhotoCardItemViewModel> _highPrioritySet = new();
    private readonly Queue<PhotoCardItemViewModel> _backgroundThumbnailQueue = new();
    private readonly HashSet<PhotoCardItemViewModel> _processingCards = new();
    private SemaphoreSlim? _thumbnailWorkSignal;

    // 扫描生命周期与按扫描模式缓存的结果
    private readonly Dictionary<int, AlbumScanner.ScanResult> _dirScanCache = new();
    private string _lastScannedDirectory = string.Empty;
    private CancellationTokenSource? _scanCts;

    [ObservableProperty]
    private bool _isScanning;

    /// <summary>当前扫描模式，见 <see cref="ScanModes"/>。</summary>
    [ObservableProperty]
    private int _scanMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FunnelTotalText))]
    [NotifyPropertyChangedFor(nameof(HasScannedFiles))]
    private int _totalScannedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalLiveReadyCount))]
    [NotifyPropertyChangedFor(nameof(FunnelReadyText))]
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

    [ObservableProperty]
    private string _selectedSummaryText = string.Empty;

    [ObservableProperty]
    private string _selectedBadgeText = string.Empty;

    [ObservableProperty]
    private int _selectedCount;

    /// <summary>最近一次悬停或点选的卡片，供检查器的对比预览使用。</summary>
    [ObservableProperty]
    private PhotoCardItemViewModel? _focusedCard;

    /// <summary>选中集合或卡片集合（重新扫描、切换扫描模式）变化后触发。</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>全部卡片，按扫描顺序。</summary>
    public IReadOnlyList<PhotoCardItemViewModel> AllCards => _allCards;

    /// <summary>选中的卡片；未选中任何卡片时为全部卡片。</summary>
    public IReadOnlyList<PhotoCardItemViewModel> SelectedOrAllCards
    {
        get
        {
            var selected = _allCards.Where(c => c.IsSelected).ToList();
            return selected.Count > 0 ? selected : _allCards;
        }
    }

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
    private string _sortMode; // DateTaken, DateCreated, DateModified, Name

    [ObservableProperty]
    private bool _isSortAscending;

    [ObservableProperty]
    private string _filterMode = "All"; // All, SuspiciousOnly, SelectedOnly

    [ObservableProperty]
    private bool _isFilterBannerVisible;

    [ObservableProperty]
    private string _filterBannerDesc = string.Empty;

    [ObservableProperty]
    private string _cropMode; // Natural (等高原始比例), Square (1:1)

    [ObservableProperty]
    private string _scaleMode; // Small, Medium, Large

    [ObservableProperty]
    private string _groupingMode; // Date, Month, Year, None

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

    public LibraryViewModel(
        SettingsStore settings,
        ILocalizer localizer,
        IDialogService dialogs,
        IFilePicker filePicker,
        PlaybackHost playback)
    {
        _settings = settings;
        _localizer = localizer;
        _dialogs = dialogs;
        _filePicker = filePicker;
        _playback = playback;
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

        _playback.OnQuickLookTriggered = OpenQuickLook;
        _playback.OnCardFocused = card =>
        {
            EnsureThumbnailLoaded(card);
            FocusedCard = card;
        };

        var s = _settings.Current;
        _scanMode = ScanModes.For(s.Action);
        var gallery = s.Gallery;
        _sortMode = OneOf(gallery.SortMode, SortModes);
        _isSortAscending = gallery.SortAscending;
        _groupingMode = OneOf(gallery.Grouping, GroupingModes);
        _scaleMode = OneOf(gallery.Scale, ScaleModes);
        _cropMode = OneOf(gallery.Crop, CropModes);

        if (!string.IsNullOrWhiteSpace(s.LastScanDirectory) && Directory.Exists(s.LastScanDirectory))
        {
            _albumDirectory = s.LastScanDirectory;
        }

        UpdateSelectionSummary();

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

            grp.Header.OnSelectGroup = h => ChangeSelection(() =>
            {
                foreach (var c in grp.AllCards)
                {
                    c.IsSelected = true;
                }
            });

            foreach (var card in grp.AllCards)
            {
                card.OnArbitrateRequested = OpenArbitrationDialog;
                card.OnQuickLookRequested = OpenQuickLook;
                card.OnPriorityLoadRequested = PrioritizeThumbnail;
                // 分组重建会重复经过同一张卡片，先退订保证只订阅一次
                card.PropertyChanged -= OnCardPropertyChanged;
                card.PropertyChanged += OnCardPropertyChanged;
                card.CropMode = CropMode;
            }
        }
    }

    /// <summary>切换扫描模式；各模式的扫描结果按目录缓存，切回时不重新读盘。</summary>
    public async Task SetScanModeAsync(int mode)
    {
        if (mode == ScanMode)
        {
            return;
        }

        ScanMode = mode;
        if (HasSelectedDirectory)
        {
            if (_scanCts is not null)
            {
                await _scanCts.CancelAsync();
            }
            await RefreshAlbumAsync();
        }
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
        SortMode = OneOf(mode, SortModes);
        _settings.Update(s => s.Gallery.SortMode = SortMode);
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
        _settings.Update(s => s.Gallery.SortAscending = IsSortAscending);
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
        CropMode = OneOf(mode, CropModes);
        _settings.Update(s => s.Gallery.Crop = CropMode);
        foreach (var grp in _groups)
        {
            foreach (var card in grp.AllCards)
            {
                card.CropMode = CropMode;
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
        ScaleMode = OneOf(scale, ScaleModes);
        _settings.Update(s => s.Gallery.Scale = ScaleMode);
        UpdateCardWidth(_lastParentWidth);
        RebuildFlattenedStream();
    }

    [RelayCommand]
    public void SetGroupingMode(string grouping)
    {
        GroupingMode = OneOf(grouping, GroupingModes);
        _settings.Update(s => s.Gallery.Grouping = GroupingMode);
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
        ChangeSelection(() =>
        {
            foreach (var card in _groups.SelectMany(g => g.AllCards).Where(c => c.IsVisible))
            {
                card.IsSelected = sel;
            }
        });
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
        string? folder = await _filePicker.PickFolderAsync(_localizer["SelectAlbumFolderBtn"], AlbumDirectory);
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        _dirScanCache.Clear();
        AlbumDirectory = folder;
        _settings.Update(s => s.LastScanDirectory = folder);

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
            FocusedCard = null;
            OnPropertyChanged(nameof(HasPhotos));
            OnPropertyChanged(nameof(AllCards));
            RebuildFlattenedStream();
            UpdateSelectionSummary();
            RaiseSelectionChanged();
            return;
        }

        // 仅当相册目录路径变更时才清空该缓存
        if (!string.Equals(target, _lastScannedDirectory, StringComparison.OrdinalIgnoreCase))
        {
            _dirScanCache.Clear();
            _lastScannedDirectory = target;
        }

        int direction = ScanMode;
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
        OnPropertyChanged(nameof(AllCards));
        if (FocusedCard is not null && !_allCards.Contains(FocusedCard))
        {
            FocusedCard = null;
        }

        BuildGroups();
        RebuildFlattenedStream();
        UpdateSelectionSummary();
        RaiseSelectionChanged();
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
                        _playback.Preload(card);
                        remainingVideoPreloads--;
                    }
                }
            }
            cursor += rowHeight + 12;
            if (cursor > offsetY + viewportHeight + 260) break;
        }
    }

    /// <summary>
    /// 试播：对当前视口内可见卡片按序触发悬停微动放映（遵循 PRD 3.2，仅处理可见项，杜绝全量并发 OOM）。
    /// </summary>
    [RelayCommand]
    public void PlayAllVisible()
    {
        foreach (var card in _groups.SelectMany(g => g.AllCards).Where(c => c.IsVisible))
        {
            _playback.OnPointerEnter(card);
        }
    }

    public void UpdateSelectionSummary()
    {
        SelectedCount = _allCards.Count(c => c.IsSelected);
        SelectedBadgeText = _localizer.Format("SelectedBadgeFormat", SelectedCount);
        SelectedSummaryText = _localizer.Format(
            "SelectedSummaryFormat",
            _allCards.Count,
            FormatBytes(JobFactory.SourceBytes(_allCards)));
    }

    /// <summary>批量修改选中状态，结束后只通知一次。</summary>
    private void ChangeSelection(Action change)
    {
        _suppressSelectionChanged = true;
        try
        {
            change();
        }
        finally
        {
            _suppressSelectionChanged = false;
        }

        UpdateSelectionSummary();
        RaiseSelectionChanged();
    }

    private void OnCardPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PhotoCardItemViewModel.IsSelected) || _suppressSelectionChanged || sender is not PhotoCardItemViewModel card)
        {
            return;
        }

        FocusedCard = card;
        UpdateSelectionSummary();
        RaiseSelectionChanged();
    }

    private void RaiseSelectionChanged() => SelectionChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>设置文件里无法识别的取值回退到第一项（默认值）。</summary>
    private static string OneOf(string? value, string[] allowed) =>
        value is not null && allowed.Contains(value, StringComparer.Ordinal) ? value : allowed[0];

    private static string FormatBytes(long bytes) =>
        Converters.ByteSizeConverter.Instance.Convert(bytes, typeof(string), null, CultureInfo.InvariantCulture) as string
        ?? $"{bytes} B";

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

    private async void OpenArbitrationDialog(PhotoCardItemViewModel card)
    {
        var verdict = await _dialogs.ShowAsync(new ArbitrateDialogViewModel(_localizer) { TargetCard = card });
        if (verdict == ArbitrationVerdict.Accept && !card.IsForceAccepted)
        {
            card.IsForceAccepted = true;
            ReadyCount++;
            SuspiciousCount = Math.Max(0, SuspiciousCount - 1);
            UpdateSelectionSummary();
            RaiseSelectionChanged();
        }
    }

    private QuickLookDialogViewModel? _activeQuickLookVm;
    private List<PhotoCardItemViewModel> _quickLookCards = [];

    private async void OpenQuickLook(PhotoCardItemViewModel card)
    {
        _playback.StopHoverPlayback();

        if (_activeQuickLookVm is { IsClosed: false } open)
        {
            int openIndex = _quickLookCards.IndexOf(card);
            if (openIndex >= 0)
            {
                open.ShowIndex(openIndex);
            }

            return;
        }

        // 打开时固定卡片序列，浏览期间重新扫描或重排不影响左右切换
        var cards = _groups.SelectMany(g => g.AllCards).ToList();
        int index = cards.IndexOf(card);
        if (index < 0)
        {
            return;
        }

        var quickLook = new QuickLookDialogViewModel(_localizer, i => (uint)i < (uint)cards.Count ? cards[i] : null, cards.Count, index);
        _activeQuickLookVm = quickLook;
        _quickLookCards = cards;
        try
        {
            await _dialogs.ShowAsync(quickLook);
        }
        finally
        {
            if (ReferenceEquals(_activeQuickLookVm, quickLook))
            {
                _activeQuickLookVm = null;
                _quickLookCards = [];
            }
        }
    }
}
