using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoConvert.Desktop.Features.Dialogs;
using LivePhotoConvert.Desktop.Features.Library.Gallery;
using LivePhotoConvert.Desktop.Features.Library.Thumbnails;
using LivePhotoConvert.Desktop.Features.Playback;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Models;

namespace LivePhotoConvert.Desktop.Features.Library;

/// <summary>
/// 图库画廊门面：把扫描（<see cref="LibraryCatalog"/>）、按动作筛选、选择与计数（<see cref="GallerySelection"/>）、
/// 排版与视图偏好（<see cref="GalleryLayoutViewModel"/>）以及缩略图管线接在一起，并承载相册目录与卡片命令。
/// </summary>
public sealed partial class LibraryViewModel : ViewModelBase
{
    private static readonly string[] FilterModes = ["All", "SuspiciousOnly", "SelectedOnly"];

    private readonly SettingsStore _settings;
    private readonly ILocalizer _localizer;
    private readonly IDialogService _dialogs;
    private readonly IFilePicker _filePicker;
    private readonly INavigator _navigator;
    private readonly DispatcherTimer _scrollIdleTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private double _renderScaling = 1.0;
    private bool _scopeRefreshPosted;
    private QuickLookDialogViewModel? _quickLook;
    private List<PhotoCardItemViewModel> _quickLookCards = [];

