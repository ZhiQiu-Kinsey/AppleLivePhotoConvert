# LivePhotoConvert (动态照片工具箱)

<p align="center">
  <img src="src/LivePhotoConvert.Cli/LivePhotoConvert.ico" width="84" height="84" alt="LivePhotoConvert Logo" />
</p>

<p align="center">
  <strong>⚡ 在苹果实况照片 (Apple Live Photo) 与安卓动态照片 (Motion Photo) 之间实现双向高保真无损转换与空间瘦身优化</strong>
</p>

<p align="center">
  <a href="https://dotnet.microsoft.com/download"><img src="https://img.shields.io/badge/.NET-10.0-512BD4?style=flat-square&logo=dotnet" alt=".NET 10" /></a>
  <a href="https://learn.microsoft.com/dotnet/csharp/"><img src="https://img.shields.io/badge/C%23-13.0-239120?style=flat-square&logo=csharp" alt="C# 13" /></a>
  <img src="https://img.shields.io/badge/Platform-Windows%20x64-0078D6?style=flat-square&logo=windows" alt="Platform" />
  <img src="https://img.shields.io/badge/Native%20AOT-Supported-success?style=flat-square" alt="Native AOT" />
  <a href="https://github.com/ZhiQiu-Kinsey/AppleLivePhotoConvert/actions/workflows/ci.yml"><img src="https://img.shields.io/badge/Tests-108%20Passed-success?style=flat-square&logo=githubactions&logoColor=white" alt="Tests" /></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-MIT-blue.svg?style=flat-square" alt="License" /></a>
</p>

<p align="center">
  <a href="README.md"><b>简体中文</b></a> • <a href="docs/README.en.md"><b>English</b></a>
</p>

---

## 📖 目录

