using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ussh.App.Services;
using Ussh.Core.Diagnostics;
using Ussh.Core.Files;
using Ussh.Core.Ssh;

namespace Ussh.App.ViewModels;

/// <summary>One file in a pane's list.</summary>
public sealed class FileRowViewModel
{
    public FileRowViewModel(FileEntry entry) => Entry = entry;

    public FileEntry Entry { get; }
    public string Name => Entry.Name;
    public string Icon => Entry.IsDirectory ? "📁" : Entry.IsLink ? "🔗" : "📄";
    public string SizeText => Entry.IsDirectory ? "" : FormatSize(Entry.Size);
    public long SizeSort => Entry.IsDirectory ? -1 : Entry.Size;
    public string ModifiedText => Entry.Modified?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "";
    public DateTimeOffset ModifiedSort => Entry.Modified ?? DateTimeOffset.MinValue;
    public string Permissions => Entry.Permissions ?? "";
    public string Owner => Entry.Owner ?? Entry.Info ?? "";
    /// <summary>Folders first, then by name: the default order.</summary>
    public string NameSort => (Entry.IsDirectory ? "0" : "1") + Entry.Name;

    public static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }
}

/// <summary>One side of the file browser: a folder listing on one file system.</summary>
public sealed partial class FilePaneViewModel : ObservableObject
{
    private readonly DialogService _dialogs;
    private List<FileEntry> _all = new();
    private IReadOnlyList<FileRowViewModel> _selected = Array.Empty<FileRowViewModel>();

    public FilePaneViewModel(IFileSystem fileSystem, DialogService dialogs)
    {
        FileSystem = fileSystem;
        _dialogs = dialogs;
    }

    public IFileSystem FileSystem { get; }
    public string Title => FileSystem.Name;
    public bool IsRemote => !FileSystem.IsLocal;
    public bool SupportsPermissions => FileSystem.SupportsPermissions;
    public ObservableCollection<FileRowViewModel> Rows { get; } = new();

