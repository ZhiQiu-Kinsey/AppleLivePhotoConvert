using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoConvert.Desktop.Services;

namespace LivePhotoConvert.Desktop.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConvertTabSelected))]
    [NotifyPropertyChangedFor(nameof(IsStripTabSelected))]
    [NotifyPropertyChangedFor(nameof(IsToolsTabSelected))]
    [NotifyPropertyChangedFor(nameof(IsReportTabSelected))]
    [NotifyPropertyChangedFor(nameof(IsSettingsTabSelected))]
    private int _selectedTabIndex; // 0=Convert, 1=Strip, 2=Tools, 3=Report, 4=Settings

    public bool IsConvertTabSelected => SelectedTabIndex == 0;
    public bool IsStripTabSelected => SelectedTabIndex == 1;
    public bool IsToolsTabSelected => SelectedTabIndex == 2;
    public bool IsReportTabSelected => SelectedTabIndex == 3;
    public bool IsSettingsTabSelected => SelectedTabIndex == 4;

    [ObservableProperty]
    private string _currentTheme;

    [ObservableProperty]
    private string _currentLanguage;

    [ObservableProperty]
    private ViewModelBase? _activeDialog;

    public ConvertViewModel ConvertVm { get; }
    public StripViewModel StripVm { get; }
    public ToolsViewModel ToolsVm { get; }
    public ReportViewModel ReportVm { get; }
    public SettingsViewModel SettingsVm { get; }

    /// <summary>窗口显示的版本号。</summary>
    public string VersionText { get; } = "v" + (typeof(MainWindowViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");

    public Action? RequestCloseWindow { get; set; }
    public Action? RequestMinimizeWindow { get; set; }
    public Action? RequestMaximizeWindow { get; set; }

    [ObservableProperty]
    private bool _isWindowMaximized;

    public MainWindowViewModel()
    {
        _settingsService = new SettingsService();

        var s = _settingsService.Current;
        _currentTheme = s.Theme;
        _currentLanguage = s.Language;

        ConvertVm = new ConvertViewModel(_settingsService)
        {
            OnShowModal = vm => ActiveDialog = vm,
            OnCloseModal = () => ActiveDialog = null
        };

        StripVm = new StripViewModel(_settingsService)
        {
            OnShowModal = vm => ActiveDialog = vm,
            OnCloseModal = () => ActiveDialog = null
        };

        ToolsVm = new ToolsViewModel(_settingsService);

        ReportVm = new ReportViewModel
        {
            OnSwitchToConvertTab = () => SelectedTabIndex = 0
        };

        // 偏好设置作为独立页面（Tab 4）常驻，保存后回调主窗口把主题/语言应用到全局
        SettingsVm = new SettingsViewModel(_settingsService)
        {
            OnSettingsSaved = s =>
            {
                SetTheme(s.Theme);
                SetLanguage(s.Language);
            }
        };

        // 批次转换完成后回填报告并自动跳转至报告页面展示报表与图表
        ConvertVm.OnBatchReportReady = model =>
        {
            ReportVm.Populate(model);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => SelectedTabIndex = 3);

            // 驱动偏好设置中的完成提醒 / 自动打开输出目录开关
            CompletionEffects.RunOnTaskComplete(_settingsService.Current, model.OutputDirectory);
        };

        LocalizationService.Instance.SetLanguage(_currentLanguage);
        if (Avalonia.Application.Current is not null)
        {
            Avalonia.Application.Current.RequestedThemeVariant = ResolveThemeVariant(_currentTheme);
        }

        SafetyGuard.CleanOrphanTempDirectories();
    }

    [RelayCommand]
    public void SelectTab(object? index)
    {
        SelectedTabIndex = index switch
        {
            int i => i,
            string s when int.TryParse(s, out int p) => p,
            _ => SelectedTabIndex
        };
    }

    [RelayCommand]
    public void SetTheme(string theme)
    {
        CurrentTheme = theme;
        var s = _settingsService.Current;
        s.Theme = theme;
        _settingsService.Save(s);
        if (Avalonia.Application.Current is not null)
        {
            Avalonia.Application.Current.RequestedThemeVariant = ResolveThemeVariant(theme);
        }
    }

    private static Avalonia.Styling.ThemeVariant ResolveThemeVariant(string theme) => theme switch
    {
        "Dark" => Avalonia.Styling.ThemeVariant.Dark,
        "Auto" => Avalonia.Styling.ThemeVariant.Default,
        _ => Avalonia.Styling.ThemeVariant.Light
    };

    [RelayCommand]
    public void SetLanguage(string lang)
    {
        CurrentLanguage = lang;
        var s = _settingsService.Current;
        s.Language = lang;
        _settingsService.Save(s);
        LocalizationService.Instance.SetLanguage(lang);
    }

    [RelayCommand]
    public void TriggerQuickLook()
    {
        ConvertVm.TriggerQuickLook();
    }

    [RelayCommand]
    public void CloseModal()
    {
        ActiveDialog = null;
    }

    [RelayCommand]
    public void MinimizeWindow()
    {
        RequestMinimizeWindow?.Invoke();
    }

    [RelayCommand]
    public void MaximizeWindow()
    {
        RequestMaximizeWindow?.Invoke();
    }

    [RelayCommand]
    public void CloseWindow()
    {
        RequestCloseWindow?.Invoke();
    }

    /// <summary>应用退出时（含标题栏关闭）清理转换临时目录，受 AutoCleanTemp 开关控制。</summary>
    public void HandleAppExit()
    {
        if (_settingsService.Current.AutoCleanTemp)
        {
            SafetyGuard.CleanAllTempDirectories();
        }
    }
}
