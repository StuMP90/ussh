using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Ussh.App.ViewModels;

namespace Ussh.App.Views;

public partial class FilePaneView : UserControl
{
    public FilePaneView()
    {
        InitializeComponent();
        // Start a drag when a selected row is dragged (rows are moved between panes to transfer).
        Grid.AddHandler(PointerPressedEvent, OnGridPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        Grid.AddHandler(PointerMovedEvent, OnGridPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private FilePaneViewModel? Pane => DataContext as FilePaneViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        // Object storage (S3) has no Unix permissions or owner; show the storage class instead.
        var objectStore = Pane is { IsRemote: true, SupportsPermissions: false };
        var (permissions, owner) = (Grid.Columns[3], Grid.Columns[4]);
        permissions.IsVisible = !objectStore;
        owner.Header = objectStore ? "Storage class" : "Owner";
        owner.Width = new DataGridLength(objectStore ? 120 : 80);
    }

    /// <summary>Raised for a file (not folder) double-clicked or Enter'd: the tab transfers it.</summary>
    public event Action<FilePaneViewModel, IReadOnlyList<FileRowViewModel>>? FilesActivated;

    /// <summary>Raised when selected rows are dragged out of the grid.</summary>
    public event Action<FilePaneView, PointerEventArgs>? DragStarted;

    public DataGrid FileGrid => Grid;

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Pane != null)
            Pane.Selected = Grid.SelectedItems.OfType<FileRowViewModel>().ToList();
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Pane == null || (e.Source as Control)?.DataContext is not FileRowViewModel row)
            return;
        Activate(row);
    }

    private void Activate(FileRowViewModel row)
    {
        if (row.Entry.IsDirectory)
            _ = Pane!.OpenAsync(row);
        else
            FilesActivated?.Invoke(Pane!, new[] { row });
    }

    private void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        var pane = Pane;
        if (pane == null)
            return;
        switch (e.Key)
        {
            case Key.Enter when pane.Selected.Count == 1:
                Activate(pane.Selected[0]);
                break;
            case Key.Enter when pane.Selected.Count > 1:
                FilesActivated?.Invoke(pane, pane.Selected);
                break;
            case Key.Back:
                pane.UpCommand.Execute(null);
                break;
            case Key.F5:
                pane.RefreshCommand.Execute(null);
                break;
            case Key.F2:
                pane.RenameCommand.Execute(null);
                break;
            case Key.Delete:
                pane.DeleteCommand.Execute(null);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void OnPathKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Pane != null)
        {
            Pane.GoToPathCommand.Execute(null);
            e.Handled = true;
        }
    }

    // -------------------------------------------------------------- drag source

    private Avalonia.Point? _pressedAt;

    private void OnGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var onRow = (e.Source as Control)?.DataContext is FileRowViewModel;
        _pressedAt = onRow && e.GetCurrentPoint(Grid).Properties.IsLeftButtonPressed ? e.GetPosition(Grid) : null;
    }

    private void OnGridPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressedAt is not { } start || !e.GetCurrentPoint(Grid).Properties.IsLeftButtonPressed)
            return;
        var delta = e.GetPosition(Grid) - start;
        if (Math.Abs(delta.X) + Math.Abs(delta.Y) < 8 || Pane?.Selected.Count is null or 0)
            return;
        _pressedAt = null;
        DragStarted?.Invoke(this, e);
    }
}
