using System.Diagnostics;
using Avalonia;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageMagick;
using LivePhotoConvert.Core.External;
using LivePhotoConvert.Core.Metadata;
using LivePhotoConvert.Core.Services;
using LivePhotoConvert.Desktop.Features.Library;
using LivePhotoConvert.Desktop.Features.Tasks;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.ViewModels.Dialogs;

namespace LivePhotoConvert.Desktop.ViewModels;

/// <summary>
/// 空间瘦身页：选择输入、画质对比与空间预估；执行交给任务中心。
/// </summary>
public sealed partial class StripViewModel : ViewModelBase
{
    private static readonly string[] SamplePhotoPatterns = ["*.heic", "*.HEIC", "*.jpg", "*.JPG", "*.jpeg", "*.JPEG", "*.png", "*.PNG"];

    private readonly SettingsStore _settings;
    private readonly ILocalizer _localizer;
    private readonly IDialogService _dialogs;
    private readonly IFilePicker _filePicker;
    private readonly IShellLauncher _shell;
    private readonly TaskCenter _tasks;
    private readonly INavigator _navigator;

    /// <summary>页面只显示运行中任务的简要状态，详细进度在任务页。</summary>
    public TaskCenter Tasks => _tasks;

    public string StageStatusText => _localizer[_tasks.IsRunning ? "StageStatusRunning" : "StageStatusReady"];

