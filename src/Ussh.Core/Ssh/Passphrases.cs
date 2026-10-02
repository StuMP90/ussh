using System.Collections.Concurrent;
using Ussh.Core.Models;

namespace Ussh.Core.Ssh;

/// <summary>Supplies key passphrases for servers set to "ask every time".</summary>
public interface IPassphraseProvider
{
    /// <summary>
    /// Returns the passphrase for <paramref name="server"/>'s key, or null if the user cancelled
    /// (or it can't be asked for right now, e.g. while locked).
    /// </summary>
    /// <param name="retryReason">Set when a previous answer was wrong; the provider must ask again.</param>
    Task<string?> GetPassphraseAsync(ServerProfile server, string? retryReason, CancellationToken cancellationToken);
}

/// <summary>
/// Prompts through a delegate and remembers answers in memory, per server, so splits and
/// side-by-side panes for the same server ask once. Nothing is ever written to disk.
/// <see cref="Clear"/> forgets everything (called when the app locks or exits); sessions that
/// already connected keep their own copy for reconnects.
/// </summary>
public sealed class CachingPassphraseProvider : IPassphraseProvider
{
    private readonly Func<ServerProfile, string?, CancellationToken, Task<string?>> _prompt;
    private readonly Func<bool> _canPrompt;
    private readonly ConcurrentDictionary<Guid, string> _cache = new();
    // One prompt at a time: panes opening together then find the first answer in the cache.
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    /// <param name="canPrompt">False while prompting isn't allowed (e.g. locked): requests then return null.</param>
    public CachingPassphraseProvider(Func<ServerProfile, string?, CancellationToken, Task<string?>> prompt, Func<bool>? canPrompt = null)
    {
        _prompt = prompt;
        _canPrompt = canPrompt ?? (() => true);
    }

    public async Task<string?> GetPassphraseAsync(ServerProfile server, string? retryReason, CancellationToken cancellationToken)
    {
        if (retryReason != null)
            _cache.TryRemove(server.Id, out _);
        else if (_cache.TryGetValue(server.Id, out var cached))
            return cached;

        if (!_canPrompt())
            return null;
        await _oneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (retryReason == null && _cache.TryGetValue(server.Id, out var answeredMeanwhile))
                return answeredMeanwhile;
            var passphrase = await _prompt(server, retryReason, cancellationToken).ConfigureAwait(false);
            if (passphrase != null)
                _cache[server.Id] = passphrase;
            return passphrase;
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    public void Clear() => _cache.Clear();
}
