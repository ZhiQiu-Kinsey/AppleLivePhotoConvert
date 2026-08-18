# LivePhotoConvert (Live & Motion Photo Toolkit)

<p align="center">
  <img src="../src/LivePhotoConvert.Cli/LivePhotoConvert.ico" width="84" height="84" alt="LivePhotoConvert Logo" />
</p>

<p align="center">
  <strong>⚡ High-fidelity bidirectional converter and space optimizer between Apple Live Photos and Android Motion Photos</strong>
</p>

<p align="center">
  <a href="https://dotnet.microsoft.com/download"><img src="https://img.shields.io/badge/.NET-10.0-512BD4?style=flat-square&logo=dotnet" alt=".NET 10" /></a>
  <a href="https://learn.microsoft.com/dotnet/csharp/"><img src="https://img.shields.io/badge/C%23-13.0-239120?style=flat-square&logo=csharp" alt="C# 13" /></a>
  <img src="https://img.shields.io/badge/Platform-Windows%20x64-0078D6?style=flat-square&logo=windows" alt="Platform" />
  <img src="https://img.shields.io/badge/Native%20AOT-Supported-success?style=flat-square" alt="Native AOT" />
  <a href="https://github.com/ZhiQiu-Kinsey/AppleLivePhotoConvert/actions/workflows/ci.yml"><img src="https://img.shields.io/badge/Tests-108%20Passed-success?style=flat-square&logo=githubactions&logoColor=white" alt="Tests" /></a>
  <a href="../LICENSE"><img src="https://img.shields.io/badge/License-MIT-blue.svg?style=flat-square" alt="License" /></a>
</p>

<p align="center">
  <a href="../README.md"><b>简体中文</b></a> • <a href="README.en.md"><b>English</b></a>
</p>

---

## 📖 Table of Contents

