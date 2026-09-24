using System.Globalization;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using LivePhotoConvert.Core.Pipeline;
using LivePhotoConvert.Desktop.Controls;
using LivePhotoConvert.Desktop.Converters;
using LivePhotoConvert.Desktop.Features.Dialogs;
using LivePhotoConvert.Desktop.Features.Library;
using LivePhotoConvert.Desktop.Features.Tasks;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Tests.Features.Library;
using LivePhotoConvert.Desktop.Tests.Harness;

namespace LivePhotoConvert.Desktop.Tests.Features.Dialogs;

/// <summary>瘦身对比弹窗：样张真实处理、体积取自实际产物、显示按请求像素解码、关闭即取消并清理临时目录。</summary>
[Collection(ProcessStateCollection.Name)]
public sealed class StripCompareDialogTests : IDisposable
{
    private readonly TestSandbox _sandbox = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _sandbox.Dispose();

    [AvaloniaFact]
    public async Task RealTools_ProductIsActualHeicAndSizesMatchFiles()
    {
        var engines = CompareSamples.RequireRealTools();
        var photo = CompareSamples.WriteMotionPhoto(Path.Combine(_sandbox.InputDirectory, "MVIMG_0001.jpg"));
        var original = await File.ReadAllBytesAsync(photo, Token);
        using var pool = new MetadataSessionPool(engines, TimeProvider.System);
        var vm = new StripCompareDialogViewModel(new Localizer(), new StripSampler(engines, pool), photo, new StripSampleOptions(ToolPaths.Auto, true, 80));

        await vm.LoadTask.WaitAsync(TimeSpan.FromSeconds(60), Token);

        Assert.False(vm.HasFailed, vm.StatusText);
        var sample = Assert.IsType<StripSample>(vm.Sample);
        Assert.True(sample.Converted);
        Assert.Equal("heif-enc", sample.EncoderName);
        Assert.Equal(".heic", Path.GetExtension(sample.ProductPath));
        Assert.StartsWith(TempWorkspace.Root, sample.ProductPath, StringComparison.Ordinal);
        Assert.Equal(new FileInfo(sample.ProductPath).Length, sample.ProductBytes);
        Assert.Equal(original.LongLength, sample.OriginalBytes);
        Assert.True(sample.ProductBytes < original.LongLength, "瘦身产物应当小于原片");
        Assert.Equal(FormatBytes(sample.ProductBytes), vm.AfterSizeText);
        Assert.Equal(FormatBytes(sample.OriginalBytes), vm.BeforeSizeText);
        Assert.Equal(new PixelSize(1200, 900), vm.SourcePixelSize);

        vm.DisplayPixelSize = new PixelSize(600, 450);
        await vm.DisplayTask.WaitAsync(TimeSpan.FromSeconds(30), Token);
        Assert.Equal(new PixelSize(600, 450), vm.OriginalCompareBitmap!.PixelSize);
        Assert.Equal(new PixelSize(600, 450), vm.StrippedCompareBitmap!.PixelSize);
        Assert.False(vm.IsBusy);

        // 放大镜的细节直接来自 HEIC 产物，按原图坐标 1:1 裁切
        using (var detail = await vm.Images!.GetDetailAsync(new PixelRect(500, 400, 128, 128), new PixelSize(128, 128), Token))
        {
            Assert.Equal(new PixelSize(128, 128), detail!.After.PixelSize);
        }

        var workspace = sample.WorkspaceDirectory!;
        vm.Cancel();
        Assert.False(Directory.Exists(workspace), "关闭后临时目录应被删除");
        Assert.Null(vm.Sample);
        Assert.Null(vm.OriginalCompareBitmap);
        Assert.Equal(original, await File.ReadAllBytesAsync(photo, Token));
    }

