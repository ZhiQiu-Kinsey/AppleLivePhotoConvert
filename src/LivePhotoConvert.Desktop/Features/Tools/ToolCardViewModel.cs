using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoConvert.Core.External.Tools;
using LivePhotoConvert.Desktop.Infrastructure;

namespace LivePhotoConvert.Desktop.Features.Tools;

/// <summary>工具能力徽章。</summary>
public sealed record CapabilityBadge(string Label, bool IsSupported);

/// <summary>
/// 依赖页的单个工具卡片：状态、版本、路径、能力与安装进度全部由 <see cref="ToolInfo"/> 与安装进度推导。
/// </summary>
public sealed partial class ToolCardViewModel : ObservableObject
{
    private static readonly string[] DerivedProperties =
    [
        nameof(IsProbing), nameof(IsReady), nameof(IsMissing), nameof(IsUpdateRecommended), nameof(IsExplicitPathInvalid),
        nameof(IsHdrUnavailable), nameof(HasCapabilities), nameof(HasWarning), nameof(IsHealthy),
        nameof(ShowInstallButton), nameof(ShowPlatformUnsupported)
    ];

    private readonly ILocalizer _localizer;
    private readonly string _descriptionKey;
    private readonly Func<ToolCardViewModel, Task> _install;
    private readonly Func<ToolCardViewModel, Task> _pickPath;
    private readonly Action _cancel;
    private ToolInstallProgress? _progress;

    /// <param name="definition">清单中的工具定义</param>
    /// <param name="descriptionKey">工具用途说明的资源键</param>
    /// <param name="isPlatformUnsupported">清单没有当前平台的下载包</param>
    /// <param name="localizer">文案</param>
    /// <param name="install">安装或升级</param>
    /// <param name="pickPath">手动指定路径</param>
    /// <param name="cancel">取消进行中的安装</param>
    public ToolCardViewModel(
        ToolDefinition definition,
        string descriptionKey,
        bool isPlatformUnsupported,
        ILocalizer localizer,
        Func<ToolCardViewModel, Task> install,
        Func<ToolCardViewModel, Task> pickPath,
        Action cancel)
    {
        Tool = definition.Id;
        DisplayName = definition.DisplayName;
        ExecutableName = definition.ExecutableFileName;
        IsPlatformUnsupported = isPlatformUnsupported;
        _localizer = localizer;
        _descriptionKey = descriptionKey;
        _install = install;
        _pickPath = pickPath;
        _cancel = cancel;
        ApplyTexts();
    }

    public ToolId Tool { get; }

    /// <summary>产品名，不做本地化。</summary>
    public string DisplayName { get; }

    public string ExecutableName { get; }

    /// <summary>当前平台没有可安装的下载包（只能用系统包管理器或手动指定）。</summary>
    public bool IsPlatformUnsupported { get; }

    [ObservableProperty]
    private ToolInfo? _info;

    [ObservableProperty]
    private string _description = string.Empty;

    /// <summary>状态徽章文字：版本号、未安装或检测中。</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>工具报告的完整版本文本，徽章被截断时从提示中查看。</summary>
    [ObservableProperty]
    private string _versionDetail = string.Empty;

    [ObservableProperty]
    private string _pathText = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<CapabilityBadge> _capabilities = [];

    [ObservableProperty]
    private string _updateText = string.Empty;

