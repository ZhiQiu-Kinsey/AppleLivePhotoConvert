using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoConvert.Desktop.Features.Dialogs;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Models;

namespace LivePhotoConvert.Desktop.Features.Settings;

/// <summary>
/// 偏好设置与“关于”页。修改即时生效并自动保存。
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private static readonly TimeSpan SavedHintDuration = TimeSpan.FromMilliseconds(2200);

    private readonly SettingsStore _settings;
    private readonly ILocalizer _localizer;
    private readonly ThemeService _themeService;
    private readonly IShellLauncher _shell;
    private readonly IDialogService _dialogs;
    private CancellationTokenSource? _savedHintCts;

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
    private bool _notifyOnComplete;

    [ObservableProperty]
    private bool _autoOpenOutput;

    [ObservableProperty]
    private bool _autoCleanTemp;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSettingsSelected))]
    private bool _isAboutSelected;

    /// <summary>“已自动保存”提示，最后一次修改后一段时间自动熄灭。</summary>
    [ObservableProperty]
    private bool _isSettingsSaved;

    [ObservableProperty]
    private IReadOnlyList<AboutCredit> _contributors = [];

    [ObservableProperty]
    private IReadOnlyList<AboutCredit> _runtimeCredits = [];

    [ObservableProperty]
    private IReadOnlyList<AboutCredit> _buildCredits = [];

    public SettingsViewModel(SettingsStore settings, ILocalizer localizer, ThemeService theme, IShellLauncher shell, IDialogService dialogs)
    {
        _settings = settings;
        _localizer = localizer;
        _themeService = theme;
        _shell = shell;
        _dialogs = dialogs;

        var current = settings.Current;
        _theme = current.Theme;
        _language = current.Language;
        _concurrency = current.Concurrency;
        _notifyOnComplete = current.NotifyOnComplete;
        _autoOpenOutput = current.AutoOpenOutput;
        _autoCleanTemp = current.AutoCleanTemp;

        RefreshCredits();
        _localizer.LanguageChanged += (_, _) => RefreshCredits();
    }

    public string AppVersion { get; } = ResolveAppVersion();

    public string AuthorName => AboutInfo.AuthorName;

    public string LicenseName => AboutInfo.LicenseName;

    public bool IsSettingsSelected => !IsAboutSelected;

    public bool IsLightTheme => Theme == ThemeService.Light;

    public bool IsDarkTheme => Theme == ThemeService.Dark;

    public bool IsAutoTheme => Theme == ThemeService.Auto;

    public bool IsChineseLanguage => Language == "zh";

    public bool IsEnglishLanguage => Language == "en";

    [RelayCommand]
    private void SetTheme(string theme) => Theme = theme;

    [RelayCommand]
    private void SetLanguage(string lang) => Language = lang;

    [RelayCommand]
    private void ShowSettings() => IsAboutSelected = false;

    [RelayCommand]
    private void ShowAbout() => IsAboutSelected = true;

    [RelayCommand]
    private Task OpenSponsorDialogAsync() => _dialogs.ShowAsync(new DonateDialogViewModel());

    [RelayCommand]
    private async Task OpenUrlAsync(string? url)
    {
        if (!string.IsNullOrWhiteSpace(url) && !_shell.OpenUri(url))
        {
            await _dialogs.AlertAsync(_localizer["ShellOpenFailedTitle"], _localizer.Format("ShellOpenFailedFormat", url), _localizer["ConfirmDialogOk"]);
        }
    }

    partial void OnThemeChanged(string value)
    {
        _themeService.Apply(value);
        Save(s => s.Theme = value);
    }

    partial void OnLanguageChanged(string value)
    {
        _localizer.SetLanguage(value);
        Save(s => s.Language = value);
    }

    partial void OnConcurrencyChanged(int value) => Save(s => s.Concurrency = value);

    partial void OnNotifyOnCompleteChanged(bool value) => Save(s => s.NotifyOnComplete = value);

    partial void OnAutoOpenOutputChanged(bool value) => Save(s => s.AutoOpenOutput = value);

    partial void OnAutoCleanTempChanged(bool value) => Save(s => s.AutoCleanTemp = value);

    private void Save(Action<DesktopSettings> change)
    {
        _settings.Update(change);
        _ = ShowSavedHintAsync();
    }

    private async Task ShowSavedHintAsync()
    {
        _savedHintCts?.Cancel();
        var cts = new CancellationTokenSource();
        _savedHintCts = cts;
        IsSettingsSaved = true;
        try
        {
            await Task.Delay(SavedHintDuration, cts.Token);
            IsSettingsSaved = false;
        }
        catch (OperationCanceledException)
        {
            // 新的修改接管了提示的计时
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
