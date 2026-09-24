using System.Runtime.Versioning;
using LivePhotoConvert.Core.Platform;

namespace LivePhotoConvert.Core.Tests;

/// <summary>
/// Windows 回收站。
/// </summary>
public class RecycleBinTests
{
    /// <summary>
    /// 在 Windows 平台上，调用 RecycleBin.Send 应能成功将文件移入回收站并且原文件不再存在
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public void Should_Send_File_To_RecycleBin_Successfully()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "回收站只在 Windows 上使用。");

        using var tempDir = new TempDirectory();
        var tempFile = tempDir.CreateFile("recycle_test.txt", "test content"u8.ToArray());

        Assert.True(File.Exists(tempFile));

        RecycleBin.Send(tempFile);

        Assert.False(File.Exists(tempFile));
    }

    [Theory]
    [InlineData(@"\\server\share\IMG_0001.MOV")]
    [InlineData("")]
    public void HasRecycleBin_NetworkOrRootlessPath_IsFalse(string path)
    {
        Assert.False(RecycleBin.HasRecycleBin(path));
    }

    [Fact]
    public void HasRecycleBin_LocalFixedDisk_IsTrue()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "回收站只在 Windows 上使用。");
        using var tempDir = new TempDirectory();

        Assert.True(RecycleBin.HasRecycleBin(tempDir.CreateFile("recycle_probe.txt")));
    }
}
