using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using LivePhotoConvert.Desktop.Features.Library.Thumbnails;
using LivePhotoConvert.Desktop.Models;

namespace LivePhotoConvert.Desktop.Controls;

/// <summary>
/// 画廊中一行卡片的容器：卡片控件向所属 <see cref="GalleryList"/> 借用，只作为本行的可视子级；
/// 行的卡片变化时按位置改数据上下文，多出的控件归还列表、不足时再借。
/// </summary>
public sealed class GalleryRowPresenter : Control
{
    private readonly GalleryList _owner;
    private readonly List<PhotoCardControl> _cards = [];
    private PhotoGridRowViewModel? _row;

    internal GalleryRowPresenter(GalleryList owner)
    {
        _owner = owner;
        Margin = GalleryMetrics.ItemMargin;
    }

    /// <summary>显示的行；回收时为 null，卡片全部归还。</summary>
    public PhotoGridRowViewModel? Row
    {
        get => _row;
        set
        {
            if (ReferenceEquals(_row, value))
            {
                return;
            }

            if (_row is not null)
            {
                _row.Cards.CollectionChanged -= OnCardsChanged;
            }

            _row = value;
            if (value is not null)
            {
                value.Cards.CollectionChanged += OnCardsChanged;
            }

            Sync();
        }
    }

    /// <summary>行内的卡片控件，按显示顺序。</summary>
    public IReadOnlyList<PhotoCardControl> Cards => _cards;

    private void OnCardsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Sync();

    private void Sync()
    {
        var source = _row?.Cards;
        var count = source?.Count ?? 0;
        for (var i = 0; i < count; i++)
        {
            var card = source![i];
            if (i < _cards.Count)
            {
                if (!ReferenceEquals(_cards[i].DataContext, card))
                {
                    _cards[i].DataContext = card;
                }
            }
            else
            {
                var control = _owner.RentCard(card);
                _cards.Add(control);
                VisualChildren.Add(control);
            }
        }

        for (var i = _cards.Count - 1; i >= count; i--)
        {
            var control = _cards[i];
            _cards.RemoveAt(i);
            VisualChildren.Remove(control);
            _owner.ReturnCard(control);
        }

        InvalidateMeasure();
    }

    /// <summary>卡片宽度由各自绑定的 DisplayWidth 决定，这里只横向依次排列。</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        var constraint = new Size(double.PositiveInfinity, availableSize.Height);
        double width = 0, height = 0;
        foreach (var card in _cards)
        {
            card.Measure(constraint);
            width += card.DesiredSize.Width;
            height = Math.Max(height, card.DesiredSize.Height);
        }

        return new Size(width + Math.Max(0, _cards.Count - 1) * GalleryMetrics.CardSpacing, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0.0;
        foreach (var card in _cards)
        {
            card.Arrange(new Rect(x, 0, card.DesiredSize.Width, finalSize.Height));
            x += card.DesiredSize.Width + GalleryMetrics.CardSpacing;
        }

        return finalSize;
    }
}
