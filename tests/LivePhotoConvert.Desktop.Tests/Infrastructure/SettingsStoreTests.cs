using System.Text.Json.Nodes;
using LivePhotoConvert.Core.Pipeline;
using LivePhotoConvert.Desktop.Infrastructure;

namespace LivePhotoConvert.Desktop.Tests.Infrastructure;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"lpc_settings_{Guid.NewGuid():N}");

    public SettingsStoreTests() => Directory.CreateDirectory(_dir);

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static JsonObject ReadJson(string path) => (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;

    [Fact]
    public void MissingFile_UsesDefaultsAndDoesNotTouchDisk()
    {
        using var store = new SettingsStore(SettingsPath);

        Assert.Equal(SettingsStore.CurrentSchemaVersion, store.Current.SchemaVersion);
        Assert.Equal(ConflictPolicy.AppendIndex, store.Current.ConflictPolicy);
        Assert.Null(store.Current.Window);

        store.Flush();
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public void Update_IsDebounced_UntilFlush()
    {
        using var store = new SettingsStore(SettingsPath, TimeSpan.FromMinutes(5));

        store.Update(s => s.Theme = "Dark");
        store.Update(s => s.HeicQuality = 77);

        Assert.False(File.Exists(SettingsPath), "防抖期内不应写盘");

        store.Flush();

        var json = ReadJson(SettingsPath);
        Assert.Equal("Dark", (string?)json["theme"]);
        Assert.Equal(77, (int?)json["heicQuality"]);
        Assert.Equal(SettingsStore.CurrentSchemaVersion, (int?)json["schemaVersion"]);
        Assert.False(File.Exists(SettingsPath + ".tmp"), "原子写入后不应残留临时文件");
    }

    [Fact]
    public async Task Update_AfterDebounce_WritesMergedChangesOnce()
    {
        using var store = new SettingsStore(SettingsPath, TimeSpan.FromMilliseconds(150));

        store.Update(s => s.Theme = "Dark");
        store.Update(s => s.Language = "en");
        store.Update(s => s.Concurrency = 3);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!File.Exists(SettingsPath) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.True(File.Exists(SettingsPath), "防抖到期后应自动写盘");
        var json = ReadJson(SettingsPath);
        Assert.Equal("Dark", (string?)json["theme"]);
        Assert.Equal("en", (string?)json["language"]);
        Assert.Equal(3, (int?)json["concurrency"]);
    }

    [Fact]
    public void Flush_WhenWriteFails_KeepsPreviousFileIntactAndRetriesLater()
    {
        File.WriteAllText(SettingsPath, $$"""{ "schemaVersion": {{SettingsStore.CurrentSchemaVersion}}, "theme": "Light" }""");
        var original = File.ReadAllText(SettingsPath);
        using var store = new SettingsStore(SettingsPath);

        // 临时文件路径被目录占用：写临时文件失败，目标文件必须保持原样
        Directory.CreateDirectory(SettingsPath + ".tmp");
        store.Update(s => s.Theme = "Dark");
        store.Flush();
        Assert.Equal(original, File.ReadAllText(SettingsPath));

        Directory.Delete(SettingsPath + ".tmp");
        store.Flush();
        Assert.Equal("Dark", (string?)ReadJson(SettingsPath)["theme"]);
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("[1, 2, 3]")]
    [InlineData("""{ "concurrency": "many" }""")]
    public void CorruptFile_IsRenamedAndDefaultsAreUsed(string content)
    {
        File.WriteAllText(SettingsPath, content);

        using var store = new SettingsStore(SettingsPath);

        Assert.Equal(new DesktopSettings().Theme, store.Current.Theme);
        Assert.False(File.Exists(SettingsPath));
        Assert.Equal(content, File.ReadAllText(SettingsPath + SettingsStore.CorruptSuffix));
    }

    [Theory]
    [InlineData("true", ConflictPolicy.Overwrite)]
    [InlineData("false", ConflictPolicy.AppendIndex)]
    [InlineData(null, ConflictPolicy.AppendIndex)]
    public void VersionOneFile_IsMigratedAndRewritten(string? overwriteSameName, ConflictPolicy expected)
    {
        var overwrite = overwriteSameName is null ? string.Empty : $"\"overwriteSameName\": {overwriteSameName},";
        File.WriteAllText(SettingsPath, $$"""
            {
              "theme": "Dark",
              {{overwrite}}
              "autoDownloadDependencies": false,
              "heicQuality": 80,
              "lastScanDirectory": "/photos"
            }
            """);

        using var store = new SettingsStore(SettingsPath);

        Assert.Equal(expected, store.Current.ConflictPolicy);
        Assert.Equal("Dark", store.Current.Theme);
        Assert.Equal(80, store.Current.HeicQuality);
        Assert.Equal("/photos", store.Current.LastScanDirectory);

        var json = ReadJson(SettingsPath);
        Assert.Equal(SettingsStore.CurrentSchemaVersion, (int?)json["schemaVersion"]);
        Assert.Equal(expected.ToString(), (string?)json["conflictPolicy"]);
        Assert.False(json.ContainsKey("overwriteSameName"));
        Assert.False(json.ContainsKey("autoDownloadDependencies"));
    }

    [Theory]
    [InlineData("", "/strip", "/strip")]
    [InlineData("/album", "/strip", "/album")]
    [InlineData("/album", null, "/album")]
    public void VersionTwoFile_MergesStripDirectoryIntoTheSingleAlbumDirectory(string scan, string? strip, string expected)
    {
        var stripField = strip is null ? string.Empty : $"\"stripLastDirectory\": \"{strip}\",";
        File.WriteAllText(SettingsPath, $$"""
            {
              "schemaVersion": 2,
              {{stripField}}
              "lastScanDirectory": "{{scan}}",
              "inPlaceStrip": true
            }
            """);

        using var store = new SettingsStore(SettingsPath);

        Assert.Equal(expected, store.Current.LastScanDirectory);
        Assert.True(store.Current.InPlaceStrip);
        Assert.Equal(new DesktopSettings().Action, store.Current.Action);
        Assert.Equal("Medium", store.Current.Gallery.Scale);
        var json = ReadJson(SettingsPath);
        Assert.Equal(SettingsStore.CurrentSchemaVersion, (int?)json["schemaVersion"]);
        Assert.False(json.ContainsKey("stripLastDirectory"));
    }

    [Fact]
    public void RoundTrip_PersistsActionAndGalleryPreferences_AndToleratesNullGallery()
    {
        using (var store = new SettingsStore(SettingsPath))
        {
            store.Update(s =>
            {
                s.Action = Desktop.Features.Library.ConversionAction.Strip;
                s.Gallery.SortMode = "Name";
                s.Gallery.Crop = "Square";
            });
        }

        var json = ReadJson(SettingsPath);
        Assert.Equal("Strip", (string?)json["action"]);
        using (var reloaded = new SettingsStore(SettingsPath))
        {
            Assert.Equal(Desktop.Features.Library.ConversionAction.Strip, reloaded.Current.Action);
            Assert.Equal(("Name", "Square"), (reloaded.Current.Gallery.SortMode, reloaded.Current.Gallery.Crop));
        }

        File.WriteAllText(SettingsPath, $$"""{ "schemaVersion": {{SettingsStore.CurrentSchemaVersion}}, "gallery": null }""");
        using var withNull = new SettingsStore(SettingsPath);
        Assert.Equal("DateTaken", withNull.Current.Gallery.SortMode);
    }

    [Fact]
    public void Migrate_CurrentVersion_IsNoOp()
    {
        var root = new JsonObject { ["schemaVersion"] = SettingsStore.CurrentSchemaVersion, ["conflictPolicy"] = "Overwrite" };

        Assert.False(SettingsStore.Migrate(root));
        Assert.Equal("Overwrite", (string?)root["conflictPolicy"]);
    }

    [Fact]
    public void RoundTrip_PersistsWindowPlacementAndConflictPolicy()
    {
        using (var store = new SettingsStore(SettingsPath))
        {
            store.Update(s =>
            {
                s.ConflictPolicy = ConflictPolicy.Overwrite;
                s.Window = new WindowPlacement { X = -1200, Y = 40, Width = 1100, Height = 700, IsMaximized = true };
            });
        }

        using var reloaded = new SettingsStore(SettingsPath);
        Assert.Equal(ConflictPolicy.Overwrite, reloaded.Current.ConflictPolicy);
        var window = Assert.IsType<WindowPlacement>(reloaded.Current.Window);
        Assert.Equal(-1200, window.X);
        Assert.Equal(40, window.Y);
        Assert.Equal(1100, window.Width);
        Assert.Equal(700, window.Height);
        Assert.True(window.IsMaximized);
    }
}
