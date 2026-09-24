using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoConvert.Core.External;
using LivePhotoConvert.Core.External.Tools;
using LivePhotoConvert.Core.Services;
using LivePhotoConvert.Desktop.Infrastructure;

namespace LivePhotoConvert.Desktop.Features.Tools;

public sealed record MirrorPreset(string Name, string Url);

/// <summary>
/// 依赖引擎页：工具状态来自 <see cref="IToolRegistry"/>，安装经 <see cref="IToolInstaller"/>。
/// </summary>
public sealed partial class ToolsViewModel : ViewModelBase
{
    /// <summary>镜像地址停止输入多久后才写入设置。</summary>
    public static readonly TimeSpan MirrorSaveDelay = TimeSpan.FromMilliseconds(600);

    /// <summary>预览进程被结束后通常立即退出；卡住时照常安装，替换失败由安装器回滚。</summary>
    public static readonly TimeSpan ReleaseIdleTimeout = TimeSpan.FromSeconds(5);

    private static readonly HttpClient SharedPingClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    // 镜像名称的资源键与地址；空地址表示手动输入
    private static readonly (string NameKey, string Url)[] MirrorPresetSources =
    [
        ("MirrorPresetGhfast", "https://ghfast.top/"),
        ("MirrorPresetGhproxy", "https://ghproxy.net/"),
        ("MirrorPresetMirrorGhproxy", "https://mirror.ghproxy.com/"),
        ("MirrorPresetGithub", "https://github.com/"),
        ("MirrorPresetCustom", "")
    ];

    private readonly SettingsStore _settings;
    private readonly ToolPathSettings _paths;
    private readonly ILocalizer _localizer;
    private readonly IFilePicker _filePicker;
    private readonly IToolRegistry _registry;
    private readonly IToolInstaller _installer;
    private readonly IToolUsage _usage;
    private readonly Action<Action> _post;
    private readonly HttpClient _pingClient;
    private readonly ITimer _mirrorTimer;
    private readonly Lock _mirrorGate = new();
    private readonly ConcurrentDictionary<ToolId, int> _refreshGeneration = new();
    private bool _mirrorDirty;
    private CancellationTokenSource? _installCts;
    private Func<string>? _actionMessage;

    // 仅替换显示名称时不能把预设地址写回，否则会覆盖用户手动输入的镜像地址
    private bool _isRebuildingMirrorPresets;

