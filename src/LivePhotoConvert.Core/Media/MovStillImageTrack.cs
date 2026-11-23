using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using LivePhotoConvert.Core.Io;
using LivePhotoConvert.Core.Pipeline;

namespace LivePhotoConvert.Core.Media;

/// <summary>
/// 写入 Apple 封面时间轨道的结果。
/// </summary>
public enum StillImageTrackResult
{
    /// <summary>已写入轨道。</summary>
    Written,

    /// <summary>视频已带 still-image-time 轨道，未改动。</summary>
    AlreadyPresent,

    /// <summary>文件结构不支持（mdat/moov 数量异常、偏移越界或回读不一致），文件未改动。</summary>
    Unsupported
}

/// <summary>
/// 为普通 MOV 追加 Apple 实况照片的封面时间轨道（handler <c>meta</c>、格式 <c>mebx</c>）。
/// </summary>
/// <remarks>
/// iOS 用这条单样本轨道定位封面帧：轨道里只有一条 <c>com.apple.quicktime.still-image-time</c> 记录，
/// 由编辑列表把它的显示时间推迟到封面时刻。FFmpeg 只能映射音视频流，重封装后该轨道必然丢失；
/// 缺它时照片仍被识别为实况照片、也能播放，但编辑界面无法拖动封面选择框。
/// 轨道结构与 Apple 原片一致：105 字节样本追加进 mdat，moov 内既有的 stco/co64 按 mdat 位移统一修正。
/// </remarks>
public static class MovStillImageTrack
{
    /// <summary>moov 通常只有几十 KB；超过上限视为异常文件，避免把畸形数据整体读入内存。</summary>
    private const int MaxMoovBytes = 32 * 1024 * 1024;

    /// <summary>块偏移表条目上限，防止畸形计数导致长时间循环。</summary>
    private const int MaxChunkEntries = 4 * 1024 * 1024;

    /// <summary>105 字节样本：still-image-time、恒等变换与参考尺寸三条记录。</summary>
    internal const int SampleLength = 105;

    internal const string StillImageTimeKey = "com.apple.quicktime.still-image-time";
    private const string StillImageTransformKey = "com.apple.quicktime.live-photo-still-image-transform";
    private const string StillImageTransformDimensionsKey = "com.apple.quicktime.live-photo-still-image-transform-reference-dimensions";

    private const uint Moov = 0x6D6F6F76; // "moov"
    private const uint Mdat = 0x6D646174; // "mdat"
    private const uint Trak = 0x7472616B; // "trak"
    private const uint Mvhd = 0x6D766864; // "mvhd"
    private const uint Tkhd = 0x746B6864; // "tkhd"
    private const uint Edts = 0x65647473; // "edts"
    private const uint Elst = 0x656C7374; // "elst"
    private const uint Mdia = 0x6D646961; // "mdia"
    private const uint Mdhd = 0x6D646864; // "mdhd"
    private const uint Hdlr = 0x68646C72; // "hdlr"
    private const uint Minf = 0x6D696E66; // "minf"
    private const uint Gmhd = 0x676D6864; // "gmhd"
    private const uint Gmin = 0x676D696E; // "gmin"
    private const uint Dinf = 0x64696E66; // "dinf"
    private const uint Dref = 0x64726566; // "dref"
    private const uint Alis = 0x616C6973; // "alis"
    private const uint Stbl = 0x7374626C; // "stbl"
    private const uint Stsd = 0x73747364; // "stsd"
    private const uint Stts = 0x73747473; // "stts"
    private const uint Stsc = 0x73747363; // "stsc"
    private const uint Stsz = 0x7374737A; // "stsz"
    private const uint Stco = 0x7374636F; // "stco"
    private const uint Co64 = 0x636F3634; // "co64"
    private const uint Mebx = 0x6D656278; // "mebx"
    private const uint Keys = 0x6B657973; // "keys"
    private const uint Keyd = 0x6B657964; // "keyd"
    private const uint Dtyp = 0x64747970; // "dtyp"
    private const uint Udta = 0x75647461; // "udta"

