using System.Diagnostics;
using Ussh.Core.Diagnostics;

namespace Ussh.Core.Files;

public enum TransferDirection
{
    Upload,
    Download,
}

public enum TransferState
{
    Queued,
    Running,
    /// <summary>Failed but will retry automatically (connection trouble).</summary>
    Retrying,
    Completed,
    Failed,
    Cancelled,
    Skipped,
}

public enum ConflictChoice
{
    Overwrite,
    Skip,
    /// <summary>Keep both: the new copy gets a free name like "file (1).txt".</summary>
    KeepBoth,
    /// <summary>
    /// Overwrite only when the sizes differ or the file being copied is newer; otherwise skip
    /// it as up to date.
    /// </summary>
    OverwriteIfDifferent,
    /// <summary>Cancel the rest of this batch.</summary>
    CancelBatch,
}

/// <summary>A destination file that already exists; the UI decides what to do.</summary>
public sealed record TransferConflict(TransferItem Item, FileEntry Existing);

public sealed record ConflictDecision(ConflictChoice Choice, bool ApplyToAll);

/// <summary>
/// One file being copied. Updated from worker threads: read it on a timer rather than
/// binding to change notifications.
/// </summary>
public sealed class TransferItem
{
    private long _transferred;

    internal TransferItem(TransferBatch batch, TransferDirection direction, FileEntry source, string destination)
    {
        Batch = batch;
        Direction = direction;
        SourcePath = source.Path;
        SourceModified = source.Modified;
        DestinationPath = destination;
        Name = source.Name;
        TotalBytes = source.Size;
    }

    /// <summary>When the source file was last modified (for "overwrite if different").</summary>
    public DateTimeOffset? SourceModified { get; }

    public Guid Id { get; } = Guid.NewGuid();
    public TransferBatch Batch { get; }
    public TransferDirection Direction { get; }
    public string SourcePath { get; }
    public string DestinationPath { get; internal set; }
    public string Name { get; }
    public long TotalBytes { get; internal set; }
    public long TransferredBytes => Interlocked.Read(ref _transferred);
    public TransferState State { get; internal set; } = TransferState.Queued;
    public string? Error { get; internal set; }
    public int Attempts { get; internal set; }
    public DateTimeOffset? StartedAt { get; internal set; }
    public DateTimeOffset? FinishedAt { get; internal set; }

    public bool IsFinished => State is TransferState.Completed or TransferState.Failed or TransferState.Cancelled or TransferState.Skipped;
    public bool IsActive => State is TransferState.Queued or TransferState.Running or TransferState.Retrying;

    internal CancellationTokenSource Cancellation { get; set; } = new();

    /// <summary>Set once bytes have been written, so a retry resumes instead of restarting.</summary>
    internal bool HasPartialData { get; set; }

    /// <summary>ItemFinished has been raised for the current run.</summary>
    internal bool FinishNotified { get; set; }

    /// <summary>The item's latest run. A cancelled run can still be unwinding after its state says Cancelled.</summary>
    internal Task Worker { get; set; } = Task.CompletedTask;

    /// <summary>Retried, waiting for the previous run to let go of the file before starting again.</summary>
    internal bool RestartPending { get; set; }

    internal void SetTransferred(long bytes) => Interlocked.Exchange(ref _transferred, bytes);
}

/// <summary>Files queued together (one upload or download action); conflict answers can apply to all.</summary>
public sealed class TransferBatch
{
    internal ConflictDecision? RememberedDecision { get; set; }
    internal bool Cancelled { get; set; }
}

/// <summary>
/// Copies files between this computer and one remote file system, a few at a time.
///
/// Folders are expanded into their files (creating folders at the destination first).
/// Connection problems are retried automatically with backoff, resuming from where the copy
/// stopped; other errors (permission denied, disk full…) fail the item, which can be retried.
/// Uses its own remote connection so browsing stays responsive during big transfers.
/// </summary>
public sealed class TransferQueue : IAsyncDisposable
{
    private const int AutomaticAttempts = 6;
    private readonly IFileSystem _local;
    private readonly IFileSystem _remote;
    private readonly Func<TransferConflict, Task<ConflictDecision>> _onConflict;
    private readonly SemaphoreSlim _slots;
    private readonly SemaphoreSlim _conflictGate = new(1, 1);
    private readonly List<TransferItem> _items = new();
    private readonly CancellationTokenSource _disposed = new();

