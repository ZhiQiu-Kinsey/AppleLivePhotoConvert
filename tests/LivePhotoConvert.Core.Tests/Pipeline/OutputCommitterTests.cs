using LivePhotoConvert.Core.Pipeline;

namespace LivePhotoConvert.Core.Tests.Pipeline;

public class OutputCommitterTests
{
    [Fact]
    public void Commit_ExistingFile_AppendsIndexAndLeavesExistingUntouched()
    {
        using var temp = new TempDirectory();
        var existing = temp.CreateFile("MVIMG_1.jpg", [1]);
        var committer = new OutputCommitter(ConflictPolicy.AppendIndex, []);

        var final = committer.Commit(Stage(temp, [2]), temp.Root, "MVIMG_1.jpg");

        Assert.Equal(temp.Combine("MVIMG_1_1.jpg"), final);
        Assert.Equal([1], File.ReadAllBytes(existing));
        Assert.Equal([2], File.ReadAllBytes(final));
        Assert.DoesNotContain(temp.FileNames(), name => name.StartsWith(OutputCommitter.StagingPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public void Commit_OverwritePolicy_ReplacesUnrelatedFile()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("out.jpg", [1]);
        var committer = new OutputCommitter(ConflictPolicy.Overwrite, []);

        var final = committer.Commit(Stage(temp, [2]), temp.Root, "out.jpg");

        Assert.Equal(temp.Combine("out.jpg"), final);
        Assert.Equal([2], File.ReadAllBytes(final));
    }

    [Fact]
    public void Commit_OverwritePolicy_NeverOverwritesBatchSources()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateFile("IMG_0001.jpg", [1]);
        var committer = new OutputCommitter(ConflictPolicy.Overwrite, [source]);

        var final = committer.Commit(Stage(temp, [2]), temp.Root, "IMG_0001.jpg");

        Assert.Equal(temp.Combine("IMG_0001_1.jpg"), final);
        Assert.Equal([1], File.ReadAllBytes(source));
    }

    [Fact]
    public void Commit_OverwritePolicy_NeverOverwritesOutputOfSameBatch()
    {
        using var temp = new TempDirectory();
        var committer = new OutputCommitter(ConflictPolicy.Overwrite, []);

        var first = committer.Commit(Stage(temp, [1]), temp.Root, "MVIMG_20240501_140303.jpg");
        var second = committer.Commit(Stage(temp, [2]), temp.Root, "MVIMG_20240501_140303.jpg");

        Assert.NotEqual(first, second);
        Assert.Equal([1], File.ReadAllBytes(first));
        Assert.Equal([2], File.ReadAllBytes(second));
    }

    [Fact]
    public async Task Commit_ConcurrentSameName_AssignsDistinctNames()
    {
        using var temp = new TempDirectory();
        var committer = new OutputCommitter(ConflictPolicy.AppendIndex, []);
        var staged = Enumerable.Range(0, 64).Select(i => Stage(temp, [(byte)i])).ToArray();

        var finals = await Task.WhenAll(staged.Select(path => Task.Run(() => committer.Commit(path, temp.Root, "same.jpg"), TestContext.Current.CancellationToken)));

        Assert.Equal(64, finals.Distinct().Count());
        Assert.Equal(64, temp.FileNames().Length);
    }

    [Fact]
    public void CommitGroup_UsesSameIndexForPair()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("IMG_0001.MOV", [0]);
        var committer = new OutputCommitter(ConflictPolicy.AppendIndex, []);

        var finals = committer.CommitGroup([new StagedFile(Stage(temp, [1]), "IMG_0001.HEIC"), new StagedFile(Stage(temp, [2]), "IMG_0001.MOV")], temp.Root);

        Assert.Equal([temp.Combine("IMG_0001_1.HEIC"), temp.Combine("IMG_0001_1.MOV")], finals);
    }

    [Fact]
    public void CommitGroup_SecondMoveFails_RollsBackFirst()
    {
        using var temp = new TempDirectory();
        var committer = new OutputCommitter(ConflictPolicy.AppendIndex, []);
        var photo = Stage(temp, [1]);
        var missingVideo = temp.Combine("~lpc-missing.mov");

        Assert.Throws<FileNotFoundException>(() => committer.CommitGroup([new StagedFile(photo, "IMG.HEIC"), new StagedFile(missingVideo, "IMG.MOV")], temp.Root));

        Assert.True(File.Exists(photo));
        Assert.False(File.Exists(temp.Combine("IMG.HEIC")));
    }

    [Fact]
    public void ReplaceSource_SameExtension_ReplacesAndRemovesBackup()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateFile("IMG_0001.jpg", [1, 1, 1]);

        var final = new OutputCommitter(ConflictPolicy.AppendIndex, [source]).ReplaceSource(Stage(temp, [2]), source, ".jpg");

        Assert.Equal(source, final);
        Assert.Equal([2], File.ReadAllBytes(source));
        Assert.Equal(["IMG_0001.jpg"], temp.FileNames());
    }

    [Fact]
    public void ReplaceSource_ExtensionChange_DeletesOriginalAndKeepsUnrelatedFiles()
    {
        using var temp = new TempDirectory();
        var jpg = temp.CreateFile("IMG_0001.jpg", [1]);
        var jpeg = temp.CreateFile("IMG_0001.jpeg", [2]);
        var unrelatedHeic = temp.CreateFile("IMG_0001.heic", [3]);
        var committer = new OutputCommitter(ConflictPolicy.AppendIndex, [jpg, jpeg]);

        var fromJpg = committer.ReplaceSource(Stage(temp, [10]), jpg, ".heic");
        var fromJpeg = committer.ReplaceSource(Stage(temp, [20]), jpeg, ".heic");

        Assert.Equal([3], File.ReadAllBytes(unrelatedHeic));
        Assert.Equal([10], File.ReadAllBytes(fromJpg));
        Assert.Equal([20], File.ReadAllBytes(fromJpeg));
        Assert.Equal(["IMG_0001.heic", "IMG_0001_1.heic", "IMG_0001_2.heic"], temp.FileNames());
    }

    [Fact]
    public void DeleteStaleStagingFiles_RemovesOnlyOldStagingFiles()
    {
        using var temp = new TempDirectory();
        var stale = temp.CreateFile("~lpc-old.jpg", [1]);
        var fresh = temp.CreateFile("~lpc-new.jpg", [1]);
        var user = temp.CreateFile("photo.jpg", [1]);
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-3));

        OutputCommitter.DeleteStaleStagingFiles(temp.Root, TimeSpan.FromDays(1));

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(user));
    }

    private static string Stage(TempDirectory temp, byte[] content)
    {
        var path = OutputCommitter.CreateStagingPath(temp.Root, ".tmp");
        File.WriteAllBytes(path, content);
        return path;
    }
}
