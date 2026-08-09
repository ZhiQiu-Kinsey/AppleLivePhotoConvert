using LivePhotoConvert.Core.Models;
using LivePhotoConvert.Core.Services;

namespace LivePhotoConvert.Core.Tests;

/// <summary>
/// 原始文件清理策略的测试
/// </summary>
public class SourceFileCleanerTests
{
    /// <summary>
    /// 测试在“保留”操作下，任何文件都不会被更改。
    /// </summary>
    [Fact]
    public void KeepAction_Should_Not_Touch_Any_File()
    {
        using var temp = new TempDirectory();
        var photo = temp.CreateFile("IMG_0001.heic", [1]);
        var video = temp.CreateFile("IMG_0001.mov", [2]);

        var cleaner = new SourceFileCleaner(SourceFileAction.Keep, temp.Root);
        var result = cleaner.Clean([photo, video]);

        Assert.Equal(0, result.CleanedCount);
        Assert.Empty(result.Failures);
        Assert.True(File.Exists(photo));
        Assert.True(File.Exists(video));
    }

    /// <summary>
    /// 测试“移动”操作会将文件移动到“Merged”子文件夹中。
    /// </summary>
    [Fact]
    public void MoveAction_Should_Move_Files_To_Merged_Subfolder()
    {
        using var temp = new TempDirectory();
        var photo = temp.CreateFile("IMG_0001.heic", [1]);
        var video = temp.CreateFile("IMG_0001.mov", [2]);

        var cleaner = new SourceFileCleaner(SourceFileAction.Move, temp.Root);
        var result = cleaner.Clean([photo, video]);

        Assert.Equal(2, result.CleanedCount);
        Assert.Empty(result.Failures);
        Assert.False(File.Exists(photo));
        Assert.True(File.Exists(temp.Combine(SourceFileCleaner.MergedFolderName, "IMG_0001.heic")));
        Assert.True(File.Exists(temp.Combine(SourceFileCleaner.MergedFolderName, "IMG_0001.mov")));
    }

    /// <summary>
    /// 测试“移动”操作不会覆盖目标文件夹中的现有文件，而是会创建一个唯一的名称。
    /// </summary>
    [Fact]
    public void MoveAction_Should_Not_Overwrite_Existing_Files_With_Same_Name()
    {
        using var temp = new TempDirectory();
        var existing = temp.CreateFile(Path.Combine(SourceFileCleaner.MergedFolderName, "IMG_0001.heic"), [99]);
        var photo = temp.CreateFile("IMG_0001.heic", [1]);

        var cleaner = new SourceFileCleaner(SourceFileAction.Move, temp.Root);
        var result = cleaner.Clean([photo]);

        Assert.Equal(1, result.CleanedCount);
        // 先到的文件必须保持原样
        Assert.Equal([99], File.ReadAllBytes(existing));
        Assert.Equal([1], File.ReadAllBytes(temp.Combine(SourceFileCleaner.MergedFolderName, "IMG_0001_1.heic")));
    }

    /// <summary>
    /// 测试“删除”操作会移除源文件。
    /// </summary>
    [Fact]
    public void DeleteAction_Should_Remove_Files()
    {
        using var temp = new TempDirectory();
        var photo = temp.CreateFile("IMG_0001.heic", [1]);

        var cleaner = new SourceFileCleaner(SourceFileAction.Delete, temp.Root);
        var result = cleaner.Clean([photo]);

        Assert.Equal(1, result.CleanedCount);
        Assert.Empty(result.Failures);
        Assert.False(File.Exists(photo));
    }

    /// <summary>
    /// 测试清理一个不再存在的文件不会导致失败。
    /// </summary>
    [Fact]
    public void NonExistent_File_Should_Not_Be_Treated_As_Failure()
    {
        using var temp = new TempDirectory();

        var cleaner = new SourceFileCleaner(SourceFileAction.Delete, temp.Root);
        var result = cleaner.Clean([temp.Combine("missing.heic")]);

        Assert.Equal(0, result.CleanedCount);
        Assert.Empty(result.Failures);
    }

    /// <summary>
    /// 测试如果“Merged”子文件夹不存在，“保留”操作不会创建它。
    /// </summary>
    [Fact]
    public void KeepAction_Should_Not_Create_Merged_Subfolder()
    {
        using var temp = new TempDirectory();

        _ = new SourceFileCleaner(SourceFileAction.Keep, temp.Root);

        Assert.False(Directory.Exists(temp.Combine(SourceFileCleaner.MergedFolderName)));
    }
}
