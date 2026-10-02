using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;
using Ussh.Core.Diagnostics;
using Ussh.Core.Models;

namespace Ussh.Core.Ssh;

/// <summary>
/// An SFTP connection to a server, including any jump-host chain it goes through.
/// Disposing closes the SFTP client and then the chain, innermost first.
/// </summary>
public sealed class SftpConnection : IDisposable
{
    private readonly List<SshClient> _hops;
    private readonly List<ForwardedPortLocal> _forwards;

    internal SftpConnection(SftpClient client, List<SshClient> hops, List<ForwardedPortLocal> forwards)
    {
        Client = client;
        _hops = hops;
        _forwards = forwards;
    }

    public SftpClient Client { get; }

    public bool IsConnected => Client.IsConnected && _hops.All(h => h.IsConnected);

    public void Dispose()
    {
        try { if (Client.IsConnected) Client.Disconnect(); } catch { }
        try { Client.Dispose(); } catch { }
        DisposeChain(_hops, _forwards);
    }

    internal static void DisposeChain(List<SshClient> hops, List<ForwardedPortLocal> forwards)
    {
        for (var i = hops.Count - 1; i >= 0; i--)
        {
            try { if (forwards.Count > i && forwards[i].IsStarted) forwards[i].Stop(); } catch { }
            try { if (hops[i].IsConnected) hops[i].Disconnect(); } catch { }
            try { hops[i].Dispose(); } catch { }
        }
    }
}

/// <summary>
/// Opens SFTP connections with the same rules as terminal sessions: saved credentials and
/// "ask every time" passphrases, jump-host chains, and host keys verified per server
/// (unknown or changed keys go to the <see cref="IHostKeyVerifier"/>).
/// </summary>
public sealed class SshConnector
{
    private readonly IHostKeyVerifier _hostKeys;
    private readonly IPassphraseProvider? _passphrases;
    private readonly PassphraseMemory _memory = new();

    public SshConnector(IHostKeyVerifier hostKeys, IPassphraseProvider? passphrases)
    {
        _hostKeys = hostKeys;
        _passphrases = passphrases;
    }

    /// <summary>Raised when the user trusts a new or changed host key (server id, fingerprint).</summary>
    public event Action<Guid, string>? HostKeyTrusted;

