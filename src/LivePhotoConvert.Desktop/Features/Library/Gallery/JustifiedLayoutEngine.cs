namespace LivePhotoConvert.Desktop.Features.Library.Gallery;

/// <param name="Start">行内第一项在输入中的下标</param>
/// <param name="Count">行内项数</param>
/// <param name="Height">行内图片的统一高度</param>
/// <param name="IsComplete">完整行铺满宽度；末行保持自然高度，可能不满</param>
public readonly record struct JustifiedRow(int Start, int Count, double Height, bool IsComplete);

/// <summary>
/// 等高自适应排版：按顺序把图片分行，每行统一高度、保持原图比例。
/// </summary>
public static class JustifiedLayoutEngine
{
    /// <summary>比例夹取范围：极端全景或长截图也不至于把一行压成细线或撑成一整屏。</summary>
    public const double MinAspect = 0.35;

    public const double MaxAspect = 4.0;

    /// <summary>
    /// 完整行铺满宽度，行高不超过 <paramref name="targetHeight"/> × <paramref name="maxRowHeightFactor"/>；
    /// 末行取 min(目标行高, 铺满所需高度)，不为填满而放大。
    /// </summary>
    /// <param name="aspects">每项的宽高比（宽 / 高）</param>
    /// <param name="width">可用宽度</param>
    /// <param name="targetHeight">目标行高</param>
    /// <param name="spacing">同行相邻项的间距</param>
    /// <param name="maxRowHeightFactor">完整行行高相对目标行高的上限</param>
    /// <param name="itemChrome">每项在图片之外占用的宽度（如卡片左右边距）</param>
    public static JustifiedRow[] Compute(ReadOnlySpan<double> aspects, double width, double targetHeight,
        double spacing, double maxRowHeightFactor = 1.3, double itemChrome = 0)
    {
        if (aspects.IsEmpty)
        {
            return [];
        }

        targetHeight = double.IsFinite(targetHeight) && targetHeight > 0 ? targetHeight : 1;
        width = double.IsFinite(width) && width > 0 ? width : 1;
        var maxHeight = targetHeight * Math.Max(1, maxRowHeightFactor);
        var rows = new List<JustifiedRow>(aspects.Length / 3 + 1);

        for (var start = 0; start < aspects.Length;)
        {
            var remaining = aspects.Length - start;
            var bestCount = 0;
            var bestDistance = double.MaxValue;
            var bestHeight = 0.0;
            var ratioSum = 0.0;
            for (var count = 1; count <= remaining; count++)
            {
                ratioSum += SafeAspect(aspects[start + count - 1]);
                var height = ContentWidth(width, count, spacing, itemChrome) / ratioSum;
                var isLast = count == remaining;

                // 超过上限的完整行会让图片过大；只有用尽剩余项（末行，按自然高度显示）时才接受
                if (height <= maxHeight || isLast)
                {
                    // 按比例衡量偏离（与 |log(h/target)| 同序，免去对数运算）
                    var ratio = Math.Min(height, maxHeight) / targetHeight;
                    var distance = ratio >= 1 ? ratio : 1 / ratio;
                    if (distance < bestDistance || bestCount == 0)
                    {
                        bestDistance = distance;
                        bestCount = count;
                        bestHeight = height;
                    }
                }

                // 行高已低于目标且离目标越来越远，继续加入只会更矮
                if (height <= targetHeight && bestCount > 0 && count > bestCount)
                {
                    break;
                }
            }

            var complete = start + bestCount < aspects.Length;
            rows.Add(new JustifiedRow(start, bestCount, complete ? bestHeight : Math.Min(targetHeight, bestHeight), complete));
            start += bestCount;
        }

        return [.. rows];
    }

    /// <summary>夹取后的比例，排版与卡片尺寸必须用同一个值才能铺满。</summary>
    public static double SafeAspect(double aspect) =>
        double.IsFinite(aspect) && aspect > 0 ? Math.Clamp(aspect, MinAspect, MaxAspect) : 4.0 / 3.0;

    private static double ContentWidth(double width, int count, double spacing, double itemChrome) =>
        Math.Max(1, width - spacing * (count - 1) - itemChrome * count);
}
