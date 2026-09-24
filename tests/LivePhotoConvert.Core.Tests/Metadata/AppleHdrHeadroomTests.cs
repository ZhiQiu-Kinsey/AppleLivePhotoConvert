using LivePhotoConvert.Core.Metadata;

namespace LivePhotoConvert.Core.Tests.Metadata;

public class AppleHdrHeadroomTests
{
    /// <summary>期望值来自调研原型 apple2iso.py 的 headroom()。</summary>
    [Theory]
    [InlineData(0.5, 0.005, 3.249009585424942)]        // maker33 < 1，maker48 ≤ 0.01：−20·m48 + 1.8
    [InlineData(0.5, 0.5, 2.9291863946399075)]         // maker33 < 1，maker48 > 0.01：−0.101·m48 + 1.601
    [InlineData(1.2, 0.005, 6.276672783174005)]        // maker33 ≥ 1，maker48 ≤ 0.01：−70·m48 + 3
    [InlineData(1.2, 0.5, 4.442894857742185)]          // maker33 ≥ 1，maker48 > 0.01：−0.303·m48 + 2.303
    [InlineData(1.2, 10.0, 1.0)]                       // 档数为负时截为 0
    [InlineData(0.8432090282, 0, 3.4822022531844965)]  // greyhounds 样片
    [InlineData(1.0255059, 0.001685693743, 7.371720837265519)] // hdr-sample 样片
    public void Compute_MatchesAppleFormula(double maker33, double maker48, double expected)
    {
        Assert.Equal(expected, AppleHdrHeadroom.Compute(maker33, maker48), 1e-12);
    }

    [Theory]
    [InlineData(1.0, 0.01, 2.3)]    // 边界 maker33 = 1 归入 ≥ 1 分支，maker48 = 0.01 归入 ≤ 0.01 分支
    [InlineData(0.9999, 0.01, 1.6)]
    [InlineData(1.0, 0.0100001, 2.3)]
    public void Stops_BranchBoundaries(double maker33, double maker48, double expected)
    {
        Assert.Equal(expected, AppleHdrHeadroom.Stops(maker33, maker48), 4);
    }

    [Fact]
    public void Stops_NonFiniteInput_IsZero()
    {
        Assert.Equal(0, AppleHdrHeadroom.Stops(double.NaN, double.NaN));
        Assert.Equal(1, AppleHdrHeadroom.Compute(1, double.PositiveInfinity));
    }
}
