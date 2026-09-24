# 阶段 1：桌面基础设施与外壳（方案 A：统一图库工作台）

分支：`claude/exciting-knuth-n0b189-phase1`（叠加在阶段 0 分支之上）。PR 基准分支为阶段 0 分支。

## 1. 现状问题（本阶段要解决的）

- 全局单例：`LocalizationService.Instance` 被引用约 90 处，`PlaybackHost.Instance` 8 处；ViewModel 之间靠 `Action` 回调互相接线（`OnShowModal`/`OnCloseModal`/`OnConfirmed`/`OnBatchReportReady`/`RequestSelectFolder`…）。
- 本地化三处数据源（两份 XAML + `LocalizationService.cs` 中的 `ZhStrings`/`EnStrings`，约 900 行）：XAML 缺 47 个键；C# 字典有重复键（`SelectedSummaryFormat`、英文 `QuickLookPause`），后者覆盖前者；`SetLanguage` 用 C# 值覆盖资源；启动时子 VM 在 `SetLanguage` 之前创建导致英文界面残留中文；硬编码文案：`ToolsViewModel.cs` 镜像名、`ReportViewModel.cs`（"实况转换批次"、CSV 表头）、`QuickLookDialogViewModel.cs`（"实况播放中"）、`ConvertViewModel.BuildGroups` 的 "yyyy年"。
- `ConvertViewModel` 1542 行，混合画廊、扫描、缩略图、参数、批处理、报告、弹窗。
- 弹窗：删除确认按钮 `ConfirmCommand` 未随输入刷新 CanExecute（永远点不了）；仅 QuickLook 支持 Esc；`MainWindowViewModel.CloseModal` 绕过 QuickLook 的 `Cleanup`；弹窗用写死的十六进制颜色（`#FEF3C7`、`#92400E`、`#78350F`、`#CC0F172A` 等）在深色主题下对比度差；`DeleteConfirmDialog.axaml`、`ArbitrateDialog.axaml` 中有乱码注释。
- 设置：`SettingsService.Save` 非原子写入；设置页主题/语言是"即时预览 + 手动保存"，离开不保存也不回滚；`AutoDownloadDependencies` 从未被使用；转换页的 HEIC 质量/命名/源文件处理与设置页同名项互不同步；瘦身页改写转换页的 `LastScanDirectory`；窗口大小位置不记忆。
- 稳定性：无全局未处理异常处理；多处 `Process.Start` 无 try/catch（`ReportViewModel.OpenOutputDir`、`StripViewModel.OpenOutputDir`）；`ExportErrorLog` 同步写桌面、CSV 未转义、表头写死中文；关窗不取消任务不结束子进程却删除临时目录；`PickStripFolderAsync` 在 UI 线程枚举目录；`App.axaml.cs` 中残留写死 `F:\AppleLivePhotoTest` 的截图代码。
- 体验：报告页"强制重试"只是切换标签页；`CompactToolbar` 上显示的 Ctrl+A / Esc 快捷键没有任何绑定（快捷键在阶段 5 统一实现，本阶段只需不再显示虚假提示）；转换页剩余时间与吞吐率从未赋值；报告页用字符 "➔" 充当图标（违反零 Emoji）。

## 2. 目标结构

