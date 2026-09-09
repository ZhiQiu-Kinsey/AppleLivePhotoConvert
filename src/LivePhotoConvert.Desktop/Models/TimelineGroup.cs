using System.Collections.ObjectModel;

namespace LivePhotoConvert.Desktop.Models;

/// <summary>
/// 时间线分组数据结构，支持动态生成多列瀑布流块及折叠/展开
/// </summary>
public sealed class TimelineGroup
{
    public required TimelineHeaderItemViewModel Header { get; init; }
    public List<PhotoCardItemViewModel> AllCards { get; init; } = [];

    public List<PhotoGridRowViewModel> BuildRows(int columnCount, bool filterVisibleOnly)
    {
        List<PhotoGridRowViewModel> blocks = [];
        var candidateCards = filterVisibleOnly
            ? AllCards.Where(c => c.IsVisible).ToList()
            : AllCards;

        if (candidateCards.Count == 0) return blocks;

        int cols = Math.Clamp(columnCount, 2, 4);
        // 每个瀑布流块包含适量的卡片，既实现多列纵向无缝咬合，又保留 ListBox 虚拟化性能
        int chunkSize = Math.Max(cols * 6, 18);

        int blockIdx = 0;
        for (int i = 0; i < candidateCards.Count; i += chunkSize)
        {
            var chunk = candidateCards.Skip(i).Take(chunkSize).ToList();
            PhotoGridRowViewModel block = new()
            {
                Key = $"{Header.Key}_masonry_{blockIdx++}",
                Cards = new ObservableCollection<PhotoCardItemViewModel>(chunk),
                Columns = cols
            };

            // 维护每列当前的预估高度，使用贪心算法将每张卡片放入当前累计高度最小的列
            double[] colHeights = new double[cols];
            var colLists = new[] { block.Column0, block.Column1, block.Column2, block.Column3 };

            foreach (var card in chunk)
            {
                // 找出当前高度最小的列
                int minCol = 0;
                double minH = colHeights[0];
                for (int c = 1; c < cols; c++)
                {
                    if (colHeights[c] < minH)
                    {
                        minH = colHeights[c];
                        minCol = c;
                    }
                }

                colLists[minCol].Add(card);

                // 依据卡片画幅模式与长宽比预估卡片高度
                double cardH = 260.0;
                if (card.IsSquareCrop)
                {
                    cardH = 278.0;
                }
                else if (card.AspectRatio > 0)
                {
                    // 估算高度 = 宽度 / AspectRatio + 78px 信息栏
                    cardH = Math.Clamp((260.0 / card.AspectRatio) + 78.0, 160.0, 480.0);
                }
                colHeights[minCol] += cardH + 16.0;
            }

            blocks.Add(block);
        }

        return blocks;
    }
}
