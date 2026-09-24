using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using LivePhotoConvert.Desktop.Converters;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Models;

namespace LivePhotoConvert.Desktop.Features.Library.Gallery;

/// <summary>
/// 当前动作范围内的卡片、选择状态与各项计数。计数随卡片状态（选中、人工裁决）实时推导；
/// 批量修改只触发一次 <see cref="SelectionChanged"/>。
/// </summary>
public sealed partial class GallerySelection(ILocalizer localizer) : ObservableObject
{
    private IReadOnlyList<PhotoCardItemViewModel> _cards = [];
    private int _batchDepth;
    private bool _changedInBatch;
    private long _scopeBytes;

    /// <summary>当前动作范围内的卡片，按扫描顺序。</summary>
    public IReadOnlyList<PhotoCardItemViewModel> Cards => _cards;

    /// <summary>选中的卡片；未选中任何卡片时为全部卡片。</summary>
    public IReadOnlyList<PhotoCardItemViewModel> SelectedOrAll
    {
        get
        {
            if (SelectedCount == 0)
            {
                return _cards;
            }

            return [.. _cards.Where(c => c.IsSelected)];
        }
    }

    /// <summary>选中集合或范围整体变化后触发（批量修改只触发一次）。</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>用户单独切换了一张卡片的选中状态。</summary>
    public event EventHandler<PhotoCardItemViewModel>? CardToggled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FunnelTotalText), nameof(HasScannedFiles))]
    private int _totalScannedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FunnelReadyText), nameof(FilterAllText), nameof(HasReadyItems))]
    private int _readyCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FunnelSuspiciousText), nameof(FilterAllText), nameof(FilterSuspiciousText), nameof(HasSuspiciousItems))]
    private int _suspiciousCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FunnelFilteredText), nameof(HasFilteredItems))]
    private int _filteredCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedBadgeText))]
    private int _selectedCount;

    public bool HasScannedFiles => TotalScannedCount > 0;

    public bool HasReadyItems => ReadyCount > 0;

    public bool HasSuspiciousItems => SuspiciousCount > 0;

    public bool HasFilteredItems => FilteredCount > 0;

    public string FunnelTotalText => localizer.Format("FunnelTotalFormat", TotalScannedCount);

    public string FunnelReadyText => localizer.Format("FunnelReadyFormat", ReadyCount);

    public string FunnelSuspiciousText => localizer.Format("FunnelSuspiciousFormat", SuspiciousCount);

    public string FunnelFilteredText => localizer.Format("FunnelFilteredFormat", FilteredCount);

    // 全部 = 就绪 + 待裁决
    public string FilterAllText => localizer.Format("FilterAllFormat", ReadyCount + SuspiciousCount);

    public string FilterSuspiciousText => localizer.Format("FilterSuspiciousFormat", SuspiciousCount);

    public string SelectedBadgeText => localizer.Format("SelectedBadgeFormat", SelectedCount);

    public string SelectedSummaryText => localizer.Format("SelectedSummaryFormat", _cards.Count, FormatBytes(_scopeBytes));

    /// <summary>更换动作范围（重扫或切换动作）；范围外卡片的选中状态保留，切回时恢复。</summary>
    public void SetScope(IReadOnlyList<PhotoCardItemViewModel> cards, int totalFiles)
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

        TotalScannedCount = totalFiles;
        Recount();
        OnPropertyChanged(nameof(Cards));
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

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
        foreach (var name in (string[])[nameof(FunnelTotalText), nameof(FunnelReadyText), nameof(FunnelSuspiciousText), nameof(FunnelFilteredText),
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
        FilteredCount = Math.Max(0, TotalScannedCount - _cards.Count);
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

    private static string FormatBytes(long bytes) =>
        ByteSizeConverter.Instance.Convert(bytes, typeof(string), null, CultureInfo.InvariantCulture) as string ?? $"{bytes} B";
}