    // -- Phase 1: 前后覆盖对比与预估 --
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurtainPixelWidth))]
    [NotifyPropertyChangedFor(nameof(DividerMargin))]
    [NotifyPropertyChangedFor(nameof(DividerHeight))]
    [NotifyPropertyChangedFor(nameof(ThumbMargin))]
    [NotifyPropertyChangedFor(nameof(CurtainPercentageText))]
    [NotifyPropertyChangedFor(nameof(OverlayClipGeometry))]
    private double _curtainPosition = 50.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurtainPixelWidth))]
    [NotifyPropertyChangedFor(nameof(DividerMargin))]
    [NotifyPropertyChangedFor(nameof(DividerHeight))]
    [NotifyPropertyChangedFor(nameof(ThumbMargin))]
    [NotifyPropertyChangedFor(nameof(OverlayClipGeometry))]
    private Rect _imageRenderRect = new(0, 0, 700, 400);

    [ObservableProperty]
    private double _parentTotalWidth = 700.0;

    [ObservableProperty]
    private double _parentTotalHeight = 400.0;

    public double DividerX => ImageRenderRect.Left + (ImageRenderRect.Width * CurtainPosition / 100.0);
    public double CurtainPixelWidth => Math.Max(0, DividerX - ImageRenderRect.Left);
    public Thickness DividerMargin => new(DividerX, ImageRenderRect.Top, 0, 0);
    public double DividerHeight => Math.Max(1, ImageRenderRect.Height);
    public Thickness ThumbMargin => new(Math.Max(0, DividerX - 17), ImageRenderRect.Top + Math.Max(0, (ImageRenderRect.Height - 34) / 2.0), 0, 0);
    public string CurtainPercentageText => _localizer.Format("CurtainPositionFormat", CurtainPosition, 100 - CurtainPosition);

    public Avalonia.Media.RectangleGeometry OverlayClipGeometry =>
        new(new Rect(ImageRenderRect.Left, ImageRenderRect.Top, Math.Max(0, DividerX - ImageRenderRect.Left), Math.Max(1, ImageRenderRect.Height)));

    public void UpdateImageGeometry(Rect rect, double width, double height)
    {
        ParentTotalWidth = width;
        ParentTotalHeight = height;
        ImageRenderRect = rect;
        OnPropertyChanged(nameof(DividerX));
        OnPropertyChanged(nameof(CurtainPixelWidth));
        OnPropertyChanged(nameof(DividerMargin));
        OnPropertyChanged(nameof(DividerHeight));
        OnPropertyChanged(nameof(ThumbMargin));
        OnPropertyChanged(nameof(OverlayClipGeometry));
    }

    [ObservableProperty]
    private string _beforeSizeText = string.Empty;

    [ObservableProperty]
    private string _afterSizeText = string.Empty;

    [ObservableProperty]
    private Bitmap? _originalCompareBitmap;

    [ObservableProperty]
    private Bitmap? _strippedCompareBitmap;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartStrip))]
    private string _currentInputPathText = string.Empty;

    public bool CanStartStrip =>
        !string.IsNullOrWhiteSpace(CurrentInputPathText) &&
        (Directory.Exists(CurrentInputPathText) || File.Exists(CurrentInputPathText));

    private bool CanStartStripTask() => CanStartStrip && !_tasks.IsRunning;

    partial void OnCurrentInputPathTextChanged(string value)
    {
        StartStripExecutionCommand.NotifyCanExecuteChanged();
    }

    [ObservableProperty]
    private string _totalOriginalText = "—";

    [ObservableProperty]
    private string _estimatedAfterText = "—";

    [ObservableProperty]
    private string _estimatedSavedText = "—";

    [ObservableProperty]
    private bool _inPlaceStrip;

    [ObservableProperty]
    private string _safeExportDirectory;

    [ObservableProperty]
    private string _errorText = string.Empty;

    [ObservableProperty]
    private string _savedPercentResult = string.Empty;

    public StripViewModel(
        SettingsStore settings,
        ILocalizer localizer,
        IDialogService dialogs,
        IFilePicker filePicker,
        IShellLauncher shell,
        TaskCenter tasks,
        INavigator navigator)
    {
        _settings = settings;
        _localizer = localizer;
        _dialogs = dialogs;
        _filePicker = filePicker;
        _shell = shell;
        _tasks = tasks;
        _navigator = navigator;
        _tasks.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TaskCenter.IsRunning))
            {
                StartStripExecutionCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(StageStatusText));
            }
        };
        var s = _settings.Current;
        _inPlaceStrip = s.InPlaceStrip;
        if (!string.IsNullOrWhiteSpace(s.StripOutputDirectory))
        {
            _safeExportDirectory = s.StripOutputDirectory;
        }
        else
        {
            var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            _safeExportDirectory = !string.IsNullOrEmpty(pictures)
                ? Path.Combine(pictures, "LivePhotoStripped")
                : Path.Combine(AppContext.BaseDirectory, "stripped");
        }

        ApplyLocalizedTexts();
        _localizer.LanguageChanged += (_, _) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(ApplyLocalizedTexts);
    }

    private void ApplyLocalizedTexts()
    {
        if (!CanStartStrip)
        {
            CurrentInputPathText = _localizer["NoAlbumOrPhotoSelected"];
        }
        OnPropertyChanged(nameof(CurtainPercentageText));
        OnPropertyChanged(nameof(StageStatusText));
    }

    [RelayCommand]
    public async Task PickSamplePhotoAsync()
    {
        FileTypeFilter[] filters = [new(_localizer["PickerImageFilterLabel"], SamplePhotoPatterns)];
        var file = await _filePicker.PickFileAsync(_localizer["PickerSamplePhotoTitle"], filters, _settings.Current.StripLastDirectory);
        if (!string.IsNullOrWhiteSpace(file) && File.Exists(file))
        {
            CurrentInputPathText = file;
            await LoadSinglePhotoComparisonAsync(file);
        }
    }

    [RelayCommand]
    public async Task PickStripFolderAsync()
    {
        var folder = await _filePicker.PickFolderAsync(_localizer["SelectAlbumFolderBtn"], _settings.Current.StripLastDirectory);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return;
        }

        CurrentInputPathText = folder;
        _settings.Update(s => s.StripLastDirectory = folder);
        await StartAnalysisAsync(folder);

        // 大目录枚举可能耗时，放到后台线程
        var firstPhoto = await Task.Run(() => FindFirstPhoto(folder));
        if (firstPhoto is not null)
        {
            await LoadSinglePhotoComparisonAsync(firstPhoto);
        }
    }

    private static string? FindFirstPhoto(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(f => Path.GetExtension(f).ToLowerInvariant() is ".heic" or ".jpg" or ".jpeg" or ".png");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [RelayCommand]
    public async Task BrowseOutputDirAsync()
    {
        var folder = await _filePicker.PickFolderAsync(_localizer["PickerOutputFolderTitle"], SafeExportDirectory);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            SafeExportDirectory = folder;
            _settings.Update(s => s.StripOutputDirectory = folder);
        }
    }

    [RelayCommand]
    public async Task OpenOutputDirAsync()
    {
        var target = InPlaceStrip ? CurrentInputPathText : SafeExportDirectory;
        if (!_shell.OpenFolder(target))
        {
            await _dialogs.AlertAsync(_localizer["ShellOpenFailedTitle"], _localizer.Format("ShellOpenFailedFormat", target), _localizer["ConfirmDialogOk"]);
        }
    }

    public async Task LoadSinglePhotoComparisonAsync(string photoPath)
    {
        if (string.IsNullOrWhiteSpace(photoPath) || !File.Exists(photoPath))
        {
            return;
        }

        try
        {
            await Task.Run(() =>
            {
                var fi = new FileInfo(photoPath);
                long originalBytes = fi.Length;

                // 检查是否存在伴随 MOV/MP4 视频（苹果实况照片或分离对）
                var dir = Path.GetDirectoryName(photoPath) ?? "";
                var stem = Path.GetFileNameWithoutExtension(photoPath);
                foreach (var ext in new[] { ".mov", ".MOV", ".mp4", ".MP4" })
                {
                    var videoPath = Path.Combine(dir, stem + ext);
                    if (File.Exists(videoPath))
                    {
                        originalBytes += new FileInfo(videoPath).Length;
                        break;
                    }
                }

                using var image = new MagickImage(photoPath);
                image.AutoOrient();

                // 生成原图预览 Bitmap（限制尺寸避免大图爆内存）
                using var origMem = new MemoryStream();
                image.Resize(new MagickGeometry(1600, 1600) { IgnoreAspectRatio = false, Greater = true });
                image.Write(origMem, MagickFormat.Png);
                origMem.Position = 0;
                var origBmp = new Bitmap(origMem);

                // 模拟空间瘦身转码（转为 HEIC，质量使用偏好设置，默认 90）
                var quality = (uint)(_settings.Current.HeicQuality > 0 ? _settings.Current.HeicQuality : ConversionDefaults.HeicQuality);
                Bitmap strippedBmp;
                long strippedEstimatedBytes;

                try
                {
                    image.Format = MagickFormat.Heic;
                    image.Quality = quality;
                    using var heicMem = new MemoryStream();
                    image.Write(heicMem);
                    strippedEstimatedBytes = heicMem.Length;

                    heicMem.Position = 0;
                    using var heicDecoded = new MagickImage(heicMem);
                    using var previewMem = new MemoryStream();
                    heicDecoded.Write(previewMem, MagickFormat.Png);
                    previewMem.Position = 0;
                    strippedBmp = new Bitmap(previewMem);
                }
                catch
                {
                    // 降级策略：环境未安装 heif-enc 时使用高质量 JPEG 模拟画质，并按 60% 估算 HEIC 尺寸
                    image.Format = MagickFormat.Jpeg;
                    image.Quality = quality;
                    using var fbMem = new MemoryStream();
                    image.Write(fbMem);
                    strippedEstimatedBytes = (long)(fbMem.Length * 0.65);

                    fbMem.Position = 0;
                    using var fbDecoded = new MagickImage(fbMem);
                    using var previewMem = new MemoryStream();
                    fbDecoded.Write(previewMem, MagickFormat.Png);
                    previewMem.Position = 0;
                    strippedBmp = new Bitmap(previewMem);
                }

                double origMb = (double)originalBytes / (1024 * 1024);
                double strippedMb = (double)strippedEstimatedBytes / (1024 * 1024);
                double savedPct = originalBytes > 0 ? (1.0 - (double)strippedEstimatedBytes / originalBytes) * 100.0 : 0;

                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    var oldOrig = OriginalCompareBitmap;
                    var oldStripped = StrippedCompareBitmap;

                    OriginalCompareBitmap = origBmp;
                    StrippedCompareBitmap = strippedBmp;

                    oldOrig?.Dispose();
                    oldStripped?.Dispose();

                    BeforeSizeText = $"{origMb:F2} MB";
                    AfterSizeText = $"{strippedMb:F2} MB";
                    SavedPercentResult = $"-{savedPct:F1}%";
                    TotalOriginalText = $"{origMb:F2} MB";
                    EstimatedAfterText = $"{strippedMb:F2} MB";
                    EstimatedSavedText = $"{Math.Max(0, origMb - strippedMb):F2} MB (-{savedPct:F1}%)";
                });
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to load compare photo: {ex}");
        }
    }

    [RelayCommand]
    public async Task ToggleInPlaceStripAsync(bool value)
    {
        if (!value)
        {
            InPlaceStrip = false;
            return;
        }

        // 确认之前保持关闭，避免弹窗期间误用就地模式
        InPlaceStrip = false;
        InPlaceStrip = await _dialogs.ShowAsync(new BackupConfirmDialogViewModel());
    }

    public async Task StartAnalysisAsync(string inputPath)
    {
        try
        {
            // 大目录的枚举同样不能占用界面线程
            var files = await Task.Run(() => ResolveInputFiles(inputPath));
            var settings = _settings.Current;
            await using var metadata = ExifToolMetadataService.Create(NullIfBlank(settings.ExifToolPath), Math.Clamp(settings.Concurrency, 1, 8));
            var stripper = new MotionPhotoStripper(metadata, MagickImageConverter.Instance);
            var candidates = await stripper.AnalyzeAsync(files);

            var original = candidates.Sum(c => c.OriginalBytes);
            var estimatedAfter = candidates.Sum(c => c.EstimateFinalBytes(convertToHeic: true));
            var saved = Math.Max(0, original - estimatedAfter);
            TotalOriginalText = $"{original / 1024.0 / 1024:F1} MB";
            EstimatedAfterText = $"{estimatedAfter / 1024.0 / 1024:F1} MB";
            var pct = original > 0 ? saved * 100.0 / original : 0;
            EstimatedSavedText = $"{saved / 1024.0 / 1024:F1} MB (-{pct:F1}%)";
            SavedPercentResult = _localizer.Format("StripSavedPctFormat", pct);
            BeforeSizeText = TotalOriginalText;
            AfterSizeText = EstimatedAfterText;
        }
        catch (Exception ex)
        {
            ErrorLogger.Log(ex, "空间瘦身分析");
            ErrorText = ex.Message;
        }
    }

    /// <summary>
    /// 选中目录时处理目录内（不含子目录）的照片；选中单张照片时只处理这一张。
    /// </summary>
    private static IReadOnlyList<string> ResolveInputFiles(string? inputPath) => inputPath switch
    {
        _ when string.IsNullOrWhiteSpace(inputPath) => [],
        _ when Directory.Exists(inputPath) => MotionPhotoStripper.FindCandidates(inputPath),
        _ when File.Exists(inputPath) => [inputPath],
        _ => []
    };

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// 依据当前选定目录/文件执行分析。
    /// </summary>
    [RelayCommand]
    public Task RefreshAnalysisAsync()
    {
        if (CanStartStrip)
        {
            if (Directory.Exists(CurrentInputPathText))
            {
                return StartAnalysisAsync(CurrentInputPathText);
            }
            if (File.Exists(CurrentInputPathText))
            {
                return LoadSinglePhotoComparisonAsync(CurrentInputPathText);
            }
        }

        return Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(CanStartStripTask))]
    public async Task StartStripExecutionAsync()
    {
        ErrorText = string.Empty;
        var input = CurrentInputPathText;
        // 大目录枚举不能占用界面线程
        var files = await Task.Run(() => ResolveInputFiles(input));
        if (files.Count == 0)
        {
            ErrorText = _localizer["NoAlbumOrPhotoSelected"];
            return;
        }

        if (_tasks.IsRunning)
        {
            return;
        }

        var settings = _settings.Current;
        var inPlace = InPlaceStrip;
        var job = new ConversionJob(
            ConversionAction.Strip,
            new ConversionOptions
            {
                Output = inPlace ? null : new OutputOptions(SafeExportDirectory),
                InPlaceLocation = inPlace ? input : null,
                ConvertToHeic = true,
                HeicQuality = settings.HeicQuality > 0 ? settings.HeicQuality : ConversionDefaults.HeicQuality
            },
            new ConversionInputs { Files = files })
        {
            Tools = ToolPaths.From(settings),
            Parallelism = Math.Clamp(settings.Concurrency, 1, 8)
        };

        await _tasks.RunAsync(job);
    }

    [RelayCommand]
    public void ViewTasks() => _navigator.NavigateTo(AppPage.Tasks);
}
