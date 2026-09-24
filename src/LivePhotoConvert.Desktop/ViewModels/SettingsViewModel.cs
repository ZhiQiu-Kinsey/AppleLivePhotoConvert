using System.Diagnostics;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Models;
using LivePhotoConvert.Desktop.Services;

namespace LivePhotoConvert.Desktop.ViewModels;

/// <summary>
/// 偏好设置页面视图模型（内建“关于”分页：作者、贡献者、仓库、开源协议与引用项目）。
/// 由主窗口作为独立页面（导航索引 4）承载，保存后立即生效，无需关闭页面。
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    /// <summary>“已保存”轻量提示的展示时长。</summary>
    private static readonly TimeSpan SavedHintDuration = TimeSpan.FromMilliseconds(2200);

    private readonly SettingsService _settingsService;
    private readonly ILocalizer _localizer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLightTheme))]
    [NotifyPropertyChangedFor(nameof(IsDarkTheme))]
    [NotifyPropertyChangedFor(nameof(IsAutoTheme))]
    private string _theme;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChineseLanguage))]
    [NotifyPropertyChangedFor(nameof(IsEnglishLanguage))]
    private string _language;

    [ObservableProperty]
    private int _concurrency;

    [ObservableProperty]
    private int _heicQuality;

    [ObservableProperty]
    private bool _notifyOnComplete;

    [ObservableProperty]
    private bool _autoOpenOutput;

    [ObservableProperty]
    private bool _autoCleanTemp;

    [ObservableProperty]
    private bool _isAboutSelected;

    /// <summary>保存成功后的轻量提示开关（自动熄灭）。</summary>
    [ObservableProperty]
    private bool _isSettingsSaved;

    /// <summary>是否显示居中的赞赏二维码弹窗。</summary>
    [ObservableProperty]
    private bool _isSponsorDialogOpen;

    [ObservableProperty]
    private IReadOnlyList<AboutCredit> _contributors = [];

    [ObservableProperty]
    private IReadOnlyList<AboutCredit> _runtimeCredits = [];

    [ObservableProperty]
    private IReadOnlyList<AboutCredit> _buildCredits = [];

    /// <summary>设置保存后的全局应用回调（由主窗口注入，用于同步主题与语言）。</summary>
    public Action<DesktopSettings>? OnSettingsSaved { get; init; }

    /// <summary>当前程序集版本号（如 2.6.0），用于“关于”页展示。</summary>
    public string AppVersion { get; } = ResolveAppVersion();

    public string AuthorName => AboutInfo.AuthorName;

    public string LicenseName => AboutInfo.LicenseName;

    /// <summary>“偏好设置”分页是否处于选中状态。</summary>
    public bool IsSettingsSelected => !IsAboutSelected;

    public bool IsLightTheme => Theme == "Light";

    public bool IsDarkTheme => Theme == "Dark";

    public bool IsAutoTheme => Theme == "Auto";

    public bool IsChineseLanguage => Language == "zh";

    public bool IsEnglishLanguage => Language == "en";

    public SettingsViewModel(SettingsService settingsService, ILocalizer localizer)
    {
        _settingsService = settingsService;
        _localizer = localizer;
        var current = _settingsService.Current;
        _theme = current.Theme;
        _language = current.Language;
        _concurrency = current.Concurrency;
        _heicQuality = current.HeicQuality;
        _notifyOnComplete = current.NotifyOnComplete;
        _autoOpenOutput = current.AutoOpenOutput;
        _autoCleanTemp = current.AutoCleanTemp;

        RefreshCredits();
        // 语言也可能由标题栏切换，统一以本地化服务的通知为准刷新鸣谢描述
        _localizer.LanguageChanged += (_, _) => RefreshCredits();
    }

    [RelayCommand]
    private void SetTheme(string theme)
    {
        Theme = theme;
        ApplyTheme();
    }

    [RelayCommand]
    private void SetLanguage(string lang)
    {
        Language = lang;
        ApplyLanguage();
    }

    [RelayCommand]
    private void ShowSettings()
    {
        IsAboutSelected = false;
    }

    [RelayCommand]
    private void ShowAbout()
    {
        IsAboutSelected = true;
    }

    [RelayCommand]
    private void OpenSponsorDialog() => IsSponsorDialogOpen = true;

    [RelayCommand]
    private void CloseSponsorDialog() => IsSponsorDialogOpen = false;

    partial void OnIsAboutSelectedChanged(bool value) => OnPropertyChanged(nameof(IsSettingsSelected));

    /// <summary>在系统默认浏览器中打开外部链接（仓库、Issue、许可证与鸣谢项目主页）。</summary>
    [RelayCommand]
    private static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            using var _ = Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception)
        {
            // 外部浏览器不可用或系统策略拦截时静默降级，绝不中断设置页交互。
        }
    }

    /// <summary>持久化偏好设置并立即应用到全局（主题 / 语言），随后展示“已保存”提示。</summary>
    [RelayCommand]
    private void Save()
    {
        var current = _settingsService.Current;
        current.Theme = Theme;
        current.Language = Language;
        current.Concurrency = Concurrency;
        current.HeicQuality = HeicQuality;
        current.NotifyOnComplete = NotifyOnComplete;
        current.AutoOpenOutput = AutoOpenOutput;
        current.AutoCleanTemp = AutoCleanTemp;

        _settingsService.Save(current);
        ApplyLanguage();
        ApplyTheme();

        OnSettingsSaved?.Invoke(current);
        _ = ShowSavedHintAsync();
    }

    /// <summary>即时预览主题；点击保存后才写入磁盘。</summary>
    private void ApplyTheme()
    {
        if (Avalonia.Application.Current is not null)
        {
            // 与 MainWindowViewModel.ResolveThemeVariant 保持一致：Auto 跟随系统（Default），不可误判为 Light
            Avalonia.Application.Current.RequestedThemeVariant = Theme switch
            {
                "Dark" => Avalonia.Styling.ThemeVariant.Dark,
                "Auto" => Avalonia.Styling.ThemeVariant.Default,
                _ => Avalonia.Styling.ThemeVariant.Light
            };
        }
    }

    /// <summary>即时预览语言；鸣谢信息随 LanguageChanged 重建。</summary>
    private void ApplyLanguage() => _localizer.SetLanguage(Language);

    /// <summary>展示“已保存”提示并在固定时长后自动熄灭（失败不影响任何交互）。</summary>
    private async Task ShowSavedHintAsync()
    {
        try
        {
            IsSettingsSaved = true;
            await Task.Delay(SavedHintDuration);
            IsSettingsSaved = false;
        }
        catch (Exception)
        {
            // 提示熄灭失败不影响设置保存结果，静默降级。
        }
    }

    private void RefreshCredits()
    {
        Contributors = MaterializeCredits(AboutInfo.Contributors);
        RuntimeCredits = MaterializeCredits(AboutInfo.RuntimeCredits);
        BuildCredits = MaterializeCredits(AboutInfo.BuildCredits);
    }

    private AboutCredit[] MaterializeCredits(AboutInfo.CreditEntry[] entries)
    {
        var result = new AboutCredit[entries.Length];
        for (int i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            result[i] = new AboutCredit(
                entry.Name,
                _localizer[entry.DescriptionKey],
                entry.Url,
                entry.Badge);
        }

        return result;
    }

    private static string ResolveAppVersion()
    {
        var assembly = typeof(SettingsViewModel).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(informational))
        {
            int plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus > 0 ? informational[..plus] : informational;
        }

        var version = assembly.GetName().Version;
        return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
