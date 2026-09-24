using LivePhotoConvert.Core.Pipeline;

namespace LivePhotoConvert.Core.Tests.Pipeline;

public class SourceDispositionTests
{
    [Fact]
    public void Keep_TouchesNothing()
    {
        using var temp = new TempDirectory();
        var file = temp.CreateFile("IMG.jpg", [1]);

        Assert.Null(new SourceDisposition(SourceFileAction.Keep, "已合成").Apply(file));
        Assert.True(File.Exists(file));
        Assert.False(Directory.Exists(temp.Combine("已合成")));
    }

    [Fact]
    public void Move_UsesArchiveFolderNextToEachSourceAndNeverOverwrites()
    {
        using var temp = new TempDirectory();
        var first = temp.CreateFile("a/IMG.jpg", [1]);
        var second = temp.CreateFile("b/IMG.jpg", [2]);
        temp.CreateFile("a/已合成/IMG.jpg", [9]);
        var disposition = new SourceDisposition(SourceFileAction.Move, "已合成");

        Assert.Null(disposition.Apply(first, second));

        Assert.Equal([9], File.ReadAllBytes(temp.Combine("a", "已合成", "IMG.jpg")));
        Assert.Equal([1], File.ReadAllBytes(temp.Combine("a", "已合成", "IMG_1.jpg")));
        Assert.Equal([2], File.ReadAllBytes(temp.Combine("b", "已合成", "IMG.jpg")));
    }

    [Fact]
    public void Delete_RemovesFilesAndIgnoresMissingOnes()
    {
        using var temp = new TempDirectory();
        var file = temp.CreateFile("IMG.jpg", [1]);

        Assert.Null(new SourceDisposition(SourceFileAction.Delete, string.Empty).Apply(file, temp.Combine("missing.mov")));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void Recycle_OnNonWindows_ReportsErrorWithoutDeleting()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows 支持回收站");
        using var temp = new TempDirectory();
        var file = temp.CreateFile("IMG.mov", [1]);

        Assert.NotNull(new SourceDisposition(SourceFileAction.Recycle, string.Empty).Apply(file));
        Assert.True(File.Exists(file));
    }
}
