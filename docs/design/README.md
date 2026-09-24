# 设计文档索引

本目录记录 4.0 版本各阶段的设计要点、已知问题清单与工作包划分，是理解现有实现取舍的参考资料。日常开发的约束以仓库根目录的 [AGENTS.md](../../AGENTS.md) 为准；两者不一致时以 AGENTS.md 和代码为准。

| 阶段 | 文档 | 内容 | 状态 |
| :--- | :--- | :--- | :--- |
| 0 | —（见 [CHANGELOG 4.0.0](../../CHANGELOG.md)） | Core 按 Media / Metadata / Pairing / Pipeline / Services 重写：暂存后原子落盘、源文件保护、托管 XMP 解析、ExifTool 会话池、配对校验纯函数化 | 已完成 |
| 1 | [phase1-desktop-shell.md](phase1-desktop-shell.md) | 桌面基础设施与外壳：依赖注入、本地化单一数据源、弹窗服务、设置存储、任务中心、图库 + 检查器工作台 | 已完成 |
| 2 | [phase2-gallery.md](phase2-gallery.md) | 画廊：统一媒体扫描、等高排版引擎、缩略图分档与磁盘缓存、字节预算与钉住、DPI 感知、sRGB 与方向 | 已完成 |
| 3 | [phase3-playback-hdr.md](phase3-playback-hdr.md) | 实况播放：统一播放器（subfile 直读、rawvideo BGRA、按 PTS 换帧、帧字节预算）、HDR 色调映射预览、HDR 转码保真 | 已完成 |
| 3 | [phase3-gainmap-spike.md](phase3-gainmap-spike.md) | 调研：iPhone HEIC 增益图换算为 Ultra HDR（ISO 21496-1 / hdrgm）的公式、映射与写出顺序 | 已完成（已实现为合成时的「保留 HDR」） |
| 4 | [phase4-strip-tools.md](phase4-strip-tools.md) | 瘦身对比（真实编码产物、1:1 放大镜、抽样预估）与依赖引擎（锁定清单、校验安装、能力探测） | 已完成 |
| 5 | [phase5-polish.md](phase5-polish.md) | 体验收尾：快捷键、拖放、无障碍对比度、任务报告口径、文档与截图、4.0.0 版本 | 已完成 |

## 阅读建议

- 修改某个子系统前，先读对应阶段文档的「已知问题」与「设计要点」，了解当时排除过哪些方案。
- 文档中的行号、类名草案与工作包编号反映的是设计当时的状态，实现可能已调整命名；以代码为准。
- 桌面端早期的原型、PRD 与审计报告（原 `docs/desktop-design/`）描述的是 3.x 的界面与架构，已被本目录取代并从仓库移除，需要时可在 git 历史中查看。
