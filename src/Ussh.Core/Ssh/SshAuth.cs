using System.Text;
using Renci.SshNet;
using Ussh.Core.Models;

namespace Ussh.Core.Ssh;

/// <summary>Passphrases a connection has used ("ask every time" keys), in memory only.</summary>
public sealed class PassphraseMemory
{
    private readonly Dictionary<Guid, string> _passphrases = new();

    public string? Get(Guid serverId)
    {
        lock (_passphrases)
            return _passphrases.TryGetValue(serverId, out var passphrase) ? passphrase : null;
    }

    public void Set(Guid serverId, string passphrase)
    {
        lock (_passphrases)
            _passphrases[serverId] = passphrase;
    }

    public void Forget(Guid serverId)
    {
        lock (_passphrases)
            _passphrases.Remove(serverId);
    }
}

/// <summary>SSH authentication shared by terminal sessions and file connections.</summary>
internal static class SshAuth
{
    /// <summary>
    /// Auth for <paramref name="server"/>. For "ask every time" keys, gets the passphrase
    /// (remembered by this connection, else from the provider, which may prompt) and checks it by
    /// loading the key locally, re-asking on a wrong answer, before anything goes to the server.
    /// </summary>
    /// <param name="onWaiting">Called just before prompting, e.g. to show "waiting for passphrase".</param>
    public static async Task<AuthenticationMethod[]> BuildAsync(ServerProfile server, PassphraseMemory memory,
        IPassphraseProvider? provider, Action<ServerProfile>? onWaiting, CancellationToken token)
    {
        if (server.AuthMethod != AuthMethod.PrivateKey || !server.AskForPassphrase)
            return Build(server, server.PrivateKeyPassphrase);

        string? retryReason = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var passphrase = memory.Get(server.Id);
            if (passphrase == null || retryReason != null)
            {
                if (provider == null)
                    throw new SessionConfigurationException($"No way to ask for the key passphrase for {server.DisplayName}.");
                onWaiting?.Invoke(server);
                passphrase = await provider.GetPassphraseAsync(server, retryReason, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (passphrase == null)
                    throw new SessionConfigurationException($"Key passphrase for {server.DisplayName} was not entered. Reconnect to try again.");
            }
            try
            {
                var auth = Build(server, passphrase);
                memory.Set(server.Id, passphrase);
                return auth;
            }
            catch (SessionConfigurationException)
            {
                // The key was checked to be a valid encrypted key when saved, so failing to
                // open it now means the passphrase is wrong.
                memory.Forget(server.Id);
                retryReason = "That passphrase didn't unlock the key. Try again.";
            }
        }
        throw new SessionConfigurationException($"Wrong key passphrase for {server.DisplayName}. Reconnect to try again.");
    }

    public static AuthenticationMethod[] Build(ServerProfile server, string? passphrase)
    {
        var user = server.Username;
        if (string.IsNullOrWhiteSpace(user))
            throw new SessionConfigurationException("No username configured.");

        switch (server.AuthMethod)
        {
            case AuthMethod.PrivateKey:
                if (string.IsNullOrWhiteSpace(server.PrivateKey))
                    throw new SessionConfigurationException("No private key configured.");
                try
                {
                    using var stream = new MemoryStream(Encoding.UTF8.GetBytes(server.PrivateKey.Trim() + "\n"));
                    var key = string.IsNullOrEmpty(passphrase)
                        ? new PrivateKeyFile(stream)
                        : new PrivateKeyFile(stream, passphrase);
                    return new AuthenticationMethod[] { new PrivateKeyAuthenticationMethod(user, key) };
                }
                catch (Exception ex)
                {
                    throw new SessionConfigurationException("Could not load private key: " + ex.Message, ex);
                }

            default:
                var password = server.Password ?? "";
                // Many servers only offer keyboard-interactive; answer its password prompt too.
                var interactive = new KeyboardInteractiveAuthenticationMethod(user);
                interactive.AuthenticationPrompt += (_, e) =>
                {
                    foreach (var prompt in e.Prompts)
                        if (prompt.Request.Contains("password", StringComparison.OrdinalIgnoreCase))
                            prompt.Response = password;
                };
                return new AuthenticationMethod[] { new PasswordAuthenticationMethod(user, password), interactive };
        }
    }
}
