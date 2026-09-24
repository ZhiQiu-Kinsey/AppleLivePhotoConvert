using LivePhotoConvert.Core.Io;

namespace LivePhotoConvert.Core.Tests.Io;

/// <summary>
/// 文件与时间戳辅助类 (FileHelper &amp; FileTimestamp) 的单元测试
/// </summary>
public class FileHelperTests
{
    /// <summary>
    /// 测试 TryDeleteFile 是否能正确删除已存在的文件
    /// </summary>
    [Fact]
    public void TryDeleteFile_Should_Delete_Existing_File()
    {
        using var temp = new TempDirectory();
        var filePath = temp.CreateFile("test.txt", [1, 2, 3]);

        Assert.True(File.Exists(filePath));
        FileHelper.TryDeleteFile(filePath);
        Assert.False(File.Exists(filePath));
    }

    /// <summary>
    /// 测试 TryDeleteFile 在传入 null 或不存在的文件路径时能够静默通过且不抛出异常
    /// </summary>
    [Fact]
    public void TryDeleteFile_Should_Not_Throw_For_Null_Or_Nonexistent_Path()
    {
        FileHelper.TryDeleteFile(null);
        FileHelper.TryDeleteFile("non_existent_file_path_12345.tmp");
    }

    /// <summary>
    /// 测试 TryDeleteDirectory 是否能递归删除包含子项的目录树
    /// </summary>
    [Fact]
    public void TryDeleteDirectory_Should_Delete_Directory_Recursively()
    {
        using var temp = new TempDirectory();
        var subDir = Path.Combine(temp.Root, "nested", "child");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "data.bin"), "content");

        Assert.True(Directory.Exists(subDir));
        FileHelper.TryDeleteDirectory(Path.Combine(temp.Root, "nested"));
        Assert.False(Directory.Exists(Path.Combine(temp.Root, "nested")));
    }

    [Fact]
    public void FileTimestamp_ApplyTo_Should_Copy_Timestamps_To_Targets()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateFile("src.jpg", [1]);
        var target = temp.CreateFile("dst.jpg", [2]);
        var expected = new DateTime(2020, 5, 1, 8, 30, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, expected);

        FileTimestamp.Read(source).ApplyTo(target);

        Assert.Equal(expected, File.GetLastWriteTimeUtc(target));
    }

    [Fact]
    public void FileTimestamp_Earliest_Should_Pick_Earliest_Of_All_Sources()
    {
        using var temp = new TempDirectory();
        var photo = temp.CreateFile("IMG.heic", [1]);
        var video = temp.CreateFile("IMG.mov", [2]);
        var older = new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(photo, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(video, older);

        Assert.Equal(older, FileTimestamp.Earliest(photo, video).LastWriteTimeUtc);
    }
}
