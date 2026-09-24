# 阶段 4：瘦身对比与依赖引擎

分支：`…-phase4`。本文件是设计要点与已知问题清单，开工前由主控细化为工作包。

## 瘦身对比（阶段 1 后位于 `StripCompareDialog`）

- 现状是假的对比：先缩到 1600px 再用 Magick 编码，体积与画质都不是实际编码器（heif-enc）的产物；heif-enc 不可用时直接按 JPEG × 0.65 估算。
- 实测：Linux 版 Magick.NET 没有 HEIC 编码器（`MagickImageConverter.SupportsHeicEncoding` 为 false）；Windows 版是否带需要在 CI 或真机上确认。
- 方案：对样张真正跑一遍 `MotionPhotoStripper`（输出到临时目录），用真实产物做对比与体积；`CurtainCompareControl` 改成通用控件（`Before`/`After` 属性），不再绑死 VM；增加同步缩放/平移与 1:1 像素放大镜（1:1 区域按需从原图裁切解码）；相册级节省估算用抽样的真实压缩比外推并标注"估算"。

## 依赖引擎

已知问题：

1. 无 SHA256 校验，经第三方代理下载后直接执行；BtbN `latest` 链接不固定版本。
2. `ToolDownloader.cs:232-243`、`:272-282` 对 `exiftool_files/../..` 类路径无前缀校验（zip slip）。
3. 直接覆盖最终可执行文件，失败会留下坏文件；读取响应体无空闲超时。
4. heif-enc 只有 7z 包，依赖 Windows 自带 `tar.exe` 解压。
5. `ToolsViewModel`：镜像名写死中文；镜像地址每次按键都同步存盘；`ProbeVersion` 的 `ReadLine` 无超时也不结束进程。

方案：

- `tools.json` 内嵌清单（Source Generator 反序列化）：工具、锁定版本、每个源的 URL、SHA256（npm 源另校验 sha512 integrity）、压缩格式与入口路径。FFmpeg 锁定 BtbN 带版本号的 GPL 构建，npmmirror 为国内首选源（`@ffmpeg-binary/win32-x64` 7.0.0 = BtbN n7.0，带 zimg/libplacebo，无 ffprobe）。ExifTool 当前源 `exiftool-vendored.exe` 13.59.2。
- `ToolInstaller`：下载到暂存目录 → 校验哈希 → 解压到暂存目录（路径穿越防护；zip 用 .NET 10 异步 `ZipFile.ExtractToDirectoryAsync`）→ 探测可运行 → 整目录原子替换；空闲超时；取消不留半成品。
- `ToolRegistry`：解析一次并缓存路径、版本与能力（FFmpeg：zscale、tonemap、libx265、10-bit；heif-enc 版本），设置变更或安装完成后失效；依赖页展示版本与能力，有推荐版本时提示升级。
- 评估 Magick.NET 自带 HEIC 编码能否在同等体积下达到 heif-enc 的画质，决定是否去掉 heif-enc 依赖。
