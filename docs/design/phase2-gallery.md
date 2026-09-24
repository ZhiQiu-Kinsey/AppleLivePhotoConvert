# 阶段 2：画廊（扫描、排版、缩略图、画质与内存）

分支：`…-phase2`（叠加在阶段 1 之上）。本文件是设计要点与已知问题清单，开工前由主控细化为工作包。

## 已知问题（审查结论，行号以阶段 0 代码为准）

1. **LRU 固定 24 张少于可见卡片数**（`LruThumbnailManager.cs:13`）：宽屏竖图约 32 张可见，会把可见卡片清成占位图且不自动重载；快速滚动时离屏卡片仍在高优先级队列里被解码并挤掉可见卡片。
2. **竖拍 JPEG 比例错误**：`ThumbnailReader` 用 Magick `Ping` 取存储宽高（未应用 EXIF 方向），覆盖了 `FastImageHeaderReader` 算对的比例，UniformToFill 把竖图裁成横框，且每张缩略图到达都触发整体重排。
3. **Windows Shell 缩略图**只传 `SIIGBF_RESIZETOFIT`：没有 HEIF 扩展时返回文件图标并被写入磁盘缓存；第二次 `GetDIBits` 前未设 `biBitCount=32`，24bpp 时 BGRA 解析错乱。应加 `SIIGBF_THUMBNAILONLY`。
4. **EXIF 内嵌缩略图（约 160px）被放大到 960 且未转正**：内嵌图小于目标时改走完整解码；几何 `Greater=true`；按原图方向旋转。
5. **扫描遇到无权限目录崩溃**：`EnumerateFiles(AllDirectories)` 未设 `IgnoreInaccessible`，`RefreshAlbumAsync` 只捕获 OCE。用 `EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = System | ReparsePoint }`。
6. **UI 线程同步 I/O 与解码**：`PrioritizeThumbnail → EnsureThumbnailLoaded` 在 UI 线程做 SHA256、多次 `File.Exists`、读缓存并解码；`UpdateSelectionSummary` 对全部照片/视频做 `FileInfo`（1 万对 = 4 万次 I/O），应复用扫描时的大小。
7. **行高估算偏差**：预取按 `RowHeight + 90` 估算，实际 +86，深处预取错位；每次滚动从头遍历。改用 `ListBox.GetRealizedContainers()` 或预计算累计偏移 + 二分。
8. **重建过于频繁**：`UpdateCardWidth` 每变 2px 全量 Reset；应防抖并在行组成不变时原地更新尺寸；`DisplayGroups` 无绑定；`GalleryTemplateSelector` 为死代码。
9. **解码峰值内存**：完整解码无尺寸提示（4800 万像素约 190MB/张）；JPEG 用 `jpeg:size` define 让 libjpeg DCT 缩放；HEIC 完整解码限制 1 个并发；重扫后旧 worker 不可取消。
10. 其它：模式 0 串行嗅探；缓存命中套用旧计数使人工裁决后的计数回退；磁盘缓存写入非原子且无容量上限；首屏插队用扫描顺序而非显示顺序；`bool.TryParse(s, out _)` 对 "false" 也返回 true（`SetSortDirection`、`SelectAllVisible`）。
11. 分组标题按钮文案为"选定本日"，年/月分组时不对。

## 设计要点

- **统一扫描**：扫描器迁入 Core（`Media/LibraryScanner`），一次扫描产出带类型标记的条目（苹果实况对 / 安卓动态照片 / 普通照片），配对复用 `MediaPairMatcher`（按目录+主干），动态照片检测复用 `MotionPhotoLayout`；HEIC 动态照片的 XMP 通过 `IMetadataService.ReadXmpAsync` 延迟补全；动作只做筛选，不再按动作重新扫描。
- **排版**：纯函数 `JustifiedLayoutEngine.Compute(aspects, width, targetHeight, spacing, maxRowHeightFactor=1.3)`，可单测；窗口缩放防抖 120ms，行对象复用。
- **缩略图分档**：目标高度 = 该模式最大行高 × `TopLevel.RenderScaling`，档位 256/384/512/768/1024，长边上限 4096，分档写入磁盘缓存键（含修改时间与大小），监听 `ScalingChanged`；内存用 `Bitmap.DecodeToHeight(..., BitmapInterpolationMode.HighQuality)`，显示缩放比控制在 1～1.5。
- **内存预算**：按字节计（默认 192MB，可设置）；已实例化卡片不驱逐（控件附加时登记、分离时注销并移出队列）；视口代次号丢弃过时请求。
- **色彩**：生成缩略图时转换到 sRGB 后去掉 profile；Shell 缩略图再编码质量 q95。
- **磁盘缓存**：原子写入、容量上限与 LRU 清理。
- 更新 AGENTS.md 4.5 的内存预算描述。
12. 排序菜单"创建日期/修改日期"实际都按拍摄日期排序。
13. `LibraryViewModel` 约 1200 行（阶段 1 从转换页原样拆出），本阶段按扫描、排版、缩略图、选择拆分。
