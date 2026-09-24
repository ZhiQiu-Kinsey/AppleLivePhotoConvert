# LivePhotoConvert 协作指南（AGENTS.md）

面向 AI 编码助手与贡献者的仓库约定。**修改代码前先通读本文件**；子系统的设计取舍见 [docs/design](docs/design/README.md)，两者冲突时以本文件和代码为准。

> 最后更新：v4.0.0（2026-09-24）

---

## 1. 项目速览

- **定位**：实况照片工作台——浏览相册，在苹果实况与安卓动态照片之间互转、解包、瘦身，并保留 HDR（Ultra HDR 增益图、HDR 视频色调映射预览与 10-bit 转码）。不向通用相册工具扩展。
- **形态**：.NET 10 + Avalonia 12 桌面应用，Native AOT 单文件发布（`TrimMode=full`）。命令行入口已移除，**不要恢复 CLI 或为新功能加命令行分支**；可复用能力放 Core，交互放 Desktop。
- **名称**：工程、可执行文件（`LivePhotoConvert.exe`）、设置与缓存目录都叫 `LivePhotoConvert`，不要改名。
- **平台**：正式支持 Windows x64（安装版、可更新便携版与纯解压版，均为 `win-x64`）；Linux 可从源码运行（依赖自行安装、无回收站）；macOS 未适配；安卓 / iOS 不支持（依赖外部命令行进程）。
- **技术栈**：Avalonia 12.1.2（Desktop / Themes.Fluent / Fonts.Inter）、CommunityToolkit.Mvvm 8.4.0、FluentIcons.Avalonia、Microsoft.Extensions.DependencyInjection、Velopack（安装与更新，vpk CLI 版本由 `.github/scripts/velopack-pack.ps1` 从 csproj 读取，始终与库同版本）；Core 只引用 `Magick.NET-Q8-x64`（图像解码）与 `SharpCompress`（7z 解压）。外部工具：ExifTool、FFmpeg、heif-enc / heif-dec。
- **测试**：xunit.v3。`LivePhotoConvert.Core.Tests`（引擎）、`LivePhotoConvert.Desktop.Tests`（VM / 服务单元测试 + Avalonia Headless 界面测试）、`LivePhotoConvert.E2E`（真实外壳 + 真实工具的黑盒流程）。

## 2. 架构速览

```
src/LivePhotoConvert.Core/            纯引擎：无 UI 依赖、AOT 兼容
  Media/        MediaFileTypes 嗅探；MotionPhotoLayout 定位内嵌视频；MotionPhotoXmp 解析与编辑 XMP；
                FastImageHeaderReader / QuickTimeHeader 不经 ExifTool 读尺寸、方向、拍摄时间与配对标识；
                LibraryScanner → LibraryItem 统一扫描；Thumbnails/ 分档缩略图与磁盘缓存；UltraHdr/ 增益图换算与写出
  Metadata/     IMetadataService：ExifTool 常驻会话池 + 批量 JSON（ExifToolJson）；AppleMakerNote 模板；AppleHdrHeadroom
  Pairing/      MediaPairMatcher（ContentIdentifier 优先，再按目录 + 主干）；PairValidator（纯函数）
  Pipeline/     BatchRunner；OutputCommitter 暂存与原子落盘；SourceDisposition；TempWorkspace；BatchReport / OutcomeReason
  Services/     MotionPhotoMerger（转安卓）/ MotionPhotoSplitter（转苹果、解包）/ MotionPhotoStripper（瘦身）
  External/     ProcessRunner；FFmpeg / heif-enc / Magick 转换器；HeifDecoder；VideoStreamProbe；ToolLocator；
                Tools/：tools.json 依赖清单、ToolInstaller 校验安装、ToolRegistry 版本与能力探测
  Io/ Platform/ BinaryFile 流式拼接切片、UniquePath、FileHelper；RecycleBin、WindowsShellThumbnailSource

src/LivePhotoConvert.Desktop/         Avalonia 桌面端（程序集名 LivePhotoConvert）
  App.axaml.cs / Program.cs           组合根（主题与语言先于任何页面 VM 生效）/ 全局异常钩子
  Infrastructure/  AppServices（DI 显式注册）、Localizer、SettingsStore / DesktopSettings、DialogService、
                   FilePicker、ShellLauncher、Navigator、ThemeService、AppLifetime（关窗流程）、AppShortcuts（快捷键唯一定义）
  Features/Shell/    ShellWindow（导航、弹窗宿主、全局按键）、ShellViewModel
  Features/Library/  LibraryView/VM（画廊门面）、InspectorView/VM（动作与参数）、JobFactory、StripEstimator / StripSampler；
                     Gallery/（LibraryCatalog、JustifiedLayoutEngine、GalleryLayoutViewModel、GallerySelection）；
                     Thumbnails/（ThumbnailPipeline、ByteBudget、GalleryThumbnailBinder、GalleryMetrics）
  Features/Playback/ LivePhotoPlayer（RawVideoDecoder、FrameStore、SurfacePair）、PlaybackService
  Features/Tasks/    TaskCenter、ConversionRunner、TasksView/VM、TaskReportView/VM、OutcomeTexts、CsvWriter
  Features/Updates/  IUpdateService、VelopackUpdateService、GithubReleaseSource、UpdateCenter、ReleaseNotes、UpdateTexts
  Features/Tools/ Settings/ Dialogs/   依赖页；偏好设置与关于；各弹窗（DialogViewModel<TResult>，含 UpdateDialog）
  Controls/ Converters/ Assets/        GalleryList / GalleryRowPresenter（卡片复用）、PhotoCardControl、CurtainCompareControl、ShortcutTip 等；Styles.axaml 与两份 Strings

tests/  Core.Tests（按 Media / Metadata / Pairing / Pipeline / Services / External / Io 分目录，Support/ 为替身与合成媒体）
        Desktop.Tests（目录与产品一致，Harness/ 为 ShellSession 等测试宿主，Docs/ 为 README 截图生成）
        E2E（Workflows/）
```

