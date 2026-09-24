using System.Globalization;
using ImageMagick;
using LivePhotoConvert.Core.Abstractions;
using LivePhotoConvert.Core.Io;

namespace LivePhotoConvert.Core.External;

/// <summary>
/// 基于 libheif heif-dec（旧版名为 heif-convert）的解码：一次导出主图与 Apple HDR 增益图。
/// </summary>
/// <remarks>
/// 主图与增益图必须由同一个解码器解码：两者的 irot/imir/clap 都由 libheif 应用，方向才能保证一致。
/// heif-dec 与 heif-enc 同属 libheif 发行包，不引入新的依赖。
/// </remarks>
public sealed class HeifDecoder : IAppleGainMapDecoder
{
    /// <summary>与 <see cref="MagickImageConverter"/> 生成 JPEG 封面的质量一致。</summary>
    public const int PrimaryJpegQuality = 95;

    /// <summary>主图与增益图宽高比相差超过该比例视为方向不一致，拉伸会错位。</summary>
    private const double MaxAspectDeviation = 0.05;

    private const string PrimaryStem = "primary";

    private readonly string _executablePath;

    private HeifDecoder(string executablePath) => _executablePath = executablePath;

    public string ExecutablePath => _executablePath;

    /// <summary>按优先级排列的可执行文件名：新版 heif-dec，旧版（1.17 及更早）heif-convert。</summary>
    public static IReadOnlyList<string> ExecutableNames { get; } = OperatingSystem.IsWindows()
        ? ["heif-dec.exe", "heif-convert.exe"]
        : ["heif-dec", "heif-convert"];

    /// <summary>
    /// 查找 heif-dec：指定路径 → heif-enc 所在目录（同一个 libheif 包）→ 常规位置。
    /// </summary>
    /// <param name="explicitPath">用户指定的 heif-dec 路径；指定时只检查该路径</param>
    /// <param name="heifEncPath">已知的 heif-enc 路径</param>
    public static string? Find(string? explicitPath = null, string? heifEncPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return ToolLocator.Find(Path.GetFileName(explicitPath), explicitPath);
        }

        if (!string.IsNullOrWhiteSpace(heifEncPath) && Path.GetDirectoryName(Path.GetFullPath(heifEncPath)) is { } directory)
        {
            foreach (var name in ExecutableNames)
            {
                var candidate = Path.Combine(directory, name);
                if (ToolLocator.IsValidTool(candidate))
                {
                    return candidate;
                }
            }
        }

        foreach (var name in ExecutableNames)
        {
            if (ToolLocator.Find(name, null, "heif-enc", "libheif", "bin") is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <exception cref="ToolNotFoundException">找不到 heif-dec</exception>
    public static HeifDecoder Create(string? explicitPath = null, string? heifEncPath = null) =>
        TryCreate(explicitPath, heifEncPath) ?? throw new ToolNotFoundException(ExecutableNames[0]);

    public static HeifDecoder? TryCreate(string? explicitPath = null, string? heifEncPath = null) =>
        Find(explicitPath, heifEncPath) is { } path ? new HeifDecoder(path) : null;

    public async Task<AppleGainMapImages?> DecodeAsync(string heicPath, string outputDirectory, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);
        var primary = Path.Combine(outputDirectory, PrimaryStem + ".jpg");
        // 输出文件名决定辅助图像的命名：<主干>-<辅助类型>.jpg；--no-colons 避免 Windows 文件名中出现冒号
        var result = await ProcessRunner.RunAsync(
            _executablePath,
            ["-q", PrimaryJpegQuality.ToString(CultureInfo.InvariantCulture), "--with-aux", "--no-colons", "--quiet", Path.GetFullPath(heicPath), primary],
            cancellationToken);
        if (!result.Success)
        {
            throw new InvalidOperationException($"heif-dec 解码失败：{result.ErrorSummary}");
        }

        if (new FileInfo(primary) is not { Exists: true, Length: > 0 })
        {
            // 多张顶层图像时 heif-dec 输出 primary-1.jpg、primary-2.jpg，无法确定哪张是主图
            throw new InvalidDataException("HEIC 含多张顶层图像或主图解码为空，不支持保留增益图。");
        }

        var gainMap = Directory.EnumerateFiles(outputDirectory)
                               .FirstOrDefault(path => Path.GetFileName(path) is var name
                                                       && name.StartsWith(PrimaryStem + "-", StringComparison.Ordinal)
                                                       && name.Contains("hdrgainmap", StringComparison.OrdinalIgnoreCase));
        if (gainMap is null)
        {
            return null;
        }

        return new AppleGainMapImages(primary, await UnifyGainMapAsync(primary, gainMap, outputDirectory, cancellationToken));
    }

    /// <summary>
    /// 让增益图的尺寸恰为主图的 1/k（k 取整数）。解码器会把增益图拉伸到主图大小，
    /// 因此这里只处理 clap 裁剪等导致的比例偏差，差 1 像素的舍入不重采样以免模糊。
    /// </summary>
    private static Task<string> UnifyGainMapAsync(string primary, string gainMap, string outputDirectory, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var primaryInfo = new MagickImageInfo(primary);
            var gainMapInfo = new MagickImageInfo(gainMap);
            if (UnifiedGainMapSize(primaryInfo.Width, primaryInfo.Height, gainMapInfo.Width, gainMapInfo.Height) is not { } size)
            {
                return gainMap;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var resized = Path.Combine(outputDirectory, PrimaryStem + "-gainmap-resized.png");
            using var image = new MagickImage(gainMap);
            image.Resize(new MagickGeometry(size.Width, size.Height) { IgnoreAspectRatio = true });
            image.Write(resized, MagickFormat.Png);
            FileHelper.TryDeleteFile(gainMap);
            return resized;
        }, cancellationToken);

    /// <summary>
    /// 计算增益图应有的尺寸；无需调整时返回 <c>null</c>。
    /// </summary>
    /// <exception cref="InvalidDataException">宽高比差异过大（方向不一致）</exception>
    internal static (uint Width, uint Height)? UnifiedGainMapSize(uint primaryWidth, uint primaryHeight, uint gainMapWidth, uint gainMapHeight)
    {
        if (primaryWidth == 0 || primaryHeight == 0 || gainMapWidth == 0 || gainMapHeight == 0)
        {
            throw new InvalidDataException("主图或增益图尺寸为 0。");
        }

        var primaryAspect = (double)primaryWidth / primaryHeight;
        var gainMapAspect = (double)gainMapWidth / gainMapHeight;
        if (Math.Abs(primaryAspect - gainMapAspect) / primaryAspect > MaxAspectDeviation)
        {
            throw new InvalidDataException($"增益图 {gainMapWidth}×{gainMapHeight} 与主图 {primaryWidth}×{primaryHeight} 的方向或比例不一致。");
        }

        var scale = Math.Max(1.0, Math.Round((double)primaryWidth / gainMapWidth));
        var width = (uint)Math.Ceiling(primaryWidth / scale);
        var height = (uint)Math.Ceiling(primaryHeight / scale);
        return Math.Abs((long)width - gainMapWidth) <= 1 && Math.Abs((long)height - gainMapHeight) <= 1 ? null : (width, height);
    }
}
