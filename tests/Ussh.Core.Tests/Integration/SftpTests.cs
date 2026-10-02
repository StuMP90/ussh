using System.Security.Cryptography;
using Ussh.Core.Files;
using Ussh.Core.Models;
using Ussh.Core.Ssh;
using Xunit.Abstractions;

namespace Ussh.Core.Tests.Integration;

/// <summary>SFTP file browsing and the transfer queue, against a real SFTP server.</summary>
[Trait("Category", "Integration")]
public sealed class SftpTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly List<IDisposable> _dispose = new();
    private readonly List<IAsyncDisposable> _asyncDispose = new();
    private readonly string _local = Path.Combine(Path.GetTempPath(), "ussh-sftp-local-" + Guid.NewGuid().ToString("N"));

    public SftpTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_local);
    }

    public void Dispose()
    {
        foreach (var item in _asyncDispose)
            item.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(10));
        foreach (var item in _dispose)
            item.Dispose();
        try { Directory.Delete(_local, true); } catch { }
    }

    [Fact]
    public async Task BrowseCreateRenameChmodAndDelete()
    {
        if (Start() is not { } server) return;
        var trusted = new List<Guid>();
        var (fs, _) = Open(server, connector => connector.HostKeyTrusted += (id, _) => trusted.Add(id));

        Assert.Equal("/", await fs.GetStartPathAsync(default));
        Assert.Empty(await fs.ListAsync("/", default));
        Assert.Single(trusted); // host key confirmed once, then remembered for the session

        await fs.CreateDirectoryAsync("/docs", default);
        File.WriteAllText(Path.Combine(server.SftpRoot!, "docs", "a.txt"), "hello");
        var docs = Assert.Single(await fs.ListAsync("/", default));
        Assert.True(docs.IsDirectory);
        var file = Assert.Single(await fs.ListAsync("/docs", default));
        Assert.Equal(("a.txt", "/docs/a.txt", 5L), (file.Name, file.Path, file.Size));
        Assert.StartsWith("-rw", file.Permissions);

        await fs.RenameAsync("/docs/a.txt", "/docs/b.txt", default);
        await fs.SetPermissionsAsync("/docs/b.txt", Convert.ToInt32("600", 8), default);
        var renamed = await fs.GetEntryAsync("/docs/b.txt", default);
        Assert.Equal("-rw-------", renamed!.Permissions);
        Assert.Null(await fs.GetEntryAsync("/docs/a.txt", default));

        var size = await FileOperations.MeasureAsync(fs, new[] { docs }, default);
        Assert.Equal((1, 1, 5L), (size.Files, size.Folders, size.Bytes));
        await FileOperations.DeleteTreeAsync(fs, docs, default);
        Assert.Empty(await fs.ListAsync("/", default));
    }

    [Fact]
    public async Task BrowsesThroughJumpHost()
    {
        if (Start() is not { } bastion || Start(sftp: true) is not { } target) return;
        File.WriteAllText(Path.Combine(target.SftpRoot!, "behind-the-bastion.txt"), "x");
        var connector = new SshConnector(new TrustAll(), null);
        var fs = new SftpFileSystem("target", token => connector.ConnectSftpAsync(Profile(target), new[] { Profile(bastion) }, token));
        _asyncDispose.Add(fs);

        var entry = Assert.Single(await fs.ListAsync("/", default));
        Assert.Equal("behind-the-bastion.txt", entry.Name);
    }

    [Fact]
    public async Task ReconnectsAfterServerRestart()
    {
        if (Start() is not { } server) return;
        var (fs, _) = Open(server);
        Assert.Empty(await fs.ListAsync("/", default));

        server.Stop();
        server.Start();
        File.WriteAllText(Path.Combine(server.SftpRoot!, "after-restart.txt"), "x");

        Assert.Single(await fs.ListAsync("/", default)); // dead connection detected, reconnected, retried
    }

    [Fact]
    public async Task UploadsAndDownloadsFolderTreesIdentically()
    {
        if (Start() is not { } server) return;
        var (_, queue) = Open(server);
        var source = Path.Combine(_local, "project");
        Directory.CreateDirectory(Path.Combine(source, "src", "deep"));
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        WriteRandom(Path.Combine(source, "readme.md"), 1_000);
        WriteRandom(Path.Combine(source, "src", "main.c"), 300_000);
        WriteRandom(Path.Combine(source, "src", "deep", "data.bin"), 2_500_000);

        var local = new LocalFileSystem();
        await queue.UploadAsync(new[] { (await local.GetEntryAsync(source, default))! }, "/");
        await WaitForQueue(queue);
        Assert.All(queue.Items, i => Assert.Equal(TransferState.Completed, i.State));
        Assert.Equal(3, queue.Items.Count);
        Assert.True(Directory.Exists(Path.Combine(server.SftpRoot!, "project", "empty")));
        AssertSameTree(source, Path.Combine(server.SftpRoot!, "project"));

        var back = Path.Combine(_local, "downloaded");
        Directory.CreateDirectory(back);
        var remote = new SftpFileSystem("browse", token => new SshConnector(new TrustAll(), null).ConnectSftpAsync(Profile(server), Array.Empty<ServerProfile>(), token));
        _asyncDispose.Add(remote);
        await queue.DownloadAsync(new[] { (await remote.GetEntryAsync("/project", default))! }, back);
        await WaitForQueue(queue);
        Assert.All(queue.Items, i => Assert.Equal(TransferState.Completed, i.State));
        AssertSameTree(source, Path.Combine(back, "project"));
    }

    [Fact]
    public async Task ConflictsAreAskedAndCanApplyToAll()
    {
        if (Start() is not { } server) return;
        var asked = 0;
        var choice = ConflictChoice.Skip;
        var applyToAll = false;
        var (_, queue) = Open(server, conflict: _ =>
        {
            Interlocked.Increment(ref asked);
            return Task.FromResult(new ConflictDecision(choice, applyToAll));
        });
        var files = Enumerable.Range(1, 3).Select(i => Path.Combine(_local, $"f{i}.txt")).ToList();
        foreach (var f in files)
            File.WriteAllText(f, "new " + Path.GetFileName(f));
        foreach (var f in files)
            File.WriteAllText(Path.Combine(server.SftpRoot!, Path.GetFileName(f)), "old");
        var local = new LocalFileSystem();
        var entries = new List<FileEntry>();
        foreach (var f in files)
            entries.Add((await local.GetEntryAsync(f, default))!);

        // Skip, asked per file.
        await queue.UploadAsync(entries, "/");
        await WaitForQueue(queue);
        Assert.Equal(3, asked);
        Assert.All(queue.Items, i => Assert.Equal(TransferState.Skipped, i.State));
        Assert.Equal("old", File.ReadAllText(Path.Combine(server.SftpRoot!, "f1.txt")));

        // Keep both, applied to all after one question.
        queue.ClearFinished();
        (asked, choice, applyToAll) = (0, ConflictChoice.KeepBoth, true);
        await queue.UploadAsync(entries, "/");
        await WaitForQueue(queue);
        Assert.Equal(1, asked);
        Assert.Equal("new f2.txt", File.ReadAllText(Path.Combine(server.SftpRoot!, "f2 (1).txt")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(server.SftpRoot!, "f2.txt")));

        // Overwrite.
        queue.ClearFinished();
        (asked, choice) = (0, ConflictChoice.Overwrite);
        await queue.UploadAsync(entries, "/");
        await WaitForQueue(queue);
        Assert.Equal("new f3.txt", File.ReadAllText(Path.Combine(server.SftpRoot!, "f3.txt")));
    }

    [Fact]
    public async Task UploadResumesAfterConnectionDrop()
    {
        if (Start() is not { } server) return;
        var (_, queue) = Open(server);
        var big = Path.Combine(_local, "big.bin");
        WriteRandom(big, 60_000_000);
        var local = new LocalFileSystem();

        await queue.UploadAsync(new[] { (await local.GetEntryAsync(big, default))! }, "/");
        var item = Assert.Single(queue.Items);
        await WaitUntil(() => item.TransferredBytes > 10_000_000 || item.IsFinished, "upload under way");
        Assert.False(item.IsFinished, "file uploaded too quickly to interrupt");
        server.Stop(); // connection drops mid-file
        await Task.Delay(1500);
        server.Start();
        await WaitForQueue(queue, TimeSpan.FromSeconds(120));

        _output.WriteLine($"attempts: {item.Attempts}");
        Assert.Equal(TransferState.Completed, item.State);
        Assert.True(item.Attempts > 1);
        Assert.Equal(Hash(big), Hash(Path.Combine(server.SftpRoot!, "big.bin")));
    }

    [Fact]
    public async Task DownloadResumesAfterConnectionDrop()
    {
        if (Start() is not { } server) return;
        var (fs, queue) = Open(server);
        var remoteFile = Path.Combine(server.SftpRoot!, "big.bin");
        WriteRandom(remoteFile, 60_000_000);

        await queue.DownloadAsync(new[] { (await fs.GetEntryAsync("/big.bin", default))! }, _local);
        var item = Assert.Single(queue.Items);
        await WaitUntil(() => item.TransferredBytes > 10_000_000 || item.IsFinished, "download under way");
        Assert.False(item.IsFinished, "file downloaded too quickly to interrupt");
        server.Stop();
        await Task.Delay(1500);
        server.Start();
        await WaitForQueue(queue, TimeSpan.FromSeconds(120));

        Assert.Equal(TransferState.Completed, item.State);
        Assert.True(item.Attempts > 1);
        Assert.Equal(Hash(remoteFile), Hash(Path.Combine(_local, "big.bin")));
    }

    [Fact]
    public async Task CancelThenRetry()
    {
        if (Start() is not { } server) return;
        var (_, queue) = Open(server);
        var big = Path.Combine(_local, "cancel-me.bin");
        WriteRandom(big, 60_000_000);
        await queue.UploadAsync(new[] { (await new LocalFileSystem().GetEntryAsync(big, default))! }, "/");
        var item = Assert.Single(queue.Items);
        await WaitUntil(() => item.TransferredBytes > 5_000_000 || item.IsFinished, "upload under way");

        queue.Cancel(item);
        await WaitForQueue(queue);
        Assert.Equal(TransferState.Cancelled, item.State);

        queue.Retry(item);
        await WaitForQueue(queue, TimeSpan.FromSeconds(120));
        Assert.Equal(TransferState.Completed, item.State);
        Assert.Equal(Hash(big), Hash(Path.Combine(server.SftpRoot!, "cancel-me.bin")));
    }

    [Fact]
    public async Task PermissionDeniedFailsWithoutRetrying()
    {
        if (OperatingSystem.IsWindows() || Start() is not { } server) return; // uses Unix permissions
        var (_, queue) = Open(server);
        var locked = Path.Combine(server.SftpRoot!, "readonly");
        Directory.CreateDirectory(locked);
        File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var file = Path.Combine(_local, "x.txt");
            File.WriteAllText(file, "x");
            await queue.UploadAsync(new[] { (await new LocalFileSystem().GetEntryAsync(file, default))! }, "/readonly");
            await WaitForQueue(queue);

            var item = Assert.Single(queue.Items);
            Assert.Equal(TransferState.Failed, item.State);
            Assert.Equal(1, item.Attempts);
            Assert.Contains("ermission", item.Error);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    // ------------------------------------------------------------------ helpers

    private SshTestServer? Start(bool sftp = true)
    {
        var server = SshTestServer.TryStart(sftp: sftp);
        if (server == null)
        {
            _output.WriteLine("SKIPPED: python3 with paramiko is not available.");
            return null;
        }
        _dispose.Add(server);
        return server;
    }

    private (SftpFileSystem Browse, TransferQueue Queue) Open(SshTestServer server, Action<SshConnector>? configure = null,
        Func<TransferConflict, Task<ConflictDecision>>? conflict = null)
    {
        var connector = new SshConnector(new TrustAll(), null);
        configure?.Invoke(connector);
        var profile = Profile(server);
        // As in the app: one connection for browsing, a separate one for transfers.
        var browse = new SftpFileSystem(profile.DisplayName, token => connector.ConnectSftpAsync(profile, Array.Empty<ServerProfile>(), token));
        var transfers = new SftpFileSystem(profile.DisplayName, token => connector.ConnectSftpAsync(profile, Array.Empty<ServerProfile>(), token));
        var queue = new TransferQueue(new LocalFileSystem(), transfers,
            conflict ?? (_ => Task.FromResult(new ConflictDecision(ConflictChoice.Overwrite, true))));
        _asyncDispose.Add(queue);
        _asyncDispose.Add(browse);
        return (browse, queue);
    }

    private static ServerProfile Profile(SshTestServer server) => new()
    {
        Name = "files",
        Kind = ServerKind.SftpOnly,
        Host = "127.0.0.1",
        Port = server.Port,
        Username = Environment.UserName,
        Password = SshTestServer.Password,
        KeepAliveSeconds = 5,
        ConnectTimeoutSeconds = 5,
    };

    private static async Task WaitForQueue(TransferQueue queue, TimeSpan? timeout = null)
    {
        await Task.Delay(100);
        await WaitUntil(() => queue.ActiveCount == 0, "transfers to finish", timeout);
    }

    private static async Task WaitUntil(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Timed out waiting for " + what);
            await Task.Delay(20);
        }
    }

    private static void WriteRandom(string path, int bytes)
    {
        var data = new byte[bytes];
        Random.Shared.NextBytes(data);
        File.WriteAllBytes(path, data);
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void AssertSameTree(string expected, string actual)
    {
        var expectedFiles = Directory.GetFiles(expected, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(expected, f)).Order().ToList();
        var actualFiles = Directory.GetFiles(actual, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(actual, f)).Order().ToList();
        Assert.Equal(expectedFiles, actualFiles);
        foreach (var file in expectedFiles)
            Assert.Equal(Hash(Path.Combine(expected, file)), Hash(Path.Combine(actual, file)));
    }

    private sealed class TrustAll : IHostKeyVerifier
    {
        public Task<bool> VerifyAsync(HostKeyInfo hostKey, CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
