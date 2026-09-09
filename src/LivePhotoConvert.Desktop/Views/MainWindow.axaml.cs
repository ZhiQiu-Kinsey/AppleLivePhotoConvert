using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using LivePhotoConvert.Desktop.Services;
using LivePhotoConvert.Desktop.ViewModels;

namespace LivePhotoConvert.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainWindowViewModel vm)
        {
            vm.IsWindowMaximized = WindowState == WindowState.Maximized;
            vm.RequestCloseWindow = Close;
            vm.RequestMinimizeWindow = () => WindowState = WindowState.Minimized;
            vm.RequestMaximizeWindow = () => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            vm.ConvertVm.RequestSelectFolder = PickAlbumFolderAsync;
            vm.ConvertVm.RequestSelectOutputFolder = PickOutputFolderAsync;
            vm.StripVm.RequestSelectFolder = PickAlbumFolderAsync;
            vm.StripVm.RequestSelectFile = PickSamplePhotoFileAsync;
            vm.ToolsVm.RequestPickToolFile = PickToolExecutableFileAsync;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty && DataContext is MainWindowViewModel vm)
        {
            vm.IsWindowMaximized = WindowState == WindowState.Maximized;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.HandleAppExit();
        }

        base.OnClosed(e);
    }

    /// <summary>调用系统原生文件夹选择器选取实况相册目录。</summary>
    private async Task<string?> PickAlbumFolderAsync()
    {
        if (!StorageProvider.CanOpen)
        {
            return null;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = LocalizationService.Instance.GetString("SelectAlbumFolderBtn"),
            AllowMultiple = false
        });

        return folders.Count > 0 ? folders[0].Path.LocalPath : null;
    }

    /// <summary>调用系统原生文件夹选择器选取转换输出目录。</summary>
    private async Task<string?> PickOutputFolderAsync()
    {
        if (!StorageProvider.CanOpen)
        {
            return null;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = LocalizationService.Instance.GetString("PickerOutputFolderTitle"),
            AllowMultiple = false
        });

        return folders.Count > 0 ? folders[0].Path.LocalPath : null;
    }


    /// <summary>调用系统原生文件选择器选取单张实况照片用于画质沙盒比对。</summary>
    private async Task<string?> PickSamplePhotoFileAsync()
    {
        if (!StorageProvider.CanOpen)
        {
            return null;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LocalizationService.Instance.GetString("PickerSamplePhotoTitle"),
            AllowMultiple = false,
            FileTypeFilter = new List<FilePickerFileType>
            {
                new(LocalizationService.Instance.GetString("PickerImageFilterLabel"))
                {
                    Patterns = ["*.heic", "*.HEIC", "*.jpg", "*.JPG", "*.jpeg", "*.JPEG", "*.png", "*.PNG"]
                }
            }
        });

        return files.Count > 0 ? files[0].Path.LocalPath : null;
    }

    /// <summary>调用系统原生文件选择器选取外部引擎（ExifTool / FFmpeg / heif-enc）可执行文件。</summary>
    private async Task<string?> PickToolExecutableFileAsync()
    {
        if (!StorageProvider.CanOpen)
        {
            return null;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LocalizationService.Instance.GetString("PickerToolExecutableTitle"),
            AllowMultiple = false
        });

        return files.Count > 0 ? files[0].Path.LocalPath : null;
    }
}
