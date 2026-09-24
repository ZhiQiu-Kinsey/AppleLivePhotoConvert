using Avalonia;
using Avalonia.Styling;

namespace LivePhotoConvert.Desktop.Infrastructure;

/// <summary>把设置中的主题名应用到应用程序。</summary>
public sealed class ThemeService
{
    public const string Light = "Light";
    public const string Dark = "Dark";
    public const string Auto = "Auto";

    public void Apply(string? theme)
    {
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = ToVariant(theme);
        }
    }

    public static ThemeVariant ToVariant(string? theme) => theme switch
    {
        Dark => ThemeVariant.Dark,
        Auto => ThemeVariant.Default,
        _ => ThemeVariant.Light
    };
}
