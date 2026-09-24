using LivePhotoConvert.Core.External.Tools;
using LivePhotoConvert.Core.Pipeline;
using LivePhotoConvert.Desktop.Features.Library;
using LivePhotoConvert.Desktop.Features.Tools;
using LivePhotoConvert.Desktop.Tests.Features.Tasks;

namespace LivePhotoConvert.Desktop.Tests.Features.Tools;

/// <summary>任务运行期间禁止安装：占用状态跟随任务中心。</summary>
public class TaskCenterToolUsageTests
{
    [Fact]
    public async Task IsBusy_FollowsRunningTaskAndRaisesChanges()
    {
        var release = new TaskCompletionSource<BatchReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new TaskCenterFixture(new ScriptedRunner((_, _, _) => release.Task));
        var released = new List<ToolId>();
        var usage = new TaskCenterToolUsage(fixture.Center, tool =>
        {
            released.Add(tool);
            return Task.CompletedTask;
        });
        var changes = 0;
        usage.BusyChanged += (_, _) => changes++;

        Assert.False(usage.IsBusy);
        var run = fixture.Center.RunAsync(Jobs.Files(ConversionAction.Extract, "/out", "/in/a.jpg"));
        Assert.True(usage.IsBusy);
        Assert.Equal(1, changes);

        release.SetResult(Jobs.Report());
        await run.Within();
        Assert.False(usage.IsBusy);
        Assert.Equal(2, changes);

        await usage.ReleaseIdleProcessesAsync(ToolId.Ffmpeg);
        Assert.Equal([ToolId.Ffmpeg], released);
    }
}
