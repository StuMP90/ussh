namespace Ussh.Core.Models;

public enum AuthMethod
{
    Password,
    PrivateKey,
}

/// <summary>What a saved server is, which decides how it can be opened.</summary>
public enum ServerKind
{
    /// <summary>A normal SSH server: terminal, plus file browsing over SFTP.</summary>
    Ssh,
    /// <summary>SFTP-only host (no shell): file browsing only.</summary>
    SftpOnly,
    /// <summary>An Amazon S3 bucket (or S3-compatible storage): file browsing only.</summary>
    S3,
}

/// <summary>
/// A saved server. Lives only inside the encrypted vault, so secrets
/// (password, private key, passphrase) are stored here directly.
/// </summary>
public sealed class ServerProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ServerKind Kind { get; set; } = ServerKind.Ssh;
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

    /// <summary>
    /// Never store the key passphrase: prompt for it when connecting and keep it in memory only
    /// (see <see cref="Ssh.IPassphraseProvider"/>). <see cref="PrivateKeyPassphrase"/> stays null.
    /// </summary>
    public bool AskForPassphrase { get; set; }

    /// <summary>SHA256 host key fingerprint accepted by the user (trust on first use).</summary>
    public string? HostKeyFingerprint { get; set; }

    public int KeepAliveSeconds { get; set; } = 30;
    public int ConnectTimeoutSeconds { get; set; } = 15;
    public bool AutoReconnect { get; set; } = true;
    public string TerminalType { get; set; } = "xterm-256color";
    public int ScrollbackLines { get; set; } = 10_000;

    public List<TunnelDefinition> Tunnels { get; set; } = new();

    /// <summary>Another saved server to connect through (bastion). It may itself have a jump host.</summary>
    public Guid? JumpHostId { get; set; }

    /// <summary>Overrides <see cref="AppSettings.OnShellExit"/> for this server; null uses the default.</summary>
    public ShellExitAction? OnShellExit { get; set; }

    /// <summary>Terminal colour theme name; null uses the default from settings.</summary>
    public string? ThemeName { get; set; }

    public string Notes { get; set; } = "";

    // ---- Amazon S3 (Kind == S3). Secrets live only inside the encrypted vault. ----
    /// <summary>Empty: browse every bucket the keys can list.</summary>
    public string S3Bucket { get; set; } = "";
    public string S3Region { get; set; } = "us-east-1";
    /// <summary>
    /// Folder to open at, e.g. "backups/2026". With no bucket set, it starts with the bucket
    /// name ("my-bucket/backups"). Empty for the top level.
    /// </summary>
    public string S3Prefix { get; set; } = "";
    public string? S3AccessKeyId { get; set; }
    public string? S3SecretAccessKey { get; set; }
    /// <summary>Optional endpoint for S3-compatible storage (MinIO, Backblaze B2, R2…); empty for AWS.</summary>
    public string S3ServiceUrl { get; set; } = "";

    public bool HasTerminal => Kind == ServerKind.Ssh;

    public string DisplayName => !string.IsNullOrWhiteSpace(Name) ? Name
        : Kind == ServerKind.S3 ? (S3Bucket.Length == 0 ? "S3 (all buckets)" : $"s3://{S3Bucket}")
        : $"{Username}@{Host}";

    public ServerProfile Clone()
    {
        var copy = (ServerProfile)MemberwiseClone();
        copy.Tunnels = Tunnels.Select(t => t.Clone()).ToList();
        return copy;
    }
}
