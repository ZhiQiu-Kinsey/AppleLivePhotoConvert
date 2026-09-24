using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LivePhotoConvert.Desktop.Converters;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Models;

namespace LivePhotoConvert.Desktop.Features.Library.Gallery;

/// <summary>扫描的文件口径：遇到的文件数、产出的条目数、不参与图库的文件数。</summary>
public readonly record struct ScanCounts(int Files, int Items, int Ignored);

/// <summary>
/// 当前动作范围内的卡片、选择状态与各项计数。计数随卡片状态（选中、人工裁决）实时推导；
/// 批量修改与一次点击只触发一次 <see cref="SelectionChanged"/>。
/// </summary>
public sealed partial class GallerySelection(ILocalizer localizer) : ObservableObject
{
    private IReadOnlyList<PhotoCardItemViewModel> _cards = [];
    private int _batchDepth;
    private bool _changedInBatch;
    private long _scopeBytes;

    /// <summary>当前动作范围内的卡片，按扫描顺序。</summary>
    public IReadOnlyList<PhotoCardItemViewModel> Cards => _cards;

    /// <summary>
    /// 选中的卡片；未选中任何卡片时为全部就绪的卡片。待裁决的配对需先裁决或显式选中，
    /// 否则合成时只会被校验跳过。
    /// </summary>
    public IReadOnlyList<PhotoCardItemViewModel> SelectedOrAll
    {
        get
        {
            if (SelectedCount > 0)
            {
                return [.. _cards.Where(c => c.IsSelected)];
            }

            return SuspiciousCount == 0 ? _cards : [.. _cards.Where(c => !c.RequiresPairReview)];
        }
    }

    /// <summary>范围选择的起点：最近一次单击或 Ctrl 单击的卡片。</summary>
    public PhotoCardItemViewModel? Anchor { get; private set; }

