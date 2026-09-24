# 调研结论：iPhone HDR 增益图 → Ultra HDR 动态照片（WP3.4）

## 结论

- **正向（HEIC → Ultra HDR 动态照片）可行，采用纯托管组装。** Apple 增益图可按公式换算为 ISO 21496-1 / Google hdrgm。原型（手写 XMP、ISO APP2 与 MPF）经 libultrahdr 解码，与 `ultrahdr_app` 组装结果最大差 0.0039（half-float 舍入），与按 Apple 公式直接合成的真值平均误差 0.009 stop、p99 0.034 stop。追加 MP4 并写 Primary / GainMap / MotionPhoto 三项目录后，libultrahdr、ExifTool 12.76/13.59、gainmap-audit 均判定合法。文件只增大约 3%。
- **反向（Ultra HDR → HEIC 保留增益图）暂不做。** heif-enc 不能写 Apple aux 增益图或 `tmap`；libultrahdr + 打补丁的 libheif 能写 ISO `tmap`，但需重建 HDR、12MP 约 18 秒，且 iOS 是否识别未验证。
- **最大未知**：iOS 18+ 原片是否只写 `tmap` 而不写 Apple aux。上游 libheif 目前取不到 `tmap` 中的增益图。实现时 aux 不存在即降级为 SDR 并在报告中说明，待拿到真机原片再补 `tmap` 读取。

## 关键事实

- 读取：`heif-dec`/`heif-convert --with-aux` 可导出 `urn:com:apple:photo:2020:aux:hdrgainmap`（8-bit 单通道，长宽约为主图 1/2，解码时随 irot 一起旋转）。项目分发的 libheif 1.23.x Windows 包已含 `heif-dec.exe`，不新增依赖。元数据用 ExifTool 读 `Apple:HDRHeadroom`（MakerNote 0x21，m33）、`Apple:HDRGain`（0x30，m48）、`AuxiliaryImageType`、`XMP-HDRGainMap:HDRGainMapVersion`。
- 余量 H（Apple 官方公式）：m33 < 1 时 stops = (m48 ≤ 0.01 ? −20·m48 + 1.8 : −0.101·m48 + 1.601)，否则 stops = (m48 ≤ 0.01 ? −70·m48 + 3 : −0.303·m48 + 2.303)；H = 2^max(stops, 0)。HDR = SDR线性 × (1 + (H − 1)·L(g))，L 为 **Rec.709 反 OETF**（不是 sRGB）。
- 映射：GainMapMin = 0，GainMapMax = log2 H，HDRCapacityMin = 0，HDRCapacityMax = log2 H，Gamma = 1，Offset = 0，useBaseColorSpace = 1（保留 Display P3 ICC）。像素用 256 项查表重编码 g′ = log2(1 + (H − 1)·L(v)) / log2 H（量化误差 ≤ 0.006 stop）；直接照搬 Apple 像素最大误差 0.14～0.21 stop，只调 Gamma 仍有 0.03～0.08 stop。
- 写出：主图段顺序 APP0 → Exif → XMP(hdrgm + Container) → ICC → ISO(仅版本) → MPF；增益图 JPEG 段 XMP(hdrgm 参数) → ISO(完整元数据)。安卓 14 需要 XMP，ISO 与 XMP 两套都写。Directory 中 GainMap 的 Length 必须与 MPF 一致，写入后回读校验。ExifTool 以 `-xmp<=` 改写 XMP 后 MPF 与增益图保留（MPF 偏移是相对的），但 MPF 中主图长度会过时（主流解码器忽略）。

## 风险

1. iOS 18+ 只写 `tmap` 的可能（见结论）。
2. 增益图尺寸不一定整除主图（clap 裁剪），需统一处理。
3. 主图与增益图的旋转/镜像必须一致：两者都用 heif-dec 解码。
4. 小米、三星、旧版 Google 相册对"主图 + 增益图 + 视频"组合的识别需真机验证；按 MicroVideoOffset 从文件尾定位视频的解析器不受影响。

## 实现工作包（WP3.5，纯托管，约 6～8 人日含真机）

元数据读取与 H 计算 → `HeifDecoder`（heif-dec `--with-aux`）→ `AppleGainMapConverter`（查表重编码、Magick 输出灰度 JPEG Q85～90）→ `UltraHdrJpegWriter`（hdrgm XMP ×2、ISO 21496-1 二进制、MPF，流式拼接、回读校验）→ 接入 Merger（生成封面 → 复制 EXIF → 组装 Ultra HDR → 写 MotionPhoto XMP → 拼接 MP4；失败降级 SDR 并写入报告）→ golden 与 E2E 测试（MIT 样片 johncf/apple-hdr-heic）→ 真机验证。

原型与样片：会话草稿目录 `spike-gainmap/`（`apple2iso.py`、`assemble_uhdr.py`、`stats.py`、`jpegseg.py`，样片 `hdr-sample.heic`（MIT）与 heic.digital 样片）。
