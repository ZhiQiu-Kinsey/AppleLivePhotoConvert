using LivePhotoConvert.Core.Pipeline;
using LivePhotoConvert.Desktop.Features.Library;
using LivePhotoConvert.Desktop.Features.Tasks;
using LivePhotoConvert.Desktop.Infrastructure;
using Microsoft.Extensions.Time.Testing;

namespace LivePhotoConvert.Desktop.Tests.Features.Tasks;

public class RunningTaskViewModelTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly Localizer _localizer = new();

    private RunningTaskViewModel Create(int items = 10) =>
        new(Jobs.Files(ConversionAction.Extract, "out", [.. Enumerable.Range(0, items).Select(i => $"{i}.jpg")]), _localizer, _time);

    [Fact]
    public void Initially_ShowsTitleTotalsAndEstimatingState()
    {
        var vm = Create();

        Assert.Equal(_localizer["TaskTitleExtract"], vm.Title);
        Assert.Equal(0, vm.Completed);
        Assert.Equal(10, vm.Total);
        Assert.Equal("0%", vm.PercentText);
        Assert.Null(vm.ItemsPerSecond);
        Assert.Null(vm.Remaining);
        Assert.Equal("—", vm.ThroughputText);
        Assert.Equal(_localizer["TaskRemainingEstimating"], vm.RemainingText);
        Assert.Equal(_localizer.Format("TaskProgressRatioFormat", 0, 10), vm.RatioText);
    }

    [Fact]
    public void Report_ComputesThroughputFromElapsedTime_AndWaitsForEnoughSamplesBeforeEstimating()
    {
        var vm = Create();

        _time.Advance(TimeSpan.FromSeconds(2));
        vm.Report(new BatchProgress(2, 10, "b.jpg"));

        Assert.Equal(1.0, vm.ItemsPerSecond!.Value, 3);
        Assert.Null(vm.Remaining);
        Assert.Equal(_localizer["TaskRemainingEstimating"], vm.RemainingText);
        Assert.Equal("b.jpg", vm.CurrentFile);
        Assert.Equal(20, vm.Percent);

        _time.Advance(TimeSpan.FromSeconds(2));
        vm.Report(new BatchProgress(4, 10, "d.jpg"));

        Assert.Equal(1.0, vm.ItemsPerSecond!.Value, 3);
        Assert.Equal(TimeSpan.FromSeconds(6), vm.Remaining);
        Assert.Equal(_localizer.Format("TaskRemainingFormat", "00:06"), vm.RemainingText);
        Assert.Equal(_localizer.Format("TaskThroughputFormat", 1.0), vm.ThroughputText);
        Assert.Equal("40%", vm.PercentText);
    }

    [Fact]
    public void PausedTime_IsExcludedFromRateAndEstimate()
    {
        var vm = Create();
        _time.Advance(TimeSpan.FromSeconds(3));
        vm.Report(new BatchProgress(3, 10, "c.jpg"));

        vm.SetPaused(true);
        Assert.True(vm.IsPaused);
        _time.Advance(TimeSpan.FromMinutes(5));
        vm.SetPaused(false);
        _time.Advance(TimeSpan.FromSeconds(2));
        vm.Report(new BatchProgress(5, 10, "e.jpg"));

        Assert.Equal(TimeSpan.FromSeconds(5), vm.ActiveElapsed);
        Assert.Equal(1.0, vm.ItemsPerSecond!.Value, 3);
        Assert.Equal(TimeSpan.FromSeconds(5), vm.Remaining);
    }

    [Fact]
    public void ActiveElapsed_StopsWhilePaused()
    {
        var vm = Create();
        _time.Advance(TimeSpan.FromSeconds(4));
        vm.SetPaused(true);
        _time.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(TimeSpan.FromSeconds(4), vm.ActiveElapsed);
    }

    [Fact]
    public void Report_ClampsOutOfRangeValues()
    {
        var vm = Create();
        _time.Advance(TimeSpan.FromSeconds(1));

        vm.Report(new BatchProgress(15, 10, null!));

        Assert.Equal(10, vm.Completed);
        Assert.Equal(100, vm.Percent);
        Assert.Equal(string.Empty, vm.CurrentFile);
        Assert.Equal(TimeSpan.Zero, vm.Remaining);

        vm.Report(new BatchProgress(-1, 0, "x"));
        Assert.Equal(0, vm.Completed);
        Assert.Equal(0, vm.Percent);
    }

    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(0.2, "00:01")]
    [InlineData(65, "01:05")]
    [InlineData(3599.5, "1:00:00")]
    [InlineData(3725, "1:02:05")]
    public void Duration_FormatsMinutesAndHours(double seconds, string expected) =>
        Assert.Equal(expected, TaskTexts.Duration(TimeSpan.FromSeconds(seconds)));
}
