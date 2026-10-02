using Ussh.Core.Models;
using Ussh.Core.Ssh;
using Xunit.Abstractions;

namespace Ussh.Core.Tests.Integration;

/// <summary>"Ask every time" key passphrases: prompting, retry, memory-only reuse.</summary>
[Trait("Category", "Integration")]
public sealed class PassphraseTests : IDisposable
{
    private const string Passphrase = "open sesame 42";
    private readonly ITestOutputHelper _output;
    private readonly List<IAsyncDisposable> _sessions = new();
    private readonly List<IDisposable> _servers = new();

    public PassphraseTests(ITestOutputHelper output) => _output = output;

    public void Dispose()
    {
        foreach (var session in _sessions)
            session.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(10));
        foreach (var server in _servers)
            server.Dispose();
    }

    [Fact]
    public void EncryptedKeyValidatesForAskMode()
    {
        if (Start() is not { } server) return;
        Assert.Null(KeyValidator.ValidateEncrypted(server.ClientPrivateKey));
        Assert.NotNull(KeyValidator.Validate(server.ClientPrivateKey, null)); // can't be stored without its passphrase
        Assert.Null(KeyValidator.Validate(server.ClientPrivateKey, Passphrase));
    }

    [Fact]
    public async Task PromptsAndRetriesOnWrongPassphrase()
    {
        if (Start() is not { } server) return;
        var prompts = new ScriptedPrompts("wrong guess", Passphrase);
        var session = Open(server, Profile(server), new CachingPassphraseProvider(prompts.Ask));

        await WaitFor(session, SessionState.Connected);

        Assert.Equal(2, prompts.Calls.Count);
        Assert.Null(prompts.Calls[0].Reason);
        Assert.Contains("didn't unlock", prompts.Calls[1].Reason);
        // The passphrase never lands in the session's (or anyone's) profile.
        Assert.Null(session.Profile.PrivateKeyPassphrase);
    }

    [Fact]
    public async Task ReconnectDoesNotPromptAgain()
    {
        if (Start() is not { } server) return;
        var prompts = new ScriptedPrompts(Passphrase);
        var provider = new CachingPassphraseProvider(prompts.Ask);
        var session = Open(server, Profile(server), provider);
        await WaitFor(session, SessionState.Connected);

        provider.Clear(); // as when zSSH locks: the shared cache is wiped...
        server.Stop();
        await WaitFor(session, SessionState.Reconnecting);
        server.Start();
        await WaitFor(session, SessionState.Connected, TimeSpan.FromSeconds(30));

        Assert.Single(prompts.Calls); // ...but the session reconnects with its own in-memory copy
    }

    [Fact]
    public async Task CancellingThePromptFailsWithoutRetrying()
    {
        if (Start() is not { } server) return;
        var prompts = new ScriptedPrompts((string?)null);
        var session = Open(server, Profile(server), new CachingPassphraseProvider(prompts.Ask));

        await WaitFor(session, SessionState.Failed);
        Assert.Contains("not entered", session.StatusMessage);
        await Task.Delay(1500);
        Assert.Single(prompts.Calls);
    }

    [Fact]
    public async Task PanesForTheSameServerAskOnceUntilCleared()
    {
        if (Start() is not { } server) return;
        var prompts = new ScriptedPrompts(Passphrase, Passphrase);
        var provider = new CachingPassphraseProvider(prompts.Ask);
        var profile = Profile(server);

        // Two panes opening together (split / side by side) share one answer.
        var first = Open(server, profile, provider);
        var second = Open(server, profile, provider);
        await WaitFor(first, SessionState.Connected);
        await WaitFor(second, SessionState.Connected);
        Assert.Single(prompts.Calls);

        // After a lock, a new pane must ask again; the open ones are unaffected.
        provider.Clear();
        var third = Open(server, profile, provider);
        await WaitFor(third, SessionState.Connected);
        Assert.Equal(2, prompts.Calls.Count);
        Assert.Equal(SessionState.Connected, first.State);
    }

    [Fact]
    public async Task NoPromptWhileLocked()
    {
        if (Start() is not { } server) return;
        var prompts = new ScriptedPrompts(Passphrase);
        var provider = new CachingPassphraseProvider(prompts.Ask, canPrompt: () => false);
        var session = Open(server, Profile(server), provider);

        await WaitFor(session, SessionState.Failed);
        Assert.Empty(prompts.Calls);
    }

    private SshTestServer? Start()
    {
        var server = SshTestServer.TryStart(clientKeyPassphrase: Passphrase);
        if (server == null)
        {
            _output.WriteLine("SKIPPED: python3 with paramiko is not available.");
            return null;
        }
        _servers.Add(server);
        return server;
    }

    private static ServerProfile Profile(SshTestServer server) => new()
    {
        Name = "keyed",
        Host = "127.0.0.1",
        Port = server.Port,
        Username = Environment.UserName,
        AuthMethod = AuthMethod.PrivateKey,
        PrivateKey = server.ClientPrivateKey,
        AskForPassphrase = true,
        KeepAliveSeconds = 5,
        ConnectTimeoutSeconds = 5,
    };

    private SshSession Open(SshTestServer server, ServerProfile profile, IPassphraseProvider provider)
    {
        var session = new SshSession(profile, new TrustAll(), passphrases: provider);
        session.StateChanged += s => _output.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {s.State}: {s.StatusMessage}");
        _sessions.Add(session);
        session.Start();
        return session;
    }

    private static async Task WaitFor(SshSession session, SessionState state, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (session.State != state)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Expected {state}, still {session.State}: {session.StatusMessage}");
            await Task.Delay(50);
        }
    }

    /// <summary>Answers prompts from a script, recording each request.</summary>
    private sealed class ScriptedPrompts
    {
        private readonly Queue<string?> _answers;
        public ScriptedPrompts(params string?[] answers) => _answers = new Queue<string?>(answers);
        public List<(string Server, string? Reason)> Calls { get; } = new();

        public Task<string?> Ask(ServerProfile server, string? reason, CancellationToken token)
        {
            lock (Calls)
            {
                Calls.Add((server.DisplayName, reason));
                return Task.FromResult(_answers.Count > 0 ? _answers.Dequeue() : null);
            }
        }
    }

    private sealed class TrustAll : IHostKeyVerifier
    {
        public Task<bool> VerifyAsync(HostKeyInfo hostKey, CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
