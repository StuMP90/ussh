namespace Ussh.Core.Files;

/// <summary>One file or folder in a listing.</summary>
public sealed record FileEntry(
    string Name,
    string Path,
    bool IsDirectory,
    long Size,
    DateTimeOffset? Modified,
    string? Permissions = null,
    int? Mode = null,
    string? Owner = null,
    bool IsHidden = false,
    bool IsLink = false,
    string? Info = null);

/// <summary>
/// A place files live: the local disk, an SFTP server or an S3 bucket. Paths are in the
/// file system's own form (local: OS paths; remote: "/"-separated). Remote implementations
/// reconnect by themselves when a connection has dropped.
/// </summary>
public interface IFileSystem : IAsyncDisposable
{
    string Name { get; }
    bool IsLocal { get; }
    bool SupportsPermissions { get; }

    /// <summary>Where browsing starts (home folder, bucket prefix…).</summary>
    Task<string> GetStartPathAsync(CancellationToken token);

    Task<IReadOnlyList<FileEntry>> ListAsync(string directory, CancellationToken token);

    /// <summary>The entry at <paramref name="path"/>, or null if nothing is there.</summary>
    Task<FileEntry?> GetEntryAsync(string path, CancellationToken token);

    Task CreateDirectoryAsync(string path, CancellationToken token);

    Task DeleteFileAsync(string path, CancellationToken token);

    /// <summary>Deletes an empty folder (see <see cref="FileOperations.DeleteTreeAsync"/> for whole trees).</summary>
    Task DeleteDirectoryAsync(string path, CancellationToken token);

    Task RenameAsync(string from, string to, CancellationToken token);

    Task SetPermissionsAsync(string path, int mode, CancellationToken token);

    /// <summary>Reads a file from <paramref name="offset"/> (for resumed downloads).</summary>
    Task<Stream> OpenReadAsync(string path, long offset, CancellationToken token);

    /// <summary>
    /// Uploads a local file to <paramref name="path"/>. With <paramref name="resume"/>, continues a
    /// partial upload of the same file where it stopped; otherwise starts afresh.
    /// <paramref name="progress"/> reports the total bytes now at the destination.
    /// </summary>
    Task UploadAsync(string localFile, string path, bool resume, IProgress<long> progress, CancellationToken token);

    string Combine(string directory, string name);

    /// <summary>The parent folder, or null at the top.</summary>
    string? Parent(string path);

    /// <summary>
    /// Why <paramref name="path"/> can't be created, written, renamed or deleted, or null if it
    /// can. E.g. S3 buckets themselves are read-only in zSSH (only their contents can change).
    /// </summary>
    string? ReadOnlyReason(string path) => null;
}

/// <summary>Helpers that work the same on every file system.</summary>
public static class FileOperations
{
    public sealed record TreeSize(int Files, int Folders, long Bytes);

    /// <summary>Counts what deleting or transferring <paramref name="entries"/> involves.</summary>
    public static async Task<TreeSize> MeasureAsync(IFileSystem fs, IEnumerable<FileEntry> entries, CancellationToken token)
    {
        int files = 0, folders = 0;
        long bytes = 0;
        foreach (var entry in entries)
        {
            await foreach (var item in WalkAsync(fs, entry, token).ConfigureAwait(false))
            {
                if (item.IsDirectory)
                    folders++;
                else
                {
                    files++;
                    bytes += item.Size;
                }
            }
        }
        return new TreeSize(files, folders, bytes);
    }

    /// <summary>The entry and, for folders, everything inside it (parents before children).</summary>
    public static async IAsyncEnumerable<FileEntry> WalkAsync(IFileSystem fs, FileEntry entry,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        yield return entry;
        if (!entry.IsDirectory || entry.IsLink)
            yield break;
        foreach (var child in await fs.ListAsync(entry.Path, token).ConfigureAwait(false))
        {
            await foreach (var item in WalkAsync(fs, child, token).ConfigureAwait(false))
                yield return item;
        }
    }

    /// <summary>Deletes a file, or a folder and everything in it (links are removed, not followed).</summary>
    /// <exception cref="UnauthorizedAccessException">The entry is read-only (checked before anything is deleted).</exception>
    public static async Task DeleteTreeAsync(IFileSystem fs, FileEntry entry, CancellationToken token)
    {
        // Checked up front: deleting a folder removes its contents first, so a refusal at the
        // end (e.g. "can't delete a bucket") would come too late.
        if (fs.ReadOnlyReason(entry.Path) is { } reason)
            throw new UnauthorizedAccessException(reason);
        await DeleteTreeCoreAsync(fs, entry, token).ConfigureAwait(false);
    }

    private static async Task DeleteTreeCoreAsync(IFileSystem fs, FileEntry entry, CancellationToken token)
    {
        if (!entry.IsDirectory || entry.IsLink)
        {
            await fs.DeleteFileAsync(entry.Path, token).ConfigureAwait(false);
            return;
        }
        foreach (var child in await fs.ListAsync(entry.Path, token).ConfigureAwait(false))
            await DeleteTreeCoreAsync(fs, child, token).ConfigureAwait(false);
        await fs.DeleteDirectoryAsync(entry.Path, token).ConfigureAwait(false);
    }

    /// <summary>"report.pdf" → "report (1).pdf" (then 2, 3…) until the name is free.</summary>
    public static async Task<string> FreeNameAsync(IFileSystem fs, string directory, string name, CancellationToken token)
    {
        var stem = System.IO.Path.GetFileNameWithoutExtension(name);
        var extension = System.IO.Path.GetExtension(name);
        for (var i = 1; i < 10_000; i++)
        {
            var candidate = $"{stem} ({i}){extension}";
            if (await fs.GetEntryAsync(fs.Combine(directory, candidate), token).ConfigureAwait(false) == null)
                return candidate;
        }
        throw new IOException("No free name found.");
    }

    /// <summary>"rwxr-xr-x" style text for a Unix mode.</summary>
    public static string FormatMode(int mode, bool isDirectory, bool isLink = false)
    {
        var chars = new char[10];
        chars[0] = isLink ? 'l' : isDirectory ? 'd' : '-';
        const string rwx = "rwxrwxrwx";
        for (var i = 0; i < 9; i++)
            chars[i + 1] = (mode & (1 << (8 - i))) != 0 ? rwx[i] : '-';
        return new string(chars);
    }

    /// <summary>"/a/b/" + "c" → "/a/b/c" for "/"-separated remote paths.</summary>
    public static string CombineRemote(string directory, string name) =>
        directory.EndsWith('/') ? directory + name : directory + "/" + name;

    /// <summary>Parent of a "/"-separated path; null for "/".</summary>
    public static string? ParentRemote(string path)
    {
        var trimmed = path.TrimEnd('/');
        if (trimmed.Length == 0)
            return null;
        var slash = trimmed.LastIndexOf('/');
        return slash <= 0 ? "/" : trimmed[..slash];
    }
}
