# 阶段 3：实况播放（iOS / 安卓统一）与 HDR 保真

分支：`…-phase3`。本文件是设计要点与已知问题清单，开工前由主控细化为工作包。

## 已知问题（审查结论）

1. **悬浮/QuickLook 过的安卓动态照片会被再合成**：`MotionPhotoVideoCache.cs:79` 把切出的临时 mp4 写回 `card.VideoPath`（阶段 0 已在批处理侧用 `IsMotionPhoto` 过滤兜底，本阶段从根上去掉）。
2. **释放后仍被界面引用（崩溃）**：`PlaybackHost.RestoreStatic` 在 `Thumbnail` 为 null 时不复位 `DisplayImage`，随后帧被 Dispose；帧缓存驱逐时未检查是否正被显示；QuickLook `Cleanup` 同步释放正在显示的帧且被调用三次。
3. 切换卡片时上一张 `IsHoverPlaying` 未复位；`PlayAllVisible` 只有最后一张真正播放（删除该功能）。
4. QuickLook 不读设置里的 FFmpeg 路径；`ffprobe` 名字写死为 `ffprobe.exe` 且依赖包里没有 ffprobe（npmmirror 的 BtbN n7.0 包只有 ffmpeg.exe），时间轴几乎总回退到 30fps。
5. 每次悬浮都启动一次 `ffmpeg -version`（阶段 0 的 `ToolLocator` 已缓存探测结果，接入新播放器时确认不再重复）。
6. QuickLook 等整段解码完才播放；达到 90 帧后用 `continue` 而非 `break`，剩余帧仍创建 Bitmap 再 Dispose；ffprobe `show_entries frame=` 会再完整解码一遍。
7. `motion_cache` 目录永不清理，缓存键（文件名主干+长度+偏移）会跨目录撞键。
8. 悬浮播放固定 33ms 一帧，不按 PTS；预热与悬浮竞态时旧帧不释放且同时跑两个 FFmpeg；取消时不直接结束进程；`_activeProcess` 跨线程访问无同步。
9. 内存：Skia 把 BGR24 BMP 解成 BGRA，悬浮约 221MB、QuickLook 约 236MB；每帧一个 LOH `byte[]` 加 MemoryStream 拷贝。
10. 转换侧：`FfmpegVideoConverter` 转码回退为 `libx264 + yuv420p`（8-bit、无色调映射），HDR 源会发灰；HEVC 输出未加 `-tag:v hvc1`。
11. 本地化：`QuickLookDialogViewModel.cs:79` 写死"实况播放中"（阶段 1 会处理）。

## 设计要点

- **输入**：iOS 直接读 MOV；安卓用 FFmpeg `subfile` 协议按 `MotionPhotoLayout` 定位的偏移直接读（`subfile,,start,<offset>,end,<offset+length>,,:<path>`），不再切临时文件；删除 `MotionPhotoVideoCache`。
- **帧管线**：`-f rawvideo -pix_fmt bgra`，输出尺寸 = 显示尺寸 × RenderScaling；直接写入 `WriteableBitmap`（`ArrayPool` 缓冲，零拷贝）；PTS 从 `-vf ...,showinfo` 的 stderr 解析；`-fps_mode passthrough` 保留；由 `TopLevel.RequestAnimationFrame` 驱动播放。
- **FrameStore**：整段按显示尺寸能放进预算（悬浮 96MB、QuickLook 256MB）则全部缓存循环播放，否则环形缓冲流式解码、接近结尾预启动下一轮保证无缝循环；完整播放 Live Photo 的 3 秒，不再固定帧数上限。
- **HDR 预览**：扫描/播放前用 `ffmpeg -hide_banner -i` 的流信息判断 `color_transfer`（arib-std-b67 / smpte2084）；HDR 链：`zscale=t=linear:npl=203,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=mobius:desat=0,zscale=t=bt709:m=bt709:r=pc`（缩放在同一 zscale 链内完成）；SDR 链显式 `in_color_matrix=auto`、`flags=lanczos+accurate_rnd+full_chroma_int`。实测：npmmirror 源（BtbN n7.0 GPL）带 `--enable-libzimg` 与 `--enable-libplacebo`；Gyan essentials 带 libzimg（不带 libplacebo）；BtbN latest 带两者。自定义 FFmpeg 缺 zscale 时明确提示而非静默降级（由阶段 4 的 `ToolRegistry` 能力探测提供）。不采用 libplacebo（依赖 Vulkan）。
- **HDR 转码**：`ConvertToMp4Async` 的转码回退对 HDR 源使用 `libx265`、`yuv420p10le`，保留 `color_primaries/color_trc/colorspace`；前置镜像视频同样走 10-bit；HEVC 输出加 `-tag:v hvc1`（需先判断编码，H.264 不能加）。
- **增益图调研（spike）**：iPhone HEIC 的 HDR 增益图转 JPEG 时丢失，调研转为 Ultra HDR JPEG（ISO 21496-1，libultrahdr）的可行性，出结论和样片对比后再决定是否实现。
- 更新 AGENTS.md 4.5 的播放规范（帧预算按字节、rawvideo BGRA、PTS）。

