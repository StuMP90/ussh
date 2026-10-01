using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Ussh.App.Services;
using Ussh.App.ViewModels;
using Ussh.App.Views;
using Ussh.Core;
using Ussh.Core.Security;
using Ussh.Core.Ssh;

namespace Ussh.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            var dialogs = new DialogService(window);
            var verifier = new HostKeyVerifier(dialogs);
            var sessions = new SessionManager(verifier);
            var viewModel = new MainWindowViewModel(new Vault(AppPaths.VaultFile), sessions, dialogs);
            verifier.IsLocked = () => viewModel.IsLocked;
            window.DataContext = viewModel;
            desktop.MainWindow = window;
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.ShutdownRequested += (_, _) => sessions.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        }

        base.OnFrameworkInitializationCompleted();
    }
}
