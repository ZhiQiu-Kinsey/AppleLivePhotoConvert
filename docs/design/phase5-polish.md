# 阶段 5：体验收尾与发布

分支：`…-phase5`。本文件是设计要点与已知问题清单，开工前由主控细化为工作包。

## 内容

- **快捷键**（`KeyBindings`）：Ctrl+A 全选、Esc 取消选择/关闭弹窗、Ctrl+O 选择相册、F5 重新扫描、Enter 开始当前动作、Space 预览（QuickLook）、方向键在 QuickLook 中切换。工具栏上的快捷键提示与实际绑定保持一致。
- **拖拽**：图库空状态与画廊区域支持拖入文件夹（`DragDrop` + `IStorageItem`）。
- **记忆**：选择器 `SuggestedStartLocation` 使用上次目录（阶段 1 已持久化窗口状态与视图偏好）。
- **无障碍与主题**：`TextMutedBrush` 在浅色主题约 2.6:1、深色约 3.0:1，用于 10～11px 正文达不到 WCAG AA，调整 token；检查所有写死颜色已改为主题 token；最小字号不低于 11px。
- **剩余文案与零 Emoji 复查**。
- **进度体验**：批处理吞吐与剩余时间（阶段 1 任务中心已实现）在长任务下的准确性复核。
- **文档**：README（中英）与 AGENTS.md **重写**（不是修补）：优化表达与布局、适当简化，可自由调整结构、增删章节（例如 README 加入功能对比表、快速上手、常见问题；AGENTS.md 收敛为架构速览 + 必守规则 + 协议要点 + 工作流，删除与 docs/design 重复的细节）；CHANGELOG `## [4.0.0]`；用 Avalonia Headless 截图更新 `docs/screenshots/`（浅色/深色、中/英）；删除 `docs/preview.png`（已移除 CLI 的截图）或更新引用。
- **版本**：`Directory.Build.props` 升到 4.0.0（`AssemblyVersion`/`FileVersion` 同步）。**不要自行打 tag**，由用户决定发布时机。
- **真机验证清单**（交付时附在 PR 中）：iPhone 实况 → 小米/Google 相册识别；安卓动态照片 → 导入 iPhone 识别为实况；就地瘦身替换与回收站；Windows Shell 缩略图；HDR 观感；Magick HEIC 编码可用性。

## 阶段 1 遗留

- 导航按钮悬停时 Fluent 模板的背景覆盖 `nav-btn.selected`，当前页悬停后变灰底。
- `Button.mini-segmented-btn` 等无引用样式清理（扫描 Styles.axaml 中未被使用的 Class）。
- 任务报告口径统一：指标卡"异常/跳过"含跳过，摘要行"异常 N 项"不含；取消的任务"处理总量"只统计已处理项，应显示计划总数。
- 合成时配对校验阶段跳过的项一开始就计入已完成，前几秒吞吐偏高。
- Core 返回的跳过/失败原因是中文字面量（如"不是动态照片"），英文界面原样显示：Core 改为返回原因码 + 参数（`OutcomeReason` 枚举），由桌面端本地化；`TaskReportViewModel` 整批失败原因在创建时固化，改为显示时本地化。
- 截图审查：英文标题栏副标题被 `MaxWidth=160` 截断；竖图卡片文件名被"Pair Locked"徽章挤压；关于页深色副标题对比度偏低；QuickLook 无缩略图时弹窗尺寸随大图到达变化导致导航按钮跳动。