    [AvaloniaFact]
    public async Task StandInEncoder_SizesComeFromTheProductFile()
    {
        var encoder = new LossyStandInEncoder();
        var engines = new CountingEngines(encoder);
        var photo = CompareSamples.WriteMotionPhoto(Path.Combine(_sandbox.InputDirectory, "MVIMG_0002.jpg"), 800, 600, videoBytes: 90_000);
        var localizer = new Localizer();
        var vm = new StripCompareDialogViewModel(localizer, CompareSamples.Sampler(engines), photo, new StripSampleOptions(ToolPaths.Auto, true, 90));

        await vm.LoadTask.WaitAsync(TimeSpan.FromSeconds(30), Token);

        var sample = vm.Sample!;
        Assert.Equal(1, encoder.Calls);
        Assert.Equal(new FileInfo(sample.ProductPath).Length, sample.ProductBytes);
        Assert.Equal(FormatBytes(sample.ProductBytes), vm.AfterSizeText);
        var saved = (sample.OriginalBytes - sample.ProductBytes) * 100.0 / sample.OriginalBytes;
        Assert.Equal(localizer.Format("StripSavedPctFormat", saved), vm.SavedPercentResult);
        Assert.Equal(localizer.Format("CompareParamsHeicFormat", 90), vm.ParametersText);
        Assert.Equal(localizer.Format("CompareEncoderFormat", "custom"), vm.EncoderText);
        Assert.True(vm.IsBusy, "显示位图解码前仍在忙");

        var workspace = sample.WorkspaceDirectory!;
        Assert.True(Directory.Exists(workspace));
        vm.Cancel();
        Assert.False(Directory.Exists(workspace));
    }

    [AvaloniaFact]
    public async Task HeicLargerThanOriginal_ShowsKeptFormatAndComparesStrippedOriginal()
    {
        var encoder = new LossyStandInEncoder { PadBytes = 2_000_000 };
        var photo = CompareSamples.WriteMotionPhoto(Path.Combine(_sandbox.InputDirectory, "MVIMG_0007.jpg"), 640, 480, videoBytes: 50_000);
        var localizer = new Localizer();
        var vm = new StripCompareDialogViewModel(localizer, CompareSamples.Sampler(new CountingEngines(encoder)), photo, new StripSampleOptions(ToolPaths.Auto, true, 90));

        await vm.LoadTask.WaitAsync(TimeSpan.FromSeconds(30), Token);

        var sample = vm.Sample!;
        Assert.Equal(1, encoder.Calls);
        Assert.True(sample.KeptOriginalFormat);
        Assert.False(sample.Converted);
        Assert.Equal(".jpg", Path.GetExtension(sample.ProductPath));
        Assert.True(sample.OriginalBytes - sample.ProductBytes >= 50_000);
        Assert.Equal(localizer["CompareKeptFormat"], vm.KeptFormatText);
        Assert.Equal(localizer.Format("CompareEncoderFormat", "custom"), vm.EncoderText);
        vm.Cancel();
    }

    [AvaloniaFact]
    public async Task Closing_WhileEncoding_CancelsAndDeletesTemporaryDirectory()
    {
        var encoder = new LossyStandInEncoder { Gate = new TaskCompletionSource() };
        var engines = new CountingEngines(encoder);
        var photo = CompareSamples.WriteMotionPhoto(Path.Combine(_sandbox.InputDirectory, "MVIMG_0003.jpg"), 640, 480);
        var vm = new StripCompareDialogViewModel(new Localizer(), CompareSamples.Sampler(engines), photo, new StripSampleOptions(ToolPaths.Auto, true, 90));

        var destination = await encoder.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        var workspace = Path.GetDirectoryName(destination)!;
        Assert.StartsWith(TempWorkspace.Root, workspace, StringComparison.Ordinal);
        Assert.True(Directory.Exists(workspace));
        Assert.True(vm.IsBusy);

        vm.Cancel();
        await vm.LoadTask.WaitAsync(TimeSpan.FromSeconds(10), Token);

        Assert.False(Directory.Exists(workspace), "取消后临时目录应被删除");
        Assert.Null(vm.Sample);
        Assert.False(vm.HasFailed);
        Assert.Null(vm.OriginalCompareBitmap);
    }

