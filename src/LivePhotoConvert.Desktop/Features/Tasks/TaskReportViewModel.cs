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

/// <summary>报告明细中的一行。</summary>
public sealed record TaskReportItem(
    TaskItemStatus Status,
    string StatusText,
    string SourcePath,
    string? OutputPath,
    string Detail,
    string TargetFormat)
{
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
    /// <param name="errorMessage">整批无法开始的原因</param>
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
        string? errorMessage,
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
        ErrorMessage = errorMessage ?? string.Empty;
        StartedAt = startedAt;
        FinishedAt = finishedAt;

        var fatalCount = Math.Max(1, job.ItemCount);
        TotalCount = IsFatal ? fatalCount : Report.Items.Count;
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

    public string ErrorMessage { get; }

    public bool WasCanceled => Report.Canceled;

    public bool IsCompleted => !IsFatal && !WasCanceled;

    public bool IsCompletedClean => IsCompleted && ProblemCount == 0;

    public bool IsCompletedWithProblems => IsCompleted && ProblemCount > 0;

    public DateTimeOffset StartedAt { get; }

    public DateTimeOffset FinishedAt { get; }

    public TimeSpan Elapsed { get; }

    public int TotalCount { get; }

    public int SuccessCount { get; }

    public int FailedCount { get; }

    public int SkippedCount { get; }

    public int CleanupCount { get; }

    /// <summary>失败与清理失败都需要用户处理。</summary>
    public int ProblemCount => FailedCount + CleanupCount;

    /// <summary>"异常/跳过"指标：需要处理的项加上被跳过的项。</summary>
    public int AttentionCount => ProblemCount + SkippedCount;

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
        SummaryText = _localizer.Format("ReportSummaryFormat", SuccessCount, ProblemCount)
                      + (WasCanceled ? _localizer["ReportCanceledSuffix"] : string.Empty);
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
                _localizer["ReportCsvSourcePath"],
                _localizer["ReportCsvOutputPath"]
            ]
        ];
        rows.AddRange(_items.Select(i => new[] { i.StatusText, i.FileName, i.SourceFormat, i.TargetFormat, i.Detail, i.SourcePath, i.OutputPath }));
        return rows;
    }

    private List<TaskReportItem> BuildItems()
    {
        var failedText = _localizer["ReportStatusFailed"];
        if (IsFatal)
        {
            return [new TaskReportItem(TaskItemStatus.Failed, failedText, string.Empty, null, ErrorMessage, string.Empty)];
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

            items.Add(outcome.Kind switch
            {
                OutcomeKind.Succeeded => new TaskReportItem(TaskItemStatus.Success, successText, outcome.Source, output, SuccessDetail(outcome, splitTarget), target),
                OutcomeKind.Skipped => new TaskReportItem(TaskItemStatus.Skipped, skippedText, outcome.Source, output, outcome.Message ?? string.Empty, target),
                _ => new TaskReportItem(TaskItemStatus.Failed, failedText, outcome.Source, output, outcome.Message ?? string.Empty, target)
            });

            if (outcome.CleanupError is not null)
            {
                items.Add(new TaskReportItem(TaskItemStatus.Cleanup, cleanupText, outcome.Source, output, outcome.CleanupError, string.Empty));
            }
        }

        return items;
    }

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
