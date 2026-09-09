using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace LivePhotoConvert.Desktop.Collections;

/// <summary>
/// 支持单次批量重置通知的 ObservableCollection，杜绝大量元素逐个 Add 导致 UI 虚拟化面板连续重排。
/// </summary>
public class BulkObservableCollection<T> : ObservableCollection<T>
{
    private const string CountString = "Count";
    private const string IndexerName = "Item[]";

    public BulkObservableCollection()
    { }

    public BulkObservableCollection(IEnumerable<T> collection) : base(collection) { }

    public BulkObservableCollection(List<T> list) : base(list) { }

    /// <summary>
    /// 清空现有集合并批量重置为指定序列，仅触发一次 Reset 通知与属性更新，
    /// 彻底消除虚拟化面板多次重排排版带来的界面冻结。
    /// </summary>
    public void Reset(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        CheckReentrancy();

        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(CountString));
        OnPropertyChanged(new PropertyChangedEventArgs(IndexerName));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>
    /// 批量追加多个元素，仅触发一次 Reset 通知。
    /// </summary>
    public void AddRange(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        CheckReentrancy();

        bool any = false;
        foreach (var item in items)
        {
            Items.Add(item);
            any = true;
        }

        if (any)
        {
            OnPropertyChanged(new PropertyChangedEventArgs(CountString));
            OnPropertyChanged(new PropertyChangedEventArgs(IndexerName));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
}
