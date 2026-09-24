using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoConvert.Core.Pipeline;
using LivePhotoConvert.Core.Services;
using LivePhotoConvert.Desktop.Converters;
using LivePhotoConvert.Desktop.Features.Library;
using LivePhotoConvert.Desktop.Infrastructure;

namespace LivePhotoConvert.Desktop.Features.Tasks;

public enum TaskItemStatus
{
    Success,
    Skipped,
    Failed,

    /// <summary>输出已成功，但按所选方式处理源文件时出错。</summary>
    Cleanup
}

/// <summary>报告条目上的信息标签（如 HDR 处理结果）。</summary>
/// <param name="Text">本地化文案</param>
/// <param name="Detail">技术细节，悬停显示</param>
/// <param name="IsPositive">正面结果用强调色</param>
public sealed record TaskReportTag(string Text, string? Detail, bool IsPositive);

/// <summary>报告明细中的一行。</summary>
/// <param name="Detail">本地化的结果说明</param>
/// <param name="TechnicalDetail">异常原文等技术细节，展开后显示</param>
public sealed record TaskReportItem(
    TaskItemStatus Status,
    string StatusText,
    string SourcePath,
    string? OutputPath,
    string Detail,
    string TargetFormat,
    string? TechnicalDetail = null)
{
    public IReadOnlyList<TaskReportTag> Tags { get; init; } = [];

    public bool HasTags => Tags.Count > 0;

    public bool HasTechnicalDetail => !string.IsNullOrWhiteSpace(TechnicalDetail);

    /// <summary>有标签或可展开的细节时才占用说明下方的一行。</summary>
    public bool HasExtras => HasTags || HasTechnicalDetail;

    public string FileName => string.IsNullOrEmpty(SourcePath) ? "—" : Path.GetFileName(SourcePath);

    public string SourceFormat => Path.GetExtension(SourcePath).TrimStart('.').ToUpperInvariant();

    public bool IsSuccess => Status == TaskItemStatus.Success;

    public bool IsFailed => Status == TaskItemStatus.Failed;

    public bool IsWarning => Status is TaskItemStatus.Skipped or TaskItemStatus.Cleanup;

    /// <summary>"打开所在位置"的目标：成功项定位输出文件，其余定位源文件。</summary>
    public string? LocatePath => IsSuccess && !string.IsNullOrEmpty(OutputPath) ? OutputPath
        : string.IsNullOrEmpty(SourcePath) ? null
        : SourcePath;

    public bool CanLocate => LocatePath is not null;
}

/// <summary>
/// 一次任务的报告。只保存原始结果，文案在语言切换后由 <see cref="Refresh"/> 重建。
/// </summary>
public sealed partial class TaskReportViewModel : ViewModelBase
{
    public const int FilterAll = 0;
    public const int FilterProblems = 1;
    public const int FilterSuccess = 2;

    private readonly TaskCenter _center;
    private readonly ILocalizer _localizer;
    private readonly IShellLauncher _shell;
    private readonly IDialogService _dialogs;
    private readonly IFilePicker _filePicker;
    private List<TaskReportItem> _items = [];

    /// <param name="center">重试时提交新任务</param>
    /// <param name="job">产生本报告的任务</param>
    /// <param name="report">Core 批处理结果；整批无法开始时为 null</param>
    /// <param name="failure">整批无法开始的原因</param>
    /// <param name="plannedCount">计划处理的条目数（取消时与已处理数对照显示）</param>
    /// <param name="startedAt">开始时间</param>
    /// <param name="finishedAt">结束时间</param>
    /// <param name="localizer">文案</param>
    /// <param name="shell">打开目录与定位文件</param>
    /// <param name="dialogs">失败提示</param>
    /// <param name="filePicker">导出位置</param>
    public TaskReportViewModel(
        TaskCenter center,
        ConversionJob job,
        BatchReport? report,
        TaskFailure? failure,
        int plannedCount,
        DateTimeOffset startedAt,
        DateTimeOffset finishedAt,
        ILocalizer localizer,
        IShellLauncher shell,
        IDialogService dialogs,
        IFilePicker filePicker)
    {
        _center = center;
        _localizer = localizer;
        _shell = shell;
        _dialogs = dialogs;
        _filePicker = filePicker;
        Job = job;
        Report = report ?? BatchReport.Empty;
        IsFatal = report is null;
        Failure = failure;
        StartedAt = startedAt;
        FinishedAt = finishedAt;

        var fatalCount = Math.Max(1, job.ItemCount);
        TotalCount = IsFatal ? fatalCount : Report.Items.Count;
        PlannedCount = IsFatal ? fatalCount : Math.Max(plannedCount, TotalCount);
        SuccessCount = Report.Succeeded;
        FailedCount = IsFatal ? fatalCount : Report.Failed;
        SkippedCount = Report.Skipped;
        CleanupCount = Report.CleanupFailures;
        SavedBytes = Report.BytesSaved;
        Elapsed = IsFatal || Report.Elapsed <= TimeSpan.Zero ? finishedAt - startedAt : Report.Elapsed;

        // 整批无法开始时没有逐项结果，重试即重新提交整个任务
        RetryJob = IsFatal
            ? job
            : job.RetryWith(Report.Items.Where(i => i.Kind == OutcomeKind.Failed).Select(i => i.Source));

        _center.PropertyChanged += OnCenterPropertyChanged;
        Refresh();
    }

