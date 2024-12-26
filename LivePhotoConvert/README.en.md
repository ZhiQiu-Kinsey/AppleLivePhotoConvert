# LivePhotoConvert

## Introduction

This program is designed to convert iPhone Live Photos into a dynamic photo format that can be recognized by Xiaomi phones. iPhone Live Photos cannot be directly used on Xiaomi phones because they handle dynamic photos differently. This program merges the photo and video files and adds necessary metadata to ensure that the resulting dynamic photos are correctly identified and displayed in Xiaomi's gallery.

## Usage

### Dependencies

- [ExifTool](https://exiftool.org/)

### Steps

1. **Prepare Files**
   - Download the program and unzip it[iPhoneLivePhotoConvert](https://github.com/ZhiQiu-Kinsey/AppleLivePhotoConvert/releases/tag/1.0)
   - Open the iPhone Photos app, select all dynamic photos, click share, choose to include original data, and save to your local device. You will find that the dynamic photos are split into photo and MOV video files.
   - Compress all the data and upload it to your computer, then decompress it.
   - Open the program, select the directory, and choose the output directory, then start the conversion process.

3. **Dynamic Photo Creation**
   - The photo file should be in `.jpg`, `.jpeg`, or `.heic` format, and the video file should be in `.mov` or `.mp4` format.
   - Ensure that the photo and video files have the same prefix in their filenames, for example, `IMG_0001.heic` and `IMG_0001.mov`.

4. **View the Results**

   - After the conversion is complete, the dynamic photos will be saved in the specified output directory.
   - Transfer these photos to your Xiaomi phone and view them in Xiaomi's gallery.

## Principles

### File Merging

The program merges the photo file and the video file into a single file, with the photo data at the beginning and the video data at the end. This follows the [Android Dynamic Photo Format](https://developer.android.com/media/platform/motion-photo-format?hl=zh-cn).

### Metadata Addition

To ensure that Xiaomi's gallery recognizes the dynamic photos, the program uses `ExifTool` to add the following metadata:

- **MicroVideo Tag**: Indicates that the file contains video data.
- **MicroVideoOffset**: The starting position of the video data in the file.
- **MicroVideoPresentationTimestampUs**: The starting timestamp for video playback.

Additionally, the program sets a special Exif tag `0x8897`, which is crucial for Xiaomi's gallery to recognize dynamic photos.

### Other Processing

- **HEIC Conversion**: If the photo is in `.heic` format, the program will convert it to `.jpg` format. Currently, it does not support dynamic photos in `.heic` format.

## Reverse Engineering of Xiaomi Gallery APP to Discover Special Identifier

To determine the special identifier used by Xiaomi's gallery to recognize dynamic photos, the following steps were taken:

1. **Reverse Engineering of Xiaomi Gallery APP**

   Use the reverse engineering tool `jadx-gui` to open the Xiaomi Gallery APK file.

2. **Code Analysis**

   In the reverse-engineered code, locate the logic that determines whether a photo is a dynamic photo. It was found that Xiaomi's gallery reads a special tag in the Exif data to make this determination.

3. **Discover Special Tag**

   The special tag mentioned in the code is the decimal number `34967`, which converts to hexadecimal `0x8897`.

4. **Apply Special Tag**

   The program uses `ExifTool` to write this special tag, which ultimately resolves the issue of Xiaomi's gallery not recognizing dynamic photos.

## Notes

- Ensure that the photo and video file prefixes are exactly the same; otherwise, the program may not correctly match the files.
- Please download ExifTool and copy it to the root directory of the program.

## Related Links

- [Android Dynamic Photo Format](https://developer.android.com/media/platform/motion-photo-format?hl=zh-cn)
- [ExifTool Official Website](https://exiftool.org/)
