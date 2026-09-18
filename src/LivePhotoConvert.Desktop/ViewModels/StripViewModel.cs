using System.Diagnostics;
using Avalonia;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageMagick;
using LivePhotoConvert.Desktop.Services;
using LivePhotoConvert.Desktop.ViewModels.Dialogs;

namespace LivePhotoConvert.Desktop.ViewModels;

/// <summary>
/// 空间瘦身优化工坊全生命周期三阶段闭环视图模型
/// </summary>
public sealed partial class StripViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;
    private CancellationTokenSource? _stripCts;
    private readonly ManualResetEventSlim _pauseGate = new(true);
    // 在 UI 线程（StartAnalysisAsync）写入、后台工作线程（StartStripExecution 汇报回调）读取，
    // 使用 Interlocked 保证 64 位原子性与跨线程可见性，杜绝工作线程读到过期缓存（volatile 不可用于 long）。
    private long _estimatedSavedBytesTotal;
    private long _analysisOriginalBytes;

    public Action<ViewModelBase>? OnShowModal { get; init; }
    public Action? OnCloseModal { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStage1))]
    [NotifyPropertyChangedFor(nameof(IsStage2))]
    [NotifyPropertyChangedFor(nameof(IsStage3))]
    [NotifyPropertyChangedFor(nameof(StageStatusText))]
    private int _currentStage = 1; // 1=待瘦身分析对比, 2=正在瘦身中, 3=成果汇报

    // 阶段可见性用类型安全的计算属性驱动，避免 ObjectConverters.Equal 将 int 与字符串参数比较而恒为 false
    public bool IsStage1 => CurrentStage == 1;
    public bool IsStage2 => CurrentStage == 2;
    public bool IsStage3 => CurrentStage == 3;
    public string StageStatusText => CurrentStage switch
    {
        1 => LocalizationService.Instance.GetString("StageStatusReady"),
        2 => LocalizationService.Instance.GetString("StageStatusRunning"),
        3 => LocalizationService.Instance.GetString("StageStatusDone"),
        _ => LocalizationService.Instance.GetString("StageStatusIdle")
    };

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
    public string CurtainPercentageText => LocalizationService.Instance.GetFormat("CurtainPositionFormat", CurtainPosition, 100 - CurtainPosition);

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
    private string _currentInputPathText = LocalizationService.Instance.GetString("NoAlbumOrPhotoSelected");

    public bool CanStartStrip =>
        !string.IsNullOrWhiteSpace(CurrentInputPathText) &&
        (Directory.Exists(CurrentInputPathText) || File.Exists(CurrentInputPathText));

    partial void OnCurrentInputPathTextChanged(string value)
    {
        StartStripExecutionCommand.NotifyCanExecuteChanged();
    }

    public Func<Task<string?>>? RequestSelectFolder { get; set; }
    public Func<Task<string?>>? RequestSelectFile { get; set; }

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

    // -- Phase 2: 实时吞吐进度 --
    [ObservableProperty]
    private string _runningFileName = string.Empty;

    [ObservableProperty]
    private double _stripProgressPercent;

    [ObservableProperty]
    private string _stripProgressStatusText = string.Empty;

    [ObservableProperty]
    private string _releasedSpaceText = string.Empty;

    [ObservableProperty]
    private string _instantThroughputText = string.Empty;

    [ObservableProperty]
    private string _remainingSecondsText = string.Empty;

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private string _errorText = string.Empty;

    // -- Phase 3: 成果庆贺大卡片 --
    [ObservableProperty]
    private string _celebrationHeader = string.Empty;

    [ObservableProperty]
    private string _celebrationDesc = string.Empty;

    [ObservableProperty]
    private string _beforeTotalResult = "—";

    [ObservableProperty]
    private string _afterTotalResult = "—";

    [ObservableProperty]
    private string _netSavedResult = "—";

    [ObservableProperty]
    private string _savedPercentResult = string.Empty;

    [ObservableProperty]
    private string _beforeCountText = string.Empty;

    public StripViewModel(SettingsService settingsService)
    {
        _settingsService = settingsService;
        var s = _settingsService.Current;
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
        LocalizationService.Instance.LanguageChanged += _ =>
            Avalonia.Threading.Dispatcher.UIThread.Post(ApplyLocalizedTexts);
    }

    private void ApplyLocalizedTexts()
    {
        CelebrationHeader = LocalizationService.Instance.GetString("CelebrationTitle");
        CelebrationDesc = LocalizationService.Instance.GetString("CelebrationDesc");
        if (!CanStartStrip)
        {
            CurrentInputPathText = LocalizationService.Instance.GetString("NoAlbumOrPhotoSelected");
        }
        OnPropertyChanged(nameof(CurtainPercentageText));
        OnPropertyChanged(nameof(StageStatusText));
    }

    [RelayCommand]
    public async Task PickSamplePhotoAsync()
    {
        if (RequestSelectFile is null) return;
        var file = await RequestSelectFile();
        if (!string.IsNullOrWhiteSpace(file) && File.Exists(file))
        {
            CurrentInputPathText = file;
            await LoadSinglePhotoComparisonAsync(file);
        }
    }

    [RelayCommand]
    public async Task PickStripFolderAsync()
    {
        if (RequestSelectFolder is null) return;
        var folder = await RequestSelectFolder();
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
        {
            CurrentInputPathText = folder;
            _settingsService.Current.LastScanDirectory = folder;
            _settingsService.Save();
            await StartAnalysisAsync(folder);

            // 自动加载首张照片进沙盒对比
            var firstPhoto = Directory.EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(f =>
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    return ext is ".heic" or ".jpg" or ".jpeg" or ".png";
                });
            if (firstPhoto != null)
            {
                await LoadSinglePhotoComparisonAsync(firstPhoto);
            }
        }
    }

    [RelayCommand]
    public async Task BrowseOutputDirAsync()
    {
        if (RequestSelectFolder is null) return;
        var folder = await RequestSelectFolder();
        if (!string.IsNullOrWhiteSpace(folder))
        {
            SafeExportDirectory = folder;
            _settingsService.Current.StripOutputDirectory = folder;
            _settingsService.Save();
        }
    }

    [RelayCommand]
    public void OpenOutputDir()
    {
        var target = InPlaceStrip ? CurrentInputPathText : SafeExportDirectory;
        if (File.Exists(target)) target = Path.GetDirectoryName(target);
        if (!string.IsNullOrWhiteSpace(target) && Directory.Exists(target))
        {
            using var _ = Process.Start(new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true
            });
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
                var quality = (uint)(_settingsService.Current.HeicQuality > 0 ? _settingsService.Current.HeicQuality : 90);
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
    public void ToggleInPlaceStrip(bool value)
    {
        if (value)
        {
            // SEC-02: 弹出就地覆盖二次确认弹窗
            BackupConfirmDialogViewModel backupVm = new()
            {
                OnConfirmed = () =>
                {
                    InPlaceStrip = true;
                    OnCloseModal?.Invoke();
                },
                OnCancelled = () =>
                {
                    InPlaceStrip = false;
                    OnCloseModal?.Invoke();
                }
            };
            OnShowModal?.Invoke(backupVm);
        }
        else
        {
            InPlaceStrip = false;
        }
    }

    public async Task StartAnalysisAsync(string inputPath)
    {
        try
        {
            var savedSettings = _settingsService.Current;
            var explicitExif = string.IsNullOrWhiteSpace(savedSettings.ExifToolPath) ? null : savedSettings.ExifToolPath;
            var exifToolPath = Core.External.ToolLocator.Find(Core.External.ExifTool.ExecutableName, explicitExif);
            var exifTool = Core.External.ExifTool.Create(exifToolPath);
            var stripper = new LivePhotoConvert.Core.Services.MotionPhotoStripper(exifTool, Core.External.MagickImageConverter.Instance);

            var report = await stripper.AnalyzeAsync(inputPath, convertToHeic: true);
            Interlocked.Exchange(ref _analysisOriginalBytes, report.OriginalTotalBytes);
            Interlocked.Exchange(ref _estimatedSavedBytesTotal, report.EstimatedSavedBytes);
            TotalOriginalText = $"{(double)report.OriginalTotalBytes / 1024 / 1024:F1} MB";
            EstimatedAfterText = $"{(double)report.EstimatedHeicTotalBytes / 1024 / 1024:F1} MB";
            double pct = report.OriginalTotalBytes > 0 ? (double)report.EstimatedSavedBytes / report.OriginalTotalBytes * 100.0 : 0;
            EstimatedSavedText = $"{(double)report.EstimatedSavedBytes / 1024 / 1024:F1} MB (-{pct:F1}%)";
            BeforeTotalResult = TotalOriginalText;
            AfterTotalResult = EstimatedAfterText;
            SavedPercentResult = LocalizationService.Instance.GetFormat("StripSavedPctFormat", pct);
            BeforeCountText = LocalizationService.Instance.GetFormat("BeforeCountFormat", report.Items.Count);
            // 卷帘对比与徒章使用真实体积（原图 / 瘦身后估算），杜绝写死 MB
            BeforeSizeText = TotalOriginalText;
            AfterSizeText = EstimatedAfterText;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

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

    [RelayCommand(CanExecute = nameof(CanStartStrip))]
    public void StartStripExecution()
    {
        CurrentStage = 2;
        StripProgressPercent = 0;
        IsPaused = false;
        ErrorText = string.Empty;
        _pauseGate.Set();
        // 释放上一次可能残留的令牌，避免重复点击启动造成句柄泄漏
        _stripCts?.Dispose();
        var cts = new CancellationTokenSource();
        _stripCts = cts;
        var token = cts.Token;
        // 本地 stopwatch 由后台线程独占读取耗时，避免与 UI 线程 AbortStrip 的 Stop() 产生跨线程竞争
        var stopwatch = Stopwatch.StartNew();

        Task.Run(async () =>
        {
            try
            {
                var reporter = new DesktopProgressReporter((completed, total, currentItem) =>
                {
                    // 吞吐/ETA/释放估算计算
                    double elapsedSec = stopwatch.Elapsed.TotalSeconds;
                    double rate = elapsedSec > 0.05 ? completed / elapsedSec : 0;
                    int remaining = Math.Max(0, total - completed);
                    int etaSec = rate > 0 ? (int)Math.Ceiling(remaining / rate) : 0;
                    long savedTotal = Interlocked.Read(ref _estimatedSavedBytesTotal);
                    long releasedEst = total > 0 ? savedTotal * completed / total : 0;

                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        double pct = total > 0 ? (double)completed / total * 100.0 : 0;
                        StripProgressPercent = pct;
                        StripProgressStatusText = LocalizationService.Instance.GetFormat("StripProgressFormat", pct, completed, total);
                        RunningFileName = currentItem;
                        InstantThroughputText = LocalizationService.Instance.GetFormat("StripThroughputFormat", rate);
                        RemainingSecondsText = LocalizationService.Instance.GetFormat("StripEtaFormat", etaSec);
                        ReleasedSpaceText = $"{releasedEst / (1024.0 * 1024.0):F1} MB";
                    });

                    // 真暂停门控：在条目之间阻塞工作线程，取消时抛出（不伪造暂停）
                    _pauseGate.Wait(token);
                });

                var savedSettings = _settingsService.Current;
                var explicitExif = string.IsNullOrWhiteSpace(savedSettings.ExifToolPath) ? null : savedSettings.ExifToolPath;
                var explicitHeif = string.IsNullOrWhiteSpace(savedSettings.HeifEncPath) ? null : savedSettings.HeifEncPath;

                var exifToolPath = Core.External.ToolLocator.Find(Core.External.ExifTool.ExecutableName, explicitExif);
                var heifEncPath = Core.External.ToolLocator.Find(Core.External.HeifEncImageConverter.ExecutableName, explicitHeif);

                var exifTool = Core.External.ExifTool.Create(exifToolPath);
                var imageConverter = heifEncPath is not null
                    ? (Core.Abstractions.IImageConverter)Core.External.HeifEncImageConverter.Create(heifEncPath)
                    : Core.External.MagickImageConverter.Instance;

                var stripper = new LivePhotoConvert.Core.Services.MotionPhotoStripper(exifTool, imageConverter, reporter);
                var rawPath = CurrentInputPathText;
                string? inputDir = null;
                if (!string.IsNullOrWhiteSpace(rawPath))
                {
                    if (Directory.Exists(rawPath))
                    {
                        inputDir = rawPath;
                    }
                    else if (File.Exists(rawPath))
                    {
                        inputDir = Path.GetDirectoryName(rawPath);
                    }
                }

                if (string.IsNullOrWhiteSpace(inputDir) || !Directory.Exists(inputDir))
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        CurrentStage = 1;
                        ErrorText = LocalizationService.Instance.GetString("NoAlbumOrPhotoSelected");
                    });
                    return;
                }

                var options = new LivePhotoConvert.Core.Models.StripOptions
                {
                    InputDirectory = inputDir,
                    OutputDirectory = InPlaceStrip ? null : SafeExportDirectory,
                    ConvertToHeic = true,
                    HeicQuality = _settingsService.Current.HeicQuality,
                    Overwrite = true
                };

                var result = await stripper.StripAsync(options, token);

                if (!token.IsCancellationRequested)
                {
                    stopwatch.Stop();
                    double savedMb = result.SavedBytes / (1024.0 * 1024.0);
                    long analysisOriginal = Interlocked.Read(ref _analysisOriginalBytes);
                    long actualAfterBytes = Math.Max(0, analysisOriginal - result.SavedBytes);
                    double actualPct = analysisOriginal > 0 ? (double)result.SavedBytes / analysisOriginal * 100.0 : 0;
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        NetSavedResult = $"+{savedMb:F1} MB";
                        BeforeCountText = LocalizationService.Instance.GetFormat("BeforeCountFormat", result.Total);
                        if (analysisOriginal > 0)
                        {
                            AfterTotalResult = $"{actualAfterBytes / (1024.0 * 1024.0):F1} MB";
                            SavedPercentResult = LocalizationService.Instance.GetFormat("StripSavedPctFormat", actualPct);
                        }

                        CurrentStage = 3;

                        // 驱动偏好设置中的完成提醒 / 自动打开输出目录开关
                        CompletionEffects.RunOnTaskComplete(
                            _settingsService.Current,
                            InPlaceStrip ? CurrentInputPathText : SafeExportDirectory);
                    });
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                stopwatch.Stop();
                Debug.WriteLine(ex);
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    CurrentStage = 1;
                    ErrorText = ex.Message;
                });
            }
            finally
            {
                // 释放瘦身取消令牌，避免 CancellationTokenSource 句柄泄漏
                cts.Dispose();
                if (_stripCts == cts)
                {
                    _stripCts = null;
                }
            }
        }, token);
    }

    [RelayCommand]
    public void TogglePause()
    {
        IsPaused = !IsPaused;
        if (IsPaused)
        {
            _pauseGate.Reset();
        }
        else
        {
            _pauseGate.Set();
        }
    }

    [RelayCommand]
    public void AbortStrip()
    {
        _pauseGate.Set();
        IsPaused = false;
        _stripCts?.Cancel();
        // stopwatch 由后台线程独占，取消令牌即触发任务结束，无需跨线程 Stop()
        CurrentStage = 1;
    }

    [RelayCommand]
    public void ContinueAnother()
    {
        IsPaused = false;
        _pauseGate.Set();
        CurrentStage = 1;
        StripProgressPercent = 0;
    }
}
