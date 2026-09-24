using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoConvert.Desktop.Collections;
using LivePhotoConvert.Desktop.Features.Library.Thumbnails;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Models;

namespace LivePhotoConvert.Desktop.Features.Library.Gallery;

/// <summary>
/// 画廊排版：排序分组 → 等高分行 → 扁平列表（组标题 + 行）。
/// 行对象按序号复用；列表项序列不变时（例如宽度微调只改变行高）不重置列表，只原地更新尺寸，
/// 列表容器与缩略图引用都保持不动。
/// </summary>
public sealed partial class GalleryLayoutViewModel : ObservableObject
{
    public static readonly TimeSpan WidthDebounce = TimeSpan.FromMilliseconds(120);

    private static readonly string[] SortModes = ["DateTaken", "DateCreated", "DateModified", "Name"];
    private static readonly string[] GroupingModes = ["Date", "Month", "Year", "None"];
    private static readonly string[] ScaleModes = ["Medium", "Small", "Large"];
    private static readonly string[] CropModes = ["Natural", "Square"];

    private readonly ILocalizer _localizer;
    private readonly SettingsStore? _settings;
    private readonly DispatcherTimer _widthTimer;
    private readonly Dictionary<string, TimelineHeaderItemViewModel> _headers = [];
    private readonly HashSet<string> _collapsed = [];
    private readonly List<PhotoGridRowViewModel> _rowPool = [];
    private readonly Dictionary<string, int> _indexByKey = new(StringComparer.Ordinal);
    private IReadOnlyList<PhotoCardItemViewModel> _cards = [];
    private IReadOnlyList<GalleryGroup> _groups = [];
    private List<PhotoCardItemViewModel> _displayedCards = [];
    private double[] _offsets = [];
    private double _pendingWidth;

    /// <param name="settings">视图偏好（排序、分组、缩放、裁切）的来源与保存位置；为 null 时只在内存中生效</param>
    public GalleryLayoutViewModel(ILocalizer localizer, SettingsStore? settings = null)
    {
        _localizer = localizer;
        _settings = settings;
        var gallery = settings?.Current.Gallery ?? new GalleryPreferences();
        _sortMode = OneOf(gallery.SortMode, SortModes);
        _isSortAscending = gallery.SortAscending;
        _groupingMode = OneOf(gallery.Grouping, GroupingModes);
        _scaleMode = OneOf(gallery.Scale, ScaleModes);
        _cropMode = OneOf(gallery.Crop, CropModes);
        _widthTimer = new DispatcherTimer { Interval = WidthDebounce };
        _widthTimer.Tick += (_, _) =>
        {
            _widthTimer.Stop();
            ApplyViewportWidth(_pendingWidth);
        };
    }

    /// <summary>扁平列表：组标题与行。</summary>
    public BulkObservableCollection<IGalleryDisplayItem> Items { get; } = [];

    /// <summary>当前排出的卡片（不含折叠分组），按显示顺序。</summary>
    public IReadOnlyList<PhotoCardItemViewModel> DisplayedCards => _displayedCards;

    /// <summary>DateTaken / DateCreated / DateModified / Name。</summary>
    [ObservableProperty]
    private string _sortMode;

    [ObservableProperty]
    private bool _isSortAscending;

    /// <summary>Date / Month / Year / None。</summary>
    [ObservableProperty]
    private string _groupingMode;

    /// <summary>Small / Medium / Large，对应目标行高。</summary>
    [ObservableProperty]
    private string _scaleMode;

    /// <summary>Natural（原图比例）/ Square（方形裁切）。</summary>
    [ObservableProperty]
    private string _cropMode;

    public GallerySort Sort => GalleryOrdering.ParseSort(SortMode);

    public GalleryGrouping Grouping => GalleryOrdering.ParseGrouping(GroupingMode);

    public bool SquareCrop => CropMode == "Square";

    /// <summary>已应用的视口宽度；防抖期间的新宽度尚未生效。</summary>
    public double ViewportWidth { get; private set; }

    /// <summary>全部列表项的总高度（与列表的实际布局一致）。</summary>
    public double ExtentHeight => _offsets.Length == 0 ? 0 : _offsets[^1];

    /// <summary>重排即将开始，视图据此记录滚动锚点。</summary>
    public event EventHandler? LayoutChanging;

    /// <summary>重排完成，视图据此恢复滚动锚点。</summary>
    public event EventHandler? LayoutChanged;

    public void SetCards(IReadOnlyList<PhotoCardItemViewModel> cards)
    {
        _cards = cards;
        Rebuild(regroup: true);
    }

    [RelayCommand]
    public void SetSortMode(string? mode)
    {
        SortMode = OneOf(mode, SortModes);
        Save(g => g.SortMode = SortMode);
        Rebuild(regroup: true);
    }

