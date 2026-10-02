using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Ussh.App.Views;

/// <summary>Keyboard shortcuts and how things work (Help button / Ctrl+Shift+H).</summary>
public partial class HelpWindow : Window
{
    // (keys, what it does); a null key is a plain note line.
    private static readonly (string Title, (string? Keys, string Text)[] Rows)[] Sections =
    {
        ("Tabs", new (string?, string)[]
        {
            ("Ctrl+Tab  /  Ctrl+PageDown", "Next tab"),
            ("Ctrl+Shift+Tab  /  Ctrl+PageUp", "Previous tab"),
            ("Alt+1 … Alt+8  /  Alt+9", "Go to tab 1–8 / the last tab"),
            ("Ctrl+click tab headers", "Mark tabs, then press \"Combine N tabs into a split\" at the top"),
            ("Right-click a tab", "Combine with another tab, separate panes into tabs, close"),
            (null, "Closing a tab goes back to the tab you used before it. Combining or separating tabs never reconnects anything."),
        }),
        ("Split panes", new (string?, string)[]
        {
            ("Ctrl+Shift+E", "Split the current pane right (same server)"),
            ("Ctrl+Shift+O", "Split the current pane down (same server)"),
            ("Alt+Arrow keys", "Move to the pane in that direction (with several panes)"),
            ("Ctrl+Shift+W", "Close the current pane (the tab, if it's the last pane)"),
            ("Ctrl+Shift+B", "Broadcast typing to every pane in the tab (amber borders while on)"),
            ("☰ / right-click in a pane", "Split with another server, move a pane to its own tab, browse files"),
        }),
        ("Terminal", new (string?, string)[]
        {
            ("Ctrl+Shift+C  /  Ctrl+Insert", "Copy the selection"),
            ("Ctrl+Shift+V  /  Shift+Insert", "Paste (middle-click pastes too)"),
            ("Double / triple click", "Select a word / a line"),
            ("Shift+drag", "Select text even when the program uses the mouse (vim, htop…)"),
            ("Shift+PageUp / PageDown", "Scroll back / forward through the output"),
            ("Shift+Home / End", "Top / bottom of the scrollback"),
            (null, "A dropped connection reconnects automatically with a fresh shell. To keep programs running across drops, use tmux or screen on the server. What happens when you type exit is set in Settings (or per server)."),
        }),
        ("File browser", new (string?, string)[]
        {
            ("→  /  ←", "Upload the selected local items / download the selected remote items"),
            ("Double-click / Enter", "Open a folder, or send a file to the other side"),
            ("Drag rows to the other pane", "Transfer them; files dropped from your file manager onto the right-hand side are uploaded"),
            ("Backspace", "Up one folder"),
            ("F5  /  F2  /  Delete", "Refresh / rename / delete"),
            (null, "When a file already exists you can Overwrite, \"Overwrite if different\" (only if the size differs or the copy being sent is newer), Keep both, or Skip, optionally for the rest of the transfer. Interrupted transfers resume where they stopped."),
            (null, "S3 servers with no bucket set list all buckets. Buckets themselves are read-only: zSSH never creates, renames or deletes them."),
        }),
        ("Servers", new (string?, string)[]
        {
            ("Click a server", "View its settings (read-only); press Edit to change them"),
            ("Double-click / Connect", "Open a terminal (SSH) or the file browser (SFTP-only and S3)"),
            ("Ctrl+click several, then Connect", "Open SSH servers side by side in one tab"),
            ("Browse files", "Open the file browser for the selected server"),
            ("Ctrl+S", "Save, while editing a server"),
        }),
        ("Security", new (string?, string)[]
        {
            ("Ctrl+Shift+L", "Lock zSSH. Sessions and transfers keep running; unlocking needs the admin password"),
            ("Ctrl+Shift+H", "This help"),
            (null, "zSSH locks itself after the idle time set in Settings. Every run starts with no sessions open."),
            (null, "Forgot the admin password? Use the link on the unlock screen to start again. The old vault is set aside, not deleted, in case the password turns up."),
        }),
    };

    public HelpWindow()
    {
        InitializeComponent();
        VersionText.Text = AppInfo.DisplayVersion;
        foreach (var (title, rows) in Sections)
        {
            SectionsPanel.Children.Add(new TextBlock { Text = title, Classes = { "section" } });
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("270,*") };
            foreach (var (keys, text) in rows)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var row = grid.RowDefinitions.Count - 1;
                if (keys == null)
                {
                    var note = new TextBlock { Text = text, Classes = { "note" } };
                    Grid.SetRow(note, row);
                    Grid.SetColumnSpan(note, 2);
                    grid.Children.Add(note);
                    continue;
                }
                var key = new TextBlock { Text = keys, Classes = { "key" } };
                var what = new TextBlock { Text = text, Classes = { "what" } };
                Grid.SetRow(key, row);
                Grid.SetRow(what, row);
                Grid.SetColumn(what, 1);
                grid.Children.Add(key);
                grid.Children.Add(what);
            }
            SectionsPanel.Children.Add(grid);
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnLicences(object? sender, RoutedEventArgs e) => _ = new LicensesWindow().ShowDialog(this);
}
