using LivePhotoConvert.Core.Io;

namespace LivePhotoConvert.Core.Tests;

/// <summary>
/// 唯一路径解析的测试
/// </summary>
public class UniquePathTests
{
    /// <summary>
    /// 测试如果目标文件名不存在，将使用原始名称。
    /// </summary>
    [Fact]
    public void Should_Use_Original_FileName_If_Destination_Does_Not_Exist()
    {
        using var temp = new TempDirectory();

        var path = UniquePath.Resolve(temp.Root, "photo.jpg");

        Assert.Equal(temp.Combine("photo.jpg"), path);
    }

    /// <summary>
    /// 测试如果存在同名文件，将在新文件名后追加一个数字。
    /// </summary>
    [Fact]
    public void Should_Append_Number_If_FileName_Exists()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("photo.jpg", [1]);

        var path = UniquePath.Resolve(temp.Root, "photo.jpg");

        Assert.Equal(temp.Combine("photo_1.jpg"), path);
        // 原文件必须原封不动
        Assert.Equal([1], File.ReadAllBytes(temp.Combine("photo.jpg")));
    }

    /// <summary>
    /// 测试如果带数字序号的文件已存在，数字将继续递增，直到找到一个唯一的名称。
    /// </summary>
    [Fact]
    public void Should_Increment_Number_Until_Unique_Name_Is_Found()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("photo.jpg");
        temp.CreateFile("photo_1.jpg");
        temp.CreateFile("photo_2.jpg");

        var path = UniquePath.Resolve(temp.Root, "photo.jpg");

        Assert.Equal(temp.Combine("photo_3.jpg"), path);
    }

    /// <summary>
    /// 测试同名目录被视为冲突，并会在文件名后追加一个数字。
    /// </summary>
    [Fact]
    public void Directory_With_Same_Name_Should_Be_Considered_As_Taken()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.Combine("photo.jpg"));

        var path = UniquePath.Resolve(temp.Root, "photo.jpg");

        Assert.Equal(temp.Combine("photo_1.jpg"), path);
    }

    /// <summary>
    /// 测试对于没有扩展名的文件，也能在其后正确追加数字。
    /// </summary>
    [Fact]
    public void Should_Correctly_Append_Number_To_File_Without_Extension()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("data");

        var path = UniquePath.Resolve(temp.Root, "data");

        Assert.Equal(temp.Combine("data_1"), path);
    }
}
