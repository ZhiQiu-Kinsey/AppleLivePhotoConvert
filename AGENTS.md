# LivePhotoConvert Agent 指南 (AGENTS.md)

面向 AI 编码助手（CodeBuddy / Antigravity / Gemini / Claude / Cursor 等）的代码库协作规范。**修改本仓库前务必先通读本文件。**

> 最后更新：v3.0.2（2026-09-18）—— CLI 已完全移除，当前产品形态为 Avalonia 桌面应用。

---

## 1. 项目概览

`LivePhotoConvert` 是跨平台动态照片互转 + 空间瘦身的 **.NET 10 桌面工具箱**（C# latest，Native AOT 单文件发布）。

| 模块 | 说明 |
| :--- | :--- |
| **实况互转 · 合成** | iPhone 实况对（HEIC/JPG + MOV）→ 单文件安卓动态照片（JPEG + 内嵌 MP4） |
| **实况互转 · 还原** | 安卓动态照片 → 苹果实况对（`.HEIC` + `.MOV`），JPEG 封面自动转码 HEIC，注入配对 UUID |
| **实况互转 · 解包** | 安卓动态照片 → 封面图 + 独立 `.mp4`（无损切片） |
| **空间瘦身** | 图库检查器中的「瘦身」动作：剥离内嵌/配对视频并可选转码 HEIC（默认质量 90），可就地替换或导出，附卷帘对比预览 |
| **依赖引擎** | 检测 / 下载 ExifTool、FFmpeg、heif-enc（内置国内加速镜像） |
| **任务中心** | 运行中任务的进度、暂停与取消；本次会话的历史报告（成败明细、重试失败项、导出 CSV） |
| **偏好设置 / 关于** | 主题、语言、并发数；版本、贡献者、仓库、MIT 协议与引用开源项目 |

**技术栈（当前真实状态，勿参考历史文档中的过期信息）**

- 运行时：.NET 10（`net10.0`），Native AOT 发布，`TrimMode=full`
- UI：**Avalonia 12.1.2**（`Avalonia.Desktop` / `Themes.Fluent` / `Fonts.Inter`）+ CommunityToolkit.Mvvm 8.4.0 + FluentIcons.Avalonia 2.1.339.1
- 核心引擎：`LivePhotoConvert.Core`，零 UI 依赖，第三方包仅 `Magick.NET-Q8-x64` 14.16.0（图像解码/缩略图）与 `SharpCompress` 1.0.0（依赖包 7z 解压）
- 外部工具：ExifTool（元数据/XMP）、FFmpeg（转码/流复制）、heif-enc（HEIC 编码）
- 测试：xunit.v3（`LivePhotoConvert.Core.Tests` 引擎单元测试 + `LivePhotoConvert.Desktop.Tests` 桌面 VM/服务单元测试与 Avalonia Headless 界面冒烟 + `LivePhotoConvert.E2E` 黑盒端到端）
- **已移除**：`LivePhotoConvert.Cli`、Spectre.Console、手写 `CliParser` 及旧命令行入口。不要再引用、恢复或为新功能增加 CLI 分支；可复用能力必须放入 Core，交互入口放入 Desktop。

---

## 2. 目录结构