    /// <param name="target">The server; its jump hosts come from <paramref name="jumpHosts"/> (outermost first).
    /// A host key the user trusts is recorded on these objects too.</param>
    /// <exception cref="FileConnectionException">With a message suitable for the user.</exception>
    public async Task<SftpConnection> ConnectSftpAsync(ServerProfile target, IReadOnlyList<ServerProfile> jumpHosts, CancellationToken token)
    {
        // Own copies, so fingerprints accepted here don't leak into anyone else's profile objects.
        var chain = jumpHosts.Select(j => j.Clone()).Append(target.Clone()).ToList();
        for (var attempt = 0; attempt < chain.Count + 2; attempt++)
        {
            var pending = new PendingKey();
            try
            {
                return await ConnectChainAsync(chain, pending, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (pending.Info != null)
            {
                // Unknown or changed key: ask without the handshake timeout running, then retry.
                var info = pending.Info;
                bool trusted;
                try { trusted = await _hostKeys.VerifyAsync(info, token).ConfigureAwait(false); }
                catch (Exception verifyError) when (verifyError is not OperationCanceledException) { trusted = false; }
                if (!trusted)
                {
                    throw new FileConnectionException(info.IsChanged
                        ? $"The host key for {info.ServerName} changed and was not trusted."
                        : $"The host key for {info.ServerName} was not trusted.", ex);
                }
                // Also on the caller's records, so its next connection (e.g. the separate transfer
                // connection) doesn't ask again.
                foreach (var server in chain.Concat(jumpHosts).Append(target).Where(s => s.Id == info.ServerId))
                    server.HostKeyFingerprint = info.Fingerprint;
                try { HostKeyTrusted?.Invoke(info.ServerId, info.Fingerprint); }
                catch (Exception handlerError) { Log.Error(nameof(SshConnector), "HostKeyTrusted handler threw.", handlerError); }
            }
            catch (Exception ex)
            {
                // Temporary trouble (refused while a server restarts, timeouts, network down) is an
                // IOException so transfers retry with backoff; anything else (credentials, keys,
                // configuration) is a FileConnectionException and isn't retried.
                throw IsTemporary(ex) ? new IOException(Describe(ex), ex) : new FileConnectionException(Describe(ex), ex);
            }
        }
        throw new FileConnectionException($"Could not verify the host keys for {target.DisplayName}.");
    }

    /// <summary>True for failures that may clear up by themselves, so retrying makes sense.</summary>
    public static bool IsTemporary(Exception ex) => ex switch
    {
        JumpHostException jump => IsTemporary(jump.InnerException!),
        SshAuthenticationException or SessionConfigurationException or FileConnectionException => false,
        System.Net.Sockets.SocketException or SshConnectionException or SshOperationTimeoutException
            or TimeoutException or OperationCanceledException or IOException => true,
        _ => false,
    };

    /// <summary>A short, user-facing description of a connection failure.</summary>
    public static string Describe(Exception ex) => ex switch
    {
        FileConnectionException => ex.Message,
        JumpHostException jump => $"Jump host {jump.HostName}: {Describe(jump.InnerException!)}",
        SessionConfigurationException => ex.Message,
        SshAuthenticationException => "Authentication failed: " + ex.Message,
        SshOperationTimeoutException => "Connection timed out",
        OperationCanceledException => "Connection timed out",
        _ => ex.Message,
    };

    private sealed class PendingKey
    {
        public HostKeyInfo? Info;
    }

    private async Task<SftpConnection> ConnectChainAsync(List<ServerProfile> chain, PendingKey pending, CancellationToken token)
    {
        var hops = new List<SshClient>();
        var forwards = new List<ForwardedPortLocal>();
        try
        {
            var (host, port) = (chain[0].Host, chain[0].Port);
            for (var i = 0; i < chain.Count - 1; i++)
            {
                var jump = chain[i];
                var next = chain[i + 1];
                SshClient hop;
                try
                {
                    hop = await ConnectAsync(jump, host, port, info => new SshClient(info), pending, token).ConfigureAwait(false);
                }
                catch (Exception ex) when (pending.Info == null && ex is not OperationCanceledException)
                {
                    throw new JumpHostException(jump.DisplayName, ex);
                }
                hops.Add(hop);
                var forward = new ForwardedPortLocal("127.0.0.1", 0, next.Host, (uint)next.Port);
                hop.AddForwardedPort(forward);
                forward.Start();
                forwards.Add(forward);
                (host, port) = ("127.0.0.1", (int)forward.BoundPort);
            }

            var target = chain[^1];
            var sftp = await ConnectAsync(target, host, port, info => new SftpClient(info), pending, token).ConfigureAwait(false);
            sftp.OperationTimeout = TimeSpan.FromSeconds(60);
            return new SftpConnection(sftp, hops, forwards);
        }
        catch
        {
            SftpConnection.DisposeChain(hops, forwards);
            throw;
        }
    }

    private async Task<TClient> ConnectAsync<TClient>(ServerProfile server, string host, int port,
        Func<ConnectionInfo, TClient> create, PendingKey pending, CancellationToken token) where TClient : BaseClient
    {
        var info = new ConnectionInfo(host, port, server.Username,
            await SshAuth.BuildAsync(server, _memory, _passphrases, null, token).ConfigureAwait(false))
        {
            Timeout = TimeSpan.FromSeconds(Math.Clamp(server.ConnectTimeoutSeconds, 3, 120)),
            Encoding = Encoding.UTF8,
        };
        var client = create(info);
        if (server.KeepAliveSeconds > 0)
            client.KeepAliveInterval = TimeSpan.FromSeconds(Math.Max(5, server.KeepAliveSeconds));
        client.HostKeyReceived += (_, e) =>
        {
            var fingerprint = e.FingerPrintSHA256;
            if (server.HostKeyFingerprint != null && string.Equals(server.HostKeyFingerprint, fingerprint, StringComparison.Ordinal))
            {
                e.CanTrust = true;
                return;
            }
            pending.Info = new HostKeyInfo(server.Id, server.DisplayName, server.Host, server.Port,
                e.HostKeyName, e.KeyLength, fingerprint, server.HostKeyFingerprint);
            e.CanTrust = false;
        };
        try
        {
            await client.ConnectAsync(token).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            throw;
        }
        SocketTuning.Apply(client, TimeSpan.FromSeconds(Math.Max(30, server.KeepAliveSeconds * 3)));
        return client;
    }
}

/// <summary>A file connection (SFTP or S3) failed; the message is suitable for the user.</summary>
public sealed class FileConnectionException : Exception
{
    public FileConnectionException(string message, Exception? inner = null) : base(message, inner) { }
}
