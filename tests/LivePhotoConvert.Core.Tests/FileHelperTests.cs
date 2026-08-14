using LivePhotoConvert.Core.Io;

namespace LivePhotoConvert.Core.Tests;

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

    /// <summary>
    /// 测试 FileTimestamp.Sync 是否能将源文件的创建时间与修改时间批量同步给所有目标文件
    /// </summary>
    [Fact]
    public void FileTimestamp_Sync_Should_Copy_Timestamps_To_Targets()
    {
        using var temp = new TempDirectory();
        var src = temp.CreateFile("source.jpg", [1]);
        var expectedCreation = new DateTime(2023, 5, 20, 10, 30, 0, DateTimeKind.Utc);
        var expectedWrite = new DateTime(2023, 5, 20, 11, 45, 0, DateTimeKind.Utc);

        File.SetCreationTimeUtc(src, expectedCreation);
        File.SetLastWriteTimeUtc(src, expectedWrite);

        var dst1 = temp.CreateFile("dest1.jpg", [2]);
        var dst2 = temp.CreateFile("dest2.mov", [3]);

        FileTimestamp.Sync(src, dst1, dst2);

        Assert.Equal(expectedCreation, File.GetCreationTimeUtc(dst1));
        Assert.Equal(expectedWrite, File.GetLastWriteTimeUtc(dst1));
        Assert.Equal(expectedCreation, File.GetCreationTimeUtc(dst2));
        Assert.Equal(expectedWrite, File.GetLastWriteTimeUtc(dst2));
    }

    /// <summary>
    /// 测试 FileTimestamp.SyncEarliest 是否能从照片与视频中正确选取最早的创建时间与修改时间
    /// </summary>
    [Fact]
    public void FileTimestamp_SyncEarliest_Should_Pick_Earliest_Timestamp()
    {
        using var temp = new TempDirectory();
        var photo = temp.CreateFile("photo.jpg", [1]);
        var video = temp.CreateFile("video.mov", [2]);
        var target = temp.CreateFile("target.jpg", [3]);

        var timeEarly = new DateTime(2023, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var timeLate = new DateTime(2023, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        File.SetCreationTimeUtc(photo, timeLate);
        File.SetLastWriteTimeUtc(photo, timeEarly);

        File.SetCreationTimeUtc(video, timeEarly);
        File.SetLastWriteTimeUtc(video, timeLate);

        FileTimestamp.SyncEarliest(target, photo, video);

        Assert.Equal(timeEarly, File.GetCreationTimeUtc(target));
        Assert.Equal(timeEarly, File.GetLastWriteTimeUtc(target));
    }
}

