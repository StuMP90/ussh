using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media;
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

// KeyPress(Key, modifiers) is marked obsolete in favour of physical keys; the logical key is what these tests mean.
#pragma warning disable CS0618

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

    [AvaloniaFact]
    public async Task ClickingOrShortcuttingToATabFocusesItsTerminal()
    {
        var server = SshTestServer.TryStart();
        if (server == null)
            return; // python3/paramiko not available
        _dispose.Add(server);

        var (window, vm, sessions) = Create();
        await CreateVault(vm);
        var profile = AddServer(vm, "Test server", "", "127.0.0.1", Environment.UserName, port: server.Port, password: SshTestServer.Password);
        vm.Connect(profile);
        var first = (TerminalTabViewModel)vm.SelectedTab!;
        vm.Connect(profile);
        var second = (TerminalTabViewModel)vm.SelectedTab!;
        await WaitUntil(() => first.Session.State == SessionState.Connected && second.Session.State == SessionState.Connected, "both connected");
        await Pump(200);

        // Mouse: Servers -> second terminal -> first terminal (terminal-to-terminal reuses the view).
        ClickTab(window, vm.Servers);
        await Pump(100);
        Assert.Null(FocusedSession(window));
        ClickTab(window, second);
        await Pump(100);
        Assert.Same(second.Session, FocusedSession(window));
        ClickTab(window, first);
        await Pump(100);
        Assert.Same(first.Session, FocusedSession(window));

        // Typing goes to the focused tab's server.
        window.KeyTextInput("echo typed-into-first");
        window.KeyPress(Key.Enter, RawInputModifiers.None);
        await WaitUntil(() => ScreenContainsLine(first.Session, "typed-into-first"), "typed text echoed");
        Assert.False(ScreenContainsLine(second.Session, "typed-into-first"));

        // Keyboard shortcuts, pressed while a terminal has focus.
        window.KeyPress(Key.Tab, RawInputModifiers.Control);
        await Pump(100);
        Assert.Same(second, vm.SelectedTab);
        Assert.Same(second.Session, FocusedSession(window));
        window.KeyPress(Key.Tab, RawInputModifiers.Control);
        await Pump(100);
        Assert.Same(vm.Servers, vm.SelectedTab); // wraps around
        window.KeyPress(Key.PageUp, RawInputModifiers.Control);
        await Pump(100);
        Assert.Same(second, vm.SelectedTab);
        window.KeyPress(Key.D2, RawInputModifiers.Alt);
        await Pump(100);
        Assert.Same(first, vm.SelectedTab);
        Assert.Same(first.Session, FocusedSession(window));
        window.KeyPress(Key.D9, RawInputModifiers.Alt);
        await Pump(100);
        Assert.Same(second, vm.SelectedTab);

        window.KeyPress(Key.L, RawInputModifiers.Control | RawInputModifiers.Shift);
        await Pump(100);
        Assert.True(vm.IsLocked);

        foreach (var tab in new[] { first, second })
            tab.Session.Disconnect();
        await sessions.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task JumpHostConfiguredInEditorConnectsThroughBastion()
    {
        var bastionServer = SshTestServer.TryStart();
        var targetServer = SshTestServer.TryStart();
        if (bastionServer == null || targetServer == null)
            return; // python3/paramiko not available
        _dispose.Add(bastionServer);
        _dispose.Add(targetServer);

        var (window, vm, sessions) = Create();
        await CreateVault(vm);
        var bastion = AddServer(vm, "bastion", "", "127.0.0.1", Environment.UserName, port: bastionServer.Port, password: SshTestServer.Password);
        AddServer(vm, "internal-app", "", "127.0.0.1", Environment.UserName, port: targetServer.Port, password: SshTestServer.Password);

        // Pick the bastion in the target's editor.
        vm.Servers.SelectedServer = vm.Servers.FilteredServers.Single(s => s.Name == "internal-app");
        var editor = vm.Servers.Editor!;
        Assert.Equal(new[] { "None (connect directly)", "bastion" }, editor.JumpHostOptions.Select(o => o.Label.Split("  ")[0]));
        editor.SelectedJumpHost = editor.JumpHostOptions.Single(o => o.Id == bastion.Id);
        vm.Servers.SaveServerCommand.Execute(null);
        Assert.Null(editor.ValidationError);
        var item = vm.Servers.FilteredServers.Single(s => s.Name == "internal-app");
        Assert.Equal("via bastion", item.Via);
        Save(window, "07-jump-host-editor");

        // The bastion's own editor must not offer the target (that would be a loop).
        vm.Servers.SelectedServer = vm.Servers.FilteredServers.Single(s => s.Name == "bastion");
        Assert.DoesNotContain(vm.Servers.Editor!.JumpHostOptions, o => o.Label.StartsWith("internal-app"));

        vm.Servers.SelectedServer = item;
        await Pump(100); // let the editor's pickers bind
        Assert.False(vm.Servers.Editor!.IsDirty, "opening a saved server must not mark it as changed");
        await vm.Servers.ConnectCommand.ExecuteAsync(null);
        var tab = Assert.IsType<TerminalTabViewModel>(vm.SelectedTab);
        await WaitUntil(() => tab.Session.State == SessionState.Connected, "connected via bastion");
        Assert.Contains("via bastion", tab.Endpoint);
        tab.Session.Send("echo hello-from-inside\r");
        await WaitUntil(() => ScreenContainsLine(tab.Session, "hello-from-inside"), "output via bastion");

        // Both host keys were remembered on their own servers.
        await Pump(100);
        Assert.All(vm.Data!.Servers, s => Assert.NotNull(s.HostKeyFingerprint));

        tab.Session.Disconnect();
        await sessions.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task PerServerThemesApplyAndUpdateLive()
    {
        var server = SshTestServer.TryStart();
        if (server == null)
            return; // python3/paramiko not available
        _dispose.Add(server);

        var (window, vm, sessions) = Create();
        await CreateVault(vm);
        AddServer(vm, "Old school", "", "127.0.0.1", Environment.UserName, port: server.Port, password: SshTestServer.Password);
        vm.Servers.SelectedServer = vm.Servers.FilteredServers.Single();
        var editor = vm.Servers.Editor!;
        Assert.StartsWith("Default (uSSH Dark)", editor.ThemeOptions[0].Label);
        Assert.Contains(editor.ThemeOptions, o => o.Name == "Green (P1 phosphor)");
        editor.SelectedTheme = editor.ThemeOptions.Single(o => o.Name == "Amber (P3 phosphor)");
        vm.Servers.SaveServerCommand.Execute(null);
        await Pump(100);
        var themePicker = window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.ItemsSource == editor.ThemeOptions);
        themePicker.BringIntoView();
        await Pump(100);
        Save(window, "08-theme-editor");

        await vm.Servers.ConnectCommand.ExecuteAsync(null);
        var tab = Assert.IsType<TerminalTabViewModel>(vm.SelectedTab);
        await WaitUntil(() => tab.Session.State == SessionState.Connected, "connected");
        await Pump(300);
        tab.Session.Send("clear; printf '\\e[31mred \\e[32mgreen \\e[34mblue \\e[1;37mbold white\\e[0m \\e[7m inverse \\e[0m\\n'; ls -la /\r");
        await WaitUntil(() => ScreenContains(tab.Session, "bold white"), "output");
        await Pump(300);
        Assert.Equal("Amber (P3 phosphor)", tab.Theme.Name);
        AssertTerminalBackground(window, Color.Parse("#120A00"));
        Save(window, "09-amber");

        // Changing the server's theme updates the open tab.
        vm.SelectedTab = vm.Servers;
        vm.Servers.Editor!.SelectedTheme = vm.Servers.Editor.ThemeOptions.Single(o => o.Name == "Green (P1 phosphor)");
        vm.Servers.SaveServerCommand.Execute(null);
        Assert.Equal("Green (P1 phosphor)", tab.Theme.Name);
        vm.SelectedTab = tab;
        await Pump(300);
        AssertTerminalBackground(window, Color.Parse("#011A07"));
        Save(window, "10-green");

        tab.Session.Disconnect();
        await sessions.DisposeAsync();
    }

    private static void AssertTerminalBackground(Window window, Color expected)
    {
        var terminal = window.GetVisualDescendants().OfType<TerminalControl>().Single();
        // Bottom-right corner of the terminal: always empty background.
        var corner = terminal.TranslatePoint(new Point(terminal.Bounds.Width - 3, terminal.Bounds.Height - 3), window)!.Value;
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frame = window.CaptureRenderedFrame()!;
        using var buffer = frame.Lock();
        var x = (int)(corner.X * frame.Dpi.X / 96);
        var y = (int)(corner.Y * frame.Dpi.Y / 96);
        var offset = y * buffer.RowBytes + x * 4;
        byte At(int i) => System.Runtime.InteropServices.Marshal.ReadByte(buffer.Address, offset + i);
        var actual = buffer.Format == Avalonia.Platform.PixelFormat.Rgba8888
            ? Color.FromRgb(At(0), At(1), At(2))
            : Color.FromRgb(At(2), At(1), At(0)); // Bgra8888
        Assert.Equal(expected.ToString(), actual.ToString());
    }

    private static void ClickTab(Window window, TabViewModel tab)
    {
        var item = window.GetVisualDescendants().OfType<TabItem>().Single(t => ReferenceEquals(t.DataContext, tab));
        var center = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
    }

    private static SshSession? FocusedSession(Window window) =>
        (window.FocusManager?.GetFocusedElement() as TerminalControl)?.Session;

    private static bool ScreenContainsLine(SshSession session, string text)
    {
        lock (session.Emulator.SyncRoot)
        {
            var buffer = session.Emulator.Terminal.Buffer;
            for (var i = 0; i < buffer.Lines.Length; i++)
                if (buffer.TranslateBufferLineToString(i, true, 0, -1).ToString()!.Trim() == text)
                    return true;
        }
        return false;
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
