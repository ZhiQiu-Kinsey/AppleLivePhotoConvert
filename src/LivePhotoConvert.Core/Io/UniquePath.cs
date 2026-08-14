namespace LivePhotoConvert.Core.Io;

/// <summary>
/// 避免覆盖已有文件的路径冲突解析与多线程原子占位服务
/// </summary>
public static class UniquePath
{
    /// <summary>
    /// 当调用方未提供独占锁时的内部备用并发同步锁
    /// </summary>
    private static readonly Lock DefaultGate = new();

    /// <summary>
    /// 在目标目录中解析不冲突的文件路径（若存在同名文件或同名目录，依次追加 _1、_2 后缀）
    /// </summary>
    /// <param name="directory">目标目录</param>
    /// <param name="fileName">期望的文件名（含扩展名）</param>
    /// <returns>不发生冲突的完整文件路径</returns>
    /// <exception cref="IOException">尝试 21 亿次后仍未找到可用文件名</exception>
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
    /// 在线程安全锁保护下原子解析可用文件名并立即创建 0 字节占位文件，彻底杜绝并发任务竞争同一文件名
    /// </summary>
    /// <param name="directory">目标目录</param>
    /// <param name="fileName">期望文件名（含扩展名）</param>
    /// <param name="overwrite">是否允许直接覆盖（若为 true 则直接返回目标路径而不创建占位文件）</param>
    /// <param name="gate">可选的外部并发锁；若未提供则使用内部默认锁</param>
    /// <returns>已占位的绝对文件路径</returns>
    public static string ReserveAtomic(string directory, string fileName, bool overwrite = false, Lock? gate = null)
    {
        if (overwrite)
        {
            return Path.Combine(directory, fileName);
        }

        lock (gate ?? DefaultGate)
        {
            var path = Resolve(directory, fileName);
            try
            {
                File.Create(path).Dispose();
                return path;
            }
            catch
            {
                // 创建占位文件发生不可预期异常时清理残留
                FileHelper.TryDeleteFile(path);
                throw;
            }
        }
    }

    /// <summary>
    /// 原子解析并成对占位两个输出文件（如拆分时的照片与视频，确保使用相同序号后缀）
    /// </summary>
    /// <remarks>
    /// 在并发拆分场景下，照片和视频必须成对使用同一个文件名序号（如 IMG_0001_1.jpg 与 IMG_0001_1.mov），
    /// 不能各自单独计算序号。本方法在临界区内一次性探测两者的占用状态并同时创建占位文件。
    /// 若在创建第二个文件时发生异常，将自动回滚清理已创建的第一个文件。
    /// </remarks>
    /// <param name="directory">目标目录</param>
    /// <param name="baseName">不含扩展名的基础文件名</param>
    /// <param name="photoExtension">照片扩展名（含句点，如 .jpg）</param>
    /// <param name="videoExtension">视频扩展名（含句点，如 .mov / .mp4）</param>
    /// <param name="overwrite">是否允许直接覆盖</param>
    /// <param name="gate">可选的外部并发锁；若未提供则使用内部默认锁</param>
    /// <returns>成对已占位的绝对文件路径元组 (PhotoPath, VideoPath)</returns>
    /// <exception cref="IOException">尝试次数耗尽仍未找到可用成对文件名</exception>
    public static (string PhotoPath, string VideoPath) ReservePairAtomic(string directory, string baseName, string photoExtension, string videoExtension, bool overwrite = false, Lock? gate = null)
    {
        if (overwrite)
        {
            return (Path.Combine(directory, $"{baseName}{photoExtension}"), Path.Combine(directory, $"{baseName}{videoExtension}"));
        }

        lock (gate ?? DefaultGate)
        {
            for (var index = 0; index < int.MaxValue; index++)
            {
                var suffix = index == 0 ? string.Empty : $"_{index}";
                var photoPath = Path.Combine(directory, $"{baseName}{suffix}{photoExtension}");
                var videoPath = Path.Combine(directory, $"{baseName}{suffix}{videoExtension}");

                // 两个文件名均不能被现有文件或目录占用
                if (Exists(photoPath) || Exists(videoPath))
                {
                    continue;
                }

                // 立即占位，避免并行写入冲突；若第二文件占位失败则回滚第一文件
                try
                {
                    File.Create(photoPath).Dispose();
                    File.Create(videoPath).Dispose();
                    return (photoPath, videoPath);
                }
                catch
                {
                    FileHelper.TryDeleteFile(photoPath);
                    FileHelper.TryDeleteFile(videoPath);
                    throw;
                }
            }

            throw new IOException($"无法为 {baseName} 找到不冲突的成对输出文件名。");
        }
    }

    /// <summary>
    /// 判断指定路径是否已被同名文件或同名文件夹占用（同名目录同样会导致文件写入失败）
    /// </summary>
    /// <param name="path">待检查的路径</param>
    /// <returns>若已被文件或目录占用返回 <c>true</c></returns>
    public static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
}