- [🔄 核心特性](#-核心特性)
- [📱 转换场景与兼容性矩阵](#-转换场景与兼容性矩阵)
- [📸 运行界面预览](#-运行界面预览)
- [🚀 快速上手指南](#-快速上手指南)
  - [1. 下载程序](#1-下载程序)
  - [2. 从 iPhone 导出实况原片](#2-从-iphone-导出实况原片)
  - [3. 一键极速运行](#3-一键极速运行)
- [💻 命令行完整手册 (CLI)](#-命令行完整手册-cli)
  - [常用命令速查](#常用命令速查)
  - [子命令详解](#子命令详解)
  - [全量参数选项表](#全量参数选项表)
  - [退出码定义](#退出码定义)
- [🔬 核心技术与底层逆向](#-核心技术与底层逆向)
  - [1. 安卓动态照片存储机制 (GCamera XMP)](#1-安卓动态照片存储机制-gcamera-xmp)
  - [2. 小米澎湃 OS `0x8897` 专属标签逆向解构](#2-小米澎湃-os-0x8897-专属标签逆向解构)
  - [3. Apple Live Photo UUID 双向配对机制](#3-apple-live-photo-uuid-双向配对机制)
  - [4. 极致性能与原子安全设计](#4-极致性能与原子安全设计)
- [❓ 常见问题与排坑指南 (FAQ)](#-常见问题与排坑指南-faq)
- [🛠️ 项目架构与本地构建](#️-项目架构与本地构建)
- [💖 致谢与开源项目引用](#-致谢与开源项目引用)
- [📄 开源许可证](#-开源许可证)

---

## 🔄 核心特性

- 🔄 **跨生态双向转换**：
  - **合成 (`merge`)**：将 iPhone 实况照片（`HEIC/JPG` + `MOV`）合成为单文件安卓动态照片（`.jpg`），支持小米相册、Google 相册与 Windows 11 照片应用播放。
  - **还原 (`split -f apple`)**：将安卓动态照片拆分并注入配对 UUID，导入 iPhone/Mac 相册可直接长按播放。
  - **解包 (`split -f android`)**：原生提取封面图片与独立 MP4 微视频。
- 🗜️ **瘦身优化 (`strip`)**：剥离内嵌微视频并可选转码为 HEIC 格式，释放 60%~96% 存储空间，100% 保持原始拍摄时间戳与 EXIF 元数据。
- 🎯 **智能配对与自拍矫正**：优先依据 ContentIdentifier UUID 精确配对，避免同名错位；自动修正前置自拍视频镜像翻转。
- ⚡ **无损流复制与极速性能**：优先采用视频流复制（无重编码损失）；基于 .NET 10 Native AOT 纯原生编译，毫秒级冷启动。
- 📥 **依赖全自动下载**：内置国内加速镜像，首次运行自动下载配置 ExifTool、FFmpeg 与 heif-enc。
- 🖥️ **双模交互**：提供带 Windows 原生文件选择器的彩色终端向导，亦支持完整的 CLI 命令行参数与批量脚本调用。

---

## 📱 转换场景与兼容性矩阵

| 模式 / 命令 | 输入源文件 | 输出目标文件 | 兼容平台 / 播放支持 | 核心应用场景 |
| :--- | :--- | :--- | :--- | :--- |
| **合成 (`merge`)** | iPhone 导出片 (`HEIC/JPG` + `MOV`) | 单文件动态照片 (`MVIMG_*.jpg`) | 小米澎湃 OS / MIUI 相册<br>Google Photos<br>Windows 11 照片<br>三星相册 | 苹果手机换机至安卓，或需在 PC/安卓上查看动态照片 |
| **苹果还原 (`split -f apple`)** | 安卓动态照片 (`.jpg/.heic`) | 苹果实况对 (`.jpg/.heic` + `.mov`) | iPhone 照片 App (iOS)<br>Mac 照片 App (macOS)<br>iCloud Web | 安卓换机至 iPhone，长按即可恢复实况动态效果 |
| **安卓解包 (`split -f android`)** | 安卓动态照片 (`.jpg/.heic`) | 静态图片 + 视频 (`.jpg` + `.mp4`) | 全平台播放器、剪辑软件 (PR/剪映) | 提取动态照片中的微视频素材进行视频剪辑或保存封面 |
| **瘦身优化 (`strip`)** | 动态照片 / JPEG 图片 | 高画质 HEIC (`.heic`) 或纯 JPG | 全平台支持 HEIC 解码的系统与设备 | 手机相册空间告急，批量释放 60%~96% 存储空间 |

---

## 📸 运行界面预览

<p align="center">
  <img src="docs/preview.png" alt="主程序交互界面" width="850" />
</p>

---

## 🚀 快速上手指南

### 1. 下载程序
前往 👉 [Releases 页面](https://github.com/ZhiQiu-Kinsey/AppleLivePhotoConvert/releases) 下载最新的 `LivePhotoConvert-win-x64.zip` 绿色压缩包并解压至任意目录。

### 2. 从 iPhone 导出实况原片
要将 iPhone 实况照片合成为安卓动态照片，请先导出**未修改的原片**：
1. 打开 iPhone【照片】App，多选需要导出的实况照片；
2. 点击左下角【分享】图标 $\rightarrow$ 上滑选择【**导出未修改的原片**】；
3. 选择保存到【文件】或通过数据线 / AirDrop / iCloud / 第三方助手导出到电脑；
4. 导出后每个实况照片将对应两个同名文件（例如 `IMG_1024.HEIC` 与 `IMG_1024.MOV`）。

### 3. 一键极速运行

直接双击运行 `LivePhotoConvert.exe`：
- **首次运行**：程序会自动检测 `ExifTool`、`FFmpeg` 和 `heif-enc`，若缺失将提示通过高速镜像一键自动下载并解压配置。
- **选择功能**：
  - 按 `1`【合成动态照片】：按弹窗提示选择照片所在输入目录与输出目录；
  - 按 `2`【拆分动态照片】：选择输出为通用安卓格式或苹果实况格式；
  - 按 `3`【瘦身优化】：批量剥离视频并转换为高画质 HEIC 释放存储空间；
  - 按 `4`【管理外部工具】：检测或更新依赖组件。

---

## 💻 命令行完整手册 (CLI)

除了交互式菜单外，`LivePhotoConvert` 提供了完善的 CLI 命令行接口，便于与脚本、任务计划程序或第三方工作流集成。

### 常用命令速查

```bash
# 1. 基础合成：将目录中的照片与视频合并为动态照片
LivePhotoConvert merge -i "D:\Photos\iPhoneExport" -o "D:\Photos\MotionPhotos"

# 2. 合成后自动将原始文件移至已处理目录，跳过确认提示
LivePhotoConvert merge -i "D:\Photos\iPhoneExport" -o "D:\Photos\MotionPhotos" -a move -y

# 3. 拆分为苹果实况照片 (生成配对 MOV 与 ContentIdentifier UUID)
LivePhotoConvert split -i "D:\Photos\MotionPhotos" -o "D:\Photos\AppleLive" -f apple

# 4. 拆分为通用安卓格式 (提取封面 .jpg 与内嵌视频 .mp4)
LivePhotoConvert split -i "D:\Photos\MotionPhotos" -o "D:\Photos\Extracted" -f android

# 5. 瘦身优化：就地剥离视频并转换为高画质 HEIC (释放 60%~96% 空间，保留拍摄时间戳)
LivePhotoConvert strip -i "D:\Photos\Camera"

# 6. 瘦身优化输出到新目录（设定 HEIC 质量为 75）
LivePhotoConvert strip -i "D:\Photos\Camera" -o "D:\Photos\Optimized" -q 75

# 7. 仅剥离内嵌视频，不转码为 HEIC
LivePhotoConvert strip -i "D:\Photos\Camera" --no-heic

# 8. 提前静默检查并下载所有外部依赖组件
LivePhotoConvert tools --auto-download
```

### 子命令详解

#### 1. `merge` (合成动态照片)
扫描输入目录中的图片（`HEIC`、`JPG`、`PNG` 等）与视频（`MOV`、`MP4` 等），经过多信号特征校验后拼接为安卓动态照片。
```bash
LivePhotoConvert merge [选项]
```

#### 2. `split` (拆分动态照片)
扫描输入目录中的动态照片并将其解包：
- `-f android`（默认）：输出 `.jpg/.heic` + `.mp4`。
- `-f apple`：输出 `.jpg/.heic` + `.mov`，并为照片与视频双向注入相同的 `ContentIdentifier` UUID。
```bash
LivePhotoConvert split [选项]
```

#### 3. `strip` (动态照片瘦身与转码)
检测动态照片并截断内嵌视频，清理动态元数据，并可调用 `heif-enc` 转码为高压缩比的 HEIC 格式。
- 若省略 `-o / --output`，则采用**安全就地原子替换模式**（在临时目录处理完成后原子覆盖原文件）。
```bash
LivePhotoConvert strip [选项]
```

#### 4. `tools` (外部依赖工具管理)
检测当前运行环境中的 `ExifTool`、`FFmpeg` 与 `heif-enc` 状态，支持通过镜像自动下载部署。
```bash
LivePhotoConvert tools [选项]
```

---

### 全量参数选项表

| 参数选项 | 简写 | 适用命令 | 默认值 | 详细说明 |
| :--- | :--- | :--- | :--- | :--- |
| `--input <路径>` | `-i` | `merge`<br>`split`<br>`strip` | 无 | 输入目录路径（省略时自动弹出 Windows 原生文件夹选择器） |
| `--output <路径>` | `-o` | `merge`<br>`split`<br>`strip` | 无 | 输出目录路径（`strip` 省略时为就地修改模式） |
| `--format <格式>` | `-f` | `split` | `android` | 拆分格式：`android`（提取 MP4）或 `apple`（生成 iOS 实况 MOV） |
| `--source-action <策略>` | `-a` / `-s` | `merge` | `keep` | 合成成功后原文件处理策略：`keep`（保留）、`move`（移动至已处理）、`recycle`（放入回收站）、`delete`（永久删除） |
| `--no-heic` | | `strip` | `false` | 跳过 HEIC 格式转换，仅剥离动态照片中的内嵌视频并保留原图格式 |
| `--quality <数值>` | `-q` | `strip` | `65` | HEIC 压缩质量（`1`–`100`，推荐 `60-75`，画质视觉无损且体积大幅缩减） |
| `--skip-validation` / `--no-verify` | | `merge` | `false` | 跳过 ContentIdentifier 与时间差校验，强制仅按文件名主干匹配 |
| `--parallel <数量>` | `-p` | 全部 | CPU 核心数 | 最大并发处理任务数（默认根据 CPU 核心数自动调优） |
| `--overwrite` | | 全部 | `false` | 输出目录存在同名文件时直接覆盖（默认自动追加 `_1`、`_2` 序号防覆盖） |
| `--auto-download` | | 全部 | `false` | 检测到缺少 ExifTool / FFmpeg / heif-enc 时自动通过加速镜像静默下载 |
| `--mirror <URL>` | | 全部 | 内置源 | 自定义 GitHub 镜像加速前缀（例如 `https://ghfast.top/` 或 `https://ghproxy.net/`） |
| `--exiftool <路径>` | | 全部 | 自动检测 | 显式指定 `exiftool.exe` 可执行文件的绝对路径 |
| `--ffmpeg <路径>` | | 全部 | 自动检测 | 显式指定 `ffmpeg.exe` 可执行文件的绝对路径 |
| `--heif-enc <路径>` | | 全部 | 自动检测 | 显式指定 `heif-enc.exe` 可执行文件的绝对路径 |
| `--yes` / `--assume-yes` | `-y` | 全部 | `false` | 跳过所有开始前确认提示，适合自动化脚本运行 |

---

### 退出码定义

| 退出码 | 含义 | 说明 |
| :---: | :--- | :--- |
| **`0`** | 操作全部成功 | 所有目标文件均成功处理完成 |
| **`1`** | 运行时异常 | 遇到未捕获的系统异常或严重错误 |
| **`2`** | 命令行参数错误 | 输入参数无效或语法不正确 |
| **`3`** | 用户主动取消 | 用户在确认提示或目录选择阶段主动取消 |
| **`4`** | 部分文件处理失败 | 任务执行完毕，但有部分文件发生错误（详见终端失败汇总或日志） |

---

## 🔬 核心技术与底层逆向

### 1. 安卓动态照片存储机制 (GCamera XMP)

安卓动态照片遵循 [Google Motion Photo 规范](https://developer.android.com/media/platform/motion-photo-format?hl=zh-cn)，将封面 JPEG 图片与内嵌 MP4 视频直接进行二进制物理拼接（JPEG 在前，MP4 紧随其后），并在 JPEG 的 XMP 元数据区注入标记：
- `GCamera:MicroVideo = 1`：声明该图片包含微视频数据；
- `GCamera:MicroVideoOffset`：内嵌视频在整个文件末尾所占的字节长度；
- `GCamera:MicroVideoPresentationTimestampUs`：实况照片封面帧展示的时间戳（微秒）。

```
┌──────────────────────────────────────────────┐
│  JPEG 图像数据                                │
│  ├─ SOI / APP1 (EXIF & XMP GCamera Metadata) │
│  └─ 图像压缩数据 ...                           │
├──────────────────────────────────────────────┤ ◄─── (文件末尾倒数 MicroVideoOffset 处)
│  MP4 视频数据                                │
│  ├─ ftyp / moov / mdat                       │
│  └─ H.264 / AAC 视频流 ...                    │
└──────────────────────────────────────────────┘
```

---

### 2. 小米澎湃 OS `0x8897` 专属标签逆向解构

在开发过程中发现：仅写入 Google 标准 XMP 标签的动态照片在小米手机（澎湃 OS / MIUI 相册）中无法触发动态播放长按按钮。

通过使用 `jadx-gui` 反编译小米相册官方 APK 的底层解析类，定位到其关键校验逻辑：

<p align="center">
  <img src="docs/PixPin_2024-12-19_19-35-11.png" alt="小米相册动态照片识别逻辑反编译源码" width="750" />
</p>

逆向源码显示：小米相册不仅读取 XMP，还在底层读取 Exif 的专属私有标签：
- 代码中匹配十进制常数 `34967`，换算为十六进制即为 **`0x8897`**；
- 本程序通过 ExifTool 自动为合成的 JPEG 注入该专属标签，使小米相册（澎湃 OS / MIUI）能够正常识别并动态播放。

---

### 3. Apple Live Photo UUID 双向配对机制

苹果 Live Photo 由一张静态图片和一个 QuickTime MOV 视频组成，系统相册依赖全局唯一的 UUID 进行强校验绑定：
1. **图片端**：在 MakerNotes 或 Exif 元数据中注入 `ContentIdentifier`（例如 `1E874403-E522-4589-948A-E97AC157F32D`）；
2. **视频端**：在 QuickTime MOV 容器的元数据轨道 `com.apple.quicktime.content.identifier` 中写入相同 UUID，并设置 `still-image-time = 0`；
3. 当使用本工具的 `split -f apple` 模式时，程序会自动生成唯一 UUID 并同步写入两端，确保导入 iPhone 或 Mac 照片库后可正常识别为原生实况照片。

---

### 4. 极致性能与原子安全设计

- **.NET 10 Native AOT 纯原生编译**：无 JIT 编译开销，毫秒级极速冷启动，内存占用极小。
- **零分配格式嗅探与内存池**：采用 UTF-8 字节切片（`"heic"u8`、`"qt  "u8`）与位运算识别文件魔数，消除 GC 停顿；使用 `ArrayPool<byte>.Shared` 内存池与文件预分配（`SetFileInformationByHandle`），极大降低磁盘碎片。
- **原子占位防竞态 (`UniquePath`)**：多线程并发写入时使用原子重命名锁占位，绝不产生同名覆盖或文件损坏。
- **临时目录与失败回滚**：所有转换操作均在操作系统临时目录完成，校验完整后再执行原子移动；若中途取消或出错，自动清理半成品，不污染用户目录。

---

## ❓ 常见问题与排坑指南 (FAQ)

### Q1: 小米相册导出的动态照片放到 iPhone 上为什么不会动？
> **A**：安卓导出的动态照片是将 MP4 嵌入到单张 JPG 尾部的格式，iOS 无法直接识别。请使用本工具的 `split -f apple` 命令将其拆分为苹果实况照片对（包含配对 UUID 的 `.jpg` + `.mov`），随后通过 AirDrop、相册导入或 iCloud 即可正常长按播放。

### Q2: 瘦身转为 HEIC 格式后，相册的时间线和地点会乱吗？
> **A**：**完全不会**。本工具在执行瘦身或转码时，会先捕获原始文件的 `CreationTime` 与 `LastWriteTime`，转码完成后完整同步；同时保留所有 EXIF 元数据（包括 GPS 坐标、拍摄器材、光圈快门等），相册排序和地图足迹 100% 精确保留。

### Q3: 首次运行时依赖工具（ExifTool / FFmpeg / heif-enc）下载失败？
> **A**：本工具已内置多个国内高速镜像加速源。如果因特定网络环境受阻，您可以：
> 1. 添加参数指定其他可用镜像：`LivePhotoConvert tools --mirror https://ghfast.top/`；
> 2. 或手动将 `exiftool.exe`、`ffmpeg.exe`、`heif-enc.exe` 放置在程序同级目录下即可。

### Q4: 合成时选择移动或清理原文件，会误删我的其他长视频吗？
> **A**：**绝对不会**。程序内置了严格的配对校验器：只有同时满足「ContentIdentifier 配对一致」或「拍摄时间差 ≤3 秒且视频时长 ≤30 秒」并最终「合成校验成功」的文件，才会执行清理操作；未匹配的文件或普通拍摄长视频绝不会被触碰。

---

## 🛠️ 项目架构与本地构建

### 代码仓库布局
```
AppleLivePhotoConvert/
├── src/
│   ├── LivePhotoConvert.Core/       # 核心引擎：格式嗅探、二进制拼接、元数据编解码、外部工具调度 (0 第三方依赖)
│   └── LivePhotoConvert.Cli/        # 控制台程序：Spectre.Console 交互菜单、CLI 参数解析、Windows 原生弹窗
├── tests/
│   └── LivePhotoConvert.Core.Tests/ # 全套自动化单元测试套件 (108+ 用例全覆盖)
├── docs/                            # 架构文档、逆向解析截图与英文 README
└── LivePhotoConvert.slnx            # 现代化 .NET 解决方案文件
```

### 源码编译与测试

本项目要求安装 [.NET 10.0 SDK](https://dotnet.microsoft.com/download) 与 C# 13：

```bash
# 1. 还原依赖并编译解决方案
dotnet build LivePhotoConvert.slnx

# 2. 执行全量单元测试
dotnet test LivePhotoConvert.slnx

# 3. 发布为 Windows x64 Native AOT 绿色运行包
dotnet publish src/LivePhotoConvert.Cli/LivePhotoConvert.Cli.csproj /p:PublishProfile=win-x64-aot -o dist/aot
```

> **说明**：发布生成的 `dist/aot` 目录包含独立的 `LivePhotoConvert.exe` 主程序与 `Magick.Native-Q8-x64.dll` 原生图像加速库，可直接拷贝至任何 Windows 10/11 x64 设备运行。

---

## 💖 致谢与开源项目引用

本项目由衷感谢以下优秀的开源工具与规范标准的贡献：

- [ExifTool by Phil Harvey](https://exiftool.org/) - 行业标准的媒体元数据读写引擎
- [FFmpeg](https://ffmpeg.org/) - 领先的多媒体音视频处理框架
- [libheif](https://github.com/strukturag/libheif) & [x265](https://www.videolan.org/developers/x265.html) - 高性能 HEIF / HEIC 图像编解码库
- [Magick.NET / ImageMagick](https://github.com/dlemstra/Magick.NET) - 强大的 .NET 图像处理库
- [Spectre.Console](https://github.com/spectreconsole/spectre.console) - 现代化终端 UI 与命令行解析框架
- [Google Motion Photo Specification](https://developer.android.com/media/platform/motion-photo-format) - 安卓动态照片官方格式规范

---

## 📄 开源许可证

本项目采用 [MIT 许可证](LICENSE) 开源。欢迎提交 [Issue](https://github.com/ZhiQiu-Kinsey/AppleLivePhotoConvert/issues) 或 [Pull Request](https://github.com/ZhiQiu-Kinsey/AppleLivePhotoConvert/pulls) 贡献代码与反馈建议！
