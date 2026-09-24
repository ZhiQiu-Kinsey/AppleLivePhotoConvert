using Avalonia;
using Avalonia.Headless;
using LivePhotoConvert.Desktop.Tests.Harness;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace LivePhotoConvert.Desktop.Tests.Harness;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHarfBuzz()
        .WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