## 3. 必守规则

### 3.1 Native AOT

- 禁止反射扫描、`Reflection.Emit` 与未标注 `[DynamicallyAccessedMembers]` 的泛型；JSON 一律 `System.Text.Json` Source Generator；新依赖必须 AOT 兼容。
- 读取程序集信息只用 `GetCustomAttribute<T>()` 这类静态类型 API（「关于」页读 `AssemblyInformationalVersionAttribute`）。
- `dotnet publish -r win-x64` 必须 0 裁剪 / AOT 警告（CI 以警告为错误）。

### 3.2 原子落盘与非破坏

- **最终输出只经 `OutputCommitter`**：先写目标目录内的 `~lpc-t{ticks}-{guid}` 暂存文件，校验后同卷重命名。同批次文件名由它统一分配，**任何冲突策略下都不覆盖本批次的源文件或已写出的文件**；成对输出用 `CommitGroup`，失败整组回滚；覆盖模式先把已有目标改名备份，失败还原。路径比较一律 `Path.GetFullPath` + 忽略大小写。
- 源文件时间戳经 `Commit` / `CommitGroup` / `ReplaceSource` 的 `timestamp` 参数在落盘后写到最终文件，不要对暂存文件设时间戳。
- 就地替换只走 `OutputCommitter.ReplaceSource`：同扩展名 `File.Replace` + `.livephoto_backup`，成功后立即删除备份；扩展名改变时先落盘新文件再删原文件，删除失败撤销新文件。
- 源文件处置（移动 / 回收站 / 删除）只在输出落盘并校验后由 `SourceDisposition` 执行。瘦身移走 Apple 配对视频前必须通过 `PairValidator`，且只走回收站。
- 中间文件放 `TempWorkspace`（`%TEMP%/LivePhotoConvert/temp-{guid}/`）；启动清理 24 小时前的孤儿目录，退出只删本进程创建的目录。外部工具只写流程自己生成的文件，从不直接改用户原文件。
- 拼接与切片用 `BinaryFile.ConcatAsync` / `CopySegmentAsync` 流式完成，**严禁整文件读入内存**；缓冲用 `ArrayPool<byte>.Shared` 并在 `finally` 归还。

### 3.3 外部进程

- 一律经 `ProcessRunner`：参数逐项 `ArgumentList`、标准输入立即关闭、默认 10 分钟超时、取消即结束进程树；FFmpeg 固定带 `-nostdin`，不用 `-hwaccel auto`。
- ExifTool 常驻会话经标准输入逐行传参，**拒绝含换行符的参数**；输出按块读取，以缓冲末尾的 `{readyN}` + 换行判定结束（`-b` 输出没有尾随换行）；读取一律批量 `-j -n -G1 -a` 后由 `ExifToolJson` 解析，禁止逐标签往返；`-api QuickTimeUTC=1` 经 `-common_args` 对所有命令生效。
- 工具路径、版本与能力由 `ToolRegistry` 缓存，可执行性探测由 `ToolLocator` 按路径、大小、修改时间缓存；不要在热路径上重复启动 `-version`。依赖下载只经 `ToolInstaller`（清单锁定版本、SHA256 / integrity 校验、路径穿越防护、试运行后整目录替换）；更新 `tools.json` 必须附实际下载计算的哈希。

