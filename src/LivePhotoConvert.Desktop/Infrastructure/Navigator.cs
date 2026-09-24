using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace LivePhotoConvert.Desktop.Infrastructure;

/// <summary>主窗口的页面。</summary>
public enum AppPage
{
    Library,
    Tasks,
    Tools,
    Settings
}

/// <summary>跨页面跳转，页面之间不互相持有回调。</summary>
public interface INavigator : INotifyPropertyChanged
{
    AppPage Current { get; }

    void NavigateTo(AppPage page);
}

public sealed partial class Navigator : ObservableObject, INavigator
{
    [ObservableProperty]
    private AppPage _current;

    public void NavigateTo(AppPage page) => Current = page;
}