    [ObservableProperty]
    private string _installButtonText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInstallButton))]
    private bool _isInstalling;

    [ObservableProperty]
    private string _progressText = string.Empty;

    /// <summary>0~100。</summary>
    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private bool _isProgressIndeterminate = true;

    /// <summary>页面允许发起安装（没有其它安装、没有运行中的任务）。</summary>
    [ObservableProperty]
    private bool _canStartInstall = true;

    public bool IsProbing => Info is null;

    public bool IsReady => Info is { IsAvailable: true };

    public bool IsMissing => Info is { IsAvailable: false };

    public bool IsUpdateRecommended => Info is { IsUpdateRecommended: true };

    public bool IsExplicitPathInvalid => Info is { IsExplicitPathInvalid: true };

    /// <summary>找到了 FFmpeg 但缺少 HDR 色调映射或 10-bit HEVC；探测本身失败时不下结论。</summary>
    public bool IsHdrUnavailable => Tool == ToolId.Ffmpeg && Info is { IsAvailable: true, ProbeError: null } info && !info.Has(ToolCapabilities.Hdr);

    public bool HasCapabilities => Capabilities.Count > 0;

    public bool HasWarning => IsReady && (IsUpdateRecommended || IsExplicitPathInvalid || IsHdrUnavailable || Info?.ProbeError is not null);

    public bool IsHealthy => IsReady && !HasWarning;

    public bool ShowInstallButton => !IsPlatformUnsupported && !IsInstalling && (IsMissing || IsUpdateRecommended);

    public bool ShowPlatformUnsupported => IsPlatformUnsupported && IsMissing;

    public void Apply(ToolInfo info)
    {
        Info = info;
        ApplyTexts();
        NotifyDerived();
    }

    /// <summary>语言切换后重建全部文案。</summary>
    public void ApplyTexts()
    {
        Description = _localizer[_descriptionKey];
        PathText = Info?.Path ?? (Info is null ? string.Empty : _localizer.Format("ToolNotDetectedFormat", ExecutableName));
        (StatusText, VersionDetail) = Info switch
        {
            null => (_localizer["ToolProbing"], string.Empty),
            { IsAvailable: false } => (_localizer["ToolNotInstalled"], string.Empty),
            { Version: { } version, VersionText: var text } => (version.ToString(), text ?? string.Empty),
            { VersionText: { Length: > 0 } text } => (text, text),
            _ => (_localizer["ToolVersionUnknown"], Info.ProbeError ?? string.Empty)
        };
        Capabilities = BuildCapabilities();
        var recommended = Info?.RecommendedVersion ?? string.Empty;
        UpdateText = _localizer.Format("ToolUpdateAvailableFormat", recommended);
        InstallButtonText = IsReady && IsUpdateRecommended
            ? _localizer.Format("ToolUpgradeBtnFormat", recommended)
            : _localizer["InstallToolBtn"];
        if (_progress is { } progress)
        {
            ProgressText = ToolTexts.Stage(_localizer, progress);
        }
    }

    internal void BeginInstall()
    {
        _progress = null;
        ProgressText = _localizer["ToolStagePreparing"];
        ProgressValue = 0;
        IsProgressIndeterminate = true;
        IsInstalling = true;
    }

    internal void ReportProgress(ToolInstallProgress progress)
    {
        // 进度经界面线程排队投递，可能晚于安装结束到达
        if (!IsInstalling)
        {
            return;
        }

        _progress = progress;
        ProgressText = ToolTexts.Stage(_localizer, progress);
        if (progress.Fraction is { } fraction)
        {
            ProgressValue = fraction * 100;
            IsProgressIndeterminate = false;
        }
        else if (progress.Stage == ToolInstallStage.Downloading)
        {
            IsProgressIndeterminate = true;
        }
    }

    internal void EndInstall()
    {
        _progress = null;
        IsInstalling = false;
        ProgressText = string.Empty;
    }

    [RelayCommand]
    private Task InstallAsync() => _install(this);

    [RelayCommand]
    private Task PickPathAsync() => _pickPath(this);

    [RelayCommand]
    private void Cancel() => _cancel();

    private IReadOnlyList<CapabilityBadge> BuildCapabilities()
    {
        if (Tool != ToolId.Ffmpeg || Info is not { IsAvailable: true, ProbeError: null } info)
        {
            return [];
        }

        return
        [
            new(_localizer["ToolCapabilityHdrTonemap"], info.Has(ToolCapabilities.Zscale | ToolCapabilities.Tonemap)),
            new(_localizer["ToolCapabilityHevc10Bit"], info.Has(ToolCapabilities.Libx265 | ToolCapabilities.Libx265TenBit))
        ];
    }

    private void NotifyDerived()
    {
        foreach (var name in DerivedProperties)
        {
            OnPropertyChanged(name);
        }
    }
}
