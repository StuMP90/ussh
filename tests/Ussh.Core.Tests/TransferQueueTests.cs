using Ussh.Core.Files;

namespace Ussh.Core.Tests;

public sealed class TransferQueueTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zssh-queue-" + Guid.NewGuid().ToString("N"));

    public TransferQueueTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    /// <summary>
    /// Cancel, then Retry straight away, while the cancelled run is still unwinding (as a real
    /// network read does). The retry must wait for it: otherwise two runs write one file, and the
    /// old run's "cancelled" lands on the new one (seen in CI as Expected Completed, Actual Cancelled).
    /// </summary>
    [Fact]
    public async Task RetryRightAfterCancelWaitsForTheCancelledRun()
    {
        var data = new byte[3 * 1024 * 1024];
        new Random(42).NextBytes(data);
        var remote = new SlowToCancelFileSystem(data);
        await using var queue = new TransferQueue(new LocalFileSystem(), remote,
            _ => Task.FromResult(new ConflictDecision(ConflictChoice.Overwrite, true)));

        await queue.DownloadAsync(new[] { remote.Entry }, _dir);
        var item = Assert.Single(queue.Items);
        await remote.FirstReadDone.Task.WaitAsync(TimeSpan.FromSeconds(10));

        queue.Cancel(item);
        queue.Retry(item);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        do
            await Task.Delay(50);
        while (queue.ActiveCount > 0 && DateTime.UtcNow < deadline);

        Assert.Equal(TransferState.Completed, item.State);
        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(_dir, "data.bin")));
    }

    /// <summary>A remote file whose first read stream takes a while to notice cancellation.</summary>
    private sealed class SlowToCancelFileSystem(byte[] data) : IFileSystem
    {
        private int _opens;
        public FileEntry Entry { get; } = new("data.bin", "/data.bin", false, data.Length, DateTimeOffset.Now);
        public TaskCompletionSource FirstReadDone { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Name => "fake";
        public bool IsLocal => false;
        public bool SupportsPermissions => false;

        public Task<Stream> OpenReadAsync(string path, long offset, CancellationToken token)
        {
            var source = new MemoryStream(data, (int)offset, data.Length - (int)offset, writable: false);
            return Task.FromResult<Stream>(Interlocked.Increment(ref _opens) == 1 ? new SlowStream(source, FirstReadDone) : source);
        }

        public Task<FileEntry?> GetEntryAsync(string path, CancellationToken token) => Task.FromResult<FileEntry?>(Entry);
        public Task<string> GetStartPathAsync(CancellationToken token) => Task.FromResult("/");
        public Task<IReadOnlyList<FileEntry>> ListAsync(string directory, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<FileEntry>>(new[] { Entry });
        public Task CreateDirectoryAsync(string path, CancellationToken token) => throw new NotSupportedException();
        public Task DeleteFileAsync(string path, CancellationToken token) => throw new NotSupportedException();
        public Task DeleteDirectoryAsync(string path, CancellationToken token) => throw new NotSupportedException();
        public Task RenameAsync(string from, string to, CancellationToken token) => throw new NotSupportedException();
        public Task SetPermissionsAsync(string path, int mode, CancellationToken token) => throw new NotSupportedException();
        public Task UploadAsync(string localFile, string path, bool resume, IProgress<long> progress, CancellationToken token) =>
            throw new NotSupportedException();
        public string Combine(string directory, string name) => directory.TrimEnd('/') + "/" + name;
        public string? Parent(string path) => path == "/" ? null : "/";
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Returns one chunk, then hangs until cancelled and takes 500 ms more to give up.</summary>
    private sealed class SlowStream(Stream inner, TaskCompletionSource firstReadDone) : Stream
    {
        private bool _servedOne;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (!_servedOne)
            {
                _servedOne = true;
                var read = await inner.ReadAsync(buffer[..Math.Min(buffer.Length, 64 * 1024)], token);
                firstReadDone.TrySetResult();
                return read;
            }
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { await Task.Delay(500, CancellationToken.None); }
            throw new OperationCanceledException(token);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
