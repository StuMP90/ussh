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
using Ussh.Core.Files;
using Ussh.Core.Models;
using Ussh.Core.Security;
using Ussh.Core.Ssh;
using Ussh.Core.Tests.Integration;

// KeyPress(Key, modifiers) is marked obsolete in favour of physical keys; the logical key is what these tests mean.
#pragma warning disable CS0618

namespace Ussh.App.Tests;

/// <summary>
/// Drives the real windows off-screen. Screenshots go to $ZSSH_SCREENSHOT_DIR (default: temp).
/// </summary>
public sealed partial class UiTests : IDisposable
{
    private const string AdminPassword = "admin-password-1";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zssh-ui-" + Guid.NewGuid().ToString("N"));
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
        await WaitUntil(() => tab.FocusedPane.Session.State == SessionState.Connected, "connected");

        // Give the terminal its real size, then draw something colourful.
        await Pump(300);
        tab.FocusedPane.Session.Send(
            "clear; printf '\\e[1;31mred \\e[0;32mgreen \\e[33myellow \\e[34mblue \\e[35mmagenta \\e[36mcyan\\e[0m\\n'; " +
            "printf '\\e[38;2;255;128;0mtruecolor orange\\e[0m \\e[44;97m white on blue \\e[0m \\e[7m inverse \\e[0m \\e[4munderline\\e[0m\\n'; " +
            "printf 'wide: 漢字 emoji: 😀 box: ┌─┐ └─┘\\n'; seq -s ' ' 1 40; echo; stty size\r");
        await WaitUntil(() => ScreenContains(tab.FocusedPane.Session, "truecolor orange") && ScreenContains(tab.FocusedPane.Session, " 40"), "output");
        await Pump(300);

        var terminal = window.GetVisualDescendants().OfType<TerminalControl>().Single();
        Assert.True(terminal.Bounds.Width > 200);
        int rows, cols;
        lock (tab.FocusedPane.Session.Emulator.SyncRoot)
            (rows, cols) = (tab.FocusedPane.Session.Emulator.Terminal.Rows, tab.FocusedPane.Session.Emulator.Terminal.Cols);
        Assert.True(ScreenContains(tab.FocusedPane.Session, $"{rows} {cols}"), "server pty size should match the rendered terminal");
        Save(window, "05-terminal");

        // Closing a connected tab asks for confirmation; disconnect first so no dialog opens.
        tab.FocusedPane.Session.Disconnect();
        await WaitUntil(() => tab.FocusedPane.Session.State == SessionState.Disconnected, "disconnected");
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
        await WaitUntil(() => first.FocusedPane.Session.State == SessionState.Connected && second.FocusedPane.Session.State == SessionState.Connected, "both connected");
        await Pump(200);

        // Mouse: Servers -> second terminal -> first terminal (terminal-to-terminal reuses the view).
        ClickTab(window, vm.Servers);
        await Pump(100);
        Assert.Null(FocusedSession(window));
        ClickTab(window, second);
        await Pump(100);
        Assert.Same(second.FocusedPane.Session, FocusedSession(window));
        ClickTab(window, first);
        await Pump(100);
        Assert.Same(first.FocusedPane.Session, FocusedSession(window));

        // Typing goes to the focused tab's server.
        window.KeyTextInput("echo typed-into-first");
        window.KeyPress(Key.Enter, RawInputModifiers.None);
        await WaitUntil(() => ScreenContainsLine(first.FocusedPane.Session, "typed-into-first"), "typed text echoed");
        Assert.False(ScreenContainsLine(second.FocusedPane.Session, "typed-into-first"));

        // Keyboard shortcuts, pressed while a terminal has focus.
        window.KeyPress(Key.Tab, RawInputModifiers.Control);
        await Pump(100);
        Assert.Same(second, vm.SelectedTab);
        Assert.Same(second.FocusedPane.Session, FocusedSession(window));
        window.KeyPress(Key.Tab, RawInputModifiers.Control);
        await Pump(100);
        Assert.Same(vm.Servers, vm.SelectedTab); // wraps around
        window.KeyPress(Key.PageUp, RawInputModifiers.Control);
        await Pump(100);
        Assert.Same(second, vm.SelectedTab);
        window.KeyPress(Key.D2, RawInputModifiers.Alt);
        await Pump(100);
        Assert.Same(first, vm.SelectedTab);
        Assert.Same(first.FocusedPane.Session, FocusedSession(window));
        window.KeyPress(Key.D9, RawInputModifiers.Alt);
        await Pump(100);
        Assert.Same(second, vm.SelectedTab);

        window.KeyPress(Key.L, RawInputModifiers.Control | RawInputModifiers.Shift);
        await Pump(100);
        Assert.True(vm.IsLocked);

        foreach (var tab in new[] { first, second })
            tab.FocusedPane.Session.Disconnect();
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
        await WaitUntil(() => tab.FocusedPane.Session.State == SessionState.Connected, "connected via bastion");
        Assert.Contains("via bastion", tab.FocusedPane.Endpoint);
        tab.FocusedPane.Session.Send("echo hello-from-inside\r");
        await WaitUntil(() => ScreenContainsLine(tab.FocusedPane.Session, "hello-from-inside"), "output via bastion");

        // Both host keys were remembered on their own servers.
        await Pump(100);
        Assert.All(vm.Data!.Servers, s => Assert.NotNull(s.HostKeyFingerprint));

