# LivePhotoConvert (Live & Motion Photo Toolkit)

<p align="center">
  <img src="../src/LivePhotoConvert.Desktop/LivePhotoConvert.ico" width="84" height="84" alt="LivePhotoConvert Logo" />
</p>

<p align="center">
  <strong>⚡ Cross-ecosystem Live Photo workbench: lossless bidirectional conversion between Apple Live Photos and Android Motion Photos, plus batch album space optimization</strong>
</p>

<p align="center">
  <a href="https://dotnet.microsoft.com/download"><img src="https://img.shields.io/badge/.NET-10.0-512BD4?style=flat-square&logo=dotnet" alt=".NET 10" /></a>
  <a href="https://avaloniaui.net/"><img src="https://img.shields.io/badge/Avalonia-12.1-9B4FBA?style=flat-square&logo=avaloniaui&logoColor=white" alt="Avalonia 12" /></a>
  <img src="https://img.shields.io/badge/Platform-Windows%20x64-0078D6?style=flat-square&logo=windows" alt="Platform" />
  <img src="https://img.shields.io/badge/Native%20AOT-Supported-success?style=flat-square" alt="Native AOT" />
  <a href="https://github.com/ZhiQiu-Kinsey/AppleLivePhotoConvert/actions/workflows/ci.yml"><img src="https://img.shields.io/badge/CI-GitHub%20Actions-2088FF?style=flat-square&logo=githubactions&logoColor=white" alt="CI" /></a>
  <a href="../LICENSE"><img src="https://img.shields.io/badge/License-MIT-blue.svg?style=flat-square" alt="License" /></a>
</p>

<p align="center">
  <a href="../README.md"><b>简体中文</b></a> • <a href="README.en.md"><b>English</b></a>
</p>

> [!IMPORTANT]
> Since v2.6 the project has been fully transformed from a command-line tool into an **Avalonia desktop application**: the CLI entry point (`LivePhotoConvert.Cli` and the Spectre.Console interactive menu) has been removed, and all capabilities are now provided by the graphical interface, with the underlying engine distilled into the pure managed `LivePhotoConvert.Core` library.

---

## 📖 Table of Contents

