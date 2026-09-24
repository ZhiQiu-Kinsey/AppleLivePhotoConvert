using System.Buffers.Binary;
using System.Globalization;

namespace LivePhotoConvert.Core.Media.UltraHdr;

/// <summary>
/// 单通道增益图的元数据（ISO 21496-1 与 Google hdrgm 共用的一组参数）。
/// </summary>
/// <param name="GainMapMin">增益图 0 对应的增益（log2）</param>
/// <param name="GainMapMax">增益图 1 对应的增益（log2）</param>
/// <param name="Gamma">增益图编码的伽马</param>
/// <param name="OffsetSdr">SDR 偏移</param>
/// <param name="OffsetHdr">HDR 偏移</param>
/// <param name="HdrCapacityMin">开始应用增益图的显示余量（log2）</param>
/// <param name="HdrCapacityMax">完整应用增益图的显示余量（log2）</param>
/// <param name="UseBaseColorSpace">在主图色彩空间（如 Display P3）中应用增益</param>
public sealed record GainMapMetadata(
    double GainMapMin,
    double GainMapMax,
    double Gamma,
    double OffsetSdr,
    double OffsetHdr,
    double HdrCapacityMin,
    double HdrCapacityMax,
    bool UseBaseColorSpace)
{
    /// <summary>ISO 21496-1 元数据所在 APP2 段的标识（含结尾 NUL）。</summary>
    internal static ReadOnlySpan<byte> IsoNamespace => "urn:iso:std:iso:ts:21496:-1\0"u8;

    internal const string HdrgmNamespace = "http://ns.adobe.com/hdr-gain-map/1.0/";

    /// <summary>主图中的 ISO 段只声明版本（minimum_version、writer_version 均为 0），参数写在增益图中。</summary>
    internal static ReadOnlySpan<byte> IsoVersionPayload => [0, 0, 0, 0];

    private const byte UseBaseColorSpaceFlag = 0x40;

    /// <summary>
    /// 由 Apple 余量 H 换算：增益图已按 <see cref="AppleGainMapConverter"/> 重编码为 log2(增益)/log2(H)，
    /// 因此增益范围与显示余量都是 [0, log2 H]，伽马 1、偏移 0；Apple 的增益在 Display P3 中计算，保留主图色彩空间。
    /// </summary>
    /// <param name="headroom">线性余量 H，必须大于 1</param>
    public static GainMapMetadata FromAppleHeadroom(double headroom)
    {
        if (!double.IsFinite(headroom) || headroom <= 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(headroom), headroom, "HDR 余量必须大于 1。");
        }

        var stops = Math.Log2(headroom);
        return new GainMapMetadata(0, stops, 1, 0, 0, 0, stops, UseBaseColorSpace: true);
    }

    /// <summary>
    /// ISO 21496-1 二进制元数据（不含命名空间前缀），单通道、正向（基础图为 SDR）。
    /// </summary>
    /// <remarks>
    /// 分数按 libavif / libultrahdr 的连分数算法由单精度值求出，与它们写出的字节一致，便于用参考实现做对照。
    /// </remarks>
    public byte[] ToIsoPayload()
    {
        var payload = new byte[5 + 16 + 40];
        var span = payload.AsSpan();
        // minimum_version、writer_version 为 0；flags 只允许 is_multichannel 与 use_base_colour_space
        span[4] = UseBaseColorSpace ? UseBaseColorSpaceFlag : (byte)0;

        var offset = 5;
        WriteUnsigned(span, ref offset, HdrCapacityMin);
        WriteUnsigned(span, ref offset, HdrCapacityMax);
        WriteSigned(span, ref offset, GainMapMin);
        WriteSigned(span, ref offset, GainMapMax);
        WriteUnsigned(span, ref offset, Gamma);
        WriteSigned(span, ref offset, OffsetSdr);
        WriteSigned(span, ref offset, OffsetHdr);
        return payload;
    }

    /// <summary>
    /// 增益图 JPEG 内的 hdrgm XMP：安卓 14 等只认 XMP 的解码器依赖它，数值与 ISO 段一致。
    /// </summary>
    public string ToGainMapXmp() =>
        "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
        + $"<rdf:Description rdf:about=\"\" xmlns:hdrgm=\"{HdrgmNamespace}\" hdrgm:Version=\"1.0\""
        + $" hdrgm:GainMapMin=\"{Format(GainMapMin)}\" hdrgm:GainMapMax=\"{Format(GainMapMax)}\" hdrgm:Gamma=\"{Format(Gamma)}\""
        + $" hdrgm:OffsetSDR=\"{Format(OffsetSdr)}\" hdrgm:OffsetHDR=\"{Format(OffsetHdr)}\""
        + $" hdrgm:HDRCapacityMin=\"{Format(HdrCapacityMin)}\" hdrgm:HDRCapacityMax=\"{Format(HdrCapacityMax)}\""
        + " hdrgm:BaseRenditionIsHDR=\"False\"/></rdf:RDF></x:xmpmeta>";

    private static string Format(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static void WriteUnsigned(Span<byte> span, ref int offset, double value)
    {
        var (numerator, denominator) = ToUnsignedFraction((float)value, uint.MaxValue);
        BinaryPrimitives.WriteUInt32BigEndian(span[offset..], numerator);
        BinaryPrimitives.WriteUInt32BigEndian(span[(offset + 4)..], denominator);
        offset += 8;
    }

    private static void WriteSigned(Span<byte> span, ref int offset, double value)
    {
        var single = (float)value;
        var (magnitude, denominator) = ToUnsignedFraction(MathF.Abs(single), int.MaxValue);
        var numerator = single < 0 ? -(int)magnitude : (int)magnitude;
        BinaryPrimitives.WriteInt32BigEndian(span[offset..], numerator);
        BinaryPrimitives.WriteUInt32BigEndian(span[(offset + 4)..], denominator);
        offset += 8;
    }

    /// <summary>
    /// 连分数求分母不超过 uint 上限、分子不超过 <paramref name="maxNumerator"/> 的最佳逼近。
    /// </summary>
    internal static (uint Numerator, uint Denominator) ToUnsignedFraction(float value, uint maxNumerator)
    {
        if (float.IsNaN(value) || value < 0 || value > maxNumerator)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "无法表示为 ISO 21496-1 分数。");
        }

        // 与参考实现一致：此处按单精度计算分母上限
        var maxDenominator = value <= 1 ? uint.MaxValue : (ulong)MathF.Floor(maxNumerator / value);
        uint denominator = 1;
        uint previousDenominator = 0;
        uint numerator = 0;
        var remainder = (double)value - MathF.Floor(value);
        for (var iteration = 0; iteration < 39; iteration++)
        {
            var exact = (double)denominator * value;
            if (exact > maxNumerator)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "无法表示为 ISO 21496-1 分数。");
            }

            numerator = (uint)Math.Round(exact, MidpointRounding.AwayFromZero);
            if (exact - numerator == 0.0)
            {
                return (numerator, denominator);
            }

            remainder = 1.0 / remainder;
            var next = previousDenominator + Math.Floor(remainder) * denominator;
            if (next > maxDenominator)
            {
                return (numerator, denominator);
            }

            if (next > uint.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "无法表示为 ISO 21496-1 分数。");
            }

            previousDenominator = denominator;
            denominator = (uint)next;
            remainder -= Math.Floor(remainder);
        }

        return ((uint)Math.Round((double)denominator * value, MidpointRounding.AwayFromZero), denominator);
    }
}