        tab.FocusedPane.Session.Disconnect();
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
        Assert.StartsWith("Default (zSSH Dark)", editor.ThemeOptions[0].Label);
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
        await WaitUntil(() => tab.FocusedPane.Session.State == SessionState.Connected, "connected");
        await Pump(300);
        tab.FocusedPane.Session.Send("clear; printf '\\e[31mred \\e[32mgreen \\e[34mblue \\e[1;37mbold white\\e[0m \\e[7m inverse \\e[0m\\n'; ls -la /\r");
        await WaitUntil(() => ScreenContains(tab.FocusedPane.Session, "bold white"), "output");
        await Pump(300);
        Assert.Equal("Amber (P3 phosphor)", tab.FocusedPane.Theme.Name);
        AssertTerminalBackground(window, Color.Parse("#120A00"));
        Save(window, "09-amber");

        // Changing the server's theme updates the open tab.
        vm.SelectedTab = vm.Servers;
        vm.Servers.Editor!.SelectedTheme = vm.Servers.Editor.ThemeOptions.Single(o => o.Name == "Green (P1 phosphor)");
        vm.Servers.SaveServerCommand.Execute(null);
        Assert.Equal("Green (P1 phosphor)", tab.FocusedPane.Theme.Name);
        vm.SelectedTab = tab;
        await Pump(300);
        AssertTerminalBackground(window, Color.Parse("#011A07"));
        Save(window, "10-green");

