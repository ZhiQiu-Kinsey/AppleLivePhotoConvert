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
