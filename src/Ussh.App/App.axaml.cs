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
            MainWindowViewModel? viewModel = null;
            // "Ask every time" key passphrases: prompted, then remembered in memory only, and
            // forgotten when zSSH locks or exits. Never prompts while locked.
            var passphrases = new CachingPassphraseProvider(
                (server, retryReason, _) => dialogs.PromptSecretAsync(
                    "Key passphrase",
                    $"Enter the passphrase for the private key of {server.DisplayName} ({server.Username}@{server.Host}).\n" +
                    "It is kept in memory only and is never saved.",
                    retryReason, "Key passphrase"),
                canPrompt: () => viewModel?.IsLocked == false);
            var sessions = new SessionManager(verifier, passphrases);
            viewModel = new MainWindowViewModel(new Vault(AppPaths.VaultFile), sessions, dialogs);
            viewModel.Locked += passphrases.Clear;
            verifier.IsLocked = () => viewModel.IsLocked;
            window.DataContext = viewModel;
            desktop.MainWindow = window;
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.ShutdownRequested += (_, _) =>
            {
                passphrases.Clear();
                sessions.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
