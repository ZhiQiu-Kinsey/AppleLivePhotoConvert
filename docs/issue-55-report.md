# 修复报告：安卓动态照片转为苹果实况后无法调整封面

**Issue**：https://github.com/ZhiQiu-Kinsey/AppleLivePhotoConvert/issues/55
**PR**：https://github.com/ZhiQiu-Kinsey/AppleLivePhotoConvert/pull/56
**分支**：`fix/issue-55-still-image-time-track`
**日期**：2026-11-23

---

## 一、问题现象

小米 15 Ultra 拍摄的动态照片，经「转为苹果」后导入 iPhone：

- 能被正确识别为**实况照片**，长按可以正常播放；
- 但进入编辑 → 实况按钮后，**封面选择框无法拖动**，无法「设为关键照片」。

## 二、根本原因

还原出的 `.MOV` 缺少 Apple 的 **still-image-time 元数据轨道**。

### 轨道的作用

iOS 靠这条轨道知道视频里哪一帧是封面。它是 handler 为 `meta`、编码格式为 `mebx` 的独立轨道，内含一条 105 字节的样本，再由 `edts/elst` 编辑列表把样本的显示时间推迟到封面时刻。

### 实测对比

**iPhone 原片 `IMG_0259.MOV`**（11 条轨道）：

```
Track11  handler='meta'  format='mebx'  timescale=600
  stsd/mebx/keys/keyd = mdta com.apple.quicktime.still-image-time (dtyp=int8s)
                      + live-photo-still-image-transform
                      + ...-transform-reference-dimensions (1920 1440)
  elst 两条：空编辑 664 units（media_time=-1）→ 1 unit 样本
  tkhd duration = 665 units  →  封面帧在 1.1067 秒
  样本值 0xFF（即 -1），真实时间由样本显示时间 + 编辑列表表达
```

**转换产物**：

```
Track1  vide  1008x1344  首帧 PTS=0
Track2  soun
—— 没有第三条轨道 ——
grep -c mebx = 0    grep -c still-image-time = 0
```

### 为什么会丢

1. FFmpeg 只能映射音视频流（`-map 0:v:0 -map 0:a:0?`），重封装时该轨道必然丢失，上游也没有写入它的能力；
2. `Keys:StillImageTime` 不是 ExifTool 可写标签，靠写标签补不回来（2.6.0 曾尝试过并移除）。

配对本身是正常的：`moov/meta` 的 keys/ilst 形状正确，ContentIdentifier 与 HEIC 的 MakerNote `0x0011` 一致——这解释了为什么能识别、能播放，只是不能编辑封面。

### 附带发现的第二处丢失

安卓原片 XMP 中记录了真实封面时刻：

```
GCamera:MotionPhotoPresentationTimestampUs = 1039066
```

转为苹果时这个值被完全丢弃了（`ImageLayout` 只暴露 `Video` 与 `HasGainMap`）。即使补上轨道，也需要先把它取出来写进去。

## 三、改动内容

新增 `Core/Media/MovStillImageTrack.cs`（721 行，纯托管、无新依赖、AOT 兼容）：

1. 解析顶层 box，要求恰好一个 `mdat` 与一个 `moov`，且两者不重叠；
2. 在 `mdat` 尾部追加 105 字节样本（封面标记 `-1`、恒等 3×3 透视变换、参考尺寸取视频 `stsd` 编码尺寸）；
3. 构造新轨道 `tkhd + edts/elst + mdia(mdhd/hdlr/minf/gmhd/gmin/dinf/stbl)`，`keys` 表写三条键，同步 `mvhd` 的 `nextTrackID`；
4. 修正 `moov` 内既有 `stco/co64` 的块偏移（流式搬运，不整文件读入内存，缓冲经 `ArrayPool` 归还）；
5. 回读校验轨道时刻与预期一致后，经 `OutputCommitter` 暂存路径原子替换。

**执行顺序是关键**：必须在 ExifTool 写完标签之后执行。ExifTool 重写文件会把 `moov` 移到 `mdat` 之前，先前算好的块偏移随即失效。

