using System.Globalization;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoConvert.Core.Media.Thumbnails;
using LivePhotoConvert.Core.Services;
using LivePhotoConvert.Desktop.Converters;
using LivePhotoConvert.Desktop.Features.Dialogs;
using LivePhotoConvert.Desktop.Features.Library.Thumbnails;
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
    private readonly IThumbnailPipeline _thumbnails;
    private readonly ThumbnailDiskCache _thumbnailCache;
    private CancellationTokenSource? _savedHintCts;
    private int _usageVersion;
    private long? _cacheUsageBytes;

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

    /// <summary>缩略图位图的内存预算（MB）；正在显示的卡片不受限。</summary>
    [ObservableProperty]
    private int _thumbnailBudgetMb;

    /// <summary>缩略图磁盘缓存上限（MB）。</summary>
    [ObservableProperty]
    private int _thumbnailDiskCacheMb;

    /// <summary>正在统计或清理磁盘缓存。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CacheUsageText))]
    [NotifyCanExecuteChangedFor(nameof(ClearThumbnailCacheCommand))]
    private bool _isCacheBusy;

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

    public SettingsViewModel(SettingsStore settings, ILocalizer localizer, ThemeService theme, IShellLauncher shell, IDialogService dialogs,
        IThumbnailPipeline thumbnails, ThumbnailDiskCache thumbnailCache, INavigator navigator)
    {
        _settings = settings;
        _localizer = localizer;
        _themeService = theme;
        _shell = shell;
        _dialogs = dialogs;
        _thumbnails = thumbnails;
        _thumbnailCache = thumbnailCache;

        var current = settings.Current;
        _theme = current.Theme;
        _language = current.Language;
        _concurrency = current.Concurrency;
        _notifyOnComplete = current.NotifyOnComplete;
        _autoOpenOutput = current.AutoOpenOutput;
        _autoCleanTemp = current.AutoCleanTemp;
        _thumbnailBudgetMb = Math.Clamp(current.Gallery.ThumbnailBudgetMb, GalleryPreferences.MinThumbnailBudgetMb, GalleryPreferences.MaxThumbnailBudgetMb);
        _thumbnailDiskCacheMb = Math.Clamp(current.Gallery.ThumbnailDiskCacheMb, GalleryPreferences.MinThumbnailDiskCacheMb, GalleryPreferences.MaxThumbnailDiskCacheMb);

        RefreshCredits();
        _localizer.LanguageChanged += (_, _) =>
        {
            RefreshCredits();
            OnPropertyChanged(nameof(CacheUsageText));
        };

        // 占用只在进入设置页时统计：遍历缓存目录有成本，不放在启动路径上
        navigator.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(INavigator.Current) && navigator.Current == AppPage.Settings)
            {
                _ = RefreshCacheUsageAsync();
            }
        };
    }

    public int MinThumbnailBudgetMb => GalleryPreferences.MinThumbnailBudgetMb;

    public int MaxThumbnailBudgetMb => GalleryPreferences.MaxThumbnailBudgetMb;

    public int MinThumbnailDiskCacheMb => GalleryPreferences.MinThumbnailDiskCacheMb;

    public int MaxThumbnailDiskCacheMb => GalleryPreferences.MaxThumbnailDiskCacheMb;

    /// <summary>磁盘缓存当前占用；统计中或尚未统计时显示对应提示。</summary>
    public string CacheUsageText => IsCacheBusy || _cacheUsageBytes is not { } bytes
        ? _localizer["ThumbnailCacheMeasuring"]
        : _localizer.Format("ThumbnailCacheUsageFormat", ByteSizeConverter.Instance.Convert(bytes, typeof(string), null, CultureInfo.InvariantCulture));

    /// <summary>当前统计任务；测试据此等待后台统计结束。</summary>
    public Task CacheUsageTask { get; private set; } = Task.CompletedTask;

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

    partial void OnThumbnailBudgetMbChanged(int value)
    {
        Save(s => s.Gallery.ThumbnailBudgetMb = value);
        // 调小时立即驱逐未钉住的位图，正在显示的卡片不受影响
        _thumbnails.BudgetBytes = _settings.Current.Gallery.ThumbnailBudgetBytes;
    }

    partial void OnThumbnailDiskCacheMbChanged(int value)
    {
        Save(s => s.Gallery.ThumbnailDiskCacheMb = value);
        _thumbnailCache.CapacityBytes = _settings.Current.Gallery.ThumbnailDiskCacheBytes;
        // 统计会顺带把超出新上限的旧文件清掉
        _ = RefreshCacheUsageAsync();
    }

    /// <summary>后台统计磁盘缓存占用；超出上限时按最近使用清理到上限以内。</summary>
    public Task RefreshCacheUsageAsync() => RunCacheOperationAsync(() => _thumbnailCache.Trim().BytesAfter);

    /// <summary>
    /// 删除磁盘缓存文件。已加载的缩略图位图在内存里，卡片继续显示；之后需要的档位按需重新生成并写回缓存。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanClearThumbnailCache))]
    private Task ClearThumbnailCacheAsync() => RunCacheOperationAsync(() =>
    {
        _thumbnailCache.Clear();
        return Math.Max(0, _thumbnailCache.ApproximateSizeBytes);
    });

    private bool CanClearThumbnailCache() => !IsCacheBusy;

    private Task RunCacheOperationAsync(Func<long> operation)
    {
        var version = ++_usageVersion;
        IsCacheBusy = true;
        CacheUsageTask = RunAsync();
        return CacheUsageTask;

        async Task RunAsync()
        {
            long? bytes = null;
            try
            {
                bytes = await Task.Run(operation);
            }
            catch (Exception ex)
            {
                // 未等待的任务：失败只记录，占用显示保留上一次的结果
                ErrorLogger.Log(ex, "统计缩略图缓存");
            }

            // 只有最后一次操作的结果写回界面，先发起的统计晚到时不覆盖
            if (version == _usageVersion)
            {
                _cacheUsageBytes = bytes ?? _cacheUsageBytes;
                IsCacheBusy = false;
                OnPropertyChanged(nameof(CacheUsageText));
            }
        }
    }

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

    private AboutCredit[] MaterializeCredits(AboutInfo.CreditEntry[] entries) =>
        [.. entries.Select(entry => new AboutCredit(entry.Name, _localizer[entry.DescriptionKey], entry.Url, entry.Badge))];

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
