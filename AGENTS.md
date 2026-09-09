# LivePhotoConvert Agent 指南 (AGENTS.md)

面向 AI 编码助手（CodeBuddy / Antigravity / Gemini / Claude / Cursor 等）的代码库协作规范。**修改本仓库前务必先通读本文件。**

> 最后更新：v2.6.0（2026-09）—— 项目已由 CLI 工具全面转型为 Avalonia 桌面应用。

---

## 1. 项目概览

`LivePhotoConvert` 是跨平台动态照片互转 + 空间瘦身的 **.NET 10 桌面工具箱**（C# latest，Native AOT 单文件发布）。

| 模块 | 说明 |
| :--- | :--- |
| **实况互转 · 合成** | iPhone 实况对（HEIC/JPG + MOV）→ 单文件安卓动态照片（JPEG + 内嵌 MP4） |
| **实况互转 · 还原** | 安卓动态照片 → 苹果实况对（`.HEIC` + `.MOV`），JPEG 封面自动转码 HEIC，注入配对 UUID |
| **实况互转 · 解包** | 安卓动态照片 → 封面图 + 独立 `.mp4`（无损切片） |
| **空间瘦身** | 剥离内嵌视频并可选转码 HEIC（默认质量 90），释放 60%~96% 空间 |
| **依赖引擎** | 检测 / 下载 ExifTool、FFmpeg、heif-enc（内置国内加速镜像） |
| **偏好设置 / 关于** | 主题、语言、并发数；版本、贡献者、仓库、MIT 协议与引用开源项目 |

**技术栈（当前真实状态，勿参考历史文档中的过期信息）**

- 运行时：.NET 10（`net10.0`），Native AOT 发布，`TrimMode=full`
- UI：**Avalonia 12.1.2**（`Avalonia.Desktop` / `Themes.Fluent` / `Fonts.Inter`）+ CommunityToolkit.Mvvm 8.4.0 + FluentIcons.Avalonia 2.1.339.1
- 核心引擎：`LivePhotoConvert.Core`，零 UI 依赖，除 `Magick.NET-Q8-x64` 14.16.0（图像解码/缩略图）外无第三方包
- 外部工具：ExifTool（元数据/XMP）、FFmpeg（转码/流复制）、heif-enc（HEIC 编码）
- 测试：xunit.v3（`LivePhotoConvert.Core.Tests` 单元测试 + `LivePhotoConvert.E2E` 黑盒端到端）
- **已移除**：`LivePhotoConvert.Cli`、Spectre.Console、手写 `CliParser`。不要再引用或恢复这些组件。

---

## 2. 目录结构

```
src/LivePhotoConvert.Core/          # 核心引擎（纯托管、AOT 兼容、无 UI 依赖）
  Abstractions/                     # 接口（IExifTool、IImageConverter、IVideoConverter…）
  External/                         # ExifTool / Ffmpeg / HeifEnc / ToolDownloader
  Io/                               # BinaryFile 流式拼接切片、UniquePath 原子占位、FileHelper/FileTimestamp
  Matching/                         # MediaFileTypes 零分配嗅探、MediaPairMatcher 配对
  Models/                           # MergeModels / SplitModels / StripModels 等契约
  Services/                         # Merger / Splitter / Stripper / PairValidator
  Platform/                         # RecycleBin 等平台相关实现
src/LivePhotoConvert.Desktop/       # Avalonia 12 桌面端
  Assets/                           # Styles.axaml、Strings.zh-CN.axaml、Strings.en-US.axaml
  Controls/                         # CompactToolbar / CurtainCompareControl / PhotoCardControl
  Converters/                       # ByteSizeConverter 等 XAML 值转换器
  Models/                           # DesktopSettings、GalleryItem、AboutCredit、AboutInfo
  Services/                         # AlbumScanner / ThumbnailReader / SettingsService / LocalizationService 等
  ViewModels/                       # Main / Convert / Strip / Tools / Report + Dialogs/
  Views/                            # 对应视图与 Dialogs/
tests/                              # Core.Tests（单元）与 E2E（黑盒）
docs/                               # 截图、架构文档、英文 README、赞赏码
Directory.Build.props               # 统一版本与语言配置
global.json                         # 固定 SDK 10.0.400（避免误用 11 preview）
```

---

## 3. 核心协议知识（改动业务逻辑前必须理解）

### 3.1 Google Motion Photo (XMP)

- 物理结构：JPEG 前段 + 尾部二进制拼接的 MP4。
- 需写 XMP：`GCamera:MicroVideo=1`、`GCamera:MicroVideoOffset=<视频字节长>`、`GCamera:MicroVideoPresentationTimestampUs`。
- 拼接/拆分由 `BinaryFile.ConcatAsync` / `CopySegmentAsync` 流式完成，**严禁整文件读入堆内存**。

### 3.2 小米澎湃 OS `0x8897`

