using CommunityToolkit.Mvvm.Input;
using LivePhotoConvert.Desktop.ViewModels;

namespace LivePhotoConvert.Desktop.Infrastructure;

/// <summary>
/// 由 <see cref="IDialogService"/> 显示的弹窗。关闭只能经 <see cref="Close"/>，保证结果只交付一次、资源只释放一次。
/// </summary>
public abstract partial class DialogViewModel : ViewModelBase
{
    private Action<DialogViewModel, object?>? _closeHandler;

    public bool IsClosed { get; private set; }

    /// <summary>Esc、遮罩点击与取消按钮返回的结果。</summary>
    public virtual object? CancelResult => null;

    [RelayCommand]
    public void Cancel() => Close(CancelResult);

    internal void Attach(Action<DialogViewModel, object?> closeHandler) => _closeHandler = closeHandler;

    protected void Close(object? result)
    {
        if (IsClosed)
        {
            return;
        }

        IsClosed = true;
        try
        {
            OnClosed();
        }
        finally
        {
            _closeHandler?.Invoke(this, result);
        }
    }

    /// <summary>弹窗关闭后调用一次，用于停止播放、释放位图等。</summary>
    protected internal virtual void OnClosed()
    {
    }
}

public abstract class DialogViewModel<TResult> : DialogViewModel
{
    public override object? CancelResult => default(TResult);

    protected void Close(TResult result) => base.Close(result);
}
