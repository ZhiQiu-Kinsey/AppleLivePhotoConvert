namespace LivePhotoConvert.Desktop.Features.Library.Thumbnails;

/// <summary>旧版平铺的 960px 缩略图缓存：键与格式都已不兼容，也不受容量上限管理，启动后在后台删除。</summary>
public static class LegacyThumbnailCache
{
    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify),
        "LivePhotoConvert", "cache", "thumbs");

    public static void TryDelete() => TryDelete(DefaultDirectory);

    /// <summary>删除目录；不存在或部分文件被占用时保留剩余内容，下次启动再试。</summary>
    public static bool TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
