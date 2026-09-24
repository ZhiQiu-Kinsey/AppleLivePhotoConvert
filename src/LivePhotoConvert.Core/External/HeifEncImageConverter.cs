using System.Globalization;
using LivePhotoConvert.Core.Abstractions;
using LivePhotoConvert.Core.Io;

namespace LivePhotoConvert.Core.External;

/// <summary>
/// 基于 libheif heif-enc 的 HEIC 编码：保留 EXIF/XMP/ICC，并把 EXIF 方向转换为 HEIF 的 irot 属性。
/// JPEG 输出交给 <see cref="MagickImageConverter"/>。
/// </summary>
public sealed class HeifEncImageConverter : IImageConverter
{
    private readonly string _executablePath;

    private HeifEncImageConverter(string executablePath) => _executablePath = executablePath;

    public static string ExecutableName => OperatingSystem.IsWindows() ? "heif-enc.exe" : "heif-enc";

    /// <exception cref="FileNotFoundException">找不到 heif-enc</exception>
    public static HeifEncImageConverter Create(string? executablePath = null) =>
        new(ToolLocator.Find(ExecutableName, executablePath, "heif-enc", "libheif", "bin")
            ?? throw new ToolNotFoundException(ExecutableName));

    public Task ConvertToJpegAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default) =>
        MagickImageConverter.Instance.ConvertToJpegAsync(sourcePath, destinationPath, cancellationToken);

    public async Task ConvertToHeicAsync(string sourcePath, string destinationPath, int quality, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(quality, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(quality, 100);
        var result = await ProcessRunner.RunAsync(_executablePath, ["-q", quality.ToString(CultureInfo.InvariantCulture), sourcePath, "-o", destinationPath], cancellationToken);
        if (!result.Success || new FileInfo(destinationPath) is not { Exists: true, Length: > 0 })
        {
            FileHelper.TryDeleteFile(destinationPath);
            throw new ImageConversionException($"HEIC 编码失败：{result.ErrorSummary}");
        }
    }
}
