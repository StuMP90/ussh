using Ussh.Core.Ssh;

namespace Ussh.App.Services;

public sealed class HostKeyVerifier : IHostKeyVerifier
{
    private readonly DialogService _dialogs;
    // One prompt at a time, so several tabs reconnecting together don't stack dialogs.
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    public HostKeyVerifier(DialogService dialogs) => _dialogs = dialogs;

    /// <summary>
    /// While locked, new or changed keys are rejected without asking: trusting a key is a
    /// security decision that needs the admin password. The session stops with a message and
    /// can be reconnected (and the key reviewed) after unlocking.
    /// </summary>
    public Func<bool> IsLocked { get; set; } = () => false;

    public async Task<bool> VerifyAsync(HostKeyInfo hostKey, CancellationToken cancellationToken)
    {
        if (IsLocked())
            return false;
        await _oneAtATime.WaitAsync(cancellationToken);
        try
        {
            var where = $"{hostKey.ServerName} ({hostKey.Host}:{hostKey.Port})";
            if (hostKey.IsChanged)
            {
                return await _dialogs.ConfirmAsync(
                    "WARNING: host key changed",
                    $"The host key for {where} has CHANGED.\n\n" +
                    "This can mean someone is intercepting the connection (man-in-the-middle), " +
                    "or the server was reinstalled or its keys were rotated.\n\n" +
                    $"Previously trusted:\n  SHA256:{hostKey.KnownFingerprint}\n\n" +
                    $"Now presented ({hostKey.Algorithm}, {hostKey.KeyLength} bits):\n  SHA256:{hostKey.Fingerprint}\n\n" +
                    "Only continue if you have verified the new fingerprint with the server's administrator.",
                    confirmText: "Trust new key",
                    cancelText: "Disconnect",
                    danger: true);
            }

            return await _dialogs.ConfirmAsync(
                "Unknown host key",
                $"First connection to {where}.\n\n" +
                $"Host key ({hostKey.Algorithm}, {hostKey.KeyLength} bits):\n  SHA256:{hostKey.Fingerprint}\n\n" +
                "Trust this key and remember it for future connections?",
                confirmText: "Trust",
                cancelText: "Cancel");
        }
        finally
        {
            _oneAtATime.Release();
        }
    }
}
