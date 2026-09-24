using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoConvert.Desktop.Features.Library;
using LivePhotoConvert.Desktop.Features.Settings;
using LivePhotoConvert.Desktop.Features.Tasks;
using LivePhotoConvert.Desktop.Features.Tools;
using LivePhotoConvert.Desktop.Infrastructure;

namespace LivePhotoConvert.Desktop.Features.Shell;

/// <summary>主窗口：左侧导航、页面切换与弹窗宿主。</summary>
public sealed partial class ShellViewModel : ViewModelBase
{
    private readonly INavigator _navigator;
    private readonly IDialogService _dialogs;

    public ShellViewModel(
        INavigator navigator,
        IDialogService dialogs,
        LibraryViewModel library,
        InspectorViewModel inspector,
        TasksViewModel tasks,
        ToolsViewModel tools,
        SettingsViewModel settings)
    {
        _navigator = navigator;
        _dialogs = dialogs;
        Library = library;
        Inspector = inspector;
        Tasks = tasks;
        Tools = tools;
        Settings = settings;

        _navigator.PropertyChanged += OnNavigatorChanged;
        _dialogs.PropertyChanged += OnDialogsChanged;
    }

    public LibraryViewModel Library { get; }
    public InspectorViewModel Inspector { get; }
    public TasksViewModel Tasks { get; }
    public ToolsViewModel Tools { get; }
    public SettingsViewModel Settings { get; }

    public AppPage CurrentPage => _navigator.Current;

    public bool IsLibrarySelected => _navigator.Current == AppPage.Library;
    public bool IsTasksSelected => _navigator.Current == AppPage.Tasks;
    public bool IsToolsSelected => _navigator.Current == AppPage.Tools;
    public bool IsSettingsSelected => _navigator.Current == AppPage.Settings;

    /// <summary>弹窗宿主显示的内容。</summary>
    public DialogViewModel? ActiveDialog => _dialogs.Current;

    public bool HasActiveDialog => _dialogs.Current is not null;

    /// <summary>由窗口在状态变化时同步，用于切换最大化/还原图标。</summary>
    [ObservableProperty]
    private bool _isWindowMaximized;

    [RelayCommand]
    public void Navigate(string? page)
    {
        if (Enum.TryParse<AppPage>(page, ignoreCase: false, out var target) && Enum.IsDefined(target))
        {
            _navigator.NavigateTo(target);
        }
    }

    /// <summary>Esc 关闭当前弹窗；没有弹窗时返回 false，按键继续交给页面。</summary>
    public bool TryCancelActiveDialog()
    {
        if (_dialogs.Current is not { } dialog)
        {
            return false;
        }

        dialog.CancelCommand.Execute(null);
        return true;
    }

    private void OnNavigatorChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(CurrentPage));
        OnPropertyChanged(nameof(IsLibrarySelected));
        OnPropertyChanged(nameof(IsTasksSelected));
        OnPropertyChanged(nameof(IsToolsSelected));
        OnPropertyChanged(nameof(IsSettingsSelected));
    }

    private void OnDialogsChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(ActiveDialog));
        OnPropertyChanged(nameof(HasActiveDialog));
    }
}