    [RelayCommand]
    public void SetSortDirection(object? ascending)
    {
        IsSortAscending = CommandParameters.ToBool(ascending, IsSortAscending);
        Save(g => g.SortAscending = IsSortAscending);
        Rebuild(regroup: true);
    }

    [RelayCommand]
    public void SetGroupingMode(string? grouping)
    {
        GroupingMode = OneOf(grouping, GroupingModes);
        Save(g => g.Grouping = GroupingMode);
        Rebuild(regroup: true);
    }

    [RelayCommand]
    public void SetScaleMode(string? scale)
    {
        ScaleMode = OneOf(scale, ScaleModes);
        Save(g => g.Scale = ScaleMode);
        Rebuild(regroup: false);
    }

    [RelayCommand]
    public void SetCropMode(string? crop)
    {
        CropMode = OneOf(crop, CropModes);
        Save(g => g.Crop = CropMode);
        Rebuild(regroup: false);
    }

    /// <summary>窗口缩放连续触发时只在停下 <see cref="WidthDebounce"/> 后重排一次；首次测量立即生效。</summary>
    public void SetViewportWidth(double width)
    {
        if (!double.IsFinite(width) || width <= 0)
        {
            return;
        }

        if (ViewportWidth <= 0)
        {
            ApplyViewportWidth(width);
            return;
        }

        _pendingWidth = width;
        _widthTimer.Stop();
        _widthTimer.Start();
    }

    /// <summary>立即按新宽度重排（跳过防抖）。</summary>
    public void ApplyViewportWidth(double width)
    {
        _widthTimer.Stop();
        if (!double.IsFinite(width) || width <= 0 || Math.Abs(width - ViewportWidth) < 0.25)
        {
            return;
        }

        ViewportWidth = width;
        Rebuild(regroup: false);
    }

    public void ToggleCollapse(TimelineHeaderItemViewModel? header)
    {
        if (header is null)
        {
            return;
        }

        if (!_collapsed.Remove(header.Key))
        {
            _collapsed.Add(header.Key);
        }

        Rebuild(regroup: false);
    }

    /// <summary>有展开的分组时全部折叠，否则全部展开。</summary>
    public void ToggleAllCollapse()
    {
        var collapse = _groups.Any(g => !_collapsed.Contains(g.Key));
        foreach (var group in _groups)
        {
            if (collapse)
            {
                _collapsed.Add(group.Key);
            }
            else
            {
                _collapsed.Remove(group.Key);
            }
        }

        Rebuild(regroup: false);
    }

    public TimelineHeaderItemViewModel? HeaderFor(string groupKey) => _headers.GetValueOrDefault(groupKey);

    /// <summary>分组内的卡片（含折叠分组）。</summary>
    public IReadOnlyList<PhotoCardItemViewModel> CardsOf(TimelineHeaderItemViewModel? header) =>
        _groups.FirstOrDefault(g => g.Key == header?.Key)?.Cards ?? [];

    /// <summary>语言切换后重新格式化分组标题与地点。</summary>
    public void RefreshTexts()
    {
        foreach (var group in _groups)
        {
            if (_headers.TryGetValue(group.Key, out var header))
            {
                ApplyHeaderTexts(header, group);
            }
        }
    }

    /// <summary>纵向偏移处的列表项对应的锚点键：行取首张卡片的键（卡片换行后仍能找到），组标题取分组键。</summary>
    public string? AnchorKeyAt(double offsetY)
    {
        var index = IndexAt(offsetY);
        return index < 0 ? null : Items[index] switch
        {
            PhotoGridRowViewModel { Cards.Count: > 0 } row => row.Cards[0].Key,
            var item => item.Key
        };
    }

    /// <summary>锚点键（卡片键或分组键）所在列表项的顶部偏移；不存在时为 <see cref="double.NaN"/>。</summary>
    public double OffsetOf(string key) => IndexOf(key) is var index and >= 0 ? _offsets[index] : double.NaN;

    /// <summary>锚点键所在列表项的序号；不存在时为 -1。</summary>
    public int IndexOf(string key) => _indexByKey.GetValueOrDefault(key, -1);

    /// <summary>纵向偏移处的列表项序号。</summary>
    public int IndexAt(double offsetY)
    {
        if (Items.Count == 0)
        {
            return -1;
        }

        // _offsets[i] 为第 i 项顶部，末尾多一个总高度
        var index = Array.BinarySearch(_offsets, 0, Items.Count, Math.Max(0, offsetY));
        index = index >= 0 ? index : ~index - 1;
        return Math.Clamp(index, 0, Items.Count - 1);
    }

