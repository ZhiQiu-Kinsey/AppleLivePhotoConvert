using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoConvert.Desktop.Services;

namespace LivePhotoConvert.Desktop.ViewModels;

public sealed record ReportItemRecord(
    string FileName,
    string StatusText,
    string DetailReason,
    bool CanForceRetry,
    string StatusType = "Info", // "Success", "Failed", "Skipped", "Info"
    string SourceFormat = "",
    string TargetFormat = "",
    string DurationText = "")
{
    public bool IsSuccess => StatusType == "Success";
    public bool IsFailed => StatusType == "Failed";
    public bool IsSkipped => StatusType is "Skipped" or "Cleanup";
}

public sealed record BatchReportModel
{
    public required string SummaryBadge { get; init; }
    public required IReadOnlyList<ReportItemRecord> Records { get; init; }
    public int TotalCount { get; init; }
    public int SuccessCount { get; init; }
    public int FailedCount { get; init; }
    public int SkippedCount { get; init; }
    public TimeSpan Elapsed { get; init; }
    public string OutputDirectory { get; init; } = string.Empty;
    public string ModeName { get; init; } = string.Empty;
}

public sealed partial class ReportViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _batchTimestampText = string.Empty;

    [ObservableProperty]
    private string _summaryBadgeText = string.Empty;

    [ObservableProperty]
    private string _taskNameText = LocalizationService.Instance.GetString("ReportTaskName");

    [ObservableProperty]
    private string _modeNameText = LocalizationService.Instance.GetString("ReportModeMerge");

    [ObservableProperty]
    private string _outputDirectoryText = string.Empty;

    // 4 核心指标卡片
    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private int _successCount;

    [ObservableProperty]
    private int _failedCount;

    [ObservableProperty]
    private int _skippedCount;

    [ObservableProperty]
    private string _successPercentText = "0%";

    [ObservableProperty]
    private double _successPercent;

    [ObservableProperty]
    private double _failedPercent;

    [ObservableProperty]
    private double _skippedPercent;

    [ObservableProperty]
    private string _totalDurationText = "—";

    [ObservableProperty]
    private string _avgSpeedText = "—";

    [ObservableProperty]
    private bool _hasRecords;

    [ObservableProperty]
    private bool _hasFailedRecords;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAllFilterSelected))]
    [NotifyPropertyChangedFor(nameof(IsFailedFilterSelected))]
    [NotifyPropertyChangedFor(nameof(IsSuccessFilterSelected))]
    private int _selectedFilterIndex; // 0=全部, 1=仅异常, 2=仅成功

    public bool IsAllFilterSelected => SelectedFilterIndex == 0;
    public bool IsFailedFilterSelected => SelectedFilterIndex == 1;
    public bool IsSuccessFilterSelected => SelectedFilterIndex == 2;

    private readonly List<ReportItemRecord> _allRecords = [];
    public ObservableCollection<ReportItemRecord> FilteredRecords { get; } = [];
    public ObservableCollection<ReportItemRecord> Records => FilteredRecords;

    public Action? OnSwitchToConvertTab { get; init; }

    public void Populate(BatchReportModel model)
    {
        SummaryBadgeText = model.SummaryBadge;
        BatchTimestampText = LocalizationService.Instance.GetFormat("ReportTimestampFormat", DateTime.Now);
        OutputDirectoryText = model.OutputDirectory;
        if (!string.IsNullOrEmpty(model.ModeName))
        {
            ModeNameText = model.ModeName;
        }

        TotalCount = model.TotalCount > 0 ? model.TotalCount : model.Records.Count;
        SuccessCount = model.SuccessCount;
        FailedCount = model.FailedCount;
        SkippedCount = model.SkippedCount;

        if (TotalCount > 0)
        {
            SuccessPercent = Math.Round((double)SuccessCount / TotalCount * 100.0, 1);
            FailedPercent = Math.Round((double)FailedCount / TotalCount * 100.0, 1);
            SkippedPercent = Math.Round((double)SkippedCount / TotalCount * 100.0, 1);
            SuccessPercentText = $"{SuccessPercent:F1}%";
        }
        else
        {
            SuccessPercent = 0;
            FailedPercent = 0;
            SkippedPercent = 0;
            SuccessPercentText = "0%";
        }

        if (model.Elapsed.TotalMilliseconds > 0)
        {
            TotalDurationText = $"{model.Elapsed.TotalSeconds:F1}s";
            double avg = TotalCount > 0 ? model.Elapsed.TotalSeconds / TotalCount : 0;
            AvgSpeedText = LocalizationService.Instance.GetFormat("ReportAvgSpeedFormat", avg);
        }
        else
        {
            TotalDurationText = "< 1s";
            AvgSpeedText = "—";
        }

        _allRecords.Clear();
        _allRecords.AddRange(model.Records);
        HasRecords = _allRecords.Count > 0;
        HasFailedRecords = FailedCount > 0 || SkippedCount > 0;

        ApplyFilter();
    }

    /// <summary>
    /// 兼容旧式调用。
    /// </summary>
    public void Populate(string summaryBadge, IReadOnlyList<ReportItemRecord> records)
    {
        int total = records.Count;
        int failed = records.Count(r => r.StatusType is "Failed" or "Cleanup");
        int skipped = records.Count(r => r.StatusType is "Skipped");
        int success = Math.Max(0, total - failed - skipped);

        Populate(new BatchReportModel
        {
            SummaryBadge = summaryBadge,
            Records = records,
            TotalCount = total,
            SuccessCount = success,
            FailedCount = failed,
            SkippedCount = skipped,
            Elapsed = TimeSpan.Zero,
            OutputDirectory = string.Empty,
            ModeName = "实况转换批次"
        });
    }

    [RelayCommand]
    public void SetFilter(object? index)
    {
        SelectedFilterIndex = index switch
        {
            int i => i,
            string s when int.TryParse(s, out int p) => p,
            _ => 0
        };
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        FilteredRecords.Clear();
        var items = SelectedFilterIndex switch
        {
            1 => _allRecords.Where(r => r.StatusType is "Failed" or "Skipped" or "Cleanup"),
            2 => _allRecords.Where(r => r.StatusType is "Success"),
            _ => _allRecords
        };
        foreach (var item in items)
        {
            FilteredRecords.Add(item);
        }
    }

    [RelayCommand]
    public void OpenOutputDir()
    {
        if (!string.IsNullOrWhiteSpace(OutputDirectoryText) && Directory.Exists(OutputDirectoryText))
        {
            using var _ = Process.Start(new ProcessStartInfo
            {
                FileName = OutputDirectoryText,
                UseShellExecute = true
            });
        }
    }

    [RelayCommand]
    public void ReturnToConvert()
    {
        OnSwitchToConvertTab?.Invoke();
    }

    [RelayCommand]
    public void ForceRetryItem(ReportItemRecord item)
    {
        OnSwitchToConvertTab?.Invoke();
    }

    [RelayCommand]
    public void ExportErrorLog()
    {
        var targetDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrEmpty(targetDir) || !Directory.Exists(targetDir))
        {
            targetDir = AppContext.BaseDirectory;
        }
        var logFile = Path.Combine(targetDir, $"LivePhotoConvert_Report_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
        using var writer = new StreamWriter(logFile, false, System.Text.Encoding.UTF8);
        writer.WriteLine("状态,文件名,来源格式,目标格式,耗时,详细信息");
        foreach (var record in _allRecords)
        {
            writer.WriteLine($"\"{record.StatusText}\",\"{record.FileName}\",\"{record.SourceFormat}\",\"{record.TargetFormat}\",\"{record.DurationText}\",\"{record.DetailReason}\"");
        }
    }
}
