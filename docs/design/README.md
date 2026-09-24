# LivePhotoConvert 重构设计总览

本目录是 v4 重构的设计与开发文档。每个阶段一份文档，实现者（人或 AI 子代理）必须先读 `AGENTS.md`，再读本文件与对应阶段文档，再动手。

## 1. 目标

- **数据安全第一**：任何路径都不能丢失、截断或覆盖用户原片。
- **画质不降级**：画廊与预览按实际显示像素 × 屏幕缩放解码；HDR 内容保真显示与转换。
- **结构清晰**：Core 纯引擎、Desktop 只做交互；ViewModel 小而专一；无全局单例；本地化单一数据源。
- **可验证**：每个阶段都有单元测试、真实工具集成测试与 Avalonia Headless 界面冒烟测试；CI 在 Windows 上构建、测试并做 Native AOT 发布检查。

## 2. 阶段路线（每阶段一个 PR，分支逐级叠加；互不依赖的工作包在独立工作树并行开发，完成后合入对应阶段分支）

| 阶段 | 分支 | 内容 | 状态 |
|---|---|---|---|
| 0 | `claude/exciting-knuth-n0b189` | Core 重构与数据安全（Media / Metadata / Pairing / Pipeline / Services） | 已完成，PR 待合并 |
| 1 | `…-phase1` | 桌面基础设施与外壳：DI、本地化单一数据源、弹窗服务、设置、任务中心、图库工作台（方案 A） | 已完成，PR 待合并 |
| 2 | `…-phase2` | 画廊：统一媒体扫描、等高排版引擎、缩略图分档与字节预算、DPI 感知、sRGB 与方向 | 进行中（WP2.1、WP2.2 并行） |
| 3 | `…-phase3` | 播放：统一 iOS/安卓播放器（subfile、rawvideo BGRA、PTS、帧预算/环形缓冲）、HDR 色调映射、HDR 转码保真 | 进行中（WP3.1 Core 转码并行） |
| 4 | `…-phase4` | 瘦身对比（真实编码 + 1:1 放大镜）、依赖清单/校验/原子安装/能力探测 | 进行中（WP4.1 Core 并行） |
| 5 | `…-phase5` | 体验收尾：快捷键、拖拽、无障碍对比度、剩余文案、文档与截图、4.0.0 版本 | 待开始 |

## 3. 目标架构

```
LivePhotoConvert.Core            纯引擎，无 UI 依赖（阶段 0 已完成）
  Media / Metadata / Pairing / Pipeline / Services / External / Io / Platform

LivePhotoConvert.Desktop
  App.axaml(.cs)                 组合根：构建 ServiceProvider，初始化主题/语言，创建主窗口
  Program.cs                     全局异常钩子
  Infrastructure/                与具体页面无关的桌面服务
    Localizer                    ILocalizer：两份 XAML 字符串字典为唯一数据源
    SettingsStore                原子写入、防抖保存、版本迁移
    DialogService                IDialogService：await 弹窗结果；Esc 关闭
    FilePicker / ShellLauncher   系统选择器与打开目录/链接，统一容错
    AppLifetime                  关窗确认、取消任务、结束子进程、清理临时目录
  Features/
    Shell/                       主窗口、导航、弹窗宿主
    Library/                     图库工作台：画廊 + 右侧检查器（动作与参数）
    Tasks/                       任务中心：运行中任务 + 历史报告
    Tools/                       依赖引擎页
    Settings/                    偏好设置与关于
    Dialogs/                     各类弹窗
  Controls/ Converters/ Assets/  通用控件、转换器、样式与字符串资源
```

## 4. 实现者守则（所有阶段通用）

1. **只改工作包列出的范围**。发现范围外的问题记录在交付说明里，不要顺手修改。
2. **AOT**：禁止反射；JSON 用 Source Generator；新依赖必须 AOT 兼容；`dotnet publish -r linux-x64 -c Release` 必须 0 警告。
3. **绑定**：所有视图 `x:CompileBindings="True"` + `x:DataType`；界面文案用 `DynamicResource`；零 Emoji，图标用 FluentIcons。
4. **本地化**：新增文案只加在 `Assets/Strings.zh-CN.axaml` 与 `Assets/Strings.en-US.axaml` 两处（阶段 1 起不再有 C# 字典），键集合必须一致，测试会校验。
5. **注释**：只写"为什么"，中文，简洁；不写历史沿革、需求编号或口语化描述。
6. **现代 C#**：主构造函数、集合表达式、`Lock`、`field` 关键字、模式匹配；`<Nullable>enable</Nullable>` 零警告。
7. **验证**：交付前必须 `dotnet build LivePhotoConvert.slnx` 0 警告 0 错误、`dotnet test LivePhotoConvert.slnx` 全绿（环境：`export PATH=$HOME/.dotnet:$PATH`）。
8. **不要提交 git**：由主控审查后统一提交。
9. **交付说明**：列出改动文件、设计取舍、验证结果、遗留问题。
