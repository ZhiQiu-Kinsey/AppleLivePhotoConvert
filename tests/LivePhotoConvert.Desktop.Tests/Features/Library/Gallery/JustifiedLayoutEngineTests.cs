using System.Diagnostics;
using LivePhotoConvert.Desktop.Features.Library.Gallery;

namespace LivePhotoConvert.Desktop.Tests.Features.Library.Gallery;

/// <summary>等高自适应排版：完整行铺满、行高有上限、末行自然高度、不拉伸图片。</summary>
/// <remarks>含耗时断言，放入不并行的集合，避免与其它用例争抢 CPU。</remarks>
[Collection(LivePhotoConvert.Desktop.Tests.Harness.ProcessStateCollection.Name)]
public class JustifiedLayoutEngineTests
{
    private const double Spacing = 8;
    private const double Chrome = 8;

    [Theory]
    [InlineData(1056, 180)]
    [InlineData(1056, 250)]
    [InlineData(1056, 320)]
    [InlineData(777.3, 250)]
    [InlineData(2400, 250)]
    public void CompleteRows_FillWidthWithinHalfPixel_AndStayUnderMaxHeight(double width, double target)
    {
        var aspects = Enumerable.Range(0, 200).Select(i => (i % 5) switch
        {
            0 => 3.0 / 4.0,
            1 => 16.0 / 9.0,
            2 => 1.0,
            3 => 9.0 / 16.0,
            _ => 4.0 / 3.0
        }).ToArray();

        var rows = Compute(aspects, width, target);

        Assert.Equal(aspects.Length, rows.Sum(r => r.Count));
        Assert.All(rows.SkipLast(1), row =>
        {
            Assert.True(row.IsComplete);
            Assert.InRange(row.Height, 1, target * 1.3);
            Assert.InRange(Occupied(aspects, row) - width, -0.5, 0.5);
        });
        Assert.False(rows[^1].IsComplete);
        Assert.True(rows[^1].Height <= target);
    }

    [Fact]
    public void Rows_AreContiguousAndOrdered()
    {
        var aspects = Enumerable.Range(0, 57).Select(i => 0.5 + i % 7 * 0.3).ToArray();

        var rows = Compute(aspects, 1200, 250);

        var next = 0;
        foreach (var row in rows)
        {
            Assert.Equal(next, row.Start);
            Assert.True(row.Count > 0);
            next += row.Count;
        }

        Assert.Equal(aspects.Length, next);
    }

    [Fact]
    public void LastRow_KeepsNaturalHeight_InsteadOfStretching()
    {
        double[] aspects = [4.0 / 3.0, 4.0 / 3.0, 4.0 / 3.0, 3.0 / 4.0, 3.0 / 4.0];

        var rows = Compute(aspects, 1056, 250);

        Assert.Equal([3, 2], rows.Select(r => r.Count));
        Assert.Equal(250, rows[1].Height);
        Assert.True(Occupied(aspects, rows[1]) < 1056 / 2.0);
    }

    [Fact]
    public void NarrowViewport_SinglePortraitIsNotStretchedAcrossTheLine()
    {
        var alone = Compute([3.0 / 4.0], 420, 250);
        var row = Assert.Single(alone);
        Assert.False(row.IsComplete);
        Assert.Equal(250, row.Height);
        Assert.True(Occupied([3.0 / 4.0], row) < 420);

        // 后面还有图片时，竖图与下一张共用一行，而不是独占一整行被放大
        double[] mixed = [3.0 / 4.0, 4.0 / 3.0, 4.0 / 3.0];
        var rows = Compute(mixed, 420, 250);
        Assert.Equal(2, rows[0].Count);
        Assert.True(rows[0].Height <= 250 * 1.3);
        Assert.InRange(Occupied(mixed, rows[0]) - 420, -0.5, 0.5);
    }

    [Fact]
    public void EmptyInput_ProducesNoRows() => Assert.Empty(Compute([], 1000, 250));

    [Fact]
    public void ExtremeAspects_AreClamped()
    {
        Assert.Equal(JustifiedLayoutEngine.MaxAspect, JustifiedLayoutEngine.SafeAspect(40));
        Assert.Equal(JustifiedLayoutEngine.MinAspect, JustifiedLayoutEngine.SafeAspect(0.01));
        Assert.Equal(4.0 / 3.0, JustifiedLayoutEngine.SafeAspect(double.NaN));
    }

    [Fact]
    public void WiderViewport_PutsMoreItemsInEachRow()
    {
        var aspects = Enumerable.Range(0, 40).Select(i => i % 2 == 0 ? 3.0 / 4.0 : 4.0 / 3.0).ToArray();

        var narrow = Compute(aspects, 800, 250);
        var wide = Compute(aspects, 1600, 250);

        Assert.True(wide[0].Count > narrow[0].Count);
        Assert.True(wide.Length < narrow.Length);
    }

    [Fact]
    public void TenThousandItems_LayOutWithin20Milliseconds()
    {
        var random = new Random(42);
        var aspects = Enumerable.Range(0, 10_000).Select(_ => 0.4 + random.NextDouble() * 2).ToArray();
        Compute(aspects, 1400, 250);

        var best = TimeSpan.MaxValue;
        for (var i = 0; i < 5; i++)
        {
            var watch = Stopwatch.StartNew();
            var rows = Compute(aspects, 1400, 250);
            watch.Stop();
            Assert.Equal(10_000, rows.Sum(r => r.Count));
            best = watch.Elapsed < best ? watch.Elapsed : best;
        }

        TestContext.Current.TestOutputHelper?.WriteLine($"1 万项排版最佳耗时 {best.TotalMilliseconds:F2} ms");
        Assert.True(best < TimeSpan.FromMilliseconds(20), $"1 万项排版耗时 {best.TotalMilliseconds:F1} ms");
    }

    private static JustifiedRow[] Compute(double[] aspects, double width, double target) =>
        JustifiedLayoutEngine.Compute(aspects, width, target, Spacing, 1.3, Chrome);

    /// <summary>一行实际占用的宽度：卡片（图片 + 边距）与间距之和。</summary>
    private static double Occupied(double[] aspects, JustifiedRow row) =>
        aspects.Skip(row.Start).Take(row.Count).Sum(a => row.Height * JustifiedLayoutEngine.SafeAspect(a) + Chrome) + Spacing * (row.Count - 1);
}
