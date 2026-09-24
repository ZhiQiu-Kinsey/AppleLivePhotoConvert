using LivePhotoConvert.Core.Pipeline;

namespace LivePhotoConvert.Core.Tests.Pipeline;

/// <summary>
/// 退出清理会删除本进程的全部工作目录，不能与其它使用 <see cref="TempWorkspace"/> 的测试并行。
/// </summary>
[CollectionDefinition(nameof(TempWorkspaceCollection), DisableParallelization = true)]
public sealed class TempWorkspaceCollection;

[Collection(nameof(TempWorkspaceCollection))]
public class TempWorkspaceTests
{
    [Fact]
    public void DeleteOwned_RemovesOnlyWorkspacesOfThisProcess()
    {
        var foreign = Directory.CreateDirectory(Path.Combine(TempWorkspace.Root, $"temp-{Guid.NewGuid():N}")).FullName;
        try
        {
            var owned = new TempWorkspace();
            File.WriteAllBytes(owned.NewFile(".jpg"), [1]);

            TempWorkspace.DeleteOwned();

            Assert.False(Directory.Exists(owned.Directory));
            Assert.True(Directory.Exists(foreign));
        }
        finally
        {
            Directory.Delete(foreign, recursive: true);
        }
    }

    [Fact]
    public void DeleteOrphans_KeepsRecentForeignAndOwnWorkspaces()
    {
        var foreign = Directory.CreateDirectory(Path.Combine(TempWorkspace.Root, $"temp-{Guid.NewGuid():N}")).FullName;
        using var owned = new TempWorkspace();
        try
        {
            TempWorkspace.DeleteOrphans(TimeSpan.FromHours(24));

            Assert.True(Directory.Exists(foreign));
            Assert.True(Directory.Exists(owned.Directory));
        }
        finally
        {
            Directory.Delete(foreign, recursive: true);
        }
    }

    [Fact]
    public void Dispose_RemovesDirectory()
    {
        var workspace = new TempWorkspace();

        workspace.Dispose();

        Assert.False(Directory.Exists(workspace.Directory));
    }
}
