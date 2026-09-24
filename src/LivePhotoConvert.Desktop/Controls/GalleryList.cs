using Avalonia.Controls;
using LivePhotoConvert.Desktop.Models;

namespace LivePhotoConvert.Desktop.Controls;

/// <summary>
/// 画廊列表：等高行以 <see cref="GalleryRowPresenter"/> 为容器，行内的 <see cref="PhotoCardControl"/> 取自本列表的卡片池。
/// </summary>
/// <remarks>
/// 虚拟化面板回收容器时会把它移出逻辑树与可视树，再次挂上要把整棵卡片树的样式重新套用一遍（每张卡片数毫秒）。
/// 卡片的逻辑父级因此固定为本列表，随行容器进出的只有可视树；行换成别的数据时卡片只换数据上下文。
/// </remarks>
public sealed class GalleryList : ListBox
{
    /// <summary>空闲卡片的上限：行高调大、窗口缩小后多出来的卡片不长期占用内存。</summary>
    internal const int MaxIdleCards = 48;

    private static readonly object RowRecycleKey = new();

    private readonly Stack<PhotoCardControl> _idle = [];

    protected override Type StyleKeyOverride => typeof(ListBox);

    /// <summary>当前存在的卡片控件数（行内 + 空闲）。</summary>
    internal int CardControlCount { get; private set; }

    /// <summary>累计新建的卡片控件数。</summary>
    internal int CardControlsCreated { get; private set; }

    internal int IdleCardCount => _idle.Count;

    /// <summary>行容器单独成池，只会被别的行复用，组标题仍用普通列表项。</summary>
    protected override bool NeedsContainerOverride(object? item, int index, out object? recycleKey)
    {
        if (item is PhotoGridRowViewModel)
        {
            recycleKey = RowRecycleKey;
            return true;
        }

        return base.NeedsContainerOverride(item, index, out recycleKey);
    }

    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey) =>
        ReferenceEquals(recycleKey, RowRecycleKey) ? new GalleryRowPresenter(this) : base.CreateContainerForItemOverride(item, index, recycleKey);

    protected override void PrepareContainerForItemOverride(Control container, object? item, int index)
    {
        base.PrepareContainerForItemOverride(container, item, index);
        if (container is GalleryRowPresenter row)
        {
            row.Row = item as PhotoGridRowViewModel;
        }
    }

    protected override void ClearContainerForItemOverride(Control container)
    {
        if (container is GalleryRowPresenter row)
        {
            row.Row = null;
        }

        base.ClearContainerForItemOverride(container);
    }

    /// <summary>
    /// 取一张卡片控件显示 <paramref name="card"/>。数据上下文先于挂上逻辑树设置：
    /// 否则新卡片会先继承列表的数据上下文，编译绑定按卡片类型取值时报错。
    /// </summary>
    internal PhotoCardControl RentCard(PhotoCardItemViewModel card)
    {
        if (_idle.TryPop(out var control))
        {
            if (!ReferenceEquals(control.DataContext, card))
            {
                control.DataContext = card;
            }

            return control;
        }

        control = new PhotoCardControl { DataContext = card };
        LogicalChildren.Add(control);
        CardControlCount++;
        CardControlsCreated++;
        return control;
    }

    /// <summary>
    /// 收回已移出行的卡片控件。空闲卡片保留原数据上下文：下次取用时只触发一轮绑定更新；
    /// 它已不在可视树中，悬浮播放随之停止。
    /// </summary>
    internal void ReturnCard(PhotoCardControl control)
    {
        if (_idle.Count < MaxIdleCards)
        {
            _idle.Push(control);
            return;
        }

        LogicalChildren.Remove(control);
        CardControlCount--;
    }
}