    /// <param name="remote">A file system dedicated to transfers (not the one used for browsing).</param>
    /// <param name="onConflict">Asks what to do when a destination file exists. Called one at a time.</param>
    public TransferQueue(IFileSystem local, IFileSystem remote, Func<TransferConflict, Task<ConflictDecision>> onConflict, int concurrency = 2)
    {
        _local = local;
        _remote = remote;
        _onConflict = onConflict;
        _slots = new SemaphoreSlim(Math.Max(1, concurrency));
    }

    /// <summary>Raised (on a worker thread) when a file finishes, fails or is skipped.</summary>
    public event Action<TransferItem>? ItemFinished;

    public IReadOnlyList<TransferItem> Items
    {
        get { lock (_items) return _items.ToList(); }
    }

    public int ActiveCount
    {
        get { lock (_items) return _items.Count(i => i.IsActive || i.RestartPending); }
    }

    /// <summary>Queues local files/folders for upload into <paramref name="remoteDirectory"/>. Returns once queued.</summary>
    public Task<TransferBatch> UploadAsync(IEnumerable<FileEntry> localEntries, string remoteDirectory) =>
        EnqueueAsync(TransferDirection.Upload, _local, _remote, localEntries.ToList(), remoteDirectory);

    /// <summary>Queues remote files/folders for download into <paramref name="localDirectory"/>. Returns once queued.</summary>
    public Task<TransferBatch> DownloadAsync(IEnumerable<FileEntry> remoteEntries, string localDirectory) =>
        EnqueueAsync(TransferDirection.Download, _remote, _local, remoteEntries.ToList(), localDirectory);

