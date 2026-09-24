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

    /// <summary>名称来自本地化资源：写错时拒绝，不能把源文件移到所在目录之外。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(" Merged")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("../Merged")]
    public void Move_RejectsInvalidArchiveFolderName(string? name)
    {
        Assert.Throws<ArgumentException>(() => new SourceDisposition(SourceFileAction.Move, name));
    }

    [Fact]
    public void OtherActions_IgnoreArchiveFolderName()
    {
        Assert.Equal(SourceFileAction.Keep, new SourceDisposition(SourceFileAction.Keep).Action);
        Assert.Equal(SourceFileAction.Delete, new SourceDisposition(SourceFileAction.Delete, "..").Action);
    }

    [Fact]
    public void Delete_RemovesFilesAndIgnoresMissingOnes()
    {
        using var temp = new TempDirectory();
        var file = temp.CreateFile("IMG.jpg", [1]);

        Assert.Null(new SourceDisposition(SourceFileAction.Delete).Apply(file, temp.Combine("missing.mov")));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void Recycle_OnNonWindows_ReportsErrorWithoutDeleting()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows 支持回收站");
        using var temp = new TempDirectory();
        var file = temp.CreateFile("IMG.mov", [1]);

        Assert.NotNull(new SourceDisposition(SourceFileAction.Recycle).Apply(file));
        Assert.True(File.Exists(file));
    }
}