    /// <summary>选中集合或范围整体变化后触发（批量修改只触发一次）。</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>用户单独切换了一张卡片的选中状态。</summary>
    public event EventHandler<PhotoCardItemViewModel>? CardToggled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FunnelTotalText), nameof(FunnelTooltipText), nameof(HasScannedFiles))]
    private int _scannedFileCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FunnelItemsText), nameof(FunnelTooltipText))]
    private int _itemCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FunnelIgnoredText), nameof(FunnelTooltipText), nameof(HasIgnoredFiles))]
    private int _ignoredFileCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FunnelReadyText), nameof(FilterAllText), nameof(HasReadyItems))]
    private int _readyCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FunnelSuspiciousText), nameof(FilterAllText), nameof(FilterSuspiciousText), nameof(HasSuspiciousItems))]
    private int _suspiciousCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedBadgeText))]
    private int _selectedCount;

    public bool HasScannedFiles => ScannedFileCount > 0;

    public bool HasReadyItems => ReadyCount > 0;

    public bool HasSuspiciousItems => SuspiciousCount > 0;

    public bool HasIgnoredFiles => IgnoredFileCount > 0;

    public string FunnelTotalText => localizer.Format("FunnelTotalFormat", ScannedFileCount);

    public string FunnelItemsText => localizer.Format("FunnelItemsFormat", ItemCount);

    public string FunnelIgnoredText => localizer.Format("FunnelIgnoredFormat", IgnoredFileCount);

    public string FunnelTooltipText => localizer.Format("FunnelTooltipFormat", ScannedFileCount, ItemCount, IgnoredFileCount);

    public string FunnelReadyText => localizer.Format("FunnelReadyFormat", ReadyCount);

    public string FunnelSuspiciousText => localizer.Format("FunnelSuspiciousFormat", SuspiciousCount);

    // 全部 = 就绪 + 待裁决
    public string FilterAllText => localizer.Format("FilterAllFormat", ReadyCount + SuspiciousCount);

    public string FilterSuspiciousText => localizer.Format("FilterSuspiciousFormat", SuspiciousCount);

    public string SelectedBadgeText => localizer.Format("SelectedBadgeFormat", SelectedCount);

    public string SelectedSummaryText => localizer.Format("SelectedSummaryFormat", _cards.Count, ByteSizeConverter.Format(_scopeBytes));

    /// <summary>更换动作范围（重扫或切换动作）；范围外卡片的选中状态保留，切回时恢复。</summary>
    public void SetScope(IReadOnlyList<PhotoCardItemViewModel> cards, ScanCounts counts)
    {
        foreach (var card in _cards)
        {
            card.PropertyChanged -= OnCardPropertyChanged;
        }

        _cards = cards;
        _scopeBytes = 0;
        foreach (var card in cards)
        {
            card.PropertyChanged += OnCardPropertyChanged;
            _scopeBytes += card.Item.SourceBytes;
        }

        if (Anchor is not null && !cards.Contains(Anchor))
        {
            Anchor = null;
        }

        (ScannedFileCount, ItemCount, IgnoredFileCount) = (counts.Files, counts.Items, counts.Ignored);
        Recount();
        OnPropertyChanged(nameof(Cards));
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 单击卡片：不带修饰键时只选中这一张（已是唯一选中项则取消）；<paramref name="toggle"/>（Ctrl）切换这一张；
    /// <paramref name="range"/>（Shift）按 <paramref name="displayOrder"/> 选中锚点到这一张之间的卡片，同时按 Ctrl 时并入已有选择。
    /// </summary>
    public void Click(PhotoCardItemViewModel card, IReadOnlyList<PhotoCardItemViewModel> displayOrder, bool toggle, bool range) => Batch(() =>
    {
        var anchorIndex = range && Anchor is { } anchor ? IndexOf(displayOrder, anchor) : -1;
        var index = IndexOf(displayOrder, card);
        if (anchorIndex >= 0 && index >= 0)
        {
            var (from, to) = anchorIndex <= index ? (anchorIndex, index) : (index, anchorIndex);
            if (!toggle)
            {
                SetAll(false);
            }

            for (var i = from; i <= to; i++)
            {
                displayOrder[i].IsSelected = true;
            }

            // 范围选择保留锚点，连续 Shift 单击以同一张为起点
            return;
        }

        if (toggle)
        {
            card.IsSelected = !card.IsSelected;
        }
        else
        {
            var onlyThis = card.IsSelected && SelectedCount == 1;
            SetAll(false);
            card.IsSelected = !onlyThis;
        }

        Anchor = card;
    });

    /// <summary>取消范围内全部卡片的选中。</summary>
    public void Clear() => Batch(() => SetAll(false));

    /// <summary>批量设置选中状态，结束后只通知一次。</summary>
    public void SetSelected(IEnumerable<PhotoCardItemViewModel> cards, bool selected) => Batch(() =>
    {
        foreach (var card in cards)
        {
            card.IsSelected = selected;
        }
    });

    /// <summary>在一次批量内修改卡片状态；嵌套调用合并为最外层的一次通知。</summary>
    public void Batch(Action change)
    {
        _batchDepth++;
        try
        {
            change();
        }
        finally
        {
            if (--_batchDepth == 0 && _changedInBatch)
            {
                _changedInBatch = false;
                Recount();
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>语言切换后刷新格式化文案。</summary>
    public void RefreshTexts()
    {
        foreach (var name in (string[])[nameof(FunnelTotalText), nameof(FunnelItemsText), nameof(FunnelIgnoredText), nameof(FunnelTooltipText),
                     nameof(FunnelReadyText), nameof(FunnelSuspiciousText),
                     nameof(FilterAllText), nameof(FilterSuspiciousText), nameof(SelectedBadgeText), nameof(SelectedSummaryText)])
        {
            OnPropertyChanged(name);
        }
    }

    private void Recount()
    {
        int selected = 0, suspicious = 0;
        foreach (var card in _cards)
        {
            selected += card.IsSelected ? 1 : 0;
            suspicious += card.RequiresPairReview ? 1 : 0;
        }

        SelectedCount = selected;
        SuspiciousCount = suspicious;
        ReadyCount = _cards.Count - suspicious;
        OnPropertyChanged(nameof(SelectedSummaryText));
    }

    private void OnCardPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not PhotoCardItemViewModel card ||
            e.PropertyName is not (nameof(PhotoCardItemViewModel.IsSelected) or nameof(PhotoCardItemViewModel.RequiresPairReview) or nameof(PhotoCardItemViewModel.Item)))
        {
            return;
        }

        if (_batchDepth > 0)
        {
            _changedInBatch = true;
            return;
        }

        Recount();
        if (e.PropertyName == nameof(PhotoCardItemViewModel.IsSelected))
        {
            CardToggled?.Invoke(this, card);
        }

        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetAll(bool selected)
    {
        foreach (var card in _cards)
        {
            card.IsSelected = selected;
        }
    }

    private static int IndexOf(IReadOnlyList<PhotoCardItemViewModel> cards, PhotoCardItemViewModel card)
    {
        for (var i = 0; i < cards.Count; i++)
        {
            if (ReferenceEquals(cards[i], card))
            {
                return i;
            }
        }

        return -1;
    }
}
