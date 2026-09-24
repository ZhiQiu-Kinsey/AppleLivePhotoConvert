using System.Formats.Tar;
using LivePhotoConvert.Core.External.Tools;

namespace LivePhotoConvert.Core.Tests.External;

public class ToolArchiveExtractorTests
{
    [Theory]
    [InlineData("a/b.txt", "a/b.txt")]
    [InlineData("./a//b.txt", "a/b.txt")]
    [InlineData("a\\b.txt", "a/b.txt")]
    [InlineData("exiftool(-k).exe", "exiftool(-k).exe")]
    public void Normalize_AcceptsRelativePaths(string entry, string expected) =>
        Assert.Equal(expected, ToolArchivePath.Normalize(entry));

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("a/../../evil.txt")]
    [InlineData("a\\..\\..\\evil.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("\\evil.txt")]
    [InlineData("C:/evil.txt")]
    [InlineData("C:evil.txt")]
    [InlineData("a/.../evil.txt")]
    [InlineData("a/file.txt:stream")]
    [InlineData("a/b?.txt")]
    public void Normalize_RejectsUnsafePaths(string entry) =>
        Assert.Throws<UnsafeArchiveException>(() => ToolArchivePath.Normalize(entry));

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("package/../../evil.txt")]
    [InlineData("/evil.txt")]
    public async Task Zip_TraversalEntry_RejectsWholeArchive(string evilName)
    {
        using var temp = new TempDirectory();
        var archive = temp.CreateFile("pkg.zip", ToolPackages.Zip(("package/tool", "ok"), (evilName, "evil")));
        var destination = temp.Combine("sandbox", "out");

        await Assert.ThrowsAsync<UnsafeArchiveException>(() =>
            ToolArchiveExtractor.ExtractAsync(ToolArchiveFormat.Zip, archive, destination, "package", null, TestContext.Current.CancellationToken));

        Assert.DoesNotContain(Directory.EnumerateFiles(temp.Root, "evil.txt", SearchOption.AllDirectories), _ => true);
    }

    [Fact]
    public async Task Tgz_TraversalEntry_RejectsWholeArchive()
    {
        using var temp = new TempDirectory();
        var archive = temp.CreateFile("pkg.tgz", ToolPackages.Tgz(
            ToolPackages.TarFile("package/tool", "ok"),
            ToolPackages.TarFile("package/../../evil.txt", "evil")));
        var destination = temp.Combine("sandbox", "out");

        await Assert.ThrowsAsync<UnsafeArchiveException>(() =>
            ToolArchiveExtractor.ExtractAsync(ToolArchiveFormat.Tgz, archive, destination, "package", null, TestContext.Current.CancellationToken));

        Assert.DoesNotContain(Directory.EnumerateFiles(temp.Root, "evil.txt", SearchOption.AllDirectories), _ => true);
    }

    [Theory]
    [InlineData(TarEntryType.SymbolicLink)]
    [InlineData(TarEntryType.HardLink)]
    public async Task Tgz_LinkEntry_IsRejected(TarEntryType type)
    {
        using var temp = new TempDirectory();
        var archive = temp.CreateFile("pkg.tgz", ToolPackages.Tgz(
            new PaxTarEntry(type, "package/lib") { LinkName = "/etc" },
            ToolPackages.TarFile("package/tool", "ok")));

        await Assert.ThrowsAsync<UnsafeArchiveException>(() =>
            ToolArchiveExtractor.ExtractAsync(ToolArchiveFormat.Tgz, archive, temp.Combine("out"), "package", null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Zip_ExtractsOnlyRootAndIncludedEntries()
    {
        using var temp = new TempDirectory();
        var archive = temp.CreateFile("pkg.zip", ToolPackages.Zip(
            ("build/bin/ffmpeg.exe", "ffmpeg"),
            ("build/bin/ffprobe.exe", "ffprobe"),
            ("build/bin/ffplay.exe", "ffplay"),
            ("build/doc/readme.html", "doc"),
            ("build/bin/", "")));
        var destination = temp.Combine("out");

        var count = await ToolArchiveExtractor.ExtractAsync(ToolArchiveFormat.Zip, archive, destination, "build/bin", ["ffmpeg.exe", "ffprobe.exe"], TestContext.Current.CancellationToken);

        Assert.Equal(2, count);
        Assert.Equal(["ffmpeg.exe", "ffprobe.exe"], temp.FileNames("out"));
        Assert.Equal("ffmpeg", File.ReadAllText(Path.Combine(destination, "ffmpeg.exe")));
    }

    [Fact]
    public async Task Tgz_ExtractsNestedDirectories()
    {
        using var temp = new TempDirectory();
        var archive = temp.CreateFile("pkg.tgz", ToolPackages.Tgz(
            new PaxTarEntry(TarEntryType.Directory, "package/bin/exiftool_files/"),
            ToolPackages.TarFile("package/bin/exiftool.exe", "exe"),
            ToolPackages.TarFile("package/bin/exiftool_files/perl.dll", "dll"),
            ToolPackages.TarFile("package/README.md", "readme")));
        var destination = temp.Combine("out");

        await ToolArchiveExtractor.ExtractAsync(ToolArchiveFormat.Tgz, archive, destination, "package/bin", null, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(destination, "exiftool.exe")));
        Assert.Equal("dll", File.ReadAllText(Path.Combine(destination, "exiftool_files", "perl.dll")));
        Assert.False(File.Exists(Path.Combine(destination, "README.md")));
    }

    [Theory]
    [InlineData(nameof(SevenZipBackend.Managed))]
    [InlineData(nameof(SevenZipBackend.SystemTar))]
    [InlineData(nameof(SevenZipBackend.Auto))]
    public async Task SevenZip_ExtractsSelectedRoot(string backendName)
    {
        var backend = Enum.Parse<SevenZipBackend>(backendName);
        RequireBackend(backend);
        using var temp = new TempDirectory();
        var archive = temp.CreateFile("pkg.7z", Convert.FromBase64String(ToolPackages.SevenZipGood));
        var destination = temp.Combine("out");

        int count;
        try
        {
            count = await ToolArchiveExtractor.ExtractAsync(ToolArchiveFormat.SevenZip, archive, destination, "pkg", null, TestContext.Current.CancellationToken, backend);
        }
        catch (SystemTarUnsupportedException)
        {
            Assert.Skip("系统 tar 不支持 LZMA2。");
            return;
        }

        Assert.Equal(2, count);
        Assert.StartsWith("#!/bin/sh", File.ReadAllText(Path.Combine(destination, "fake-tool.exe")), StringComparison.Ordinal);
        Assert.Equal("dependency", File.ReadAllText(Path.Combine(destination, "lib", "dep.dll")));
        Assert.False(Directory.Exists(Path.Combine(destination, "other")));
        // 系统 tar 的临时目录用完即删
        Assert.Equal(["out", "pkg.7z"], Directory.EnumerateFileSystemEntries(temp.Root).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task SevenZip_Include_SkipsUnselectedEntries()
    {
        using var temp = new TempDirectory();
        var archive = temp.CreateFile("pkg.7z", Convert.FromBase64String(ToolPackages.SevenZipGood));
        var destination = temp.Combine("out");

        var count = await ToolArchiveExtractor.ExtractAsync(ToolArchiveFormat.SevenZip, archive, destination, "pkg", ["lib"], TestContext.Current.CancellationToken, SevenZipBackend.Managed);

        Assert.Equal(1, count);
        Assert.Equal(["dep.dll"], temp.FileNames("out"));
    }

    [Theory]
    [InlineData(nameof(SevenZipBackend.Managed))]
    [InlineData(nameof(SevenZipBackend.SystemTar))]
    public async Task SevenZip_TraversalEntry_RejectsWholeArchive(string backendName)
    {
        var backend = Enum.Parse<SevenZipBackend>(backendName);
        RequireBackend(backend);
        using var temp = new TempDirectory();
        var archive = temp.CreateFile("pkg.7z", Convert.FromBase64String(ToolPackages.SevenZipTraversal));

        await Assert.ThrowsAsync<UnsafeArchiveException>(() =>
            ToolArchiveExtractor.ExtractAsync(ToolArchiveFormat.SevenZip, archive, temp.Combine("sandbox", "out"), "pkg", null, TestContext.Current.CancellationToken, backend));

        Assert.DoesNotContain(Directory.EnumerateFiles(temp.Root, "evil.txt", SearchOption.AllDirectories), _ => true);
    }

    private static void RequireBackend(SevenZipBackend backend) =>
        Assert.SkipWhen(backend == SevenZipBackend.SystemTar && ToolArchiveExtractor.LocateSystemTar() is null, "系统没有 libarchive 版 tar。");

    [Theory]
    [InlineData(ToolArchiveFormat.Zip)]
    [InlineData(ToolArchiveFormat.Tgz)]
    [InlineData(ToolArchiveFormat.SevenZip)]
    public async Task CorruptArchive_ThrowsInvalidData(ToolArchiveFormat format)
    {
        using var temp = new TempDirectory();
        var archive = temp.CreateFile("pkg.bin", [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ToolArchiveExtractor.ExtractAsync(format, archive, temp.Combine("out"), "package", null, TestContext.Current.CancellationToken));
    }
}
