namespace LivePhotoConvert.Core.Media.Thumbnails;

/// <summary>
/// 缩略图高度档位。需求像素按档位向上取整，使不同缩放比与行高共用少量缓存文件。
/// 1536 与 2048 供高分屏上的大图预览与方形裁切的竖图使用，画廊常规行高用不到。
/// </summary>
public static class ThumbnailTiers
{
    /// <summary>输出长边上限：限制超宽全景在最高档位下的像素量。</summary>
    public const int MaxLongEdge = 4096;

    private static readonly int[] HeightTable = [256, 384, 512, 768, 1024, 1536, 2048];

    /// <summary>全部档位高度（升序）。</summary>
    public static ReadOnlySpan<int> Heights => HeightTable;

    public static int Smallest => HeightTable[0];

    public static int Largest => HeightTable[^1];

    /// <summary>不小于需求像素的最小档位；超过最高档位时取最高档位。</summary>
    public static int Select(int requiredPx)
    {
        foreach (var height in HeightTable)
        {
            if (height >= requiredPx)
            {
                return height;
            }
        }

        return Largest;
    }

    public static bool IsTier(int height) => Array.IndexOf(HeightTable, height) >= 0;

    /// <summary>
    /// 转正后的源尺寸在指定档位下的输出尺寸：高度缩到档位、长边不超过上限，只缩小不放大。
    /// </summary>
    public static (int Width, int Height) Fit(int width, int height, int tier)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tier);

        var byTier = (double)tier / height;
        var byLongEdge = (double)MaxLongEdge / Math.Max(width, height);
        if (byTier >= 1 && byLongEdge >= 1)
        {
            return (width, height);
        }

        // 由档位决定时高度取精确值，避免浮点误差让输出比档位少 1 像素
        if (byTier <= byLongEdge)
        {
            return (Math.Max(1, Round(width * byTier)), tier);
        }

        return width >= height
            ? (MaxLongEdge, Math.Max(1, Round(height * byLongEdge)))
            : (Math.Max(1, Round(width * byLongEdge)), MaxLongEdge);
    }

    private static int Round(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);
}
