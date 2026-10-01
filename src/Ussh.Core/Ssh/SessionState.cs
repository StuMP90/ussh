using Ussh.Core.Models;

namespace Ussh.Core.Ssh;

public enum SessionState
{
    Connecting,
    Connected,
    /// <summary>The connection dropped and an automatic retry is scheduled or in progress.</summary>
    Reconnecting,
    /// <summary>Not connected and not retrying (user disconnected, shell exited, or auto-reconnect off).</summary>
    Disconnected,
    /// <summary>Stopped because retrying cannot help (bad credentials, rejected host key, invalid key file).</summary>
    Failed,
    Closed,
}

public sealed record TunnelStatus(TunnelDefinition Definition, bool Active, string? Error);
