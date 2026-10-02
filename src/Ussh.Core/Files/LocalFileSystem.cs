namespace Ussh.Core.Files;

/// <summary>
/// This computer's files. On Windows the top level (path "") lists the drives; elsewhere it's "/".
/// </summary>
public sealed class LocalFileSystem : IFileSystem
{
    public string Name => "This computer";
    public bool IsLocal => true;
    public bool SupportsPermissions => !OperatingSystem.IsWindows();

    public Task<string> GetStartPathAsync(CancellationToken token) =>
        Task.FromResult(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public Task<IReadOnlyList<FileEntry>> ListAsync(string directory, CancellationToken token) => Task.Run<IReadOnlyList<FileEntry>>(() =>
    {
        if (OperatingSystem.IsWindows() && directory.Length == 0)
        {
            return DriveInfo.GetDrives()
                .Where(d => d.IsReady)
                .Select(d => new FileEntry(d.Name.TrimEnd('\\'), d.RootDirectory.FullName, true, 0, null,
                    Info: string.IsNullOrEmpty(d.VolumeLabel) ? d.DriveType.ToString() : d.VolumeLabel))
                .ToList();
        }
        var info = new DirectoryInfo(directory);
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0, RecurseSubdirectories = false };
        return info.EnumerateFileSystemInfos("*", options).Select(ToEntry).ToList();
    }, token);

    public Task<FileEntry?> GetEntryAsync(string path, CancellationToken token)
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        return Task.FromResult(info.Exists ? ToEntry(info) : null);
    }

    public Task CreateDirectoryAsync(string path, CancellationToken token)
    {
        Directory.CreateDirectory(path);
        return Task.CompletedTask;
    }

    public Task DeleteFileAsync(string path, CancellationToken token)
    {
        File.Delete(path);
        return Task.CompletedTask;
    }

    public Task DeleteDirectoryAsync(string path, CancellationToken token)
    {
        Directory.Delete(path, recursive: false);
        return Task.CompletedTask;
    }

    public Task RenameAsync(string from, string to, CancellationToken token)
    {
        if (Directory.Exists(from))
            Directory.Move(from, to);
        else
            File.Move(from, to);
        return Task.CompletedTask;
    }

    public Task SetPermissionsAsync(string path, int mode, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, (UnixFileMode)mode);
        return Task.CompletedTask;
    }

    public Task<Stream> OpenReadAsync(string path, long offset, CancellationToken token)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, useAsync: true);
        stream.Seek(offset, SeekOrigin.Begin);
        return Task.FromResult<Stream>(stream);
    }

    public Task UploadAsync(string localFile, string path, bool resume, IProgress<long> progress, CancellationToken token) =>
        throw new NotSupportedException("Uploads go to a remote file system.");

    public string Combine(string directory, string name) => directory.Length == 0 ? name : Path.Combine(directory, name);

    public string? Parent(string path)
    {
        if (path.Length == 0)
            return null;
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path));
        // Windows: from "C:\" go up to the drive list.
        return parent ?? (OperatingSystem.IsWindows() ? "" : null);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static FileEntry ToEntry(FileSystemInfo info)
    {
        var isDirectory = info is DirectoryInfo;
        var isLink = info.LinkTarget != null;
        int? mode = null;
        string? permissions = null;
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                mode = (int)info.UnixFileMode;
                permissions = FileOperations.FormatMode(mode.Value, isDirectory, isLink);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return new FileEntry(
            info.Name, info.FullName, isDirectory,
            info is FileInfo file ? file.Length : 0,
            info.LastWriteTimeUtc,
            permissions, mode, null,
            IsHidden: info.Name.StartsWith('.') || info.Attributes.HasFlag(FileAttributes.Hidden),
            IsLink: isLink);
    }
}
