using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ussh.App.Services;
using Ussh.Core.Files;
using Ussh.Core.Models;

namespace Ussh.App.ViewModels;

/// <summary>
/// A dual-pane file browser tab: this computer on the left, the server or bucket on the right,
/// and the transfer queue underneath. Browsing and transfers use separate connections.
/// </summary>
public sealed partial class FilesTabViewModel : TabViewModel
{
    private static readonly IBrush ConnectedBrush = new SolidColorBrush(Color.Parse("#40C48C"));
    private static readonly IBrush PendingBrush = new SolidColorBrush(Color.Parse("#E0B040"));
    private static readonly IBrush DownBrush = new SolidColorBrush(Color.Parse("#E05555"));

    private readonly MainWindowViewModel _main;
    private readonly DialogService _dialogs;
    private readonly DispatcherTimer _timer;
    private int _finishedSinceRefresh;

    public FilesTabViewModel(MainWindowViewModel main, ServerProfile profile, IFileSystem browse, IFileSystem transfers, DialogService dialogs)
    {
        _main = main;
        _dialogs = dialogs;
        Profile = profile;
        Title = profile.DisplayName + " · files";
        Indicator = PendingBrush;
        Local = new FilePaneViewModel(new LocalFileSystem(), dialogs);
        Remote = new FilePaneViewModel(browse, dialogs);
        Remote.Listed += () => Indicator = ConnectedBrush;
        Remote.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FilePaneViewModel.Error) && Remote.Error != null)
                Indicator = DownBrush;
        };
        Queue = new TransferQueue(Local.FileSystem, transfers, AskAboutConflictAsync);
        Queue.ItemFinished += _ => Interlocked.Increment(ref _finishedSinceRefresh);
        CloseTabCommand = new AsyncRelayCommand(() => _main.CloseFilesTabAsync(this));
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) => Tick());
        _timer.Start();
    }

    public ServerProfile Profile { get; }
    public FilePaneViewModel Local { get; }
    public FilePaneViewModel Remote { get; }
    public TransferQueue Queue { get; }
    public ObservableCollection<TransferRowViewModel> Transfers { get; } = new();
    public override bool CanClose => true;
    public override IAsyncRelayCommand CloseTabCommand { get; }

    [ObservableProperty] private int _activeTransfers;
    [ObservableProperty] private string _transferSummary = "No transfers";

    public async Task InitializeAsync()
    {
        await Task.WhenAll(Local.InitializeAsync(), Remote.InitializeAsync());
    }

    [RelayCommand]
    private Task UploadAsync() => UploadEntriesAsync(Local.SelectedEntries);

    [RelayCommand]
    private Task DownloadAsync() => DownloadEntriesAsync(Remote.SelectedEntries);

    /// <summary>Local files/folders into the remote pane's current folder.</summary>
    public async Task UploadEntriesAsync(IReadOnlyList<FileEntry> entries)
    {
        if (entries.Count == 0 || Remote.HasError)
            return;
        if (Remote.FileSystem.ReadOnlyReason(Remote.FileSystem.Combine(Remote.CurrentPath, entries[0].Name)) is { } reason)
        {
            await _dialogs.AlertAsync("Upload", reason);
            return;
        }
        await QueueAsync(() => Queue.UploadAsync(entries, Remote.CurrentPath));
    }

    /// <summary>Remote files/folders into the local pane's current folder.</summary>
    public async Task DownloadEntriesAsync(IReadOnlyList<FileEntry> entries)
    {
        if (entries.Count == 0)
            return;
        await QueueAsync(() => Queue.DownloadAsync(entries, Local.CurrentPath));
    }

    /// <summary>Files dropped from the system file manager onto the remote pane.</summary>
    public async Task UploadPathsAsync(IEnumerable<string> paths)
    {
        var entries = new List<FileEntry>();
        foreach (var path in paths)
        {
            if (await Local.FileSystem.GetEntryAsync(path, default) is { } entry)
                entries.Add(entry);
        }
        await UploadEntriesAsync(entries);
    }

    [RelayCommand]
    private void ClearFinished()
    {
        Queue.ClearFinished();
        Tick();
    }

    [RelayCommand]
    private void CancelAll() => Queue.CancelAll();

    [RelayCommand]
    private void CancelTransfer(TransferRowViewModel row) => Queue.Cancel(row.Item);

    [RelayCommand]
    private void RetryTransfer(TransferRowViewModel row) => Queue.Retry(row.Item);

    public async Task ShutdownAsync()
    {
        _timer.Stop();
        await Queue.DisposeAsync();
        await Remote.FileSystem.DisposeAsync();
    }

    private async Task QueueAsync(Func<Task> enqueue)
    {
        try
        {
            await enqueue();
        }
        catch (Exception ex)
        {
            await _dialogs.AlertAsync("Transfer", "Couldn't queue the transfer: " + ex.Message);
        }
        Tick();
    }

    private async Task<ConflictDecision> AskAboutConflictAsync(TransferConflict conflict)
    {
        var item = conflict.Item;
        var existing = conflict.Existing;
        var where = item.Direction == TransferDirection.Upload ? Profile.DisplayName : "this computer";
        var details = existing.IsDirectory
            ? "A folder with that name exists."
            : $"Existing: {FileRowViewModel.FormatSize(existing.Size)}, modified {existing.Modified?.ToLocalTime():yyyy-MM-dd HH:mm}.\n" +
              $"New: {FileRowViewModel.FormatSize(item.TotalBytes)}.";
        var (choice, applyToAll) = await _dialogs.ChooseAsync("File already exists",
            $"\"{item.Name}\" already exists on {where}:\n{item.DestinationPath}\n\n{details}",
            new[] { "Overwrite", "Keep both", "Skip", "Cancel transfer" },
            "Do the same for the rest of this transfer");
        var decision = choice switch
        {
            0 => ConflictChoice.Overwrite,
            1 => ConflictChoice.KeepBoth,
            2 => ConflictChoice.Skip,
            _ => ConflictChoice.CancelBatch,
        };
        return new ConflictDecision(decision, applyToAll);
    }

    /// <summary>Syncs the queue into rows, and refreshes listings when files have landed.</summary>
    private void Tick()
    {
        var items = Queue.Items;
        var known = Transfers.ToDictionary(r => r.Item);
        for (var i = Transfers.Count - 1; i >= 0; i--)
        {
            if (!items.Contains(Transfers[i].Item))
                Transfers.RemoveAt(i);
        }
        foreach (var item in items)
        {
            if (known.TryGetValue(item, out var row))
                row.Refresh();
            else
                Transfers.Add(new TransferRowViewModel(item));
        }

        var active = items.Count(i => i.IsActive);
        var failed = items.Count(i => i.State == TransferState.Failed);
        if (active != ActiveTransfers)
        {
            ActiveTransfers = active;
            _main.OnTransfersChanged();
        }
        TransferSummary = items.Count == 0 ? "No transfers"
            : $"{active} active · {items.Count(i => i.State == TransferState.Completed)} done" + (failed > 0 ? $" · {failed} failed" : "");

        // Show newly arrived files: refresh both sides once things have landed (cheap, and only when idle-ish).
        if (Interlocked.Exchange(ref _finishedSinceRefresh, 0) > 0)
        {
            if (!Local.IsBusy)
                _ = Local.RefreshAsync();
            if (!Remote.IsBusy && !Remote.HasError)
                _ = Remote.RefreshAsync();
        }
    }
}