```
src/LivePhotoConvert.Core/          # 核心引擎（纯托管、AOT 兼容、无 UI 依赖）
  Abstractions/                     # IImageConverter / IVideoConverter
  Media/                            # MediaFileTypes 嗅探、MotionPhotoLayout 视频定位、MotionPhotoXmp 解析与编辑、FastImageHeaderReader
  Metadata/                         # IMetadataService、ExifTool 会话池与批量 JSON 读取、CaptureTime、AppleMakerNote
  Pairing/                          # MediaPair、MediaPairMatcher（按目录 + 主干配对）、PairValidator（纯函数）
  Pipeline/                         # BatchRunner、OutputCommitter 原子落盘、SourceDisposition、TempWorkspace、BatchReport
  Services/                         # MotionPhotoMerger / Splitter / Stripper 及其请求模型
  External/                         # ProcessRunner、FFmpeg / heif-enc / Magick 转换器、ToolLocator；Tools/ 依赖清单（tools.json）、ToolInstaller 校验安装、ToolRegistry 能力探测
  Io/                               # BinaryFile 流式拼接切片、UniquePath、FileHelper / FileTimestamp
  Platform/                         # RecycleBin 等平台相关实现
src/LivePhotoConvert.Desktop/       # Avalonia 12 桌面端
  App.axaml(.cs) / Program.cs       # 组合根（主题与语言先于页面 VM 生效）/ 全局异常钩子
  Assets/                           # Styles.axaml、Strings.zh-CN.axaml、Strings.en-US.axaml
  Collections/                      # BulkObservableCollection（批量替换只发一次 Reset）
  Controls/                         # CompactToolbar / CurtainCompareControl / PhotoCardControl
  Converters/                       # ByteSizeConverter 等 XAML 值转换器
  Features/                         # 按功能组织的视图 + VM
    Shell/                          # ShellWindow（导航、弹窗宿主、Esc）、ShellViewModel
    Library/                        # 图库工作台：LibraryView/VM（画廊）、InspectorView/VM（动作与参数）、JobFactory、StripEstimator
    Tasks/                          # 任务中心：TaskCenter、ConversionRunner、TasksView/VM、TaskReportView/VM、CsvWriter
    Tools/                          # 依赖引擎页
    Settings/                       # 偏好设置与关于
    Dialogs/                        # 各弹窗（DialogViewModel<TResult> + 视图）
  Infrastructure/                   # 与具体页面无关的桌面服务：AppServices（DI 组合根）、Localizer、SettingsStore/DesktopSettings、DialogService、FilePicker、ShellLauncher、Navigator、ThemeService、AppLifetime
  Models/                           # GalleryItem（卡片/行/分组头）、TimelineGroup（等高排版）、AboutCredit、AboutInfo
  Services/                         # AlbumScanner / ThumbnailReader / PlaybackHost / SafetyGuard 等
tests/
  LivePhotoConvert.Core.Tests/      # 引擎单元测试：Media / Metadata / Pairing / Pipeline / Services / Io，Support/ 为内存替身与合成媒体
  LivePhotoConvert.Desktop.Tests/   # 桌面单元测试与 Headless 界面冒烟：目录与产品一致（Features/*、Infrastructure、Services…），Harness/ 为测试宿主
  LivePhotoConvert.E2E/             # 黑盒端到端：真实外壳 + 真实外部工具，按用户操作点击界面，只看磁盘结果（工具缺失时跳过）
docs/                               # 截图、架构文档、英文 README、赞赏码
Directory.Build.props               # 统一版本与语言配置
global.json                         # 固定 SDK 10.0.400（避免误用 11 preview）
```

---

## 3. 核心协议知识（改动业务逻辑前必须理解）

### 3.1 Google Motion Photo (XMP)

- 物理结构：JPEG 前段（可能还跟着 Ultra HDR 增益图）+ 尾部拼接的 MP4。
- 需写 XMP：`GCamera:MotionPhoto*`、`GCamera:MicroVideo*`（`MicroVideoOffset` = 视频字节长）以及 `Container:Directory`（Primary / GainMap / MotionPhoto 项）。
- **读取与编辑一律走 `MotionPhotoXmp`（托管 XDocument），不依赖 ExifTool 标签表**：ExifTool 12.x 不认识 Container 命名空间，13.x 又把组名改成 `XMP-GContainer`，按标签名读写会随版本失效。写入时在原有 XMP 上合并后整包回写（`-xmp<=`），不得丢弃封面原有的其它命名空间。
- 定位视频一律走 `MotionPhotoLayout`：候选偏移处必须是 `ftyp`，否则视为非动态照片；只有 GainMap 没有 MotionPhoto 项的 Ultra HDR 照片不是动态照片。HEIC 动态照片无需 XMP：遍历顶层 box（支持 64 位长度与 size 0），内容以 `ftyp` 开头的 `mpvd` 即为视频。
- `EmbeddedVideo` 区分两个位置：`Offset`/`Length` 是视频数据（切片、播放用），`ImageEnd` 是照片部分的结束位置（剥离、拆分截断用）。视频包在 `mpvd` box 或三星 SEF 数据块中时 `ImageEnd < Offset`，截断必须用 `ImageEnd`，否则会残留容器头。
- 剥离视频时只删除 MotionPhoto 目录项，保留 GainMap 项；带增益图的照片不转码 HEIC（会丢失 HDR）。
- 拼接/拆分由 `BinaryFile.ConcatAsync` / `CopySegmentAsync` 流式完成，**严禁整文件读入堆内存**。

### 3.2 小米澎湃 OS `0x8897`

- 相册识别动态照片除 XMP 外还校验 Exif `34967`（`0x8897`），合成/修复时必须写入，否则小米手机不触发长按播放。

### 3.3 Apple Live Photo UUID 配对

