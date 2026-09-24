using LivePhotoConvert.Core.Media;

namespace LivePhotoConvert.Core.Tests.Media;

public class MediaFileTypesTests
{
    [Fact]
    public void Should_Detect_Jpeg_From_Magic_Bytes()
    {
        byte[] jpegHeader = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46];
        var ext = MediaFileTypes.DetectPhotoExtension(jpegHeader);
        Assert.Equal(".jpg", ext);
    }

    [Fact]
    public void Should_Detect_Png_From_Magic_Bytes()
    {
        byte[] pngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        var ext = MediaFileTypes.DetectPhotoExtension(pngHeader);
        Assert.Equal(".png", ext);
    }

    [Fact]
    public void Should_Detect_Heic_From_Ftyp_Brands()
    {
        byte[] heicHeader = [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70, 0x68, 0x65, 0x69, 0x63]; // ftypheic
        var ext = MediaFileTypes.DetectPhotoExtension(heicHeader);
        Assert.Equal(".heic", ext);
    }

    [Fact]
    public void Should_Detect_Mov_From_Ftyp_Brands()
    {
        byte[] movHeader = [0x00, 0x00, 0x00, 0x14, 0x66, 0x74, 0x79, 0x70, 0x71, 0x74, 0x20, 0x20]; // ftypqt  
        var ext = MediaFileTypes.DetectVideoExtension(movHeader);
        Assert.Equal(".mov", ext);
    }

    [Fact]
    public void Should_Detect_Mp4_From_Ftyp_Brands()
    {
        byte[] mp4Header = [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70, 0x6D, 0x70, 0x34, 0x32]; // ftypmp42
        var ext = MediaFileTypes.DetectVideoExtension(mp4Header);
        Assert.Equal(".mp4", ext);
    }

    [Fact]
    public void Should_Return_True_For_Valid_Jpeg_File()
    {
        using var tempDir = new TempDirectory();
        var validJpeg = tempDir.CreateFile("test.jpg", [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10]);
        Assert.True(MediaFileTypes.IsJpeg(validJpeg));
    }

    [Fact]
    public void Should_Return_False_For_Misnamed_Non_Jpeg_File()
    {
        using var tempDir = new TempDirectory();
        // 实际上是 HEIC，但被错误命名为 .jpg
        var misnamedJpg = tempDir.CreateFile("test.jpg", [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70]);
        Assert.False(MediaFileTypes.IsJpeg(misnamedJpg));
    }

    [Fact]
    public void Should_Return_False_For_Non_Jpeg_Extension()
    {
        using var tempDir = new TempDirectory();
        var heicFile = tempDir.CreateFile("test.heic", [0x00, 0x00, 0x00, 0x18]);
        Assert.False(MediaFileTypes.IsJpeg(heicFile));
    }

    // S3: AVIF 分支测试（DetectPhotoExtension 的 AvifBrands 路径）
    [Fact]
    public void Should_Detect_Avif_From_Ftyp_Brands()
    {
        // ftyp box + major brand "avif"（偏移 8..11）
        byte[] avifHeader = [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70, 0x61, 0x76, 0x69, 0x66];
        var ext = MediaFileTypes.DetectPhotoExtension(avifHeader);
        Assert.Equal(".avif", ext);
    }

    // S4: DetectVideoExtension 的纯 ftyp box 分支（品牌不在 MovBrands/Mp4Brands，但符合 ftyp box 特征）
    [Fact]
    public void DetectVideoExtension_Should_Fallback_To_Mp4_For_Generic_Ftyp_Box()
    {
        // ftyp box + major brand "avif"（合法 ftyp 容器，但非 MOV/MP4 品牌）→ 走 IsFtypBox 分支返回 .mp4
        byte[] genericFtyp = [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70, 0x61, 0x76, 0x69, 0x66];
        var ext = MediaFileTypes.DetectVideoExtension(genericFtyp);
        Assert.Equal(".mp4", ext);
    }

    [Fact]
    public void IsValidVideoPayload_Should_Return_True_For_Valid_Mov_Ftyp()
    {
        byte[] movHeader = [0x00, 0x00, 0x00, 0x14, 0x66, 0x74, 0x79, 0x70, 0x71, 0x74, 0x20, 0x20]; // ftypqt
        Assert.True(MediaFileTypes.IsValidVideoPayload(movHeader));
    }

    [Fact]
    public void IsValidVideoPayload_Should_Return_True_For_Valid_Mp4_Ftyp()
    {
        byte[] mp4Header = [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70, 0x6D, 0x70, 0x34, 0x32]; // ftypmp42
        Assert.True(MediaFileTypes.IsValidVideoPayload(mp4Header));
    }

    [Fact]
    public void IsValidVideoPayload_Should_Return_Fake_For_RandomGarbage()
    {
        byte[] garbage = [0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC, 0xDE, 0xF0, 0x00, 0x00, 0x00, 0x00];
        Assert.False(MediaFileTypes.IsValidVideoPayload(garbage));
    }

    [Fact]
    public void IsValidVideoPayload_Should_Return_False_For_TooShort_Buffer()
    {
        byte[] tooShort = [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70]; // only 8 bytes, below 12 minimum
        Assert.False(MediaFileTypes.IsValidVideoPayload(tooShort));
    }
}