    public LibraryViewModel(SettingsStore settings, ILocalizer localizer, IDialogService dialogs, IFilePicker filePicker, INavigator navigator,
        PlaybackService playback, IThumbnailPipeline thumbnails, LibraryCatalog catalog)
    {
        (_settings, _localizer, _dialogs, _filePicker, _navigator) = (settings, localizer, dialogs, filePicker, navigator);
        (Playback, Thumbnails, Catalog) = (playback, thumbnails, catalog);
        Selection = new GallerySelection(localizer);
        Layout = new GalleryLayoutViewModel(localizer, settings);
        _actionFilter = Enum.IsDefined(settings.Current.Action) ? settings.Current.Action : ConversionAction.ToAndroid;
        _albumDirectory = settings.Current.LastScanDirectory ?? string.Empty;
        ConfigureThumbnails();

        _scrollIdleTimer.Tick += (_, _) =>
        {
            _scrollIdleTimer.Stop();
            Thumbnails.NextGeneration();
        };
        Layout.LayoutChanged += (_, _) => Thumbnails.NextGeneration();
        Layout.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(GalleryLayoutViewModel.ScaleMode) or nameof(GalleryLayoutViewModel.CropMode))
            {
                ConfigureThumbnails();
            }
        };
        Selection.SelectionChanged += (_, _) => SelectionChanged?.Invoke(this, EventArgs.Empty);
        Selection.CardToggled += (_, card) => FocusedCard = card;
        Catalog.CardsReplaced += (_, _) => OnCardsReplaced();
        Catalog.CardUpgraded += (_, _) => PostScopeRefresh();
        Catalog.PropertyChanged += OnCatalogPropertyChanged;
        _dialogs.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IDialogService.Current))
            {
                OnPropertyChanged(nameof(HasActiveDialog));
            }
        };
        _localizer.LanguageChanged += (_, _) => Dispatcher.UIThread.Post(RefreshTexts);

        // 记住的相册目录冷启动即扫描；目录已不存在时由目录状态提示
        if (HasSelectedDirectory)
        {
            RefreshAlbumAsync().LogFaults("扫描相册");
        }
    }

    public LibraryCatalog Catalog { get; }

    public GallerySelection Selection { get; }

    public GalleryLayoutViewModel Layout { get; }

    /// <summary>播放器来源；画廊视图与 QuickLook 各自从这里创建播放器。</summary>
    public PlaybackService Playback { get; }

    /// <summary>画廊缩略图管线；视图据此把列表容器接到卡片的引用计数上。</summary>
    public IThumbnailPipeline Thumbnails { get; }

    /// <summary>选中集合或动作范围变化后触发。</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>当前动作范围内的卡片，按扫描顺序。</summary>
    public IReadOnlyList<PhotoCardItemViewModel> AllCards => Selection.Cards;

    /// <summary>选中的卡片；未选中任何卡片时为全部卡片。</summary>
    public IReadOnlyList<PhotoCardItemViewModel> SelectedOrAllCards => Selection.SelectedOrAll;

    /// <summary>画廊只列出适用于该动作的条目；切换动作只筛选，不重新扫描。</summary>
    [ObservableProperty]
    private ConversionAction _actionFilter;

    /// <summary>最近一次悬停或点选的卡片，供检查器的对比预览使用。</summary>
    [ObservableProperty]
    private PhotoCardItemViewModel? _focusedCard;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AlbumDirectorySummary), nameof(HasSelectedDirectory), nameof(EmptyStateTitle), nameof(EmptyStateSubtitle))]
    private string _albumDirectory;

    /// <summary>All / SuspiciousOnly / SelectedOnly：在动作范围内再按状态显示。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilterBannerVisible), nameof(FilterBannerDesc))]
    private string _filterMode = "All";

    public bool IsScanning => Catalog.IsScanning;

    public bool HasSelectedDirectory => !string.IsNullOrWhiteSpace(AlbumDirectory);

    public bool HasPhotos => AllCards.Count > 0;

    public string AlbumDirectorySummary => HasSelectedDirectory
        ? Path.GetFileName(AlbumDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        : _localizer["AlbumSummaryNoDir"];

    public string SelectAlbumFolderBtnText => _localizer["SelectAlbumFolderBtn"];

    public string EmptyStateTitle => _localizer[
        Catalog.Error != LibraryScanError.None ? "ScanErrorTitle" : HasSelectedDirectory ? "EmptyStateEmptyTitle" : "EmptyStateNoDirTitle"];

    public string EmptyStateSubtitle => Catalog.Error != LibraryScanError.None
        ? Catalog.StatusText
        : _localizer[HasSelectedDirectory ? "EmptyStateEmptySubtitle" : "EmptyStateNoDirSubtitle"];

    public bool IsFilterBannerVisible => FilterMode != "All";

    public string FilterBannerDesc => FilterMode switch
    {
        "SuspiciousOnly" => _localizer.Format("FilterBannerSuspiciousFormat", Selection.SuspiciousCount),
        "SelectedOnly" => _localizer["FilterBannerSelected"],
        _ => string.Empty
    };

    partial void OnActionFilterChanged(ConversionAction value) => ApplyScope();

    /// <summary>切换动作：只重新筛选已扫描的条目。</summary>
    public void SetActionFilter(ConversionAction action) => ActionFilter = action;

    [RelayCommand]
    public void SetFilterMode(string? mode)
    {
        FilterMode = FilterModes.Contains(mode) ? mode! : FilterModes[0];
        ApplyDisplayFilter();
    }

    [RelayCommand]
    public void SelectAllVisible(object? select) => Selection.SetSelected(Layout.DisplayedCards, CommandParameters.ToBool(select, true));

    /// <summary>单击卡片（Ctrl 切换、Shift 按显示顺序范围选择），并把它设为检查器的对比对象。</summary>
    public void ClickCard(PhotoCardItemViewModel card, bool toggle, bool range)
    {
        Selection.Click(card, Layout.DisplayedCards, toggle, range);
        FocusedCard = card;
    }

    /// <summary>取消范围内全部选择。</summary>
    public void ClearSelection() => Selection.Clear();

    /// <summary>空格预览的对象：最近单击且仍选中的卡片，否则为最近悬停的卡片。</summary>
    public PhotoCardItemViewModel? PreviewTarget =>
        Selection.Anchor is { IsSelected: true } anchor && Layout.DisplayedCards.Contains(anchor) ? anchor : FocusedCard;

    /// <summary>有弹窗时画廊不处理快捷键，按键留给弹窗。</summary>
    public bool HasActiveDialog => _dialogs.Current is not null;

    [RelayCommand]
    public void ToggleAllCollapse() => Layout.ToggleAllCollapse();

    [RelayCommand]
    private void ToggleGroupCollapse(TimelineHeaderItemViewModel? header) => Layout.ToggleCollapse(header);

    [RelayCommand]
    private void SelectGroup(TimelineHeaderItemViewModel? header) => Selection.SetSelected(Layout.CardsOf(header), true);

    /// <summary>选择器从当前相册（即上次选择或拖入的目录）开始。</summary>
    [RelayCommand]
    public async Task SelectAlbumFolderAsync()
    {
        var folder = await _filePicker.PickFolderAsync(_localizer["SelectAlbumFolderBtn"], AlbumDirectory);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            await OpenAlbumAsync(folder);
        }
    }

    /// <summary>拖入与选择按钮同一判定：选择相册的命令正在执行（选择器打开或其扫描未完成）时不接受新相册。</summary>
    public bool CanOpenAlbum => SelectAlbumFolderCommand.CanExecute(null);

    /// <summary>切换到指定相册：选择器与拖入共用，记入设置后扫描。</summary>
    public Task OpenAlbumAsync(string folder)
    {
        AlbumDirectory = folder;
        _settings.Update(s => s.LastScanDirectory = folder);
        return RefreshAlbumAsync();
    }

    /// <summary>视图的拖放处理不等待扫描；异常与冷启动扫描一样只记日志。</summary>
    public void OpenDroppedAlbum(string folder)
    {
        if (CanOpenAlbum)
        {
            OpenAlbumAsync(folder).LogFaults("打开拖入的相册");
        }
    }

    [RelayCommand]
    public Task RescanAlbumAsync() => RefreshAlbumAsync();

    [RelayCommand]
    public Task RefreshAlbumAsync() => Catalog.ScanAsync(AlbumDirectory);

    /// <summary>
    /// 人工裁决：确认后卡片视为就绪，计数与徽章随卡片状态更新。已有选择时并入选择；
    /// 未选中任何卡片时动作本就按全部就绪卡片统计，不因此把范围收窄到这一张。
    /// </summary>
    [RelayCommand]
    private async Task ArbitrateAsync(PhotoCardItemViewModel? card)
    {
        if (card is not { RequiresPairReview: true } ||
            await _dialogs.ShowAsync(new ArbitrateDialogViewModel(_localizer) { TargetCard = card }) != ArbitrationVerdict.Accept)
        {
            return;
        }

        Selection.Batch(() =>
        {
            card.IsForceAccepted = true;
            if (Selection.SelectedCount > 0)
            {
                card.IsSelected = true;
            }
        });
        FocusedCard = card;
        // 「仅待裁决」视图中已裁决的卡片随之移出
        ApplyDisplayFilter();
        OnPropertyChanged(nameof(FilterBannerDesc));
    }

    /// <summary>大图预览；序列固定为打开时画廊中显示的卡片，浏览期间的重排不影响左右切换。</summary>
    [RelayCommand]
    private async Task OpenQuickLookAsync(PhotoCardItemViewModel? card)
    {
        if (card is null)
        {
            return;
        }

        if (_quickLook is { IsClosed: false } open)
        {
            open.ShowIndex(_quickLookCards.IndexOf(card));
            return;
        }

        List<PhotoCardItemViewModel> cards = [.. Layout.DisplayedCards];
        if (cards.IndexOf(card) is var index and >= 0)
        {
            // QuickLook 独占播放：悬浮播放先停，弹窗期间也不会再启动
            Playback.StopAll();
            var quickLook = new QuickLookDialogViewModel(_localizer, Thumbnails, i => (uint)i < (uint)cards.Count ? cards[i] : null, cards.Count, index)
            {
                Playback = Playback,
                OpenTools = () => _navigator.NavigateTo(AppPage.Tools)
            };
            (_quickLook, _quickLookCards) = (quickLook, cards);
            await _dialogs.ShowAsync(quickLook);
            if (ReferenceEquals(_quickLook, quickLook))
            {
                (_quickLook, _quickLookCards) = (null, []);
            }
        }
    }

    /// <summary>屏幕缩放比变化（跨显示器、系统缩放调整）后按新的像素需求取档。</summary>
    public void SetRenderScaling(double scaling)
    {
        if (double.IsFinite(scaling) && scaling > 0 && Math.Abs(scaling - _renderScaling) >= 0.001)
        {
            _renderScaling = scaling;
            ConfigureThumbnails();
        }
    }

    /// <summary>视口滚动：静止 250ms 后推进缩略图代次，此后入队的请求优先于滚动途中的请求。</summary>
    public void OnViewportScrolled()
    {
        _scrollIdleTimer.Stop();
        _scrollIdleTimer.Start();
    }

    private void OnCardsReplaced()
    {
        // 旧扫描的在途结果作废；新卡片随列表容器的准备逐个进入缩略图队列
        Thumbnails.Reset();
        if (FocusedCard is not null && !Catalog.Cards.Contains(FocusedCard))
        {
            FocusedCard = null;
        }

        ApplyScope();
    }

    private void ApplyScope()
    {
        Selection.SetScope([.. Catalog.Cards.Where(c => JobFactory.IsApplicable(ActionFilter, c))],
            new ScanCounts(Catalog.TotalFiles, Catalog.Cards.Count, Catalog.IgnoredFiles));
        OnPropertyChanged(nameof(HasPhotos));
        OnPropertyChanged(nameof(AllCards));
        OnPropertyChanged(nameof(FilterBannerDesc));
        ApplyDisplayFilter();
    }

    private void ApplyDisplayFilter() => Layout.SetCards(FilterMode switch
    {
        "SuspiciousOnly" => [.. AllCards.Where(c => c.RequiresPairReview)],
        "SelectedOnly" => [.. AllCards.Where(c => c.IsSelected)],
        _ => AllCards
    });

    /// <summary>后台补全可能逐条到达，合并为一次重新筛选与重排。</summary>
    private void PostScopeRefresh()
    {
        if (!_scopeRefreshPosted)
        {
            _scopeRefreshPosted = true;
            Dispatcher.UIThread.Post(() =>
            {
                _scopeRefreshPosted = false;
                ApplyScope();
            }, DispatcherPriority.Background);
        }
    }

    private void ConfigureThumbnails() =>
        Thumbnails.Configure(GalleryMetrics.MaxRowHeight(Layout.ScaleMode), _renderScaling, Layout.SquareCrop);

    private void OnCatalogPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LibraryCatalog.IsScanning))
        {
            OnPropertyChanged(nameof(IsScanning));
        }
        else if (e.PropertyName == nameof(LibraryCatalog.StatusText))
        {
            OnPropertyChanged(nameof(EmptyStateTitle));
            OnPropertyChanged(nameof(EmptyStateSubtitle));
        }
    }

    private void RefreshTexts()
    {
        foreach (var card in Catalog.Cards)
        {
            card.RefreshLocalizedTexts();
        }

        Catalog.RefreshTexts();
        Selection.RefreshTexts();
        Layout.RefreshTexts();
        foreach (var name in (string[])[nameof(EmptyStateTitle), nameof(EmptyStateSubtitle), nameof(SelectAlbumFolderBtnText), nameof(AlbumDirectorySummary), nameof(FilterBannerDesc)])
        {
            OnPropertyChanged(name);
        }
    }
}