- 相册识别动态照片除 XMP 外还校验 Exif `34967`（`0x8897`），合成/修复时必须写入，否则小米手机不触发长按播放。

### 3.3 Apple Live Photo UUID 配对

- **图片端**：MakerNotes/Exif 写入 `ContentIdentifier`（大写 UUID）。
- **视频端**：QuickTime Keys 写入 `com.apple.quicktime.content.identifier`（同一 UUID），并同步 `creationdate`/`make`/`model`/`software`/`location.ISO6709`。
- ⚠️ **QuickTime 时间标签（mvhd/mdhd 的 CreateDate/MediaCreateDate/TrackCreateDate）按 UTC 存储**；用 ExifTool 写入时必须带 `-api QuickTimeUTC=1`，否则本地时间被当成 UTC 写入，产生时区偏移。
- 还原模式用 `Guid.NewGuid().ToString().ToUpperInvariant()` 生成配对 UUID 注入两端。
- 实测结论：`Keys:StillImageTime` 不是 ExifTool 可写标签、Apple 原片亦无此键，**不要写入**。

### 3.4 配对与校验

- 优先 `ContentIdentifier` 精确匹配；否则文件名主干 + `PairValidator`（时间差 ≤3s、时长 ≤30s）。
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

- 临时目录隔离：`Path.GetTempPath()/LivePhotoConvert/temp-{guid}/`，启动时清理 24h 前的孤儿目录。
- 并发写用 `UniquePath.ReserveAtomic` / `ReservePairAtomic` 原子占位。
- `try/finally` 清理半成品；完成后校验文件大小/格式。
- 默认不改源文件；仅当用户显式选择（如就地瘦身/物理删除）且校验成功才处理，并保留 `.livephoto_backup` 暂存。

### 4.4 Avalonia / MVVM 规范

- 所有视图必须 `x:CompileBindings="True"` + 显式 `x:DataType`；绑定一律 `CompiledBinding`，界面文案一律 `DynamicResource`。
- 视图模型继承 `ViewModelBase`，使用 CommunityToolkit `[ObservableProperty]` / `[RelayCommand]`。
- **零 Emoji 策略**：UI 只用 FluentIcons 矢量图标（`ic:SymbolIcon`），不得用 emoji 字符充当图标。
- 新增按钮/卡片样式统一写入 `Assets/Styles.axaml`（如 `Button.credit-row`），不要在视图里堆内联样式。
- 复用型列表项模板放 `UserControl.Resources` 的 `DataTemplate`，跨级取命令用 `((vm:XxxViewModel)$parent[ItemsControl].DataContext).Command` 语法。

### 4.5 本地化（三处必须同步，缺一不可）

新增/修改界面文案时，**必须同时更新三处**：

1. `Assets/Strings.zh-CN.axaml`
2. `Assets/Strings.en-US.axaml`
3. `Services/LocalizationService.cs` 中的 `ZhStrings` 与 `EnStrings`（`SetLanguage` 会用字典覆盖 `Application.Resources`，缺失会退化成显示 key）

XAML 里 `&` 需写成 `&amp;`；C# 字典里直接写 `&` 即可。

### 4.6 语言风格

- 主构造函数注入依赖；集合表达式 `[]` / `[..x]`；用 `System.Threading.Lock`；`<Nullable>enable</Nullable>` 零警告。
- 注释用中文，与 public API 语义保持一致。

### 4.7 退出码（Core 层契约，供外部宿主复用）

- `0` Success / `1` Failure / `2` InvalidArguments / `3` Canceled / `4` PartialFailure。

---

## 5. 本地构建

```bash
dotnet build LivePhotoConvert.slnx                  # 编译（要求 0 警告 0 错误）
dotnet test  LivePhotoConvert.slnx                  # 全量测试（Core.Tests + E2E）
dotnet run --project src/LivePhotoConvert.Desktop/LivePhotoConvert.Desktop.csproj   # 启动桌面端
dotnet publish src/LivePhotoConvert.Desktop/LivePhotoConvert.Desktop.csproj /p:PublishProfile=win-x64-aot -o dist/aot
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
3. 新增界面文案同步三处本地化资源（见 4.5）。
4. Core 改动必须补单元测试；涉及 UI 流程的改动补充/更新 E2E 用例。
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
   git tag v2.6.0
   git push origin v2.6.0
   ```

tag 名与 `<Version>` 保持一致并带 `v` 前缀（如 `v2.6.0`）；Release 标题与 ZIP 名均取自该 tag。「关于」页展示的版本号由 `AssemblyInformationalVersionAttribute` 自动读取，无需手工维护。

---

## 8. 当前已知待办

- Core 与 Desktop 的重复逻辑收敛、接口一致性、边界校验与 AOT 裁剪陷阱自查；
- 待补充的测试盲区：异常路径、中文/空格/超长路径、并发与取消、EXIF 缺失文件、损坏文件；
- `docs/README.en.md` 英文文档需随 README 同步更新。
