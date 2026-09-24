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

        ToolStatuses = [new("ExifTool"), new("FFmpeg"), new("heif-enc")];
        foreach (var status in ToolStatuses)
        {
            status.PropertyChanged += OnToolStatusChanged;
        }
        SyncToolStatuses();

        _navigator.PropertyChanged += OnNavigatorChanged;
        _dialogs.PropertyChanged += OnDialogsChanged;
        Tools.PropertyChanged += (_, _) => SyncToolStatuses();
    }

    public LibraryViewModel Library { get; }
    public InspectorViewModel Inspector { get; }
    public TasksViewModel Tasks { get; }
    public ToolsViewModel Tools { get; }
    public SettingsViewModel Settings { get; }

    public AppPage CurrentPage => _navigator.Current;

    /// <summary>侧栏依赖状态，顺序为 ExifTool、FFmpeg、heif-enc。</summary>
    public IReadOnlyList<ToolStatusItem> ToolStatuses { get; }

    /// <summary>导航"依赖引擎"上的状态点，取各依赖中最差的一项。</summary>
    public ToolHealth ToolsHealth => ToolStatusItem.Worst(ToolStatuses);

    public bool AreToolsReady => ToolsHealth == ToolHealth.Ready;

    public bool DoToolsNeedAttention => ToolsHealth == ToolHealth.Attention;

    public bool AreToolsMissing => ToolsHealth == ToolHealth.Missing;

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

    // 依赖页目前只报告就绪与否；它能给出升级建议或能力缺失时，把判定接到 needsAttention 上
    private void SyncToolStatuses()
    {
        ToolStatuses[0].Health = ToolStatusItem.Evaluate(Tools.IsExifToolReady, needsAttention: false);
        ToolStatuses[1].Health = ToolStatusItem.Evaluate(Tools.IsFfmpegReady, needsAttention: false);
        ToolStatuses[2].Health = ToolStatusItem.Evaluate(Tools.IsHeifEncReady, needsAttention: false);
    }

    private void OnToolStatusChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ToolStatusItem.Health))
        {
            OnPropertyChanged(nameof(ToolsHealth));
            OnPropertyChanged(nameof(AreToolsReady));
            OnPropertyChanged(nameof(DoToolsNeedAttention));
            OnPropertyChanged(nameof(AreToolsMissing));
        }
    }

    private void OnDialogsChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(ActiveDialog));
        OnPropertyChanged(nameof(HasActiveDialog));
    }
}
