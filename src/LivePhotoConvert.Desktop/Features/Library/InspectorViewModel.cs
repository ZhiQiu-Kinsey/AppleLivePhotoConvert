using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoConvert.Core.Pipeline;
using LivePhotoConvert.Core.Services;
using LivePhotoConvert.Desktop.Converters;
using LivePhotoConvert.Desktop.Features.Dialogs;
using LivePhotoConvert.Desktop.Features.Tasks;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Models;
using LivePhotoConvert.Desktop.Services;

namespace LivePhotoConvert.Desktop.Features.Library;

/// <summary>输出盘剩余空间检查；测试可替换。</summary>
public interface IDiskSpaceGuard
{
    (bool HasEnoughSpace, long RequiredBytes, long AvailableBytes) Check(string targetDirectory, long totalSourceBytes);
}

public sealed class DiskSpaceGuard : IDiskSpaceGuard
{
    public static DiskSpaceGuard Instance { get; } = new();

    public (bool HasEnoughSpace, long RequiredBytes, long AvailableBytes) Check(string targetDirectory, long totalSourceBytes) =>
        SafetyGuard.CheckDiskSpace(targetDirectory, totalSourceBytes);
}

/// <summary>
/// 图库右侧检查器：选择动作、编辑参数（即时写入设置）、启动前检查并把任务交给任务中心。
/// </summary>
public sealed partial class InspectorViewModel : ViewModelBase
{
    /// <summary>选择连续变化时只在停下来之后估算一次，避免每次点选都启动 ExifTool。</summary>
    public static readonly TimeSpan EstimateDebounce = TimeSpan.FromMilliseconds(400);

    private const int DeleteSourceAction = 3;

    private readonly LibraryViewModel _library;
    private readonly SettingsStore _settings;
    private readonly ILocalizer _localizer;
    private readonly IDialogService _dialogs;
    private readonly IFilePicker _filePicker;
    private readonly IShellLauncher _shell;
    private readonly TaskCenter _tasks;
    private readonly INavigator _navigator;
    private readonly IToolAvailability _toolAvailability;
    private readonly IStripEstimator _estimator;
    private readonly IDiskSpaceGuard _diskSpace;
    private readonly TimeProvider _time;
    private CancellationTokenSource? _estimateCts;
    private StripEstimate? _estimate;
    private string? _estimateError;