### 3.4 MVVM、依赖注入与设置

- 视图全部 `x:CompileBindings="True"` + 显式 `x:DataType`；可能为空的路径写 `Current?.Title`（值类型加 `TargetNullValue`），绑定错误日志必须为空。
- VM 继承 `ViewModelBase`，用 `[ObservableProperty]` / `[RelayCommand]`；服务与页面 VM 在 `AppServices` 显式注册、构造函数注入；**VM 之间不用 `Action` 回调接线，不新增静态单例**。
- 弹窗继承 `DialogViewModel<TResult>`，调用方 `await dialogs.ShowAsync(...)`；Esc 与遮罩走 `CancelCommand`，资源在 `OnClosed` 释放。
- 设置只经 `SettingsStore.Update(...)`（防抖、原子写入、坏文件改名为 `.corrupt`）。`DesktopSettings` 新增字段缺失时取默认值即可；**重命名、删除或改变含义时提升 `SettingsStore.CurrentSchemaVersion` 并在 `Migrate` 中补迁移**。
- 文件选择、打开目录与链接只经 `IFilePicker` / `IShellLauncher`，不直接 `Process.Start`。
- 快捷键只在 `AppShortcuts` 定义；界面提示用 `ShortcutTip` 附加属性由定义合成，文案资源里不手写按键。

### 3.4.1 安装与自动更新

- `Program.Main` 第一句必须是 `VelopackApp.Build().Run()`（仅 Windows），前面不能加任何代码：安装、更新、卸载的钩子参数由它处理后直接退出。
- packId 固定为 `LivePhotoConvert.App`，**不得改成 `LivePhotoConvert`**：卸载会整体删除 `%LocalAppData%\<packId>`，而日志、缩略图缓存与依赖工具在 `%LocalAppData%\LivePhotoConvert`。
- 程序目录由更新器管理时（`IUpdateService.IsSupported`），持久文件一律不写进程序目录；依赖工具经 `ToolDirectories.GetWritableToolDirectory(true)` 装到 `%LocalAppData%\LivePhotoConvert\tools`。
- 信任链：发布列表只直连 api.github.com → 附件 `digest` → `releases.win.json` → 清单中的 SHA256 → 更新包。镜像只用于下载，没有 digest 的清单不得走镜像。
- 更新失败一律抛 `UpdateException(UpdateFailureKind)`，界面经 `UpdateTexts` 本地化；自动检查失败只记日志。退出更新经 `IAppShutdown.ShutdownForUpdateAsync`（先完成正常收尾再启动更新程序）；`IBackgroundWork` 实现必须触发 `BusyChanged`。

### 3.5 本地化

- `Assets/Strings.zh-CN.axaml` 与 `Assets/Strings.en-US.axaml` 是唯一数据源，**两份必须同时修改、键集合一致**。视图用 `{DynamicResource Key}`，代码用 `ILocalizer`（`localizer["Key"]`、`localizer.Format("XxxFormat", ...)`），.cs 中不写界面可见的中英文字面量。
- 格式串以 `Format` 结尾，两种语言占位符一致；日期格式也放资源。XAML 中转义 `&` `<` `>`，首尾空格加 `xml:space="preserve"`。缺失键在 Debug 下 `Debug.Fail`（测试进程直接终止）。
- **Core 只返回原因码**：跳过 / 失败用 `OutcomeReason` + `OutcomeCause`，附注用 `OutcomeNoteKind`，异常原文放 `ItemOutcome.Detail`；`Features/Tasks/OutcomeTexts` 为每个枚举值映射字符串键，新增枚举值须同步两份 Strings。

### 3.6 界面样式与无障碍

- **零 Emoji**：图标只用 FluentIcons（`ic:SymbolIcon`），界面源码与字符串中不出现 Emoji。
- 颜色只用主题 token（`Styles.axaml` 的 ThemeDictionaries 为浅色 / 深色各定义一份，键集合一致），不写字面量颜色；文字与图标对比度达到 WCAG AA（正文 ≥4.5:1，大字与图标 ≥3:1）；最小字号 11px。
- 新样式写进 `Assets/Styles.axaml`，不在视图里堆内联样式；样式类必须被视图或代码使用；带自定义悬停底色的按钮类必须加入呈现器跟随规则，否则 Fluent 模板的悬停底色会覆盖它。

