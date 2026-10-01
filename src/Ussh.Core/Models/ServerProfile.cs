namespace Ussh.Core.Models;

public enum AuthMethod
{
    Password,
    PrivateKey,
}

/// <summary>
/// A saved server. Lives only inside the encrypted vault, so secrets
/// (password, private key, passphrase) are stored here directly.
/// </summary>
public sealed class ServerProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Group { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string Username { get; set; } = "";

    public AuthMethod AuthMethod { get; set; } = AuthMethod.Password;
    public string? Password { get; set; }

    /// <summary>Private key contents (OpenSSH or PEM). Imported into the vault, not referenced by path.</summary>
    public string? PrivateKey { get; set; }
    public string? PrivateKeyPassphrase { get; set; }

    /// <summary>SHA256 host key fingerprint accepted by the user (trust on first use).</summary>
    public string? HostKeyFingerprint { get; set; }

    public int KeepAliveSeconds { get; set; } = 30;
    public int ConnectTimeoutSeconds { get; set; } = 15;
    public bool AutoReconnect { get; set; } = true;
    public string TerminalType { get; set; } = "xterm-256color";
    public int ScrollbackLines { get; set; } = 10_000;

    public List<TunnelDefinition> Tunnels { get; set; } = new();
    public string Notes { get; set; } = "";

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"{Username}@{Host}" : Name;

    public ServerProfile Clone()
    {
        var copy = (ServerProfile)MemberwiseClone();
        copy.Tunnels = Tunnels.Select(t => t.Clone()).ToList();
        return copy;
    }
}
