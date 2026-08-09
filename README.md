# LivePhotoConvert (动态照片工具箱)

<p align="center">
  <img src="src/LivePhotoConvert.Cli/LivePhotoConvert.ico" width="80" height="80" alt="LivePhotoConvert Logo" />
</p>

<p align="center">
  <strong>⚡ 在苹果实况照片 (Apple Live Photo) 与安卓动态照片 (Motion Photo) 之间实现双向高保真无损转换</strong>
</p>

<p align="center">
  <a href="https://dotnet.microsoft.com/download"><img src="https://img.shields.io/badge/.NET-10.0-512BD4?style=flat-square&logo=dotnet" alt=".NET 10" /></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-MIT-blue.svg?style=flat-square" alt="License" /></a>
  <img src="https://img.shields.io/badge/Platform-Windows%20x64-0078D6?style=flat-square&logo=windows" alt="Platform" />
  <img src="https://img.shields.io/badge/Native%20AOT-Supported-success?style=flat-square" alt="Native AOT" />
  <a href="https://github.com/ZhiQiu-Kinsey/AppleLivePhotoConvert/actions/workflows/ci.yml"><img src="https://img.shields.io/badge/Tests-86%20Passed-success?style=flat-square&logo=githubactions&logoColor=white" alt="Tests" /></a>
</p>

<p align="center">
  <a href="README.md"><b>简体中文</b></a> • <a href="docs/README.en.md"><b>English</b></a>
</p>

---

## 🌟 核心特性

- 🔄 **双向转换与实况还原**：
  - **合成模式**：将 iPhone 导出的实况照片（`HEIC/JPG` + `MOV`）合成为标准安卓单文件动态照片（`.jpg`），完美适配**小米相册（澎湃 OS / MIUI）、Google 相册、Windows 11 照片应用**等全平台动态播放。
  - **双格式拆分模式**：
    - **标准安卓格式 (`android`)**：原生无损提取封面与内嵌视频（`.jpg/.heic` + `.mp4`）。
    - **苹果实况格式 (`apple`)**：将安卓动态照片转换为 Apple Live Photo 兼容格式（`.jpg/.heic` + `.mov`），自动生成并注入配对的 `ContentIdentifier` UUID，**可直接导入 iPhone/Mac 相册长按动态播放**。
- ⚡ **无损流复制与高效转换**：
  - 视频优先采用流复制（`-c:v copy`），音频自动转码为标准 AAC，**毫秒级极速处理且视频画质 0 损失**。
- 🚀 **极小体积与原生机器码**：
  - 基于 .NET 10 **Native AOT** 纯原生机器码编译，免安装绿色压缩包开箱即用，冷启动达到毫秒级。
- 📥 **智能依赖一键就绪**：
  - 内置国内加速镜像（阿里云 CDN）下载源，首次启动自动检测并一键静默安装配置 `ExifTool` 与 `FFmpeg`。
- 🖥️ **现代化双模交互**：
  - **交互式菜单**：基于 Spectre.Console 的彩色终端界面，集成 **Windows 原生文件夹弹窗选择器** 与拖拽支持。
  - **CLI 命令行参数**：支持完整静默参数、并发控制、批处理脚本集成。
- 🧹 **安全清理与完整性校验**：
  - 支持合成成功后对原始文件进行保留、移动或清理；内置字节流长度与元数据双重校验，绝不误删未配对的长视频。

---

## 📸 运行预览

<p align="center">
  <img src="docs/preview.png" alt="主程序交互界面" width="850" />
</p>

---

## 🚀 快速上手