    public ConversionJob Job { get; }

    public ConversionAction Action => Job.Action;

    public BatchReport Report { get; }

    public bool IsFatal { get; }

    public TaskFailure? Failure { get; }

    public bool WasCanceled => Report.Canceled;

    public bool IsCompleted => !IsFatal && !WasCanceled;

    public bool IsCompletedClean => IsCompleted && ProblemCount == 0;

    public bool IsCompletedWithProblems => IsCompleted && ProblemCount > 0;

    public DateTimeOffset StartedAt { get; }

    public DateTimeOffset FinishedAt { get; }

    public TimeSpan Elapsed { get; }

    /// <summary>已处理（有结果）的条目数。</summary>
    public int TotalCount { get; }

    public int PlannedCount { get; }

    /// <summary>"处理总量"指标显示计划总数；取消时已处理与未处理的项数在副标题中分列。</summary>
    public string TotalText => PlannedCount.ToString(CultureInfo.InvariantCulture);

    /// <summary>取消后未处理的项数。</summary>
    public int UnprocessedCount => PlannedCount - TotalCount;

    public int SuccessCount { get; }

    public int FailedCount { get; }

    public int SkippedCount { get; }

    public int CleanupCount { get; }

    /// <summary>"异常"：失败与清理失败，都需要用户处理；跳过的项单独统计。</summary>
    public int ProblemCount => FailedCount + CleanupCount;

    public long SavedBytes { get; }

    public bool HasSavedBytes => Action == ConversionAction.Strip && SavedBytes > 0;

    public double SuccessPercent => TotalCount > 0 ? Math.Round(SuccessCount * 100.0 / TotalCount, 1) : 0;

    public string SuccessPercentText => string.Create(CultureInfo.InvariantCulture, $"{SuccessPercent:F1}%");

    public string OutputDirectory => Job.ResultLocation;

    public bool HasOutputDirectory => !string.IsNullOrWhiteSpace(OutputDirectory);

    /// <summary>重试失败项将提交的任务；没有可重试项时条目数为 0。</summary>
    public ConversionJob RetryJob { get; }

    public int RetryCount => RetryJob.ItemCount;

    public bool CanRetry => RetryCount > 0;

    public IReadOnlyList<TaskReportItem> Items => _items;

    public ObservableCollection<TaskReportItem> FilteredItems { get; } = [];

    public bool HasItems => _items.Count > 0;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _timeText = string.Empty;

    [ObservableProperty]
    private string _timestampText = string.Empty;

    [ObservableProperty]
    private string _summaryText = string.Empty;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private string _totalSubText = string.Empty;

    [ObservableProperty]
    private string _problemSubText = string.Empty;

    [ObservableProperty]
    private string _durationText = string.Empty;

    [ObservableProperty]
    private string _averageText = string.Empty;

    [ObservableProperty]
    private string _savedText = string.Empty;

    [ObservableProperty]
    private string _retryText = string.Empty;