- **图片端**：Apple MakerNotes 的 `ContentIdentifier`（大写 UUID）。ExifTool 无法在没有 Apple MakerNotes 的文件里凭空创建该块，必须用 `AppleMakerNote.BuildTemplateJpeg` 生成模板后 `-tagsFromFile 模板 -MakerNotes` 整块复制，并回读校验。
- **视频端**：QuickTime Keys 写入 `com.apple.quicktime.content.identifier`（同一 UUID），并同步 `creationdate`/`make`/`model`/`software`/`GPSCoordinates`（格式 `纬度, 经度, 海拔`）。
- ⚠️ QuickTime 时间标签（mvhd/mdhd）按 UTC 存储。ExifTool 会话通过 `-common_args -api QuickTimeUTC=1` 对所有命令生效；写入时传入**带偏移**的时间（来自 DateTimeOriginal + OffsetTimeOriginal），由 ExifTool 换算为 UTC。
- 还原模式用 `Guid.NewGuid().ToString().ToUpperInvariant()` 生成配对 UUID 注入两端。
- 实测结论：`Keys:StillImageTime` 不是 ExifTool 可写标签、Apple 原片亦无此键，**不要写入**。

### 3.4 配对与校验

- 优先 `ContentIdentifier` 精确匹配；否则「同一目录 + 文件名主干」+ `PairValidator`（时间差 ≤3s、时长 ≤30s）。两侧都有时区偏移时按绝对时刻比较，否则按当地时间比较。
- 格式优选：`HEIC > JPG > PNG`，`MOV > MP4`。
- 时间差超阈值 → 桌面端弹「人工裁决」弹窗，用户确认后进入 `ForceAcceptedPairs` 白名单，合并阶段 O(1) 命中并跳过校验。

---

## 4. 编码守则

### 4.1 Native AOT（最高优先级）

- 禁止反射 / `Reflection.Emit` / 未标注 `[DynamicallyAccessedMembers]` 的泛型；JSON 用 `System.Text.Json` Source Generator；新依赖必须 AOT 兼容。
- 读取程序集元信息只可用 `GetCustomAttribute<T>()` 这类静态已知类型的 API（例如“关于”页读 `AssemblyInformationalVersionAttribute`）。

### 4.2 零分配与性能

- 魔数嗅探用 `ReadOnlySpan<byte>` + UTF-8 字面量（`"heic"u8`）+ `stackalloc`。
- 流式复制用 `ArrayPool<byte>.Shared.Rent()` 并在 `finally` Return。
- 只读集合用 `FrozenSet` / `FrozenDictionary`。

### 4.3 原子安全与非破坏

- 中间文件放在 `TempWorkspace`（`Path.GetTempPath()/LivePhotoConvert/temp-{guid}/`），启动时清理 24h 前的孤儿目录；退出时只用 `TempWorkspace.DeleteOwned()` 删除本进程创建的目录（多个实例共用根目录）。
- **最终输出必须经 `OutputCommitter`**：先写到目标目录内的 `~lpc-*` 暂存文件，校验通过后同卷重命名落盘；同一批次的文件名由它统一分配，并发任务不会写到同一路径，**任何冲突策略下都不会覆盖本批次的源文件或本批次已写出的文件**。成对输出（HEIC + MOV）用 `CommitGroup` 保证同一序号，失败整组回滚；覆盖模式下成组落盘前先把已有目标改名为同目录 `~lpc-*` 备份，失败时新文件退回暂存、备份还原，成功后删除备份。受保护路径与已认领路径一律 `Path.GetFullPath` + 忽略大小写比较（大小写不敏感卷上只差大小写即同一文件）。
- 暂存文件名带创建时间（`~lpc-t{ticks}-{guid}`），过期残留按文件名中的时间判断；源文件时间戳通过 `Commit`/`CommitGroup`/`ReplaceSource` 的 `timestamp` 参数在落盘后写到最终文件，不要对暂存文件调用 `FileTimestamp.ApplyTo`。
- 就地替换走 `OutputCommitter.ReplaceSource`：扩展名不变时 `File.Replace` + `.livephoto_backup`，成功后**立即删除备份**；扩展名改变时先落盘新文件（不覆盖已有文件）再删除原文件，删除失败则撤销新文件。
- 源文件处置（移动/回收站/删除）只在输出落盘并校验通过后由 `SourceDisposition` 执行；瘦身删除 Apple 配对视频前必须通过 `PairValidator`，且只走回收站。
- 外部工具只写流程自己生成的中间或暂存文件，从不直接修改用户原文件。

### 4.3.1 外部进程

