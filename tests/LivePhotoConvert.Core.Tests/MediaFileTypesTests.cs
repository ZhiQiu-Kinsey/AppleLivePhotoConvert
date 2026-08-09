using LivePhotoConvert.Core.Matching;

namespace LivePhotoConvert.Core.Tests;

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
}
