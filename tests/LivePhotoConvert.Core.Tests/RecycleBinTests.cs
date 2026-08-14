using System.Runtime.InteropServices;
using LivePhotoConvert.Core.Platform;

namespace LivePhotoConvert.Core.Tests;

/// <summary>
/// Windows 回收站 P/Invoke 操作的单元测试
/// </summary>
public class RecycleBinTests
{
    /// <summary>
    /// 在 Windows 平台上，调用 RecycleBin.Send 应能成功将文件移入回收站并且原文件不再存在
    /// </summary>
    [Fact]
    public void Should_Send_File_To_RecycleBin_Successfully()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        using var tempDir = new TempDirectory();
        var tempFile = tempDir.CreateFile("recycle_test.txt", "test content"u8.ToArray());

        Assert.True(File.Exists(tempFile));

        RecycleBin.Send(tempFile);

        Assert.False(File.Exists(tempFile));
    }
}