    /// <summary>Apple 原片 gmin 的内容：图形模式、不透明色与平衡值，逐字节照抄以与原片一致。</summary>
    private static readonly byte[] GraphicsMode = [0, 0, 0, 0, 0, 0x40, 0x80, 0x00, 0x80, 0x00, 0x80, 0x00, 0, 0, 0, 0];

    private static readonly byte[] IdentityMatrix = U32(0x00010000, 0, 0, 0, 0x00010000, 0, 0, 0, 0x40000000);

    /// <summary>
    /// 把封面时间写入 MOV 的元数据轨道；成功时原地替换文件。
    /// </summary>
    /// <param name="path">已由 FFmpeg 重封装、且 ExifTool 标签已写完的 MOV</param>
    /// <param name="stillTimeUs">封面帧在视频中的时间（微秒）</param>
    /// <param name="cancellationToken">取消令牌</param>
    public static async Task<StillImageTrackResult> AddAsync(string path, long stillTimeUs, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(stillTimeUs);

        // 写入完成后要覆盖原文件，源文件句柄必须在此之前关闭（Windows 上被占用的文件不能替换）
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1, FileOptions.RandomAccess);
        var boxes = ReadTopLevelBoxes(source);
        var moovs = boxes.Where(box => box.Type == Moov).ToList();
        var mdats = boxes.Where(box => box.Type == Mdat).ToList();
        if (moovs.Count != 1 || mdats.Count != 1 || moovs[0].Size is < 8 or > MaxMoovBytes)
        {
            return StillImageTrackResult.Unsupported;
        }

        var moov = moovs[0];
        var mdat = mdats[0];
        // 长度 0 的 mdat 会延续到文件末尾并吞掉 moov；两者重叠时按原样搬移会破坏样本数据
        if (mdat.Offset + mdat.Size > moov.Offset && mdat.Offset < moov.Offset + moov.Size)
        {
            return StillImageTrackResult.Unsupported;
        }

