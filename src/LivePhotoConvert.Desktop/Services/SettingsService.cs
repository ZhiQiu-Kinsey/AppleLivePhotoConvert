using System.Text.Json;
using LivePhotoConvert.Desktop.Models;

namespace LivePhotoConvert.Desktop.Services;

/// <summary>
/// 本地配置持久化服务（基于 System.Text.Json AOT Source Generator）
/// </summary>
public sealed class SettingsService
{
    private readonly Lock _lock = new();
    private readonly string _settingsPath;
    private DesktopSettings _currentSettings;

    public DesktopSettings Current => _currentSettings;

    public SettingsService(string? customPath = null)
    {
        if (customPath is not null)
        {
            _settingsPath = customPath;
            _currentSettings = LoadSettingsInternal();
            return;
        }

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string configDir = Path.Combine(appData, "LivePhotoConvert");
        Directory.CreateDirectory(configDir);
        _settingsPath = Path.Combine(configDir, "settings.json");
        _currentSettings = LoadSettingsInternal();
    }

    public DesktopSettings Load()
    {
        lock (_lock)
        {
            _currentSettings = LoadSettingsInternal();
            return _currentSettings;
        }
    }

    public void Save() => Save(_currentSettings);

    public void Save(DesktopSettings settings)
    {
        lock (_lock)
        {
            _currentSettings = settings;
            try
            {
                string json = JsonSerializer.Serialize(settings, SettingsJsonContext.Default.DesktopSettings);
                File.WriteAllText(_settingsPath, json);
            }
            catch
            {
                // 防御性忽略写失败
            }
        }
    }

    private DesktopSettings LoadSettingsInternal()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                string json = File.ReadAllText(_settingsPath);
                var loaded = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.DesktopSettings);
                if (loaded is not null) return loaded;
            }
        }
        catch
        {
            // 忽略反序列化失败，回退默认
        }

        return new DesktopSettings();
    }
}
