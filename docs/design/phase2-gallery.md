# 阶段 2：画廊（扫描、排版、缩略图、画质与内存）

分支：`…-phase2`（叠加在阶段 1 之上）。本文件包含已知问题清单、设计要点与工作包。

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

## 清单外问题（阶段 1 结束时代码审查）

14. 同一照片产出多张卡片：`AlbumScanner` 对每个候选配对各建一张卡，同时有 MOV 与 MP4 时 `Key=PhotoPath` 重复，计数重复。
15. 扫描未过滤 `~lpc-*` 暂存与 `.livephoto_backup`，就地瘦身期间会进入画廊。
16. "拍摄日期"实为修改时间；`ThumbnailReader` 读出的拍摄时间从未使用。
17. 写死英文 "Motion Photo"；日期与状态文案在扫描时固定，切换语言不刷新。
18. 裁决通过后卡片仍未选中、可疑徽章仍显示（`HasSuspiciousWarning` 为 init-only）。
19. QuickLook 借用卡片位图显示，可能被 LRU 驱逐释放；QuickLook 自建 `ThumbnailReader` 做 1600px 完整解码，绕开并发限制并非原子写同一缓存目录。
20. `LibraryViewModel`/`LibraryView.axaml.cs` 仍有 Action 回调接线；`PhotoCardControl` 使用静态 `PlaybackHost.Instance`。
21. 滚动恢复按像素偏移，重排后跳位；`async void` 与丢弃的 Task 未观察异常；`Directory.Exists` 被绑定在 UI 线程反复调用；QuickLook 序列包含被筛掉的卡片；待裁决阈值写死 3.0 未引用 `PairValidator`；窄视口 `Math.Max(260, w-24)` 溢出。

## 工作包

依次实现：WP2.1 → WP2.2 → WP2.3 → WP2.4 → WP2.5。WP2.3 先把缩略图代码从 `LibraryViewModel` 抽走，WP2.4 再拆剩余部分，避免同一文件大改两次。删除旧 API 的工作包负责同步迁移引用它的测试；测试中通过反射读私有字段的写法一律改为公开读模型。

**共享常量** `Features/Library/GalleryMetrics`（WP2.3 新建）：目标行高 180/250/320、最大行高系数 1.3、信息栏高、卡片边距、行距、组标题固定高 40。分档与排版都用它，消除 +86/+90 两套估算。

### WP2.1 Core 统一扫描

目标：一次扫描产出带类型标记的条目，切换动作只做筛选。

- 新增 `Core/Media/LibraryScanner.cs`、`LibraryItem.cs`；`FastImageHeaderReader` 新增 `TryReadHeader`（转正后宽高、Orientation、DateTimeOriginal），原 `TryReadDimensions` 保留为包装。
- 类型（草案，实现者可调整命名但需保留语义）：

```csharp
public enum LibraryItemKind { ApplePair, MotionPhoto, Still }
public sealed record LibraryFile(string Path, long Length, DateTime LastWriteTimeUtc, DateTime CreationTimeUtc);
// 为阶段 3 播放器预留：iOS 为整段 MOV，安卓为照片内 [Offset, Offset+Length)，直接映射到 FFmpeg subfile
public sealed record VideoSource(string Path, long Offset, long Length, bool IsEmbedded);
public readonly record struct ImageHeader(int Width, int Height, int Orientation, DateTime? DateTimeOriginal);
public enum CaptureTimeSource { Exif, FileName, LastWrite }
public sealed record LibraryItem(LibraryItemKind Kind, LibraryFile Photo)
{
    public LibraryFile? Video { get; init; }
    public EmbeddedVideo? Embedded { get; init; }
    public IReadOnlyList<MediaPair> PairCandidates { get; init; } = [];   // 同主干全部候选，合成时择优
    public ImageHeader? Header { get; init; }
    public bool HasGainMap { get; init; }
    public DateTime CaptureTimeLocal { get; init; }
    public CaptureTimeSource CaptureTimeSource { get; init; }
    public TimeSpan? PairTimeDelta { get; init; }
    public long SourceBytes => Photo.Length + (Video?.Length ?? 0);
    public VideoSource? VideoSource { get; }
}
public sealed record LibraryScanResult(IReadOnlyList<LibraryItem> Items, int TotalFiles, int InaccessibleEntries);
public static class LibraryScanner
{
    public static Task<LibraryScanResult> ScanAsync(string root, IProgress<int>? progress = null, CancellationToken ct = default);
    public static IAsyncEnumerable<LibraryItem> EnrichHeicAsync(IReadOnlyList<LibraryItem> stills, IMetadataService metadata, CancellationToken ct = default);
}
```

