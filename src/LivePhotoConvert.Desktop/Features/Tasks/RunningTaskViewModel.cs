using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using LivePhotoConvert.Core.Pipeline;
using LivePhotoConvert.Desktop.Features.Library;
using LivePhotoConvert.Desktop.Infrastructure;

namespace LivePhotoConvert.Desktop.Features.Tasks;

/// <summary>动作相关的共用文案。</summary>
public static class TaskTexts
{
    public static string Title(ILocalizer localizer, ConversionAction action) => localizer[action switch
    {
        ConversionAction.ToAndroid => "TaskTitleToAndroid",
        ConversionAction.ToApple => "TaskTitleToApple",
        ConversionAction.Extract => "TaskTitleExtract",
        _ => "TaskTitleStrip"
    }];

    /// <summary>「移入备份文件夹」时的子文件夹名：合成与拆分各用一个，按界面语言命名。</summary>
    public static string ArchiveFolderName(ILocalizer localizer, ConversionAction action) =>
        localizer[action == ConversionAction.ToAndroid ? "ArchiveFolderMerged" : "ArchiveFolderSplit"];

    /// <summary>剩余时间：不足一小时显示 分:秒。</summary>
    public static string Duration(TimeSpan value)
    {
        var rounded = TimeSpan.FromSeconds(Math.Ceiling(Math.Max(0, value.TotalSeconds)));
        return rounded.TotalHours >= 1
            ? rounded.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : rounded.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// 运行中任务的进度卡片。吞吐与剩余时间按扣除暂停后的实际运行时间估算，
/// 且只计实际处理的条目：处理前就确定结果的条目（如配对校验跳过的）会在第一次进度里一次性计入已完成。
/// </summary>
public sealed partial class RunningTaskViewModel : ViewModelBase
{
    /// <summary>实际处理的项少于该值时速率波动太大，剩余时间显示为"估算中"。</summary>
    public const int MinSamplesForEstimate = 3;

    private readonly ILocalizer _localizer;
    private readonly TimeProvider _time;
    private readonly long _startedAt;
    private long _pausedAt;
    private TimeSpan _pausedTotal;
    private int _preresolved;

    public RunningTaskViewModel(ConversionJob job, ILocalizer localizer, TimeProvider time)
    {
        _localizer = localizer;
        _time = time;
        _startedAt = time.GetTimestamp();
        Action = job.Action;
        _total = job.ItemCount;
        Refresh();
    }

    public ConversionAction Action { get; }

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Percent))]
    [NotifyPropertyChangedFor(nameof(PercentText))]
    [NotifyPropertyChangedFor(nameof(RatioText))]
    private int _completed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Percent))]
    [NotifyPropertyChangedFor(nameof(PercentText))]
    [NotifyPropertyChangedFor(nameof(RatioText))]
    private int _total;

    [ObservableProperty]
    private string _currentFile = string.Empty;

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private bool _isCancelling;

    [ObservableProperty]
    private double? _itemsPerSecond;

    [ObservableProperty]
    private TimeSpan? _remaining;

    [ObservableProperty]
    private string _throughputText = string.Empty;

    [ObservableProperty]
    private string _remainingText = string.Empty;

    public double Percent => Total > 0 ? Math.Min(100, Completed * 100.0 / Total) : 0;

    public string PercentText => string.Create(CultureInfo.InvariantCulture, $"{Math.Floor(Percent):F0}%");

    public string RatioText => _localizer.Format("TaskProgressRatioFormat", Completed, Total);

    /// <summary>扣除暂停时长后的运行时间。</summary>
    public TimeSpan ActiveElapsed
    {
        get
        {
            var paused = _pausedTotal + (IsPaused ? _time.GetElapsedTime(_pausedAt) : TimeSpan.Zero);
            var active = _time.GetElapsedTime(_startedAt) - paused;
            return active > TimeSpan.Zero ? active : TimeSpan.Zero;
        }
    }

    public void Report(BatchProgress value)
    {
        // 并行的工作线程各自汇报，较早的计数可能晚于较新的计数到达界面线程
        if (value.Total == Total && value.Completed < Completed)
        {
            return;
        }

        Total = Math.Max(0, value.Total);
        Completed = Math.Clamp(value.Completed, 0, Total);
        _preresolved = Math.Clamp(value.Preresolved, 0, Completed);
        CurrentFile = value.CurrentItem ?? string.Empty;
        UpdateEstimate();
    }

    public void SetPaused(bool paused)
    {
        if (paused == IsPaused)
        {
            return;
        }

        if (paused)
        {
            _pausedAt = _time.GetTimestamp();
        }
        else
        {
            _pausedTotal += _time.GetElapsedTime(_pausedAt);
        }

        IsPaused = paused;
    }

    /// <summary>语言切换后重建文案。</summary>
    public void Refresh()
    {
        Title = TaskTexts.Title(_localizer, Action);
        OnPropertyChanged(nameof(RatioText));
        UpdateTexts();
    }

    private void UpdateEstimate()
    {
        var seconds = ActiveElapsed.TotalSeconds;
        var processed = Completed - _preresolved;
        ItemsPerSecond = processed > 0 && seconds > 0 ? processed / seconds : null;
        Remaining = processed >= MinSamplesForEstimate && ItemsPerSecond is > 0 and var rate
            ? TimeSpan.FromSeconds((Total - Completed) / rate)
            : null;
        UpdateTexts();
    }

    private void UpdateTexts()
    {
        ThroughputText = ItemsPerSecond is { } rate ? _localizer.Format("TaskThroughputFormat", rate) : "—";
        RemainingText = Remaining is { } remaining
            ? _localizer.Format("TaskRemainingFormat", TaskTexts.Duration(remaining))
            : _localizer["TaskRemainingEstimating"];
    }
}
