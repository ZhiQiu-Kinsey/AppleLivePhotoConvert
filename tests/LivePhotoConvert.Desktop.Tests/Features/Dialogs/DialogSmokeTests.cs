using LivePhotoConvert.Core.Pipeline;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using LivePhotoConvert.Core.Tests.Support;
using LivePhotoConvert.Desktop.Features.Dialogs;
using LivePhotoConvert.Desktop.Features.Library;
using LivePhotoConvert.Desktop.Features.Library.Thumbnails;
using LivePhotoConvert.Desktop.Features.Tasks;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Core.Media;
using LivePhotoConvert.Core.Pairing;
using LivePhotoConvert.Desktop.Models;
using LivePhotoConvert.Desktop.Tests.Harness;

namespace LivePhotoConvert.Desktop.Tests.Features.Dialogs;

/// <summary>每种弹窗经 IDialogService 显示在真实外壳里：视图实例化、Esc/遮罩取消、主按钮结果、中英文与深浅色截图。</summary>
[Collection(ProcessStateCollection.Name)]
public sealed class DialogSmokeTests : IDisposable
{
    private readonly TestSandbox _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    private static readonly string[] DialogNames =
        ["Confirm", "DeleteConfirm", "LowDiskSpace", "BackupConfirm", "Arbitrate", "StripCompare", "Donate", "QuickLook"];

    public static TheoryData<string> AllDialogs => [.. DialogNames];

    public static TheoryData<string> DialogsWithPrimaryButton =>
        ["Confirm", "DeleteConfirm", "LowDiskSpace", "BackupConfirm", "Arbitrate", "ArbitrateReject"];

    [AvaloniaTheory]
    [MemberData(nameof(AllDialogs))]
    public async Task Show_RendersItsView_AndEscapeClosesWithCancelResult(string name)
    {
        using var session = new ShellSession();
        var dialog = await CreateAsync(session, name);

        var result = Show(session, dialog.ViewModel);
        session.Pump();

        var view = AssertShown(session, dialog);
        session.PressEscape();

        // 关闭是同步的；结果经线程池延续交付，要等一下
        Assert.True(dialog.ViewModel.IsClosed, "Esc 没有关闭弹窗");
        Assert.Equal(dialog.CancelResult, await Within(result));
        Assert.Null(session.Dialogs.Current);
        Assert.DoesNotContain(view, session.Descendants<Control>());
        session.Log.AssertNoBindingErrors();
    }

    [AvaloniaTheory]
    [MemberData(nameof(DialogsWithPrimaryButton))]
    public async Task PrimaryButton_ClosesWithItsResult(string name)
    {
        using var session = new ShellSession();
        var dialog = await CreateAsync(session, name);
        var result = Show(session, dialog.ViewModel);
        session.Pump();
        AssertShown(session, dialog);

        if (dialog.ViewModel is DeleteConfirmDialogViewModel)
        {
            // 口令输入前确认按钮不可用；逐字键入后才解锁
            Assert.False(FindButton(session, dialog.PrimaryCommand!).IsEffectivelyEnabled);
            var box = session.Descendants<TextBox>().Single(t => t.Name == "PasswordBox");
            box.Focus();
            session.Window.KeyTextInput("DELETE");
            session.Pump();
        }

        session.Click(FindButton(session, dialog.PrimaryCommand!));

        Assert.True(dialog.ViewModel.IsClosed, "主按钮没有关闭弹窗");
        Assert.Equal(dialog.PrimaryResult, await Within(result));
        Assert.Null(session.Dialogs.Current);
        session.Log.AssertNoBindingErrors();
    }

    [AvaloniaFact]
    public async Task OverlayClick_OutsideDialog_Cancels()
    {
        using var session = new ShellSession();
        var dialog = await CreateAsync(session, "Confirm");
        var result = Show(session, dialog.ViewModel);
        session.Pump();
        AssertShown(session, dialog);

        // 左下角远离居中的弹窗卡片，命中的是遮罩
        var corner = new Point(8, session.Window.Bounds.Height - 8);
        session.Window.MouseDown(corner, Avalonia.Input.MouseButton.Left);
        session.Window.MouseUp(corner, Avalonia.Input.MouseButton.Left);
        session.Pump();

        Assert.True(dialog.ViewModel.IsClosed, "点击遮罩没有关闭弹窗");
        Assert.Equal(false, await Within(result));
    }

