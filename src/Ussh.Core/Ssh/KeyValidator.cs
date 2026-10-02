using System.Text;
using Renci.SshNet;

namespace Ussh.Core.Ssh;

public static class KeyValidator
{
    /// <summary>
    /// Returns null if the private key loads, otherwise a message suitable for the user.
    /// Accepts OpenSSH, PEM (PKCS#1/PKCS#8) and PuTTY .ppk (v2 and v3) private keys.
    /// </summary>
    public static string? Validate(string? privateKey, string? passphrase)
    {
        if (string.IsNullOrWhiteSpace(privateKey))
            return "Paste or import a private key.";
        var trimmed = privateKey.Trim();
        if (trimmed.StartsWith("ssh-", StringComparison.Ordinal) || trimmed.StartsWith("ecdsa-", StringComparison.Ordinal)
            || trimmed.StartsWith("---- BEGIN SSH2 PUBLIC KEY", StringComparison.Ordinal))
            return "That looks like a public key. Use the private key file instead.";
        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(trimmed + "\n"));
            _ = string.IsNullOrEmpty(passphrase) ? new PrivateKeyFile(stream) : new PrivateKeyFile(stream, passphrase);
            return null;
        }
        catch (Exception ex)
        {
            // PuTTY reports a wrong passphrase as a failed MAC check; say what it means.
            if (IsPuTTY(trimmed) && !string.IsNullOrEmpty(passphrase) && ex.Message.Contains("MAC", StringComparison.Ordinal))
                return "Private key could not be loaded: wrong passphrase for this PuTTY key.";
            return "Private key could not be loaded: " + ex.Message;
        }
    }

    public static bool IsPuTTY(string? privateKey) =>
        privateKey?.TrimStart().StartsWith("PuTTY-User-Key-File-", StringComparison.Ordinal) == true;

    /// <summary>
    /// For "ask for passphrase every time": the key must parse and be passphrase-protected.
    /// Returns null if so, otherwise a message suitable for the user.
    /// </summary>
    public static string? ValidateEncrypted(string? privateKey)
    {
        if (Validate(privateKey, "x") is { } error && !error.StartsWith("Private key could not be loaded", StringComparison.Ordinal))
            return error; // empty, public key, .ppk
        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(privateKey!.Trim() + "\n"));
            _ = new PrivateKeyFile(stream);
            return "This key has no passphrase, so there is nothing to ask for. Untick \"Ask every time\".";
        }
        catch (Renci.SshNet.Common.SshPassPhraseNullOrEmptyException)
        {
            return null; // encrypted: exactly what we want
        }
        catch (Exception ex)
        {
            return "Private key could not be loaded: " + ex.Message;
        }
    }
}
