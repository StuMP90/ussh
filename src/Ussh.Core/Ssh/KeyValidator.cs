using System.Text;
using Renci.SshNet;

namespace Ussh.Core.Ssh;

public static class KeyValidator
{
    /// <summary>Returns null if the private key loads, otherwise a message suitable for the user.</summary>
    public static string? Validate(string? privateKey, string? passphrase)
    {
        if (string.IsNullOrWhiteSpace(privateKey))
            return "Paste or import a private key.";
        var trimmed = privateKey.Trim();
        if (trimmed.StartsWith("ssh-", StringComparison.Ordinal) || trimmed.StartsWith("ecdsa-", StringComparison.Ordinal))
            return "That looks like a public key (.pub). Use the private key file instead.";
        if (trimmed.StartsWith("PuTTY-User-Key-File", StringComparison.Ordinal))
            return "PuTTY .ppk keys aren't supported. Convert it with PuTTYgen (Conversions → Export OpenSSH key).";
        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(trimmed + "\n"));
            _ = string.IsNullOrEmpty(passphrase) ? new PrivateKeyFile(stream) : new PrivateKeyFile(stream, passphrase);
            return null;
        }
        catch (Exception ex)
        {
            return "Private key could not be loaded: " + ex.Message;
        }
    }
}
