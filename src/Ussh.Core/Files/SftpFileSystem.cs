using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;
using Ussh.Core.Diagnostics;
using Ussh.Core.Ssh;

namespace Ussh.Core.Files;

/// <summary>
/// Files on an SSH/SFTP server. Opens its own connection (never shared with terminals) and
/// reconnects once and retries when an operation fails because the connection dropped.
/// </summary>
public sealed class SftpFileSystem : IFileSystem
{
    private const int CopyBuffer = 1 << 18;
    private readonly Func<CancellationToken, Task<SftpConnection>> _connect;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private SftpConnection? _connection;

    /// <param name="connect">Opens a fresh connection (see <see cref="SshConnector.ConnectSftpAsync"/>).</param>
    public SftpFileSystem(string name, Func<CancellationToken, Task<SftpConnection>> connect)
    {
        Name = name;
        _connect = connect;
    }

    public string Name { get; }
    public bool IsLocal => false;
    public bool SupportsPermissions => true;

    public Task<string> GetStartPathAsync(CancellationToken token) =>
        RunAsync(client => client.WorkingDirectory is { Length: > 0 } dir ? dir : "/", token);

    public Task<IReadOnlyList<FileEntry>> ListAsync(string directory, CancellationToken token) =>
        RunAsync<IReadOnlyList<FileEntry>>(client => client.ListDirectory(directory)
            .Where(f => f.Name is not ("." or ".."))
            .Select(f => ToEntry(client, f))
            .ToList(), token);

    public Task<FileEntry?> GetEntryAsync(string path, CancellationToken token) =>
        RunAsync<FileEntry?>(client =>
        {
            try
            {
                return ToEntry(client, client.Get(path));
            }
            catch (SftpPathNotFoundException)
            {
                return null;
            }
        }, token);

    public Task CreateDirectoryAsync(string path, CancellationToken token) =>
        RunAsync(client => { client.CreateDirectory(path); return true; }, token);

    public Task DeleteFileAsync(string path, CancellationToken token) =>
        RunAsync(client => { client.DeleteFile(path); return true; }, token);

    public Task DeleteDirectoryAsync(string path, CancellationToken token) =>
        RunAsync(client => { client.DeleteDirectory(path); return true; }, token);

    public Task RenameAsync(string from, string to, CancellationToken token) =>
        RunAsync(client => { client.RenameFile(from, to); return true; }, token);

    public Task SetPermissionsAsync(string path, int mode, CancellationToken token) =>
        // SSH.NET takes the mode as octal digits written in decimal (0o755 → 755).
        RunAsync(client => { client.ChangePermissions(path, short.Parse(Convert.ToString(mode & 0x1FF, 8))); return true; }, token);

    public Task<Stream> OpenReadAsync(string path, long offset, CancellationToken token) =>
        RunAsync<Stream>(client =>
        {
            var stream = client.OpenRead(path);
            if (offset > 0)
                stream.Seek(offset, SeekOrigin.Begin);
            return stream;
        }, token);

    public async Task UploadAsync(string localFile, string path, bool resume, IProgress<long> progress, CancellationToken token)
    {
        var client = await GetClientAsync(token).ConfigureAwait(false);
        await using var source = new FileStream(localFile, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBuffer, useAsync: true);
        long offset = 0;
        if (resume)
        {
            try
            {
                var existing = await Task.Run(() => client.GetAttributes(path), token).ConfigureAwait(false);
                if (!existing.IsDirectory && existing.Size <= source.Length)
                    offset = existing.Size;
            }
            catch (SftpPathNotFoundException) { }
        }

        await using var target = await Task.Run(
            () => client.Open(path, offset > 0 ? FileMode.Open : FileMode.Create, FileAccess.Write), token).ConfigureAwait(false);
        if (offset > 0)
        {
            target.Seek(offset, SeekOrigin.Begin);
            source.Seek(offset, SeekOrigin.Begin);
        }
        progress.Report(offset);
        var buffer = new byte[CopyBuffer];
        int read;
        while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            offset += read;
            progress.Report(offset);
        }
        await target.FlushAsync(token).ConfigureAwait(false);
    }

    public string Combine(string directory, string name) => FileOperations.CombineRemote(directory, name);

    public string? Parent(string path) => FileOperations.ParentRemote(path);

    public async ValueTask DisposeAsync()
    {
        await _connectGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _connection?.Dispose();
            _connection = null;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    /// <summary>Runs an operation, reconnecting and retrying once if the connection had dropped.</summary>
    private async Task<T> RunAsync<T>(Func<SftpClient, T> operation, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            var client = await GetClientAsync(token).ConfigureAwait(false);
            try
            {
                return await Task.Run(() => operation(client), token).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt == 0 && IsConnectionLost(ex, client))
            {
                Log.Info(nameof(SftpFileSystem), $"{Name}: connection lost ({ex.Message}); reconnecting.");
                await DropConnectionAsync(client).ConfigureAwait(false);
            }
        }
    }

    private async Task<SftpClient> GetClientAsync(CancellationToken token)
    {
        await _connectGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_connection is { IsConnected: true })
                return _connection.Client;
            _connection?.Dispose();
            _connection = null;
            _connection = await _connect(token).ConfigureAwait(false);
            return _connection.Client;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private async Task DropConnectionAsync(SftpClient client)
    {
        await _connectGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_connection?.Client == client)
            {
                _connection.Dispose();
                _connection = null;
            }
        }
        finally
        {
            _connectGate.Release();
        }
    }

    /// <summary>True when a failure means the transport is gone (as opposed to e.g. permission denied).</summary>
    public static bool IsConnectionLost(Exception ex, SftpClient? client = null) =>
        ex is SshConnectionException or System.Net.Sockets.SocketException or ObjectDisposedException
            or SshOperationTimeoutException
        || (ex is IOException && ex is not SftpPathNotFoundException && ex is not SftpPermissionDeniedException)
        || (client != null && !client.IsConnected && ex is SshException);

    private static FileEntry ToEntry(SftpClient client, ISftpFile file)
    {
        var isDirectory = file.IsDirectory;
        var isLink = file.IsSymbolicLink;
        if (isLink)
        {
            // Show links to folders as folders (browsing into them works), but never recurse into them.
            try { isDirectory = client.GetAttributes(file.FullName).IsDirectory; } catch (SshException) { }
        }
        var mode = ModeOf(file);
        return new FileEntry(
            file.Name, file.FullName, isDirectory,
            isDirectory ? 0 : file.Length,
            new DateTimeOffset(DateTime.SpecifyKind(file.LastWriteTimeUtc, DateTimeKind.Utc)),
            FileOperations.FormatMode(mode, isDirectory && !isLink, isLink), mode,
            file.UserId.ToString(),
            IsHidden: file.Name.StartsWith('.'),
            IsLink: isLink);
    }

    private static int ModeOf(ISftpFile f)
    {
        var mode = 0;
        if (f.OwnerCanRead) mode |= 0x100;
        if (f.OwnerCanWrite) mode |= 0x80;
        if (f.OwnerCanExecute) mode |= 0x40;
        if (f.GroupCanRead) mode |= 0x20;
        if (f.GroupCanWrite) mode |= 0x10;
        if (f.GroupCanExecute) mode |= 0x8;
        if (f.OthersCanRead) mode |= 0x4;
        if (f.OthersCanWrite) mode |= 0x2;
        if (f.OthersCanExecute) mode |= 0x1;
        return mode;
    }
}