    [ObservableProperty]
    private string _exportStatusText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAllFilterSelected))]
    [NotifyPropertyChangedFor(nameof(IsProblemFilterSelected))]
    [NotifyPropertyChangedFor(nameof(IsSuccessFilterSelected))]
    private int _selectedFilterIndex;

    public bool IsAllFilterSelected => SelectedFilterIndex == FilterAll;

    public bool IsProblemFilterSelected => SelectedFilterIndex == FilterProblems;

    public bool IsSuccessFilterSelected => SelectedFilterIndex == FilterSuccess;

    /// <summary>语言切换后重建全部文案与明细。</summary>
    public void Refresh()
    {
        var culture = _localizer.Culture;
        Title = TaskTexts.Title(_localizer, Action);
        StatusText = _localizer[IsFatal ? "TaskStatusFailed" : WasCanceled ? "TaskStatusCanceled" : "TaskStatusCompleted"];
        TimeText = FinishedAt.ToLocalTime().ToString(_localizer["TaskHistoryTimeFormat"], culture);
        TimestampText = _localizer.Format("ReportTimestampFormat", FinishedAt.LocalDateTime);
        SummaryText = _localizer.Format("ReportSummaryFormat", SuccessCount, ProblemCount, SkippedCount)
                      + (WasCanceled ? _localizer["ReportCanceledSuffix"] : string.Empty);
        ErrorMessage = Failure?.Describe(_localizer) ?? string.Empty;
        TotalSubText = WasCanceled
            ? _localizer.Format("ReportKpiTotalCanceledSubFormat", TotalCount, UnprocessedCount)
            : _localizer["ReportKpiTotalSub"];
        ProblemSubText = _localizer.Format("ReportKpiProblemsSubFormat", FailedCount, CleanupCount);
        DurationText = Elapsed.TotalSeconds >= 1
            ? string.Create(culture, $"{Elapsed.TotalSeconds:F1}s")
            : "< 1s";
        AverageText = TotalCount > 0 && Elapsed.TotalSeconds >= 1
            ? _localizer.Format("ReportAvgSpeedFormat", Elapsed.TotalSeconds / TotalCount)
            : "—";
        SavedText = _localizer.Format("ReportSavedFormat", FormatBytes(SavedBytes));
        RetryText = _localizer.Format("RetryFailedFormat", RetryCount);

        _items = BuildItems();
        OnPropertyChanged(nameof(Items));
        OnPropertyChanged(nameof(HasItems));
        ApplyFilter();
    }

    [RelayCommand]
    public void SetFilter(object? index)
    {
        SelectedFilterIndex = index switch
        {
            int i => i,
            string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => FilterAll
        };
        ApplyFilter();
    }

    [RelayCommand]
    public async Task OpenOutputDirAsync()
    {
        if (HasOutputDirectory && !_shell.OpenFolder(OutputDirectory))
        {
            await AlertShellFailureAsync(OutputDirectory);
        }
    }

    [RelayCommand]
    public async Task RevealItemAsync(TaskReportItem? item)
    {
        if (item?.LocatePath is { } path && !_shell.RevealFile(path))
        {
            await AlertShellFailureAsync(path);
        }
    }

    private bool CanRetryNow() => CanRetry && !_center.IsRunning;

    [RelayCommand(CanExecute = nameof(CanRetryNow))]
    public Task RetryFailedAsync() => CanRetryNow() ? _center.RunAsync(RetryJob) : Task.CompletedTask;

    [RelayCommand]
    public async Task ExportCsvAsync()
    {
        var suggested = string.Create(CultureInfo.InvariantCulture, $"LivePhotoConvert_{Action}_{FinishedAt.ToLocalTime():yyyyMMdd_HHmmss}.csv");
        var path = await _filePicker.SaveFileAsync(
            _localizer["ExportCsvTitle"],
            suggested,
            [new FileTypeFilter(_localizer["ExportCsvFilterName"], ["*.csv"])],
            HasOutputDirectory ? OutputDirectory : null);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var rows = BuildCsvRows();
        try
        {
            await Task.Run(() => CsvWriter.WriteFile(path, rows));
            ExportStatusText = _localizer.Format("ExportCsvDoneFormat", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ErrorLogger.Log(ex, "导出任务报告");
            await _dialogs.AlertAsync(_localizer["ExportCsvFailedTitle"], _localizer.Format("ExportCsvFailedFormat", path, ex.Message), _localizer["ConfirmDialogOk"]);
        }
    }

    /// <summary>表头 + 全部明细（不受当前筛选影响）。</summary>
    public IReadOnlyList<string?[]> BuildCsvRows()
    {
        List<string?[]> rows =
        [
            [
                _localizer["ReportCsvStatus"],
                _localizer["ReportCsvFileName"],
                _localizer["ReportCsvSourceFormat"],
                _localizer["ReportCsvTargetFormat"],
                _localizer["ReportCsvDetail"],
                _localizer["ReportCsvTechnicalDetail"],
                _localizer["ReportCsvSourcePath"],
                _localizer["ReportCsvOutputPath"]
            ]
        ];
        rows.AddRange(_items.Select(i => new[] { i.StatusText, i.FileName, i.SourceFormat, i.TargetFormat, DetailWithTags(i), i.TechnicalDetail, i.SourcePath, i.OutputPath }));
        return rows;
    }

    private List<TaskReportItem> BuildItems()
    {
        var failedText = _localizer["ReportStatusFailed"];
        if (IsFatal)
        {
            var detail = Failure?.Detail;
            return [new TaskReportItem(TaskItemStatus.Failed, failedText, string.Empty, null, ErrorMessage, string.Empty, detail == ErrorMessage ? null : detail)];
        }

        var successText = _localizer["ReportStatusSuccess"];
        var skippedText = _localizer["ReportStatusSkipped"];
        var cleanupText = _localizer["ReportStatusCleanup"];
        var splitTarget = Action switch
        {
            ConversionAction.ToApple => _localizer["SplitTargetApple"],
            ConversionAction.Extract => _localizer["SplitTargetExtract"],
            _ => string.Empty
        };

        var items = new List<TaskReportItem>(Report.Items.Count);
        // 失败项排在最前，便于处理
        foreach (var outcome in Report.Items.OrderByDescending(i => i.Kind).ThenBy(i => i.Source, StringComparer.OrdinalIgnoreCase))
        {
            var output = outcome.Outputs.Count > 0 ? outcome.Outputs[0] : null;
            var target = Action switch
            {
                ConversionAction.ToAndroid => "Motion Photo",
                ConversionAction.Strip => Path.GetExtension(output ?? string.Empty).TrimStart('.').ToUpperInvariant(),
                _ => splitTarget
            };

            var (status, statusText, detail) = outcome.Kind switch
            {
                OutcomeKind.Succeeded => (TaskItemStatus.Success, successText, SuccessDetail(outcome, splitTarget)),
                OutcomeKind.Skipped => (TaskItemStatus.Skipped, skippedText, CauseText(outcome)),
                _ => (TaskItemStatus.Failed, failedText, CauseText(outcome))
            };
            items.Add(new TaskReportItem(status, statusText, outcome.Source, output, detail, target, TechnicalDetail(outcome, detail)) { Tags = Tags(outcome) });

            if (outcome.CleanupError is not null)
            {
                items.Add(new TaskReportItem(TaskItemStatus.Cleanup, cleanupText, outcome.Source, output, _localizer["ReportCleanupFailedDesc"], string.Empty, outcome.CleanupError));
            }
        }

        return items;
    }

    /// <summary>没有原因码时退回显示技术细节，避免说明为空。</summary>
    private string CauseText(ItemOutcome outcome) =>
        outcome.Causes.Count > 0 ? OutcomeTexts.Describe(_localizer, outcome.Causes)
        : outcome.Detail ?? OutcomeTexts.Describe(_localizer, OutcomeReason.Unexpected);

    /// <summary>异常原文与附注细节合并到展开区；与说明相同的内容不再重复。</summary>
    private string? TechnicalDetail(ItemOutcome outcome, string detail)
    {
        IEnumerable<string> lines =
        [
            .. outcome.Detail is { } raw && raw != detail ? [raw] : Array.Empty<string>(),
            .. outcome.Notes.Where(n => !string.IsNullOrWhiteSpace(n.Detail)).Select(n => $"{OutcomeTexts.Describe(_localizer, n.Kind)}: {n.Detail}")
        ];
        var text = string.Join(Environment.NewLine, lines);
        return text.Length == 0 ? null : text;
    }

    private List<TaskReportTag> Tags(ItemOutcome outcome) =>
    [
        .. outcome.Notes.Select(n => new TaskReportTag(OutcomeTexts.Describe(_localizer, n.Kind), n.Detail, n.Kind == OutcomeNoteKind.UltraHdrWritten))
    ];

    private static string DetailWithTags(TaskReportItem item) =>
        item.HasTags ? $"{item.Detail} [{string.Join(", ", item.Tags.Select(t => t.Text))}]" : item.Detail;

    private string SuccessDetail(ItemOutcome outcome, string splitTarget) => Action switch
    {
        ConversionAction.ToAndroid => _localizer["MergeSuccessDesc"],
        ConversionAction.Strip => _localizer.Format("StripItemSavedFormat", FormatBytes(outcome.BytesSaved)),
        _ => _localizer.Format("SplitSuccessDescFormat", splitTarget)
    };

    private void ApplyFilter()
    {
        FilteredItems.Clear();
        foreach (var item in _items.Where(i => SelectedFilterIndex switch
                 {
                     FilterProblems => !i.IsSuccess,
                     FilterSuccess => i.IsSuccess,
                     _ => true
                 }))
        {
            FilteredItems.Add(item);
        }
    }

    private Task AlertShellFailureAsync(string target) =>
        _dialogs.AlertAsync(_localizer["ShellOpenFailedTitle"], _localizer.Format("ShellOpenFailedFormat", target), _localizer["ConfirmDialogOk"]);

    private void OnCenterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TaskCenter.IsRunning))
        {
            RetryFailedCommand.NotifyCanExecuteChanged();
        }
    }

    private static string FormatBytes(long bytes) =>
        ByteSizeConverter.Instance.Convert(bytes, typeof(string), null, CultureInfo.InvariantCulture) as string ?? $"{bytes} B";
}