    [ObservableProperty] private string _currentPath = "";
    [ObservableProperty] private string _pathText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpCommand), nameof(RefreshCommand), nameof(NewFolderCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    [ObservableProperty] private bool _showHidden;
    [ObservableProperty] private string _status = "";

    public bool HasError => Error != null;

    /// <summary>Set by the view from the grid's selection.</summary>
    public IReadOnlyList<FileRowViewModel> Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            UpdateStatus();
            RenameCommand.NotifyCanExecuteChanged();
            DeleteCommand.NotifyCanExecuteChanged();
            PermissionsCommand.NotifyCanExecuteChanged();
        }
    }

    public IReadOnlyList<FileEntry> SelectedEntries => Selected.Select(r => r.Entry).ToList();

    /// <summary>Raised after a successful listing (e.g. so the tab can show the remote as connected).</summary>
    public event Action? Listed;

    public async Task InitializeAsync()
    {
        await RunAsync(async () =>
        {
            var start = await FileSystem.GetStartPathAsync(default);
            await LoadAsync(start);
        });
    }

    public Task NavigateAsync(string path) => RunAsync(() => LoadAsync(path));

    partial void OnShowHiddenChanged(bool value) => ApplyFilter();

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private Task UpAsync()
    {
        var parent = FileSystem.Parent(CurrentPath);
        return parent == null ? Task.CompletedTask : NavigateAsync(parent);
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    public Task RefreshAsync() => CurrentPath.Length == 0 && !FileSystem.IsLocal ? InitializeAsync() : NavigateAsync(CurrentPath);

    [RelayCommand]
    private Task GoToPathAsync() => NavigateAsync(PathText.Trim());

    /// <summary>Double-click / Enter on a folder.</summary>
    public Task OpenAsync(FileRowViewModel row) => row.Entry.IsDirectory ? NavigateAsync(row.Entry.Path) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task NewFolderAsync()
    {
        if (FileSystem.ReadOnlyReason(FileSystem.Combine(CurrentPath, "new")) is { } reason)
        {
            await _dialogs.AlertAsync("New folder", reason);
            return;
        }
        var name = await _dialogs.PromptTextAsync("New folder", $"New folder in {CurrentPath}:", "");
        if (string.IsNullOrWhiteSpace(name))
            return;
        await RunAsync(async () =>
        {
            await FileSystem.CreateDirectoryAsync(FileSystem.Combine(CurrentPath, name.Trim()), default);
            await LoadAsync(CurrentPath);
        });
    }

    [RelayCommand(CanExecute = nameof(HasSingleSelection))]
    private async Task RenameAsync()
    {
        var entry = Selected[0].Entry;
        if (FileSystem.ReadOnlyReason(entry.Path) is { } reason)
        {
            await _dialogs.AlertAsync("Rename", reason);
            return;
        }
        var name = await _dialogs.PromptTextAsync("Rename", $"New name for {entry.Name}:", entry.Name);
        if (string.IsNullOrWhiteSpace(name) || name == entry.Name)
            return;
        await RunAsync(async () =>
        {
            await FileSystem.RenameAsync(entry.Path, FileSystem.Combine(CurrentPath, name.Trim()), default);
            await LoadAsync(CurrentPath);
        });
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteAsync()
    {
        var entries = SelectedEntries;
        // Before counting or confirming: e.g. S3 buckets themselves can never be deleted here.
        if (entries.Select(e => FileSystem.ReadOnlyReason(e.Path)).FirstOrDefault(r => r != null) is { } reason)
        {
            await _dialogs.AlertAsync("Delete", reason);
            return;
        }
        FileOperations.TreeSize? size = null;
        await RunAsync(async () => size = await FileOperations.MeasureAsync(FileSystem, entries, default));
        if (size == null)
            return;
        var what = entries.Count == 1 ? $"\"{entries[0].Name}\"" : $"{entries.Count} items";
        var detail = size.Folders > 0
            ? $"This deletes {size.Files} file(s) in {size.Folders} folder(s), {FileRowViewModel.FormatSize(size.Bytes)} in total."
            : $"{size.Files} file(s), {FileRowViewModel.FormatSize(size.Bytes)}.";
        var where = FileSystem.IsLocal ? "from this computer" : $"from {FileSystem.Name}";
        if (!await _dialogs.ConfirmAsync("Delete", $"Delete {what} {where}?\n\n{detail}\n\nThis can't be undone.", "Delete", danger: true))
            return;
        await RunAsync(async () =>
        {
            foreach (var entry in entries)
                await FileOperations.DeleteTreeAsync(FileSystem, entry, default);
            await LoadAsync(CurrentPath);
        });
    }

    [RelayCommand(CanExecute = nameof(CanChangePermissions))]
    private async Task PermissionsAsync()
    {
        var entry = Selected[0].Entry;
        var current = entry.Mode is { } mode ? Convert.ToString(mode & 0x1FF, 8) : "644";
        var answer = await _dialogs.PromptTextAsync("Permissions",
            $"Permissions for {entry.Name} as an octal number (e.g. 644 or 755):", current);
        if (string.IsNullOrWhiteSpace(answer))
            return;
        int value;
        try
        {
            value = Convert.ToInt32(answer.Trim(), 8);
        }
        catch (FormatException)
        {
            await _dialogs.AlertAsync("Permissions", $"\"{answer}\" isn't an octal number like 644.");
            return;
        }
        if (value is < 0 or > 0x1FF)
        {
            await _dialogs.AlertAsync("Permissions", "Permissions must be between 000 and 777.");
            return;
        }
        await RunAsync(async () =>
        {
            await FileSystem.SetPermissionsAsync(entry.Path, value, default);
            await LoadAsync(CurrentPath);
        });
    }

    private bool NotBusy() => !IsBusy;
    private bool HasSelection() => Selected.Count > 0;
    private bool HasSingleSelection() => Selected.Count == 1;
    private bool CanChangePermissions() => Selected.Count == 1 && SupportsPermissions;

    private async Task LoadAsync(string path)
    {
        var entries = await FileSystem.ListAsync(path, default);
        _all = entries.ToList();
        CurrentPath = path;
        PathText = path;
        Error = null;
        ApplyFilter();
        Listed?.Invoke();
    }

    private void ApplyFilter()
    {
        Rows.Clear();
        foreach (var entry in _all
                     .Where(e => ShowHidden || !e.IsHidden)
                     .OrderBy(e => !e.IsDirectory)
                     .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            Rows.Add(new FileRowViewModel(entry));
        Selected = Array.Empty<FileRowViewModel>();
    }

    private void UpdateStatus()
    {
        var folders = Rows.Count(r => r.Entry.IsDirectory);
        var files = Rows.Count - folders;
        Status = Selected.Count > 0
            ? $"{Selected.Count} selected ({FileRowViewModel.FormatSize(Selected.Sum(r => r.Entry.IsDirectory ? 0 : r.Entry.Size))})"
            : $"{folders} folder(s), {files} file(s)";
    }

    private async Task RunAsync(Func<Task> action)
    {
        IsBusy = true;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Error = ex is FileConnectionException ? ex.Message : SshConnector.Describe(ex);
            Log.Warn("files", $"{FileSystem.Name}: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }
}
