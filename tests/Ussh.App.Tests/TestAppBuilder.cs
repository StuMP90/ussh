using Avalonia;
using Avalonia.Headless;
using Ussh.App;
using Ussh.App.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Ussh.App.Tests;

public static class TestAppBuilder
{
    // Real Skia rendering (not headless drawing) so screenshots show actual output.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
