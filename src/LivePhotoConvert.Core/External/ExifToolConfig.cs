namespace LivePhotoConvert.Core.External;

/// <summary>
/// ExifTool 用户自定义标签配置
/// </summary>
static class ExifToolConfig
{
    /// <summary>
    /// 配置文件名
    /// </summary>
    private const string FileName = "LivePhotoExif.config";

    /// <summary>
    /// 配置内容
    /// </summary>
    /// <remarks>
    /// 小米相册通过 EXIF 十进制 34967（即 0x8897）标签判断是否为动态照片，
    /// 该标签不是标准标签，必须先在此声明才能写入。
    /// XMP-GCamera 系列标签 ExifTool 已内置支持，无需重复定义。
    /// </remarks>
    private const string Content = """
                                   %Image::ExifTool::UserDefined = (
                                      'Image::ExifTool::Exif::Main' => {
                                          0x8897 => { Name => 'MicroVideo', Writable => 'int8u' },
                                      },
                                   );

                                   # Perl 要求配置文件以真值结尾
                                   1;
                                   """;

    /// <summary>
    /// 确保配置文件已写入磁盘
    /// </summary>
    /// <remarks>
    /// 写到临时目录而不是当前工作目录，后者可能没有写权限，
    /// 也会在用户的工作目录里留下无关文件。
    /// </remarks>
    /// <returns>配置文件的完整路径</returns>
    public static string EnsureCreated()
    {
        var directory = Path.Combine(Path.GetTempPath(), "LivePhotoConvert");
        Directory.CreateDirectory(directory);
        var configPath = Path.Combine(directory, FileName);
        // 内容固定，已存在且一致时不重复写入，避免影响正在运行的其他实例
        if (File.Exists(configPath) && File.ReadAllText(configPath) == Content)
        {
            return configPath;
        }
        File.WriteAllText(configPath, Content);
        return configPath;
    }
}