### 1. 下载程序
前往 [Releases 页面](https://github.com/ZhiQiu-Kinsey/AppleLivePhotoConvert/releases) 下载最新的 `LivePhotoConvert` 绿色压缩包并解压（包含主程序与原生图像加速库）。

### 2. 从 iPhone 导出照片
1. 打开 iPhone【照片】App，选中需要导出的实况照片。
2. 点击左下角【分享】 $\rightarrow$ 选择【导出未修改的原片】保存到本地（每张实况照片将导出为一个同名的图片与 `.MOV` 视频）。
3. 将导出的文件夹复制到电脑上。

### 3. 运行转换
直接双击运行 `LivePhotoConvert.exe`：
* **首次运行**：程序会自动检查外部工具，若缺失将提示通过国内镜像一键自动下载。
* **合成转换**：在主菜单选择 `1. 合成动态照片`，按弹窗提示选取输入与输出目录即可。
* **拆分还原**：选择 `2. 拆分动态照片`，可自主选择输出为通用安卓格式或苹果实况格式。

---

## 💻 命令行使用指南 (CLI)

除交互菜单外，程序提供丰富的 CLI 命令行接口，便于脚本自动化与批处理调用：

```bash
# 1. 基础合成：将照片和视频合成为动态照片
LivePhotoConvert merge -i "D:\Photos" -o "D:\MotionPhotos"

# 2. 合成并移动原始文件（静默确认）
LivePhotoConvert merge -i "D:\Photos" -o "D:\MotionPhotos" -s move -y

# 3. 拆分为标准安卓格式 (.jpg + .mp4)
LivePhotoConvert split -i "D:\MotionPhotos" -o "D:\Output" -f android

# 4. 拆分为苹果实况照片格式 (.jpg + .mov，写入 Live Photo 元数据)
LivePhotoConvert split -i "D:\MotionPhotos" -o "D:\AppleLivePhotos" -f apple

# 5. 提前检查或下载外部依赖工具
LivePhotoConvert tools --auto-download
```

### 参数选项一览表

| 选项 | 别名 | 适用命令 | 说明 |
| :--- | :--- | :--- | :--- |
| `--input <目录>` | `-i` | `merge` / `split` | 输入目录路径（省略时自动弹出原生文件夹选择框） |
| `--output <目录>` | `-o` | `merge` / `split` | 输出目录路径（省略时自动弹出原生文件夹选择框） |
| `--format <格式>` | `-f` | `split` | 拆分目标格式：`android`（标准安卓格式，默认）或 `apple`（苹果实况照片） |
| `--source-action <方式>`| `-s` | `merge` | 合成成功后原始文件处理策略：`keep`（保留，默认）、`move`（移动）、`recycle`（回收站）、`delete`（永久删除） |
| `--strict` | | `merge` | 严格校验模式：使用 Apple Content Identifier 校验图片与视频确实属于同一张实况 |
| `--parallel <数量>` | `-p` | `merge` / `split` | 并行并发处理文件数（默认根据 CPU 核心数自动调优） |
| `--overwrite` | | `merge` / `split` | 输出目录存在同名文件时直接覆盖（默认自动追加 `_1`、`_2` 后缀） |
| `--auto-download` | | 全部 | 缺少 ExifTool 或 FFmpeg 时自动通过加速镜像下载安装 |
| `--mirror <前缀>` | | `tools` | 自定义 GitHub 镜像代理前缀（如 `https://ghfast.top/`） |
| `--exiftool <路径>` | | 全部 | 显式指定 ExifTool 可执行文件路径 |
| `--ffmpeg <路径>` | | 全部 | 显式指定 FFmpeg 可执行文件路径 |
| `--yes` | `-y` | 全部 | 跳过开始前的确认提示，便于脚本自动化执行 |

> [!NOTE]
> **退出码定义**：`0` 全部成功，`1` 执行异常，`2` 参数错误，`3` 用户主动取消，`4` 部分文件处理失败。

---

## 🔬 技术原理与逆向解析

### 1. 安卓动态照片存储机制
标准安卓动态照片遵循 [Google Motion Photo 规范](https://developer.android.com/media/platform/motion-photo-format?hl=zh-cn)，将封面 JPEG 与 MP4 视频直接进行二进制流拼接（封面在前，视频在后），并通过 XMP 命名空间注入元数据：
* `GCamera:MicroVideo = 1`：声明该图片包含微视频数据。
* `GCamera:MicroVideoOffset`：内嵌视频在文件尾部的字节偏移量。
* `GCamera:MicroVideoPresentationTimestampUs`：实况照片代表帧的时间戳（微秒）。

### 2. 小米相册 `0x8897` 专属标识逆向发现
在开发过程中，仅写入 Google 标准 XMP 标签的小米手机无法正常触发动态播放效果。通过使用 `jadx-gui` 反编译小米相册官方 APK 分析其判定逻辑：

<p align="center">
  <img src="docs/PixPin_2024-12-19_19-35-11.png" alt="小米相册动态照片识别逻辑反编译源码" width="750" />
</p>

逆向发现小米相册在底层通过读取 Exif 专属标签判定动态照片：
* 代码中匹配十进制常数 `34967`，换算为十六进制即 **`0x8897`**。
* 本程序使用 ExifTool 自动配置并注入该特殊标签，完美解决了小米澎湃 OS（Xiaomi HyperOS / MIUI）相册无法识别的问题。

### 3. Apple Live Photo 配对标识机制
苹果实况照片依赖全局唯一的 UUID 进行双向绑定：
* **图片端**：在 MakerNotes 或 Exif 注入 `ContentIdentifier`。
* **视频端**：在 QuickTime MOV 容器的 `com.apple.quicktime.content.identifier` 注入相同 UUID，并设置 `still-image-time = 0`。
* 拆分选择 `apple` 格式时，程序会自动生成配对 UUID 并完成双向元数据写入，确保导入 iOS / macOS 照片库后可正常动态播放。

---

## 🛠️ 项目结构与本地构建

### 架构布局
```
src/
  LivePhotoConvert.Core/       # 纯净核心库：格式嗅探、二进制拼接、元数据编解码、外部工具驱动（0 第三方依赖）
  LivePhotoConvert.Cli/        # 控制台程序：Spectre.Console 交互界面、CLI 参数解析、进度渲染
tests/
  LivePhotoConvert.Core.Tests/ # 全套自动化单元测试（覆盖解包、嗅探、配对与清理）
```

### 编译运行
本项目基于 **.NET 10.0** 与 C# 13 构建：

```bash
# 1. 还原并编译解决方案
dotnet build LivePhotoConvert.slnx

# 2. 运行自动化测试套件
dotnet test LivePhotoConvert.slnx

# 3. 发布为 Native AOT 绿色运行包
dotnet publish src/LivePhotoConvert.Cli/LivePhotoConvert.Cli.csproj /p:PublishProfile=win-x64-aot -o dist/aot
```
> 发布输出目录包含 `LivePhotoConvert.exe` 主程序及 `Magick.Native-Q8-x64.dll` 原生加速库。

---

## 💖 致谢与开源项目引用

本项目由衷感谢以下优秀的开源工具与项目支持：

* [ExifTool by Phil Harvey](https://exiftool.org/) - 强大的多格式媒体元数据读写引擎
* [FFmpeg](https://ffmpeg.org/) - 领先的多媒体音视频处理框架
* [Magick.NET / ImageMagick](https://github.com/dlemstra/Magick.NET) - 强大的图像处理库
* [Spectre.Console](https://github.com/spectreconsole/spectre.console) - 优雅强大的 .NET 终端 UI 渲染库
* [Google Motion Photo Specification](https://developer.android.com/media/platform/motion-photo-format) - 安卓动态照片规范

---

## 📄 开源许可证

本项目基于 [MIT 许可证](LICENSE) 开源。欢迎提交 Issue 或 Pull Request 为项目贡献力量！
