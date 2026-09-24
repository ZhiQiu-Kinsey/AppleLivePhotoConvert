using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Models;
using LivePhotoConvert.Desktop.ViewModels.Dialogs;

namespace LivePhotoConvert.E2E.Tier1_FeatureCoverage.DesktopInfrastructure;

public class DialogViewModelTests
{
    private static async Task<T> ShowAndAct<T>(DialogViewModel<T> dialog, Action act)
    {
        var task = new DialogService().ShowAsync(dialog);
        act();
        var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Same(task, finished);
        return (await task)!;
    }

    [Fact]
    public void DeleteConfirm_ConfirmCommand_TracksPasswordInput()
    {
        var dialog = new DeleteConfirmDialogViewModel(new Localizer()) { AffectedCount = 3, AffectedSizeText = "1 MB" };
        var raised = 0;
        dialog.ConfirmCommand.CanExecuteChanged += (_, _) => raised++;

        Assert.False(dialog.ConfirmCommand.CanExecute(null));

        dialog.PasswordInput = "delete";
        Assert.False(dialog.ConfirmCommand.CanExecute(null));

        dialog.PasswordInput = "DELETE";
        Assert.True(dialog.ConfirmCommand.CanExecute(null));
        Assert.True(dialog.CanConfirm);

        dialog.PasswordInput = "DELETE ";
        Assert.False(dialog.ConfirmCommand.CanExecute(null));
        Assert.True(raised >= 3, "每次输入变化都应通知按钮刷新可用状态");
    }

    [Fact]
    public async Task DeleteConfirm_Confirm_ReturnsTrue_Cancel_ReturnsFalse()
    {
        var confirmed = new DeleteConfirmDialogViewModel(new Localizer());
        Assert.True(await ShowAndAct(confirmed, () =>
        {
            confirmed.PasswordInput = "DELETE";
            confirmed.ConfirmCommand.Execute(null);
        }));

        var canceled = new DeleteConfirmDialogViewModel(new Localizer());
        Assert.False(await ShowAndAct(canceled, () => canceled.CancelCommand.Execute(null)));
    }

    [Fact]
    public async Task ConfirmDialog_ReturnsChoice_AndSingleButtonHidesCancel()
    {
        var dialog = new ConfirmDialogViewModel { Title = "t", Message = "m", ConfirmText = "ok", CancelText = "no", IsDanger = true };
        Assert.True(dialog.ShowCancelButton);
        Assert.True(dialog.HasMessage);
        Assert.True(await ShowAndAct(dialog, () => dialog.ConfirmCommand.Execute(null)));

        var alert = new ConfirmDialogViewModel { Title = "t", ConfirmText = "ok", IsSingleButton = true };
        Assert.False(alert.ShowCancelButton);
        Assert.False(alert.HasMessage);
        Assert.False(await ShowAndAct(alert, () => alert.CancelCommand.Execute(null)));
    }

    [Fact]
    public async Task AlertAsync_ShowsSingleButtonDialog()
    {
        var service = new DialogService();
        var task = service.AlertAsync("title", "message", "ok");

        var alert = Assert.IsType<ConfirmDialogViewModel>(service.Current);
        Assert.True(alert.IsSingleButton);
        Assert.Equal("title", alert.Title);
        Assert.Equal("message", alert.Message);

        alert.ConfirmCommand.Execute(null);
        await task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task LowDiskSpace_ContinueReturnsTrue_DismissReturnsFalse()
    {
        var proceed = new LowDiskSpaceDialogViewModel();
        Assert.True(await ShowAndAct(proceed, () => proceed.ContinueCommand.Execute(null)));

        var back = new LowDiskSpaceDialogViewModel();
        Assert.False(await ShowAndAct(back, () => back.CancelCommand.Execute(null)));
    }

    [Fact]
    public async Task BackupConfirm_ReturnsTrueOnlyWhenConfirmed()
    {
        var yes = new BackupConfirmDialogViewModel();
        Assert.True(await ShowAndAct(yes, () => yes.ConfirmCommand.Execute(null)));

        var no = new BackupConfirmDialogViewModel();
        Assert.False(await ShowAndAct(no, () => no.CancelCommand.Execute(null)));
    }

    [Theory]
    [InlineData(ArbitrationVerdict.Accept)]
    [InlineData(ArbitrationVerdict.Reject)]
    public async Task Arbitrate_ReturnsVerdict_WithoutTouchingCard(ArbitrationVerdict expected)
    {
        var card = new PhotoCardItemViewModel { Key = "k", PhotoPath = "missing.heic" };
        var dialog = new ArbitrateDialogViewModel(new Localizer()) { TargetCard = card };

        var verdict = await ShowAndAct(dialog, () =>
        {
            if (expected == ArbitrationVerdict.Accept)
            {
                dialog.ConfirmWhitelistCommand.Execute(null);
            }
            else
            {
                dialog.RejectSplitCommand.Execute(null);
            }
        });

        Assert.Equal(expected, verdict);
        Assert.False(card.IsForceAccepted, "是否放行由调用方根据结果决定");
    }
}