    /// <param name="settings">镜像地址</param>
    /// <param name="paths">工具的指定路径</param>
    /// <param name="localizer">文案</param>
    /// <param name="filePicker">手动指定路径</param>
    /// <param name="registry">工具状态</param>
    /// <param name="installer">下载安装</param>
    /// <param name="usage">安装前检查与释放占用工具的进程</param>
    /// <param name="time">镜像地址保存防抖所用时钟</param>
    /// <param name="post">把后台结果投递到界面线程；默认使用 Avalonia 调度器</param>
    /// <param name="pingClient">测试连通性所用客户端</param>
    public ToolsViewModel(
        SettingsStore settings,
        ToolPathSettings paths,
        ILocalizer localizer,
        IFilePicker filePicker,
        IToolRegistry registry,
        IToolInstaller installer,
        IToolUsage usage,
        TimeProvider? time = null,
        Action<Action>? post = null,
        HttpClient? pingClient = null)
    {
        _settings = settings;
        _paths = paths;
        _localizer = localizer;
        _filePicker = filePicker;
        _registry = registry;
        _installer = installer;
        _usage = usage;
        _post = post ?? (action => Dispatcher.UIThread.Post(action));
        _pingClient = pingClient ?? SharedPingClient;
        _mirrorTimer = (time ?? TimeProvider.System).CreateTimer(_ => SaveMirror(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        Cards = [.. installer.Manifest.Tools.Select(CreateCard)];
        foreach (var card in Cards)
        {
            card.PropertyChanged += OnCardChanged;
        }

        _mirrorPresets = BuildMirrorPresets();
        var saved = settings.Current.CustomMirrorUrl;
        _customMirrorUrl = string.IsNullOrWhiteSpace(saved) ? string.Empty : saved;
        _selectedMirrorPreset = MatchPreset(_customMirrorUrl);
        _isInstallBlocked = usage.IsBusy;
        UpdateCardInstallability();

        _localizer.LanguageChanged += (_, _) => _post(ApplyLocalizedTexts);
        _registry.Invalidated += (_, tool) => _post(() => _ = RefreshAsync(tool));
        _usage.BusyChanged += (_, _) => _post(() => IsInstallBlocked = _usage.IsBusy);
        _ = RefreshAsync(null);
    }

    public IReadOnlyList<ToolCardViewModel> Cards { get; }

    public ToolCardViewModel ExifTool => Card(ToolId.ExifTool);

    public ToolCardViewModel Ffmpeg => Card(ToolId.Ffmpeg);

    public ToolCardViewModel HeifEnc => Card(ToolId.HeifEnc);

    public bool IsExifToolReady => ExifTool.IsReady;

    public bool IsFfmpegReady => Ffmpeg.IsReady;

    public bool IsHeifEncReady => HeifEnc.IsReady;

    /// <summary>三个工具全部就绪（导航栏状态点）。</summary>
    public bool AllToolsReady => Cards.All(c => c.IsReady);

    /// <summary>安装目录，展示给用户以便手动检查或清理。</summary>
    public string InstallRoot => _installer.InstallRoot;

    /// <summary>测试连通性的超时。</summary>
    public TimeSpan PingTimeout { get; init; } = TimeSpan.FromSeconds(5);

    [ObservableProperty]
    private IReadOnlyList<MirrorPreset> _mirrorPresets;

    [ObservableProperty]
    private MirrorPreset? _selectedMirrorPreset;

    [ObservableProperty]
    private string _customMirrorUrl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPingResult))]
    private string _pingLatencyText = string.Empty;

    public bool HasPingResult => !string.IsNullOrEmpty(PingLatencyText);

    [ObservableProperty]
    private bool _isPingHealthy;

    /// <summary>同一时刻只允许一个安装：进度与取消入口是页面级的。</summary>
    [ObservableProperty]
    private bool _isInstallingAny;

    /// <summary>有任务在运行，任务持有的工具进程会让替换失败。</summary>
    [ObservableProperty]
    private bool _isInstallBlocked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActionMessage))]
    private string _actionMessageText = string.Empty;

    [ObservableProperty]
    private bool _isActionMessageError;

    public bool HasActionMessage => !string.IsNullOrEmpty(ActionMessageText);

    /// <summary>
    /// 手动指定路径时的可运行性检查，默认真实启动一次；测试可替换以脱离真实工具。
    /// </summary>
    public Func<string, bool> ValidateToolExecutable { get; set; } = ToolLocator.IsValidTool;

    partial void OnSelectedMirrorPresetChanged(MirrorPreset? value)
    {
        if (!_isRebuildingMirrorPresets && value is not null && !string.IsNullOrWhiteSpace(value.Url))
        {
            CustomMirrorUrl = value.Url;
        }
    }

    partial void OnCustomMirrorUrlChanged(string value)
    {
        lock (_mirrorGate)
        {
            _mirrorDirty = true;
            _mirrorTimer.Change(MirrorSaveDelay, Timeout.InfiniteTimeSpan);
        }
    }

    partial void OnIsInstallingAnyChanged(bool value) => UpdateCardInstallability();

    partial void OnIsInstallBlockedChanged(bool value) => UpdateCardInstallability();

    /// <summary>立即写入尚在防抖中的镜像地址。</summary>
    public void FlushMirror() => SaveMirror();

    [RelayCommand]
    public async Task RescanToolsAsync()
    {
        _registry.Invalidate();
        await RefreshAsync(null);
    }

    [RelayCommand]
    public async Task TestMirrorSpeedAsync()
    {
        PingLatencyText = _localizer["PingTesting"];
        IsPingHealthy = false;

        if (PingTarget(CustomMirrorUrl) is not { } target)
        {
            PingLatencyText = _localizer.Format("PingFailedFormat", _localizer["PingInvalidAddress"]);
            return;
        }

        using var timeout = new CancellationTokenSource(PingTimeout);
        try
        {
            var stopwatch = Stopwatch.StartNew();
            using var request = new HttpRequestMessage(HttpMethod.Head, target);
            using var response = await _pingClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            stopwatch.Stop();

            var status = (int)response.StatusCode;
            PingLatencyText = _localizer.Format("PingResultFormat", stopwatch.ElapsedMilliseconds, status);
            // 代理对 HEAD 根路径常回 4xx，只要不是服务端错误就说明节点可达
            IsPingHealthy = status < 500;
        }
        catch (OperationCanceledException)
        {
            PingLatencyText = _localizer.Format("PingFailedFormat", _localizer["PingTimedOut"]);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or NotSupportedException)
        {
            // 运行时的异常消息不随界面语言变化，按原因归类显示
            PingLatencyText = _localizer.Format("PingFailedFormat", _localizer[PingFailureKey(ex)]);
        }
    }

    private static string PingFailureKey(Exception ex) => ex switch
    {
        HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError } => "PingDnsFailed",
        HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } => "PingTlsFailed",
        HttpRequestException => "PingUnreachable",
        _ => "PingInvalidAddress"
    };

    [RelayCommand]
    private void CancelInstall() => _installCts?.Cancel();

    /// <summary>
    /// 安装或升级指定工具；任何失败都转成提示文案，不向命令抛出。
    /// </summary>
    public async Task InstallAsync(ToolId tool)
    {
        var card = Card(tool);
        if (IsInstallingAny || card.IsPlatformUnsupported)
        {
            return;
        }

        if (_usage.IsBusy)
        {
            IsInstallBlocked = true;
            ShowMessage(() => _localizer["ToolInstallBlockedByTask"], isError: true);
            return;
        }

        FlushMirror();
        IsInstallingAny = true;
        card.BeginInstall();
        ShowMessage(() => _localizer.Format("InstallStartingFormat", card.DisplayName), isError: false);

        using var cts = new CancellationTokenSource();
        _installCts = cts;
        try
        {
            await ReleaseIdleProcessesAsync(tool, cts.Token);
            var progress = new PostingProgress(this, card);
            var result = await _installer.InstallAsync(tool, CustomMirrorUrl, progress, cts.Token);
            await AdoptInstalledAsync(tool, result.ExecutablePath);
            ShowMessage(() => _localizer.Format("InstallCompletedFormat", card.DisplayName), isError: false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            ShowMessage(() => _localizer["InstallCanceled"], isError: true);
        }
        catch (ToolInstallException ex)
        {
            ErrorLogger.Log(ex, $"安装 {card.DisplayName}");
            ShowMessage(
                ex.IsPlatformUnsupported
                    ? () => _localizer["ToolPlatformUnsupported"]
                    : () => _localizer.Format("InstallFailedFormat", card.DisplayName, ToolTexts.Failures(_localizer, _installer, ex.Failures)),
                isError: true);
        }
        catch (Exception ex)
        {
            // 命令边界：任何异常都只能变成提示，穿透到 AsyncRelayCommand 会成为界面线程未处理异常
            ErrorLogger.Log(ex, $"安装 {card.DisplayName}");
            ShowMessage(() => _localizer.Format("InstallFailedFormat", card.DisplayName, _localizer["ToolInstallUnexpected"]), isError: true);
        }
        finally
        {
            if (ReferenceEquals(_installCts, cts))
            {
                _installCts = null;
            }

            card.EndInstall();
            IsInstallingAny = false;
        }

        await RefreshAsync(tool);
    }

    /// <summary>调用系统文件选择器为工具指定可执行文件，校验通过后写入设置。</summary>
    public async Task PickPathAsync(ToolId tool)
    {
        var card = Card(tool);
        // 从该工具当前指定的位置开始；未指定时取其它工具指定的位置（多半装在同一处）
        var start = _paths.Get(tool) ?? Enum.GetValues<ToolId>().Select(_paths.Get).FirstOrDefault(p => p is not null);
        var file = await _filePicker.PickFileAsync(_localizer["PickerToolExecutableTitle"], suggestedStartLocation: start);
        if (string.IsNullOrWhiteSpace(file))
        {
            return;
        }

        // 文件名必须对得上：ToolLocator 的试运行对不认识的文件名一律放行，单靠它会接受任意文件
        if (!File.Exists(file) || !MatchesExecutable(card.ExecutableName, file) || !ValidateToolExecutable(file))
        {
            ShowMessage(() => _localizer.Format("ToolPathInvalidFormat", file), isError: true);
            return;
        }

        _paths.Set(tool, file);
        ShowMessage(() => _localizer.Format("ToolPathAppliedFormat", card.DisplayName), isError: false);
        await RefreshAsync(tool);
    }

    private async Task ReleaseIdleProcessesAsync(ToolId tool, CancellationToken cancellationToken)
    {
        try
        {
            await _usage.ReleaseIdleProcessesAsync(tool).WaitAsync(ReleaseIdleTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            ErrorLogger.Log(new TimeoutException($"等待 {tool} 的预览进程退出超时"), "安装前释放工具进程");
        }
    }

    /// <summary>
    /// 让新装的版本生效：指定路径指向别处时清除它；清除后自动发现仍找到别的文件（如程序目录里的旧版本）时，改为指定新装的路径。
    /// </summary>
    private async Task AdoptInstalledAsync(ToolId tool, string installedPath)
    {
        if (_paths.Get(tool) is { } explicitPath && !SamePath(explicitPath, installedPath))
        {
            _paths.Set(tool, null);
        }

        _registry.Invalidate(tool);
        var info = await _registry.GetAsync(tool);
        if (!SamePath(info.Path, installedPath))
        {
            _paths.Set(tool, installedPath);
        }
    }

    /// <param name="tool">要刷新的工具；null 表示全部</param>
    private async Task RefreshAsync(ToolId? tool)
    {
        var targets = tool is { } id ? [Card(id)] : Cards;
        await Task.WhenAll(targets.Select(RefreshCardAsync));
    }

    private async Task RefreshCardAsync(ToolCardViewModel card)
    {
        // 连续失效时只采用最后一次探测的结果
        var generation = _refreshGeneration.AddOrUpdate(card.Tool, 1, static (_, current) => current + 1);
        ToolInfo info;
        try
        {
            info = await _registry.GetAsync(card.Tool);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ErrorLogger.Log(ex, $"探测 {card.DisplayName}");
            return;
        }

        if (_refreshGeneration[card.Tool] == generation)
        {
            // 同一次探测结果会被多次取用，只在结果变化时记录原因
            if (info.ProbeError is { } error && !ReferenceEquals(error, card.Info?.ProbeError))
            {
                ErrorLogger.Log(error.Cause, $"探测 {card.DisplayName}");
            }

            card.Apply(info);
        }
    }

    private ToolCardViewModel CreateCard(ToolDefinition definition) => new(
        definition,
        definition.Id switch
        {
            ToolId.ExifTool => "ExifToolDesc",
            ToolId.Ffmpeg => "FfmpegDesc",
            _ => "HeifEncDesc"
        },
        _installer.GetCandidates(definition.Id).Count == 0,
        _localizer,
        card => InstallAsync(card.Tool),
        card => PickPathAsync(card.Tool),
        () => _installCts?.Cancel());

    private ToolCardViewModel Card(ToolId tool) => Cards.First(c => c.Tool == tool);

    private void OnCardChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not ToolCardViewModel card || e.PropertyName != nameof(ToolCardViewModel.IsReady))
        {
            return;
        }

        OnPropertyChanged(card.Tool switch
        {
            ToolId.ExifTool => nameof(IsExifToolReady),
            ToolId.Ffmpeg => nameof(IsFfmpegReady),
            _ => nameof(IsHeifEncReady)
        });
        OnPropertyChanged(nameof(AllToolsReady));
    }

    private void UpdateCardInstallability()
    {
        foreach (var card in Cards)
        {
            card.CanStartInstall = !IsInstallingAny && !IsInstallBlocked;
        }
    }

    private void ShowMessage(Func<string> message, bool isError)
    {
        _actionMessage = message;
        IsActionMessageError = isError;
        ActionMessageText = message();
    }

    private void ApplyLocalizedTexts()
    {
        // 名称随语言变化而顺序不变，按位置重新选中（自定义项地址为空，无法按地址匹配）
        var selectedIndex = SelectedMirrorPreset is { } selected ? MirrorPresets.ToList().IndexOf(selected) : -1;
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

        foreach (var card in Cards)
        {
            card.ApplyTexts();
        }

        if (_actionMessage is { } message)
        {
            ActionMessageText = message();
        }
    }

    private IReadOnlyList<MirrorPreset> BuildMirrorPresets() =>
        [.. MirrorPresetSources.Select(p => new MirrorPreset(_localizer[p.NameKey], p.Url))];

    private MirrorPreset MatchPreset(string url)
    {
        var normalized = url.TrimEnd('/') + "/";
        return MirrorPresets.FirstOrDefault(p => p.Url.Length > 0 && p.Url.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            ?? MirrorPresets[^1];
    }

    private void SaveMirror()
    {
        string value;
        lock (_mirrorGate)
        {
            if (!_mirrorDirty)
            {
                return;
            }

            _mirrorDirty = false;
            _mirrorTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            value = CustomMirrorUrl ?? string.Empty;
        }

        if (!string.Equals(_settings.Current.CustomMirrorUrl, value, StringComparison.Ordinal))
        {
            _settings.Update(s => s.CustomMirrorUrl = value);
        }
    }

    /// <summary>空白测直连 GitHub；没写协议的补 https。</summary>
    private static Uri? PingTarget(string? input)
    {
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return new Uri(ToolDownloadUrls.GithubPrefix);
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        return Uri.TryCreate(text, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
               && !string.IsNullOrWhiteSpace(uri.Host)
            ? uri
            : null;
    }

    /// <summary>忽略大小写；也接受同前缀形态，以兼容省略 .exe 与 ExifTool 官方包的 exiftool(-k).exe。</summary>
    private static bool MatchesExecutable(string expected, string path)
    {
        var actual = Path.GetFileName(path);
        if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var expectedStem = Path.GetFileNameWithoutExtension(expected);
        return expectedStem.Length > 0
               && Path.GetFileNameWithoutExtension(actual).StartsWith(expectedStem, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SamePath(string? a, string? b)
    {
        if (a is null || b is null)
        {
            return false;
        }

        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>安装器在下载线程上回报，经界面线程投递给卡片。</summary>
    private sealed class PostingProgress(ToolsViewModel owner, ToolCardViewModel card) : IProgress<ToolInstallProgress>
    {
        public void Report(ToolInstallProgress value) => owner._post(() => card.ReportProgress(value));
    }
}
