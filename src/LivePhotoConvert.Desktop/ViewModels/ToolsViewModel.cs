using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Services;

namespace LivePhotoConvert.Desktop.ViewModels;

public sealed record MirrorPreset(string Name, string Url);

/// <summary>三大外部引擎的标识，用于统一驱动安装与自定义路径逻辑。</summary>
public enum ToolKind
{
    ExifTool,
    Ffmpeg,
    HeifEnc
}

public sealed partial class ToolsViewModel : ViewModelBase
{
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(5)
    };
    private readonly SettingsService _settingsService;
    private readonly ILocalizer _localizer;

    // 最近一次探测结果；语言切换时据此重新生成状态文案，无需再次启动外部进程
    private bool _hasProbed;
    private ToolProbe _exifProbe;
    private ToolProbe _ffmpegProbe;
    private ToolProbe _heifProbe;

    /// <summary>探测结果：路径为 null 表示未找到；版本为 null 表示找到但读不出版本号。</summary>
    private readonly record struct ToolProbe(string? Path, string? Version);

    // 镜像名称的资源键与地址；空地址表示手动输入
    private static readonly (string NameKey, string Url)[] MirrorPresetSources =
    [
        ("MirrorPresetGhfast", "https://ghfast.top/"),
        ("MirrorPresetGhproxy", "https://ghproxy.net/"),
        ("MirrorPresetMirrorGhproxy", "https://mirror.ghproxy.com/"),
        ("MirrorPresetGithub", "https://github.com/"),
        ("MirrorPresetCustom", "")
    ];

    /// <summary>当前进行中的安装任务取消源（同一时刻只允许一个安装，没有任务时为 null）。</summary>
    private CancellationTokenSource? _installCts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AllToolsReady))]
    private bool _isExifToolReady;

    [ObservableProperty]
    private string _exifToolVersion = string.Empty;

    [ObservableProperty]
    private string _exifToolPath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AllToolsReady))]
    private bool _isFfmpegReady;

    [ObservableProperty]
    private string _ffmpegVersion = string.Empty;

    [ObservableProperty]
    private string _ffmpegPath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AllToolsReady))]
    private bool _isHeifEncReady;

    [ObservableProperty]
    private string _heifEncVersion = string.Empty;

    [ObservableProperty]
    private string _heifEncPath = string.Empty;

    [ObservableProperty]
    private bool _autoDownload;

    [ObservableProperty]
    private IReadOnlyList<MirrorPreset> _mirrorPresets;

    [ObservableProperty]
    private MirrorPreset? _selectedMirrorPreset;

    // 仅替换显示名称时不能把预设地址写回，否则会覆盖用户手动输入的镜像地址
    private bool _isRebuildingMirrorPresets;

    partial void OnSelectedMirrorPresetChanged(MirrorPreset? value)
    {
        if (!_isRebuildingMirrorPresets && value is not null && !string.IsNullOrWhiteSpace(value.Url))
        {
            CustomMirrorUrl = value.Url;
            _settingsService.Current.CustomMirrorUrl = value.Url;
            _settingsService.Save();
        }
    }

    [ObservableProperty]
    private string _customMirrorUrl = "https://ghfast.top/";

    partial void OnCustomMirrorUrlChanged(string value)
    {
        _settingsService.Current.CustomMirrorUrl = value;
        _settingsService.Save();
    }

    [ObservableProperty]
    private string _pingLatencyText = string.Empty;

    [ObservableProperty]
    private bool _isPingHealthy;

    // ===== 单引擎安装状态（驱动按钮禁用态与“正在下载...”文案，防重复点击） =====

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInstallingAny))]
    private bool _isInstallingExifTool;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInstallingAny))]
    private bool _isInstallingFfmpeg;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInstallingAny))]
    private bool _isInstallingHeifEnc;

    /// <summary>下载进度文本（已下载 / 总大小），仅在安装期间展示。</summary>
    [ObservableProperty]
    private string _installProgressText = string.Empty;

    /// <summary>引擎操作结果提示（安装完成 / 失败原因 / 自定义路径结果）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActionMessage))]
    private string _actionMessageText = string.Empty;

    /// <summary>当前提示是否为错误（决定图标与配色）。</summary>
    [ObservableProperty]
    private bool _isActionMessageError;

    /// <summary>是否存在需要展示的操作结果提示。</summary>
    public bool HasActionMessage => !string.IsNullOrEmpty(ActionMessageText);

    /// <summary>是否有任一引擎正在安装。</summary>
    public bool IsInstallingAny => IsInstallingExifTool || IsInstallingFfmpeg || IsInstallingHeifEnc;

    /// <summary>三大核心引擎（ExifTool / FFmpeg / heif-enc）是否全部就绪。</summary>
    public bool AllToolsReady => IsExifToolReady && IsFfmpegReady && IsHeifEncReady;

    /// <summary>由主窗口注入的“选择引擎可执行文件”系统文件选择器。</summary>
    public Func<Task<string?>>? RequestPickToolFile { get; set; }

    /// <summary>
    /// 引擎可执行文件的可用性探测委托，默认走 Core 的真实启动探测。
    /// 与 <see cref="RequestPickToolFile"/> 一样作为可替换成员暴露，便于在无真实引擎的环境下验证路径回写逻辑。
    /// </summary>
    public Func<string, bool> ValidateToolExecutable { get; set; } = Core.External.ToolLocator.IsValidTool;

    public ToolsViewModel(SettingsService settingsService, ILocalizer localizer)
    {
        _settingsService = settingsService;
        _localizer = localizer;
        _mirrorPresets = BuildMirrorPresets();
        var s = _settingsService.Current;
        _autoDownload = s.AutoDownloadDependencies;
        if (!string.IsNullOrWhiteSpace(s.CustomMirrorUrl))
        {
            _customMirrorUrl = s.CustomMirrorUrl;
        }

        var normalized = _customMirrorUrl.TrimEnd('/') + "/";
        _selectedMirrorPreset = MirrorPresets.FirstOrDefault(p =>
            !string.IsNullOrEmpty(p.Url) &&
            (p.Url.Equals(normalized, StringComparison.OrdinalIgnoreCase) || p.Url.Equals(_customMirrorUrl, StringComparison.OrdinalIgnoreCase)))
            ?? MirrorPresets.Last();

        _localizer.LanguageChanged += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(ApplyLocalizedTexts);
        _ = RescanToolsAsync();
    }

    private IReadOnlyList<MirrorPreset> BuildMirrorPresets() =>
        [.. MirrorPresetSources.Select(p => new MirrorPreset(_localizer[p.NameKey], p.Url))];

    private void ApplyLocalizedTexts()
    {
        // 名称随语言变化而顺序不变，按位置重新选中（自定义项地址为空，无法按地址匹配）
        int selectedIndex = SelectedMirrorPreset is { } selected ? MirrorPresets.ToList().IndexOf(selected) : -1;
        _isRebuildingMirrorPresets = true;
        try
        {
            MirrorPresets = BuildMirrorPresets();
            SelectedMirrorPreset = selectedIndex >= 0 ? MirrorPresets[selectedIndex] : MirrorPresets[^1];
        }
        finally
        {
            _isRebuildingMirrorPresets = false;
        }
        ApplyToolTexts();
    }

    [RelayCommand]
    public async Task TestMirrorSpeedAsync()
    {
        PingLatencyText = _localizer["PingTesting"];
        IsPingHealthy = false;

        try
        {
            var targetUrl = CustomMirrorUrl.Trim();
            if (string.IsNullOrWhiteSpace(targetUrl))
            {
                targetUrl = "https://github.com";
            }
            else if (!targetUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                     !targetUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                targetUrl = "https://" + targetUrl;
            }

            var sw = Stopwatch.StartNew();
            using var req = new HttpRequestMessage(HttpMethod.Head, targetUrl);
            var res = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            sw.Stop();

            long ms = sw.ElapsedMilliseconds;
            PingLatencyText = $"{ms}ms (HTTP {(int)res.StatusCode})";
            IsPingHealthy = res.IsSuccessStatusCode || (int)res.StatusCode < 500;
        }
        catch (Exception ex)
        {
            PingLatencyText = _localizer.Format("PingFailedFormat", ex.Message);
            IsPingHealthy = false;
        }
    }

    [RelayCommand]
    public async Task RescanToolsAsync()
    {
        ToolProbe exif = default, ffmpeg = default, heif = default;

        // 优先使用用户在设置中显式指定的路径，失效时自动回退到程序目录 / PATH 探测
        var saved = _settingsService.Current;
        var explicitExif = NormalizeExplicitPath(saved.ExifToolPath);
        var explicitFfmpeg = NormalizeExplicitPath(saved.FfmpegPath);
        var explicitHeif = NormalizeExplicitPath(saved.HeifEncPath);

        // 探测版本需同步调用外部进程，放到后台线程执行，避免阻塞 UI
        await Task.Run(() =>
        {
            exif = Probe(Core.Metadata.ExifToolMetadataService.ExecutableName, explicitExif, "-ver");
            ffmpeg = Probe(Core.External.FfmpegVideoConverter.ExecutableName, explicitFfmpeg, "-version");
            heif = Probe(Core.External.HeifEncImageConverter.ExecutableName, explicitHeif, "-v");
        });

        // async 延续回到 Avalonia 调度上下文（UI 线程），避免后台线程写 ObservableProperty 引发跨线程异常
        _exifProbe = exif;
        _ffmpegProbe = ffmpeg;
        _heifProbe = heif;
        _hasProbed = true;
        ApplyToolTexts();
    }

    private void ApplyToolTexts()
    {
        if (!_hasProbed)
        {
            return;
        }

        IsExifToolReady = _exifProbe.Path is not null;
        ExifToolPath = DescribePath(_exifProbe, Core.Metadata.ExifToolMetadataService.ExecutableName);
        ExifToolVersion = DescribeVersion(_exifProbe);

        IsFfmpegReady = _ffmpegProbe.Path is not null;
        FfmpegPath = DescribePath(_ffmpegProbe, Core.External.FfmpegVideoConverter.ExecutableName);
        FfmpegVersion = DescribeVersion(_ffmpegProbe);

        IsHeifEncReady = _heifProbe.Path is not null;
        HeifEncPath = DescribePath(_heifProbe, Core.External.HeifEncImageConverter.ExecutableName);
        HeifEncVersion = DescribeVersion(_heifProbe);
    }

    private string DescribePath(ToolProbe probe, string executableName) =>
        probe.Path ?? _localizer.Format("ToolNotDetectedFormat", executableName);

    private string DescribeVersion(ToolProbe probe) => probe switch
    {
        { Path: null } => _localizer["ToolNotInstalled"],
        { Version: { } version } => version,
        _ => _localizer["ToolReady"]
    };

    private static ToolProbe Probe(string executableName, string? explicitPath, string versionArg)
    {
        var path = FindTool(executableName, explicitPath);
        return new ToolProbe(path, path is null ? null : ProbeVersion(path, versionArg));
    }

    /// <summary>空白路径视为“未指定”，返回 null 交给自动发现逻辑。</summary>
    private static string? NormalizeExplicitPath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : path;

    /// <summary>先按显式路径定位，显式路径缺失或无效时回退到程序目录 / PATH 自动发现。</summary>
    private static string? FindTool(string executableName, string? explicitPath) =>
        Core.External.ToolLocator.Find(executableName, explicitPath)
        ?? Core.External.ToolLocator.Find(executableName);

    private static string? ProbeVersion(string executablePath, string versionArg)
    {
        try
        {
            using var proc = new Process();
            proc.StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = versionArg,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            if (proc.Start())
            {
                var line = proc.StandardOutput.ReadLine();
                proc.WaitForExit(1000);
                if (!string.IsNullOrWhiteSpace(line))
                {
                    return line.Trim();
                }
            }
        }
        catch
        {
            // 忽略探测异常，由调用方显示"已就绪"
        }
        return null;
    }

    // ===== 单引擎一键安装（未就绪时由卡片按钮触发） =====

    [RelayCommand]
    public async Task InstallExifToolAsync() =>
        await InstallToolAsync(ToolKind.ExifTool, Core.External.ExternalToolMetadata.ExifTool);

    [RelayCommand]
    public async Task InstallFfmpegAsync() =>
        await InstallToolAsync(ToolKind.Ffmpeg, Core.External.ExternalToolMetadata.FFmpeg);

    [RelayCommand]
    public async Task InstallHeifEncAsync() =>
        await InstallToolAsync(ToolKind.HeifEnc, Core.External.ExternalToolMetadata.HeifEnc);

    /// <summary>取消当前进行中的引擎下载（安装中按钮旁的“取消”入口）。</summary>
    [RelayCommand]
    private void CancelInstall() => _installCts?.Cancel();

    // ===== 自定义指定路径（修复卡片按钮无命令的问题） =====

    [RelayCommand]
    public async Task PickExifToolPathAsync() => await PickCustomToolPathAsync(ToolKind.ExifTool);

    [RelayCommand]
    public async Task PickFfmpegPathAsync() => await PickCustomToolPathAsync(ToolKind.Ffmpeg);

    [RelayCommand]
    public async Task PickHeifEncPathAsync() => await PickCustomToolPathAsync(ToolKind.HeifEnc);

    /// <summary>
    /// 下载并解压指定引擎，全程异步且可取消；完成后回写路径设置并自动重扫刷新状态。
    /// 任何异常均被捕获并以提示文案呈现，绝不向外抛出未观察异常。
    /// </summary>
    private async Task InstallToolAsync(ToolKind kind, Core.External.ToolDownloadInfo info)
    {
        // 安装进度文本与取消源为三个引擎共用，因此同一时刻只放行一个安装任务，
        // 避免并发下载互相覆盖进度或使先结束的任务把仍在运行的任务的取消源置空。
        if (IsInstallingAny)
        {
            return;
        }
        SetInstalling(kind, true);
        InstallProgressText = string.Empty;
        IsActionMessageError = false;
        ActionMessageText = _localizer.Format("InstallStartingFormat", info.ToolName);

        using var cts = new CancellationTokenSource();
        _installCts = cts;
        try
        {
            var progress = new Progress<Core.External.DownloadProgressReport>(report =>
                InstallProgressText = FormatProgress(report));

            var installedPath = await Core.External.ToolDownloader.DownloadAndExtractAsync(
                info, CustomMirrorUrl, progress, null, cts.Token);

            ApplyCustomToolPath(kind, installedPath);
            IsActionMessageError = false;
            ActionMessageText = _localizer.Format("InstallCompletedFormat", info.ToolName);
        }
        catch (OperationCanceledException)
        {
            IsActionMessageError = true;
            ActionMessageText = _localizer["InstallCanceled"];
        }
        catch (Exception ex)
        {
            IsActionMessageError = true;
            ActionMessageText = _localizer.Format("InstallFailedFormat", info.ToolName, ex.Message);
        }
        finally
        {
            // 仅当取消源仍归属本次安装时才清空，避免误伤后续任务
            if (ReferenceEquals(_installCts, cts))
            {
                _installCts = null;
            }

            InstallProgressText = string.Empty;
            SetInstalling(kind, false);

            // 重扫失败不得穿透到 AsyncRelayCommand 的 async void Execute（会导致 UI 线程未处理异常）
            try
            {
                await RescanToolsAsync();
            }
            catch (Exception)
            {
                // 引擎探测异常不影响安装结果提示，静默降级为保留上一次状态
            }
        }
    }

    /// <summary>调用系统文件选择器为指定引擎选择可执行文件，校验通过后写入设置并重扫。</summary>
    private async Task PickCustomToolPathAsync(ToolKind kind)
    {
        if (RequestPickToolFile is null)
        {
            return;
        }

        var file = await RequestPickToolFile();
        if (string.IsNullOrWhiteSpace(file))
        {
            return;
        }

        // 三重校验：文件必须存在 → 文件名必须与该引擎期望的可执行文件名匹配 → 真实启动探测必须可用。
        // 仅靠 IsValidTool 不够：它对「文件名不含引擎关键字」的任意存在文件一律返回 true，
        // 会把 notes.txt / notepad.exe 误判为有效引擎。
        if (!File.Exists(file) || !MatchesExpectedExecutable(kind, file) || !ValidateToolExecutable(file))
        {
            IsActionMessageError = true;
            ActionMessageText = _localizer.Format("ToolPathInvalidFormat", file);
            return;
        }

        ApplyCustomToolPath(kind, file);
        IsActionMessageError = false;
        ActionMessageText = _localizer.Format("ToolPathAppliedFormat", GetDisplayName(kind));
        await RescanToolsAsync();
    }

    /// <summary>该引擎期望的可执行文件名（如 exiftool.exe）。</summary>
    private static string ExpectedExecutableName(ToolKind kind) => kind switch
    {
        ToolKind.ExifTool => Core.Metadata.ExifToolMetadataService.ExecutableName,
        ToolKind.Ffmpeg => Core.External.FfmpegVideoConverter.ExecutableName,
        _ => Core.External.HeifEncImageConverter.ExecutableName
    };

    /// <summary>
    /// 校验所选文件的文件名是否与该引擎期望的可执行文件名匹配（忽略大小写）。
    /// 除完全相等外，还接受同前缀形态，以兼容 Windows 下省略 .exe 后缀与 ExifTool 官方发行包的 exiftool(-k).exe。
    /// </summary>
    private static bool MatchesExpectedExecutable(ToolKind kind, string path)
    {
        var expected = Path.GetFileName(ExpectedExecutableName(kind));
        var actual = Path.GetFileName(path);
        if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var expectedStem = Path.GetFileNameWithoutExtension(expected);
        var actualStem = Path.GetFileNameWithoutExtension(actual);
        return expectedStem.Length > 0
               && actualStem.StartsWith(expectedStem, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>将引擎路径写入设置并持久化（安装完成与手动指定共用）。</summary>
    private void ApplyCustomToolPath(ToolKind kind, string path)
    {
        var s = _settingsService.Current;
        switch (kind)
        {
            case ToolKind.ExifTool:
                s.ExifToolPath = path;
                break;
            case ToolKind.Ffmpeg:
                s.FfmpegPath = path;
                break;
            default:
                s.HeifEncPath = path;
                break;
        }

        _settingsService.Save(s);
    }

    private void SetInstalling(ToolKind kind, bool value)
    {
        switch (kind)
        {
            case ToolKind.ExifTool:
                IsInstallingExifTool = value;
                break;
            case ToolKind.Ffmpeg:
                IsInstallingFfmpeg = value;
                break;
            default:
                IsInstallingHeifEnc = value;
                break;
        }
    }

    /// <summary>把下载进度报告格式化为「已下载 MB / 总 MB」文本。</summary>
    private static string FormatProgress(Core.External.DownloadProgressReport report)
    {
        const double Mb = 1024.0 * 1024.0;
        double downloaded = report.DownloadedBytes / Mb;
        return report.TotalBytes is > 0
            ? $"{downloaded:F1} MB / {report.TotalBytes.Value / Mb:F1} MB"
            : $"{downloaded:F1} MB";
    }

    private static string GetDisplayName(ToolKind kind) => kind switch
    {
        ToolKind.ExifTool => "ExifTool",
        ToolKind.Ffmpeg => "FFmpeg",
        _ => "heif-enc"
    };

    [RelayCommand]
    public void SetMirrorPreset(string url)
    {
        CustomMirrorUrl = url;
        _settingsService.Current.CustomMirrorUrl = url;
        _settingsService.Save(_settingsService.Current);
    }
}