    private void Rebuild(bool regroup)
    {
        LayoutChanging?.Invoke(this, EventArgs.Empty);
        if (regroup)
        {
            _groups = GalleryOrdering.Group(GalleryOrdering.Sort(_cards, Sort, IsSortAscending), Grouping, Sort, IsSortAscending);
        }

        var items = new List<IGalleryDisplayItem>(Items.Count + 8);
        var displayed = new List<PhotoCardItemViewModel>(_cards.Count);
        var width = GalleryMetrics.LayoutWidth(ViewportWidth > 0 ? ViewportWidth : GalleryMetrics.DefaultViewportWidth);
        var target = GalleryMetrics.TargetRowHeight(ScaleMode);
        var rowOrdinal = 0;
        var aspects = new List<double>();
        foreach (var group in _groups)
        {
            if (Grouping != GalleryGrouping.None)
            {
                var header = HeaderFor(group);
                header.IsCollapsed = _collapsed.Contains(group.Key);
                items.Add(header);
                if (header.IsCollapsed)
                {
                    continue;
                }
            }

            aspects.Clear();
            foreach (var card in group.Cards)
            {
                aspects.Add(SquareCrop ? 1 : JustifiedLayoutEngine.SafeAspect(card.AspectRatio));
            }

            var rows = JustifiedLayoutEngine.Compute(
                CollectionsMarshal.AsSpan(aspects), width, target,
                GalleryMetrics.CardSpacing, GalleryMetrics.MaxRowHeightFactor, GalleryMetrics.CardHorizontalChrome);
            foreach (var row in rows)
            {
                var rowVm = RowAt(rowOrdinal++);
                for (var i = 0; i < row.Count; i++)
                {
                    var card = group.Cards[row.Start + i];
                    card.PreviewHeight = row.Height;
                    card.DisplayWidth = row.Height * aspects[row.Start + i] + GalleryMetrics.CardHorizontalChrome;
                    displayed.Add(card);
                }

                rowVm.RowHeight = row.Height;
                SyncCards(rowVm.Cards, group.Cards, row.Start, row.Count);
                items.Add(rowVm);
            }
        }

        // 多余的行对象不再持有卡片
        if (_rowPool.Count > rowOrdinal)
        {
            _rowPool.RemoveRange(rowOrdinal, _rowPool.Count - rowOrdinal);
        }

        if (!items.SequenceEqual(Items))
        {
            Items.Reset(items);
        }

        _displayedCards = displayed;
        IndexItems();
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private TimelineHeaderItemViewModel HeaderFor(GalleryGroup group)
    {
        if (!_headers.TryGetValue(group.Key, out var header))
        {
            _headers[group.Key] = header = new TimelineHeaderItemViewModel(group.Key, group.Period);
        }

        header.PhotoCount = group.Cards.Count;
        ApplyHeaderTexts(header, group);
        return header;
    }

    private void ApplyHeaderTexts(TimelineHeaderItemViewModel header, GalleryGroup group)
    {
        header.Title = GalleryOrdering.FormatTitle(_localizer, Grouping, group.Period);
        header.LocationSummary = Grouping == GalleryGrouping.Day && group.Cards.Count > 0 ? group.Cards[0].LocationSummary : string.Empty;
    }

    private PhotoGridRowViewModel RowAt(int ordinal)
    {
        if (ordinal == _rowPool.Count)
        {
            _rowPool.Add(new PhotoGridRowViewModel($"row_{ordinal}"));
        }

        return _rowPool[ordinal];
    }

    /// <summary>逐位替换：未变化的位置不产生通知，仍在行内的卡片引用计数不会归零。</summary>
    private static void SyncCards(ObservableCollection<PhotoCardItemViewModel> target, IReadOnlyList<PhotoCardItemViewModel> source, int start, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var card = source[start + i];
            if (i < target.Count)
            {
                if (!ReferenceEquals(target[i], card))
                {
                    target[i] = card;
                }
            }
            else
            {
                target.Add(card);
            }
        }

        while (target.Count > count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }

    private void IndexItems()
    {
        _indexByKey.Clear();
        _offsets = new double[Items.Count + 1];
        var y = 0.0;
        for (var i = 0; i < Items.Count; i++)
        {
            _offsets[i] = y;
            switch (Items[i])
            {
                case PhotoGridRowViewModel row:
                    foreach (var card in row.Cards)
                    {
                        _indexByKey[card.Key] = i;
                    }

                    y += GalleryMetrics.RowExtent(row.RowHeight);
                    break;
                case var header:
                    _indexByKey[header.Key] = i;
                    y += GalleryMetrics.GroupHeaderExtent;
                    break;
            }
        }

        _offsets[Items.Count] = y;
    }

    private void Save(Action<GalleryPreferences> change) => _settings?.Update(s => change(s.Gallery));

    /// <summary>设置文件里无法识别的取值回退到第一项（默认值）。</summary>
    private static string OneOf(string? value, string[] allowed) =>
        value is not null && allowed.Contains(value, StringComparer.Ordinal) ? value : allowed[0];
}