**失败不致命**：结构不支持时返回 `Unsupported`，条目仍然成功，只在报告中注明「未能写入封面时间轨道，iPhone 上无法调整封面」（中英文资源均已补齐）。

### 改动文件

| 文件 | 说明 |
| :--- | :--- |
| `Core/Media/MovStillImageTrack.cs` | 新增，721 行 |
| `Core/Media/MotionPhotoLayout.cs` | `ImageLayout` 增加 `PresentationTimestampUs` |
| `Core/Services/MotionPhotoSplitter.cs` | 接入轨道写入，失败时记录附注 |
| `Core/Pipeline/BatchReport.cs` | 新增 `OutcomeNoteKind.StillImageTrackUnavailable` |
| `Desktop/Features/Tasks/OutcomeTexts.cs` + 两份 Strings | 附注文案映射 |
| `tests/Core.Tests/Media/MovStillImageTrackTests.cs` | 新增，12 个测试 |
| `tests/Core.Tests/Services/MotionPhotoSplitterTests.cs` | 断言失败时报告附注 |
| `tests/E2E/Workflows/ConversionWorkflowTests.cs` | `ToApple` 用例断言产物轨道时刻 |
| `README.md` / `docs/README.en.md` / `AGENTS.md` / `CHANGELOG.md` | 文档同步 |

合计 14 个文件，+1063 / −11。

## 四、验证结果

### 轨道结构与 iPhone 原片逐字节对比

用真实样片转换后，取新轨道与原片 Track11 逐字节比较：

```
两者均为 781 字节，仅 26 字节不同
差异全部落在应当不同的字段：创建时间、轨道号、时长、时间刻度、样本偏移
结构字段（tkhd 标志与矩阵、elst、mdhd、hdlr、gmhd/gmin、dinf、stsd、keys、stts/stsc/stsz）完全一致
```

### ExifTool 读取

```
[Track3]  HandlerType         : NRT Metadata
[Track3]  HandlerDescription  : Core Media Metadata
[Track3]  StillImageTime      : -1
[Track3]  TrackDuration       : 1.04
[Track3]  MediaDuration       : 0.001
```

与原片 Track11 的形态一致。

### FFmpeg 读取

```
Stream #0:0  Video: hevc (hvc1)  1008x1344
Stream #0:1  Audio: pcm_s16le
Stream #0:2  Data: none (mebx)          ← 新增
```

`-f null -` 完整解码无错，退出码 0。

### 封面时刻

源 XMP `1039066 µs` → 写入后 `1039000 µs`（按影片时间刻度 1000 取整）。超出视频时长时收敛到最后一帧之前，避免指向不存在的画面。

### 真机验证

用小米 15 Ultra 拍摄的动态照片转换后导入 iPhone：**封面选择框已可正常拖动**，问题解决。

### 自动化测试

| 范围 | 结果 |
| :--- | :--- |
| 新增 `MovStillImageTrackTests` | 12 通过 |
| Core.Tests（排除既有 FFmpeg 失败项） | 687 通过 / 23 跳过 |
| Desktop.Tests | 814 通过 / 1 跳过 |
| E2E | 4 通过 |
| 规则测试（字符串键、零 Emoji、样式、对比度） | 20 通过 |
| AOT / 裁剪 / 单文件分析器 | 0 警告 |

新增测试覆盖：轨道写入、块偏移正确性（逐块校验样本字节未变）、时刻收敛、重复调用返回 `AlreadyPresent`、无视频轨、无 mvhd、纯 MOV、4 种长度的截断文件、畸形 tkhd。

## 五、CI 状态说明

PR 的 CI 结果：**Windows 两个任务全部通过**，Linux 任务失败。

### Linux 失败与本次改动无关

关键证据是时间线：

| 日期 | 运行 | Linux 任务 | CI 上的 FFmpeg |
| :--- | :--- | :--- | :--- |
| 10-19 | dependabot PR | 通过 | 6.1.1-3ubuntu5 |
| 10-26 | main | 通过 | — |
| **11-02** | dependabot PR（无业务代码改动） | **失败** | **8.0.1-3ubuntu2** |
| 11-09 ~ 11-16 | 全部 dependabot PR | 失败 | 8.0.x |
| 11-23 | 本 PR | 失败 | 8.0.x |