```
src/LivePhotoConvert.Desktop/
  App.axaml(.cs)            组合根：AppServices.Build() → 初始化 Localizer/主题 → ShellWindow
  Program.cs                全局异常钩子（AppDomain、TaskScheduler、Dispatcher.UIThread.UnhandledException）
  Infrastructure/
    AppServices.cs          Microsoft.Extensions.DependencyInjection 注册（全部单例或瞬态，禁止反射扫描）
    ILocalizer.cs / Localizer.cs
    SettingsStore.cs        （取代 SettingsService）
    DesktopSettings.cs      （由 Models 迁入，含 SchemaVersion、窗口状态、图库视图偏好、动作参数）
    IDialogService.cs / DialogService.cs / DialogViewModel.cs
    IFilePicker.cs / FilePicker.cs
    IShellLauncher.cs / ShellLauncher.cs
    AppLifetime.cs          关窗流程
    ErrorLogger 使用 Core 的 LivePhotoConvert.Core.Services.ErrorLogger
  Features/
    Shell/     ShellWindow.axaml(.cs)、ShellViewModel.cs
    Library/   LibraryView、LibraryViewModel（画廊，来自 ConvertViewModel）、InspectorView、InspectorViewModel、ConversionAction.cs
    Tasks/     TaskCenter.cs（服务）、ConversionRunner.cs、TasksView、TasksViewModel、TaskReportViewModel（来自 ReportViewModel）
    Tools/     ToolsView、ToolsViewModel（仅做依赖注入与本地化适配，功能改造在阶段 4）
    Settings/  SettingsView、SettingsViewModel
    Dialogs/   各弹窗 VM + View（改为 DialogViewModel<TResult>）
```

画廊内部实现（`AlbumScanner`、`ThumbnailReader`、`LruThumbnailManager`、`TimelineGroup`、`GalleryItem`、`PhotoCardControl`、`PlaybackHost`、`LivePhotoStreamPlayer`）本阶段**只做依赖注入与本地化适配，不改算法**（阶段 2、3 重写）。

## 3. 关键设计

### 3.1 依赖注入

- 包：`Microsoft.Extensions.DependencyInjection` 10.0.x（AOT 兼容，禁止 `Scan`/反射注册）。
- `AppServices.Build()` 返回 `ServiceProvider`；`App.OnFrameworkInitializationCompleted` 中构建，`ShellWindow` 的 DataContext 为 `ShellViewModel`。
- 注册：`SettingsStore`、`ILocalizer`、`IDialogService`、`IFilePicker`、`IShellLauncher`、`TaskCenter`、`AppLifetime` 为单例；各页面 VM 单例（整个窗口生命周期只有一份）。
- `PlaybackHost` 本阶段保留静态实例，但 VM 通过构造函数接收（注册 `services.AddSingleton(PlaybackHost.Instance)`），其 FFmpeg 路径从 `SettingsStore` 读取。
- 测试可通过 `AppServices.Build(configure)` 替换服务（例如设置文件路径指向临时目录）。

### 3.2 本地化单一数据源

- 唯一数据源：`Assets/Strings.zh-CN.axaml`、`Assets/Strings.en-US.axaml`。删除 `LocalizationService.cs`（含 C# 字典）与 `ILocalizationService.cs`。
- **迁移规则**（用脚本完成，不要手抄）：对 C# 字典中的每个键，XAML 中的值以 C# 字典**最终生效值**为准（重复键取最后一次赋值）；C# 中有而 XAML 中没有的键补入 XAML；XAML 中 `&` 写成 `&amp;`，含前导/尾随空格的值加 `xml:space="preserve"`。
- 迁移后删除从未被引用的键（在 .axaml 的 `{DynamicResource X}`、.cs 中的字符串字面量里都找不到的键；注意 `AboutInfo` 的 `Credit*Desc` 等通过字段间接引用的键要保留）。
- `Localizer`：启动时用 `AvaloniaXamlLoader.Load(new Uri("avares://LivePhotoConvert/Assets/Strings.xx.axaml"))` 加载两份 `ResourceDictionary` 并缓存；`SetLanguage` 替换 `Application.Resources.MergedDictionaries` 中的字符串字典，设置 `CultureInfo.CurrentCulture/CurrentUICulture`，触发 `LanguageChanged`。
- 接口：`string Language`（"zh-CN"/"en-US"）、`CultureInfo Culture`、`string this[string key]`、`string Format(string key, params object?[] args)`（用 `CompositeFormat` 缓存）、`event EventHandler? LanguageChanged`、`void SetLanguage(string code)`。缺失键返回键名并 `Debug.Fail`。
- 语言设置必须在创建任何页面 VM 之前应用。
- 日期分组标题用资源中的格式串（新增 `GroupTitleMonthFormat`、`GroupTitleYearFormat`），不再写死 "yyyy年"。
- 测试：两份 XAML 键集合一致、值非空；所有 `DynamicResource` 与代码中通过 `ILocalizer` 使用的字面量键都存在。

