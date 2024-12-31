# LivePhotoConvert
[English](/LivePhotoConvert/Docs/README.en.md) 
## 介绍

本程序用于将苹果设备的实况照片转换为小米手机可识别的动态照片格式。苹果的实况照片无法直接在小米手机上使用，因为两者对动态照片的处理方式不同。
本程序通过将照片和视频合并，并添加必要的元数据，使得生成的动态照片能够在小米相册中正确识别和显示。
支持安卓动态照片拆分成照片和视频

## 使用方法

### 依赖项

- [ExifTool](https://exiftool.org/)
- [FFmpeg](https://www.ffmpeg.org/)

### 步骤

1. **准备文件**
   - 下载程序并解压[AppleLivePhotoConvert](https://github.com/ZhiQiu-Kinsey/AppleLivePhotoConvert/releases/tag/1.0)
   - 打开苹果相册，选择所有动态照片，点击分享，选择包含原始数据，并保存到本地，找到文件夹你会发现，动态照片被拆分为照片和mov视频。
   - 将所有数据压缩，并且上传到电脑中，并解压。
   - 打开程序拖拽输入目录到控制台，以及输出目录，开始你的转换吧！。
	 
3. **动态照片制作**
   - 照片文件应为 `.jpg`, `.jpeg`, 或 `.heic` 格式，视频文件应为 `.mov` 或 `.mp4` 格式。
   - 确保照片和视频文件的命名前缀相同，例如 `IMG_0001.heic` 和 `IMG_0001.mov`。

4. **查看结果**

   - 转换完成后，生成的动态照片将保存在指定的输出目录中。
   - 将这些照片传输到小米手机，并在小米相册中查看。

## 原理

### 文件合并

程序将照片文件和视频文件合并成一个文件，照片数据在前，视频数据在后。[安卓动态照片格式](https://developer.android.com/media/platform/motion-photo-format?hl=zh-cn)。

### 元数据添加

为了使小米相册识别动态照片，程序使用 `ExifTool` 添加以下元数据：

- **MicroVideo 标签**: 指示文件中包含视频数据。
- **MicroVideoOffset**: 视频数据在文件中的起始位置。
- **MicroVideoPresentationTimestampUs**: 视频播放的起始时间戳。

此外，程序还设置了一个特殊的 Exif 标签 `0x8897`，这是小米相册识别动态照片的关键。

### 其他处理

- **HEIC 转换**: 如果照片是 `.heic` 格式，程序会将其转换为 `.jpg` 格式，目前还不支持`.heic`格式的动态照片。

## 反编译小米相册APP发现特殊标识

为了确定小米相册识别动态照片的特殊标识，我进行了以下步骤：

1. **反编译小米相册APP**

   使用反编译工具 `jadx-gui` 打开小米相册的 APK 文件。

2. **分析代码**

   在反编译的代码中，找到判断照片是否为动态照片的逻辑。发现小米相册通过读取 Exif 中的特殊标签来判断。
1. <img src=".\LivePhotoConvert\Docs\PixPin_2024-12-19_19-35-11.png" alt="">

3. **发现特殊标签**

   代码中提到的特殊标签是十进制数 `34967`，将其转换为十六进制得到 `0x8897`。

4. **应用特殊标签**

   在程序中使用 `ExifTool` 写入这个特殊标签，最终解决了小米相册无法识别动态照片的问题。
1. 具体的代码实现可以查看 [Program.cs](./Program.cs) 文件。

## 注意事项

- 确保照片和视频文件的命名前缀完全一致，否则程序无法正确匹配文件。
- 请把ExifTool下载后复制到程序根目录。

## 相关链接

- [安卓动态照片格式](https://developer.android.com/media/platform/motion-photo-format?hl=zh-cn)
- [ExifTool 官方网站](https://exiftool.org/)
