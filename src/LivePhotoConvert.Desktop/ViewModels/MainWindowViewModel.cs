using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoConvert.Desktop.Infrastructure;

namespace LivePhotoConvert.Desktop.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly INavigator _navigator;
    private readonly IDialogService _dialogs;

    public MainWindowViewModel(
        INavigator navigator,
        IDialogService dialogs,
        ConvertViewModel convertVm,
        StripViewModel stripVm,
        ToolsViewModel toolsVm,
        ReportViewModel reportVm,
        SettingsViewModel settingsVm)
    {
        _navigator = navigator;
        _dialogs = dialogs;
        ConvertVm = convertVm;
        StripVm = stripVm;
        ToolsVm = toolsVm;
        ReportVm = reportVm;
        SettingsVm = settingsVm;

        _navigator.PropertyChanged += OnNavigatorChanged;
        _dialogs.PropertyChanged += OnDialogsChanged;
    }

    public ConvertViewModel ConvertVm { get; }
    public StripViewModel StripVm { get; }
    public ToolsViewModel ToolsVm { get; }
    public ReportViewModel ReportVm { get; }
    public SettingsViewModel SettingsVm { get; }

    public int SelectedTabIndex
    {
        get => (int)_navigator.Current;
        set => _navigator.NavigateTo((AppPage)value);
    }

    public bool IsConvertTabSelected => _navigator.Current == AppPage.Convert;
    public bool IsStripTabSelected => _navigator.Current == AppPage.Strip;
    public bool IsToolsTabSelected => _navigator.Current == AppPage.Tools;
    public bool IsReportTabSelected => _navigator.Current == AppPage.Report;
    public bool IsSettingsTabSelected => _navigator.Current == AppPage.Settings;

    /// <summary>弹窗宿主显示的内容。</summary>
    public DialogViewModel? ActiveDialog => _dialogs.Current;

    public bool HasActiveDialog => _dialogs.Current is not null;

    /// <summary>窗口显示的版本号。</summary>
    public string VersionText { get; } = "v" + (typeof(MainWindowViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");

    // 标题栏按钮由窗口在绑定数据上下文时接管
    public Action? RequestCloseWindow { get; set; }
    public Action? RequestMinimizeWindow { get; set; }
    public Action? RequestMaximizeWindow { get; set; }

    [ObservableProperty]
    private bool _isWindowMaximized;

    [RelayCommand]
    public void SelectTab(object? index)
    {
        int? target = index switch
        {
            int i => i,
            string s when int.TryParse(s, out int p) => p,
            _ => null
        };

        if (target is { } page && Enum.IsDefined((AppPage)page))
        {
            _navigator.NavigateTo((AppPage)page);
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

    [RelayCommand]
    public void MinimizeWindow() => RequestMinimizeWindow?.Invoke();

    [RelayCommand]
    public void MaximizeWindow() => RequestMaximizeWindow?.Invoke();

    [RelayCommand]
    public void CloseWindow() => RequestCloseWindow?.Invoke();

    private void OnNavigatorChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(SelectedTabIndex));
        OnPropertyChanged(nameof(IsConvertTabSelected));
        OnPropertyChanged(nameof(IsStripTabSelected));
        OnPropertyChanged(nameof(IsToolsTabSelected));
        OnPropertyChanged(nameof(IsReportTabSelected));
        OnPropertyChanged(nameof(IsSettingsTabSelected));
    }

    private void OnDialogsChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(ActiveDialog));
        OnPropertyChanged(nameof(HasActiveDialog));
    }
}