    [AvaloniaFact]
    public async Task QuickLook_NavigatesBetweenCardsAndReleasesOnClose()
    {
        using var session = new ShellSession();
        var dialog = await CreateAsync(session, "QuickLook");
        var quickLook = (QuickLookDialogViewModel)dialog.ViewModel;
        var result = Show(session, quickLook);
        session.Pump();
        AssertShown(session, dialog);
        // 大图到达后弹窗按图片比例重新布局，等它稳定再点击
        await dialog.Ready(session);
        var first = quickLook.Card;

        session.Click(FindButton(session, quickLook.NextItemCommand));
        Assert.NotSame(first, quickLook.Card);
        Assert.Equal("2 / 2", quickLook.NavigationIndexText);
        await session.WaitUntilAsync(() => quickLook.CurrentDisplayImage is not null);

        session.Click(FindButton(session, quickLook.PrevItemCommand));
        Assert.Same(first, quickLook.Card);
        Assert.Equal("1 / 2", quickLook.NavigationIndexText);
        await session.WaitUntilAsync(() => quickLook.CurrentDisplayImage is not null);

        session.PressEscape();
        Assert.Equal(false, await Within(result));
        Assert.Null(quickLook.CurrentDisplayImage);
        session.Log.AssertNoBindingErrors();
    }

    /// <summary>浅色中文与深色英文各一套；英文下不得残留中文或资源键名。</summary>
    [AvaloniaFact]
    public async Task EveryDialog_RendersInBothLanguagesAndThemes()
    {
        foreach (var (language, theme) in new[] { ("zh", ThemeService.Light), ("en", ThemeService.Dark) })
        {
            using var session = new ShellSession(language, theme);
            foreach (var name in DialogNames)
            {
                var dialog = await CreateAsync(session, name);
                var result = Show(session, dialog.ViewModel);
                session.Pump();
                AssertShown(session, dialog);
                await dialog.Ready(session);

                UiTexts.AssertNoResourceKeys(session, $"{name} ({language})");
                if (language == "en")
                {
                    UiTexts.AssertNoChinese(session, $"{name} ({language})");
                }

                Screenshots.Save(session, $"dialog-{name.ToLowerInvariant()}-{theme.ToLowerInvariant()}-{language}");
                dialog.ViewModel.Cancel();
                await Within(result);
            }

            session.Log.AssertNoBindingErrors();
        }
    }

    private sealed record DialogCase(
        DialogViewModel ViewModel,
        Type ViewType,
        object? CancelResult,
        ICommand? PrimaryCommand = null,
        object? PrimaryResult = null)
    {
        /// <summary>等到后台加载（大图、对比图）交付后再截图。</summary>
        public Func<ShellSession, Task> Ready { get; init; } = _ => Task.CompletedTask;
    }

    private async Task<DialogCase> CreateAsync(ShellSession session, string name)
    {
        var loc = session.Localizer;
        switch (name)
        {
            case "Confirm":
            {
                var vm = new ConfirmDialogViewModel
                {
                    Title = loc["ExitConfirmTitle"],
                    Message = loc["ExitConfirmDesc"],
                    ConfirmText = loc["ExitConfirmBtn"],
                    CancelText = loc["ConfirmDialogCancel"],
                    IsDanger = true
                };
                return new DialogCase(vm, typeof(ConfirmDialog), false, vm.ConfirmCommand, true);
            }
            case "DeleteConfirm":
            {
                var vm = new DeleteConfirmDialogViewModel(loc) { AffectedCount = 12, AffectedSizeText = "48.6 MB" };
                return new DialogCase(vm, typeof(DeleteConfirmDialog), false, vm.ConfirmCommand, true);
            }
            case "LowDiskSpace":
            {
                var vm = new LowDiskSpaceDialogViewModel
                {
                    TargetDirectory = _sandbox.OutputDirectory,
                    RequiredSpaceText = "2.10 GB",
                    AvailableSpaceText = "512.00 MB"
                };
                return new DialogCase(vm, typeof(LowDiskSpaceDialog), false, vm.ContinueCommand, true);
            }
            case "BackupConfirm":
            {
                var vm = new BackupConfirmDialogViewModel();
                return new DialogCase(vm, typeof(BackupConfirmDialog), false, vm.ConfirmCommand, true);
            }
            case "Arbitrate" or "ArbitrateReject":
            {
                var vm = new ArbitrateDialogViewModel(loc) { TargetCard = ArbitrationCard(loc) };
                return name == "Arbitrate"
                    ? new DialogCase(vm, typeof(ArbitrateDialog), ArbitrationVerdict.Dismiss, vm.ConfirmWhitelistCommand, ArbitrationVerdict.Accept)
                    : new DialogCase(vm, typeof(ArbitrateDialog), ArbitrationVerdict.Dismiss, vm.RejectSplitCommand, ArbitrationVerdict.Reject);
            }
            case "StripCompare":
            {
                var photo = SampleAlbum.WriteJpeg(Path.Combine(_sandbox.InputDirectory, "compare.jpg"), 5, 960, 720);
                // 以低质量 JPEG 代替 HEIC 编码，不依赖外部工具
                var engines = new CountingEngines(new LossyStandInEncoder());
                var vm = new StripCompareDialogViewModel(loc, CompareSamples.Sampler(engines), photo, new StripSampleOptions(ToolPaths.Auto, true, 90));
                await vm.LoadTask.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
                return new DialogCase(vm, typeof(StripCompareDialog), false)
                {
                    Ready = s => s.WaitUntilAsync(() => vm.StrippedCompareBitmap is not null && vm.OriginalCompareBitmap is not null)
                };
            }
            case "Donate":
                return new DialogCase(new DonateDialogViewModel(), typeof(DonateDialog), false);
            case "QuickLook":
            {
                // 静态卡片（无视频）：不启动 FFmpeg 播放，只验证弹窗、导航与大图加载
                PhotoCardItemViewModel[] cards =
                [
                    StillCard("IMG_0101", 1, loc),
                    StillCard("IMG_0102", 2, loc)
                ];
                var vm = new QuickLookDialogViewModel(loc, session.Host.Get<IThumbnailPipeline>(), i => i >= 0 && i < cards.Length ? cards[i] : null, cards.Length, 0);
                return new DialogCase(vm, typeof(QuickLookDialog), false)
                {
                    Ready = s => s.WaitUntilAsync(() => vm.CurrentDisplayImage is not null)
                };
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(name), name, null);
        }
    }

