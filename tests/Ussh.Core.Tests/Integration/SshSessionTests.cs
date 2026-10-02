using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Ussh.Core.Models;
using Ussh.Core.Ssh;
using Xunit.Abstractions;

namespace Ussh.Core.Tests.Integration;

/// <summary>
/// End-to-end tests of <see cref="SshSession"/> against a real SSH server (see SshTestServer).
/// They return early, passing, when Python/paramiko aren't installed.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SshSessionTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly ITestOutputHelper _output;
    private readonly List<IAsyncDisposable> _cleanup = new();
    private readonly List<IDisposable> _servers = new();

    public SshSessionTests(ITestOutputHelper output) => _output = output;

    public void Dispose()
    {
        foreach (var item in _cleanup)
            item.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(10));
        foreach (var server in _servers)
            server.Dispose();
    }

    [Fact]
    public async Task PasswordLoginRunsCommands()
    {
        if (StartServer() is not { } server) return;
        var session = Open(server, PasswordProfile(server));

        await WaitForState(session, SessionState.Connected);
        session.Send("echo hello-$((6*7))\r");

        await WaitForScreen(session, "hello-42");
    }

    [Fact]
    public async Task Ed25519KeyLogin()
    {
        if (StartServer(withClientKey: true) is not { } server) return;
        var profile = PasswordProfile(server);
        profile.AuthMethod = AuthMethod.PrivateKey;
        profile.Password = null;
        profile.PrivateKey = server.ClientPrivateKey;
        Assert.Null(KeyValidator.Validate(profile.PrivateKey, null));

        var session = Open(server, profile);

        await WaitForState(session, SessionState.Connected);
        session.Send("echo key-login-ok\r");
        await WaitForScreen(session, "key-login-ok");
    }

    [Fact]
    public async Task WrongPasswordFailsWithoutRetrying()
    {
        if (StartServer() is not { } server) return;
        var profile = PasswordProfile(server);
        profile.Password = "wrong";
        var session = Open(server, profile);

        await WaitForState(session, SessionState.Failed);
        Assert.Contains("Authentication failed", session.StatusMessage);
        var changes = 0;
        session.StateChanged += _ => Interlocked.Increment(ref changes);
        await Task.Delay(3000);

        Assert.Equal(SessionState.Failed, session.State);
        Assert.Equal(0, changes); // no retry loop hammering the server with bad credentials
    }

    [Fact]
    public async Task UnknownHostKeyIsConfirmedThenRemembered()
    {
        if (StartServer() is not { } server) return;
        var verifier = new TestVerifier(accept: true);
        var profile = PasswordProfile(server);
        profile.HostKeyFingerprint = null;
        var trusted = new TaskCompletionSource<string>();
        var session = Open(server, profile, verifier);
        session.HostKeyTrusted += (_, _, fp) => trusted.TrySetResult(fp);

        await WaitForState(session, SessionState.Connected);

        var call = Assert.Single(verifier.Calls);
        Assert.False(call.IsChanged);
        Assert.Equal(call.Fingerprint, await trusted.Task.WaitAsync(Timeout));
        Assert.Equal(call.Fingerprint, session.Profile.HostKeyFingerprint);
    }

    [Fact]
    public async Task RejectedHostKeyStopsWithoutConnecting()
    {
        if (StartServer() is not { } server) return;
        var profile = PasswordProfile(server);
        profile.HostKeyFingerprint = null;
        var session = Open(server, profile, new TestVerifier(accept: false));

        await WaitForState(session, SessionState.Failed);

        Assert.Contains("Host key", session.StatusMessage);
    }

    [Fact]
    public async Task ChangedHostKeyIsReportedAsChanged()
    {
        if (StartServer() is not { } server) return;
        var verifier = new TestVerifier(accept: false);
        var profile = PasswordProfile(server);
        profile.HostKeyFingerprint = "not-the-real-fingerprint";
        var session = Open(server, profile, verifier);

        await WaitForState(session, SessionState.Failed);

        var call = Assert.Single(verifier.Calls);
        Assert.True(call.IsChanged);
        Assert.Equal("not-the-real-fingerprint", call.KnownFingerprint);
    }

    [Fact]
    public async Task ReconnectsAfterServerRestart()
    {
        if (StartServer() is not { } server) return;
        var session = Open(server, PasswordProfile(server));
        await WaitForState(session, SessionState.Connected);

        server.Stop();
        await WaitForState(session, SessionState.Reconnecting);
        _output.WriteLine("Dropped: " + session.StatusMessage);
        server.Start();

        await WaitForState(session, SessionState.Connected, TimeSpan.FromSeconds(30));
        session.Send("echo back-again\r");
        await WaitForScreen(session, "back-again");
    }

    [Fact]
    public async Task ShellExitDoesNotReconnect()
    {
        if (StartServer() is not { } server) return;
        var session = Open(server, PasswordProfile(server));
        await WaitForState(session, SessionState.Connected);

        session.Send("exit\r");

        await WaitForState(session, SessionState.Disconnected);
        Assert.Contains("exited", session.StatusMessage);
        await Task.Delay(2500);
        Assert.Equal(SessionState.Disconnected, session.State);
    }

    [Fact]
    public async Task UserDisconnectAndReconnect()
    {
        if (StartServer() is not { } server) return;
        var session = Open(server, PasswordProfile(server));
        await WaitForState(session, SessionState.Connected);

        session.Disconnect();
        await WaitForState(session, SessionState.Disconnected);
        await Task.Delay(1500);
        Assert.Equal(SessionState.Disconnected, session.State); // must not auto-reconnect

        session.Reconnect();
        await WaitForState(session, SessionState.Connected);
    }

    [Fact]
    public async Task ResizeReachesTheServerPty()
    {
        if (StartServer() is not { } server) return;
        var session = Open(server, PasswordProfile(server));
        await WaitForState(session, SessionState.Connected);

        session.Resize(101, 33, 0, 0);
        await Task.Delay(300);
        session.Send("stty size\r");

        await WaitForScreen(session, "33 101");
    }

    [Fact]
    public async Task LocalTunnelForwardsTraffic()
    {
        if (StartServer() is not { } server) return;
        using var echo = new EchoServer();
        var localPort = SshTestServer.FreePort();
        var profile = PasswordProfile(server);
        profile.Tunnels.Add(new TunnelDefinition
        {
            Type = TunnelType.Local, BindAddress = "127.0.0.1", BindPort = localPort,
            DestinationHost = "127.0.0.1", DestinationPort = echo.Port,
        });
        var session = Open(server, profile);
        await WaitForState(session, SessionState.Connected);
        Assert.True(Assert.Single(session.Tunnels).Active);

        Assert.Equal("ping-local", await RoundTrip(localPort, "ping-local"));
    }

    [Fact]
    public async Task RemoteTunnelForwardsTraffic()
    {
        if (StartServer() is not { } server) return;
        using var echo = new EchoServer();
        var remotePort = SshTestServer.FreePort();
        var profile = PasswordProfile(server);
        profile.Tunnels.Add(new TunnelDefinition
        {
            Type = TunnelType.Remote, BindAddress = "127.0.0.1", BindPort = remotePort,
            DestinationHost = "127.0.0.1", DestinationPort = echo.Port,
        });
        var session = Open(server, profile);
        await WaitForState(session, SessionState.Connected);
        Assert.True(Assert.Single(session.Tunnels).Active, session.Tunnels[0].Error);

        Assert.Equal("ping-remote", await RoundTrip(remotePort, "ping-remote"));
    }

    [Fact]
    public async Task FailedTunnelDoesNotAffectTerminal()
    {
        if (StartServer() is not { } server) return;
        using var occupied = new EchoServer(); // its port is already taken
        var profile = PasswordProfile(server);
        profile.Tunnels.Add(new TunnelDefinition
        {
            Type = TunnelType.Local, BindAddress = "127.0.0.1", BindPort = occupied.Port,
            DestinationHost = "127.0.0.1", DestinationPort = 9,
        });
        var session = Open(server, profile);
        await WaitForState(session, SessionState.Connected);

        var tunnel = Assert.Single(session.Tunnels);
        Assert.False(tunnel.Active);
        session.Send("echo still-fine\r");
        await WaitForScreen(session, "still-fine");
    }

    [Fact]
    public async Task OutputFloodStaysResponsiveAndBounded()
    {
        if (StartServer() is not { } server) return;
        var profile = PasswordProfile(server);
        profile.ScrollbackLines = 2000;
        var session = Open(server, profile);
        await WaitForState(session, SessionState.Connected);

        var watch = Stopwatch.StartNew();
        session.Send("seq 1 200000; echo flood-$((1+1))-done\r");
        await WaitForScreen(session, "flood-2-done", TimeSpan.FromSeconds(60));
        _output.WriteLine($"200k lines in {watch.Elapsed.TotalSeconds:0.0}s");

        lock (session.Emulator.SyncRoot)
            Assert.True(session.Emulator.Terminal.Buffer.Lines.Length <= 2000 + session.Emulator.Terminal.Rows);
        session.Send("echo responsive\r");
        await WaitForScreen(session, "responsive");
    }

    [Fact]
    public async Task ConnectsThroughJumpHost()
    {
        if (StartServer() is not { } bastion || StartServer() is not { } target) return;
        var bastionProfile = PasswordProfile(bastion);
        bastionProfile.Name = "bastion";
        var targetProfile = PasswordProfile(target);
        targetProfile.Name = "target";
        targetProfile.JumpHostId = bastionProfile.Id;
        var verifier = new TestVerifier(accept: true);
        var trusted = new List<Guid>();

        var session = Open(target, targetProfile, verifier, new[] { bastionProfile });
        session.HostKeyTrusted += (_, id, _) => { lock (trusted) trusted.Add(id); };

        await WaitForState(session, SessionState.Connected);
        session.Send("echo through-the-$((2*1))-hops\r");
        await WaitForScreen(session, "through-the-2-hops");

        // Each hop's key is verified as that server, outermost first, and remembered on its own record.
        Assert.Equal(new[] { bastionProfile.Id, targetProfile.Id }, verifier.Calls.Select(c => c.ServerId));
        Assert.NotEqual(verifier.Calls[0].Fingerprint, verifier.Calls[1].Fingerprint);
        Assert.Equal(new[] { bastionProfile.Id, targetProfile.Id }, trusted);
        Assert.Equal(verifier.Calls[0].Fingerprint, session.JumpHosts[0].HostKeyFingerprint);
        Assert.Equal(verifier.Calls[1].Fingerprint, session.Profile.HostKeyFingerprint);
        Assert.Contains("via bastion", session.Route);
    }

    [Fact]
    public async Task ConnectsThroughTwoJumpHosts()
    {
        if (StartServer() is not { } outer || StartServer() is not { } inner || StartServer() is not { } target) return;
        var outerProfile = PasswordProfile(outer);
        var innerProfile = PasswordProfile(inner);
        innerProfile.JumpHostId = outerProfile.Id;
        var targetProfile = PasswordProfile(target);
        targetProfile.JumpHostId = innerProfile.Id;
        var chain = JumpHostResolver.Resolve(targetProfile, new[] { targetProfile, innerProfile, outerProfile });
        Assert.Equal(new[] { outerProfile.Id, innerProfile.Id }, chain.Select(c => c.Id));

        var session = Open(target, targetProfile, null, chain);

        await WaitForState(session, SessionState.Connected);
        session.Send("echo three-servers-deep\r");
        await WaitForScreen(session, "three-servers-deep");
    }

    [Fact]
    public async Task ReconnectsWhenJumpHostRestarts()
    {
        if (StartServer() is not { } bastion || StartServer() is not { } target) return;
        var bastionProfile = PasswordProfile(bastion);
        var targetProfile = PasswordProfile(target);
        var session = Open(target, targetProfile, null, new[] { bastionProfile });
        await WaitForState(session, SessionState.Connected);

        bastion.Stop();
        await WaitForState(session, SessionState.Reconnecting);
        bastion.Start();

        await WaitForState(session, SessionState.Connected, TimeSpan.FromSeconds(30));
        session.Send("echo bastion-back\r");
        await WaitForScreen(session, "bastion-back");
    }

    [Fact]
    public async Task JumpHostAuthFailureNamesTheJumpHost()
    {
        if (StartServer() is not { } bastion || StartServer() is not { } target) return;
        var bastionProfile = PasswordProfile(bastion);
        bastionProfile.Name = "bastion";
        bastionProfile.Password = "wrong";
        var session = Open(target, PasswordProfile(target), null, new[] { bastionProfile });

        await WaitForState(session, SessionState.Failed);

        Assert.StartsWith("Jump host bastion: Authentication failed", session.StatusMessage);
    }

    [Fact]
    public async Task TunnelsWorkThroughJumpHost()
    {
        if (StartServer() is not { } bastion || StartServer() is not { } target) return;
        using var echo = new EchoServer();
        var localPort = SshTestServer.FreePort();
        var targetProfile = PasswordProfile(target);
        targetProfile.Tunnels.Add(new TunnelDefinition
        {
            Type = TunnelType.Local, BindAddress = "127.0.0.1", BindPort = localPort,
            DestinationHost = "127.0.0.1", DestinationPort = echo.Port,
        });
        var session = Open(target, targetProfile, null, new[] { PasswordProfile(bastion) });
        await WaitForState(session, SessionState.Connected);

        Assert.Equal("ping-jumped", await RoundTrip(localPort, "ping-jumped"));
    }

    // ------------------------------------------------------------------ helpers

    private SshTestServer? StartServer(bool withClientKey = false)
    {
        var server = SshTestServer.TryStart(withClientKey);
        if (server == null)
        {
            _output.WriteLine("SKIPPED: python3 with paramiko is not available.");
            return null;
        }
        _servers.Add(server);
        return server;
    }

    private SshSession Open(SshTestServer server, ServerProfile profile, IHostKeyVerifier? verifier = null,
        IReadOnlyList<ServerProfile>? jumpHosts = null)
    {
        var session = new SshSession(profile, verifier ?? new TestVerifier(accept: true), jumpHosts);
        session.StateChanged += s => _output.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {s.State}: {s.StatusMessage}");
        _cleanup.Add(session);
        session.Start();
        return session;
    }

    private static ServerProfile PasswordProfile(SshTestServer server) => new()
    {
        Name = "test",
        Host = "127.0.0.1",
        Port = server.Port,
        Username = Environment.UserName,
        AuthMethod = AuthMethod.Password,
        Password = SshTestServer.Password,
        KeepAliveSeconds = 5,
        ConnectTimeoutSeconds = 5,
        AutoReconnect = true,
    };

    private static async Task WaitForState(SshSession session, SessionState state, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? Timeout);
        while (session.State != state)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Expected {state}, still {session.State}: {session.StatusMessage}");
            await Task.Delay(50);
        }
    }

    private static async Task WaitForScreen(SshSession session, string text, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? Timeout);
        while (!ScreenText(session).Contains(text))
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"'{text}' not on screen. Screen:\n{ScreenText(session)}");
            await Task.Delay(50);
        }
    }

    private static string ScreenText(SshSession session)
    {
        lock (session.Emulator.SyncRoot)
        {
            var buffer = session.Emulator.Terminal.Buffer;
            var text = new StringBuilder();
            for (var i = 0; i < buffer.Lines.Length; i++)
                text.AppendLine(buffer.TranslateBufferLineToString(i, true, 0, -1).ToString());
            return text.ToString();
        }
    }

    private static async Task<string> RoundTrip(int port, string message)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(Timeout);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(message));
        var buffer = new byte[message.Length];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read)).AsTask().WaitAsync(Timeout);
            if (n == 0)
                break;
            read += n;
        }
        return Encoding.ASCII.GetString(buffer, 0, read);
    }

    private sealed class TestVerifier : IHostKeyVerifier
    {
        private readonly bool _accept;
        public TestVerifier(bool accept) => _accept = accept;
        public List<HostKeyInfo> Calls { get; } = new();

        public Task<bool> VerifyAsync(HostKeyInfo hostKey, CancellationToken cancellationToken)
        {
            lock (Calls)
                Calls.Add(hostKey);
            return Task.FromResult(_accept);
        }
    }

    private sealed class EchoServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();

        public EchoServer()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptLoop);
        }

        public int Port { get; }

        private async Task AcceptLoop()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch { return; }
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        var stream = client.GetStream();
                        var buffer = new byte[4096];
                        int n;
                        try
                        {
                            while ((n = await stream.ReadAsync(buffer, _stop.Token)) > 0)
                                await stream.WriteAsync(buffer.AsMemory(0, n), _stop.Token);
                        }
                        catch { }
                    }
                });
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }
}
