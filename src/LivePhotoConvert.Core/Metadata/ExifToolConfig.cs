using LivePhotoConvert.Core.Io;

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
    /// <remarks>
    /// 多个实例共用该目录：先写唯一的临时文件再改名覆盖，其它实例启动的 ExifTool 不会读到写了一半的配置。
    /// </remarks>
    public static string EnsureCreated()
    {
        var directory = Path.Combine(Path.GetTempPath(), "LivePhotoConvert");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FileName);
        if (IsCurrent(path))
        {
            return path;
        }

        var temp = Path.Combine(directory, $"{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, Content);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && IsCurrent(path))
        {
            // 目标正被另一实例替换或读取，而内容已经是最新的
        }
        finally
        {
            FileHelper.TryDeleteFile(temp);
        }

        return path;
    }

    private static bool IsCurrent(string path)
    {
        try
        {
            return File.Exists(path) && File.ReadAllText(path) == Content;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
