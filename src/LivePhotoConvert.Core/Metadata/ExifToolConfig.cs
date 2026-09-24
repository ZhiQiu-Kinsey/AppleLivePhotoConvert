namespace LivePhotoConvert.Core.Metadata;

/// <summary>
/// ExifTool 用户自定义标签配置。
/// </summary>
internal static class ExifToolConfig
{
    private const string FileName = "LivePhotoExif.config";

    /// <summary>
    /// 小米相册除 XMP 外还依据 EXIF 0x8897 判断动态照片，该标签不在 ExifTool 标准表中，需要先声明才能写入。
    /// </summary>
    private const string Content = """
                                   %Image::ExifTool::UserDefined = (
                                      'Image::ExifTool::Exif::Main' => {
                                          0x8897 => { Name => 'MicroVideo', Writable => 'int8u' },
                                      },
                                   );
                                   1;
                                   """;

    /// <summary>
    /// 把配置写到临时目录（程序目录可能不可写），内容未变时不重复写入。
    /// </summary>
    public static string EnsureCreated()
    {
        var directory = Path.Combine(Path.GetTempPath(), "LivePhotoConvert");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path) || File.ReadAllText(path) != Content)
        {
            File.WriteAllText(path, Content);
        }

        return path;
    }
}
