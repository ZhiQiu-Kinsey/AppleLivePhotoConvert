using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LivePhotoConvert.Desktop.ViewModels.Dialogs;

namespace LivePhotoConvert.Desktop.Infrastructure;

public interface IDialogService : INotifyPropertyChanged
{
    /// <summary>正在显示的弹窗；主窗口弹窗宿主绑定它。</summary>
    DialogViewModel? Current { get; }

    /// <summary>显示弹窗并等待结果；已有弹窗时排队，按提交顺序依次显示。</summary>
    Task<TResult?> ShowAsync<TResult>(DialogViewModel<TResult> dialog);

    /// <summary>以取消结果关闭当前与排队中的全部弹窗（退出程序时使用）。</summary>
    void CancelAll();
}

public static class DialogServiceExtensions
{
    /// <summary>单按钮提示。</summary>
    public static Task AlertAsync(this IDialogService dialogs, string title, string message, string okText) =>
        dialogs.ShowAsync(new ConfirmDialogViewModel
        {
            Title = title,
            Message = message,
            ConfirmText = okText,
            IsSingleButton = true
        });
}

/// <summary>
/// 同一时刻只显示一个弹窗；须在界面线程调用。
/// </summary>
public sealed partial class DialogService : ObservableObject, IDialogService
{
    private readonly Queue<DialogViewModel> _pending = new();
    private readonly Dictionary<DialogViewModel, TaskCompletionSource<object?>> _completions = [];

    [ObservableProperty]
    private DialogViewModel? _current;

    public Task<TResult?> ShowAsync<TResult>(DialogViewModel<TResult> dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        if (dialog.IsClosed || _completions.ContainsKey(dialog))
        {
            throw new InvalidOperationException("弹窗实例不能重复显示。");
        }

        // 异步延续：结果交付时不在 Close 调用栈内重入调用方逻辑
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _completions[dialog] = completion;
        dialog.Attach(OnDialogClosed);

        if (Current is null)
        {
            Current = dialog;
        }
        else
        {
            _pending.Enqueue(dialog);
        }

        return Unwrap<TResult>(completion.Task);
    }

    public void CancelAll()
    {
        foreach (var dialog in _pending.ToArray())
        {
            dialog.Cancel();
        }

        Current?.Cancel();
    }

    private static async Task<TResult?> Unwrap<TResult>(Task<object?> task) =>
        await task.ConfigureAwait(false) is TResult result ? result : default;

    private void OnDialogClosed(DialogViewModel dialog, object? result)
    {
        if (!_completions.Remove(dialog, out var completion))
        {
            return;
        }

        if (ReferenceEquals(Current, dialog))
        {
            Current = _pending.Count > 0 ? _pending.Dequeue() : null;
        }
        else if (_pending.Contains(dialog))
        {
            var remaining = _pending.Where(d => !ReferenceEquals(d, dialog)).ToList();
            _pending.Clear();
            foreach (var d in remaining)
            {
                _pending.Enqueue(d);
            }
        }

        completion.TrySetResult(result);
    }
}