    private PhotoCardItemViewModel StillCard(string stem, int color, ILocalizer localizer)
    {
        var path = SampleAlbum.WriteJpeg(Path.Combine(_sandbox.InputDirectory, stem + ".jpg"), color, 1200, 900);
        FastImageHeaderReader.TryReadHeader(path, out var header);
        return new PhotoCardItemViewModel(new LibraryItem(LibraryItemKind.Still, Scanned(path))
        {
            Header = header,
            CaptureTimeLocal = new DateTime(2026, 9, 1, 10, 0, 0)
        }, localizer);
    }

    /// <summary>照片与视频修改时间相差 12 秒，裁决弹窗显示真实时间差。</summary>
    private PhotoCardItemViewModel ArbitrationCard(ILocalizer localizer)
    {
        var photo = SampleAlbum.WriteJpeg(Path.Combine(_sandbox.InputDirectory, "IMG_0200.jpg"), 3);
        var video = _sandbox.CreateInputFile("IMG_0200.mov", SyntheticMedia.Mov(4096));
        var taken = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Local);
        File.SetLastWriteTime(photo, taken);
        File.SetLastWriteTime(video, taken.AddSeconds(12));
        return new PhotoCardItemViewModel(new LibraryItem(LibraryItemKind.ApplePair, Scanned(photo))
        {
            Video = Scanned(video),
            PairCandidates = [new MediaPair(photo, video)],
            PairValidation = PairValidationResult.Reject(new OutcomeCause(OutcomeReason.PairCaptureTimeTooFar, 12.0, 3.0)),
            CaptureTimeLocal = taken
        }, localizer);
    }

    private static LibraryFile Scanned(string path)
    {
        var info = new FileInfo(path);
        return new LibraryFile(info.FullName, info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc);
    }

    private static Task<object?> Show(ShellSession session, DialogViewModel dialog) => dialog switch
    {
        DialogViewModel<bool> b => Box(session.Dialogs.ShowAsync(b)),
        DialogViewModel<ArbitrationVerdict> a => Box(session.Dialogs.ShowAsync(a)),
        _ => throw new NotSupportedException(dialog.GetType().Name)
    };

    private static async Task<object?> Box<T>(Task<T?> task) => await task;

    private static Task<object?> Within(Task<object?> result) =>
        result.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

    private static Control AssertShown(ShellSession session, DialogCase dialog)
    {
        Assert.Same(dialog.ViewModel, session.Dialogs.Current);
        Assert.True(session.Shell.HasActiveDialog);
        var view = Assert.Single(session.Descendants<Control>(), c => c.GetType() == dialog.ViewType);
        Assert.Same(dialog.ViewModel, view.DataContext);
        Assert.True(view.IsEffectivelyVisible, $"{dialog.ViewType.Name} 不可见");
        Assert.True(view.Bounds.Width > 0 && view.Bounds.Height > 0, $"{dialog.ViewType.Name} 没有参与布局");
        return view;
    }

    private static Button FindButton(ShellSession session, ICommand command) =>
        Assert.Single(session.Descendants<Button>(), b => ReferenceEquals(b.Command, command) && b.IsEffectivelyVisible);
}
