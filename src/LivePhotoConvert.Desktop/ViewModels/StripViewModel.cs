using System.Diagnostics;
using Avalonia;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageMagick;
using LivePhotoConvert.Core.Abstractions;
using LivePhotoConvert.Core.External;
using LivePhotoConvert.Core.Metadata;
using LivePhotoConvert.Core.Pipeline;
using LivePhotoConvert.Core.Services;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Models;
using LivePhotoConvert.Desktop.Services;
using LivePhotoConvert.Desktop.ViewModels.Dialogs;

namespace LivePhotoConvert.Desktop.ViewModels;

/// <summary>
/// 空间瘦身优化工坊全生命周期三阶段闭环视图模型
/// </summary>
public sealed partial class StripViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;
    private readonly ILocalizer _localizer;
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
        1 => _localizer["StageStatusReady"],
        2 => _localizer["StageStatusRunning"],
        3 => _localizer["StageStatusDone"],
        _ => _localizer["StageStatusIdle"]
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

    public StripViewModel(SettingsService settingsService, ILocalizer localizer)
    {
        _settingsService = settingsService;
        _localizer = localizer;
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
        _localizer.LanguageChanged += (_, _) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(ApplyLocalizedTexts);
    }

    private void ApplyLocalizedTexts()
    {
        CelebrationHeader = _localizer["CelebrationTitle"];
        CelebrationDesc = _localizer["CelebrationDesc"];
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
            var files = ResolveInputFiles(inputPath);
            var settings = _settingsService.Current;
            await using var metadata = ExifToolMetadataService.Create(NullIfBlank(settings.ExifToolPath), Math.Clamp(settings.Concurrency, 1, 8));
            var stripper = new MotionPhotoStripper(metadata, MagickImageConverter.Instance);
            var candidates = await stripper.AnalyzeAsync(files);

            var original = candidates.Sum(c => c.OriginalBytes);
            var estimatedAfter = candidates.Sum(c => c.EstimateFinalBytes(convertToHeic: true));
            var saved = Math.Max(0, original - estimatedAfter);
            Interlocked.Exchange(ref _analysisOriginalBytes, original);
            Interlocked.Exchange(ref _estimatedSavedBytesTotal, saved);
            TotalOriginalText = $"{original / 1024.0 / 1024:F1} MB";
            EstimatedAfterText = $"{estimatedAfter / 1024.0 / 1024:F1} MB";
            var pct = original > 0 ? saved * 100.0 / original : 0;
            EstimatedSavedText = $"{saved / 1024.0 / 1024:F1} MB (-{pct:F1}%)";
            BeforeTotalResult = TotalOriginalText;
            AfterTotalResult = EstimatedAfterText;
            SavedPercentResult = _localizer.Format("StripSavedPctFormat", pct);
            BeforeCountText = _localizer.Format("BeforeCountFormat", candidates.Count);
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
    /// 在条目之间阻塞工作线程实现暂停；进度本身转发给界面。
    /// </summary>
    private sealed class PausableProgress(IProgress<BatchProgress> inner, ManualResetEventSlim gate, CancellationToken cancellationToken) : IProgress<BatchProgress>
    {
        public void Report(BatchProgress value)
        {
            inner.Report(value);
            gate.Wait(cancellationToken);
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

        var files = ResolveInputFiles(CurrentInputPathText);
        if (files.Count == 0)
        {
            CurrentStage = 1;
            ErrorText = _localizer["NoAlbumOrPhotoSelected"];
            cts.Dispose();
            _stripCts = null;
            return;
        }

        var settings = _settingsService.Current;
        var inPlace = InPlaceStrip;
        var exportDirectory = SafeExportDirectory;
        var request = new StripRequest
        {
            Files = files,
            Output = inPlace ? null : new OutputOptions(exportDirectory),
            ConvertToHeic = true,
            HeicQuality = settings.HeicQuality > 0 ? settings.HeicQuality : ConversionDefaults.HeicQuality,
            Parallelism = Math.Clamp(settings.Concurrency, 1, 8)
        };
        var ui = new DesktopProgressReporter(value =>
        {
            var elapsed = stopwatch.Elapsed.TotalSeconds;
            var rate = elapsed > 0.05 ? value.Completed / elapsed : 0;
            var eta = rate > 0 ? (int)Math.Ceiling((value.Total - value.Completed) / rate) : 0;
            var released = value.Total > 0 ? Interlocked.Read(ref _estimatedSavedBytesTotal) * value.Completed / value.Total : 0;
            var pct = value.Total > 0 ? value.Completed * 100.0 / value.Total : 0;
            StripProgressPercent = pct;
            StripProgressStatusText = _localizer.Format("StripProgressFormat", pct, value.Completed, value.Total);
            RunningFileName = value.CurrentItem;
            InstantThroughputText = _localizer.Format("StripThroughputFormat", rate);
            RemainingSecondsText = _localizer.Format("StripEtaFormat", eta);
            ReleasedSpaceText = $"{released / (1024.0 * 1024.0):F1} MB";
        });
        var progress = new PausableProgress(ui, _pauseGate, token);
        _ = RunStripAsync(request, settings, inPlace ? CurrentInputPathText : exportDirectory, progress, stopwatch, cts);
    }

    private async Task RunStripAsync(StripRequest request, DesktopSettings settings, string resultLocation, IProgress<BatchProgress> progress, Stopwatch stopwatch, CancellationTokenSource cts)
    {
        try
        {
            var report = await Task.Run(async () =>
            {
                await using var metadata = ExifToolMetadataService.Create(NullIfBlank(settings.ExifToolPath), request.Parallelism);
                IImageConverter imageConverter = ToolLocator.Find(HeifEncImageConverter.ExecutableName, NullIfBlank(settings.HeifEncPath)) is { } heifEnc
                    ? HeifEncImageConverter.Create(heifEnc)
                    : MagickImageConverter.Instance;
                return await new MotionPhotoStripper(metadata, imageConverter).StripAsync(request, progress, cts.Token);
            });

            stopwatch.Stop();
            if (report.Canceled || _stripCts != cts)
            {
                return;
            }

            var original = Interlocked.Read(ref _analysisOriginalBytes);
            NetSavedResult = $"+{report.BytesSaved / (1024.0 * 1024.0):F1} MB";
            BeforeCountText = _localizer.Format("BeforeCountFormat", report.Items.Count);
            if (original > 0)
            {
                AfterTotalResult = $"{Math.Max(0, original - report.BytesSaved) / (1024.0 * 1024.0):F1} MB";
                SavedPercentResult = _localizer.Format("StripSavedPctFormat", report.BytesSaved * 100.0 / original);
            }

            if (report.Failed > 0)
            {
                ErrorText = string.Join(Environment.NewLine, report.Items.Where(i => i.Kind == OutcomeKind.Failed).Take(5).Select(i => _localizer.Format("StripFailedItemFormat", Path.GetFileName(i.Source), i.Message)));
            }

            CurrentStage = 3;
            CompletionEffects.RunOnTaskComplete(settings, resultLocation);
        }
        catch (OperationCanceledException)
        {
            // 用户中止，界面已由 AbortStrip 复位
        }
        catch (Exception ex)
        {
            ErrorLogger.Log(ex, "空间瘦身");
            if (_stripCts == cts)
            {
                CurrentStage = 1;
                ErrorText = ex.Message;
            }
        }
        finally
        {
            if (_stripCts == cts)
            {
                _stripCts = null;
            }

            cts.Dispose();
        }
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
