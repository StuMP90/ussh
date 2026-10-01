namespace Ussh.Core.Models;

public enum TunnelType
{
    /// <summary>-L: listen locally, forward through the server to DestinationHost:DestinationPort.</summary>
    Local,

    /// <summary>-R: listen on the server, forward back to DestinationHost:DestinationPort from this machine.</summary>
    Remote,

    /// <summary>-D: local SOCKS proxy that routes through the server.</summary>
    Dynamic,
}

public sealed class TunnelDefinition
{
    public bool Enabled { get; set; } = true;
    public TunnelType Type { get; set; } = TunnelType.Local;
    public string BindAddress { get; set; } = "127.0.0.1";
    public int BindPort { get; set; }

    /// <summary>Unused for <see cref="TunnelType.Dynamic"/>.</summary>
    public string DestinationHost { get; set; } = "127.0.0.1";
    public int DestinationPort { get; set; }

    public string Describe() => Type switch
    {
        TunnelType.Local => $"L {BindAddress}:{BindPort} → {DestinationHost}:{DestinationPort}",
        TunnelType.Remote => $"R {BindAddress}:{BindPort} → {DestinationHost}:{DestinationPort}",
        TunnelType.Dynamic => $"D {BindAddress}:{BindPort} (SOCKS)",
        _ => Type.ToString(),
    };

    public TunnelDefinition Clone() => (TunnelDefinition)MemberwiseClone();
}