- 一律通过 `ProcessRunner`（参数逐项 `ArgumentList`、标准输入立即关闭、默认 10 分钟超时、取消即结束进程树）；FFmpeg 固定带 `-nostdin`。
- ExifTool 常驻会话经标准输入逐行传参，**含换行符的参数必须拒绝**；输出按块读取，以「缓冲末尾为 `{readyN}` + 换行」判定结束（`-b` 输出没有尾随换行，不能逐行匹配标记）；读取一律批量 `-j -n -G1 -a` 后由 `ExifToolJson` 解析，禁止逐标签往返。
- 工具可用性探测结果由 `ToolLocator` 按文件路径、大小与修改时间缓存，不要在热路径上重复启动 `-version`。

### 4.4 Avalonia / MVVM 规范

- 所有视图必须 `x:CompileBindings="True"` + 显式 `x:DataType`；绑定一律 `CompiledBinding`，界面文案一律 `DynamicResource`。
- 视图模型继承 `ViewModelBase`，使用 CommunityToolkit `[ObservableProperty]` / `[RelayCommand]`。
- 服务与页面 VM 由 `Infrastructure/AppServices` 显式工厂注册（禁止反射扫描），通过构造函数注入；VM 之间不用 `Action` 回调互相接线，不新增静态单例。
- 弹窗继承 `DialogViewModel<TResult>`，调用方 `await dialogs.ShowAsync(...)` 取结果；Esc 与遮罩点击走 `CancelCommand`，资源在 `OnClosed` 中释放。
- 设置只经 `SettingsStore.Update(...)` 修改（防抖原子写入）；文件选择与打开目录/链接只经 `IFilePicker` / `IShellLauncher`，不直接 `Process.Start`。
- **零 Emoji 策略**：UI 只用 FluentIcons 矢量图标（`ic:SymbolIcon`），不得用 emoji 字符充当图标。
- 新增按钮/卡片样式统一写入 `Assets/Styles.axaml`（如 `Button.credit-row`），不要在视图里堆内联样式。
- 复用型列表项模板放 `UserControl.Resources` 的 `DataTemplate`，跨级取命令用 `((vm:XxxViewModel)$parent[ItemsControl].DataContext).Command` 语法。
- 路径中间可能为空的绑定写成 `Current?.Title`（值类型再加 `TargetNullValue`），否则会产生绑定错误日志。
- **界面改动补 Headless 冒烟测试**（`tests/LivePhotoConvert.Desktop.Tests`，`[AvaloniaFact]` + `Harness/ShellSession`）：新页面/弹窗/参数区要能在真实外壳中实例化，绑定错误日志为空，中英文切换后不残留另一语言或资源键名；截图输出到测试输出目录的 `screenshots/`（CI 作为产物上传）供人工审查。

### 4.5 响应式相册与预览内存安全

- 「等高自适应」由 `LibraryViewModel` / `TimelineGroup.BuildRows` 依据可用视口宽度、原图比例与目标行高动态分行；完整行需铺满，末行保持自然宽度，禁止退化为固定列数或拉伸图片。
- 卡片尺寸必须使用实际解码宽高与方向信息；窗口、侧栏或分组状态变化后必须触发重排，不得仅保存显示模式而不更新布局。
- 原始 `Bitmap` 是非托管像素内存，**严禁无界缓存**。当前预算：960px 缩略图最多 24 张；悬浮预览最多 1 组、60 帧、720p；QuickLook 最多 90 帧、1080p；首屏仅预热 1 段视频。调整数值必须补内存压力验证。
- 缩略图解码或 Skia 像素分配失败必须降级为占位图，不得让异常穿透 UI 线程导致进程退出。
- 悬浮与 QuickLook 解码保留源时序：FFmpeg 使用 `-fps_mode passthrough`、BMP/BGR24 帧管线和 Lanczos 缩放；不要使用 `-hwaccel auto`，进程参数用 `ProcessStartInfo.ArgumentList` 逐项传入。

### 4.6 本地化（两处必须同步，缺一不可）

两份 XAML 字符串字典是唯一数据源。新增/修改界面文案时，**必须同时更新两处**，键集合保持一致（`StringResourceTests` 会校验）：

1. `Assets/Strings.zh-CN.axaml`
2. `Assets/Strings.en-US.axaml`

- 视图用 `{DynamicResource Key}`；代码通过 `Infrastructure/ILocalizer` 取文案（`localizer["Key"]`、`localizer.Format("KeyFormat", ...)`），不要在 .cs 里写界面可见的中文或英文字面量。
- 格式串以 `Format` 结尾，中英两版占位符编号必须一致；日期格式也放进资源（如 `GroupTitleMonthFormat`）。
- XAML 里 `&`、`<`、`>` 需转义；带前导/尾随空格的值加 `xml:space="preserve"`。
- 缺失键在 Debug 下会触发 `Debug.Fail`（测试进程会直接终止），发布版返回键名。