        var moovBytes = new byte[(int)moov.Size];
        source.Position = moov.Offset;
        source.ReadExactly(moovBytes);
        if (moovBytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(StillImageTimeKey)) >= 0)
        {
            return StillImageTrackResult.AlreadyPresent;
        }

        if (!TryReadMovieHeader(moovBytes, out var header) || !TryReadTracks(moovBytes, out var trackId, out var width, out var height))
        {
            return StillImageTrackResult.Unsupported;
        }

        var stillUnits = ClampStillUnits(stillTimeUs, header.Timescale, header.Duration);
        var payloadLength = mdat.Size - mdat.HeaderLength;
        var prefixLength = boxes.Where(box => box.Type is not (Moov or Mdat)).Sum(box => box.Size);
        // mdat 总长超过 32 位时改写为 64 位长度头；此时前缀里其它 box 的偏移不变，只是样本位置后移
        var headerLength = mdat.HeaderLength == 16 || payloadLength + 8 > uint.MaxValue ? 16 : 8;
        var payloadOffset = prefixLength + headerLength;
        var sampleOffset = payloadOffset + payloadLength;
        if (sampleOffset > uint.MaxValue)
        {
            return StillImageTrackResult.Unsupported;
        }

        var delta = payloadOffset - (mdat.Offset + mdat.HeaderLength);
        if (!TryShiftChunkOffsets(moovBytes, 8, moovBytes.Length, delta))
        {
            return StillImageTrackResult.Unsupported;
        }

        var track = BuildTrack(trackId + 1, stillUnits, sampleOffset, header.CreationTime, header.Timescale, width, height);
        var newMoov = RebuildMovie(moovBytes, track, trackId + 1);

        var staging = OutputCommitter.CreateStagingPath(Path.GetDirectoryName(Path.GetFullPath(path))!, ".MOV");
        try
        {
            await using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
                try
                {
                    foreach (var box in boxes.Where(box => box.Type is not (Moov or Mdat)))
                    {
                        await CopyRangeAsync(source, output, box.Offset, box.Size, buffer, cancellationToken);
                    }

                    await output.WriteAsync(BuildMdatHeader(headerLength, payloadLength + SampleLength), cancellationToken);
                    await CopyRangeAsync(source, output, mdat.Offset + mdat.HeaderLength, payloadLength, buffer, cancellationToken);
                    await output.WriteAsync(BuildSample(width, height), cancellationToken);
                    await output.WriteAsync(newMoov, cancellationToken);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            var expectedUs = stillUnits * 1_000_000d / header.Timescale;
            if (TryReadStillImageTimeUs(staging) is not { } actual || Math.Abs(actual - expectedUs) > 1.5)
            {
                return StillImageTrackResult.Unsupported;
            }

            await source.DisposeAsync();
            File.Move(staging, path, overwrite: true);
            return StillImageTrackResult.Written;
        }
        finally
        {
            FileHelper.TryDeleteFile(staging);
        }
    }

    /// <summary>
    /// 读取 MOV 中封面时间轨道的时刻（微秒）；没有该轨道时返回 <c>null</c>。
    /// </summary>
    public static long? TryReadStillImageTimeUs(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.RandomAccess);
            var moov = ReadTopLevelBoxes(stream).Where(box => box.Type == Moov).ToList() is [var only] ? only : default;
            if (moov.Size is < 8 or > MaxMoovBytes)
            {
                return null;
            }

            var moovBytes = new byte[(int)moov.Size];
            stream.Position = moov.Offset;
            stream.ReadExactly(moovBytes);
            if (!TryReadMovieHeader(moovBytes, out var header) || header.Timescale == 0)
            {
                return null;
            }

            var key = Encoding.UTF8.GetBytes(StillImageTimeKey);
            foreach (var trak in ChildBoxes(moovBytes, 8, moovBytes.Length).Where(box => box.Type == Trak))
            {
                var start = (int)trak.Offset + trak.HeaderLength;
                var end = (int)(trak.Offset + trak.Size);
                if (moovBytes.AsSpan(start, end - start).IndexOf(key) < 0)
                {
                    continue;
                }

                foreach (var edts in ChildBoxes(moovBytes, start, end).Where(box => box.Type == Edts))
                {
                    foreach (var elst in ChildBoxes(moovBytes, (int)edts.Offset + edts.HeaderLength, (int)(edts.Offset + edts.Size)).Where(box => box.Type == Elst))
                    {
                        var body = (int)elst.Offset + elst.HeaderLength;
                        var elstEnd = (int)(elst.Offset + elst.Size);
                        if (body + 12 > elstEnd || BinaryPrimitives.ReadUInt32BigEndian(moovBytes.AsSpan(body + 4)) == 0)
                        {
                            continue;
                        }

                        var duration = moovBytes[body] == 1
                            ? BinaryPrimitives.ReadUInt64BigEndian(moovBytes.AsSpan(body + 8))
                            : BinaryPrimitives.ReadUInt32BigEndian(moovBytes.AsSpan(body + 8));
                        return (long)Math.Round(duration * 1_000_000d / header.Timescale, MidpointRounding.AwayFromZero);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            return null;
        }

        return null;
    }

    /// <summary>封面时刻必须落在视频时长内：超出时写进去的是视频里不存在的画面。</summary>
    private static long ClampStillUnits(long stillTimeUs, uint timescale, ulong duration)
    {
        var units = (long)Math.Round(stillTimeUs * (double)timescale / 1_000_000d, MidpointRounding.AwayFromZero);
        var maximum = duration > 0 ? (long)duration - 1 : long.MaxValue;
        return Math.Clamp(units, 0, Math.Max(0, maximum));
    }

    private static List<BoxSpan> ReadTopLevelBoxes(Stream stream)
    {
        var boxes = new List<BoxSpan>();
        long position = 0;
        while (position + 8 <= stream.Length)
        {
            if (IsoBox.ReadHeader(stream, position) is not { } header)
            {
                break;
            }

            var size = header.Size == 0 ? stream.Length - position : header.Size;
            if (size < header.HeaderLength || position + size > stream.Length)
            {
                break;
            }

            boxes.Add(new BoxSpan(position, size, header.Type, header.HeaderLength));
            position += size;
        }

        return boxes;
    }

    /// <summary>枚举内存中 [start, end) 内相邻的 box，遇到畸形结构即停止。</summary>
    private static List<BoxSpan> ChildBoxes(ReadOnlySpan<byte> data, int start, int end)
    {
        var boxes = new List<BoxSpan>();
        var position = start;
        while (position + 8 <= end)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(data[position..]);
            var headerLength = 8;
            if (size == 1)
            {
                if (position + 16 > end)
                {
                    break;
                }

                var large = BinaryPrimitives.ReadUInt64BigEndian(data[(position + 8)..]);
                if (large > int.MaxValue)
                {
                    break;
                }

                size = (long)large;
                headerLength = 16;
            }
            else if (size == 0)
            {
                size = end - position;
            }

            if (size < headerLength || position + size > end)
            {
                break;
            }

            boxes.Add(new BoxSpan(position, size, BinaryPrimitives.ReadUInt32BigEndian(data[(position + 4)..]), headerLength));
            position += (int)size;
        }

        return boxes;
    }

    private static bool TryReadMovieHeader(ReadOnlySpan<byte> moov, out MovieHeader header)
    {
        header = default;
        foreach (var box in ChildBoxes(moov, 8, moov.Length))
        {
            if (box.Type != Mvhd)
            {
                continue;
            }

            var body = (int)box.Offset + box.HeaderLength;
            var boxEnd = (int)(box.Offset + box.Size);
            if (body + 20 > boxEnd)
            {
                return false;
            }

            if (moov[body] == 1)
            {
                if (body + 32 > boxEnd)
                {
                    return false;
                }

                header = new MovieHeader(
                    BinaryPrimitives.ReadUInt32BigEndian(moov[(body + 20)..]),
                    BinaryPrimitives.ReadUInt64BigEndian(moov[(body + 24)..]),
                    BinaryPrimitives.ReadUInt32BigEndian(moov[(body + 4)..]));
            }
            else
            {
                if (body + 20 > boxEnd)
                {
                    return false;
                }

                header = new MovieHeader(
                    BinaryPrimitives.ReadUInt32BigEndian(moov[(body + 12)..]),
                    BinaryPrimitives.ReadUInt32BigEndian(moov[(body + 16)..]),
                    BinaryPrimitives.ReadUInt32BigEndian(moov[(body + 4)..]));
            }

            return header.Timescale > 0;
        }

        return false;
    }

    /// <summary>
    /// 读取最大轨道号与视频轨道的编码尺寸；参考尺寸必须取视频轨而不是照片，Apple 原片即如此。
    /// </summary>
    private static bool TryReadTracks(ReadOnlySpan<byte> moov, out int maxTrackId, out int width, out int height)
    {
        maxTrackId = 0;
        width = 0;
        height = 0;
        foreach (var trak in ChildBoxes(moov, 8, moov.Length).Where(box => box.Type == Trak))
        {
            var start = (int)trak.Offset + trak.HeaderLength;
            var end = (int)(trak.Offset + trak.Size);
            var children = ChildBoxes(moov, start, end);
            foreach (var tkhd in children.Where(box => box.Type == Tkhd))
            {
                var body = (int)tkhd.Offset + tkhd.HeaderLength;
                // 版本字节必须先落在 box 内，畸形 tkhd 不能越界读取
                if (body >= end)
                {
                    continue;
                }

                var idOffset = body + (moov[body] == 1 ? 20 : 12);
                if (idOffset + 4 <= end)
                {
                    maxTrackId = Math.Max(maxTrackId, (int)BinaryPrimitives.ReadUInt32BigEndian(moov[idOffset..]));
                }
            }

            if (width > 0)
            {
                continue;
            }

            foreach (var mdia in children.Where(box => box.Type == Mdia))
            {
                var mdiaStart = (int)mdia.Offset + mdia.HeaderLength;
                var mdiaEnd = (int)(mdia.Offset + mdia.Size);
                var mdiaChildren = ChildBoxes(moov, mdiaStart, mdiaEnd);
                var isVideo = false;
                foreach (var handler in mdiaChildren.Where(box => box.Type == Hdlr))
                {
                    // hdlr 的 handler type 位于版本/标志之后 8 字节
                    var type = (int)handler.Offset + handler.HeaderLength + 8;
                    var handlerEnd = (int)(handler.Offset + handler.Size);
                    isVideo |= type + 4 <= handlerEnd && type + 4 <= mdiaEnd && moov[type..].StartsWith("vide"u8);
                }

                if (!isVideo)
                {
                    continue;
                }

                foreach (var minf in mdiaChildren.Where(box => box.Type == Minf))
                {
                    foreach (var stbl in ChildBoxes(moov, (int)minf.Offset + minf.HeaderLength, (int)(minf.Offset + minf.Size)).Where(box => box.Type == Stbl))
                    {
                        foreach (var stsd in ChildBoxes(moov, (int)stbl.Offset + stbl.HeaderLength, (int)(stbl.Offset + stbl.Size)).Where(box => box.Type == Stsd))
                        {
                            // stsd 是 FullBox：版本/标志与条目数之后才是条目；VisualSampleEntry 的宽高在条目起点后 32 字节
                            var entry = (int)stsd.Offset + stsd.HeaderLength + 8;
                            if (entry + 36 <= stsd.Offset + stsd.Size)
                            {
                                width = BinaryPrimitives.ReadUInt16BigEndian(moov[(entry + 32)..]);
                                height = BinaryPrimitives.ReadUInt16BigEndian(moov[(entry + 34)..]);
                            }
                        }
                    }
                }
            }
        }

        return maxTrackId > 0 && width > 0 && height > 0;
    }

    /// <summary>
    /// 把 moov 内所有 stco/co64 的块偏移平移 <paramref name="delta"/>；越界时返回 <c>false</c>，由调用方放弃写入。
    /// </summary>
    private static bool TryShiftChunkOffsets(Span<byte> data, int start, int end, long delta)
    {
        if (delta == 0)
        {
            return true;
        }

        foreach (var box in ChildBoxes(data, start, end))
        {
            if (box.Type is Trak or Mdia or Minf or Stbl or Edts or Dinf or Udta)
            {
                if (!TryShiftChunkOffsets(data, (int)box.Offset + box.HeaderLength, (int)(box.Offset + box.Size), delta))
                {
                    return false;
                }

                continue;
            }

            if (box.Type is not (Stco or Co64))
            {
                continue;
            }

            var body = (int)box.Offset + box.HeaderLength;
            var boxEnd = (int)(box.Offset + box.Size);
            if (body + 8 > boxEnd)
            {
                return false;
            }

            var count = BinaryPrimitives.ReadUInt32BigEndian(data[(body + 4)..]);
            var entrySize = box.Type == Stco ? 4 : 8;
            if (count > MaxChunkEntries || body + 8 + (long)count * entrySize > boxEnd)
            {
                return false;
            }

            for (var i = 0; i < count; i++)
            {
                var entry = body + 8 + i * entrySize;
                var shifted = box.Type == Stco
                    ? BinaryPrimitives.ReadUInt32BigEndian(data[entry..]) + delta
                    : (long)BinaryPrimitives.ReadUInt64BigEndian(data[entry..]) + delta;
                if (shifted < 0 || (box.Type == Stco && shifted > uint.MaxValue))
                {
                    return false;
                }

                if (box.Type == Stco)
                {
                    BinaryPrimitives.WriteUInt32BigEndian(data[entry..], (uint)shifted);
                }
                else
                {
                    BinaryPrimitives.WriteUInt64BigEndian(data[entry..], (ulong)shifted);
                }
            }
        }

        return true;
    }

    /// <summary>
    /// 重排 moov：原样保留各子 box（块偏移已修正），新轨道插在最后一个轨道之后，并同步 nextTrackID；
    /// Apple 原片同样是轨道在前、moov/meta 与 udta 在最后。
    /// </summary>
    private static byte[] RebuildMovie(byte[] moov, byte[] track, int nextTrackId)
    {
        var children = ChildBoxes(moov, 8, moov.Length);
        var body = new List<byte[]>(children.Count + 1);
        var lastTrack = -1;
        foreach (var child in children)
        {
            var copy = moov.AsSpan((int)child.Offset, (int)child.Size).ToArray();
            if (child.Type == Mvhd)
            {
                PatchNextTrackId(copy, child.HeaderLength, nextTrackId);
            }

            body.Add(copy);
            if (child.Type == Trak)
            {
                lastTrack = body.Count - 1;
            }
        }

        // 没有轨道时 mvhd 必须仍在最前，追加到末尾
        body.Insert(lastTrack >= 0 ? lastTrack + 1 : body.Count, track);
        return Box(Moov, [.. body]);
    }

    private static void PatchNextTrackId(byte[] mvhd, int headerLength, int nextTrackId)
    {
        // 版本/标志、创建与修改时间、时间刻度与时长之后是速率、音量、矩阵等固定字段，nextTrackID 在其后
        var offset = headerLength + (mvhd[headerLength] == 1 ? 108 : 96);
        if (offset + 4 <= mvhd.Length)
        {
            BinaryPrimitives.WriteUInt32BigEndian(mvhd.AsSpan(offset), (uint)nextTrackId);
        }
    }

    /// <summary>
    /// 新轨道：tkhd + edts/elst + mdia，字段值与 Apple 原片逐项一致，时长与尺寸按本文件填写。
    /// </summary>
    private static byte[] BuildTrack(int trackId, long stillUnits, long sampleOffset, uint creationTime, uint timescale, int width, int height)
    {
        var tkhd = Box(Tkhd,
            U32(0x0000000F),
            U32(creationTime), U32(creationTime), U32((uint)trackId), U32(0), U32((uint)(stillUnits + 1)),
            U32(0), U32(0), U16(0), U16(0), U16(0x0100), U16(0),
            // 元数据轨不显示画面：矩阵之后是宽高两个 0（Apple 原片亦然）
            IdentityMatrix, U32(0), U32(0));
        // 空编辑覆盖封面之前的时长，再让单样本从 0 开始显示，样本因此落在封面时刻
        var elst = Box(Elst, U32(0), U32(2), U32((uint)stillUnits), U32(uint.MaxValue), U32(0x00010000), U32(1), U32(0), U32(0x00010000));
        var edts = Box(Edts, elst);
        var mdhd = Box(Mdhd, U32(0), U32(creationTime), U32(creationTime), U32(timescale), U32(1), U16(0x55C4), U16(0));
        var hdlr = Box(Hdlr, U32(0), "mhlr"u8.ToArray(), "meta"u8.ToArray(), "appl"u8.ToArray(), U32(1), U32(0), PascalString("Core Media Metadata"));
        var gmhd = Box(Gmhd, Box(Gmin, GraphicsMode));
        var dataHandler = Box(Hdlr, U32(0), "dhlr"u8.ToArray(), "alis"u8.ToArray(), "appl"u8.ToArray(), U32(0), U32(0), PascalString("Core Media Data Handler"));
        var dinf = Box(Dinf, Box(Dref, U32(0), U32(1), Box(Alis, U32(1))));
        var mebx = Box(Mebx, new byte[6], U16(1), BuildKeys());
        var stsd = Box(Stsd, U32(0), U32(1), mebx);
        var stts = Box(Stts, U32(0), U32(1), U32(1), U32(1));
        var stsc = Box(Stsc, U32(0), U32(1), U32(1), U32(1), U32(1));
        var stsz = Box(Stsz, U32(0), U32(SampleLength), U32(1));
        var stco = Box(Stco, U32(0), U32(1), U32((uint)sampleOffset));
        var stbl = Box(Stbl, stsd, stts, stsc, stsz, stco);
        var minf = Box(Minf, gmhd, dataHandler, dinf, stbl);
        var mdia = Box(Mdia, mdhd, hdlr, minf);
        return Box(Trak, tkhd, edts, mdia);
    }

    /// <summary>
    /// 键表：每个键一条 [条目长度][本地 ID] 记录，内含 keyd（命名空间 mdta）与 dtyp（数据类型）。
    /// 键名按 Apple 的写法不带结尾 NUL，长度因此比带 NUL 时少一字节。
    /// </summary>
    private static byte[] BuildKeys()
    {
        (string Name, uint DataType)[] keys =
        [
            (StillImageTimeKey, 65), // int8s
            (StillImageTransformKey, 83), // undef
            (StillImageTransformDimensionsKey, 71) // float[2]
        ];

        var entries = new byte[keys.Length][];
        for (var i = 0; i < keys.Length; i++)
        {
            entries[i] = KeyEntry(i + 1,
                Box(Keyd, "mdta"u8.ToArray(), Encoding.ASCII.GetBytes(keys[i].Name)),
                Box(Dtyp, U32(0), U32(keys[i].DataType)));
        }

        return Box(Keys, entries);
    }

    private static byte[] KeyEntry(int localId, params byte[][] children)
    {
        var length = 8 + children.Sum(child => child.Length);
        var entry = new byte[length];
        BinaryPrimitives.WriteUInt32BigEndian(entry, (uint)length);
        BinaryPrimitives.WriteUInt32BigEndian(entry.AsSpan(4), (uint)localId);
        var offset = 8;
        foreach (var child in children)
        {
            child.CopyTo(entry, offset);
            offset += child.Length;
        }

        return entry;
    }

    /// <summary>三条记录：still-image-time（-1）、恒等透视变换、参考尺寸。</summary>
    private static byte[] BuildSample(int width, int height)
    {
        var sample = new byte[SampleLength];
        var span = sample.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(span, 9);
        BinaryPrimitives.WriteUInt32BigEndian(span[4..], 1);
        span[8] = 0xFF;

        BinaryPrimitives.WriteUInt32BigEndian(span[9..], 80);
        BinaryPrimitives.WriteUInt32BigEndian(span[13..], 2);
        for (var i = 0; i < 9; i++)
        {
            BinaryPrimitives.WriteDoubleBigEndian(span[(17 + i * 8)..], i % 4 == 0 ? 1d : 0d);
        }

        BinaryPrimitives.WriteUInt32BigEndian(span[89..], 16);
        BinaryPrimitives.WriteUInt32BigEndian(span[93..], 3);
        BinaryPrimitives.WriteSingleBigEndian(span[97..], width);
        BinaryPrimitives.WriteSingleBigEndian(span[101..], height);
        return sample;
    }

    private static byte[] BuildMdatHeader(int headerLength, long payloadLength)
    {
        var header = new byte[headerLength];
        if (headerLength == 16)
        {
            BinaryPrimitives.WriteUInt32BigEndian(header, 1);
            "mdat"u8.CopyTo(header.AsSpan(4));
            BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(8), (ulong)(16 + payloadLength));
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(header, (uint)(8 + payloadLength));
            "mdat"u8.CopyTo(header.AsSpan(4));
        }

        return header;
    }

    private static async Task CopyRangeAsync(FileStream source, FileStream destination, long offset, long length, byte[] buffer, CancellationToken cancellationToken)
    {
        source.Position = offset;
        var remaining = length;
        while (remaining > 0)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("读取视频时提前到达文件末尾。");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            remaining -= read;
        }
    }

    private static byte[] Box(uint type, params byte[][] children)
    {
        var length = 8 + children.Sum(child => child.Length);
        var box = new byte[length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)length);
        BinaryPrimitives.WriteUInt32BigEndian(box.AsSpan(4), type);
        var offset = 8;
        foreach (var child in children)
        {
            child.CopyTo(box, offset);
            offset += child.Length;
        }

        return box;
    }

    private static byte[] PascalString(string value)
    {
        var text = Encoding.ASCII.GetBytes(value);
        return [(byte)text.Length, .. text];
    }

    private static byte[] U16(params ushort[] values)
    {
        var bytes = new byte[values.Length * 2];
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(i * 2), values[i]);
        }

        return bytes;
    }

    private static byte[] U32(params uint[] values)
    {
        var bytes = new byte[values.Length * 4];
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(i * 4), values[i]);
        }

        return bytes;
    }

    private readonly record struct BoxSpan(long Offset, long Size, uint Type, int HeaderLength);

    private readonly record struct MovieHeader(uint Timescale, ulong Duration, uint CreationTime);
}
