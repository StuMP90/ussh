using Avalonia;
using Avalonia.Threading;
using Ussh.Core.Diagnostics;

namespace Ussh.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Last-resort guards. Anything reaching these is a bug, but a bug in one place must not
        // take down every open session, so log it and keep running wherever the runtime allows.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("app", $"Unhandled exception (terminating={e.IsTerminating}).", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("app", "Unobserved task exception.", e.Exception);
            e.SetObserved();
        };

        Log.Info("app", $"Starting uSSH {AppInfo.FullVersion} on {Environment.OSVersion}.");
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .AfterSetup(_ =>
            {
                Dispatcher.UIThread.UnhandledException += (_, e) =>
                {
                    Log.Error("ui", "Unhandled UI exception.", e.Exception);
                    e.Handled = true;
                };
            });
}
