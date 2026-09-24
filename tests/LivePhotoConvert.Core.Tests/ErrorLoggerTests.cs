using LivePhotoConvert.Core.Services;

namespace LivePhotoConvert.Core.Tests;

public class ErrorLoggerTests
{
    [Fact]
    public void Log_WritesContextAndEveryInnerException()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("error.log");
        var ex = new InvalidOperationException("外层消息", new AggregateException(new ArgumentException("第一层"), new IOException("第二层")));

        Assert.Equal(path, ErrorLogger.Log(ex, "单元测试上下文", path));

        var content = File.ReadAllText(path);
        Assert.Contains("单元测试上下文", content);
        Assert.Contains("外层消息", content);
        Assert.Contains("第一层", content);
        Assert.Contains("第二层", content);
    }

    [Fact]
    public void Log_OversizedFile_RotatesToBackup()
    {
        using var temp = new TempDirectory();
        var path = temp.CreateFile("error.log", new byte[5 * 1024 * 1024 + 1]);

        ErrorLogger.Log(new InvalidOperationException("新记录"), null, path);

        Assert.True(File.Exists(path + ".bak"));
        Assert.Contains("新记录", File.ReadAllText(path));
        Assert.True(new FileInfo(path).Length < 5 * 1024 * 1024);
    }

    [Fact]
    public void LogFilePath_IsWritableLocation()
    {
        var directory = Path.GetDirectoryName(ErrorLogger.LogFilePath);

        Assert.True(Directory.Exists(directory));
    }
}
