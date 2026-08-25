# LivePhotoConvert Agent 指南 (AGENTS.md)

面向 AI 编码助手（Antigravity / Gemini / Claude / Cursor 等）的代码库协作规范。修改本仓库前务必先通读本文件。

---

## 1. 项目概览

`LivePhotoConvert` 是跨平台动态照片互转 + 空间瘦身的 .NET 10 开源工具箱（C# 13，Native AOT 单文件）。

| 命令 | 作用 |
| --- | --- |
| `merge` | iPhone 实况对（HEIC/JPG + MOV）→ 单文件安卓动态照片（JPEG + 内嵌 MP4） |
| `split -f apple` | 安卓动态照片 → 苹果实况对（`.HEIC` + `.MOV`），JPEG 封面自动转码 HEIC，注入配对 UUID |
| `split -f android` | 安卓动态照片 → 封面图 + 独立 `.mp4`（无损切片） |
| `strip` | 剥离内嵌视频并可选转码 HEIC（默认质量 90），释放 60%~96% 空间 |
| `tools` | 检测/下载 ExifTool、FFmpeg、heif-enc |

- **外部工具**：ExifTool（元数据/XMP）、FFmpeg（视频转码/流复制）、heif-enc（HEIC 编码）；图像解码用 Magick.NET-Q8-x64；测试用 xunit.v3。

---

## 2. 目录结构

```
src/LivePhotoConvert.Core/        # 核心引擎（纯托管、AOT 兼容、无 UI 依赖）
  Abstractions/                   # 接口（IExifTool、IImageConverter、IVideoConverter…）
  External/                       # ExifTool / Ffmpeg / HeifEnc / ToolDownloader
  Io/                             # BinaryFile 流式拼接/切片、UniquePath 原子占位、FileHelper/FileTimestamp
  Matching/                       # MediaFileTypes 零分配嗅探、MediaPairMatcher 配对
  Models/                         # MergeModels / SplitModels / StripModels 等契约
  Services/                       # Merger / Splitter / Stripper / PairValidator
src/LivePhotoConvert.Cli/         # 控制台（CommandLine 命令、Ui 渲染、Program 入口）
tests/LivePhotoConvert.Core.Tests/# xunit.v3 单元测试
docs/  Directory.Build.props  LivePhotoConvert.slnx  CHANGELOG.md  AGENTS.md
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
- `split -f apple` 用 `Guid.NewGuid().ToString().ToUpperInvariant()` 生成配对 UUID 注入两端。
- 实测结论：`Keys:StillImageTime` 不是 ExifTool 可写标签、Apple 原片亦无此键，**不要写入**。

### 3.4 配对与校验
- 优先 `ContentIdentifier` 精确匹配；否则文件名主干 + `PairValidator`（时间差 ≤3s、时长 ≤30s）。
- 格式优选：`HEIC > JPG > PNG`，`MOV > MP4`。

---

## 4. 编码守则

### 4.1 Native AOT（最高优先级）
- 禁止反射 / `Reflection.Emit` / 未标注 `[DynamicallyAccessedMembers]` 的泛型；JSON 用 `System.Text.Json` Source Generator；新依赖必须 AOT 兼容。

### 4.2 零分配与性能
- 魔数嗅探用 `ReadOnlySpan<byte>` + UTF-8 字面量（`"heic"u8`）+ `stackalloc`。
- 流式复制用 `ArrayPool<byte>.Shared.Rent()` 并在 `finally` Return。
- 只读集合用 `FrozenSet` / `FrozenDictionary`。

### 4.3 原子安全与非破坏
- 临时目录隔离：`Path.GetTempPath()/LivePhotoConvert/temp-{guid}/`。
- 并发写用 `UniquePath.ReserveAtomic` / `ReservePairAtomic` 原子占位。
- `try/finally` 清理半成品；完成后校验文件大小/格式。
- 默认不改源文件；仅当 `--source-action` 显式指定且校验成功才处理。

### 4.4 语言风格
- 主构造函数注入依赖；集合表达式 `[]` / `[..x]`；用 `System.Threading.Lock`；`<Nullable>enable</Nullable>` 零警告。

### 4.5 退出码
- `0` Success / `1` Failure / `2` InvalidArguments / `3` Canceled / `4` PartialFailure。

---

## 5. 常用命令

```powershell
dotnet build LivePhotoConvert.slnx            # 编译
dotnet test LivePhotoConvert.slnx             # 全量测试
dotnet run --project src/LivePhotoConvert.Cli/LivePhotoConvert.Cli.csproj -- split -i "D:\MotionPhotos" -o "D:\AppleLive" -f apple -y
dotnet publish src/LivePhotoConvert.Cli/LivePhotoConvert.Cli.csproj /p:PublishProfile=win-x64-aot -o dist/aot   # AOT 发布
```

---

## 6. 改动工作流

1. 定位 Core（引擎）还是 Cli（交互）；Core 不得引入 UI/Console 依赖。
2. 遵循 AOT / 零分配 / 原子规范，保持注释与 public API 语义。
3. Core 改动必须补单元测试。
4. `dotnet build` 0 警告 0 报错，`dotnet test` 全绿。
5. 交付总结：改动内容、设计决策、验证结果。

---

## 7. 版本发布（自动发布依赖 Git Tag）

GitHub Actions 的 [`release.yml`](.github/workflows/release.yml) 监听 **git tag**（匹配 `v*` 或 `[0-9]+.*`）自动执行：跑测试 → 发布 Native AOT → 打包 `LivePhotoConvert-<tag>-win-x64.zip` → 创建 GitHub Release。

**版本更新时必须依次完成（缺一不会触发发版）：**
1. **写更新日志**：在 `CHANGELOG.md` 顶部新增 `## [x.y.z] - YYYY-MM-DD` 条目；
2. **升版本号**：同步 `Directory.Build.props` 的 `<Version>`（`<AssemblyVersion>` / `<FileVersion>` 保持一致）；
3. **打 tag 并推送**（触发自动发布）：
   ```powershell
   git tag v2.6.0
   git push origin v2.6.0
   ```
   tag 名与 `<Version>` 保持一致并带 `v` 前缀（如 `v2.6.0`）；Release 标题与 ZIP 名均取自该 tag。

> 也可在 GitHub 页面手动 `workflow_dispatch` 触发并填 `tag_name`，但日常发布请统一走 tag push。