## 工作包

- **WP3.1 HDR 保真转码与视频流探测（Core，已完成）**：`VideoStreamProbe`（解析 `ffmpeg -i`，支持 subfile）、HDR 源 libx265 10-bit 并保留色彩三元组与 HDR10 元数据、HEVC 统一 hvc1、缺 libx265 时 `VideoConversionException(HdrEncoderUnavailable)`、转码 CRF 18。
- **WP3.2 播放引擎（Desktop，独立于画廊）**：`Features/Playback/`：
  - `VideoSource`（与 WP2.1 的 Core `VideoSource` 对齐：Path、Offset、Length、IsEmbedded；合并前可先用本地 record，合并阶段 2 后改用 Core 类型）。
  - `RawVideoDecoder`：经 ProcessRunner 风格的受控进程启动 FFmpeg，输入为文件或 `VideoStreamProbe.SubfileInput`，输出 `-f rawvideo -pix_fmt bgra`，尺寸 = 显示尺寸 × RenderScaling（保持比例、偶数对齐），SDR 链 `scale=...:flags=lanczos+accurate_rnd+full_chroma_int:in_color_matrix=auto`，HDR 源（`VideoStreamProbe.IsHdr`）链 `zscale=t=linear:npl=203,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=mobius:desat=0,zscale=t=bt709:m=bt709:r=pc,format=bgra`（缩放在 zscale 链内完成）；FFmpeg 缺 zscale 时返回明确状态（不静默降级）；`-fps_mode passthrough`；PTS 从 `showinfo` 的 stderr 解析；取消即结束进程树；stdout 读入 `ArrayPool` 缓冲。
  - `FrameStore`：按字节预算（悬浮 96MB、QuickLook 256MB）——整段放得下则全部缓存循环播放，否则环形缓冲流式解码并在接近结尾时预启动下一轮保证无缝循环；帧的生命周期由 FrameStore 管理，正在显示的帧不会被释放（引用计数或双缓冲 `WriteableBitmap`）。
  - `LivePhotoPlayer`：单实例播放器（同一时刻只一个悬浮播放、QuickLook 独占），`Play(VideoSource, Size, scaling)` / `Stop()`，按 PTS 由 `TopLevel.RequestAnimationFrame` 驱动，输出一个 `WriteableBitmap` 供绑定；状态：Idle/Loading/Playing/Error(原因)。
  - 测试：PTS 解析、滤镜链构造（SDR/HDR/缺 zscale）、FrameStore 预算与循环、取消后进程退出、真实 FFmpeg 解码 testsrc 片段帧数与 PTS 单调（工具缺失跳过）、subfile 读内嵌视频。
- **WP3.3 接入与清理（阶段 2 完成后）**：卡片悬浮与 QuickLook 改用 `LivePhotoPlayer`；删除 `PlaybackHost`、`LivePhotoStreamPlayer`、`MotionPhotoVideoCache`、`BmpPipeFrameReader`、`motion_cache` 目录（启动时清理旧目录）；修复已知问题 1～9；QuickLook 读设置中的 FFmpeg 路径；AGENTS.md 4.5 播放规范更新；Headless 冒烟与内存压力验证。
- **WP3.4 增益图调研（spike）**：iPhone HEIC HDR 增益图 → Ultra HDR JPEG（ISO 21496-1，libultrahdr）可行性，出结论与样片对比，不直接实现。