    public InspectorViewModel(
        LibraryViewModel library,
        SettingsStore settings,
        ILocalizer localizer,
        IDialogService dialogs,
        IFilePicker filePicker,
        IShellLauncher shell,
        TaskCenter tasks,
        INavigator navigator,
        IToolAvailability toolAvailability,
        IStripEstimator estimator,
        IDiskSpaceGuard diskSpace,
        TimeProvider time)
    {
        _library = library;
        _settings = settings;
        _localizer = localizer;
        _dialogs = dialogs;
        _filePicker = filePicker;
        _shell = shell;
        _tasks = tasks;
        _navigator = navigator;
        _toolAvailability = toolAvailability;
        _estimator = estimator;
        _diskSpace = diskSpace;
        _time = time;

        var s = settings.Current;
        _action = Enum.IsDefined(s.Action) ? s.Action : ConversionAction.ToAndroid;
        _namingFormat = Math.Clamp(s.NamingFormat, 0, 2);
        _sourceAction = Math.Clamp(s.SourceAction, 0, DeleteSourceAction);
        _keepSubfolderHierarchy = s.KeepSubfolderHierarchy;
        _autoAppendIndex = s.ConflictPolicy == ConflictPolicy.AppendIndex;
        _heicQuality = s.HeicQuality > 0 ? Math.Clamp(s.HeicQuality, 50, 100) : ConversionDefaults.HeicQuality;
        _preserveHdr = s.PreserveHdr;
        _outputDirectory = JobFactory.ResolveOutputDirectory(s);
        _stripOutputDirectory = JobFactory.ResolveStripDirectory(s);
        _inPlaceStrip = s.InPlaceStrip;
        _stripConvertToHeic = s.StripConvertToHeic;

        library.SelectionChanged += (_, _) => OnSelectionChanged();
        library.PropertyChanged += OnLibraryPropertyChanged;
        tasks.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TaskCenter.IsRunning))
            {
                StartCommand.NotifyCanExecuteChanged();
            }
        };
        localizer.LanguageChanged += (_, _) => RefreshTexts();

        RefreshApplicable();
        RefreshTexts();
        ScheduleEstimate();
    }

    public TaskCenter Tasks => _tasks;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsToAndroid))]
    [NotifyPropertyChangedFor(nameof(IsToApple))]
    [NotifyPropertyChangedFor(nameof(IsExtract))]
    [NotifyPropertyChangedFor(nameof(IsStrip))]
    [NotifyPropertyChangedFor(nameof(HasSourceAction))]
    [NotifyPropertyChangedFor(nameof(HasHeicQuality))]
    [NotifyPropertyChangedFor(nameof(IsDeleteWarningVisible))]
    [NotifyPropertyChangedFor(nameof(IsOutputSectionVisible))]
    [NotifyPropertyChangedFor(nameof(IsStripExportVisible))]
    [NotifyPropertyChangedFor(nameof(IsOutputDirectoryVisible))]
    private ConversionAction _action;

    public bool IsToAndroid => Action == ConversionAction.ToAndroid;

    public bool IsToApple => Action == ConversionAction.ToApple;

    public bool IsExtract => Action == ConversionAction.Extract;

    public bool IsStrip => Action == ConversionAction.Strip;

    public bool HasSourceAction => !IsStrip;

    public bool HasHeicQuality => IsToApple || (IsStrip && StripConvertToHeic);

    [ObservableProperty]
    private string _actionDescription = string.Empty;

    // ── 通用参数 ──

    [ObservableProperty]
    private string _applicableText = string.Empty;

    [ObservableProperty]
    private string _applicableScopeText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private int _applicableCount;

    [ObservableProperty]
    private long _applicableBytes;

    [ObservableProperty]
    private string _outputDirectory;

    /// <summary>就地瘦身不写输出目录，也就没有层级与重名问题。</summary>
    public bool IsOutputSectionVisible => !(IsStrip && InPlaceStrip);

    public bool IsOutputDirectoryVisible => !IsStrip;

    [ObservableProperty]
    private bool _keepSubfolderHierarchy;

    [ObservableProperty]
    private bool _autoAppendIndex;

    // ── 合成 / 还原 / 解包 ──

    [ObservableProperty]
    private int _namingFormat;

    /// <summary>顺序与 <see cref="NamingFormat"/> 的取值一一对应。</summary>
    public IReadOnlyList<LocalizedOption> NamingFormatOptions { get; } =
        [new("NamingFormatOriginalShort"), new("NamingFormatDateNameShort"), new("NamingFormatCleanShort")];

    [ObservableProperty]
    private string _liveFilenameDemo = string.Empty;

    /// <summary>合成时把 iPhone HDR 照片保留为 Ultra HDR 封面。</summary>
    [ObservableProperty]
    private bool _preserveHdr;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDeleteWarningVisible))]
    private int _sourceAction;

    public bool IsDeleteWarningVisible => HasSourceAction && SourceAction == DeleteSourceAction;

    /// <summary>顺序与 <see cref="SourceAction"/> 的取值一一对应。</summary>
    public IReadOnlyList<LocalizedOption> SourceActionOptions { get; } =
    [
        new("SourceActionKeepShort"),
        new("SourceActionSubfolderShort"),
        new("SourceActionRecycleShort"),
        new("SourceActionDeleteShort", isDanger: true)
    ];

    [ObservableProperty]
    private int _heicQuality;

    // ── 瘦身 ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOutputSectionVisible))]
    [NotifyPropertyChangedFor(nameof(IsStripExportVisible))]
    private bool _inPlaceStrip;

    public bool IsStripExportVisible => IsStrip && !InPlaceStrip;

    [ObservableProperty]
    private string _stripOutputDirectory;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHeicQuality))]
    private bool _stripConvertToHeic;

    [ObservableProperty]
    private bool _isEstimating;

    [ObservableProperty]
    private bool _hasEstimate;

    [ObservableProperty]
    private string _estimateOriginalText = "—";

    [ObservableProperty]
    private string _estimateAfterText = "—";

    [ObservableProperty]
    private string _estimateSavedText = "—";

    /// <summary>估算中、没有可瘦身的照片或估算失败时的说明；有结果时为空。</summary>
    [ObservableProperty]
    private string _estimateStatusText = string.Empty;

    /// <summary>对比预览的对象：最近聚焦的卡片，否则为首个选中的卡片。</summary>
    public PhotoCardItemViewModel? CompareCard =>
        _library.FocusedCard ?? _library.SelectedOrAllCards.FirstOrDefault();

    [ObservableProperty]
    private string _primaryButtonText = string.Empty;

    /// <summary>当前挂起的空间估算（含防抖等待）；测试据此等待估算结束。</summary>
    internal Task EstimateTask { get; private set; } = Task.CompletedTask;

    [RelayCommand]
    private void SetAction(string? value)
    {
        if (Enum.TryParse<ConversionAction>(value, ignoreCase: false, out var action) && Enum.IsDefined(action))
        {
            Action = action;
        }
    }

    partial void OnActionChanged(ConversionAction value)
    {
        _settings.Update(s => s.Action = value);
        _library.SetActionFilter(value);
        RefreshApplicable();
        RefreshTexts();
        ScheduleEstimate();
    }

    partial void OnNamingFormatChanged(int value)
    {
        _settings.Update(s => s.NamingFormat = value);
        RefreshTexts();
    }

    partial void OnSourceActionChanged(int value) => _settings.Update(s => s.SourceAction = value);

    partial void OnPreserveHdrChanged(bool value) => _settings.Update(s => s.PreserveHdr = value);

    partial void OnKeepSubfolderHierarchyChanged(bool value) => _settings.Update(s => s.KeepSubfolderHierarchy = value);

    partial void OnAutoAppendIndexChanged(bool value) =>
        _settings.Update(s => s.ConflictPolicy = value ? ConflictPolicy.AppendIndex : ConflictPolicy.Overwrite);

    partial void OnHeicQualityChanged(int value)
    {
        _settings.Update(s => s.HeicQuality = value);
        ScheduleEstimate();
    }

    partial void OnOutputDirectoryChanged(string value) => _settings.Update(s => s.OutputDirectory = value);

    partial void OnStripOutputDirectoryChanged(string value) => _settings.Update(s => s.StripOutputDirectory = value);

    partial void OnStripConvertToHeicChanged(bool value)
    {
        _settings.Update(s => s.StripConvertToHeic = value);
        ScheduleEstimate();
    }

    /// <summary>
    /// 就地瘦身会替换原片，开启前必须确认。开关的双向绑定会先把界面值写进来，这里立刻复位，确认后才真正开启并保存。
    /// </summary>
    [RelayCommand]
    private async Task ToggleInPlaceStripAsync(bool enable)
    {
        InPlaceStrip = false;
        if (enable)
        {
            InPlaceStrip = await _dialogs.ShowAsync(new BackupConfirmDialogViewModel());
        }

        var confirmed = InPlaceStrip;
        _settings.Update(s => s.InPlaceStrip = confirmed);
    }

    [RelayCommand]
    private async Task SelectOutputFolderAsync()
    {
        var folder = await _filePicker.PickFolderAsync(_localizer["PickerOutputFolderTitle"], OutputDirectory);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            OutputDirectory = folder;
        }
    }

    [RelayCommand]
    private async Task SelectStripFolderAsync()
    {
        var folder = await _filePicker.PickFolderAsync(_localizer["PickerOutputFolderTitle"], StripOutputDirectory);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            StripOutputDirectory = folder;
        }
    }

    [RelayCommand]
    private Task OpenOutputFolderAsync() => OpenFolderAsync(OutputDirectory);

    [RelayCommand]
    private Task OpenStripFolderAsync() => OpenFolderAsync(StripOutputDirectory);

    [RelayCommand]
    private void ViewTasks() => _navigator.NavigateTo(AppPage.Tasks);

    private bool CanOpenCompare() => CompareCard is not null;

    [RelayCommand(CanExecute = nameof(CanOpenCompare))]
    private async Task OpenStripCompareAsync()
    {
        if (CompareCard is not { } card)
        {
            return;
        }

        await _dialogs.ShowAsync(new StripCompareDialogViewModel(
            _localizer, _estimator.Sampler, card.PhotoPath, new StripSampleOptions(ToolPaths.From(_settings.Current), StripConvertToHeic, HeicQuality)));
    }

    private bool CanStart() => !_tasks.IsRunning && ApplicableCount > 0;

    /// <summary>
    /// 依次检查：所需工具 → 输出盘空间 → 永久删除确认；任一步被拒绝都不启动任务。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (_tasks.IsRunning)
        {
            return;
        }

        var action = Action;
        var cards = JobFactory.Applicable(action, _library.SelectedOrAllCards);
        if (cards.Count == 0)
        {
            return;
        }

        var settings = _settings.Current;
        var tools = ToolPaths.From(settings);
        var requiredTools = JobFactory.RequiredTools(action, settings.StripConvertToHeic);
        // 探测可能启动外部进程，不能占用界面线程
        var missing = await Task.Run(() => requiredTools.Where(t => !_toolAvailability.IsAvailable(t, tools)).ToList());
        if (missing.Count > 0)
        {
            var goToTools = await _dialogs.ShowAsync(new ConfirmDialogViewModel
            {
                Title = _localizer["MissingToolsTitle"],
                Message = _localizer.Format("MissingToolsFormat", string.Join(", ", missing.Select(ToolName))),
                ConfirmText = _localizer["MissingToolsGoBtn"],
                CancelText = _localizer["ConfirmDialogCancel"]
            });
            if (goToTools)
            {
                _navigator.NavigateTo(AppPage.Tools);
            }

            return;
        }

        var totalBytes = JobFactory.SourceBytes(cards);
        var target = JobFactory.ResolveTargetDirectory(action, settings, _library.AlbumDirectory);
        var (hasSpace, requiredBytes, availableBytes) = _diskSpace.Check(target, totalBytes);
        if (!hasSpace)
        {
            var proceed = await _dialogs.ShowAsync(new LowDiskSpaceDialogViewModel
            {
                TargetDirectory = target,
                RequiredSpaceText = FormatBytes(requiredBytes),
                AvailableSpaceText = FormatBytes(availableBytes)
            });
            if (!proceed)
            {
                return;
            }
        }

        if (action != ConversionAction.Strip && SourceAction == DeleteSourceAction)
        {
            var confirmed = await _dialogs.ShowAsync(new DeleteConfirmDialogViewModel(_localizer)
            {
                AffectedCount = cards.Count,
                AffectedSizeText = FormatBytes(totalBytes)
            });
            if (!confirmed)
            {
                // 取消按钮承诺“保留原片”，这里兑现
                SourceAction = 0;
                return;
            }
        }

        // 弹窗期间可能已从别处启动了任务，或切换了动作
        if (_tasks.IsRunning || action != Action)
        {
            return;
        }

        await _tasks.RunAsync(JobFactory.Build(action, cards, _settings.Current, _library.AlbumDirectory));
    }

    private string ToolName(RequiredTool tool) => tool switch
    {
        RequiredTool.ExifTool => "ExifTool",
        RequiredTool.Ffmpeg => "FFmpeg",
        _ => _localizer["ToolNameHeicEncoder"]
    };

    private async Task OpenFolderAsync(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        try
        {
            // 输出目录首次使用前可能尚不存在，先建好再打开
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ErrorLogger.Log(ex, "创建输出目录");
        }

        if (!_shell.OpenFolder(folder))
        {
            await _dialogs.AlertAsync(_localizer["ShellOpenFailedTitle"], _localizer.Format("ShellOpenFailedFormat", folder), _localizer["ConfirmDialogOk"]);
        }
    }

    private void OnSelectionChanged()
    {
        RefreshApplicable();
        RefreshPrimaryButton();
        OpenStripCompareCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CompareCard));
        ScheduleEstimate();
    }

    private void OnLibraryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LibraryViewModel.FocusedCard))
        {
            OnPropertyChanged(nameof(CompareCard));
            OpenStripCompareCommand.NotifyCanExecuteChanged();
        }
    }

    private void RefreshApplicable()
    {
        var selectedCount = _library.Selection.SelectedCount;
        var applicable = JobFactory.Applicable(Action, _library.SelectedOrAllCards);
        ApplicableCount = applicable.Count;
        ApplicableBytes = JobFactory.SourceBytes(applicable);
        ApplicableText = _localizer.Format("ApplicableFormat", ApplicableCount, FormatBytes(ApplicableBytes));
        ApplicableScopeText = selectedCount > 0
            ? _localizer.Format("ApplicableScopeSelectedFormat", selectedCount)
            : _localizer["ApplicableScopeAll"];
    }

    private void RefreshTexts()
    {
        foreach (var option in NamingFormatOptions.Concat(SourceActionOptions))
        {
            option.Refresh(_localizer);
        }

        ActionDescription = _localizer[Action switch
        {
            ConversionAction.ToAndroid => "ActionToAndroidDesc",
            ConversionAction.ToApple => "ActionToAppleDesc",
            ConversionAction.Extract => "ActionExtractDesc",
            _ => "ActionStripDesc"
        }];
        LiveFilenameDemo = NamingFormat switch
        {
            0 => "MVIMG_IMG_0012.jpg",
            2 => "MVIMG_20260905_142033.jpg",
            _ => "MVIMG_20260905_142033_IMG_0012.jpg"
        };
        RefreshApplicable();
        RefreshPrimaryButton();
        ApplyEstimateTexts();
    }

    private void RefreshPrimaryButton()
    {
        PrimaryButtonText = ApplicableCount == 0
            ? _localizer["PrimaryBtnNone"]
            : _localizer.Format(Action switch
            {
                ConversionAction.ToAndroid => "PrimaryBtnMergeFormat",
                ConversionAction.ToApple => "PrimaryBtnAppleFormat",
                ConversionAction.Extract => "PrimaryBtnExtractFormat",
                _ => "PrimaryBtnStripFormat"
            }, ApplicableCount);
    }

    private void ScheduleEstimate()
    {
        _estimateCts?.Cancel();
        _estimateCts?.Dispose();
        _estimateCts = null;

        if (!IsStrip)
        {
            IsEstimating = false;
            EstimateTask = Task.CompletedTask;
            return;
        }

        var cts = new CancellationTokenSource();
        _estimateCts = cts;
        var files = JobFactory.Applicable(ConversionAction.Strip, _library.SelectedOrAllCards).Select(c => c.PhotoPath).ToList();
        EstimateTask = EstimateAsync(files, ToolPaths.From(_settings.Current), StripConvertToHeic, HeicQuality, cts.Token);
    }

    private async Task EstimateAsync(IReadOnlyList<string> files, ToolPaths tools, bool convertToHeic, int heicQuality, CancellationToken token)
    {
        IsEstimating = true;
        ApplyEstimateTexts();
        try
        {
            await Task.Delay(EstimateDebounce, _time, token);
            var estimate = files.Count == 0
                ? new StripEstimate(0, 0, 0)
                : await Task.Run(() => _estimator.EstimateAsync(files, tools, convertToHeic, heicQuality, token), token);
            token.ThrowIfCancellationRequested();
            _estimate = estimate;
            _estimateError = null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 被更新的选择取代，由新的估算负责刷新界面
            return;
        }
        catch (Exception ex)
        {
            ErrorLogger.Log(ex, "空间瘦身预估");
            _estimate = null;
            _estimateError = ErrorMessages.Describe(_localizer, ex);
        }

        IsEstimating = false;
        ApplyEstimateTexts();
    }

    private void ApplyEstimateTexts()
    {
        var estimate = IsEstimating ? null : _estimate;
        HasEstimate = estimate is { Count: > 0 };
        EstimateOriginalText = HasEstimate ? FormatBytes(estimate!.OriginalBytes) : "—";
        EstimateAfterText = HasEstimate ? FormatBytes(estimate!.EstimatedBytes) : "—";
        EstimateSavedText = HasEstimate
            ? _localizer.Format("EstimateSavedFormat", FormatBytes(estimate!.SavedBytes), estimate.SavedPercent)
            : "—";
        EstimateStatusText = IsEstimating
            ? _localizer["EstimateCalculating"]
            : _estimateError is { } error
                ? _localizer.Format("EstimateFailedFormat", error)
                : HasEstimate ? string.Empty : _localizer["EstimateEmpty"];
    }

    private static string FormatBytes(long bytes) =>
        ByteSizeConverter.Instance.Convert(bytes, typeof(string), null, CultureInfo.InvariantCulture) as string
        ?? $"{bytes} B";
}