### 4.7 语言风格

- 主构造函数注入依赖；集合表达式 `[]` / `[..x]`；用 `System.Threading.Lock`；`<Nullable>enable</Nullable>` 零警告。
- 注释用中文，与 public API 语义保持一致。

### 4.8 退出码（Core 层契约，供外部宿主复用）

- `0` Success / `1` Failure / `2` InvalidArguments / `3` Canceled / `4` PartialFailure。

---

## 5. 本地构建

```bash
dotnet build LivePhotoConvert.slnx                  # 编译（要求 0 警告 0 错误）
dotnet test  LivePhotoConvert.slnx                  # 全量测试（Core.Tests + Desktop.Tests + E2E，Headless 无需显示器）
dotnet run --project src/LivePhotoConvert.Desktop/LivePhotoConvert.Desktop.csproj   # 启动桌面端
dotnet publish src/LivePhotoConvert.Desktop/LivePhotoConvert.Desktop.csproj -r win-x64 -c Release -o dist/aot   # Native AOT，要求 0 裁剪警告
```

> **终端环境坑**：若使用 Git Bash 等 POSIX shell，`HOME` 会是 MSYS 路径（`/c/Users/...`）、`APPDATA` 可能为空，NuGet 会抛 `Value cannot be null. (Parameter 'path1')`。构建前修正：
> ```bash
> export HOME='C:\Users\<用户名>'
> export APPDATA='C:\Users\<用户名>\AppData\Roaming'
> ```
> `global.json` 已固定 SDK `10.0.400`（机器同时装有 11 preview），不要删除。

---

## 6. 改动工作流

1. 定位 Core（引擎）还是 Desktop（交互）；**Core 不得引入 UI/Console 依赖**。
2. 遵循 AOT / 零分配 / 原子 / MVVM 规范，保持注释与 public API 语义。
3. 新增界面文案同步两处本地化资源（见 4.6）。
4. Core 改动必须补单元测试（`tests/LivePhotoConvert.Core.Tests` 按 Media / Metadata / Pairing / Pipeline / Services 分目录；真实外部工具的集成测试在工具缺失时自动跳过）；Desktop 改动补 `tests/LivePhotoConvert.Desktop.Tests`（目录与命名空间对应产品的 `Features/*`、`Infrastructure` 等，界面改动含 Headless 冒烟）；跨越「选相册 → 执行 → 落盘」的完整流程改动补充/更新 `tests/LivePhotoConvert.E2E` 用例。测试必须验证产品代码，不要在测试工程里重新实现一份被测逻辑。
5. `dotnet build` 0 警告 0 报错，`dotnet test` 全绿。
6. 交付总结：改动内容、设计决策、验证结果。

---

## 7. 版本发布（自动发布依赖 Git Tag）

GitHub Actions 的 [`release.yml`](.github/workflows/release.yml) 监听 **git tag**（匹配 `v*` 或 `[0-9]+.*`）自动执行：跑测试 → 发布 Native AOT → 打包 `LivePhotoConvert-<tag>-win-x64.zip` → 创建 GitHub Release。

**版本更新时必须依次完成（缺一不会触发发版）：**

1. **写更新日志**：在 `CHANGELOG.md` 顶部新增 `## [x.y.z] - YYYY-MM-DD` 条目；
2. **升版本号**：同步 `Directory.Build.props` 的 `<Version>`（`<AssemblyVersion>` / `<FileVersion>` 保持一致）；
3. **打 tag 并推送**：
   ```bash
   git tag -a v3.0.1 -m "LivePhotoConvert 3.0.1"
   git push origin main
   git push origin v3.0.1
   ```

tag 名与 `<Version>` 保持一致并带 `v` 前缀（如 `v3.0.1`）；Release 标题与 ZIP 名均取自该 tag。「关于」页展示的版本号由 `AssemblyInformationalVersionAttribute` 自动读取，无需手工维护。已推送的发布 tag 不得改写；热修复必须递增 patch 版本。

---

## 8. 当前已知待办

- Core 与 Desktop 的重复逻辑收敛、接口一致性、边界校验与 AOT 裁剪陷阱自查；
- 扩充万张级相册的长时间滚动、悬浮播放、QuickLook 切换与窗口缩放内存压力验证；
- 继续补充异常路径、中文/空格/超长路径、并发与取消、EXIF 缺失文件与损坏文件覆盖；
- 当前自动发布仅产出 `win-x64` 包，其他平台需在完成真机 UI 与外部工具链验证后再开放发布。
