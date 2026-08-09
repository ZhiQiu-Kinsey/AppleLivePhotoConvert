namespace LivePhotoConvert.Core.Io;

/// <summary>
/// 避免覆盖已有文件的路径解析
/// </summary>
public static class UniquePath
{
    /// <summary>
    /// 在目标目录中获取不冲突的文件路径，重名时依次追加 _1、_2
    /// </summary>
    /// <param name="directory">目标目录</param>
    /// <param name="fileName">文件名</param>
    /// <returns>不冲突的完整路径</returns>
    /// <exception cref="IOException">尝试次数耗尽仍未找到可用文件名</exception>
    public static string Resolve(string directory, string fileName)
    {
        var candidate = Path.Combine(directory, fileName);
        if (!Exists(candidate))
        {
            return candidate;
        }

        var name = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 1; index < int.MaxValue; index++)
        {
            candidate = Path.Combine(directory, $"{name}_{index}{extension}");
            if (!Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException($"无法为 {fileName} 找到不冲突的文件名。");
    }

    /// <summary>
    /// 同名目录同样会让文件写入失败，因此一并视为已占用
    /// </summary>
    /// <param name="path">待检查的路径</param>
    /// <returns>路径是否已被占用</returns>
    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
}
