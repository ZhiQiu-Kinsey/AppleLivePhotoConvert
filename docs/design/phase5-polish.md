# 阶段 5：体验收尾与发布

分支：`…-phase5`。本文件是设计要点与已知问题清单，开工前由主控细化为工作包。

## 产品定位（用户已确定）

- 保持**实况照片专精**：README 与界面文案定位为"实况照片工作台"——浏览、互转、瘦身，并保留 HDR 画质；不往通用相册工具方向扩展。
- **不改名**：工程名、可执行文件、设置与缓存目录保持 `LivePhotoConvert`；改名以后单独评估。
- 平台：正式支持 Windows；Linux/macOS 的现状与缺口（回收站、依赖包、Apple Silicon、签名）写进 README 的平台说明，暂不实现。

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

## 合流后待处理（WP5.2 范围外清单）

阶段 3 与阶段 4 合流时已完成：写死颜色与小于 11px 的文字（PhotoCardControl、ToolsView；CurtainCompareControl 全部取主题 token，"瘦身后"角标改用 `SuccessFillBrush`）、状态胶囊 / 检查器危险选项 / 卡片徽章 / QuickLook 与瘦身对比弹窗正文的对比度 token、悬停修复规则补齐（`engine-path-btn` 已随阶段 4 重写删除，新增测试校验所有自定义悬停底色的按钮类都在规则内）、`ViewStyleRulesTests` 豁免名单移除、侧栏"需要注意"接入依赖页卡片。卡片信息栏改用 11px 后内边距收紧，由测试保证内容放得下固定高度。

剩余：

- 窄窗口下检查器占位偏大：可折叠或按宽度自动收起。

## CI 与发布流程（用户已同意纳入阶段 5）

按优先级：
1. **Linux 真实工具测试**：新增 `ubuntu-latest` 任务，apt 安装 exiftool / ffmpeg / heif-enc（及 libheif-examples 的 heif-dec），源码编译 libultrahdr 的 `ultrahdr_app` 并设置 `LPC_ULTRAHDR_APP`，让 E2E 与 Core 集成测试真正执行；Windows 任务可选用 `ToolInstaller` 按清单安装 win-x64 工具，验证下载、校验与原子安装链路。
2. **速度**：`concurrency: cancel-in-progress`；NuGet 缓存（`setup-dotnet` 的 `cache: true` + `packages.lock.json`）；构建与 AOT 任务共享缓存。
3. **结果呈现**：`--logger trx` 并上传或生成 PR 摘要；coverlet 覆盖率报告作为产物（不设门槛）；界面截图仅在失败或 Desktop 改动时上传。
4. **版本与安全**：升级 checkout / setup-dotnet / upload-artifact 到 Node 24 的大版本；工作流声明最小权限（默认 `contents: read`）；第三方 Action 固定到提交 SHA。
5. **发布加固（release.yml）**：发布前复用 CI 全部检查（AOT 警告视为错误）；生成 SHA256 校验文件与 `actions/attest-build-provenance` 来源证明；Release 说明取自 CHANGELOG 对应段落。仍不自行打 tag。
6. **定时检查依赖清单**：每周校验 `tools.json` 中各源地址可访问、哈希一致。
7. **`.github/dependabot.yml`**：NuGet 与 GitHub Actions 每周更新。

需用户在 GitHub 网页开启（不在代码范围）：main 分支规则集（要求 CI 通过、禁止强推）、Dependabot 安全告警、CodeQL 默认设置、密钥扫描与推送保护、私密漏洞报告、合并方式与自动删除分支、自动合并。
