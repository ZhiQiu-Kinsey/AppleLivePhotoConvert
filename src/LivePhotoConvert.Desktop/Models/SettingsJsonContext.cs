using System.Text.Json.Serialization;

namespace LivePhotoConvert.Desktop.Models;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(DesktopSettings))]
public sealed partial class SettingsJsonContext : JsonSerializerContext;
