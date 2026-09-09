namespace LivePhotoConvert.E2E.Harness;

/// <summary>
/// 真实二进制结构合成工厂：生成具有合法魔数与文件结构的测试多媒体载荷，严禁空壳或虚假数据。
/// </summary>
public static class SyntheticMediaFactory
{
    private static readonly byte[] JpegHeader =
    [
        0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10,
        (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00
    ];

    private static readonly byte[] JpegTrailer = [0xFF, 0xD9];

    /// <summary>
    /// 创建合法的 JPEG 图片二进制载荷
    /// </summary>
    public static byte[] CreateJpeg(int totalBytes = 1024)
    {
        if (totalBytes < JpegHeader.Length + JpegTrailer.Length)
        {
            totalBytes = JpegHeader.Length + JpegTrailer.Length + 64;
        }

        var buffer = new byte[totalBytes];
        JpegHeader.CopyTo(buffer, 0);

        // 填充中间数据
        for (var i = JpegHeader.Length; i < totalBytes - JpegTrailer.Length; i++)
        {
            buffer[i] = (byte)(i % 251);
        }

        JpegTrailer.CopyTo(buffer, totalBytes - JpegTrailer.Length);
        return buffer;
    }

    /// <summary>
    /// 创建合法的 HEIC (ISO Base Media File Format, ftypheic) 载荷
    /// </summary>
    public static byte[] CreateHeic(int totalBytes = 1024)
    {
        if (totalBytes < 32)
        {
            totalBytes = 32;
        }

        var buffer = new byte[totalBytes];
        // ftyp box header
        buffer[0] = 0x00; buffer[1] = 0x00; buffer[2] = 0x00; buffer[3] = 0x18; // length = 24
        buffer[4] = (byte)'f'; buffer[5] = (byte)'t'; buffer[6] = (byte)'y'; buffer[7] = (byte)'p';
        buffer[8] = (byte)'h'; buffer[9] = (byte)'e'; buffer[10] = (byte)'i'; buffer[11] = (byte)'c';
        // minor version
        buffer[12] = 0x00; buffer[13] = 0x00; buffer[14] = 0x00; buffer[15] = 0x00;
        // compatible brands (mif1, msf1)
        buffer[16] = (byte)'m'; buffer[17] = (byte)'i'; buffer[18] = (byte)'f'; buffer[19] = (byte)'1';
        buffer[20] = (byte)'m'; buffer[21] = (byte)'s'; buffer[22] = (byte)'f'; buffer[23] = (byte)'1';

        for (var i = 24; i < totalBytes; i++)
        {
            buffer[i] = (byte)(i % 253);
        }

        return buffer;
    }

    /// <summary>
    /// 创建合法的 MP4 (ftypmp42) 视频载荷
    /// </summary>
    public static byte[] CreateMp4(int totalBytes = 2048)
    {
        if (totalBytes < 32)
        {
            totalBytes = 32;
        }

        var buffer = new byte[totalBytes];
        buffer[0] = 0x00; buffer[1] = 0x00; buffer[2] = 0x00; buffer[3] = 0x18;
        buffer[4] = (byte)'f'; buffer[5] = (byte)'t'; buffer[6] = (byte)'y'; buffer[7] = (byte)'p';
        buffer[8] = (byte)'m'; buffer[9] = (byte)'p'; buffer[10] = (byte)'4'; buffer[11] = (byte)'2';
        buffer[12] = 0x00; buffer[13] = 0x00; buffer[14] = 0x00; buffer[15] = 0x00;
        buffer[16] = (byte)'i'; buffer[17] = (byte)'s'; buffer[18] = (byte)'o'; buffer[19] = (byte)'m';
        buffer[20] = (byte)'m'; buffer[21] = (byte)'p'; buffer[22] = (byte)'4'; buffer[23] = (byte)'2';

        for (var i = 24; i < totalBytes; i++)
        {
            buffer[i] = (byte)(i % 255);
        }

        return buffer;
    }

    /// <summary>
    /// 创建合法的 Apple QuickTime MOV (ftypqt  ) 视频载荷
    /// </summary>
    public static byte[] CreateMov(int totalBytes = 2048)
    {
        if (totalBytes < 32)
        {
            totalBytes = 32;
        }

        var buffer = new byte[totalBytes];
        buffer[0] = 0x00; buffer[1] = 0x00; buffer[2] = 0x00; buffer[3] = 0x14;
        buffer[4] = (byte)'f'; buffer[5] = (byte)'t'; buffer[6] = (byte)'y'; buffer[7] = (byte)'p';
        buffer[8] = (byte)'q'; buffer[9] = (byte)'t'; buffer[10] = (byte)' '; buffer[11] = (byte)' ';
        buffer[12] = 0x00; buffer[13] = 0x00; buffer[14] = 0x00; buffer[15] = 0x00;
        buffer[16] = (byte)'q'; buffer[17] = (byte)'t'; buffer[18] = (byte)' '; buffer[19] = (byte)' ';

        for (var i = 20; i < totalBytes; i++)
        {
            buffer[i] = (byte)(i % 254);
        }

        return buffer;
    }

    /// <summary>
    /// 创建 Google/小米动态照片二进制：JPEG 封面 + 尾部二进制拼接的 MP4
    /// </summary>
    public static (byte[] Content, int VideoOffset) CreateMotionPhoto(int photoBytes = 1024, int videoBytes = 2048)
    {
        var photo = CreateJpeg(photoBytes);
        var video = CreateMp4(videoBytes);

        var combined = new byte[photo.Length + video.Length];
        Buffer.BlockCopy(photo, 0, combined, 0, photo.Length);
        Buffer.BlockCopy(video, 0, combined, photo.Length, video.Length);

        return (combined, video.Length);
    }

    /// <summary>
    /// 创建各种畸变或损坏的文件载荷
    /// </summary>
    public static byte[] CreateCorrupted(CorruptedFileType type) => type switch
    {
        CorruptedFileType.ZeroBytes => [],
        CorruptedFileType.TruncatedJpeg => [0xFF, 0xD8, 0xFF],
        CorruptedFileType.TruncatedMp4 => [0x00, 0x00, 0x00, 0x08, (byte)'f', (byte)'t'],
        CorruptedFileType.RandomGarbage => [0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC, 0xDE, 0xF0],
        _ => []
    };
}

public enum CorruptedFileType
{
    ZeroBytes,
    TruncatedJpeg,
    TruncatedMp4,
    RandomGarbage
}