- [🔄 Key Features](#-key-features)
- [📱 Conversion Scenarios & Compatibility Matrix](#-conversion-scenarios--compatibility-matrix)
- [📸 Interface Preview](#-interface-preview)
- [🚀 Quick Start Guide](#-quick-start-guide)
  - [1. Download](#1-download)
  - [2. Exporting Live Photos from iPhone](#2-exporting-live-photos-from-iphone)
  - [3. Run the App](#3-run-the-app)
- [💻 Command Line Interface (CLI)](#-command-line-interface-cli)
  - [Common Recipes](#common-recipes)
  - [Subcommands](#subcommands)
  - [Full CLI Options Reference](#full-cli-options-reference)
  - [Exit Codes](#exit-codes)
- [🔬 Technical Architecture & Reverse Engineering](#-technical-architecture--reverse-engineering)
  - [1. Android Motion Photo Binary Layout (GCamera XMP)](#1-android-motion-photo-binary-layout-gcamera-xmp)
  - [2. Xiaomi HyperOS `0x8897` Tag Reverse Engineering](#2-xiaomi-hyperos-0x8897-tag-reverse-engineering)
  - [3. Apple Live Photo UUID Pairing Mechanism](#3-apple-live-photo-uuid-pairing-mechanism)
  - [4. Zero-Allocation & Atomic Reliability](#4-zero-allocation--atomic-reliability)
- [❓ Frequently Asked Questions (FAQ)](#-frequently-asked-questions-faq)
- [🛠️ Building from Source](#️-building-from-source)
- [💖 Acknowledgments & Open Source Libraries](#-acknowledgments--open-source-libraries)
- [📄 License](#-license)

---

## 🔄 Key Features

- 🔄 **Cross-Ecosystem Bidirectional Conversion**:
  - **Merge (`merge`)**: Stitch iPhone Live Photos (`HEIC/JPG` + `MOV`) into single-file Android Motion Photos (`.jpg`), playable across Xiaomi Gallery, Google Photos, and Windows 11 Photos.
  - **Restore (`split -f apple`)**: Split Android Motion Photos and inject paired UUIDs for native Live Photo playback on iOS and macOS.
  - **Unpack (`split -f android`)**: Extract cover images and standalone MP4 micro-videos.
- 🗜️ **Space Optimization (`strip`)**: Strip embedded micro-videos and optionally re-encode to HEIC, saving 60%~96% disk space while preserving 100% of EXIF metadata and filesystem timestamps.
- 🎯 **Smart Pairing & Selfie Correction**: Prioritizes ContentIdentifier UUID matching to eliminate filename shift issues; automatically corrects front-camera selfie mirror orientation.
- ⚡ **Lossless Stream Copy & Fast Startup**: Prioritizes stream copying without re-encoding quality loss; built with .NET 10 Native AOT for instant millisecond cold starts.
- 📥 **Automated Tool Downloads**: Built-in accelerated mirror downloads automatically fetch and configure ExifTool, FFmpeg, and heif-enc on first run.
- 🖥️ **Dual Interaction Modes**: Beautiful color terminal wizard with a Windows native folder picker, plus a comprehensive CLI for scripting and batch workflows.

---

## 📱 Conversion Scenarios & Compatibility Matrix

| Mode / Command | Input Files | Output Files | Supported Platforms & Viewers | Typical Use Case |
| :--- | :--- | :--- | :--- | :--- |
| **Merge (`merge`)** | iPhone export (`HEIC/JPG` + `MOV`) | Single Motion Photo (`MVIMG_*.jpg`) | Xiaomi HyperOS / MIUI Gallery<br>Google Photos<br>Windows 11 Photos<br>Samsung Gallery | Migrating from iPhone to Android, or viewing Live Photos on PC |
| **Apple Restore (`split -f apple`)** | Android Motion Photo (`.jpg/.heic`) | Apple Live Photo (`.jpg/.heic` + `.mov`) | iPhone Photos (iOS)<br>Mac Photos (macOS)<br>iCloud Web | Migrating from Android to iPhone, restoring long-press animation |
| **Android Unpack (`split -f android`)** | Android Motion Photo (`.jpg/.heic`) | Cover Still + Video (`.jpg` + `.mp4`) | Any media player, Premiere, CapCut | Extracting video clips or still covers for editing |
| **Space Optimizer (`strip`)** | Motion Photos / JPEGs | High-Efficiency HEIC (`.heic`) or clean JPG | Systems with HEIC decoding support | Freeing up 60%~96% disk space while keeping EXIF & timestamps |

---

## 📸 Interface Preview

<p align="center">
  <img src="preview.png" alt="Interactive Interface" width="850" />
</p>

---

## 🚀 Quick Start Guide

### 1. Download
Download the latest `LivePhotoConvert-win-x64.zip` portable archive from the [Releases Page](https://github.com/ZhiQiu-Kinsey/AppleLivePhotoConvert/releases) and extract it anywhere.

### 2. Exporting Live Photos from iPhone
To merge iPhone Live Photos into Android Motion Photos, export the **unmodified originals**:
1. Open the **Photos** app on iPhone, select the desired Live Photos;
2. Tap the **Share** button $\rightarrow$ scroll up and tap **Export Unmodified Originals**;
3. Save to Files or transfer to PC via USB Cable / AirDrop / iCloud / Assistant tools;
4. Each Live Photo will export as two matching files (e.g. `IMG_1024.HEIC` + `IMG_1024.MOV`).

### 3. Run the App

Double-click `LivePhotoConvert.exe`:
- **First Launch**: Automatically detects `ExifTool`, `FFmpeg`, and `heif-enc`. If missing, prompts to download and configure them in seconds.
- **Select Task**:
  - Press `1` 【Merge Motion Photos】: Pick input & output directories via the native dialog;
  - Press `2` 【Split Motion Photos】: Choose Android or Apple Live Photo target format;
  - Press `3` 【Space Optimization】: Strip videos and convert to HEIC;
  - Press `4` 【Manage External Tools】: Check or update dependency binaries.

---

## 💻 Command Line Interface (CLI)

Full CLI arguments are supported for automated workflows and batch processing.

### Common Recipes

```bash
# 1. Basic merge: Combine iPhone photos and videos into Android Motion Photos
LivePhotoConvert merge -i "D:\Photos\iPhoneExport" -o "D:\Photos\MotionPhotos"

# 2. Merge and move processed original files quietly
LivePhotoConvert merge -i "D:\Photos\iPhoneExport" -o "D:\Photos\MotionPhotos" -a move -y

# 3. Restore to Apple Live Photos (generates paired MOV and ContentIdentifier UUIDs)
LivePhotoConvert split -i "D:\Photos\MotionPhotos" -o "D:\Photos\AppleLive" -f apple

# 4. Unpack into standalone cover image and MP4 video
LivePhotoConvert split -i "D:\Photos\MotionPhotos" -o "D:\Photos\Extracted" -f android

# 5. Space Optimization: Strip embedded video and convert to HEIC in-place (save 60%~96%)
LivePhotoConvert strip -i "D:\Photos\Camera"

# 6. Space Optimization: Output to a separate folder with custom HEIC quality (75)
LivePhotoConvert strip -i "D:\Photos\Camera" -o "D:\Photos\Optimized" -q 75

# 7. Strip embedded video only (keep JPEG format, do not convert to HEIC)
LivePhotoConvert strip -i "D:\Photos\Camera" --no-heic

# 8. Pre-download and setup external dependencies silently
LivePhotoConvert tools --auto-download
```

### Subcommands

#### 1. `merge` (Combine into Motion Photos)
Scans images (`HEIC`, `JPG`, `PNG`, etc.) and videos (`MOV`, `MP4`, etc.), validates pairings, and stitches them into Android Motion Photos.
```bash
LivePhotoConvert merge [options]
```

#### 2. `split` (Unpack / Restore Motion Photos)
Scans Motion Photos and separates them:
- `-f android` (default): Outputs `.jpg/.heic` + `.mp4`.
- `-f apple`: Outputs `.jpg/.heic` + `.mov` and injects identical `ContentIdentifier` UUIDs.
```bash
LivePhotoConvert split [options]
```

#### 3. `strip` (Space Optimizer & HEIC Re-encoding)
Detects Motion Photos, removes embedded video streams, cleans dynamic tags, and re-encodes images to HEIC via `heif-enc`.
- When `-o / --output` is omitted, operates in **safe in-place atomic mode**.
```bash
LivePhotoConvert strip [options]
```

#### 4. `tools` (External Tools Manager)
Inspects and manages `ExifTool`, `FFmpeg`, and `heif-enc` dependencies.
```bash
LivePhotoConvert tools [options]
```

---

### Full CLI Options Reference

| Option | Short | Applicable Command | Default | Description |
| :--- | :--- | :--- | :--- | :--- |
| `--input <path>` | `-i` | `merge`<br>`split`<br>`strip` | None | Input directory path (opens native folder picker when omitted) |
| `--output <path>` | `-o` | `merge`<br>`split`<br>`strip` | None | Output directory path (`strip` modifies in-place when omitted) |
| `--format <format>` | `-f` | `split` | `android` | Split target format: `android` (extract MP4) or `apple` (iOS Live Photo MOV) |
| `--source-action <mode>` | `-a` / `-s` | `merge` | `keep` | Action for source files on success: `keep`, `move`, `recycle`, `delete` |
| `--no-heic` | | `strip` | `false` | Skip HEIC conversion and only strip embedded video streams |
| `--quality <num>` | `-q` | `strip` | `65` | HEIC compression quality (`1`–`100`, recommended `60-75` for near-lossless ratio) |
| `--skip-validation` / `--no-verify` | | `merge` | `false` | Skip ContentIdentifier & timestamp validation, match solely by filename |
| `--parallel <num>` | `-p` | All | CPU Cores | Number of parallel processing workers (defaults to CPU core count) |
| `--overwrite` | | All | `false` | Overwrite existing files in output directory instead of appending numeric suffixes |
| `--auto-download` | | All | `false` | Automatically download missing ExifTool, FFmpeg, or heif-enc via mirrors |
| `--mirror <URL>` | | All | Built-in | Custom GitHub mirror prefix (e.g. `https://ghfast.top/` or `https://ghproxy.net/`) |
| `--exiftool <path>` | | All | Auto-detect | Explicit path to `exiftool.exe` |
| `--ffmpeg <path>` | | All | Auto-detect | Explicit path to `ffmpeg.exe` |
| `--heif-enc <path>` | | All | Auto-detect | Explicit path to `heif-enc.exe` |
| `--yes` / `--assume-yes` | `-y` | All | `false` | Skip confirmation prompts for automated script execution |

---

### Exit Codes

| Exit Code | Meaning | Description |
| :---: | :--- | :--- |
| **`0`** | Success | All target files were processed successfully |
| **`1`** | Runtime Failure | An unhandled exception or critical error occurred |
| **`2`** | Invalid Arguments | Command-line arguments are missing or malformed |
| **`3`** | User Cancelled | Operation was aborted by user during prompts or dialogs |
| **`4`** | Partial Failure | Completed execution, but some individual files encountered errors |

---

## 🔬 Technical Architecture & Reverse Engineering

### 1. Android Motion Photo Binary Layout (GCamera XMP)

Android Motion Photos follow the [Google Motion Photo Specification](https://developer.android.com/media/platform/motion-photo-format?hl=en), concatenating a JPEG cover image with an MP4 video stream directly into one binary file:
- `GCamera:MicroVideo = 1`: Declares that the file contains an embedded video stream;
- `GCamera:MicroVideoOffset`: Byte length of the video stream from the end of the file;
- `GCamera:MicroVideoPresentationTimestampUs`: Presentation timestamp of the representative still frame (in microseconds).

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

---

### 2. Xiaomi HyperOS `0x8897` Tag Reverse Engineering

During development, photos with only Google standard XMP metadata failed to trigger dynamic playback in Xiaomi Gallery (Xiaomi HyperOS / MIUI).

Decompiling the official Xiaomi Gallery APK via `jadx-gui` revealed the underlying check:

<p align="center">
  <img src="PixPin_2024-12-19_19-35-11.png" alt="Xiaomi Gallery Decompiled Logic" width="750" />
</p>

The decompiled code checks for a specific Exif tag:
- Decimal constant `34967` $\rightarrow$ Hexadecimal **`0x8897`**;
- By injecting this tag via ExifTool during merge, Xiaomi Gallery (HyperOS / MIUI) properly recognizes and plays the Motion Photo.

---

### 3. Apple Live Photo UUID Pairing Mechanism

Apple Live Photos consist of a still image and a QuickTime MOV video linked by a globally unique UUID:
1. **Photo**: Injected as `ContentIdentifier` within MakerNotes or Exif metadata;
2. **Video**: Injected into the QuickTime MOV metadata track under `com.apple.quicktime.content.identifier` alongside `still-image-time = 0`;
3. The `split -f apple` mode automatically assigns and writes paired UUIDs, ensuring imported files are recognized as native Live Photos on iOS and macOS.

---

### 4. Zero-Allocation & Atomic Reliability

- **.NET 10 Native AOT**: Zero JIT overhead, instant startup, minimal memory footprint.
- **Zero-Allocation Sniffing**: Uses UTF-8 byte spans (`"heic"u8`) and bitwise operations to detect magic headers, eliminating GC pauses; leverages `ArrayPool<byte>.Shared` memory rental.
- **Atomic File Reservation (`UniquePath`)**: Concurrency gates guarantee atomic path creation, eliminating name collisions and race conditions.
- **Transactional Rollback**: Operations run in isolated temporary directories and atomically replace targets upon verification; incomplete files are wiped upon cancellation.

---

## ❓ Frequently Asked Questions (FAQ)

### Q1: Why won't Xiaomi Motion Photos play when transferred to iPhone?
> **A**: Android Motion Photos embed MP4 video inside a single JPEG file, which iOS Photos does not support. Use `LivePhotoConvert split -f apple` to convert them into standard Apple Live Photo pairs (`.jpg` + `.mov` with paired UUIDs), then import them via AirDrop, iCloud, or the Photos app.

### Q2: Will converting to HEIC mess up my photo timeline or GPS location?
> **A**: **Not at all**. `LivePhotoConvert` captures original filesystem timestamps (`CreationTime` / `LastWriteTime`) before processing and restores them to the output files. All EXIF metadata (GPS coordinates, camera model, aperture, shutter) is 100% preserved.

### Q3: What if external dependency tools fail to download on first run?
> **A**: Accelerated mirrors are built-in. If your network blocks default mirrors, you can:
> 1. Supply a custom mirror URL: `LivePhotoConvert tools --mirror https://ghfast.top/`
> 2. Or place `exiftool.exe`, `ffmpeg.exe`, and `heif-enc.exe` manually in the application directory.

### Q4: Will moving or deleting source files accidentally delete my standalone videos?
> **A**: **Never**. The validation engine strictly checks that files have matching ContentIdentifiers or a timestamp delta ≤3 seconds and video duration ≤30 seconds. Unpaired videos and regular recordings are never touched.

---

## 🛠️ Building from Source

### Repository Structure
```
AppleLivePhotoConvert/
├── src/
│   ├── LivePhotoConvert.Core/       # Core library: sniffing, binary concatenation, metadata encoding (0 dependencies)
│   └── LivePhotoConvert.Cli/        # CLI & Interactive Spectre.Console frontend, Windows folder picker
├── tests/
│   └── LivePhotoConvert.Core.Tests/ # Automated unit test suite (108+ tests)
├── docs/                            # Documentation, reverse engineering screenshots, English README
└── LivePhotoConvert.slnx            # Modern .NET solution
```

### Build & Test Commands

Requires [.NET 10.0 SDK](https://dotnet.microsoft.com/download) and C# 13:

```bash
# 1. Restore and compile solution
dotnet build LivePhotoConvert.slnx

# 2. Run unit tests
dotnet test LivePhotoConvert.slnx

# 3. Publish Windows x64 Native AOT portable release package
dotnet publish src/LivePhotoConvert.Cli/LivePhotoConvert.Cli.csproj /p:PublishProfile=win-x64-aot -o dist/aot
```

> **Note**: The output directory `dist/aot` contains the standalone executable `LivePhotoConvert.exe` and `Magick.Native-Q8-x64.dll`.

---

## 💖 Acknowledgments & Open Source Libraries

We would like to express our gratitude to the following open-source projects:

- [ExifTool by Phil Harvey](https://exiftool.org/) - Multimedia metadata engine
- [FFmpeg](https://ffmpeg.org/) - Multimedia audio/video processing framework
- [libheif](https://github.com/strukturag/libheif) & [x265](https://www.videolan.org/developers/x265.html) - High-performance HEIF / HEIC codec
- [Magick.NET / ImageMagick](https://github.com/dlemstra/Magick.NET) - Image processing library
- [Spectre.Console](https://github.com/spectreconsole/spectre.console) - Terminal UI and CLI parsing framework for .NET
- [Google Motion Photo Specification](https://developer.android.com/media/platform/motion-photo-format) - Android Motion Photo standard

---

## 📄 License

This project is licensed under the [MIT License](../LICENSE). Issues and Pull Requests are warmly welcomed!
