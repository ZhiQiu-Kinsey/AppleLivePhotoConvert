namespace LivePhotoConvert.Core.Tests;

/// <summary>
/// 测试用的临时目录，释放时整体删除
/// </summary>
sealed class TempDirectory : IDisposable
{
    /// <summary>
    /// 创建临时目录
    /// </summary>
    public TempDirectory() => Root = Directory.CreateTempSubdirectory("lpc-test-").FullName;

    /// <summary>
    /// 目录完整路径
    /// </summary>
    public string Root { get; }

    /// <summary>
    /// 在目录中创建一个文件
    /// </summary>
    /// <param name="name">文件名，可含子目录</param>
    /// <param name="content">文件内容，为空时创建空文件</param>
    /// <returns>文件完整路径</returns>
    public string CreateFile(string name, byte[]? content = null)
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content ?? []);
        return path;
    }

    /// <summary>
    /// 组合出目录下的路径
    /// </summary>
    /// <param name="parts">路径片段</param>
    /// <returns>完整路径</returns>
    public string Combine(params string[] parts) => Path.Combine([Root, .. parts]);

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception)
        {
            // 测试临时目录删不掉不影响结果
        }
    }
}
