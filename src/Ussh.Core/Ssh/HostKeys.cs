namespace Ussh.Core.Ssh;

/// <param name="KnownFingerprint">The previously trusted fingerprint, or null on first connection.</param>
public sealed record HostKeyInfo(
    string ServerName,
    string Host,
    int Port,
    string Algorithm,
    int KeyLength,
    string Fingerprint,
    string? KnownFingerprint)
{
    public bool IsChanged => KnownFingerprint != null;
}

/// <summary>
/// Asks the user whether to trust a host key. Called on a background thread during
/// connect; implementations should show UI and complete when the user answers.
/// </summary>
public interface IHostKeyVerifier
{
    Task<bool> VerifyAsync(HostKeyInfo hostKey, CancellationToken cancellationToken);
}

internal sealed class SessionConfigurationException : Exception
{
    public SessionConfigurationException(string message, Exception? inner = null) : base(message, inner) { }
}