- 数据流：`FileSystemEnumerable`（`IgnoreInaccessible`、跳过 System/ReparsePoint/Hidden，枚举时直接取大小与时间，不再逐个 `FileInfo`；过滤 `~lpc-*` 与 `.livephoto_backup`）→ `MediaPairMatcher` 按照片分组，每张照片只产出一个条目 → 未配对的 jpg/jpeg/heic 有界并行 `MotionPhotoLayout.Inspect` → 其余为 `Still` → 并行读头部 → 拍摄时间 EXIF → 文件名 → 修改时间。HEIC 延迟补全：顶层 box 之后有未归属尾部字节才 `ReadXmpAsync` + `Locate(path, xmp)`；无 ExifTool 时跳过。待裁决阈值引用 `PairValidator`。
- 测试（Core.Tests/Media）：苹果对；同主干 HEIC+JPG+MOV+MP4 只产出一个条目并保留候选；JPEG、HEIC(mpvd)、三星动态照片；普通照片；暂存与备份被过滤；无权限子目录（Linux `SetUnixFileMode`，Windows 跳过）；取消；结果顺序确定；方向 6 宽高互换；`DateTimeOriginal` 解析；假 `IMetadataService` 升级 HEIC。
- 验收：无权限目录不抛异常并计数；1 万文件合成目录扫描不超过 3 秒（记录实测值）。

### WP2.2 Core 缩略图引擎

目标：生成方向正确、sRGB、分档尺寸的磁盘缓存，压住解码峰值内存。

- 新增 `Core/Media/Thumbnails/{ThumbnailTiers, ThumbnailKey, ThumbnailDiskCache, ThumbnailGenerator}.cs`；`WindowsShellThumbnail` 迁到 `Core/Platform/`，改 `LibraryImport`，实现 `IThumbnailSource`，在专用 MTA 线程调用。
- 分档：高度 256/384/512/768/1024，长边上限 4096，`Select(requiredPx)` 取不小于需求的最小档。
- 键：SHA256(版本 | 规范化路径 | 长度 | 修改时间 | 档位)，按前 2 位分子目录。
- 缓存：同目录临时文件 + `File.Move(overwrite)` 原子写入；命中时节流刷新 LastWriteTime 作为访问时间（NTFS 默认不更新访问时间）；超上限按访问时间删到 80%。
- 来源顺序：磁盘缓存 → Windows Shell（`THUMBNAILONLY | BIGGERSIZEOK`；第二次 `GetDIBits` 前设 32 位；小于 0.9×档位丢弃）→ EXIF 内嵌缩略图（转正后不低于档位才用）→ 完整解码（JPEG 用 `JpegReadDefines.Size` 按存储方向传入做 DCT 缩放，最多 2 并发；HEIC 全局 1 并发）。
- 规范化：先取出 ICC → `AutoOrient` → 只缩小（`Greater`）→ `TransformColorSpace(icc → sRGB)` → `Strip` → **JPEG q92、4:4:4 色度不抽样**（画廊画质优先，缓存体积可接受）。
- 测试：4000×3000 方向 6 输出竖图且高度等于档位；160px 内嵌缩略图不采用；小图不放大；AdobeRGB 源转换后无 profile 且饱和绿像素值改变；读取几何按存储方向；同键并发 Put 无异常无残留；容量淘汰最旧；键随修改时间/大小/档位变化；HEIC 并发不超过 1（注入可计数解码委托）；Shell 仅 Windows。
- 验收：4800 万像素 JPEG 峰值内存增量低于 60MB（记录实测）。

### WP2.3 桌面缩略图管线

目标：按字节预算取代"24 张"；已实例化卡片不驱逐；视口代次号；DPI 分档；UI 线程零 I/O。

- 新增 `Features/Library/Thumbnails/{ByteBudget, ThumbnailPipeline, GalleryMetrics}.cs`；删除 `Services/LruThumbnailManager.cs`、`ThumbnailReader.cs`、`WindowsShellThumbnail.cs`；`PhotoCardControl` 删除 `TriggerPriorityLoad`；设置新增 `Gallery.ThumbnailBudgetMb = 192`、`ThumbnailDiskCacheMb`（schema 升级）。

```csharp
public sealed class ByteBudget<TKey>(long capacityBytes) where TKey : notnull
{
    public void Add(TKey key, long bytes); public void Remove(TKey key);
    public void Pin(TKey key); public void Unpin(TKey key); public void Touch(TKey key);
    public IReadOnlyList<TKey> CollectEvictions();   // 只返回未钉住条目，按 LRU 直到不超预算
    public long ResidentBytes { get; } public long PinnedBytes { get; }
}
public interface IThumbnailPipeline
{
    void Acquire(PhotoCardItemViewModel card);   // 引用计数；未加载则按当前代次入队
    void Release(PhotoCardItemViewModel card);   // 计数归零：出队、解除钉住
    int NextGeneration();                        // 滚动静止或重排后调用
    void Configure(double maxRowHeightDip, double renderScaling);
    void Reset();                                // 重扫：纪元加一，丢弃在途结果
    Task<Bitmap?> LoadPreviewAsync(PhotoCardItemViewModel card, int heightPx, CancellationToken ct); // 调用方拥有
    long ResidentBytes { get; }
}
```

