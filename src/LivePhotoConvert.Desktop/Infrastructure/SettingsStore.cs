using System.Text.Json;
using System.Text.Json.Nodes;
using LivePhotoConvert.Core.Io;
using LivePhotoConvert.Core.Services;

namespace LivePhotoConvert.Desktop.Infrastructure;

/// <summary>
/// 设置的唯一持有者：修改经 <see cref="Update"/> 进入，防抖后原子写盘；读取失败时保留坏文件并回退默认值。
/// </summary>
public sealed class SettingsStore : IDisposable
{
    public const int CurrentSchemaVersion = 4;
    public const string CorruptSuffix = ".corrupt";

    private static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(500);

    private readonly Lock _gate = new();
    private readonly TimeSpan _debounce;
    private readonly Timer _timer;
    private bool _dirty;
    private bool _disposed;

    public SettingsStore(string filePath, TimeSpan? debounce = null)
    {
        FilePath = Path.GetFullPath(filePath);
        _debounce = debounce ?? DefaultDebounce;
        _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        Current = Load(FilePath, out var migrated);
        if (migrated)
        {
            // 迁移结果立即落盘，避免旧字段在下次启动时再次参与迁移
            _dirty = true;
            Flush();
        }
    }

    /// <summary>默认位置：%AppData%/LivePhotoConvert/settings.json。</summary>
    /// <remarks>Create 选项：目录尚不存在时（如全新的 Linux 账户）默认选项会返回空串，设置文件将落到当前目录。</remarks>
    public static string DefaultFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
        "LivePhotoConvert",
        "settings.json");

    public string FilePath { get; }

    public DesktopSettings Current { get; }

    public void Update(Action<DesktopSettings> change)
    {
        lock (_gate)
        {
            change(Current);
            _dirty = true;
            if (!_disposed)
            {
                _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
            }
        }
    }

    /// <summary>立即写出尚未保存的修改；没有修改时不触碰磁盘。</summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
            }

            if (!_dirty)
            {
                return;
            }

            _dirty = false;
            try
            {
                Current.SchemaVersion = CurrentSchemaVersion;
                WriteAtomic(FilePath, JsonSerializer.Serialize(Current, SettingsJsonContext.Default.DesktopSettings));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 写失败保留脏标记，下次修改或退出时重试
                _dirty = true;
                ErrorLogger.Log(ex, "保存设置");
            }
        }
    }

    public void Dispose()
    {
        Flush();
        lock (_gate)
        {
            _disposed = true;
            _timer.Dispose();
        }
    }

    /// <summary>
    /// 读取设置；文件缺失时返回默认值，无法解析时把原文件改名为 *.corrupt 后返回默认值。
    /// </summary>
    internal static DesktopSettings Load(string path, out bool migrated)
    {
        migrated = false;
        if (!File.Exists(path))
        {
            return new DesktopSettings();
        }

        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root)
            {
                throw new JsonException("设置文件根节点不是对象。");
            }

            migrated = Migrate(root);
            return root.Deserialize(SettingsJsonContext.Default.DesktopSettings) ?? new DesktopSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            ErrorLogger.Log(ex, "读取设置");
            QuarantineCorruptFile(path);
            migrated = false;
            return new DesktopSettings();
        }
    }

    /// <summary>把旧版本的 JSON 就地升级到当前版本；返回是否有改动。</summary>
    internal static bool Migrate(JsonObject root)
    {
        var version = root["schemaVersion"] is JsonValue v && v.TryGetValue<int>(out var parsed) ? parsed : 1;
        if (version >= CurrentSchemaVersion)
        {
            return false;
        }

        if (version < 2)
        {
            var overwrite = root["overwriteSameName"] is JsonValue o && o.TryGetValue<bool>(out var flag) && flag;
            root["conflictPolicy"] = overwrite ? "Overwrite" : "AppendIndex";
            root.Remove("overwriteSameName");
            root.Remove("autoDownloadDependencies");
        }

        if (version < 3)
        {
            // 瘦身并入图库后只保留一个相册目录；旧的瘦身目录仅在图库目录为空时接替它
            var scan = root["lastScanDirectory"] is JsonValue d && d.TryGetValue<string>(out var dir) ? dir : null;
            var strip = root["stripLastDirectory"] is JsonValue sd && sd.TryGetValue<string>(out var stripDir) ? stripDir : null;
            if (string.IsNullOrWhiteSpace(scan) && !string.IsNullOrWhiteSpace(strip))
            {
                root["lastScanDirectory"] = strip;
            }

            root.Remove("stripLastDirectory");
        }

        if (version < 4)
        {
            // 缩略图由固定 24 张改为字节预算与磁盘缓存上限；显式写入默认值，文件里能看到可调项
            if (root["gallery"] is not JsonObject gallery)
            {
                gallery = new JsonObject();
                root["gallery"] = gallery;
            }

            gallery["thumbnailBudgetMb"] ??= GalleryPreferences.DefaultThumbnailBudgetMb;
            gallery["thumbnailDiskCacheMb"] ??= GalleryPreferences.DefaultThumbnailDiskCacheMb;
        }

        root["schemaVersion"] = CurrentSchemaVersion;
        return true;
    }

    private static void QuarantineCorruptFile(string path)
    {
        try
        {
            File.Move(path, path + CorruptSuffix, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLogger.Log(ex, "隔离损坏的设置文件");
        }
    }

    /// <summary>先写同目录临时文件再整体替换：进程中途退出也不会留下半截设置文件。</summary>
    private static void WriteAtomic(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, content);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            FileHelper.TryDeleteFile(temp);
        }
    }
}
