using LivePhotoConvert.Core.External.Tools;

namespace LivePhotoConvert.Core.Tests.External;

public class ToolManifestTests
{
    [Theory]
    [InlineData(ToolId.ExifTool)]
    [InlineData(ToolId.Ffmpeg)]
    [InlineData(ToolId.HeifEnc)]
    public void Embedded_EveryToolHasVerifiedWindowsPackage(ToolId id)
    {
        var tool = ToolManifest.Embedded.Get(id);

        Assert.Contains(tool.PackagesFor("win-x64"), package => package.IsVerified);
        Assert.Equal(tool.InstallDirectory, Path.GetFileNameWithoutExtension(tool.ExecutableFileName));
    }

    [Fact]
    public void Embedded_AllPackagesArePinnedAndHashed()
    {
        foreach (var package in ToolManifest.Embedded.Tools.SelectMany(tool => tool.Packages))
        {
            Assert.True(package.IsVerified, $"{package.Id} 缺少 SHA256");
            Assert.DoesNotContain("latest", package.Url, StringComparison.OrdinalIgnoreCase);
            if (package.Url.Contains("registry.npm", StringComparison.Ordinal))
            {
                Assert.NotNull(package.Integrity);
            }
        }
    }

    [Fact]
    public void Embedded_FfmpegPrefersNpmMirrorWithHdrCapableBuild()
    {
        var first = ToolManifest.Embedded.Get(ToolId.Ffmpeg).PackagesFor("win-x64").First();

        Assert.Contains("registry.npmmirror.com/@ffmpeg-binary/win32-x64", first.Url, StringComparison.Ordinal);
        Assert.Equal("7.0", first.Version);
    }

    [Fact]
    public void Parse_AcceptsPackageWithoutHashButMarksItUnverified()
    {
        var manifest = ToolManifest.Parse(Json(Package(sha256: null)));

        Assert.False(manifest.Get(ToolId.ExifTool).Packages[0].IsVerified);
    }

    public static TheoryData<string, string> InvalidManifests => new()
    {
        { "http url", Json(Package(url: "http://example.com/a.tgz")) },
        { "short sha", Json(Package(sha256: "abcd")) },
        { "bad integrity", Json(Package(extra: "\"integrity\": \"sha1-abc\",")) },
        { "entry with directory", Json(Package(entry: "bin/tool.exe")) },
        { "root traversal", Json(Package(root: "../x")) },
        { "unknown field", Json(Package(extra: "\"sha1\": \"x\",")) },
        { "unknown format", Json(Package(format: "rar")) },
        { "github flag on other host", Json(Package(extra: "\"githubRelease\": true,")) },
        { "duplicate package", Json(Package() + "," + Package()) },
        { "missing field", Json(Package().Replace("\"version\": \"1.0\",", "", StringComparison.Ordinal)) },
        { "schema version", Json(Package()).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal) },
        { "not json", "{" }
    };

    [Theory]
    [MemberData(nameof(InvalidManifests))]
    public void Parse_RejectsInvalidManifest(string reason, string json)
    {
        Assert.False(string.IsNullOrEmpty(reason));
        Assert.Throws<InvalidDataException>(() => ToolManifest.Parse(json));
    }

    private static string Package(
        string url = "https://example.com/a.tgz",
        string? sha256 = "0000000000000000000000000000000000000000000000000000000000000000",
        string entry = "tool.exe",
        string root = "package",
        string format = "tgz",
        string extra = "") =>
        $$"""
        {
          "id": "p", "nameKey": "K", "rid": "win-x64", "version": "1.0",
          "url": "{{url}}",
          {{(sha256 is null ? "" : $"\"sha256\": \"{sha256}\",")}}
          {{extra}}
          "format": "{{format}}", "root": "{{root}}", "entry": "{{entry}}"
        }
        """;

    private static string Json(string packages) =>
        $$"""
        {
          "schemaVersion": 1,
          "githubMirrors": [],
          "tools": [
            { "id": "exiftool", "displayName": "ExifTool", "executable": "exiftool", "installDirectory": "exiftool",
              "versionArguments": ["-ver"], "homepage": "https://exiftool.org/", "packages": [ {{packages}} ] }
          ]
        }
        """;
}
