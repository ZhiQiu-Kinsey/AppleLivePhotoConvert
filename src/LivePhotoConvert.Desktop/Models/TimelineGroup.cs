using System.Collections.ObjectModel;
using LivePhotoConvert.Desktop.Features.Library.Thumbnails;

namespace LivePhotoConvert.Desktop.Models;

/// <summary>
/// 时间线分组数据结构，负责生成稳定测量尺寸的自适应等高图片行。
/// </summary>
public sealed class TimelineGroup
{
    public required TimelineHeaderItemViewModel Header { get; init; }
    public List<PhotoCardItemViewModel> AllCards { get; init; } = [];

    public List<PhotoGridRowViewModel> BuildRows(string scaleMode, bool filterVisibleOnly, double parentWidth = 900)
    {
        List<PhotoGridRowViewModel> rows = [];
        var candidateCards = filterVisibleOnly
            ? AllCards.Where(c => c.IsVisible).ToList()
            : AllCards;

        if (candidateCards.Count == 0) return rows;

        const double rowGap = GalleryMetrics.CardSpacing;
        const double cardMargin = GalleryMetrics.CardHorizontalChrome;
        double available = Math.Max(260, parentWidth - 24);
        double targetHeight = GalleryMetrics.TargetRowHeight(scaleMode);

        for (int offset = 0, rowIndex = 0; offset < candidateCards.Count; rowIndex++)
        {
            int count = FindBestRowLength(candidateCards, offset, available, targetHeight, rowGap, cardMargin);
            bool isCompleteRow = offset + count < candidateCards.Count;
            double rowHeight = CalculateRowHeight(
                candidateCards, offset, count, available, targetHeight, rowGap, cardMargin, isCompleteRow);
            var rowCards = candidateCards.GetRange(offset, count);
            foreach (var card in rowCards)
            {
                card.PreviewHeight = rowHeight;
                card.DisplayWidth = rowHeight * SafeAspect(card) + cardMargin;
            }

            rows.Add(new PhotoGridRowViewModel
            {
                Key = $"{Header.Key}_row_{rowIndex}",
                Cards = new ObservableCollection<PhotoCardItemViewModel>(rowCards),
                RowHeight = rowHeight
            });
            offset += count;
        }

        return rows;
    }

    /// <summary>
    /// 选择使实际行高最接近目标行高的断点。允许多放一张后整体略微缩小，
    /// 避免窄视口中的单张竖图被横向拉伸成整行。
    /// </summary>
    private static int FindBestRowLength(IReadOnlyList<PhotoCardItemViewModel> cards, int offset,
        double available, double targetHeight, double rowGap, double cardMargin)
    {
        int remaining = cards.Count - offset;
        int bestCount = 1;
        double ratioSum = 0;
        double bestDistance = double.MaxValue;

        for (int count = 1; count <= remaining; count++)
        {
            ratioSum += SafeAspect(cards[offset + count - 1]);
            double contentWidth = AvailableImageWidth(available, count, rowGap, cardMargin);
            double candidateHeight = contentWidth / ratioSum;
            double distance = Math.Abs(Math.Log(candidateHeight / targetHeight));

            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestCount = count;
            }

            // 行高已经低于目标且误差开始增大；继续加入只会让图片更矮。
            if (candidateHeight <= targetHeight && count > bestCount)
            {
                break;
            }
        }

        return bestCount;
    }

    private static double CalculateRowHeight(IReadOnlyList<PhotoCardItemViewModel> cards, int offset, int count,
        double available, double targetHeight, double rowGap, double cardMargin, bool isCompleteRow)
    {
        double ratioSum = 0;
        for (int i = 0; i < count; i++)
        {
            ratioSum += SafeAspect(cards[offset + i]);
        }

        double justifiedHeight = AvailableImageWidth(available, count, rowGap, cardMargin) / Math.Max(0.1, ratioSum);

        // 完整行通过统一调整行高铺满宽度，卡片始终保持原图比例；末行不为填满而放大。
        return isCompleteRow ? justifiedHeight : Math.Min(targetHeight, justifiedHeight);
    }

    private static double AvailableImageWidth(double available, int count, double rowGap, double cardMargin) =>
        Math.Max(1, available - rowGap * (count - 1) - cardMargin * count);

    private static double SafeAspect(PhotoCardItemViewModel card) =>
        card.IsSquareCrop ? 1 :
        (double.IsFinite(card.AspectRatio) && card.AspectRatio > 0.1
            ? Math.Clamp(card.AspectRatio, 0.35, 4.0)
            : 4.0 / 3.0);
}
