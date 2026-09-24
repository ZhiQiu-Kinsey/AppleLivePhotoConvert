namespace LivePhotoConvert.Core.Io;

/// <summary>
/// 为不能覆盖已有文件的场景解析可用路径。
/// </summary>
public static class UniquePath
{
    /// <summary>
    /// 返回目录中未被文件或文件夹占用的路径，冲突时依次追加 _1、_2 后缀。
    /// </summary>
    /// <remarks>只做探测不做占位；并发写入必须配合原子操作（如不覆盖的 <see cref="File.Move(string, string)"/>）。</remarks>
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

    public static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
}
