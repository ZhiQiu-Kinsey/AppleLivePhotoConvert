using LivePhotoConvert.Core.Services;

namespace LivePhotoConvert.Core.Tests.Services;

public class OutputOptionsTests
{
    private static readonly string Album = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "album"));
    private static readonly string Output = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "out"));

    [Fact]
    public void DirectoryFor_WithoutHierarchy_IsOutputRoot()
    {
        Assert.Equal(Output, new OutputOptions(Output).DirectoryFor(Path.Combine(Album, "2024", "IMG_0001.jpg")));
    }

    [Theory]
    [InlineData("2024")]
    [InlineData("..hidden")]
    public void DirectoryFor_SubfolderOfAlbum_IsMirrored(string subfolder)
    {
        var options = new OutputOptions(Output) { PreserveHierarchyFrom = Album };

        Assert.Equal(Path.Combine(Output, subfolder), options.DirectoryFor(Path.Combine(Album, subfolder, "IMG_0001.jpg")));
    }

    [Fact]
    public void DirectoryFor_OutsideAlbum_FallsBackToOutputRoot()
    {
        var options = new OutputOptions(Output) { PreserveHierarchyFrom = Album };

        Assert.Equal(Output, options.DirectoryFor(Path.Combine(Path.GetTempPath(), "elsewhere", "IMG_0001.jpg")));
        Assert.Equal(Output, options.DirectoryFor(Path.Combine(Album, "IMG_0001.jpg")));
    }
}
