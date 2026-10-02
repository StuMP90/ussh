using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Ussh.App.ViewModels;
using Ussh.Core.Files;

namespace Ussh.App.Views;

/// <summary>
/// The dual-pane file browser. Transfers start from the arrow buttons, double-click/Enter on
/// files, dragging rows to the other pane, or dropping files from the system file manager onto
/// the remote pane.
/// </summary>
public partial class FilesView : UserControl
{
    public static readonly IValueConverter StatusBrush = new FuncValueConverter<bool, IBrush>(error =>
        error ? Brushes.IndianRed : new SolidColorBrush(Color.Parse("#C0FFFFFF")));

    private static readonly DataFormat<string> DragFormat = DataFormat.CreateStringApplicationFormat("zssh-file-entries");
    // Dragged rows, in-process (both panes live in this window).
    private static (FilePaneViewModel Source, IReadOnlyList<FileEntry> Entries)? _dragged;

    public FilesView()
    {
        InitializeComponent();
        foreach (var pane in new[] { LocalPane, RemotePane })
        {
            pane.FilesActivated += OnFilesActivated;
            pane.DragStarted += OnDragStarted;
            pane.FileGrid.AddHandler(DragDrop.DragOverEvent, OnDragOver);
            pane.FileGrid.AddHandler(DragDrop.DropEvent, OnDrop);
        }
    }

    private FilesTabViewModel? Tab => DataContext as FilesTabViewModel;

    private void OnFilesActivated(FilePaneViewModel pane, IReadOnlyList<FileRowViewModel> rows)
    {
        var entries = rows.Select(r => r.Entry).ToList();
        if (Tab == null)
            return;
        // Like FileZilla: activating a file sends it to the other side.
        _ = pane == Tab.Local ? Tab.UploadEntriesAsync(entries) : Tab.DownloadEntriesAsync(entries);
    }

    private async void OnDragStarted(FilePaneView view, PointerEventArgs e)
    {
        if (view.DataContext is not FilePaneViewModel pane || pane.Selected.Count == 0)
            return;
        _dragged = (pane, pane.SelectedEntries);
        try
        {
            var item = new DataTransferItem();
            item.Set(DragFormat, "zssh");
            var data = new DataTransfer();
            data.Add(item);
            await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Copy);
        }
        finally
        {
            _dragged = null;
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = TargetFor(sender, e) == null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var tab = Tab;
        var target = TargetFor(sender, e);
        e.Handled = true;
        if (tab == null || target == null)
            return;
        if (e.DataTransfer.Contains(DragFormat) && _dragged is { } dragged)
        {
            _ = target == tab.Remote ? tab.UploadEntriesAsync(dragged.Entries) : tab.DownloadEntriesAsync(dragged.Entries);
        }
        else if (target == tab.Remote && e.DataTransfer.TryGetFiles() is { } files)
        {
            var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
            _ = tab.UploadPathsAsync(paths);
        }
    }

    /// <summary>The pane a drop would land in, or null if it isn't allowed there.</summary>
    private FilePaneViewModel? TargetFor(object? sender, DragEventArgs e)
    {
        var tab = Tab;
        var target = (sender as Control)?.DataContext as FilePaneViewModel;
        if (tab == null || target == null)
            return null;
        if (e.DataTransfer.Contains(DragFormat))
            return _dragged is { } dragged && dragged.Source != target ? target : null;
        // From the system file manager: only onto the remote side (uploads).
        return target == tab.Remote && e.DataTransfer.Contains(DataFormat.File) ? target : null;
    }
}