### 3.7 画廊内存与播放

细节与取舍见 [phase2-gallery.md](docs/design/phase2-gallery.md)、[phase3-playback-hdr.md](docs/design/phase3-playback-hdr.md)。必须保持的不变式：

- 排版只用 `JustifiedLayoutEngine`（完整行铺满、行高 ≤ 目标 × 1.3），比例取扫描得到的转正后宽高，缩略图到达不触发重排；几何常量只来自 `GalleryMetrics`。
- 卡片控件复用：`GalleryList` 以 `GalleryRowPresenter` 作行容器，`PhotoCardControl` 的逻辑父级固定为列表（卡片池），行回收时卡片只进出可视树、换数据上下文；不要改回"行模板 + 内层 ItemsControl"（控件重新挂上逻辑树要重新套用全部样式，每张数毫秒）。卡片内不绘制的容器用 `Panel` / `Decorator`，不用 `Border`。
- 位图是非托管内存，**严禁无界缓存**：缩略图按字节计入 `ByteBudget`（设置 64～1024MB），已实例化卡片经 `GalleryThumbnailBinder` 钉住、永不驱逐，满足 `ResidentBytes ≤ 预算 + 钉住字节`；驱逐先放开绑定再延迟释放像素。解码或像素分配失败降级为占位图，异常不得穿透界面线程。**界面线程零 I/O**。
- 播放只走 `LivePhotoPlayer`（`ILivePhotoPlayer`，由 `PlaybackService` 创建）：安卓内嵌视频用 FFmpeg `subfile` 直读，不切临时文件、不把路径写回卡片；`rawvideo` BGRA 输出、按 PTS 换帧；帧按字节预算（悬浮 96MB、QuickLook 256MB）；HDR 源缺 zscale / tonemap 时报 `HdrToneMapUnavailable`，不得静默显示发灰画面。
- **播放单实例**：整窗一个悬浮播放器，QuickLook 独占一个；新播放先结束旧 FFmpeg；退出与替换 FFmpeg 前经 `IPlaybackControl.StopAllAsync` 等待播放进程退出。
- 调整预算、档位、并发或播放参数必须补内存压力验证（`GalleryStressTests`、`GalleryScrollTests` 等）。

### 3.8 代码风格

- 主构造函数注入、集合表达式、`System.Threading.Lock`、模式匹配；`<Nullable>enable</Nullable>` 零警告。
- 魔数嗅探用 `ReadOnlySpan<byte>` + UTF-8 字面量；只读集合用 `FrozenSet` / `FrozenDictionary`。
- 注释用中文，只写「为什么」，与 public API 语义一致；不写历史沿革、需求编号或口语。

### 3.9 测试约定

- Core 改动补 `Core.Tests`；Desktop 改动补 `Desktop.Tests`（目录与命名空间对应产品）；跨越「选相册 → 执行 → 落盘」的流程改动补 E2E。**测试验证产品代码，不在测试里重写被测逻辑**。
- 真实外部工具的集成测试在工具缺失时 `Assert.Skip`；新增按环境跳过的用例要同步 CI 的跳过阈值（见 5.2）。
- 界面改动补 Headless 测试（`[AvaloniaFact]` + `Harness/ShellSession`）：能在真实外壳中实例化、绑定错误日志为空、中英切换后无另一语言与资源键名残留。**含 `[AvaloniaFact]` / `[AvaloniaTheory]` 的测试类必须标 `[Collection(ProcessStateCollection.Name)]`**（`TestConventionsTests` 校验），避免与其它界面用例并行。
- 审查用截图用 `Harness/Screenshots.Save` 写到测试输出目录的 `screenshots/`，CI 作为产物上传。README 截图由 `Docs/ReadmeScreenshotTests` 生成，只在设置 `LPC_DOCS_SCREENSHOTS` 时工作，未设置时直接通过（不占 CI 跳过额度）。
- 规则测试会拦截违规：`StringResourceTests`（键一致、无未用键、占位符一致、零 Emoji）、`ViewStyleRulesTests`（主题 token、11px、悬停规则、样式类被使用）、`ThemeContrastTests`（WCAG AA、两套主题键一致）。

## 4. 协议要点

改动转换逻辑前必须理解以下事实；Ultra HDR 的公式与写出细节见 [phase3-gainmap-spike.md](docs/design/phase3-gainmap-spike.md)。

