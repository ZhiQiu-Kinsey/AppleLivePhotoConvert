namespace LivePhotoConvert.Core.Abstractions;

/// <summary>
/// 从 HEIC 解码出的主图与 Apple HDR 增益图。
/// </summary>
/// <param name="PrimaryPath">主图 JPEG（已按 irot/imir/clap 转正，EXIF 方向为 1）</param>
/// <param name="GainMapPath">单通道增益图，方向与主图一致，宽高比与主图一致</param>
public sealed record AppleGainMapImages(string PrimaryPath, string GainMapPath);

/// <summary>
/// 解码 HEIC 的主图与 Apple HDR 增益图辅助图像（urn:com:apple:photo:2020:aux:hdrgainmap）。
/// </summary>
public interface IAppleGainMapDecoder
{
    /// <summary>
    /// 把主图与增益图解码到 <paramref name="outputDirectory"/>。
    /// </summary>
    /// <returns>源中没有 Apple 增益图（包括只有 ISO tmap 增益图）时返回 <c>null</c></returns>
    /// <exception cref="InvalidOperationException">解码失败</exception>
    /// <exception cref="InvalidDataException">主图与增益图的方向或比例无法统一</exception>
    Task<AppleGainMapImages?> DecodeAsync(string heicPath, string outputDirectory, CancellationToken cancellationToken = default);
}
