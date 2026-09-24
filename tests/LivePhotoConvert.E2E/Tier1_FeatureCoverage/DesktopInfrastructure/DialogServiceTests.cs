using System.ComponentModel;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Models;
using LivePhotoConvert.Desktop.ViewModels;
using LivePhotoConvert.Desktop.ViewModels.Dialogs;
using LivePhotoConvert.E2E.Harness;

namespace LivePhotoConvert.E2E.Tier1_FeatureCoverage.DesktopInfrastructure;

public class DialogServiceTests
{
    /// <summary>记录 OnClosed 次数的弹窗，取消时返回自定义结果。</summary>
    private sealed class ProbeDialog(string cancelResult = "canceled") : DialogViewModel<string>
    {
        public int ClosedCount { get; private set; }

        public override object? CancelResult => cancelResult;

        public void Finish(string result) => Close(result);

        protected internal override void OnClosed() => ClosedCount++;
    }

    private static ConfirmDialogViewModel Confirm(string title = "t") => new() { Title = title, ConfirmText = "ok", CancelText = "cancel" };

    private static async Task<T> CompletesSoon<T>(Task<T> task)
    {
        var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Same(task, finished);
        return await task;
    }

    [Fact]
    public async Task ShowAsync_SetsCurrent_AndReturnsResultWhenClosed()
    {
        var service = new DialogService();
        var dialog = new ProbeDialog();

        var pending = service.ShowAsync(dialog);
        Assert.Same(dialog, service.Current);
        Assert.False(pending.IsCompleted);

        dialog.Finish("done");

        Assert.Equal("done", await CompletesSoon(pending));
        Assert.Null(service.Current);
    }

    [Fact]
    public async Task SecondDialog_IsQueued_UntilFirstCloses()
    {
        var service = new DialogService();
        var first = new ProbeDialog();
        var second = new ProbeDialog();

        var firstTask = service.ShowAsync(first);
        var secondTask = service.ShowAsync(second);
        Assert.Same(first, service.Current);

        first.Finish("a");
        Assert.Same(second, service.Current);
        Assert.Equal("a", await CompletesSoon(firstTask));
        Assert.False(secondTask.IsCompleted);

        second.Finish("b");
        Assert.Equal("b", await CompletesSoon(secondTask));
        Assert.Null(service.Current);
    }

    [Fact]
    public async Task Cancel_ReturnsCancelResult()
    {
        var service = new DialogService();

        var probe = new ProbeDialog("custom");
        var probeTask = service.ShowAsync(probe);
        probe.CancelCommand.Execute(null);
        Assert.Equal("custom", await CompletesSoon(probeTask));

        var confirm = Confirm();
        var confirmTask = service.ShowAsync(confirm);
        confirm.CancelCommand.Execute(null);
        Assert.False(await CompletesSoon(confirmTask));

        var arbitrate = new ArbitrateDialogViewModel(new Localizer()) { TargetCard = new PhotoCardItemViewModel { Key = "k", PhotoPath = "missing.heic" } };
        var arbitrateTask = service.ShowAsync(arbitrate);
        arbitrate.CancelCommand.Execute(null);
        Assert.Equal(ArbitrationVerdict.Dismiss, await CompletesSoon(arbitrateTask));
    }

    [Fact]
    public async Task OnClosed_RunsExactlyOnce_AndLaterCloseIsIgnored()
    {
        var service = new DialogService();
        var dialog = new ProbeDialog();
        var task = service.ShowAsync(dialog);

        dialog.CancelCommand.Execute(null);
        dialog.CancelCommand.Execute(null);
        dialog.Finish("late");

        Assert.Equal(1, dialog.ClosedCount);
        Assert.True(dialog.IsClosed);
        Assert.Equal("canceled", await CompletesSoon(task));
    }

    [Fact]
    public async Task CancelAll_ClosesCurrentAndQueued_EachOnce()
    {
        var service = new DialogService();
        var first = new ProbeDialog("c1");
        var second = new ProbeDialog("c2");
        var firstTask = service.ShowAsync(first);
        var secondTask = service.ShowAsync(second);

        service.CancelAll();

        Assert.Null(service.Current);
        Assert.Equal("c1", await CompletesSoon(firstTask));
        Assert.Equal("c2", await CompletesSoon(secondTask));
        Assert.Equal(1, first.ClosedCount);
        Assert.Equal(1, second.ClosedCount);
    }

    [Fact]
    public async Task QueuedDialog_ClosedBeforeShown_IsRemovedFromQueue()
    {
        var service = new DialogService();
        var first = new ProbeDialog();
        var second = new ProbeDialog();
        var third = new ProbeDialog();
        _ = service.ShowAsync(first);
        var secondTask = service.ShowAsync(second);
        _ = service.ShowAsync(third);

        second.Finish("skipped");
        Assert.Equal("skipped", await CompletesSoon(secondTask));

        first.Finish("x");
        Assert.Same(third, service.Current);
    }

    [Fact]
    public void ShowAsync_SameInstanceTwice_Throws()
    {
        var service = new DialogService();
        var dialog = new ProbeDialog();
        _ = service.ShowAsync(dialog);

        Assert.Throws<InvalidOperationException>(() => { _ = service.ShowAsync(dialog); });
    }

    [Fact]
    public async Task MainWindow_Escape_CancelsActiveDialog_AndExposesHostState()
    {
        using var host = new DesktopTestHost();
        var dialogs = host.Get<IDialogService>();
        var shell = host.Get<MainWindowViewModel>();
        var changed = new List<string?>();
        ((INotifyPropertyChanged)shell).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.False(shell.TryCancelActiveDialog(), "没有弹窗时 Esc 应交还给页面");

        var confirm = Confirm();
        var task = dialogs.ShowAsync(confirm);
        Assert.Same(confirm, shell.ActiveDialog);
        Assert.True(shell.HasActiveDialog);
        Assert.Contains(nameof(MainWindowViewModel.ActiveDialog), changed);

        Assert.True(shell.TryCancelActiveDialog());
        Assert.False(await CompletesSoon(task));
        Assert.Null(shell.ActiveDialog);
        Assert.False(shell.HasActiveDialog);
    }
}
