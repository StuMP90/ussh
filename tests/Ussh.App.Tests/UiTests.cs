using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ussh.App.Controls;
using Ussh.App.Services;
using Ussh.App.ViewModels;
using Ussh.App.Views;
using Ussh.Core.Models;
using Ussh.Core.Security;
using Ussh.Core.Ssh;
using Ussh.Core.Tests.Integration;

namespace Ussh.App.Tests;

/// <summary>
/// Drives the real windows off-screen. Screenshots go to $USSH_SCREENSHOT_DIR (default: temp).
/// </summary>
public sealed class UiTests : IDisposable
{
    private const string AdminPassword = "admin-password-1";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ussh-ui-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _dispose = new();

    public void Dispose()
    {
        foreach (var d in _dispose)
            d.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    [AvaloniaFact]
    public async Task FirstRunAsksForAdminPasswordTwice()
    {
        var (window, vm, _) = Create();

        Assert.True(vm.IsSetupRequired);
        Assert.True(vm.IsLocked);
        Save(window, "01-setup");

        vm.Password = AdminPassword;
        vm.ConfirmPassword = "different";
        await vm.UnlockCommand.ExecuteAsync(null);
        Assert.True(vm.IsLocked);
        Assert.Equal("The passwords don't match.", vm.LockError);

        vm.Password = AdminPassword;
        vm.ConfirmPassword = AdminPassword;
        await vm.UnlockCommand.ExecuteAsync(null);
        Assert.False(vm.IsLocked);
        Assert.True(File.Exists(Path.Combine(_dir, "vault.json")));
    }

    [AvaloniaFact]
    public async Task ServerManagementAddsEditsAndPersists()
    {
        var (window, vm, _) = Create();
        await CreateVault(vm);

        AddServer(vm, "Production web 1", "Production", "web1.example.com", "deploy", tunnels: true);
        AddServer(vm, "Database primary", "Production", "db1.example.com", "postgres");
        AddServer(vm, "Raspberry Pi", "Home", "192.168.1.20", "pi");
        vm.Servers.SelectedServer = vm.Servers.FilteredServers.First(s => s.Name == "Production web 1");
        Save(window, "02-servers");

        vm.Servers.Filter = "home";
        Assert.Single(vm.Servers.FilteredServers);
        vm.Servers.Filter = "";

        // Re-open from disk: the data must survive a lock/unlock.
        vm.Lock();
        Assert.Empty(vm.Servers.FilteredServers); // decrypted data dropped while locked
        Save(window, "03-locked");
        vm.Password = AdminPassword;
        await vm.UnlockCommand.ExecuteAsync(null);
        Assert.Equal(3, vm.Servers.FilteredServers.Count);
        var web = vm.Servers.FilteredServers.First(s => s.Name == "Production web 1").Profile;
        Assert.Equal(2, web.Tunnels.Count);

        vm.Servers.ShowSettingsCommand.Execute(null);
        Save(window, "04-settings");
    }

    [AvaloniaFact]
    public async Task EditorValidatesBeforeSaving()
    {
        var (_, vm, _) = Create();
        await CreateVault(vm);

        vm.Servers.AddServerCommand.Execute(null);
        var editor = vm.Servers.Editor!;
        editor.Host = "";
        vm.Servers.SaveServerCommand.Execute(null);
        Assert.Equal("Host is required.", editor.ValidationError);

        editor.Host = "example.com";
        editor.Username = "me";
        editor.AuthMethodIndex = (int)AuthMethod.PrivateKey;
        editor.PrivateKey = "ssh-ed25519 AAAAC3Nza... me@laptop";
        vm.Servers.SaveServerCommand.Execute(null);
        Assert.Contains("public key", editor.ValidationError);
        Assert.Empty(vm.Servers.FilteredServers);
    }

    [AvaloniaFact]
    public async Task TerminalTabRendersLiveSession()
    {
        var server = SshTestServer.TryStart();
        if (server == null)
            return; // python3/paramiko not available
        _dispose.Add(server);

        var (window, vm, sessions) = Create();
        await CreateVault(vm);
        var profile = AddServer(vm, "Test server", "", "127.0.0.1", Environment.UserName, port: server.Port, password: SshTestServer.Password);

        vm.Connect(profile);
        var tab = Assert.IsType<TerminalTabViewModel>(vm.SelectedTab);
        await WaitUntil(() => tab.Session.State == SessionState.Connected, "connected");

        // Give the terminal its real size, then draw something colourful.
        await Pump(300);
        tab.Session.Send(
            "clear; printf '\\e[1;31mred \\e[0;32mgreen \\e[33myellow \\e[34mblue \\e[35mmagenta \\e[36mcyan\\e[0m\\n'; " +
            "printf '\\e[38;2;255;128;0mtruecolor orange\\e[0m \\e[44;97m white on blue \\e[0m \\e[7m inverse \\e[0m \\e[4munderline\\e[0m\\n'; " +
            "printf 'wide: 漢字 emoji: 😀 box: ┌─┐ └─┘\\n'; seq -s ' ' 1 40; echo; stty size\r");
        await WaitUntil(() => ScreenContains(tab.Session, "truecolor orange") && ScreenContains(tab.Session, " 40"), "output");
        await Pump(300);

        var terminal = window.GetVisualDescendants().OfType<TerminalControl>().Single();
        Assert.True(terminal.Bounds.Width > 200);
        int rows, cols;
        lock (tab.Session.Emulator.SyncRoot)
            (rows, cols) = (tab.Session.Emulator.Terminal.Rows, tab.Session.Emulator.Terminal.Cols);
        Assert.True(ScreenContains(tab.Session, $"{rows} {cols}"), "server pty size should match the rendered terminal");
        Save(window, "05-terminal");

        // Closing a connected tab asks for confirmation; disconnect first so no dialog opens.
        tab.Session.Disconnect();
        await WaitUntil(() => tab.Session.State == SessionState.Disconnected, "disconnected");
        await Pump(100);
        Save(window, "06-disconnected");
        await vm.CloseTabAsync(tab);
        Assert.DoesNotContain(tab, vm.Tabs);
        await sessions.DisposeAsync();
    }

    // ------------------------------------------------------------------ helpers

    private (MainWindow Window, MainWindowViewModel Vm, SessionManager Sessions) Create()
    {
        Directory.CreateDirectory(_dir);
        var window = new MainWindow { Width = 1280, Height = 800 };
        var dialogs = new DialogService(window);
        var sessions = new SessionManager(new TrustAll());
        var vm = new MainWindowViewModel(new Vault(Path.Combine(_dir, "vault.json")), sessions, dialogs);
        window.DataContext = vm;
        window.Show();
        return (window, vm, sessions);
    }

    private static async Task CreateVault(MainWindowViewModel vm)
    {
        vm.Password = AdminPassword;
        vm.ConfirmPassword = AdminPassword;
        await vm.UnlockCommand.ExecuteAsync(null);
        Assert.False(vm.IsLocked);
    }

    private static ServerProfile AddServer(MainWindowViewModel vm, string name, string group, string host, string user,
        int port = 22, string password = "secret", bool tunnels = false)
    {
        vm.Servers.AddServerCommand.Execute(null);
        var editor = vm.Servers.Editor!;
        editor.Name = name;
        editor.Group = group;
        editor.Host = host;
        editor.Port = port;
        editor.Username = user;
        editor.Password = password;
        if (tunnels)
        {
            editor.AddTunnelCommand.Execute(null);
            editor.Tunnels[0].BindPort = 15432;
            editor.Tunnels[0].DestinationPort = 5432;
            editor.AddTunnelCommand.Execute(null);
            editor.Tunnels[1].TypeIndex = (int)TunnelType.Dynamic;
            editor.Tunnels[1].BindPort = 1080;
        }
        vm.Servers.SaveServerCommand.Execute(null);
        Assert.Null(editor.ValidationError);
        Assert.False(editor.IsDirty);
        return vm.Data!.Servers.Single(s => s.Id == editor.Id);
    }

    private static bool ScreenContains(SshSession session, string text)
    {
        lock (session.Emulator.SyncRoot)
        {
            var buffer = session.Emulator.Terminal.Buffer;
            for (var i = 0; i < buffer.Lines.Length; i++)
                if (buffer.TranslateBufferLineToString(i, true, 0, -1).ToString()!.Contains(text))
                    return true;
        }
        return false;
    }

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Timed out waiting for " + what);
            await Pump(50);
        }
    }

    private static async Task Pump(int milliseconds)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        do
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(15);
        } while (DateTime.UtcNow < until);
    }

    private static void Save(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var dir = Environment.GetEnvironmentVariable("USSH_SCREENSHOT_DIR") ?? Path.Combine(Path.GetTempPath(), "ussh-screenshots");
        Directory.CreateDirectory(dir);
        frame!.Save(Path.Combine(dir, name + ".png"));
    }

    private sealed class TrustAll : IHostKeyVerifier
    {
        public Task<bool> VerifyAsync(HostKeyInfo hostKey, CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
