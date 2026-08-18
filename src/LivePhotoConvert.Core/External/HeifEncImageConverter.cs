using LivePhotoConvert.Core.Abstractions;
using LivePhotoConvert.Core.Io;

namespace LivePhotoConvert.Core.External;

/// <summary>
/// 基于 heif-enc (libheif) 的高性能 HEIC 图像编码器
/// </summary>
public sealed class HeifEncImageConverter : IImageConverter
{
    private readonly string _executablePath;

    private HeifEncImageConverter(string executablePath) => _executablePath = executablePath;

    /// <summary>
    /// 当前操作系统的 heif-enc 可执行文件名
    /// </summary>
    public static string ExecutableName => OperatingSystem.IsWindows() ? "heif-enc.exe" : "heif-enc";

    /// <summary>
    /// 定位 heif-enc 并创建转换器实例
    /// </summary>
    /// <param name="executablePath">用户显式指定的路径，为空时自动在程序目录、tools 子目录及环境变量 PATH 中定位</param>
    /// <returns>可用的 heif-enc 转换器实例</returns>
    /// <exception cref="FileNotFoundException">未找到 heif-enc 可执行程序</exception>
    public static HeifEncImageConverter Create(string? executablePath = null)
    {
        var path = ToolLocator.Find(ExecutableName, executablePath, "heif-enc", "libheif", "bin", "tools");
        if (path is null)
        {
            throw new FileNotFoundException($"未找到 {ExecutableName} 外部工具。");
        }

        return new HeifEncImageConverter(path);
    }

    /// <inheritdoc />
    public Task ConvertToJpegAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        return MagickImageConverter.Instance.ConvertToJpegAsync(sourcePath, destinationPath, cancellationToken);
    }

    /// <inheritdoc />
    public async Task ConvertToHeicAsync(string sourcePath, string destinationPath, int quality = 65, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // heif-enc -q <quality> <source> -o <destination>
        IReadOnlyList<string> arguments =
        [
            "-q", quality.ToString(),
            sourcePath,
            "-o", destinationPath
        ];

        var result = await ProcessRunner.RunAsync(_executablePath, arguments, cancellationToken);
        if (!result.Success || !File.Exists(destinationPath))
        {
            FileHelper.TryDeleteFile(destinationPath);
            throw new InvalidOperationException($"HEIC 编码失败：{result.StandardError.Trim()}");
        }
    }
}