**从 11 月 2 日起，上游每一个 PR 的 Linux 任务都失败**，包括纯依赖升级的 PR。`ubuntu-latest` 镜像更新后，`apt install ffmpeg` 从 6.1.1 跳到了 8.0.1。

11-02 那次失败的用例与本次 PR 高度重合（`FfmpegVideoConverterTests` 7 项 + `VideoStreamProbeTests` 1 项），说明是同一个根因。

### 根因：`-autorotate` 的选项位置语义变了

`FfmpegVideoConverter.BuildTranscodeArguments` 中：

```csharp
arguments.AddRange(["-autorotate", "0"]);
```

FFmpeg 6 里这是**输入选项**；从 7.0 起变为**输出选项**，且两种位置都会报错。本地 FFmpeg 7.0 复现出完全相同的报错：

```
Option autorotate (automatically insert correct filter) cannot be applied to output url 0
-- you are trying to apply an input option to an output file or vice versa.
Error opening output files: Invalid argument
```

实测四种写法：

| 写法 | 结果 |
| :--- | :--- |
| `-autorotate 0 -i in` | 失败（当前代码） |
| `-i in ... -autorotate 0 out` | 失败 |
| `-autorotate=0` | 不认识该选项 |
| `-noautorotate -i in` | **正常** |

这解释了为什么所有**需要转码**的用例都挂（`HdrSource_*`、`SdrTranscode_*`、`RotatedSource_*`、`ConvertToMp4_ForcedTranscodeOfHdr_*` 都走转码分支）。

**这是真实的产品缺陷**：`ForceTranscode` 与 `BakeOrientation`（镜像前置摄像头视频）路径在 FFmpeg 7+ 下不可用。日常「转为苹果」只做流复制，不受影响。

本地复现：`FfmpegVideoConverterTests` + `VideoStreamProbeTests` 共 17 项，6 项失败，全部是转码路径。用 `git stash` 撤掉本次改动后同样失败，确认与本次改动无关。

### 另外两项 Linux 特有的失败

- `HevcSource_IsStreamCopiedAndTaggedHvc1`（2 个）：FFmpeg 8 对 HLG 流的探测输出格式有变化，`Assert.True(after.IsHdr)` 失败。本地 FFmpeg 7.0 通过。
- `ProbeAsync_SubfileInPathWithSpecialCharacters_ReadsEmbeddedVideo`：期望 `bt709` 实际 `null`，同样是探测输出格式差异。

### 跳过数

CI 的 `-MaxSkipped 7`，本次实际 10（Core 7 + Desktop 3）。Desktop 的 3 项是新增的 HDR 色调映射用例，在 Linux 上因缺 zscale/tonemap 而跳过——同样是 11-02 起就存在的现象（那次也是 7 + 3 = 10），与本次改动无关。

## 六、建议（未实施）

按你的要求，我只做了排查，没有修改以下内容：

1. **修复 `-autorotate`**：改为 `-noautorotate`，两行改动。建议按 FFmpeg 主版本分支处理以兼容 6.x。
2. **适配 FFmpeg 8 的探测输出**：`HevcSource` 与 `ProbeAsync_Subfile` 两条断言需要跟随新格式调整。
3. **跳过阈值**：Linux 的 `-MaxSkipped` 需从 7 调整到 10（或给新增的 HDR 用例加平台条件）。
4. **CI 固定 FFmpeg 版本**：目前用 `apt install ffmpeg` 跟随镜像漂移，建议锁定版本，避免上游工具升级导致 CI 突然变红。

## 七、本地环境备注

- 本机未安装 .NET SDK（只有运行时 10.0.15），验证时下载了官方 10.0.400 解压到临时目录使用。
- 本机缺 Windows SDK 的库（`advapi32.lib` 所在目录不存在），Native AOT 原生链接无法完成；改用框架依赖发布验证功能，行为一致。CI 的 AOT 任务已通过，可覆盖这一项。
