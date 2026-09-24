using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoConvert.Desktop.Infrastructure;

namespace LivePhotoConvert.Desktop.Features.Tasks;

/// <summary>任务页：顶部运行中任务，下方历史列表与选中报告。</summary>
public sealed partial class TasksViewModel : ViewModelBase
{
    private readonly INavigator _navigator;

    public TasksViewModel(TaskCenter center, INavigator navigator)
    {
        Center = center;
        _navigator = navigator;
        center.PropertyChanged += OnCenterPropertyChanged;
        center.History.CollectionChanged += OnHistoryChanged;
        // 新报告自动选中，完成后跳到任务页即可看到结果
        center.Completed += (_, report) => SelectedReport = report;
    }

    public TaskCenter Center { get; }

    public ObservableCollection<TaskReportViewModel> History => Center.History;

    public bool HasHistory => History.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedReport))]
    private TaskReportViewModel? _selectedReport;

    public bool HasSelectedReport => SelectedReport is not null;

    private bool CanControlTask() => Center.IsRunning;

    [RelayCommand(CanExecute = nameof(CanControlTask))]
    public void TogglePause() => Center.TogglePause();

    [RelayCommand(CanExecute = nameof(CanControlTask))]
    public void Cancel() => Center.Cancel();

    [RelayCommand]
    public void GoToLibrary() => _navigator.NavigateTo(AppPage.Library);

    private void OnCenterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TaskCenter.IsRunning))
        {
            TogglePauseCommand.NotifyCanExecuteChanged();
            CancelCommand.NotifyCanExecuteChanged();
        }
    }

    private void OnHistoryChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasHistory));
        if (SelectedReport is not null && !History.Contains(SelectedReport))
        {
            SelectedReport = History.FirstOrDefault();
        }
    }
}
