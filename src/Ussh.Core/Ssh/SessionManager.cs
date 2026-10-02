using System.Net.NetworkInformation;
using Ussh.Core.Diagnostics;
using Ussh.Core.Models;

namespace Ussh.Core.Ssh;

/// <summary>
/// Owns all open sessions and watches for conditions that silently kill connections:
/// resume from sleep and network changes. When either happens, every live session is
/// probed so dead sockets fail fast and reconnect instead of sitting frozen.
/// </summary>
public sealed class SessionManager : IAsyncDisposable
{
    private static readonly TimeSpan HealthInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SleepThreshold = TimeSpan.FromSeconds(30);

    private readonly IHostKeyVerifier _hostKeyVerifier;
    private readonly IPassphraseProvider? _passphrases;
    private readonly List<SshSession> _sessions = new();
    private readonly object _gate = new();
    private readonly Timer _healthTimer;
    private DateTime _lastTickUtc = DateTime.UtcNow;

    public SessionManager(IHostKeyVerifier hostKeyVerifier, IPassphraseProvider? passphrases = null)
    {
        _hostKeyVerifier = hostKeyVerifier;
        _passphrases = passphrases;
        _healthTimer = new Timer(_ => HealthTick(), null, HealthInterval, HealthInterval);
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
    }

    public IReadOnlyList<SshSession> Sessions
    {
        get { lock (_gate) return _sessions.ToList(); }
    }

    /// <summary>
    /// Raised (on a background thread) when a host key is newly trusted. The Guid is the server
    /// it belongs to: the session's target or one of its jump hosts.
    /// </summary>
    public event Action<SshSession, Guid, string>? HostKeyTrusted;

    /// <param name="jumpHosts">From <see cref="JumpHostResolver.Resolve"/>, outermost first.</param>
    public SshSession Open(ServerProfile profile, IReadOnlyList<ServerProfile>? jumpHosts = null)
    {
        var session = new SshSession(profile, _hostKeyVerifier, jumpHosts, _passphrases);
        session.HostKeyTrusted += (s, id, fp) => HostKeyTrusted?.Invoke(s, id, fp);
        lock (_gate)
            _sessions.Add(session);
        Log.Info(nameof(SessionManager), $"Opening session to {profile.DisplayName} ({session.Route}).");
        return session;
    }

    public async Task CloseAsync(SshSession session)
    {
        lock (_gate)
            _sessions.Remove(session);
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(nameof(SessionManager), "Error closing session.", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        await _healthTimer.DisposeAsync().ConfigureAwait(false);
        await Task.WhenAll(Sessions.Select(CloseAsync)).ConfigureAwait(false);
    }

    private void HealthTick()
    {
        try
        {
            var now = DateTime.UtcNow;
            var gap = now - _lastTickUtc;
            _lastTickUtc = now;
            // Timers don't fire while the machine is suspended, so a long gap means we just woke up.
            if (gap > SleepThreshold)
            {
                Log.Info(nameof(SessionManager), $"Resumed after ~{gap.TotalMinutes:0.#} min; checking connections.");
                ProbeAll();
            }
        }
        catch (Exception ex)
        {
            Log.Error(nameof(SessionManager), "Health check failed.", ex);
        }
    }

    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        Log.Info(nameof(SessionManager), "Network change detected; checking connections.");
        ProbeAll();
    }

    private void ProbeAll()
    {
        foreach (var session in Sessions)
        {
            try { session.CheckConnection(); }
            catch (Exception ex) { Log.Warn(nameof(SessionManager), "Probe failed: " + ex.Message); }
        }
    }
}
