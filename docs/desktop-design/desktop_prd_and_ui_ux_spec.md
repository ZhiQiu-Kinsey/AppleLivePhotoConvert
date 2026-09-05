# LivePhotoConvert 桌面端产品需求文档 (PRD) 与 UI/UX 架构设计规范

**文档版本**：v2.0.0 (Avalonia 12 工业级旗舰定稿版)  
**目标运行时**：.NET 10 (C# 13) Native AOT 单文件发布 (`win-x64-aot`)  
**UI 框架标准**：**Avalonia UI 12 (v12.1.2)**（原生支持 .NET 10、首选 Native AOT 零反射编译、内置 WinUI 3 FluentTheme 与硬件加速 Composition 渲染管线）  
**交互原型设计参照**：[`ui_design_showcase.html`](file:///C:/Users/KinseyQiu/.gemini/antigravity/brain/34c4369b-9a74-45bc-b39e-12c9f9a0ff9c/ui_design_showcase.html)  
**架构与安全审计追溯**：
- [架构与底层引擎审计报告](file:///C:/Users/KinseyQiu/.gemini/antigravity/brain/34c4369b-9a74-45bc-b39e-12c9f9a0ff9c/scratch/audit_architecture_clean.md)
- [UX 体验与数据防灾审计报告](file:///C:/Users/KinseyQiu/.gemini/antigravity/brain/34c4369b-9a74-45bc-b39e-12c9f9a0ff9c/scratch/audit_ux_safety_clean.md)

---

## 1. 产品愿景与设计哲学

### 1.1 核心定位与用户价值
`LivePhotoConvert` 桌面端是面向摄影爱好者、苹果/安卓跨系统换机用户与家庭相册整理者的专业实况照片工坊。
其设计核心在于：**“相册内容为王、纯中文零门槛、严守数据资产安全、人机工学极致流畅”**。

### 1.2 核心体验准则 (UX Principles)
1. **纯粹自然语言界面（No Technical Jargon）**：
   - 界面面向普通消费者与摄影师，**全站移除一切开发或 CLI 英文缩写**（如 `Merge`、`Split -f apple`、`SourceAction`、`MergeOptions` 等）；
   - 全部采用规范、亲切的现代中文术语（如 `苹果 实况转安卓`、`安卓 实况转苹果`、`提取独立封面与视频`、`源文件处理策略`）。
2. **现代 Fluent 开关控件（Toggle Switch Everywhere）**：
   - **全面废除传统生硬的打勾方框（Checkbox）**；
   - 涉及参数配置、路径保留、覆盖保护、全选切换等所有布尔二元操作，一律统一采用 iOS / Windows 11 Fluent 风格的**双态滑块开关（ToggleSwitch）**，手感丝滑，状态清晰。
3. **真实相册画廊与摄影参数精读**：
   - 绝大多数实况照片均为天然匹配，无需在所有卡片展示冗余的“时差公式”；
   - 取而代之的是用户真正关心的核心摄影元数据：**拍摄具体时间、GPS 地理位置（如 云南·大理 洱海）、相机机型与焦段（如 iPhone 16 Pro · 24mm）、有效分辨率（如 4800万像素）**；
   - 仅对小概率发生的超标异常照片亮黄灯预警，辅以一键人工裁决。
4. **多维画廊排版与时间线智能分组**：
   - **排版切换**：支持「📐 原始比例瀑布流（保留 4:3 / 3:4 / 16:9 / 1:1 真实构图）」与「▦ 方形统一网格」；
   - **时间线分组**：支持「按拍摄日期」、「按拍摄月份」、「按拍摄年份」与「连续平铺」快速聚合浏览。
5. **空间瘦身全状态闭环（Full Strip Lifecycle）**：
   - 完整展示瘦身前的**待处理相册分析与 Before/After 卷帘对比**；
   - 执行过程中的**实时流式吞吐进度与已释放空间计时器**；
   - 瘦身完毕后的**空间回收庆贺大卡片与原体积/现体积成果对比**。
6. **无感悬浮即播与极简底栏偏好**：
   - 鼠标悬停卡片即可直接在卡片内循环微动回放实况视频，无需点击，无需弹出阻断模态框；
   - 辅以 **空格键 (Spacebar) QuickLook** 原速高清无损弹窗试播；
   - 左下角常驻收纳「偏好设置」底栏，集成暗色模式与中英双语快速切换。

---

## 2. 核心架构与工作区布局规范

```
+--------------------------------------------------------------------------------------------------+
|  [LP] LivePhotoConvert | 桌面端专业实况照片工坊    [Avalonia 12.1.2 · .NET 10 AOT]        [— 口 X] |
+------------------+---------------------------------------------------------------+---------------+
| [业务模块]       | [方向: 苹果实况转安卓 | 安卓实况转苹果 | 提取独立图影]                | 任务转换控制台|
|                  | [扫描: 2,450 ➔ 就绪: 13 组 | 待定: 1 组 | 安全过滤 ▼]             | (纯中文配置)  |
| ① 实况互转  [14] |---------------------------------------------------------------| 生成文件命名  |
| ② 空间瘦身 [-78%]| [相册实况胶卷 · 14 组 · 428.5 MB] [☑选择] [▷] [⇅ ⌵] [T ⌵] [🝔 ⌵] [···] | [日期+原名]   |
| ③ 依赖引擎   [✓] |---------------------------------------------------------------| 源文件处理策略|
| ④ 批次报告   [1] |  +==========================================================+ | [保留原始文件]|
|                  |  |  [▼] 2026年9月5日 · 今天 (📍云南大理洱海 · 4 组实况)      | |               |
|                  |  |  +-------------+  +-------------+  +------------------+   | | 遇同名文件保护|
|                  |  |  | 洱海飞鸟 4:3|  | 街头人像 3:4|  | 手冲咖啡 1:1     |   | | [开关 自动追加]|
|                  |  |  | 实况 · 2.8秒|  | 实况 · 2.9秒|  | 实况 · 2.4秒     |   | |               |
|                  |  |  | 14:20·洱海  |  | 15:42·洋人街|  | 16:05·古城       |   | | 开始批量转换|
|                  |  |  | 16Pro·48MP  |  | 16Pro·77mm  |  | 15Pro·24MP       |   | | [ 13/14 就绪 ]|
| [底部偏好栏]     |  +==========================================================+ |               |
| [☀浅色|🌙深色]   |---------------------------------------------------------------|               |
| [🇨🇳中 | 🇺🇸EN]   | 输出目录: D:\Photos\Converted     [开关 保持原相册相对层级]     |               |
| ⚙ 偏好设置       | 写入流速: 38.4 MB/s (零堆内存流式追加) 剩余: 4秒                |               |
+------------------+---------------------------------------------------------------+---------------+
```

---

## 3. 微软照片 (Windows 11 Photos) 紧凑工具栏与桌面人机工效

### 3.1 界面控件职责分工标准 (Control Hierarchy Standard)
彻底理清相册卡片勾选与系统参数配置的控件差异，杜绝控件滥用：

| 模块 / 功能项 | 控件形态 | 交互逻辑与视觉规范 |
| :--- | :--- | :--- |
| **画廊照片卡片勾选** | **圆形相册勾选徽标** (`✓` / `○`) | 悬浮浮现半透明微圈，选中时高亮 Fluent 蓝底白勾，遵循原生照片应用习惯 |
| **系统核心配置项** | **Fluent ToggleSwitch 开关** | 用于二元状态控制（如“保留相册子目录层级”、“覆盖同名文件”），手感丝滑 |
| **空间瘦身就地替换** | **ToggleSwitch (琥珀警示色)** | 开启时触发高危阻断模态确认，严禁单向静默生效 |
| **永久物理删除原片** | **ComboBox + 文本密码锁** | 切换至删除原片时触发银行级防灾弹窗，强制键入大写 `DELETE` 解锁 |

---

### 3.2 顶层 32px 紧凑工具栏与全生命周期行为规范

工具条布局结构：
```
[ 相册实况胶卷 · 14 组照片 · 428.5 MB | 已选 14 项 ]    [☑ 选择] [▷] [⇅ ⌵] [T ⌵] [🝔 ⌵] [···]
```

#### 1. `[☑ 选择]` 按钮（多选模式切换开关，非粗暴全选）
- **职责定位**：**模式切换器 (Mode Switcher)**，严格遵循 Windows 11 Photos 规范；
- **普通浏览态（默认）**：卡片右上角勾选徽标默认隐藏，鼠标 Hover 时浮现半透明微圈（`○`）；
- **激活多选态**：按钮呈 Fluent 主题色高亮按下态，所有卡片常驻显示勾选徽标，右上角出现“完成选择”；
- **核心原则**：**点击模式切换绝不清空或覆盖用户此前已挑选的照片集合**。

#### 2. `[▷]` 试播 / 放映按钮（按视口有序微动 / 全屏幻灯片）
- 点击直接对当前屏幕视口内可见的项目按序触发实况微动，杜绝数千张全量并发导致的显存溢出（OOM）；
- 支持选中单张时进入全屏沉浸式连续放映（Slideshow）。

#### 3. `[⇅ ⌵]` 排序下拉菜单
- **依据**：`📅 拍摄日期 (EXIF)`、`📅+ 创建日期`、`📅✏ 修改日期`、`T 名字`；
- **方向**：`↑ 升序`、`↓ 降序`（左侧带圆点槽位互斥指示）。

#### 4. `[T ⌵]` 筛选菜单（视图可见性控制，与勾选解耦）
- **核心整改**：**筛选仅控制视口卡片的 `IsVisible`，严禁擅自清空用户的勾选集合**；
- 选项：`全部照片`、`仅待人工裁决 (1项)`、`仅已选中项目`；
- 激活过滤时，顶栏常驻展示过滤提示条：`正在筛选: 仅待人工裁决 [✕ 清除筛选]`。

#### 5. `[🝔 ⌵]` 视图画幅、缩放与时间线分组菜单（单点全控）
- **第一区段：画幅裁切**
  - `• 🝔 等高 (原始比例)`：严格保持 4:3、3:4、16:9、1:1、9:16 等原生构图画幅，不拉伸、不裁切，以自然瀑布流呈现；
  - `  🝔 方形`：1:1 统一居中裁切规整网格，适合海量快速检视；
- **第二区段：卡片缩放**
  - `• ▦ 小` (4~5 列密排) / `  ⊞ 中等` (3 列标准) / `  ▢ 大` (2 列大图)；
- **第三区段：时间线分组**
  - `• 🗓 按日期分组` / `  📆 按月份汇总` / `  🏛 按年份足迹` / `  📜 连续平铺 (不分组)`。

#### 6. `[···]` 更多操作下拉菜单
- `⚏ 全选 (Ctrl+A)`（仅在当前筛选视图可见集合内全选）；
- `✕ 不选择任何项目 (Esc / Ctrl+D)`；
- `▼ 全部折叠 / ▶ 全部展开分组`；
- `⟳ 刷新相册`（重新热重载元数据）。

---

### 3.3 桌面级人机工程与键盘快捷操作体系

| 快捷键 / 手势 | 操作定义 | 人机工效细节与防护 |
| :--- | :--- | :--- |
| **`Space` (空格)** | **无损 QuickLook 原速试播** | 焦点停留在卡片上按下空格，即刻弹出无损大图原速实况视频层试播，再按空格或 `Esc` 秒级关闭 |
| **`Ctrl+A`** | 全选当前可见照片 | 仅对当前筛选过滤后的可见集合生效，避免误选被隐藏项 |
| **`Esc` / `Ctrl+D`**| 退出多选 / 取消勾选 | 弹窗打开时优先关闭弹窗，画廊聚焦时取消选中 |
| **`Ctrl+O`** | 打开相册选择目录 | 全局快捷调出系统文件夹选择对话框 |
| **`Shift + 单击`** | 区间批量连选 (Range Select) | 单击第 1 项后按住 Shift 单击第 10 项，批量选中两者闭区间内的全部照片 |
| **`↑ ↓ ← →`** | 画廊网格焦点漫游 | 带有 Fluent Focus Visual 焦点外边框指示 |
| **`Delete`** | 移出本次转换队列 | 仅从当前 UI 待办列表移除，严禁触碰物理磁盘源文件 |

---

## 4. Avalonia 12 与 Native AOT 架构设计规范

### 4.1 核心技术栈对齐
- **运行框架**：`Avalonia 12.1.2` + `Avalonia.Themes.Fluent 12.1.2` + `CommunityToolkit.Mvvm 8.4.0`；
- **编译模式**：.NET 10 C# 13 Native AOT 单文件编译 (`PublishProfile=win-x64-aot`)；
- **修剪安全**：全 XAML 启用 `x:CompileBindings="True"`，数据模板强声明 `x:DataType`，JSON 配置统一使用 `System.Text.Json` Source Generator。

### 4.2 5,000+ 海量相册扁平化虚拟化流 (Flattened Virtual Stream)
彻底杜绝双层嵌套 `ItemsControl`（嵌套会彻底击穿 Avalonia 虚拟化，导致 5000+ 照片一次性创建全部 Visual 树导致 OOM 闪退）。
统一采用**扁平化视图模型单流架构**：

```csharp
// 零反射 AOT 抽象项
public interface IGalleryItemViewModel
{
    string Key { get; }
}

public sealed partial class TimelineHeaderItemViewModel : ObservableObject, IGalleryItemViewModel
{
    public required string Key { get; init; }
    public required DateTime GroupDate { get; init; }
    public required string Title { get; init; }
    public required string LocationSummary { get; init; }
    public int PhotoCount { get; set; }
    [ObservableProperty] private bool _isCollapsed;
}

public sealed partial class PhotoCardItemViewModel : ObservableObject, IGalleryItemViewModel
{
    public required string Key { get; init; }
    public required string PhotoPath { get; init; }
    public string? VideoPath { get; init; }
    public double AspectRatio { get; init; } = 4.0 / 3.0;
    [ObservableProperty] private bool _isSelected = true;
    [ObservableProperty] private bool _isVisible = true;
    [ObservableProperty] private Bitmap? _thumbnail;
    public bool HasSuspiciousWarning { get; init; }
    public string? WarningReason { get; init; }
    public bool IsForceAccepted { get; set; }
}
```

Avalonia 12 XAML 单层 `ItemsRepeater` + `DataTemplateSelector`：
```xml
<ScrollViewer HorizontalScrollBarVisibility="Disabled" VerticalScrollBarVisibility="Auto">
    <ItemsRepeater ItemsSource="{CompiledBinding FlattenedDisplayItems}">
        <ItemsRepeater.Layout>
            <StackLayout Spacing="8"/>
        </ItemsRepeater.Layout>
        <ItemsRepeater.ItemTemplate>
            <controls:GalleryTemplateSelector>
                <DataTemplate x:Key="HeaderTemplate" x:DataType="vm:TimelineHeaderItemViewModel">
                    <!-- 时间线折叠标题栏 -->
                </DataTemplate>
                <DataTemplate x:Key="RowTemplate" x:DataType="vm:PhotoGridRowViewModel">
                    <!-- 虚拟化等高行/网格行 -->
                </DataTemplate>
            </controls:GalleryTemplateSelector>
        </ItemsRepeater.ItemTemplate>
    </ItemsRepeater>
</ScrollViewer>
```
*当折叠某一日期组时，仅从 `FlattenedDisplayItems` 中批量移除所辖卡片行，虚拟化保持完整，视口常驻仅保持约 20 个 Visual 节点！*

### 4.3 零堆分配 EXIF 缩略图流式嗅探 (FastExifThumbnailReader)
禁止为海量照片把 4800 万像素的 HEIC/JPG 全图解压进堆内存。直接通过 `stackalloc` 读取前 64KB APP1 标记中的嵌入缩略图：
- 零堆分配提取 `IFD1` 嵌入缩略图；
- 配合 SkiaSharp 亚采样，单张缩略图内存消耗由 150MB 骤降至 60KB，实现秒级加载数千张相册。

### 4.4 悬停微动试播全局单例漂浮播放器 (Singleton Hover Video Overlay)
严禁在每个卡片模板内初始化原生视频播放器。采用**全局单例漂浮播放器宿主**：
1. 鼠标悬停卡片触发 250ms 防抖；
2. 命中后将全局唯一的 `VideoHost` 移动覆盖至该卡片视口区域，发起硬件加速循环微动解码；
3. 鼠标离开立即 `Stop()` 并重置 Direct3D/Vulkan Surface，移回屏幕外；
4. **整个客户端生命周期内永远只维持 1 个硬件视频解码句柄**，彻底杜绝显存泄漏。

---

## 5. 核心引擎契约扩展与数据安全防线 (P0/P1 闭环)

### 5.1 业务引擎契约补全 (Core Contracts Alignment)

#### 1. 单项人工裁决配对白名单 (`MergeOptions.ForceAcceptedPairs`)
针对用户在 UI 上单项放行时差 >3s 的可疑实况照片：
```csharp
// 在 LivePhotoConvert.Core/Models/MergeModels.cs
public sealed record MergeOptions
{
    // ...
    /// <summary>
    /// 人工强制确认配对的白名单对（绕过时差与时长校验）
    /// </summary>
    public IReadOnlySet<MediaPair>? ForceAcceptedPairs { get; init; }
}
```
`MotionPhotoMerger.cs` 在校验阶段优先放行白名单，彻底实现 UI 人工决策到 Core 引擎的直通闭环。

#### 2. 拆分实况支持源文件策略 (`SplitOptions.SourceFileAction`)
补齐 `SplitOptions` 中的 `SourceFileAction` 属性，与 `Merge` / `Strip` 保持一致，杜绝 UI 虚假配置。

#### 3. 空间瘦身只读分析契约 (`MotionPhotoStripper.AnalyzeAsync`)
空间瘦身阶段 ① 需要在不进行任何破坏性转码的前提下，快速估算相册节省体积并驱动发丝级 Before/After 卷帘滑杆：
```csharp
public sealed record StripAnalysisItem(
    string FilePath,
    long OriginalBytes,
    long? EmbeddedVideoBytes,
    bool HasEmbeddedVideo,
    bool NeedsHeicConversion,
    long EstimatedFinalBytes
);

public sealed record StripAnalysisReport(
    IReadOnlyList<StripAnalysisItem> Items,
    long TotalOriginalBytes,
    long EstimatedTotalSavedBytes
);

public async Task<StripAnalysisReport> AnalyzeAsync(
    string inputDirectory,
    bool convertToHeic,
    CancellationToken cancellationToken = default);
```

#### 4. 高并发 UI 进度防雪崩节流器 (`ThrottledUiProgressReporter`)
针对 `Parallel.ForEachAsync` 的高频回调，采用 50ms 缓冲锁汇聚机制，通过 `DispatcherPriority.Background` 批量推送到 Avalonia UI 线程，彻底消除海量并发下的界面假死与卡顿。

---

### 5.2 银行级数据资产防灾安全矩阵

```mermaid
flowchart TD
    A[用户点击开始批量转换] --> B{磁盘剩余容量预检 (Pre-flight ENOSPC Check)}
    B -- 空间不足 (< 1.2倍总需求 + 500MB) --> C[阻断并弹窗警告: 磁盘空间不足]
    B -- 空间充裕 --> D{是否包含源文件破坏性操作?}
    
    D -- 物理删除源文件 --> E[强制弹出红屏防灾弹窗 ➔ 键入大写 DELETE 解锁]
    E --> F[全量批次 100% 成功后统一执行延后批量清理]
    F --> G[优先调用 Windows 回收站 (RecycleBin)]
    F --> H[单次任务后源文件策略自动重置为保留原片]
    
    D -- 空间瘦身就地覆盖 --> I[触发琥珀色阻断弹窗: 提示将永久移除视频流]
    I --> J[底层采用 .livephoto_backup 原图原子暂存 ➔ 校验新图成功后方删备份]
    
    D -- 安全模式 (保留原片) --> K[原子流式写入, 重名自动追加 _1 保护]
```

1. **禁止并发循环内物理删除 (SEC-01)**：
   - 彻底废除 `Parallel.ForEachAsync` 内部单张完成后立刻删除源文件的做法；
   - 必须等待整批任务全量完成、校验通过后，由用户统一确认延后清理；
   - 任务结束后，源文件策略强制重置为“保留原始文件”（One-time Ephemeral Consent）。
2. **就地覆盖原子备份与二次阻断 (SEC-02)**：
   - 开启瘦身“就地覆盖”时，必须弹出高危阻断模态框；
   - 写入前对原图进行 `.livephoto_backup` 原子重命名备份，转码校验成功后方可清理，确保断电零损坏。
3. **磁盘容量预检与孤儿临时目录自愈 (SEC-03)**：
   - 转换启动前执行空间预检：$\text{RequiredSpace} = \text{TotalSourceSize} \times 1.2 + 500\text{MB}$；
   - 客户端每次冷启动时，自动扫描并清理 `%TEMP%/LivePhotoConvert/` 中超期 24 小时的孤儿缓存。

---

## 6. 工程实施与交付路线

- **原型交付物**：[`ui_design_showcase.html`](file:///C:/Users/KinseyQiu/.gemini/antigravity/brain/34c4369b-9a74-45bc-b39e-12c9f9a0ff9c/ui_design_showcase.html)（完整包含 Windows 11 Photos 紧凑工具栏、等高原始比例瀑布流、选择模式解耦、空格键 QuickLook 试播、瘦身卷帘对比与防灾弹窗）。
- **技术栈路线**：
  1. **框架搭建**：基于 `net10.0` 创建 `LivePhotoConvert.Desktop`，引入 `Avalonia 12.1.2` 核心包；
  2. **核心契约扩展**：在 `LivePhotoConvert.Core` 中落地 `ForceAcceptedPairs`、`SplitOptions.SourceFileAction` 与 `Strip.AnalyzeAsync`；
  3. **控件与虚拟化**：实现基于 Avalonia 12 `ItemsRepeater` 的扁平化虚拟相册流与单例悬停微动视频宿主；
  4. **全链路验证**：全量单元测试通过，Native AOT 发布 `LivePhotoConvert-win-x64.zip`。