**Google Motion Photo**

- 结构：JPEG（可能带 Ultra HDR 增益图）+ 尾部 MP4。XMP 写 `GCamera:MotionPhoto*`、`GCamera:MicroVideo*`（`MicroVideoOffset` = 视频字节长）与 `Container:Directory`（Primary / GainMap / MotionPhoto 项，顺序与物理顺序一致）。封面帧时间戳取自 MOV 的 still-image-time 轨道，缺失时为 0。
- XMP 读写只走 `MotionPhotoXmp`（托管 XDocument），不依赖 ExifTool 标签表（12.x 不认识 Container，13.x 组名变为 `XMP-GContainer`）；写入在原有 XMP 上合并后整包回写（`-xmp<=`），不丢其它命名空间。
- 定位视频只走 `MotionPhotoLayout`：候选偏移处必须是 `ftyp`；只有 GainMap 没有 MotionPhoto 项的 Ultra HDR 照片不是动态照片；HEIC 动态照片遍历顶层 box（支持 64 位长度与 size 0），内容以 `ftyp` 开头的 `mpvd` 即视频。
- `EmbeddedVideo`：`Offset` / `Length` 是视频数据（切片、播放），`ImageEnd` 是照片结束位置（剥离、截断）；`mpvd` 与三星 SEF 中 `ImageEnd < Offset`，截断必须用 `ImageEnd`。
- 剥离视频只删 MotionPhoto 目录项、保留 GainMap；截断后用 `UltraHdrJpegWriter.RefreshPrimaryLength` 修正 MPF 主图长度；带增益图的照片不转码 HEIC。

**小米 `0x8897`**：小米相册除 XMP 外还校验 Exif `34967`（`0x8897`），合成时必须写入；不要伪造小米私有的 `0x889e`。

**Apple Live Photo 配对**

- 照片端：Apple MakerNotes 的 `ContentIdentifier`（大写 UUID）。ExifTool 不能在没有 MakerNotes 的文件里创建该块，必须用 `AppleMakerNote.BuildTemplateJpeg` 生成模板后 `-tagsFromFile 模板 -MakerNotes` 整块复制并回读校验。
- 视频端：QuickTime Keys 写 `com.apple.quicktime.content.identifier`（同一 UUID），同步 `creationdate` / `make` / `model` / `software` / `GPSCoordinates`（`纬度, 经度, 海拔`）。mvhd / mdhd 按 UTC 存储，写入时传**带偏移**的时间由 ExifTool 换算。还原时 UUID 用 `Guid.NewGuid().ToString().ToUpperInvariant()`。`Keys:StillImageTime` 不可写，**不要写入**。
- 配对：`MediaPairMatcher` 先按 ContentIdentifier，再按「同目录 + 主干」（HEIC > JPG > PNG，MOV > MP4，候选全部保留由合成阶段择优）。`PairValidator`：标识一致直接通过；标识冲突或单边存在、时间差 >3s、仅单边有拍摄时间、视频 >30s 则拒绝；两侧都有时区偏移按绝对时刻比较，否则按当地时间。扫描与合成共用同一判定；人工裁决通过的配对进入 `ForceAccepted`，合成时跳过校验。

**Ultra HDR（合成时 `PreserveHdr`，默认开启）**

- 余量只用 `AppleHdrHeadroom.Compute`（Apple 公式，读 `HDRHeadroom` / `HDRGain`）；主图与增益图必须由同一次 `heif-dec --with-aux` 解码以保证方向一致；增益图经 `AppleGainMapConverter` 查表重编码（Rec.709 反 OETF），不能只改 Gamma。
- ISO 21496-1 与 `hdrgm` XMP 两套元数据都写；Container 中 GainMap 的长度必须等于 MPF 中的增益图长度；组装后、写 MotionPhoto XMP 后、拼接后三次回读校验。
- 任一步失败、源无 Apple 增益图（含只有 ISO `tmap` 的 HEIC）或缺 heif-dec 时降级为 SDR，原因写入 `ItemOutcome.Notes`，**不得让条目失败**。转为苹果时不保留增益图。

## 5. 工作流

### 5.1 本地构建与测试