### 3.3 弹窗服务

```csharp
public abstract partial class DialogViewModel : ViewModelBase
{
    // 由 DialogService 设置；Close 完成等待中的任务
    protected void Close(object? result);
    public virtual object? CancelResult => null;
    [RelayCommand] public void Cancel() => Close(CancelResult);   // Esc 与遮罩点击也走这里
    protected internal virtual void OnClosed() { }                 // 释放资源（QuickLook 在此停止播放）
}
public abstract class DialogViewModel<TResult> : DialogViewModel;
public interface IDialogService
{
    DialogViewModel? Current { get; }                              // ShellWindow 绑定显示
    Task<TResult?> ShowAsync<TResult>(DialogViewModel<TResult> dialog);
}
```

- 同一时刻只显示一个弹窗；新弹窗排队。所有弹窗 VM 去掉 `On*` 回调，改为返回结果：
  - `DeleteConfirmDialogViewModel : DialogViewModel<bool>`：`PasswordInput` 变化时 `NotifyCanExecuteChangedFor(nameof(ConfirmCommand))`；去掉视图里重复的 `IsEnabled` 绑定。
  - `LowDiskSpaceDialogViewModel : DialogViewModel<bool>`：新增"仍然继续"按钮（返回 true）。
  - `BackupConfirmDialogViewModel : DialogViewModel<bool>`（就地瘦身确认）。
  - `ArbitrateDialogViewModel : DialogViewModel<ArbitrationVerdict>`（Accept/Reject/Dismiss）。
  - `QuickLookDialogViewModel : DialogViewModel<bool>`，`OnClosed` 中停止播放并释放帧；导航通过构造参数传入的委托 `Func<int, PhotoCardItemViewModel?>`。
  - 新增 `ConfirmDialogViewModel : DialogViewModel<bool>`（标题、说明、确认按钮文案、是否危险操作），用于关窗确认、缺少依赖提示等通用场景。
  - 新增 `StripCompareDialogViewModel`：承载原瘦身页的卷帘对比（沿用 `CurtainCompareControl` 与原 `LoadSinglePhotoComparisonAsync` 逻辑，阶段 4 再重写为真实编码）。
- 弹窗颜色全部改用主题 token（`WarningSoftBrush`、`WarningBrush`、`DangerSoftBrush`、遮罩用新 token `OverlayBrush`），在 `Styles.axaml` 的 ThemeDictionaries 中为浅色/深色分别定义；修复乱码注释。

### 3.4 设置

- `SettingsStore`：`DesktopSettings Current`；`void Update(Action<DesktopSettings> change)` 修改后防抖 500ms 保存；`void Flush()`（退出时调用）；写入走"临时文件 + `File.Move(overwrite: true)`"；读取失败时把坏文件改名为 `settings.json.corrupt` 并使用默认值；`SchemaVersion` 当前为 2，旧文件迁移（`OverwriteSameName` → `ConflictPolicy`，删除 `AutoDownloadDependencies`）。
- 设置页只保留全局偏好：主题、语言、并发数、完成提示音、完成后打开目录、退出时清理临时文件、关于。**修改即时生效并自动保存**，去掉"保存"按钮，显示"已自动保存"提示。
- 转换参数（动作、命名、源文件处理、冲突策略、保留子目录层级、HEIC 质量、输出目录、瘦身就地/导出目录）只在检查器中设置，并持久化到 `DesktopSettings`。
- 图库视图偏好（排序、分组、缩放、裁切）持久化。
- 窗口位置、大小、最大化状态持久化；恢复时确保窗口落在当前屏幕范围内。

### 3.5 外壳与导航（方案 A）

- 左侧导航：**图库**（`Library`）、**任务**（`Tasks`，运行中显示百分比徽章）、**依赖**（`Tools`，沿用状态点）、底部 **设置**。取消原"实况互转 / 空间瘦身 / 批次报告"三个入口。
- 标题栏沿用现有自绘标题栏。
- 弹窗宿主：`ContentControl` 绑定 `IDialogService.Current`，通过 `DataTemplates` 映射弹窗视图；`ShellWindow` 处理 Esc（`Current?.CancelCommand`）。

