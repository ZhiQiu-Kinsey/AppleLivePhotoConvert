using System.Collections.Specialized;
using Avalonia.Controls;
using LivePhotoConvert.Desktop.Models;

namespace LivePhotoConvert.Desktop.Features.Library.Thumbnails;

/// <summary>
/// 把画廊列表的容器准备/回收映射为行内卡片的 <see cref="IThumbnailPipeline.Acquire"/> / <see cref="IThumbnailPipeline.Release"/>。
/// 虚拟化回收的容器留在可视树里只换内容，不能依赖 OnDetachedFromVisualTree；条目按事件给出的序号取，不依赖 DataContext 的设置时机。
/// </summary>
public sealed class GalleryThumbnailBinder : IDisposable
{
    private readonly ItemsControl _list;
    private readonly IThumbnailPipeline _pipeline;
    private readonly Dictionary<Control, Attachment> _attachments = [];

    public GalleryThumbnailBinder(ItemsControl list, IThumbnailPipeline pipeline)
    {
        _list = list;
        _pipeline = pipeline;
        list.ContainerPrepared += OnContainerPrepared;
        list.ContainerClearing += OnContainerClearing;
        foreach (var container in list.GetRealizedContainers())
        {
            Attach(container, list.ItemFromContainer(container));
        }
    }

    /// <summary>当前由已实例化容器持有的卡片数。</summary>
    public int AttachedCardCount => _attachments.Values.Sum(a => a.Cards.Count);

    public void Dispose()
    {
        _list.ContainerPrepared -= OnContainerPrepared;
        _list.ContainerClearing -= OnContainerClearing;
        foreach (var container in _attachments.Keys.ToList())
        {
            Detach(container);
        }
    }

    private void OnContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        var items = _list.ItemsView;
        var item = (uint)e.Index < (uint)items.Count ? items[e.Index] : e.Container.DataContext;
        Attach(e.Container, item);
    }

    private void OnContainerClearing(object? sender, ContainerClearingEventArgs e) => Detach(e.Container);

    private void Attach(Control container, object? item)
    {
        Detach(container);
        if (item is not PhotoGridRowViewModel row)
        {
            return;
        }

        var attachment = new Attachment(this, row);
        _attachments[container] = attachment;
        attachment.AcquireCurrent();
    }

    private void Detach(Control container)
    {
        if (_attachments.Remove(container, out var attachment))
        {
            attachment.ReleaseAll();
        }
    }

    private sealed class Attachment(GalleryThumbnailBinder owner, PhotoGridRowViewModel row)
    {
        public List<PhotoCardItemViewModel> Cards { get; private set; } = [];

        public void AcquireCurrent()
        {
            Cards = [.. row.Cards];
            foreach (var card in Cards)
            {
                owner._pipeline.Acquire(card);
            }

            row.Cards.CollectionChanged += OnCardsChanged;
        }

        public void ReleaseAll()
        {
            row.Cards.CollectionChanged -= OnCardsChanged;
            foreach (var card in Cards)
            {
                owner._pipeline.Release(card);
            }

            Cards = [];
        }

        /// <summary>行内卡片原地变化：先占住新集合再放开旧集合，仍在行内的卡片计数不会短暂归零。</summary>
        private void OnCardsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            var previous = Cards;
            Cards = [.. row.Cards];
            foreach (var card in Cards)
            {
                owner._pipeline.Acquire(card);
            }

            foreach (var card in previous)
            {
                owner._pipeline.Release(card);
            }
        }
    }
}