```bash
export PATH=$HOME/.dotnet:$PATH                     # 按本机 SDK 位置调整；global.json 固定 SDK 10.0.400
dotnet build LivePhotoConvert.slnx                  # 要求 0 警告 0 错误
dotnet test  LivePhotoConvert.slnx                  # 全部测试，Headless 不需要显示器
dotnet run --project src/LivePhotoConvert.Desktop/LivePhotoConvert.Desktop.csproj
dotnet publish src/LivePhotoConvert.Desktop/LivePhotoConvert.Desktop.csproj -r win-x64 -c Release -o dist/aot
LPC_DOCS_SCREENSHOTS=docs/screenshots dotnet test tests/LivePhotoConvert.Desktop.Tests --filter "FullyQualifiedName~ReadmeScreenshotTests"   # 重新生成 README 截图
```

- 集成测试用到的环境变量：`LPC_ULTRAHDR_APP`（libultrahdr 的 `ultrahdr_app`）、`LPC_HDR_SAMPLES`（私有 HDR 样片目录）、`LPC_TOOL_PACKAGE_DIR`（`ToolManifestPackageTests` 用的依赖包目录）。
- Windows 上用 Git Bash 等 POSIX shell 时 `HOME` 为 MSYS 路径、`APPDATA` 可能为空，NuGet 会抛 `Value cannot be null. (Parameter 'path1')`：先 `export HOME='C:\Users\<用户名>'` 与 `export APPDATA='C:\Users\<用户名>\AppData\Roaming'`。
- 交付前：build 0 警告、test 全绿；交付说明写清改动、设计取舍与验证结果。

### 5.2 CI

| 工作流 | 触发 | 内容 |
| :--- | :--- | :--- |
| [`ci.yml`](.github/workflows/ci.yml) | push `main`、所有 PR、手动、被 `release.yml` 调用（不接受外部指定检出引用） | **Linux**：安装 ExifTool / FFmpeg / libheif / Noto CJK 并编译固定版本的 libultrahdr，全部测试（`-m:1` 串行、排除 `ToolManifestPackageTests`）并收集覆盖率；**跳过数超过 5 即失败**（允许的 5 条见工作流注释）。**Windows**：构建与测试（未装外部工具，集成用例按设计跳过）。**AOT 与安装包**：`win-x64` 发布，并用同一产物打两个测试版本，静默安装、启动、增量升级、卸载（不联网）。编译与发布均以警告为错误。 |
| [`tools-manifest.yml`](.github/workflows/tools-manifest.yml) | 每周一、手动、PR 改动 `External/Tools/**` | 在 Linux 与 Windows 下载 `tools.json` 的每个包并用 `ToolInstaller` 校验哈希与结构。 |
| [`release.yml`](.github/workflows/release.yml) | 推送版本 tag | 见 5.3。 |

- 测试结果写入 Job Summary（`.github/scripts/test-summary.ps1`），trx、覆盖率与界面截图作为产物上传（截图仅在失败或桌面端改动时上传）。
- 新增按环境跳过的用例：同步调整 `ci.yml` 中的 `-MaxSkipped` 与注释；需要预先准备数据的用例放到专门工作流。
- 官方 Action 用最新大版本，第三方 Action 固定提交 SHA；工作流默认 `contents: read`。

### 5.3 发版

`release.yml` 只由推送的 tag（`v*` 或 `[0-9]*.[0-9]*.*`）触发：校验 tag 去掉 `v` 后等于 `Directory.Build.props` 的 `<Version>`、`CHANGELOG.md` 有对应段落 → 复用 `ci.yml` 全部检查 → Windows 上用 vpk 打包（先下载上一正式版的完整包生成增量包）：Setup、Portable、full / delta nupkg、`releases.win.json`，另附纯解压版 `LivePhotoConvert-<tag>-win-x64.zip` → 全部文件计入 `SHA256SUMS.txt` 并生成构建来源证明 → 以 CHANGELOG 段落（`.github/scripts/get-changelog-section.ps1` 提取到下一个二级标题为止）为说明创建 Release，版本含 `-` 时为预发布。

发版步骤（由维护者决定时机，助手不要自行打 tag 或推送）：

1. 在 `CHANGELOG.md` 顶部新增 `## [x.y.z] - YYYY-MM-DD`，其下用三级标题分组；
2. 同步 `Directory.Build.props` 的 `<Version>`、`<AssemblyVersion>`、`<FileVersion>`；
3. 提交后打带注释的 tag 并推送：`git tag -a vx.y.z -m "LivePhotoConvert x.y.z"`、`git push origin main`、`git push origin vx.y.z`。

发布失败时对原运行 Re-run，不提供手动输入 tag 的入口。已推送的发布 tag 不得改写，热修复递增 patch 版本。「关于」页的版本号来自程序集属性，无需手工维护。
