# LivePhotoConvert 桌面端设计规范与交互原型

本目录包含 `LivePhotoConvert` 跨平台桌面端（基于 **Avalonia 12.1.2** 与 **.NET 10 Native AOT**）的产品需求文档、架构规范、交互原型与审计报告。

---

## 📁 目录清单

| 文件 / 目录 | 作用说明 |
| :--- | :--- |
| [`ui_design_showcase.html`](./ui_design_showcase.html) | **高保真交互原型**（双击或浏览器打开直接运行，纯中文、Windows 11 Photos 顶栏、等高原始画幅瀑布流、快捷键 QuickLook、空间瘦身三阶段闭环、高危防灾弹窗） |
| [`desktop_prd_and_ui_ux_spec.md`](./desktop_prd_and_ui_ux_spec.md) | **产品需求文档 (PRD) 与 UI/UX 架构设计规范 (v2.0.0)**（工业级定稿，涵盖 Avalonia 12、Native AOT 扁平化虚拟化、Core 契约补齐与银行级防灾矩阵） |
| [`architecture_audit_report.md`](./architecture_audit_report.md) | **架构与底层多媒体引擎深度审计报告**（P0/P1 漏洞定位、白名单契约、轻量只读预估、单例悬停播放器） |
| [`ux_safety_audit_report.md`](./ux_safety_audit_report.md) | **产品体验、人机工学与数据防灾审计报告**（长流程生命周期、多选模式解耦、破坏性操作防灾铁律） |

---

## 🚀 如何体验交互原型

直接在任意现代浏览器（Chrome、Edge、Safari、Firefox）中双击打开 [`ui_design_showcase.html`](./ui_design_showcase.html) 即可体验：
- **键盘快捷键**：
  - <kbd>Space</kbd>（空格键）：秒级呼出 / 关闭全屏沉浸式 QuickLook 原速实况试播浮窗
  - <kbd>Ctrl+A</kbd>：全选可见照片
  - <kbd>Esc</kbd> / <kbd>Ctrl+D</kbd>：退出多选 / 关闭当前弹窗 / 取消勾选
  - <kbd>←</kbd> / <kbd>→</kbd>：在 QuickLook 中切换上一张 / 下一张实况照片
- **Windows 11 Photos 顶层工具栏**：
  - `[☑ 选择]`：切换多选模式（保留当前勾选）
  - `[▷]`：触发当前视口实况微动试播
  - `[⇅ ⌵]`：排序字段与升降序
  - `[T ⌵]`：视口条件筛选（不冲刷选中状态）
  - `[🝔 ⌵]`：等高原始画幅/方形裁切、小/中/大缩放、时间线分组（日期/月份/年份/连续平铺）
  - `[···]`：全选、反选、全部折叠/展开、刷新
- **防灾安全演练**：
  - 切换源文件处理为“删除原片”，体验大写 `DELETE` 密码锁二次防灾
  - 空间瘦身拨动“就地覆盖原照片”，体验琥珀色高危阻断提示
  - 待人工裁决照片点击“双屏比对裁决”，体验同屏首帧比对与强制加入白名单
