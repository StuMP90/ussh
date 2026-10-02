using System.Diagnostics;
using Ussh.Core.Models;
using Ussh.Core.Ssh;
using Ussh.Core.Tests.Integration;
using Xunit.Abstractions;

namespace Ussh.Core.Tests;

/// <summary>
/// PuTTY .ppk private keys (formats v2 and v3), generated with the real puttygen.
/// Tests return early, passing, where puttygen isn't installed.
/// </summary>
public sealed class PuTTYKeyTests : IDisposable
{
    private const string Passphrase = "putty passphrase";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ussh-ppk-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;
    private readonly List<IDisposable> _dispose = new();
    private readonly List<IAsyncDisposable> _sessions = new();

    public PuTTYKeyTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_dir);
        File.WriteAllText(PassphraseFile, Passphrase);
    }

    private string PassphraseFile => Path.Combine(_dir, "passphrase.txt");

    public void Dispose()
    {
        foreach (var session in _sessions)
            session.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(10));
        foreach (var item in _dispose)
            item.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static bool PuttygenAvailable { get; } = Run("puttygen", "--version") == 0;

    [Theory]
    [InlineData("ed25519", 2)]
    [InlineData("ed25519", 3)]
    [InlineData("rsa", 2)]
    [InlineData("rsa", 3)]
    [InlineData("ecdsa", 2)]
    [InlineData("ecdsa", 3)]
    public void GeneratedPpkKeysValidate(string type, int version)
    {
        if (!Available()) return;
        var plain = Generate(type, version, encrypted: false);
        var encrypted = Generate(type, version, encrypted: true);
        Assert.True(KeyValidator.IsPuTTY(plain));

        Assert.Null(KeyValidator.Validate(plain, null));
        Assert.Null(KeyValidator.Validate(encrypted, Passphrase));
        Assert.NotNull(KeyValidator.Validate(encrypted, null));
        Assert.Equal("Private key could not be loaded: wrong passphrase for this PuTTY key.", KeyValidator.Validate(encrypted, "wrong"));

        // "Ask every time" needs a passphrase-protected key.
        Assert.Null(KeyValidator.ValidateEncrypted(encrypted));
        Assert.Contains("no passphrase", KeyValidator.ValidateEncrypted(plain));
    }

    [Fact]
    public void PuTTYPublicKeyIsRecognisedAsPublic()
    {
        if (!Available()) return;
        var ppk = Path.Combine(_dir, "pub_src.ppk");
        Assert.Equal(0, Run("puttygen", "-t", "ed25519", "-o", ppk, "--new-passphrase", "/dev/null"));
        var pub = Path.Combine(_dir, "pub.txt");
        Assert.Equal(0, Run("puttygen", ppk, "-O", "public", "-o", pub));
        Assert.Contains("public key", KeyValidator.Validate(File.ReadAllText(pub), null));
    }

    [Fact]
    public async Task LogsInWithUnencryptedPpkV3()
    {
        if (!Available() || StartServer() is not { } server) return;
        var session = Open(server, Profile(server, ConvertToPpk(server.ClientPrivateKey!, 3, encrypted: false)));
        await WaitFor(session, SessionState.Connected);
    }

    [Fact]
    public async Task LogsInWithEncryptedPpkV2AndStoredPassphrase()
    {
        if (!Available() || StartServer() is not { } server) return;
        var profile = Profile(server, ConvertToPpk(server.ClientPrivateKey!, 2, encrypted: true));
        profile.PrivateKeyPassphrase = Passphrase;
        var session = Open(server, profile);
        await WaitFor(session, SessionState.Connected);
    }

    [Fact]
    public async Task LogsInWithEncryptedPpkV3AndAskEveryTime()
    {
        if (!Available() || StartServer() is not { } server) return;
        var profile = Profile(server, ConvertToPpk(server.ClientPrivateKey!, 3, encrypted: true));
        profile.AskForPassphrase = true;
        Assert.Null(KeyValidator.ValidateEncrypted(profile.PrivateKey));
        var answers = new Queue<string>(new[] { "wrong", Passphrase });
        var asked = new List<string?>();
        var provider = new CachingPassphraseProvider((_, reason, _) =>
        {
            asked.Add(reason);
            return Task.FromResult<string?>(answers.Dequeue());
        });

        var session = Open(server, profile, provider);

        await WaitFor(session, SessionState.Connected);
        Assert.Equal(2, asked.Count);
        Assert.NotNull(asked[1]); // the second prompt explained the first answer was wrong
    }

    // ------------------------------------------------------------------ helpers

    private bool Available()
    {
        if (PuttygenAvailable)
            return true;
        _output.WriteLine("SKIPPED: puttygen is not installed.");
        return false;
    }

    private SshTestServer? StartServer()
    {
        var server = SshTestServer.TryStart(withClientKey: true);
        if (server == null)
        {
            _output.WriteLine("SKIPPED: python3 with paramiko is not available.");
            return null;
        }
        _dispose.Add(server);
        return server;
    }

    private string Generate(string type, int version, bool encrypted)
    {
        var file = Path.Combine(_dir, $"{type}_v{version}_{(encrypted ? "enc" : "plain")}.ppk");
        Assert.Equal(0, Run("puttygen", "-t", type, "--ppk-param", $"version={version}",
            "--new-passphrase", encrypted ? PassphraseFile : "/dev/null", "-o", file));
        return File.ReadAllText(file);
    }

    /// <summary>Converts an OpenSSH private key to .ppk, as a PuTTY user would have it.</summary>
    private string ConvertToPpk(string openSshKey, int version, bool encrypted)
    {
        var source = Path.Combine(_dir, $"openssh_{Guid.NewGuid():N}");
        File.WriteAllText(source, openSshKey);
        var target = source + ".ppk";
        var args = new List<string> { source, "-O", "private", "-o", target, "--ppk-param", $"version={version}" };
        if (encrypted)
            args.AddRange(new[] { "-P", "--new-passphrase", PassphraseFile });
        Assert.Equal(0, Run("puttygen", args.ToArray()));
        var ppk = File.ReadAllText(target);
        Assert.StartsWith($"PuTTY-User-Key-File-{version}:", ppk);
        return ppk;
    }

    private static ServerProfile Profile(SshTestServer server, string privateKey) => new()
    {
        Name = "putty-user",
        Host = "127.0.0.1",
        Port = server.Port,
        Username = Environment.UserName,
        AuthMethod = AuthMethod.PrivateKey,
        PrivateKey = privateKey,
        KeepAliveSeconds = 5,
        ConnectTimeoutSeconds = 5,
    };

    private SshSession Open(SshTestServer server, ServerProfile profile, IPassphraseProvider? provider = null)
    {
        var session = new SshSession(profile, new TrustAll(), passphrases: provider);
        session.StateChanged += s => _output.WriteLine($"{s.State}: {s.StatusMessage}");
        _sessions.Add(session);
        session.Start();
        return session;
    }

    private static async Task WaitFor(SshSession session, SessionState state)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (session.State != state)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Expected {state}, still {session.State}: {session.StatusMessage}");
            await Task.Delay(50);
        }
    }

    private static int Run(string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi)!;
            process.StandardInput.Close();
            return process.WaitForExit(60_000) ? process.ExitCode : -1;
        }
        catch
        {
            return -1;
        }
    }

    private sealed class TrustAll : IHostKeyVerifier
    {
        public Task<bool> VerifyAsync(HostKeyInfo hostKey, CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