### 3.6 图库工作台

- **LibraryView** = 现有画廊区域（顶部统计徽章、`CompactToolbar`、筛选条、`ListBox` 行虚拟化、空状态）+ 右侧 320px 检查器；原转换页右侧控制台与底部输出目录栏并入检查器。
- **动作**（`ConversionAction`）：`ToAndroid`（合成）、`ToApple`（还原）、`Extract`（拆分提取）、`Strip`（瘦身）。检查器顶部为分段选择器。扫描模式映射：ToAndroid→0，ToApple→1，Extract→2，Strip→0（该模式同时包含苹果实况对与安卓动态照片）。沿用 `_dirScanCache` 按模式缓存。
- **检查器内容**随动作切换：
  - 通用：适用项统计（选中项中适用于当前动作的数量与体积；未选中时为全部）、输出目录（选择/打开）、保留子目录层级、重名处理（追加序号/覆盖）。
  - ToAndroid：命名格式 + 示例、源文件处理（含删除警示）。
  - ToApple：HEIC 质量、源文件处理。
  - Extract：源文件处理。
  - Strip：就地替换开关（开启需 `BackupConfirmDialog` 确认，确认前开关保持关闭）、导出目录（非就地时）、转 HEIC 开关与质量、空间释放预估（对适用项调用 `MotionPhotoStripper.AnalyzeAsync`，选择变化后防抖 400ms、可取消）、"对比预览"按钮（对当前聚焦或首个选中的卡片打开 `StripCompareDialog`）。
  - 底部主按钮：文案随动作与数量变化；任务运行中禁用（`CanExecute`）。
- **启动前检查**（检查器执行，全部经 `IDialogService`）：缺少所需工具时弹 `ConfirmDialog` 并可跳转依赖页；磁盘空间不足弹 `LowDiskSpaceDialog`（可仍然继续）；源文件处理为"删除"时弹 `DeleteConfirmDialog`；然后提交给 `TaskCenter`。
- 人工裁决（`ArbitrateDialog`）与 QuickLook 由 `LibraryViewModel` 通过 `IDialogService` 打开。
- `LibraryViewModel` 暴露 `IReadOnlyList<PhotoCardItemViewModel> SelectedOrAllCards`、`PhotoCardItemViewModel? FocusedCard`、`event SelectionChanged` 给检查器使用。

### 3.7 任务中心

- `TaskCenter`（单例，`ObservableObject`）：`bool IsRunning`、`RunningTaskViewModel? Current`、`ObservableCollection<TaskReportViewModel> History`（最新在前，会话内保存）、`Task<TaskReportViewModel> RunAsync(ConversionJob job)`、`void Cancel()`、`void TogglePause()`、`Task CancelAndWaitAsync(TimeSpan timeout)`、`event EventHandler<TaskReportViewModel>? Completed`。
- `ConversionJob`：动作、参数快照、卡片快照（启动时固定）、工具路径快照、并发数。
- `ConversionRunner`：把 `ConversionJob` 映射到 `MotionPhotoMerger` / `MotionPhotoSplitter` / `MotionPhotoStripper`（逻辑取自现有 `ConvertViewModel.ExecuteBatchAsync` 与 `StripViewModel.RunStripAsync`），返回 Core `BatchReport`；所有动作都支持暂停（`PausableProgress`，在条目之间阻塞）。
- `RunningTaskViewModel`：标题、百分比、`已完成/总数`、当前文件、吞吐（项/秒）、剩余时间（按已完成速率估算）、是否暂停；进度事件 100ms 节流（沿用 `DesktopProgressReporter`）。
- 完成后：生成 `TaskReportViewModel`（由现有 `BatchReportMapper` + `ReportViewModel.Populate` 改造），插入历史，切换到任务页；非取消时执行完成提示音/自动打开目录。
- **TasksView**：顶部运行中任务卡片（进度、暂停/继续、取消）；下方左侧历史列表（时间、动作、成功/失败数），右侧选中报告详情（沿用原报告页的指标卡与明细表，筛选全部/异常/成功）。
- 报告行操作："打开所在位置"（成功项定位输出文件，失败项定位源文件，用 `IShellLauncher.RevealFile`）；报告级"重试失败项"：用失败项的源文件重新提交同一动作与参数（新任务）。
- 导出 CSV：通过 `IFilePicker` 选择保存位置，后台写入，字段按 RFC 4180 转义，表头走本地化。
- 报告页 "➔" 改为 `ic:SymbolIcon Symbol="ArrowRight"`。