    /// <summary>Stops an item; its worker then reports it as cancelled.</summary>
    public void Cancel(TransferItem item)
    {
        if (item.IsFinished && !item.RestartPending)
            return;
        item.State = TransferState.Cancelled;
        try { item.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
    }

    public void CancelAll()
    {
        foreach (var item in Items)
            Cancel(item);
    }

    /// <summary>Runs a failed or cancelled item again, resuming where it stopped.</summary>
    public void Retry(TransferItem item)
    {
        if (item.State is not (TransferState.Failed or TransferState.Cancelled) || item.RestartPending)
            return;
        var cancellation = new CancellationTokenSource();
        item.Cancellation = cancellation;
        item.RestartPending = true;
        item.Worker = RestartAsync(item, item.Worker, cancellation);
    }

    /// <summary>
    /// Starts a retry once the previous run has ended. A cancelled run stops at its next await, so
    /// without this a quick retry would race it: two writers on one file, and the old run's
    /// "cancelled" landing on the new one.
    /// </summary>
    private async Task RestartAsync(TransferItem item, Task previous, CancellationTokenSource cancellation)
    {
        await previous.ConfigureAwait(false); // RunAsync never throws
        lock (item)
        {
            if (cancellation.IsCancellationRequested)
            {
                item.RestartPending = false;
                return; // cancelled again before it restarted
            }
            item.FinishNotified = false;
            item.State = TransferState.Queued;
            item.Error = null;
            item.Attempts = 0;
            item.FinishedAt = null;
            item.RestartPending = false; // after Queued, so ActiveCount never dips to zero here
        }
        await RunAsync(item).ConfigureAwait(false);
    }

    public void ClearFinished()
    {
        lock (_items)
            _items.RemoveAll(i => i.IsFinished);
    }

    public async ValueTask DisposeAsync()
    {
        CancelAll();
        _disposed.Cancel();
        await _remote.DisposeAsync().ConfigureAwait(false);
    }

    private async Task<TransferBatch> EnqueueAsync(TransferDirection direction, IFileSystem source, IFileSystem destination,
        IReadOnlyList<FileEntry> entries, string destinationDirectory)
    {
        var batch = new TransferBatch();
        var token = _disposed.Token;
        // Refuse up front (e.g. uploading into an S3 account's bucket list) before queuing anything.
        foreach (var entry in entries)
        {
            if (destination.ReadOnlyReason(destination.Combine(destinationDirectory, entry.Name)) is { } reason)
                throw new UnauthorizedAccessException(reason);
        }
        foreach (var entry in entries)
        {
            if (!entry.IsDirectory)
            {
                Add(new TransferItem(batch, direction, entry, destination.Combine(destinationDirectory, entry.Name)));
                continue;
            }
            // Recreate the folder structure, then queue its files.
            var root = destination.Combine(destinationDirectory, entry.Name);
            var map = new Dictionary<string, string> { [entry.Path] = root };
            await foreach (var item in FileOperations.WalkAsync(source, entry, token).ConfigureAwait(false))
            {
                var parent = source.Parent(item.Path);
                var target = item == entry ? root : destination.Combine(map[parent!], item.Name);
                if (item.IsDirectory)
                {
                    map[item.Path] = target;
                    if (await destination.GetEntryAsync(target, token).ConfigureAwait(false) == null)
                        await destination.CreateDirectoryAsync(target, token).ConfigureAwait(false);
                }
                else
                {
                    Add(new TransferItem(batch, direction, item, target));
                }
            }
        }
        return batch;
    }

    private void Add(TransferItem item)
    {
        lock (_items)
            _items.Add(item);
        item.Worker = RunAsync(item);
    }

    private async Task RunAsync(TransferItem item)
    {
        try
        {
            await _slots.WaitAsync(item.Cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Finish(item, TransferState.Cancelled, null);
            return;
        }
        try
        {
            if (item.Batch.Cancelled)
            {
                Finish(item, TransferState.Cancelled, null);
                return;
            }
            if (!item.HasPartialData && !await ResolveConflictAsync(item).ConfigureAwait(false))
                return;
            await TransferWithRetriesAsync(item).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(nameof(TransferQueue), $"Transfer of {item.Name} failed unexpectedly.", ex);
            Finish(item, TransferState.Failed, ex.Message);
        }
        finally
        {
            _slots.Release();
        }
    }

    /// <summary>Handles an existing destination file. False if the item ended (skipped/cancelled).</summary>
    private async Task<bool> ResolveConflictAsync(TransferItem item)
    {
        var destination = item.Direction == TransferDirection.Upload ? _remote : _local;
        var token = item.Cancellation.Token;
        var existing = await destination.GetEntryAsync(item.DestinationPath, token).ConfigureAwait(false);
        if (existing == null)
            return true;

        ConflictDecision decision;
        await _conflictGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            decision = item.Batch.RememberedDecision
                ?? await _onConflict(new TransferConflict(item, existing)).ConfigureAwait(false);
            if (decision.ApplyToAll)
                item.Batch.RememberedDecision = decision;
        }
        finally
        {
            _conflictGate.Release();
        }

        switch (decision.Choice)
        {
            case ConflictChoice.Skip:
                Finish(item, TransferState.Skipped, "Already exists");
                return false;
            case ConflictChoice.CancelBatch:
                item.Batch.Cancelled = true;
                Finish(item, TransferState.Cancelled, null);
                return false;
            case ConflictChoice.OverwriteIfDifferent when !IsDifferent(item, existing):
                Finish(item, TransferState.Skipped, "up to date");
                return false;
            case ConflictChoice.KeepBoth:
                var directory = destination.Parent(item.DestinationPath) ?? "";
                var free = await FileOperations.FreeNameAsync(destination, directory, item.Name, token).ConfigureAwait(false);
                item.DestinationPath = destination.Combine(directory, free);
                return true;
            default:
                return true; // overwrite: the copy below replaces it
        }
    }

    /// <summary>
    /// True if <paramref name="existing"/> should be replaced: the sizes differ, or the source is
    /// newer. Times are compared with a 2-second allowance, since servers and file systems keep
    /// modification times at different precision (FAT and some servers to 2s, SFTP to 1s).
    /// </summary>
    internal static bool IsDifferent(TransferItem item, FileEntry existing)
    {
        if (existing.IsDirectory || existing.Size != item.TotalBytes)
            return true;
        if (item.SourceModified is not { } source || existing.Modified is not { } destination)
            return false; // same size and no times to compare: treat as up to date
        return source - destination > TimeSpan.FromSeconds(2);
    }

    private async Task TransferWithRetriesAsync(TransferItem item)
    {
        var token = item.Cancellation.Token;
        while (true)
        {
            item.Attempts++;
            item.State = TransferState.Running;
            item.StartedAt ??= DateTimeOffset.Now;
            try
            {
                if (item.Direction == TransferDirection.Upload)
                    await UploadOneAsync(item, token).ConfigureAwait(false);
                else
                    await DownloadOneAsync(item, token).ConfigureAwait(false);
                Finish(item, TransferState.Completed, null);
                return;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                Finish(item, TransferState.Cancelled, null);
                return;
            }
            catch (Exception ex) when (IsRetryable(ex) && item.Attempts < AutomaticAttempts)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, item.Attempts)));
                item.State = TransferState.Retrying;
                item.Error = $"{ex.Message} Retrying in {delay.TotalSeconds:0}s…";
                Log.Info(nameof(TransferQueue), $"{item.Name}: {ex.Message}; retry {item.Attempts} in {delay.TotalSeconds:0}s.");
                try
                {
                    await Task.Delay(delay, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Finish(item, TransferState.Cancelled, null);
                    return;
                }
            }
            catch (Exception ex)
            {
                Finish(item, TransferState.Failed, ex.Message);
                return;
            }
        }
    }

    private async Task UploadOneAsync(TransferItem item, CancellationToken token)
    {
        var size = new FileInfo(item.SourcePath).Length;
        item.TotalBytes = size;
        var resume = item.HasPartialData;
        var progress = new Progress(item);
        await _remote.UploadAsync(item.SourcePath, item.DestinationPath, resume, progress, token).ConfigureAwait(false);
    }

    private async Task DownloadOneAsync(TransferItem item, CancellationToken token)
    {
        long offset = 0;
        if (item.HasPartialData && File.Exists(item.DestinationPath))
        {
            var partial = new FileInfo(item.DestinationPath).Length;
            if (partial <= item.TotalBytes)
                offset = partial;
        }

        await using var source = await _remote.OpenReadAsync(item.SourcePath, offset, token).ConfigureAwait(false);
        await using var target = new FileStream(item.DestinationPath, offset > 0 ? FileMode.OpenOrCreate : FileMode.Create,
            FileAccess.Write, FileShare.None, 1 << 18, useAsync: true);
        target.SetLength(offset);
        target.Seek(offset, SeekOrigin.Begin);
        item.SetTransferred(offset);
        item.HasPartialData = true;

        var buffer = new byte[1 << 18];
        int read;
        while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            offset += read;
            item.SetTransferred(offset);
        }
        await target.FlushAsync(token).ConfigureAwait(false);
        if (item.TotalBytes > 0 && offset != item.TotalBytes)
            throw new IOException($"Download ended early ({offset:N0} of {item.TotalBytes:N0} bytes).");
    }

    private void Finish(TransferItem item, TransferState state, string? error)
    {
        lock (item)
        {
            if (item.FinishNotified)
                return;
            item.FinishNotified = true;
            if (item.State == TransferState.Cancelled)
                (state, error) = (TransferState.Cancelled, null); // the user's cancel wins over any late outcome
            item.State = state;
            item.Error = error;
            item.FinishedAt = DateTimeOffset.Now;
            if (state == TransferState.Completed)
                item.SetTransferred(item.TotalBytes);
        }
        try { ItemFinished?.Invoke(item); }
        catch (Exception ex) { Log.Error(nameof(TransferQueue), "ItemFinished handler threw.", ex); }
    }

    /// <summary>
    /// Connection-type failures are retried; anything else (permissions, missing files, disk full)
    /// is not. Remote file systems report transient failures as IOException.
    /// </summary>
    private static bool IsRetryable(Exception ex) =>
        ex switch
        {
            UnauthorizedAccessException => false,
            Renci.SshNet.Common.SftpPermissionDeniedException => false,
            Renci.SshNet.Common.SftpPathNotFoundException => false,
            Ssh.FileConnectionException => false,
            FileNotFoundException or DirectoryNotFoundException => false,
            IOException io when io.HResult == unchecked((int)0x80070070) => false, // disk full
            TimeoutException or System.Net.Http.HttpRequestException => true,
            _ => SftpFileSystem.IsConnectionLost(ex),
        };

    private sealed class Progress : IProgress<long>
    {
        private readonly TransferItem _item;
        public Progress(TransferItem item) => _item = item;

        public void Report(long value)
        {
            _item.SetTransferred(value);
            if (value > 0)
                _item.HasPartialData = true;
        }
    }
}