    [AvaloniaFact]
    public async Task EncoderFailure_ShowsReasonAndLeavesNoTemporaryFiles()
    {
        var engines = new CountingEngines(new FailingEncoder());
        var photo = CompareSamples.WriteMotionPhoto(Path.Combine(_sandbox.InputDirectory, "MVIMG_0004.jpg"), 320, 240);
        var localizer = new Localizer();
        var vm = new StripCompareDialogViewModel(localizer, CompareSamples.Sampler(engines), photo, new StripSampleOptions(ToolPaths.Auto, true, 90));

        await vm.LoadTask.WaitAsync(TimeSpan.FromSeconds(10), Token);

        Assert.True(vm.HasFailed);
        Assert.False(vm.IsBusy);
        Assert.Equal(localizer.Format("CompareFailedFormat", FailingEncoder.Reason), vm.StatusText);
        Assert.Null(vm.Sample);
    }

    [AvaloniaFact]
    public async Task WithoutHeicConversion_OnlyStripsVideo()
    {
        var encoder = new LossyStandInEncoder();
        var photo = CompareSamples.WriteMotionPhoto(Path.Combine(_sandbox.InputDirectory, "MVIMG_0005.jpg"), 320, 240, videoBytes: 50_000);
        var localizer = new Localizer();
        var vm = new StripCompareDialogViewModel(localizer, CompareSamples.Sampler(new CountingEngines(encoder)), photo, new StripSampleOptions(ToolPaths.Auto, false, 90));

        await vm.LoadTask.WaitAsync(TimeSpan.FromSeconds(10), Token);

        var sample = vm.Sample!;
        Assert.False(sample.Converted);
        Assert.Equal(0, encoder.Calls);
        Assert.Equal(".jpg", Path.GetExtension(sample.ProductPath));
        Assert.True(sample.OriginalBytes - sample.ProductBytes >= 50_000);
        Assert.Equal(localizer["CompareParamsKeep"], vm.ParametersText);
        Assert.Equal(string.Empty, vm.EncoderText);
        vm.Cancel();
    }

    [Fact]
    public void ZoomCommands_StepThroughOneToEight()
    {
        var vm = new StripCompareDialogViewModel(new Localizer(), new PendingStripSampler(), "/none.jpg", new StripSampleOptions(ToolPaths.Auto, true, 0));

        Assert.Equal(90, vm.HeicQuality);
        Assert.False(vm.ZoomOutCommand.CanExecute(null));
        vm.ZoomInCommand.Execute(null);
        vm.ZoomInCommand.Execute(null);
        vm.ZoomInCommand.Execute(null);
        Assert.Equal(8, vm.Zoom);
        Assert.Equal("800%", vm.ZoomText);
        Assert.False(vm.ZoomInCommand.CanExecute(null));
        vm.ZoomOutCommand.Execute(null);
        Assert.Equal(4, vm.Zoom);
        vm.ResetZoomCommand.Execute(null);
        Assert.Equal(1, vm.Zoom);
        vm.ToggleMagnifierCommand.Execute(null);
        Assert.True(vm.IsMagnifierEnabled);
        vm.Cancel();
    }

    [Theory]
    [InlineData(600, 450, 600, 450, false)]
    [InlineData(600, 450, 640, 480, false)]
    [InlineData(600, 450, 800, 600, true)]
    [InlineData(600, 450, 300, 225, true)]
    [InlineData(600, 450, 500, 450, false)]
    public void NeedsRedecode_OnlyForClearlyDifferentSizes(int decodedW, int decodedH, int targetW, int targetH, bool expected) =>
        Assert.Equal(expected, StripCompareDialogViewModel.NeedsRedecode(new PixelSize(decodedW, decodedH), new PixelSize(targetW, targetH)));