- 数据流：`ListBox.ContainerPrepared/ContainerClearing`（虚拟化回收时容器不一定分离，不能依赖 `OnDetachedFromVisualTree`）→ 行内卡片 `Acquire/Release`（UI 线程 O(1)）→ 缓存通道（1 worker）`TryGet` → `Bitmap.DecodeToHeight(min(档位, ⌈最大行高×RenderScaling⌉), HighQuality)` → 未命中转生成通道（2 worker）→ 回 UI 线程：纪元过期或无引用且超预算则释放，否则赋值、登记、`CollectEvictions`，驱逐沿用延迟释放。队列按代次（新优先）再按附加顺序（后附加优先）。缩略图到达不改比例（比例由扫描确定）。`TopLevel.ScalingChanged` 与缩放模式变化 → `Configure` → 已附加卡片重新入队，新图到达前保留旧图。QuickLook 占位图通过 `Acquire` 钉住，高清图走 `LoadPreviewAsync`。
- 测试（Desktop.Tests）：`ByteBudget` 钉住永不驱逐、全钉住时驱逐为空；`[AvaloniaFact]` + 假解码器：过期代次/纪元丢弃、`Release` 后出队、解码回调不在 UI 线程；分档表驱动（325,1.0→384；325,1.5→512；416,3→1024）。
- 验收：钉住字节不超预算时 `ResidentBytes` 不超预算；附加中的卡片不回退占位图；快速滚动时队列长度不超过已实例化卡片数。

### WP2.4 扫描接入、排版引擎与 VM 拆分

- 新增 `Features/Library/Gallery/{JustifiedLayoutEngine, GalleryOrdering, GalleryLayoutViewModel, GallerySelection, LibraryCatalog}.cs`；`LibraryViewModel` 收缩为门面（不超过 350 行）；卡片包装 `LibraryItem` 并暴露 `VideoSource? Video`（`VideoPath` 仅供旧播放器，阶段 3 删除）；`JobFactory` 按 `Kind` 判断适用性、合成用 `PairCandidates`、体积用 `SourceBytes`；检查器删除 `_sizeCache`，`SetScanModeAsync` 改为同步 `SetActionFilter`；删除 `AlbumScanner.cs`、`TimelineGroup.cs`、`GalleryTemplateSelector.cs`、`DisplayGroups`、`ScanModes`。

```csharp
public readonly record struct JustifiedRow(int Start, int Count, double Height, bool IsComplete);
public static class JustifiedLayoutEngine
{
    // 完整行铺满，行高不超过 target×系数；末行取 min(target, 自然高)
    public static JustifiedRow[] Compute(ReadOnlySpan<double> aspects, double width, double targetHeight,
        double spacing, double maxRowHeightFactor = 1.3);
}
public enum GallerySort { DateTaken, DateCreated, DateModified, Name }
public sealed partial class GalleryLayoutViewModel : ObservableObject
{
    public void SetCards(IReadOnlyList<PhotoCardItemViewModel> visible);
    public void SetViewportWidth(double width);            // 120ms 防抖；行组成不变时原地更新尺寸
    public string? AnchorKeyAt(double offsetY); public double OffsetOf(string key);  // 滚动锚定
}
```

- 数据流：`LibraryCatalog.ScanAsync` 在 UI 线程外执行，捕获 IO/权限异常显示错误状态 → HEIC 补全后台逐条更新 → 按动作筛选 → 排序分组 → 排版 → 行 VM（组成不变的行复用）→ 计数由条目实时推导；组标题命令用 `$parent[ListBox]` 绑定；文案在显示时本地化，切换语言刷新；裁决通过后更新计数、徽章与选中；滚动按锚点恢复。
- 测试：排版（铺满误差 ≤0.5px、行高上限、窄视口单张竖图不铺满、空输入、1 万项 <20ms）；三种日期排序互不相同；`SetSortDirection("false")` 为 false；切换动作扫描次数保持 1；宽度变 1px 时 Reset 为 0；裁决后计数/徽章/选中更新。

### WP2.5 收尾

- 设置页：内存预算（64～1024MB）、磁盘缓存上限、占用显示与清理按钮。
- Headless 压测：1 万张卡片自顶到底滚动，`ResidentBytes` 不超过预算 + 钉住字节，无 `ObjectDisposedException`。
- AGENTS.md 4.5 与目录说明同步。

## 风险与实测点

- Avalonia 12.1.2：`DecodeToHeight` 对 JPEG 是否走缩放解码（测峰值）；`ContainerPrepared` 时 `DataContext` 是否已设置；Headless 下缩放比固定为 1，DPI 分档靠纯函数测试。
- Magick.NET：`Thumbnail` 清除 profile，必须先取 ICC；HEIC 只有 nclx 无 ICC 时的色彩处理；Magick OpenMP 线程与 worker 数叠加（必要时 `ResourceLimits.Thread`）。
- Windows Shell 需真机验证：无 HEIF 扩展时 `THUMBNAILONLY` 是否失败、JPEG 是否已转正、系统只有 256px 缓存时的降级。
- HEIC EXIF 拍摄时间需解析 iinf/iloc，成本超预期时降级为文件名。
