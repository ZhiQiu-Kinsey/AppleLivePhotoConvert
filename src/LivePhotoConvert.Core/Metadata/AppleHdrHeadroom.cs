namespace LivePhotoConvert.Core.Metadata;

/// <summary>
/// Apple HDR 增益图的余量（HDR 相对 SDR 的最大线性亮度倍数），按 Apple 公开的公式由 MakerNote 33 / 48 计算。
/// </summary>
/// <remarks>
/// 增益图像素 g 的线性增益为 1 + (H − 1)·L(g)，L 为 Rec.709 反 OETF；H 只由这两个标签决定，
/// 文件里没有直接记录，换算为 ISO 21496-1 参数时必须先算出 H。
/// </remarks>
public static class AppleHdrHeadroom
{
    /// <summary>
    /// 余量的档数（log2 H），不小于 0。
    /// </summary>
    /// <param name="maker33">HDRHeadroom（MakerNote 0x21）</param>
    /// <param name="maker48">HDRGain（MakerNote 0x30）</param>
    public static double Stops(double maker33, double maker48)
    {
        double stops;
        if (maker33 < 1.0)
        {
            stops = maker48 <= 0.01 ? -20.0 * maker48 + 1.8 : -0.101 * maker48 + 1.601;
        }
        else
        {
            stops = maker48 <= 0.01 ? -70.0 * maker48 + 3.0 : -0.303 * maker48 + 2.303;
        }

        return double.IsFinite(stops) ? Math.Max(stops, 0.0) : 0.0;
    }

    /// <summary>
    /// 线性余量 H = 2^stops，不小于 1。
    /// </summary>
    public static double Compute(double maker33, double maker48) => Math.Pow(2.0, Stops(maker33, maker48));
}