        tab.FocusedPane.Session.Disconnect();
        await sessions.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task SplitPanesNavigateBroadcastAndClose()
    {
        var server = SshTestServer.TryStart();
        if (server == null)
            return; // python3/paramiko not available
        _dispose.Add(server);

        var (window, vm, sessions) = Create();
        await CreateVault(vm);
        var profile = AddServer(vm, "web1", "", "127.0.0.1", Environment.UserName, port: server.Port, password: SshTestServer.Password);
        vm.Connect(profile);
        var tab = Assert.IsType<TerminalTabViewModel>(vm.SelectedTab);
        await WaitUntil(() => tab.FocusedPane.Session.State == SessionState.Connected, "first pane");
        await Pump(200);
        Assert.False(tab.HasMultiplePanes);

        // Ctrl+Shift+E: split right with the same server; the new pane gets the keyboard.
        window.KeyPress(Key.E, RawInputModifiers.Control | RawInputModifiers.Shift);
        await WaitUntil(() => tab.Panes.Count == 2 && tab.Panes.All(p => p.Session.State == SessionState.Connected), "split right");
        await Pump(200);
        var (left, right) = (tab.Panes[0], tab.Panes[1]);
        Assert.Same(right, tab.FocusedPane);
        Assert.Same(right.Session, FocusedSession(window));
        Assert.Equal("web1 | web1", tab.Title);

        // Alt+Left moves to the left pane; typing goes only there.
        window.KeyPress(Key.Left, RawInputModifiers.Alt);
        await Pump(100);
        Assert.Same(left.Session, FocusedSession(window));
        Assert.Same(left, tab.FocusedPane);
        window.KeyTextInput("echo only-left");
        window.KeyPress(Key.Enter, RawInputModifiers.None);
        await WaitUntil(() => ScreenContainsLine(left.Session, "only-left"), "left output");
        await Pump(300);
        Assert.False(ScreenContainsLine(right.Session, "only-left"));

        // Ctrl+Shift+B: broadcast to every pane.
        window.KeyPress(Key.B, RawInputModifiers.Control | RawInputModifiers.Shift);
        Assert.True(tab.IsBroadcasting);
        window.KeyTextInput("echo to-both-panes");
        window.KeyPress(Key.Enter, RawInputModifiers.None);
        await WaitUntil(() => ScreenContainsLine(left.Session, "to-both-panes") && ScreenContainsLine(right.Session, "to-both-panes"), "broadcast output");
        await Pump(200);
        Save(window, "11-split-broadcast");
        window.KeyPress(Key.B, RawInputModifiers.Control | RawInputModifiers.Shift);
        Assert.False(tab.IsBroadcasting);

        // Ctrl+Shift+O on the left pane: split down. Then navigate the 3-pane layout.
        window.KeyPress(Key.O, RawInputModifiers.Control | RawInputModifiers.Shift);
        await WaitUntil(() => tab.Panes.Count == 3 && tab.Panes.All(p => p.Session.State == SessionState.Connected), "split down");
        await Pump(200);
        var bottom = tab.Panes[2];
        Assert.Same(bottom.Session, FocusedSession(window));
        window.KeyPress(Key.Up, RawInputModifiers.Alt);
        await Pump(100);
        Assert.Same(left.Session, FocusedSession(window));
        window.KeyPress(Key.Right, RawInputModifiers.Alt);
        await Pump(100);
        Assert.Same(right.Session, FocusedSession(window));
        window.KeyPress(Key.Left, RawInputModifiers.Alt);
        window.KeyPress(Key.Down, RawInputModifiers.Alt);
        await Pump(100);
        Assert.Same(bottom.Session, FocusedSession(window));
        Save(window, "12-split-three");

        // Ctrl+Shift+W closes the focused pane (disconnected first, so no confirmation dialog).
        bottom.Session.Disconnect();
        await WaitUntil(() => bottom.Session.State == SessionState.Disconnected, "bottom disconnected");
        window.KeyPress(Key.W, RawInputModifiers.Control | RawInputModifiers.Shift);
        await WaitUntil(() => tab.Panes.Count == 2, "pane closed");
        await Pump(200);
        Assert.Equal(new[] { left, right }, tab.Panes);
        Assert.NotNull(FocusedSession(window));

        foreach (var pane in tab.Panes)
            pane.Session.Disconnect();
        await sessions.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task SelectingTwoServersConnectsThemSideBySide()
    {
        var server = SshTestServer.TryStart();
        if (server == null)
            return; // python3/paramiko not available
        _dispose.Add(server);

        var (window, vm, sessions) = Create();
        await CreateVault(vm);
        // Two "load-balanced" servers (the same test server under two names), one amber, one green.
        AddServer(vm, "lb-node-a", "Load balanced", "127.0.0.1", Environment.UserName, port: server.Port, password: SshTestServer.Password);
        vm.Servers.Editor!.SelectedTheme = vm.Servers.Editor.ThemeOptions.Single(o => o.Name == "Amber (P3 phosphor)");
        vm.Servers.SaveServerCommand.Execute(null);
        AddServer(vm, "lb-node-b", "Load balanced", "127.0.0.1", Environment.UserName, port: server.Port, password: SshTestServer.Password);
        vm.Servers.Editor!.SelectedTheme = vm.Servers.Editor.ThemeOptions.Single(o => o.Name == "Green (P1 phosphor)");
        vm.Servers.SaveServerCommand.Execute(null);

        // Ctrl+click both in the list.
        await Pump(100);
        var list = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "ServerList");
        list.SelectedItems!.Clear();
        foreach (var item in vm.Servers.FilteredServers)
            list.SelectedItems.Add(item);
        await Pump(100);
        Assert.Equal(2, vm.Servers.SelectedServers.Count);
        Assert.Equal("Connect 2 side by side", vm.Servers.ConnectLabel);

        await vm.Servers.ConnectCommand.ExecuteAsync(null);
        var tab = Assert.IsType<TerminalTabViewModel>(vm.SelectedTab);
        Assert.Equal(2, tab.Panes.Count);
        Assert.Equal("lb-node-a | lb-node-b", tab.Title);
        Assert.Equal(new[] { "Amber (P3 phosphor)", "Green (P1 phosphor)" }, tab.Panes.Select(p => p.Theme.Name));
        await WaitUntil(() => tab.Panes.All(p => p.Session.State == SessionState.Connected), "both connected");
        await Pump(300);

        tab.IsBroadcasting = true;
        tab.FocusedPane.SendInput("clear; echo \"$(date +%H:%M) checking both nodes\"; uptime; df -h / | tail -n 1\r");
        await WaitUntil(() => tab.Panes.All(p => ScreenContains(p.Session, "checking both nodes")), "output in both");
        await Pump(300);
        Save(window, "13-pair-side-by-side");

        foreach (var pane in tab.Panes)
            pane.Session.Disconnect();
        await sessions.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task CombiningTabsKeepsSessionsConnectedAndScreensIntact()
    {
        var server = SshTestServer.TryStart();
        if (server == null)
            return; // python3/paramiko not available
        _dispose.Add(server);

        var (window, vm, sessions) = Create();
        await CreateVault(vm);
        var web1 = AddServer(vm, "web1", "", "127.0.0.1", Environment.UserName, port: server.Port, password: SshTestServer.Password);
        var web2 = AddServer(vm, "web2", "", "127.0.0.1", Environment.UserName, port: server.Port, password: SshTestServer.Password);
        vm.Connect(web1);
        var tabA = (TerminalTabViewModel)vm.SelectedTab!;
        vm.Connect(web2);
        var tabB = (TerminalTabViewModel)vm.SelectedTab!;
        var (sessionA, sessionB) = (tabA.FocusedPane.Session, tabB.FocusedPane.Session);
        await WaitUntil(() => sessionA.State == SessionState.Connected && sessionB.State == SessionState.Connected, "both tabs");
        sessionA.Send("echo marker-from-tab-a\r");
        sessionB.Send("echo marker-from-tab-b\r");
        await WaitUntil(() => ScreenContainsLine(sessionA, "marker-from-tab-a") && ScreenContainsLine(sessionB, "marker-from-tab-b"), "markers");
        var (sinceA, sinceB) = (sessionA.ConnectedSince, sessionB.ConnectedSince);
        await Pump(100);

        // Ctrl+click tab A's header while B is selected: marks A without switching to it.
        ClickTab(window, tabA, RawInputModifiers.Control);
        await Pump(100);
        Assert.Same(tabB, vm.SelectedTab);
        Assert.True(tabA.IsMarked);
        Assert.Equal("Combine 2 tabs into a split", vm.CombineLabel);
        Save(window, "14-tabs-marked");

        vm.CombineMarkedCommand.Execute(null);
        await Pump(300);
        var combined = Assert.Single(vm.Tabs.OfType<TerminalTabViewModel>());
        Assert.Equal(new[] { sessionA, sessionB }, combined.Panes.Select(p => p.Session));
        Assert.Equal("web1 | web2", combined.Title);
        Assert.Same(sessionB, combined.FocusedPane.Session); // B was the selected tab
        Assert.Same(sessionB, FocusedSession(window));
        // Re-hosted, not reconnected: same connections, earlier output still on screen.
        Assert.Equal((sinceA, sinceB), (sessionA.ConnectedSince, sessionB.ConnectedSince));
        Assert.True(ScreenContainsLine(sessionA, "marker-from-tab-a"));
        Assert.True(ScreenContainsLine(sessionB, "marker-from-tab-b"));
        Assert.False(tabA.IsMarked);
        Save(window, "15-tabs-combined");

        // Pane menu "Move to new tab", then combine with a tab that is itself split.
        vm.MovePaneToNewTab(combined, combined.Panes[0]);
        Assert.Equal(2, vm.Tabs.OfType<TerminalTabViewModel>().Count());
        var movedOut = (TerminalTabViewModel)vm.SelectedTab!;
        Assert.Same(sessionA, movedOut.FocusedPane.Session);
        vm.Split(combined, combined.Panes[0], Avalonia.Layout.Orientation.Vertical);
        await WaitUntil(() => combined.Panes.All(p => p.Session.State == SessionState.Connected), "split");

        vm.CombineTabs(new[] { movedOut, combined });
        var three = Assert.Single(vm.Tabs.OfType<TerminalTabViewModel>());
        // Tabs combine in on-screen order: the split tab (left), then the moved-out pane (right).
        var root = Assert.IsType<SplitViewModel>(three.Root);
        Assert.Equal(Avalonia.Layout.Orientation.Horizontal, root.Orientation);
        Assert.Equal(Avalonia.Layout.Orientation.Vertical, Assert.IsType<SplitViewModel>(root.First).Orientation); // kept its stack
        Assert.Same(sessionA, Assert.IsType<TerminalPaneViewModel>(root.Second).Session);

        vm.SeparatePanes(three);
        Assert.Equal(3, vm.Tabs.OfType<TerminalTabViewModel>().Count());
        Assert.All(vm.Tabs.OfType<TerminalTabViewModel>(), t => Assert.Equal(SessionState.Connected, t.FocusedPane.Session.State));
        Assert.Equal((sinceA, sinceB), (sessionA.ConnectedSince, sessionB.ConnectedSince));

        foreach (var tab in vm.Tabs.OfType<TerminalTabViewModel>())
            tab.FocusedPane.Session.Disconnect();
        await sessions.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task AskEveryTimePassphraseIsNeverStoredAndReusedInMemory()
    {
        const string passphrase = "correct key passphrase";
        var server = SshTestServer.TryStart(clientKeyPassphrase: passphrase);
        if (server == null)
            return; // python3/paramiko not available
        _dispose.Add(server);

        var prompts = 0;
        var provider = new CachingPassphraseProvider((_, _, _) =>
        {
            Interlocked.Increment(ref prompts);
            return Task.FromResult<string?>(passphrase);
        });
        var (window, vm, sessions) = Create(provider);
        vm.Locked += provider.Clear; // as App wires it
        await CreateVault(vm);

        vm.Servers.AddServerCommand.Execute(null);
        var editor = vm.Servers.Editor!;
        (editor.Name, editor.Host, editor.Port, editor.Username) = ("keyed", "127.0.0.1", server.Port, Environment.UserName);
        editor.AuthMethodIndex = (int)AuthMethod.PrivateKey;
        editor.PrivateKey = server.ClientPrivateKey!;
        vm.Servers.SaveServerCommand.Execute(null);
        Assert.Contains("could not be loaded", editor.ValidationError); // encrypted key, no passphrase

        editor.PrivateKeyPassphrase = "typed then thought better of it";
        editor.AskForPassphrase = true;
        Assert.False(editor.ShowStoredPassphrase);
        vm.Servers.SaveServerCommand.Execute(null);
        Assert.Null(editor.ValidationError);
        var saved = vm.Data!.Servers.Single();
        Assert.True(saved.AskForPassphrase);
        Assert.Null(saved.PrivateKeyPassphrase);
        Save(window, "16-ask-passphrase-editor");

        vm.Connect(saved);
        var tab = (TerminalTabViewModel)vm.SelectedTab!;
        await WaitUntil(() => tab.FocusedPane.Session.State == SessionState.Connected, "connected with prompted passphrase");
        Assert.Equal(1, prompts);

        vm.Split(tab, tab.FocusedPane, Avalonia.Layout.Orientation.Horizontal);
        await WaitUntil(() => tab.Panes.All(p => p.Session.State == SessionState.Connected), "split");
        Assert.Equal(1, prompts); // the split reused the in-memory answer

        vm.Lock();
        vm.Password = AdminPassword;
        await vm.UnlockCommand.ExecuteAsync(null);
        vm.Split(tab, tab.FocusedPane, Avalonia.Layout.Orientation.Vertical);
        await WaitUntil(() => tab.Panes.Count == 3 && tab.Panes.All(p => p.Session.State == SessionState.Connected), "split after lock");
        Assert.Equal(2, prompts); // locking forgot it

        Assert.DoesNotContain(passphrase, File.ReadAllText(Path.Combine(_dir, "vault.json")));
        Assert.All(vm.Data!.Servers, s => Assert.Null(s.PrivateKeyPassphrase));

        foreach (var pane in tab.Panes)
            pane.Session.Disconnect();
        await sessions.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task ShellExitClosesOrKeepsThePanePerServerAndDefault()
    {
        var server = SshTestServer.TryStart();
        if (server == null)
            return; // python3/paramiko not available
        _dispose.Add(server);

        var (window, vm, sessions) = Create();
        await CreateVault(vm);
        var keeper = AddServer(vm, "keeper", "", "127.0.0.1", Environment.UserName, port: server.Port, password: SshTestServer.Password);
        AddServer(vm, "closer", "", "127.0.0.1", Environment.UserName, port: server.Port, password: SshTestServer.Password);
        var editor = vm.Servers.Editor!;
        Assert.Equal("Default (Keep the pane open (show Reconnect))", editor.ShellExitOptions[0].Label);
        editor.SelectedShellExit = editor.ShellExitOptions.Single(o => o.Value == ShellExitAction.Close);
        vm.Servers.SaveServerCommand.Execute(null);
        var closer = vm.Data!.Servers.Single(s => s.Name == "closer");
        Assert.Equal(ShellExitAction.Close, closer.OnShellExit);

        // Side by side: "closer" (override: close) and "keeper" (default: keep open).
        vm.ConnectTogether(new[] { keeper, closer });
        var tab = (TerminalTabViewModel)vm.SelectedTab!;
        await WaitUntil(() => tab.Panes.All(p => p.Session.State == SessionState.Connected), "both connected");
        var (keeperPane, closerPane) = (tab.Panes[0], tab.Panes[1]);

        closerPane.Session.Send("exit\r");
        await WaitUntil(() => tab.Panes.Count == 1, "closer pane closed");
        Assert.Same(keeperPane, tab.Panes.Single());
        Assert.Equal(SessionState.Closed, closerPane.Session.State);

        keeperPane.Session.Send("exit\r");
        await WaitUntil(() => keeperPane.Session.State == SessionState.Disconnected, "keeper exited");
        await Pump(1500);
        Assert.Contains(tab, vm.Tabs); // kept open, with the Reconnect banner
        Assert.True(keeperPane.ShowBanner && keeperPane.CanReconnect);
        await vm.CloseTabAsync(tab);

        // Global default "close": the last pane closing closes the tab.
        vm.Servers.ShowSettingsCommand.Execute(null);
        vm.Servers.DefaultShellExit = vm.Servers.SettingsShellExitOptions.Single(o => o.Value == ShellExitAction.Close);
        vm.Servers.SaveSettingsCommand.Execute(null);
        Assert.Equal(ShellExitAction.Close, vm.Settings.OnShellExit);
        vm.Connect(keeper);
        var single = (TerminalTabViewModel)vm.SelectedTab!;
        await WaitUntil(() => single.FocusedPane.Session.State == SessionState.Connected, "keeper again");
        single.FocusedPane.Session.Send("exit\r");
        await WaitUntil(() => !vm.Tabs.Contains(single), "tab closed on exit");

        // A dropped connection never auto-closes, even with "close" set.
        vm.Connect(keeper);
        var dropped = (TerminalTabViewModel)vm.SelectedTab!;
        await WaitUntil(() => dropped.FocusedPane.Session.State == SessionState.Connected, "keeper third time");
        server.Stop();
        await WaitUntil(() => dropped.FocusedPane.Session.State == SessionState.Reconnecting, "dropped");
        await Pump(1500);
        Assert.Contains(dropped, vm.Tabs);

        dropped.FocusedPane.Session.Disconnect();
        await sessions.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task SftpOnlyServerOpensDualPaneBrowserAndTransfers()
    {
        var server = SshTestServer.TryStart(sftp: true);
        if (server == null)
            return; // python3/paramiko not available
        _dispose.Add(server);
        var local = Path.Combine(_dir, "local");
        Directory.CreateDirectory(Path.Combine(local, "site", "css"));
        File.WriteAllText(Path.Combine(local, "site", "index.html"), "<h1>hi</h1>");
        File.WriteAllText(Path.Combine(local, "site", "css", "main.css"), "body{}");
        File.WriteAllBytes(Path.Combine(local, "backup.tar"), new byte[3_000_000]);
        Directory.CreateDirectory(Path.Combine(server.SftpRoot!, "var", "www"));
        File.WriteAllText(Path.Combine(server.SftpRoot!, "var", "www", "remote.txt"), "from the server");

        var (window, vm, sessions) = Create();
        await CreateVault(vm);
        vm.Servers.AddServerCommand.Execute(null);
        var editor = vm.Servers.Editor!;
        editor.SelectedKind = editor.KindOptions.Single(k => k.Kind == ServerKind.SftpOnly);
        Assert.False(editor.ShowTerminalFields);
        Assert.True(editor.ShowSshFields);
        (editor.Name, editor.Host, editor.Port, editor.Username, editor.Password) =
            ("files.example", "127.0.0.1", server.Port, Environment.UserName, SshTestServer.Password);
        vm.Servers.SaveServerCommand.Execute(null);
        Assert.Null(editor.ValidationError);
        var item = vm.Servers.FilteredServers.Single();
        Assert.Equal("SFTP", item.KindBadge);
        vm.Servers.SelectedServer = item;
        Assert.Equal("Open files", vm.Servers.ConnectLabel);

        // Connect on an SFTP-only server opens the file browser.
        await vm.Servers.ConnectCommand.ExecuteAsync(null);
        var tab = Assert.IsType<FilesTabViewModel>(vm.SelectedTab);
        await WaitUntil(() => tab.Remote.CurrentPath == "/" && tab.Local.CurrentPath.Length > 0, "both panes listed");
        await tab.Local.NavigateAsync(local);
        await tab.Remote.NavigateAsync("/var/www");
        await Pump(200);
        Assert.Equal(new[] { "site", "backup.tar" }, tab.Local.Rows.Select(r => r.Name));
        Assert.Equal("remote.txt", Assert.Single(tab.Remote.Rows).Name);
        Assert.Null(tab.Remote.Error);

        // Upload a folder and a file from the left pane into /var/www.
        tab.Local.Selected = tab.Local.Rows.ToList();
        await tab.UploadCommand.ExecuteAsync(null);
        await WaitUntil(() => tab.Transfers.Count == 3, "three files queued");
        Assert.True(vm.ActiveTransfers >= 0);
        await WaitUntil(() => tab.Queue.ActiveCount == 0, "uploads to finish");
        await Pump(800); // let the progress panel and listings refresh
        Assert.All(tab.Transfers, t => Assert.Equal("Done", t.StatusText));
        Assert.Equal("body{}", File.ReadAllText(Path.Combine(server.SftpRoot!, "var", "www", "site", "css", "main.css")));
        Assert.Equal(3_000_000, new FileInfo(Path.Combine(server.SftpRoot!, "var", "www", "backup.tar")).Length);
        await WaitUntil(() => tab.Remote.Rows.Count == 3, "remote listing refreshed");
        Assert.Equal(0, vm.ActiveTransfers);
        Save(window, "17-files-sftp");

        // Download the server's file to the left pane.
        tab.Remote.Selected = tab.Remote.Rows.Where(r => r.Name == "remote.txt").ToList();
        await tab.DownloadCommand.ExecuteAsync(null);
        await WaitUntil(() => tab.Queue.ActiveCount == 0 && File.Exists(Path.Combine(local, "remote.txt")), "download");
        Assert.Equal("from the server", File.ReadAllText(Path.Combine(local, "remote.txt")));

        await vm.CloseFilesTabAsync(tab);
        Assert.DoesNotContain(tab, vm.Tabs);
        await sessions.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task S3BucketOpensInFileBrowserAndUploads()
    {
        var s3 = S3TestServer.TryStart();
        if (s3 == null)
            return; // moto_server not available
        _dispose.Add(s3);
        var bucket = s3.CreateBucket("ui-bucket");
        var local = Path.Combine(_dir, "to-upload");
        Directory.CreateDirectory(local);
        File.WriteAllText(Path.Combine(local, "notes.txt"), "uploaded from zSSH");

        var (window, vm, sessions) = Create();
        await CreateVault(vm);
        vm.Servers.AddServerCommand.Execute(null);
        var editor = vm.Servers.Editor!;
        editor.SelectedKind = editor.KindOptions.Single(k => k.Kind == ServerKind.S3);
        (editor.Name, editor.S3Bucket, editor.S3Region, editor.S3AccessKeyId, editor.S3SecretAccessKey, editor.S3ServiceUrl) =
            ("Backups bucket", "ui-bucket", "us-east-1", bucket.S3AccessKeyId!, bucket.S3SecretAccessKey!, s3.Url);
        vm.Servers.SaveServerCommand.Execute(null);
        Assert.Null(editor.ValidationError);
        vm.Servers.SelectedServer = vm.Servers.FilteredServers.Single();

        await vm.Servers.ConnectCommand.ExecuteAsync(null);
        var tab = Assert.IsType<FilesTabViewModel>(vm.SelectedTab);
        await WaitUntil(() => tab.Remote.CurrentPath == "/" && tab.Local.CurrentPath.Length > 0, "bucket listed");
        Assert.False(tab.Remote.SupportsPermissions);
        await tab.Local.NavigateAsync(local);
        await Pump(100);

        tab.Local.Selected = tab.Local.Rows.ToList();
        await tab.UploadCommand.ExecuteAsync(null);
        await WaitUntil(() => tab.Transfers.Count == 1 && tab.Queue.ActiveCount == 0, "upload");
        await WaitUntil(() => tab.Remote.Rows.Any(r => r.Name == "notes.txt"), "bucket listing refreshed");
        Assert.Equal("Done", tab.Transfers.Single().StatusText);
        Assert.Equal("STANDARD", tab.Remote.Rows.Single().Owner); // storage class in the last column
        await Pump(300);
        Save(window, "19-files-s3");

        await vm.CloseFilesTabAsync(tab);
        await sessions.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task S3WithoutBucketBrowsesAllBuckets()
    {
        var s3 = S3TestServer.TryStart();
        if (s3 == null)
            return; // moto_server not available
        _dispose.Add(s3);
        var first = s3.CreateBucket("company-backups");
        s3.CreateBucket("company-logs");
        s3.CreateBucket("website-assets");

        var (window, vm, sessions) = Create();
        await CreateVault(vm);
        vm.Servers.AddServerCommand.Execute(null);
        var editor = vm.Servers.Editor!;
        editor.SelectedKind = editor.KindOptions.Single(k => k.Kind == ServerKind.S3);
        (editor.Name, editor.S3AccessKeyId, editor.S3SecretAccessKey, editor.S3ServiceUrl) =
            ("AWS account", first.S3AccessKeyId!, first.S3SecretAccessKey!, s3.Url);
        vm.Servers.SaveServerCommand.Execute(null);
        Assert.Null(editor.ValidationError);
        var item = vm.Servers.FilteredServers.Single();
        Assert.Equal("s3:// all buckets", item.Address);
        vm.Servers.SelectedServer = item;

        await vm.Servers.ConnectCommand.ExecuteAsync(null);
        var tab = Assert.IsType<FilesTabViewModel>(vm.SelectedTab);
        await WaitUntil(() => tab.Remote.Rows.Count == 3, "bucket list");
        Assert.Equal(new[] { "company-backups", "company-logs", "website-assets" }, tab.Remote.Rows.Select(r => r.Name));
        Assert.All(tab.Remote.Rows, r => Assert.True(r.Entry.IsDirectory));
        Assert.NotNull(tab.Remote.FileSystem.ReadOnlyReason("/company-logs")); // buckets are read-only
        await Pump(200);
        Save(window, "20-s3-all-buckets");

        await tab.Remote.OpenAsync(tab.Remote.Rows.First(r => r.Name == "company-logs"));
        Assert.Equal("/company-logs", tab.Remote.CurrentPath);
        Assert.Null(tab.Remote.Error);

        await vm.CloseFilesTabAsync(tab);
        await sessions.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task ForgottenPasswordStartsAgainAndKeepsTheOldVault()
    {
        var (window, vm, _) = Create();
        await CreateVault(vm);
        AddServer(vm, "will be set aside", "", "old.example", "root");
        vm.Lock();
        Assert.False(vm.IsSetupRequired);
        await Pump(100);
        Save(window, "21-unlock-forgot-link");

        var aside = vm.ResetForgottenVault();

        Assert.True(vm.IsSetupRequired && vm.IsLocked);
        Assert.True(File.Exists(aside));
        vm.Password = vm.ConfirmPassword = "brand new password";
        await vm.UnlockCommand.ExecuteAsync(null);
        Assert.False(vm.IsLocked);
        Assert.Empty(vm.Data!.Servers);
        Assert.Empty(vm.Servers.FilteredServers);
    }

    [AvaloniaFact]
    public async Task LicencesWindowShowsBundledNotices()
    {
        var notices = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt");
        Assert.False(File.Exists(notices));
        Assert.Contains("development build", LicensesWindow.Load()); // not packaged: says where they come from

        File.WriteAllText(notices, "zSSH: THIRD-PARTY NOTICES\n\nSSH.NET 2026.0.0\nLicence: MIT");
        try
        {
            Assert.StartsWith("zSSH: THIRD-PARTY NOTICES", LicensesWindow.Load());
            var window = new LicensesWindow();
            window.Show();
            await Pump(100);
            Assert.Contains(window.GetVisualDescendants().OfType<SelectableTextBlock>(), t => t.Text?.Contains("SSH.NET") == true);
            window.Close();
        }
        finally
        {
            File.Delete(notices);
        }
    }

    [Fact]
    public void TransferSpeedsAreShownInBitsPerSecond()
    {
        Assert.Equal("800 kbps", TransferRowViewModel.FormatBitRate(100_000));
        Assert.Equal("100 Mbps", TransferRowViewModel.FormatBitRate(12_500_000));
        Assert.Equal("9.6 Mbps", TransferRowViewModel.FormatBitRate(1_200_000));
        Assert.Equal("1.2 Gbps", TransferRowViewModel.FormatBitRate(150_000_000));
        Assert.Equal("400 bps", TransferRowViewModel.FormatBitRate(50));
    }

    [AvaloniaFact]
    public async Task ServersAreReadOnlyUntilEditIsPressed()
    {
        var (window, vm, _) = Create();
        await CreateVault(vm);
        vm.Servers.AddServerCommand.Execute(null);
        Assert.True(vm.Servers.Editor!.IsEditing); // new servers start in edit mode
        (vm.Servers.Editor.Name, vm.Servers.Editor.Host, vm.Servers.Editor.Username) = ("web1", "web1.example", "deploy");
        vm.Servers.SaveServerCommand.Execute(null);
        Assert.False(vm.Servers.Editor.IsEditing); // saving returns to viewing

        // Selecting a server shows it read-only.
        vm.Servers.SelectedServer = null;
        vm.Servers.SelectedServer = vm.Servers.FilteredServers.Single();
        var editor = vm.Servers.Editor!;
        Assert.False(editor.IsEditing);
        await Pump(100);
        Save(window, "22-server-view-mode");

        // Edit, change, Cancel: nothing changes.
        vm.Servers.EditServerCommand.Execute(null);
        Assert.True(vm.Servers.Editor!.IsEditing);
        vm.Servers.Editor.Host = "changed.example";
        vm.Servers.RevertServerCommand.Execute(null);
        Assert.False(vm.Servers.Editor!.IsEditing);
        Assert.Equal("web1.example", vm.Servers.Editor.Host);
        Assert.Equal("web1.example", vm.Data!.Servers.Single().Host);

        // Edit, change, Save.
        vm.Servers.EditServerCommand.Execute(null);
        vm.Servers.Editor!.Host = "web1-new.example";
        vm.Servers.SaveServerCommand.Execute(null);
        Assert.False(vm.Servers.Editor.IsEditing);
        Assert.Equal("web1-new.example", vm.Data.Servers.Single().Host);
    }

    [AvaloniaFact]
    public async Task S3RegionIsPickedFromAListOrTyped()
    {
        var (window, vm, _) = Create();
        await CreateVault(vm);
        vm.Servers.AddServerCommand.Execute(null);
        var editor = vm.Servers.Editor!;
        editor.SelectedKind = editor.KindOptions.Single(k => k.Kind == ServerKind.S3);
        (editor.S3AccessKeyId, editor.S3SecretAccessKey) = ("AKIAEXAMPLE", "secret");

        // Europe first, then the USA, then the rest of the world; "Other" last.
        var codes = editor.RegionOptions.Select(r => r.Code).ToList();
        Assert.StartsWith("eu-", codes[0]);
        var firstUs = codes.FindIndex(c => c.StartsWith("us-"));
        Assert.True(codes.Take(firstUs).All(c => c.StartsWith("eu-")));
        Assert.True(codes.Skip(firstUs).TakeWhile(c => c.StartsWith("us-")).Count() == 4);
        Assert.True(editor.RegionOptions[^1].IsOther);

        editor.SelectedRegion = editor.RegionOptions.Single(r => r.Code == "eu-west-2");
        Assert.False(editor.IsCustomRegion);
        vm.Servers.SaveServerCommand.Execute(null);
        Assert.Equal("eu-west-2", vm.Data!.Servers.Single().S3Region);

        // A region not in the list (e.g. Cloudflare R2's "auto") via Other.
        vm.Servers.EditServerCommand.Execute(null);
        editor = vm.Servers.Editor!;
        editor.SelectedRegion = RegionOption.Other;
        Assert.True(editor.IsCustomRegion);
        vm.Servers.SaveServerCommand.Execute(null);
        Assert.Contains("Choose a region", editor.ValidationError);
        editor.CustomRegion = "auto";
        vm.Servers.SaveServerCommand.Execute(null);
        Assert.Null(editor.ValidationError);
        Assert.Equal("auto", vm.Data.Servers.Single().S3Region);

        // Reopened, it shows as Other with the typed code.
        vm.Servers.SelectedServer = null;
        vm.Servers.SelectedServer = vm.Servers.FilteredServers.Single();
        Assert.True(vm.Servers.Editor!.IsCustomRegion);
        Assert.Equal("auto", vm.Servers.Editor.CustomRegion);
    }

    [AvaloniaFact]
    public async Task ClosingATabGoesBackToThePreviousTab()
    {
        var server = SshTestServer.TryStart();
        if (server == null)
            return; // python3/paramiko not available
        _dispose.Add(server);
        var (window, vm, sessions) = Create();
        await CreateVault(vm);
        var profile = AddServer(vm, "box", "", "127.0.0.1", Environment.UserName, port: server.Port, password: SshTestServer.Password);
        vm.Connect(profile);
        var a = (TerminalTabViewModel)vm.SelectedTab!;
        vm.Connect(profile);
        var b = (TerminalTabViewModel)vm.SelectedTab!;
        vm.Connect(profile);
        var c = (TerminalTabViewModel)vm.SelectedTab!;
        await WaitUntil(() => new[] { a, b, c }.All(t => t.FocusedPane.Session.State == SessionState.Connected), "three tabs");

        // Tabs: Servers, A, B, C. Use A, then C; closing C returns to A (not its neighbour B).
        vm.SelectedTab = a;
        vm.SelectedTab = c;
        c.FocusedPane.Session.Disconnect();
        await WaitUntil(() => c.FocusedPane.Session.State == SessionState.Disconnected, "c disconnected");
        await vm.CloseTabAsync(c);
        Assert.Same(a, vm.SelectedTab);

        // Closing a tab that isn't showing leaves the current tab alone.
        b.FocusedPane.Session.Disconnect();
        await WaitUntil(() => b.FocusedPane.Session.State == SessionState.Disconnected, "b disconnected");
        await vm.CloseTabAsync(b);
        Assert.Same(a, vm.SelectedTab);

        a.FocusedPane.Session.Disconnect();
        await sessions.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task HelpWindowListsShortcuts()
    {
        var (window, vm, _) = Create();
        await CreateVault(vm);
        await Pump(100);
        Save(window, "23-header-version");
        var help = new HelpWindow { Width = 760, Height = 900 };
        help.Show();
        await Pump(200);
        var text = string.Join("\n", help.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
        Assert.Contains("Ctrl+Shift+E", text);
        Assert.Contains("Overwrite if different", text);
        Assert.Contains("Ctrl+Shift+H", text);
        Save(help, "24-help");
        help.Close();
    }

    [AvaloniaFact]
    public async Task ServerTypesShowTheRightFieldsAndValidate()
    {
        var (window, vm, _) = Create();
        await CreateVault(vm);
        vm.Servers.AddServerCommand.Execute(null);
        var editor = vm.Servers.Editor!;
        Assert.True(editor.ShowTerminalFields); // SSH by default

        editor.SelectedKind = editor.KindOptions.Single(k => k.Kind == ServerKind.S3);
        Assert.True(editor.ShowS3Fields);
        Assert.False(editor.ShowSshFields);
        vm.Servers.SaveServerCommand.Execute(null);
        Assert.Contains("Access key", editor.ValidationError); // the bucket is optional (all buckets)
        editor.S3Bucket = "my-bucket/backups";
        vm.Servers.SaveServerCommand.Execute(null);
        Assert.Contains("just the bucket name", editor.ValidationError);
        (editor.S3Bucket, editor.S3Prefix, editor.S3Region) = ("my-bucket", "/backups/2026/", "eu-west-2");
        vm.Servers.SaveServerCommand.Execute(null);
        Assert.Contains("Access key", editor.ValidationError);
        (editor.S3AccessKeyId, editor.S3SecretAccessKey) = ("AKIAEXAMPLE", "secret-example");
        vm.Servers.SaveServerCommand.Execute(null);
        Assert.Null(editor.ValidationError);
        var saved = vm.Data!.Servers.Single();
        Assert.Equal((ServerKind.S3, "backups/2026", "secret-example"), (saved.Kind, saved.S3Prefix, saved.S3SecretAccessKey));
        Assert.Null(saved.Password);
        var item = vm.Servers.FilteredServers.Single();
        Assert.Equal(("S3", "s3://my-bucket/backups/2026"), (item.KindBadge, item.Address));
        Assert.DoesNotContain("secret-example", File.ReadAllText(Path.Combine(_dir, "vault.json")));
        await Pump(100);
        Save(window, "18-s3-editor");
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

    private static void ClickTab(Window window, TabViewModel tab, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        var item = window.GetVisualDescendants().OfType<TabItem>().Single(t => ReferenceEquals(t.DataContext, tab));
        var center = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left, modifiers);
        window.MouseUp(center, MouseButton.Left, modifiers);
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

    private (MainWindow Window, MainWindowViewModel Vm, SessionManager Sessions) Create(IPassphraseProvider? passphrases = null)
    {
        Directory.CreateDirectory(_dir);
        var window = new MainWindow { Width = 1280, Height = 800 };
        var dialogs = new DialogService(window);
        var sessions = new SessionManager(new TrustAll(), passphrases);
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
        var dir = Environment.GetEnvironmentVariable("ZSSH_SCREENSHOT_DIR") ?? Path.Combine(Path.GetTempPath(), "zssh-screenshots");
        Directory.CreateDirectory(dir);
        frame!.Save(Path.Combine(dir, name + ".png"));
    }

    private sealed class TrustAll : IHostKeyVerifier
    {
        public Task<bool> VerifyAsync(HostKeyInfo hostKey, CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
