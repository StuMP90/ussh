using System.Net;
using System.Text;
using System.Threading.Channels;
using Renci.SshNet;
using Renci.SshNet.Common;
using Ussh.Core.Diagnostics;
using Ussh.Core.Models;
using Ussh.Core.Terminal;

namespace Ussh.Core.Ssh;

/// <summary>
/// One SSH terminal session (one tab). Owns its own connection, reader, writer, tunnels
/// and terminal model, and supervises them: when the connection drops it reconnects with
/// backoff; when anything throws, only this session is affected.
///
/// Threading: a supervisor task runs connect/run/retry. Each live connection has a
/// dedicated reader thread (blocking reads feed the emulator) and an async writer that
/// drains a queue of keystrokes, so a stalled server never blocks the UI or other tabs.
/// All events are raised on background threads.
/// </summary>
public sealed class SshSession : IAsyncDisposable
{
    private const string ShellExitedReason = "Remote shell exited";
    private static readonly TimeSpan TeardownTimeout = TimeSpan.FromSeconds(5);

    private readonly IHostKeyVerifier _hostKeyVerifier;
    private readonly IPassphraseProvider? _passphrases;
    // "Ask every time" passphrases this session has used, so its own reconnects never prompt.
    // Memory only; gone when the session closes.
    private readonly PassphraseMemory _rememberedPassphrases = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly object _gate = new();
    private Connection? _current;
    private Task? _supervisor;
    private volatile bool _userStopped;
    private uint _cols = 80, _rows = 24, _pixelWidth, _pixelHeight;
    private IReadOnlyList<TunnelStatus> _tunnels = Array.Empty<TunnelStatus>();