### 3.8 生命周期与异常

- `Program.Main`：`AppDomain.CurrentDomain.UnhandledException` 与 `TaskScheduler.UnobservedTaskException`（`SetObserved`）写日志。
- `App`：`Dispatcher.UIThread.UnhandledException` 写日志、`e.Handled = true`，通过 `IDialogService` 显示错误提示（`ConfirmDialog` 单按钮），避免进程退出。
- `AppLifetime.OnClosingAsync`：任务运行中 → 确认弹窗 → `TaskCenter.CancelAndWaitAsync(10s)`；停止 `PlaybackHost`；`SettingsStore.Flush()`；按设置清理临时目录（`SafetyGuard.CleanAllTempDirectories`）；然后真正关闭。
- 启动时 `SafetyGuard.CleanOrphanTempDirectories()` 放到后台线程。
- 删除 `App.axaml.cs` 中 `--snapshot` 截图代码。

## 4. 工作包（依次由子代理实现，主控审查后提交）

| 编号 | 内容 | 依赖 |
|---|---|---|
| WP1.1 | 本地化单一数据源（3.2）：迁移脚本、`ILocalizer`/`Localizer`、全量替换 `LocalizationService.Instance`（临时通过静态 `Localizer.Current` 过渡可以接受，但 WP1.2 结束时必须清零）、未用键清理、硬编码文案、键一致性测试 | — |
| WP1.2 | 基础设施（3.1、3.3、3.4、3.8）：DI 组合根、`SettingsStore`、`IDialogService` 与全部弹窗改造、`IFilePicker`、`IShellLauncher`、全局异常、`AppLifetime`、删除截图代码；此时外壳仍为旧的五个标签页 | WP1.1 |
| WP1.3 | 任务中心（3.7）：`TaskCenter`、`ConversionRunner`、`TasksView`，把转换与瘦身的执行都迁入任务中心；报告页并入任务页 | WP1.2 |
| WP1.4 | 外壳与图库工作台（3.5、3.6）：新导航、`LibraryViewModel` 拆分、检查器四种动作、瘦身并入图库、`StripCompareDialog`、删除旧 `StripView`/`ConvertView` 右侧控制台、窗口状态与视图偏好持久化 | WP1.3 |
| WP1.5 | 测试与验证：引入 `Avalonia.Headless.XUnit` 12.1.2，新建 `tests/LivePhotoConvert.Desktop.Tests`（界面冒烟：外壳加载、四个页面、每种弹窗、语言切换后无缺失键；截图输出到测试输出目录供审查）；把 E2E 中针对旧 VM 的测试迁移到新结构；删除重复或失效的测试 | WP1.4 |

每个工作包交付时：`dotnet build` 0 警告 0 错误；`dotnet test` 全绿；`dotnet publish src/LivePhotoConvert.Desktop -r linux-x64 -c Release` 0 警告。

## 5. 验收

- 全局搜索 `LocalizationService`、`.Instance`（除 `PlaybackHost`、转换器与 Core 中合法的单例外）、`OnShowModal`、`OnCloseModal`、`Request*Folder` 结果为空。
- 界面冒烟测试覆盖中英文；截图人工审查布局（浅色、深色各一套）。
- AGENTS.md 同步更新：4.4 MVVM（DI、弹窗服务）、4.6 本地化（两处同步）、目录结构。
