using Avalonia;
using Avalonia.Media.Imaging;

namespace LivePhotoConvert.Desktop.Controls;

/// <summary>
/// 按原图像素区域提供两侧的细节位图，供对比控件的放大镜与放大视图使用；
/// 显示用的整图只有屏幕分辨率，细节必须回到原图按需裁切。
/// </summary>
public interface ICompareDetailSource
{
    /// <summary>原图像素尺寸（已按方向摆正），也是区域坐标所在的空间。</summary>
    PixelSize SourceSize { get; }

    /// <param name="region">原图像素区域</param>
    /// <param name="maxOutputSize">输出不超过此尺寸；区域更大时等比缩小</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>两侧同一区域的位图；来源已释放时返回 <c>null</c></returns>
    Task<CompareDetail?> GetDetailAsync(PixelRect region, PixelSize maxOutputSize, CancellationToken cancellationToken);
}

/// <summary>两侧同一原图区域的细节位图；由取得它的一方负责释放。</summary>
public sealed class CompareDetail(PixelRect region, Bitmap before, Bitmap after) : IDisposable
{
    public PixelRect Region { get; } = region;

    public Bitmap Before { get; } = before;

    public Bitmap After { get; } = after;

    public void Dispose()
    {
        Before.Dispose();
        After.Dispose();
    }
}