- [🔄 Key Features](#-key-features)
- [📱 Conversion Scenarios & Compatibility Matrix](#-conversion-scenarios--compatibility-matrix)
- [📸 Interface Preview](#-interface-preview)
- [🚀 Quick Start Guide](#-quick-start-guide)
- [🖥️ Desktop Features in Depth](#️-desktop-features-in-depth)
- [🔬 Core Technology & Reverse Engineering](#-core-technology--reverse-engineering)
- [❓ Frequently Asked Questions (FAQ)](#-frequently-asked-questions-faq)
- [🛠️ Project Architecture & Building from Source](#️-project-architecture--building-from-source)
- [💖 Acknowledgments & Open Source Libraries](#-acknowledgments--open-source-libraries)
- [☕ Support the Project](#-support-the-project)
- [📄 License](#-license)

---

## 🔄 Key Features

- 🔄 **Cross-Ecosystem Bidirectional Conversion**
  - **Merge (Live Photo Convert · Apple → Android)**: Stitch iPhone Live Photos (`HEIC/JPG` + `MOV`) into single-file Android Motion Photos (`.jpg`), with long-press playback supported on Xiaomi HyperOS, Google Photos, and Windows 11 Photos.
  - **Restore (Live Photo Convert · Android → Apple)**: Split Android Motion Photos and inject paired UUIDs (`ContentIdentifier`) so that importing them into iPhone / Mac Photos restores native Live Photo behavior.
  - **Unpack (Live Photo Convert · Extract to Files)**: Losslessly extract the static cover and the embedded micro-video from a Motion Photo.
- 🗜️ **Space Optimization**: Batch-strip embedded videos from Motion Photos and optionally re-encode to high-quality HEIC (default quality 90, visually lossless), freeing 60%~96% of storage while preserving capture timestamps and EXIF metadata 100%.
- 🎯 **Smart Pairing & Manual Arbitration**: Matches by `ContentIdentifier` UUID first; suspicious pairs whose timestamp delta exceeds the threshold are pushed into a dual-pane arbitration dialog for human confirmation or splitting.
- 🖼️ **Album-Level Batch Workflow**: Virtualized photo reel (grouped by capture date / month / year), hover live preview, full-screen QuickLook on Space, before/after quality slider comparison.
- 🛡️ **Multi-Layer Safety**: Disk space pre-check (ENOSPC warning), second-confirmation lock for physical deletion, atomic in-place overwrite backup (`.livephoto_backup`), self-healing temp directory cleanup.
- ⚡ **Native AOT Performance**: .NET 10 Native AOT single-file publishing with millisecond cold start; the core engine uses zero-allocation streaming (`ArrayPool` + file pre-allocation) and never loads whole files into the managed heap.
- 🌗 **Light / Dark Theme & Bilingual UI**: Follow the system or switch manually, with a built-in "About" page (version, contributors, license, and referenced projects).
- 📥 **Fully Managed Dependencies**: ExifTool, FFmpeg, and heif-enc are auto-detected on first run, with one-click download from built-in regional accelerated mirrors.

---

## 📱 Conversion Scenarios & Compatibility Matrix

| Mode | Input Files | Output Files | Supported Platforms & Viewers | Typical Use Case |
| :--- | :--- | :--- | :--- | :--- |
| **Merge** (Apple → Android) | iPhone export (`HEIC/JPG` + `MOV`) | Single Motion Photo (`MVIMG_*.jpg`) | Xiaomi HyperOS / MIUI Gallery<br>Google Photos<br>Windows 11 Photos<br>Samsung Gallery | Switching from iPhone to Android, or viewing Live Photos on PC / Android |
| **Restore** (Android → Apple) | Android Motion Photo (`.jpg/.heic`) | Apple Live Photo pair (`.HEIC` + `.MOV`) | iPhone Photos (iOS)<br>Mac Photos (macOS)<br>iCloud Web | Switching from Android to iPhone, restoring long-press animation |
| **Unpack** (Extract to Files) | Android Motion Photo (`.jpg/.heic`) | Still image + video (`.jpg` + `.mp4`) | Any media player, Premiere, CapCut | Extracting the micro-video or the cover for editing |
| **Space Optimization** | Motion Photos / JPEGs | High-quality HEIC (`.heic`) or plain JPG | Any system or device with HEIC decoding | Freeing 60%~96% storage when phone space runs low |

---

## 📸 Interface Preview

<p align="center">
  <img src="preview.png" alt="Main Window" width="850" />
</p>

<details>
<summary><b>More screenshots</b></summary>

| Feature | Screenshot |
| :--- | :--- |
| Live Photo Convert (Light) | ![Convert Light](screenshots/01_convert_light.png) |
| Live Photo Convert (Dark) | ![Convert Dark](screenshots/02_convert_dark.png) |
| Space Optimization | ![Strip](screenshots/03_strip.png) |
| Dependency Engines | ![Engines](screenshots/04_tools.png) |
| Batch Report | ![Report](screenshots/05_report.png) |
| Preferences | ![Settings](screenshots/06_settings.png) |
| About | ![About](screenshots/07_about.png) |

</details>

---

## 🚀 Quick Start Guide

### 1. Download

Grab the latest `LivePhotoConvert-win-x64.zip` portable archive from the 👉 [Releases page](https://github.com/ZhiQiu-Kinsey/AppleLivePhotoConvert/releases), extract it anywhere, and double-click `LivePhotoConvert.exe` (no .NET runtime required — Native AOT single-file publishing).

### 2. Export Live Photos from iPhone

To merge into Android Motion Photos, first export the **unmodified originals**:

1. Open the **Photos** app on iPhone and multi-select the Live Photos you want;
2. Tap the **Share** icon at the bottom-left → swipe up and choose **Export Unmodified Originals**;
3. Save to **Files**, or transfer to your PC via USB / AirDrop / iCloud;
4. Each Live Photo exports as two files sharing the same name (e.g. `IMG_1024.HEIC` and `IMG_1024.MOV`).

### 3. Start Using

1. **First run**: Go to the **Dependency Engines** page; the app auto-detects ExifTool / FFmpeg / heif-enc. If anything is missing, tick "silently auto-fetch" to download through mirrors in one click.
2. **Pick a directory**: On the **Live Photo Convert** page, choose your album directory; the app scans, pairs files, and reports how many are convertible.
3. **Run the conversion**: Switch the direction (Apple → Android / Android → Apple / Extract to Files), tweak output options in the inspector on the right, and execute.
4. **Review the report**: After conversion, open the **Batch Report** page for per-item results; export error logs or force-retry individual items.

---

## 🖥️ Desktop Features in Depth

### Live Photo Convert

- Left photo reel: virtualized long list with grouping and collapsing by capture date / month / year, sorting, multi-select, and hover auto-playback.
- Right inspector: conversion direction, output naming format (original name / date + original name / pure timestamp), source file handling (keep / archive subfolder / recycle bin / permanent delete), HEIC quality, and subdirectory hierarchy preservation.
- Suspicious pair arbitration: candidate pairs whose capture-time delta exceeds 3 seconds automatically pop up a dual-pane verification dialog for same-frame comparison, then are either whitelisted or split by human decision.

### Space Optimization

- Three-stage workflow: pending analysis (with estimated space savings) → live progress (throughput / remaining time) → results summary.
- Before/after "curtain compare" sandbox: drag the divider to pixel-compare the original against the optimized HEIC.
- Supports a safe export directory as well as in-place overwrite (the latter requires passing an amber high-risk confirmation dialog, with atomic `.livephoto_backup` backup guaranteeing zero corruption on power loss).

### Dependency Engines

- Status cards and re-scan for the three engines: ExifTool / FFmpeg / heif-enc.
- GitHub regional accelerated mirror selection, connectivity latency test, and automatic download of missing tools.

### Batch Report

- Success / failure / skipped breakdown with one-click error log export.
- Force-retry individual items or jump back to continue converting.

### Preferences · About

- Theme (light / dark / follow system), language (简体中文 / English), concurrency level.
- **About** tab: version number, author and contributors, quick links to the GitHub repo / Issues / Releases, the full MIT license text, and a clickable list of referenced open-source projects.

---

## 🔬 Core Technology & Reverse Engineering

### 1. Android Motion Photo Storage Mechanism (GCamera XMP)

Android Motion Photos follow the [Google Motion Photo Specification](https://developer.android.com/media/platform/motion-photo-format?hl=en): the cover JPEG and the embedded MP4 video are physically concatenated in one binary (JPEG first, MP4 right after), and markers are injected into the JPEG's XMP metadata region:

- `GCamera:MicroVideo = 1`: declares that the image contains micro-video data;
- `GCamera:MicroVideoOffset`: byte length of the embedded video from the end of the file;
- `GCamera:MicroVideoPresentationTimestampUs`: presentation timestamp of the Live Photo cover frame (microseconds).

```
┌──────────────────────────────────────────────┐
│  JPEG Image Data                             │
│  ├─ SOI / APP1 (EXIF & XMP GCamera Metadata) │
│  └─ Compressed Image Bitstream ...           │
├──────────────────────────────────────────────┤ ◄─── (MicroVideoOffset from EOF)
│  MP4 Video Data                              │
│  ├─ ftyp / moov / mdat                       │
│  └─ H.264 / AAC Bitstream ...                │
└──────────────────────────────────────────────┘
```

### 2. Reverse Engineering the Xiaomi HyperOS `0x8897` Private Tag

During development we found that Motion Photos carrying only the Google standard XMP tags failed to trigger the long-press playback button in Xiaomi Gallery (HyperOS / MIUI). Decompiling the official Xiaomi Gallery APK with `jadx-gui` located the key check:

<p align="center">
  <img src="PixPin_2024-12-19_19-35-11.png" alt="Xiaomi Gallery Motion Photo detection logic (decompiled)" width="750" />
</p>

The decompiled source shows that Xiaomi Gallery reads not only XMP but also a private Exif tag — the code matches the decimal constant `34967` (i.e. **`0x8897`**). This app injects that private tag through ExifTool, so Xiaomi Gallery (HyperOS / MIUI) can properly recognize and play the Motion Photo.

### 3. Apple Live Photo UUID Pairing Mechanism

An Apple Live Photo consists of a still image and a QuickTime MOV video, bound together by a globally unique UUID that the system Photos library strictly validates:

1. **Image side**: inject `ContentIdentifier` (uppercase UUID) into MakerNotes or Exif metadata;
2. **Video side**: write the same UUID into the QuickTime MOV metadata track `com.apple.quicktime.content.identifier`, and sync `creationdate` / `make` / `model` / `software` / `location.ISO6709`;
3. **Restore (Android → Apple)** mode auto-generates a unique UUID and writes it to both sides, so importing into iPhone / Mac Photos registers them as native Live Photos;
4. ⚠️ QuickTime time tags (`CreateDate` etc. in mvhd / mdhd) are stored in UTC — always pass `-api QuickTimeUTC=1` when writing, otherwise a timezone offset is introduced.

### 4. Extreme Performance & Atomic Safety Design

- **.NET 10 Native AOT compilation**: no JIT overhead, millisecond cold start, tiny memory footprint.
- **Zero-allocation format sniffing & memory pooling**: UTF-8 byte slices (`"heic"u8`, `"qt  "u8`) plus bitwise magic-number detection; `ArrayPool<byte>.Shared` rental and file pre-allocation eliminate GC pressure when concatenating large files.
- **Atomic path reservation (`UniquePath`)**: multi-threaded concurrent writes reserve paths with an atomic rename lock, so no same-name overwrite or file corruption can occur.
- **Temp directory & failure rollback**: all conversions complete in the system temp directory and are atomically moved after verification; cancelling or erroring mid-way cleans up partial artifacts, and orphaned temp directories self-heal on startup.

---

## ❓ Frequently Asked Questions (FAQ)

### Q1: Why won't Motion Photos exported from Xiaomi Gallery animate on iPhone?
> **A**: Android Motion Photos embed an MP4 inside a single JPG, a format iOS cannot recognize. Use **Live Photo Convert · Restore (Android → Apple)** to split them into a UUID-paired `.HEIC` + `.MOV`, then import via AirDrop, the Photos app, or iCloud for proper long-press playback.

### Q2: Will converting to HEIC mess up my album timeline or GPS location?
> **A**: **Not at all**. The tool captures the original file's `CreationTime` and `LastWriteTime` before stripping or transcoding and fully restores them afterwards; all EXIF metadata (GPS, camera gear, aperture, shutter) is preserved, so album ordering and map footprints stay 100% intact.

### Q3: Dependency downloads fail on first run?
> **A**: Several regional high-speed mirrors are built in. Switch mirror nodes on the **Dependency Engines** page and click "test connectivity"; alternatively, drop `exiftool.exe`, `ffmpeg.exe`, and `heif-enc.exe` manually into the application directory.

### Q4: When merging, will choosing to move or clean up source files delete my other long videos?
> **A**: **Absolutely not**. A strict pairing validator is built in: a file is only cleaned up when it satisfies "matching ContentIdentifier" or "capture-time delta ≤3 seconds and video duration ≤30 seconds" **and** the merge verification succeeded. Unmatched files and regular long videos are never touched.

---

## 🛠️ Project Architecture & Building from Source

### Repository Layout

```
AppleLivePhotoConvert/
├── src/
│   ├── LivePhotoConvert.Core/       # Core engine: format sniffing, binary concatenation, metadata codec, external tool orchestration (pure managed, AOT-ready)
│   └── LivePhotoConvert.Desktop/    # Avalonia 12 desktop app: MVVM views / view models / services / dialogs
├── tests/
│   ├── LivePhotoConvert.Core.Tests/ # xunit.v3 unit test suite
│   └── LivePhotoConvert.E2E/        # Black-box end-to-end verification suite
├── docs/                            # Screenshots, architecture docs, and this English README
├── Directory.Build.props            # Unified version and language configuration
└── LivePhotoConvert.slnx            # Modern .NET solution file
```

**Module boundaries**:

- `LivePhotoConvert.Core`: a UI-free domain engine — media pairing (`MediaPairMatcher`), merge / split / strip services (`Merger` / `Splitter` / `Stripper`), external tool drivers (ExifTool / FFmpeg / heif-enc), streaming binary IO (`BinaryFile` / `UniquePath`), and typed contract models. No third-party dependency other than `Magick.NET-Q8-x64` (image decoding).
- `LivePhotoConvert.Desktop`: the Avalonia 12.1.2 + CommunityToolkit.Mvvm presentation layer, with enforced compiled bindings (`x:CompileBindings` + explicit `x:DataType`), the System.Text.Json source generator, and Fluent vector icons — fully trimmable for Native AOT.

### Build & Test

Requires the [.NET 10.0 SDK](https://dotnet.microsoft.com/download):

```bash
# 1. Restore dependencies and build the solution
dotnet build LivePhotoConvert.slnx

# 2. Run the full test suite
dotnet test LivePhotoConvert.slnx

# 3. Launch the desktop app
dotnet run --project src/LivePhotoConvert.Desktop/LivePhotoConvert.Desktop.csproj

# 4. Publish a Windows x64 Native AOT single-file portable package
dotnet publish src/LivePhotoConvert.Desktop/LivePhotoConvert.Desktop.csproj /p:PublishProfile=win-x64-aot -o dist/aot
```

> The published output is a standalone `LivePhotoConvert.exe` plus the `Magick.Native-Q8-x64.dll` native image library; just copy them to any Windows 10/11 x64 machine and run.

---

## 💖 Acknowledgments & Open Source Libraries

Sincere thanks to the following excellent open-source tools, frameworks, and standards (the same list is viewable in-app under **Settings → About**, with direct links to each project homepage):

**Runtime engines & built-in libraries**

- [ExifTool by Phil Harvey](https://exiftool.org/) - The industry-standard media metadata read/write engine
- [FFmpeg](https://ffmpeg.org/) - Leading multimedia audio/video processing framework
- [libheif](https://github.com/strukturag/libheif) & [x265](https://www.videolan.org/developers/x265.html) - High-performance HEIF / HEIC codec (`heif-enc` is built on them)
- [Magick.NET / ImageMagick](https://github.com/dlemstra/Magick.NET) - Powerful .NET image processing library

**Frameworks & specifications**

- [.NET 10](https://dotnet.microsoft.com/) - Cross-platform runtime and Native AOT toolchain
- [Avalonia UI](https://avaloniaui.net/) - Cross-platform XAML desktop UI framework
- [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) - MVVM source generators and messaging infrastructure
- [FluentIcons.Avalonia](https://github.com/davidxuang/FluentIcons) - Fluent Design vector icon library
- [Google Motion Photo Specification](https://developer.android.com/media/platform/motion-photo-format) - Official Android Motion Photo format spec

---

## ☕ Support the Project

If this project helped you recover cross-ecosystem Live Photo memories or reclaim a meaningful chunk of storage, feel free to buy the author a coffee — every bit of support keeps maintenance going!

<p align="center">
  <img src="sponsor-qrcode.png" alt="ZhiQiu's Sponsor QR Code" width="300" />
</p>

<p align="center">
  <sub>Scan with WeChat to sponsor · "Thanks for the support!"</sub>
</p>

You can also support the project in other ways: drop a Star ⭐, file an [Issue](https://github.com/ZhiQiu-Kinsey/AppleLivePhotoConvert/issues), open a [Pull Request](https://github.com/ZhiQiu-Kinsey/AppleLivePhotoConvert/pulls), or simply recommend the tool to more people.

---

## 📄 License

This project is licensed under the [MIT License](../LICENSE). Issues and Pull Requests are warmly welcomed!