    /// <summary>在真实外壳里打开对比弹窗，开启放大镜并截图（浅色中文、深色英文）。有真实工具时用 heif-enc 产物。</summary>
    [AvaloniaFact]
    public async Task Dialog_WithMagnifier_RendersInBothThemes()
    {
        var photo = CompareSamples.WriteMotionPhoto(Path.Combine(_sandbox.InputDirectory, "MVIMG_0006.jpg"), 1600, 1200);
        var real = CompareSamplesRealOrNull();
        foreach (var (language, theme) in new[] { ("zh", ThemeService.Light), ("en", ThemeService.Dark) })
        {
            using var session = new ShellSession(language, theme);
            var engines = real ?? new CountingEngines(new LossyStandInEncoder());
            using var pool = new MetadataSessionPool(engines, TimeProvider.System);
            var vm = new StripCompareDialogViewModel(session.Localizer, new StripSampler(engines, pool), photo, new StripSampleOptions(ToolPaths.Auto, true, 60));
            var result = session.Dialogs.ShowAsync(vm);
            session.Pump();
            var compare = Assert.Single(session.Descendants<CurtainCompareControl>());
            // 首次布局时控件可能更窄，尺寸稳定后按最终的物理像素重新解码
            await session.WaitUntilAsync(() => vm.StrippedCompareBitmap is not null && vm.DisplayTask.IsCompleted
                && Math.Abs(vm.OriginalCompareBitmap!.PixelSize.Width - compare.RequestedPixelSize.Width) <= 1, 60);
            Assert.Equal(vm.DisplayPixelSize, compare.RequestedPixelSize);
            Assert.Equal(compare.RequestedPixelSize.Height, vm.OriginalCompareBitmap!.PixelSize.Height);
            Assert.Equal(vm.OriginalCompareBitmap.PixelSize, vm.StrippedCompareBitmap!.PixelSize);
            Screenshots.Save(session, $"strip-compare-{theme.ToLowerInvariant()}-{language}");

            vm.IsMagnifierEnabled = true;
            // 指向圆形右缘：放大镜里能看到边缘两侧的编码差异
            var edge = compare.Viewport.ToControl(new PixelRect(720, 560, 1, 1)).TopLeft;
            var pointer = compare.TranslatePoint(edge, session.Window)!.Value;
            session.Window.MouseMove(pointer);
            session.Pump();
            Assert.NotNull(compare.MagnifierRegion);
            await session.WaitUntilAsync(() => compare.MagnifierDetail is not null, 30);
            Assert.Equal(compare.MagnifierRegion, compare.MagnifierDetail!.Region);
            UiTexts.AssertNoResourceKeys(session, $"StripCompare ({language})");
            if (language == "en")
            {
                UiTexts.AssertNoChinese(session, $"StripCompare ({language})");
            }

            Screenshots.Save(session, $"strip-compare-magnifier-{theme.ToLowerInvariant()}-{language}");

            vm.IsMagnifierEnabled = false;
            vm.Zoom = 2;
            await session.WaitUntilAsync(() => compare.ViewDetail is not null, 30);
            Screenshots.Save(session, $"strip-compare-zoom2-{theme.ToLowerInvariant()}-{language}");

            var workspace = vm.Sample!.WorkspaceDirectory!;
            session.PressEscape();
            Assert.False(await result.WaitAsync(TimeSpan.FromSeconds(10), Token));
            Assert.False(Directory.Exists(workspace));
            Assert.Null(compare.MagnifierDetail);
            session.Log.AssertNoBindingErrors();
        }
    }

    private static IConversionEngines? CompareSamplesRealOrNull() =>
        Core.External.ToolLocator.Find(Core.Metadata.ExifToolMetadataService.ExecutableName) is not null
        && Core.External.ToolLocator.Find(Core.External.HeifEncImageConverter.ExecutableName) is not null
            ? ExternalToolEngines.Instance
            : null;

    private static string FormatBytes(long bytes) =>
        (string)ByteSizeConverter.Instance.Convert(bytes, typeof(string), null, CultureInfo.InvariantCulture)!;

    private sealed class FailingEncoder : Core.Abstractions.IImageConverter
    {
        public const string Reason = "模拟编码失败";

        public Task ConvertToJpegAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(Reason);

        public Task ConvertToHeicAsync(string sourcePath, string destinationPath, int quality, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(Reason);
    }
}
