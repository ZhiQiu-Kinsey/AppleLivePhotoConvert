using LivePhotoConvert.Core.Pairing;
using LivePhotoConvert.Core.Pipeline;
using LivePhotoConvert.Core.Services;
using LivePhotoConvert.Desktop.Features.Library;
using LivePhotoConvert.Desktop.Features.Tasks;
using LivePhotoConvert.Desktop.Models;
using LivePhotoConvert.Desktop.ViewModels;
using LivePhotoConvert.E2E.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace LivePhotoConvert.E2E.Tier1_FeatureCoverage.Tasks;

/// <summary>页面只负责把界面状态固定成任务；执行由任务中心承担。</summary>
public class PageJobAssemblyTests
{
    private static PhotoCardItemViewModel ApplePair(string name, bool forced = false) => new()
    {
        Key = name,
        PhotoPath = $"/album/{name}.heic",
        VideoPath = $"/album/{name}.mov",
        IsForceAccepted = forced
    };

    private static PhotoCardItemViewModel MotionPhoto(string name) => new()
    {
        Key = name,
        PhotoPath = $"/album/{name}.jpg",
        IsMotionPhoto = true
    };

    [Fact]
    public void ConvertPage_ToAndroid_SnapshotsPairsForcedPairsAndOptions()
    {
        using var host = new DesktopTestHost();
        host.Settings.Update(s =>
        {
            s.Concurrency = 20;
            s.FfmpegPath = "/opt/ffmpeg";
        });
        var vm = host.Get<ConvertViewModel>();
        vm.OutputDirectory = "/export";
        vm.NamingFormat = 2;
        vm.AutoAppendIndex = false;
        vm.SourceAction = 1;
        var forced = ApplePair("IMG_2", forced: true);

        var job = vm.BuildJob([ApplePair("IMG_1"), forced, MotionPhoto("MVIMG_3")]);

        Assert.Equal(ConversionAction.ToAndroid, job.Action);
        Assert.Equal([new MediaPair("/album/IMG_1.heic", "/album/IMG_1.mov"), new MediaPair("/album/IMG_2.heic", "/album/IMG_2.mov")], job.Inputs.Pairs);
        Assert.Equal([new MediaPair("/album/IMG_2.heic", "/album/IMG_2.mov")], job.Inputs.ForceAccepted);
        Assert.Empty(job.Inputs.Files);
        Assert.Equal("/export", job.Options.Output?.Directory);
        Assert.Equal(ConflictPolicy.Overwrite, job.Options.Output?.Conflict);
        Assert.Equal(MergeNamingFormat.XiaomiClean, job.Options.Naming);
        Assert.Equal(SourceFileAction.Move, job.Options.SourceAction);
        Assert.Equal(8, job.Parallelism);
        Assert.Equal("/opt/ffmpeg", job.Tools.Ffmpeg);
        Assert.Null(job.Tools.ExifTool);
    }

    [Theory]
    [InlineData(1, ConversionAction.ToApple)]
    [InlineData(2, ConversionAction.Extract)]
    public async Task ConvertPage_SplitDirections_UseOnlyMotionPhotos(int direction, ConversionAction expected)
    {
        using var host = new DesktopTestHost();
        var vm = host.Get<ConvertViewModel>();
        await vm.SetDirection(direction);
        vm.HeicQuality = 70;

        var job = vm.BuildJob([ApplePair("IMG_1"), MotionPhoto("MVIMG_3")]);

        Assert.Equal(expected, job.Action);
        Assert.Equal(["/album/MVIMG_3.jpg"], job.Inputs.Files);
        Assert.Empty(job.Inputs.Pairs);
        Assert.Equal(70, job.Options.HeicQuality);
    }

    [Fact]
    public async Task StartButtons_AreDisabledWhileTaskCenterRuns()
    {
        var release = new TaskCompletionSource<BatchReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = new DesktopTestHost(services =>
            services.AddSingleton<IConversionRunner>(new ScriptedRunner((_, _, _) => release.Task)));
        var convert = host.Get<ConvertViewModel>();
        var strip = host.Get<StripViewModel>();
        var center = host.Get<TaskCenter>();
        strip.CurrentInputPathText = host.Directory;
        Assert.True(convert.TriggerBatchConvertCommand.CanExecute(null));
        Assert.True(strip.StartStripExecutionCommand.CanExecute(null));

        var run = center.RunAsync(Jobs.Files(ConversionAction.Extract, host.Directory, "/in/a.jpg"));

        Assert.False(convert.TriggerBatchConvertCommand.CanExecute(null));
        Assert.False(strip.StartStripExecutionCommand.CanExecute(null));
        Assert.Equal(host.Localizer["StageStatusRunning"], strip.StageStatusText);

        release.SetResult(Jobs.Report());
        await run.Within();
        Assert.True(convert.TriggerBatchConvertCommand.CanExecute(null));
        Assert.True(strip.StartStripExecutionCommand.CanExecute(null));
    }
}