    /// <param name="jumpHosts">Bastions to connect through, outermost first (see <see cref="JumpHostResolver"/>).</param>
    public SshSession(ServerProfile profile, IHostKeyVerifier hostKeyVerifier, IReadOnlyList<ServerProfile>? jumpHosts = null,
        IPassphraseProvider? passphrases = null)
    {
        _passphrases = passphrases;
        // Own copies, so edits in Server Management (or locking the vault) don't affect a live
        // session, and auto-reconnect still has credentials while the vault is locked.
        Profile = profile.Clone();
        JumpHosts = (jumpHosts ?? Array.Empty<ServerProfile>()).Select(j => j.Clone()).ToList();
        _hostKeyVerifier = hostKeyVerifier;
        Emulator = new TerminalEmulator((int)_cols, (int)_rows, Profile.ScrollbackLines);
        Emulator.ResponseReady += Send;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public ServerProfile Profile { get; }
    public IReadOnlyList<ServerProfile> JumpHosts { get; }
    public TerminalEmulator Emulator { get; }

    /// <summary>"web1" or "web1 via bastion" (outermost jump host first).</summary>
    public string Route => JumpHosts.Count == 0
        ? $"{Profile.Host}:{Profile.Port}"
        : $"{Profile.Host}:{Profile.Port} via {string.Join(" → ", JumpHosts.Select(j => j.DisplayName))}";
    public SessionState State { get; private set; } = SessionState.Connecting;
    public string StatusMessage { get; private set; } = "";
    public DateTimeOffset? ConnectedSince { get; private set; }

    /// <summary>
    /// True while the session is disconnected because the remote shell exited cleanly (the user
    /// typed <c>exit</c>), as opposed to a drop, failure or user disconnect.
    /// </summary>
    public bool EndedByShellExit { get; private set; }
    public IReadOnlyList<TunnelStatus> Tunnels => _tunnels;

    public event Action<SshSession>? StateChanged;

    /// <summary>
    /// Raised when the user accepts a new or changed host key, so it can be saved to the vault.
    /// The Guid identifies which server it belongs to (the target or one of its jump hosts).
    /// </summary>
    public event Action<SshSession, Guid, string>? HostKeyTrusted;

    public void Start()
    {
        lock (_gate)
            _supervisor ??= Task.Run(SuperviseAsync);
    }

    /// <summary>Queues bytes for the server. Dropped if not connected.</summary>
    public void Send(byte[] data)
    {
        if (data.Length == 0)
            return;
        _current?.Enqueue(data);
    }

    public void Send(string text) => Send(Encoding.UTF8.GetBytes(text));

    public void Resize(int cols, int rows, int pixelWidth, int pixelHeight)
    {
        lock (_gate)
        {
            _cols = (uint)Math.Max(cols, 2);
            _rows = (uint)Math.Max(rows, 1);
            _pixelWidth = (uint)Math.Max(pixelWidth, 0);
            _pixelHeight = (uint)Math.Max(pixelHeight, 0);
        }
        Emulator.Resize(cols, rows);
        _current?.Resize(_cols, _rows, _pixelWidth, _pixelHeight);
    }

    /// <summary>Drops the current connection (if any) and connects again immediately.</summary>
    public void Reconnect()
    {
        _userStopped = false;
        _current?.Abort("Reconnect requested");
        Wake();
    }

    /// <summary>Disconnects and stays disconnected until <see cref="Reconnect"/>.</summary>
    public void Disconnect()
    {
        _userStopped = true;
        _current?.Abort("Disconnected by user");
        Wake(); // breaks out of a pending retry delay
    }

    /// <summary>
    /// Called after resume-from-sleep or a network change. Forces traffic so a dead socket
    /// fails quickly (within the OS dead-peer timeout) and triggers a reconnect.
    /// </summary>
    public void CheckConnection() => _current?.Probe();

    public async ValueTask DisposeAsync()
    {
        _userStopped = true;
        _lifetime.Cancel();
        _current?.Abort("Session closed");
        Wake();
        if (_supervisor != null)
        {
            try
            {
                await _supervisor.WaitAsync(TeardownTimeout + TeardownTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Log.Warn(LogSource, "Session did not stop in time; abandoning it.");
            }
        }
    }

    private async Task SuperviseAsync()
    {
        var token = _lifetime.Token;
        var failures = 0;
        var everConnected = false;

        while (!token.IsCancellationRequested)
        {
            if (_userStopped)
            {
                EndedByShellExit = false;
                SetState(SessionState.Disconnected, "Disconnected");
                // Loop: a stale wake signal (e.g. from Disconnect itself) must not reconnect.
                while (_userStopped && !token.IsCancellationRequested)
                    await WaitForWakeAsync(null, token).ConfigureAwait(false);
                failures = 0;
                continue;
            }

            EndedByShellExit = false;
            SetState(everConnected ? SessionState.Reconnecting : SessionState.Connecting,
                $"Connecting to {Route}…");

            Connection connection;
            lock (_gate)
            {
                connection = new Connection(this, _cols, _rows, _pixelWidth, _pixelHeight);
                _current = connection;
            }

            string reason;
            bool retryable;
            HostKeyInfo? pendingHostKey = null;
            try
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                everConnected = true;
                failures = 0;
                ConnectedSince = DateTimeOffset.Now;
                SetState(SessionState.Connected, $"Connected to {Profile.Host}");
                Log.Info(LogSource, "Connected.");

                var end = await connection.RunAsync().ConfigureAwait(false);
                (reason, retryable) = end;
                failures = 1; // first retry after a drop is quick
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception) when (connection.PendingHostKey != null)
            {
                // Unknown or changed host key. The handshake was refused so the user can decide
                // without SSH.NET's handshake timeout running; reconnect straight away if trusted.
                (reason, retryable) = ("Host key not trusted", false);
                pendingHostKey = connection.PendingHostKey;
            }
            catch (Exception ex)
            {
                (reason, retryable) = Classify(ex, connection);
                failures++;
                Log.Warn(LogSource, $"Connection attempt failed: {reason}", retryable ? null : ex);
            }
            finally
            {
                lock (_gate)
                    _current = null;
                ConnectedSince = null;
                _tunnels = Array.Empty<TunnelStatus>();
                await connection.DisposeAsync(TeardownTimeout).ConfigureAwait(false);
            }

            if (token.IsCancellationRequested)
                break;

            if (pendingHostKey != null)
            {
                if (await ConfirmHostKeyAsync(pendingHostKey, token).ConfigureAwait(false))
                {
                    failures = 0;
                    continue;
                }
                reason = pendingHostKey.IsChanged
                    ? "Host key changed and was not trusted. Reconnect to review it."
                    : "Host key not trusted";
            }

            Log.Info(LogSource, $"Connection ended: {reason}");

            if (_userStopped)
                continue;

            if (retryable && Profile.AutoReconnect)
            {
                var delay = Backoff(failures);
                SetState(SessionState.Reconnecting, $"{reason}. Retrying in {delay.TotalSeconds:0}s…");
                await WaitForWakeAsync(delay, token).ConfigureAwait(false);
                continue;
            }

            // A clean shell exit is a normal end, not an error.
            EndedByShellExit = reason == ShellExitedReason;
            SetState(retryable || reason == ShellExitedReason ? SessionState.Disconnected : SessionState.Failed, reason);
            await WaitForWakeAsync(null, token).ConfigureAwait(false);
            failures = 0;
        }

        SetState(SessionState.Closed, "Closed");
    }

    private async Task<bool> ConfirmHostKeyAsync(HostKeyInfo hostKey, CancellationToken token)
    {
        SetState(SessionState.Connecting, hostKey.IsChanged
            ? "WARNING: the server's host key has changed. Waiting for confirmation…"
            : "Waiting for host key confirmation…");
        bool trusted;
        try
        {
            trusted = await _hostKeyVerifier.VerifyAsync(hostKey, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(LogSource, "Host key prompt failed.", ex);
            return false;
        }

        if (!trusted)
        {
            Log.Warn(LogSource, $"Host key {hostKey.Fingerprint} rejected by user.");
            return false;
        }

        Log.Info(LogSource, hostKey.IsChanged
            ? $"Host key CHANGED from {hostKey.KnownFingerprint} to {hostKey.Fingerprint}; accepted by user."
            : $"Host key {hostKey.Fingerprint} trusted on first use.");
        foreach (var server in JumpHosts.Append(Profile).Where(p => p.Id == hostKey.ServerId))
            server.HostKeyFingerprint = hostKey.Fingerprint;
        try { HostKeyTrusted?.Invoke(this, hostKey.ServerId, hostKey.Fingerprint); }
        catch (Exception ex) { Log.Error(LogSource, "HostKeyTrusted handler threw.", ex); }
        return true;
    }

    private static (string Reason, bool Retryable) Classify(Exception ex, Connection connection)
    {
        if (ex is JumpHostException jump)
        {
            var (reason, retryable) = Classify(jump.InnerException!, connection);
            return ($"Jump host {jump.HostName}: {reason}", retryable);
        }
        return ex switch
        {
            SessionConfigurationException => (ex.Message, false),
            SshAuthenticationException => ("Authentication failed: " + ex.Message, false),
            OperationCanceledException => (connection.AbortReason ?? "Connection timed out", connection.AbortReason == null),
            SshOperationTimeoutException => ("Connection timed out", true),
            System.Net.Sockets.SocketException se => (se.Message, true),
            _ => (ex.Message, true),
        };
    }

    private static TimeSpan Backoff(int failures)
    {
        var seconds = Math.Min(60, Math.Pow(2, Math.Max(0, failures - 1)));
        return TimeSpan.FromSeconds(seconds + Random.Shared.NextDouble());
    }

    private async Task WaitForWakeAsync(TimeSpan? delay, CancellationToken token)
    {
        try
        {
            await _wake.WaitAsync(delay ?? Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    private void Wake()
    {
        lock (_gate)
        {
            if (_wake.CurrentCount == 0)
                _wake.Release();
        }
    }

    private void SetState(SessionState state, string message)
    {
        State = state;
        StatusMessage = message;
        try
        {
            StateChanged?.Invoke(this);
        }
        catch (Exception ex)
        {
            Log.Error(LogSource, "StateChanged handler threw.", ex);
        }
    }

    private string LogSource => $"session {Profile.DisplayName}";

    /// <summary>
    /// A single connection attempt and its lifetime. Disposed and replaced on every reconnect,
    /// so no state leaks from a dead connection into the next one.
    /// </summary>
    private sealed class Connection
    {
        private readonly SshSession _owner;
        private readonly CancellationTokenSource _abort = new();
        private readonly Channel<byte[]> _outgoing = Channel.CreateUnbounded<byte[]>(
            new UnboundedChannelOptions { SingleReader = true });
        private readonly TaskCompletionSource<Exception> _faulted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly uint _cols, _rows, _pixelWidth, _pixelHeight;
        private readonly List<ForwardedPort> _ports = new();
        // Jump host clients (outermost first) and the forwards that chain them together.
        private readonly List<SshClient> _hopClients = new();
        private readonly List<ForwardedPortLocal> _hopForwards = new();
        private SshClient? _client;
        private ShellStream? _shell;
        // Distinguishing "the user typed exit" from "the network died": servers usually drop the
        // whole connection right after the shell exits, so "is the transport still up?" can't tell
        // them apart. The channel can: a clean exit delivers end-of-stream (and a channel close)
        // before any transport error, whereas on an abrupt drop the reader never sees end-of-stream.
        private readonly TaskCompletionSource _shellClosedByServer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _endOfStreamBeforeFault;

        public Connection(SshSession owner, uint cols, uint rows, uint pixelWidth, uint pixelHeight)
        {
            _owner = owner;
            _cols = cols;
            _rows = rows;
            _pixelWidth = pixelWidth;
            _pixelHeight = pixelHeight;
        }

        /// <summary>Set when the server presented a key the user hasn't trusted yet.</summary>
        public HostKeyInfo? PendingHostKey { get; private set; }
        public string? AbortReason { get; private set; }

        private ServerProfile Profile => _owner.Profile;

        public async Task OpenAsync(CancellationToken lifetime)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime, _abort.Token);
            var token = linked.Token;

            // Each jump host connects to the next hop through a private local forward
            // (127.0.0.1 on an OS-assigned port); the target connects through the last one.
            var host = (_owner.JumpHosts.Count > 0 ? _owner.JumpHosts[0] : Profile).Host;
            var port = (_owner.JumpHosts.Count > 0 ? _owner.JumpHosts[0] : Profile).Port;
            for (var i = 0; i < _owner.JumpHosts.Count; i++)
            {
                var jump = _owner.JumpHosts[i];
                var next = i + 1 < _owner.JumpHosts.Count ? _owner.JumpHosts[i + 1] : Profile;
                SshClient hop;
                try
                {
                    hop = await ConnectClientAsync(jump, host, port, token).ConfigureAwait(false);
                }
                catch (Exception ex) when (PendingHostKey == null && ex is not OperationCanceledException)
                {
                    throw new JumpHostException(jump.DisplayName, ex);
                }
                _hopClients.Add(hop);

                var forward = new ForwardedPortLocal("127.0.0.1", 0, next.Host, (uint)next.Port);
                forward.Exception += (_, e) => Log.Warn(_owner.LogSource, $"Jump via {jump.DisplayName} to {next.Host}:{next.Port}: {e.Exception.Message}");
                hop.AddForwardedPort(forward);
                forward.Start();
                _hopForwards.Add(forward);
                (host, port) = ("127.0.0.1", (int)forward.BoundPort);
            }

            _client = await ConnectClientAsync(Profile, host, port, token).ConfigureAwait(false);

            _shell = _client.CreateShellStream(
                string.IsNullOrWhiteSpace(Profile.TerminalType) ? "xterm-256color" : Profile.TerminalType,
                _cols, _rows, _pixelWidth, _pixelHeight, 64 * 1024);
            _shell.ErrorOccurred += (_, e) => _faulted.TrySetResult(e.Exception);
            _shell.Closed += (_, _) => _shellClosedByServer.TrySetResult();

            StartTunnels();
        }

        /// <summary>
        /// Connects to <paramref name="server"/> at <paramref name="host"/>:<paramref name="port"/>
        /// (its real address, or a local forward through a jump host), authenticating and
        /// verifying the host key as that server.
        /// </summary>
        private async Task<SshClient> ConnectClientAsync(ServerProfile server, string host, int port, CancellationToken token)
        {
            var info = new ConnectionInfo(host, port, server.Username, await BuildAuthAsync(server, token).ConfigureAwait(false))
            {
                Timeout = TimeSpan.FromSeconds(Math.Clamp(server.ConnectTimeoutSeconds, 3, 120)),
                Encoding = Encoding.UTF8,
            };
            var client = new SshClient(info);
            if (server.KeepAliveSeconds > 0)
                client.KeepAliveInterval = TimeSpan.FromSeconds(Math.Max(5, server.KeepAliveSeconds));
            client.HostKeyReceived += (_, e) => OnHostKeyReceived(server, e);
            // A failure on any hop breaks the chain, so it fails the whole connection.
            client.ErrorOccurred += (_, e) => _faulted.TrySetResult(e.Exception);
            try
            {
                await client.ConnectAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
            }
            catch
            {
                client.Dispose();
                throw;
            }
            SocketTuning.Apply(client, TimeSpan.FromSeconds(Math.Max(30, server.KeepAliveSeconds * 3)));
            return client;
        }

        /// <summary>Runs until the shell ends or the connection fails. Returns why, and whether a retry makes sense.</summary>
        public async Task<(string Reason, bool Retryable)> RunAsync()
        {
            var reader = Task.Factory.StartNew(ReadLoop, CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            var writer = WriteLoopAsync();

            var finished = await Task.WhenAny(reader, writer, _faulted.Task).ConfigureAwait(false);

            if (finished == reader && !reader.IsFaulted)
            {
                // End-of-stream arrives (channel EOF) just before the channel close message, so
                // give the close (clean exit) or a transport error a moment to show up.
                await Task.WhenAny(_shellClosedByServer.Task, _faulted.Task, Task.Delay(3000)).ConfigureAwait(false);
            }

            if (AbortReason != null)
                return (AbortReason, AbortReason != "Disconnected by user" && AbortReason != "Session closed");
            if (_shellClosedByServer.Task.IsCompleted || _endOfStreamBeforeFault)
                return (ShellExitedReason, false);
            if (finished == _faulted.Task)
                return ("Connection lost: " + _faulted.Task.Result.Message, true);
            if (finished.IsFaulted)
                return ("Connection lost: " + finished.Exception!.GetBaseException().Message, true);

            return ("Connection closed by server", true);
        }

        private void ReadLoop()
        {
            var buffer = new byte[32 * 1024];
            var shell = _shell!;
            while (!_abort.IsCancellationRequested)
            {
                int count;
                try
                {
                    count = shell.Read(buffer, 0, buffer.Length);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                if (count <= 0)
                {
                    _endOfStreamBeforeFault = !_faulted.Task.IsCompleted && !_abort.IsCancellationRequested;
                    return;
                }
                _owner.Emulator.Feed(buffer, count);
            }
        }

        private async Task WriteLoopAsync()
        {
            var shell = _shell!;
            try
            {
                await foreach (var data in _outgoing.Reader.ReadAllAsync(_abort.Token).ConfigureAwait(false))
                {
                    shell.Write(data, 0, data.Length);
                    shell.Flush();
                }
            }
            catch (OperationCanceledException) { }
        }

        public void Enqueue(byte[] data) => _outgoing.Writer.TryWrite(data);

        public void Resize(uint cols, uint rows, uint pixelWidth, uint pixelHeight)
        {
            var shell = _shell;
            if (shell == null)
                return;
            // Off the caller's (UI) thread: a send on a stalled socket must not freeze the window.
            _ = Task.Run(() =>
            {
                try { shell.ChangeWindowSize(cols, rows, pixelWidth, pixelHeight); }
                catch (Exception ex) { Log.Warn(_owner.LogSource, "Window resize failed: " + ex.Message); }
            });
        }

        public void Probe()
        {
            var clients = _hopClients.Append(_client).OfType<SshClient>().ToList();
            _ = Task.Run(() =>
            {
                foreach (var client in clients)
                {
#pragma warning disable CS0618 // one-off probe after resume; periodic keepalives use KeepAliveInterval
                    try { client.SendKeepAlive(); }
#pragma warning restore CS0618
                    catch (Exception ex) { _faulted.TrySetResult(ex); }
                }
            });
        }

        public void Abort(string reason)
        {
            AbortReason ??= reason;
            try { _abort.Cancel(); } catch (ObjectDisposedException) { }
            _faulted.TrySetResult(new OperationCanceledException(reason));
            // Unblocks the reader thread's pending Read.
            try { _shell?.Dispose(); } catch { }
        }

        /// <summary>Tears down without letting a dead socket hang the supervisor.</summary>
        public async Task DisposeAsync(TimeSpan timeout)
        {
            AbortReason ??= "Connection ended";
            try { _abort.Cancel(); } catch (ObjectDisposedException) { }
            _outgoing.Writer.TryComplete();

            var teardown = Task.Run(() =>
            {
                foreach (var port in _ports)
                {
                    try { if (port.IsStarted) port.Stop(); } catch { }
                    try { port.Dispose(); } catch { }
                }
                try { _shell?.Dispose(); } catch { }
                try { if (_client?.IsConnected == true) _client.Disconnect(); } catch { }
                try { _client?.Dispose(); } catch { }
                // Then the jump chain, innermost first.
                for (var i = _hopClients.Count - 1; i >= 0; i--)
                {
                    try { if (_hopForwards.Count > i && _hopForwards[i].IsStarted) _hopForwards[i].Stop(); } catch { }
                    try { if (_hopClients[i].IsConnected) _hopClients[i].Disconnect(); } catch { }
                    try { _hopClients[i].Dispose(); } catch { }
                }
            });
            try
            {
                await teardown.WaitAsync(timeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Log.Warn(_owner.LogSource, "Connection teardown timed out; abandoning it.");
            }
            _abort.Dispose();
        }

        private Task<AuthenticationMethod[]> BuildAuthAsync(ServerProfile server, CancellationToken token) =>
            SshAuth.BuildAsync(server, _owner._rememberedPassphrases, _owner._passphrases,
                waitingFor => _owner.SetState(
                    _owner.State is SessionState.Reconnecting ? SessionState.Reconnecting : SessionState.Connecting,
                    $"Waiting for the key passphrase for {waitingFor.DisplayName}…"),
                token);

        private void OnHostKeyReceived(ServerProfile server, HostKeyEventArgs e)
        {
            // Verified against the server's own record, so going through a jump host's local
            // forward (127.0.0.1:random) doesn't weaken the check.
            var fingerprint = e.FingerPrintSHA256;
            var known = server.HostKeyFingerprint;
            if (known != null && string.Equals(known, fingerprint, StringComparison.Ordinal))
            {
                e.CanTrust = true;
                return;
            }

            PendingHostKey = new HostKeyInfo(server.Id, server.DisplayName, server.Host, server.Port,
                e.HostKeyName, e.KeyLength, fingerprint, known);
            e.CanTrust = false;
        }

        private void StartTunnels()
        {
            var statuses = new List<TunnelStatus>();
            foreach (var tunnel in Profile.Tunnels.Where(t => t.Enabled))
            {
                try
                {
                    ForwardedPort port = tunnel.Type switch
                    {
                        TunnelType.Local => new ForwardedPortLocal(tunnel.BindAddress, (uint)tunnel.BindPort,
                            tunnel.DestinationHost, (uint)tunnel.DestinationPort),
                        TunnelType.Remote => new ForwardedPortRemote(tunnel.BindAddress, (uint)tunnel.BindPort,
                            tunnel.DestinationHost, (uint)tunnel.DestinationPort),
                        TunnelType.Dynamic => new ForwardedPortDynamic(tunnel.BindAddress, (uint)tunnel.BindPort),
                        _ => throw new NotSupportedException(tunnel.Type.ToString()),
                    };
                    // An individual forwarded connection failing is normal (e.g. the target refused);
                    // log it but never let it affect the shell.
                    port.Exception += (_, e) => Log.Warn(_owner.LogSource, $"Tunnel {tunnel.Describe()}: {e.Exception.Message}");
                    _client!.AddForwardedPort(port);
                    port.Start();
                    _ports.Add(port);
                    statuses.Add(new TunnelStatus(tunnel, true, null));
                    Log.Info(_owner.LogSource, $"Tunnel started: {tunnel.Describe()}");
                }
                catch (Exception ex)
                {
                    // A port already in use etc. shouldn't stop the terminal from working.
                    statuses.Add(new TunnelStatus(tunnel, false, ex.Message));
                    Log.Warn(_owner.LogSource, $"Tunnel failed: {tunnel.Describe()}: {ex.Message}");
                }
            }
            _owner._tunnels = statuses;
        }
    }
}
