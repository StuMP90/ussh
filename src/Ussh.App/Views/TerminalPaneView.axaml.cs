using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Ussh.App.ViewModels;
using Ussh.Core.Models;

namespace Ussh.App.Views;

// Keyboard focus on tab switch is handled by MainWindow (views are recycled between tabs).
public partial class TerminalPaneView : UserControl
{
    public TerminalPaneView()
    {
        InitializeComponent();
        // Clicking or tabbing into a pane makes it the tab's focused pane.
        AddHandler(GotFocusEvent, (_, _) =>
        {
            if (DataContext is TerminalPaneViewModel pane)
                pane.Tab.FocusedPane = pane;
        }, RoutingStrategies.Bubble, handledEventsToo: true);

        var contextMenu = new ContextMenu();
        contextMenu.Opening += (_, _) => contextMenu.ItemsSource = BuildMenu();
        Terminal.ContextMenu = contextMenu;
    }

    private void OnMenuButtonClick(object? sender, RoutedEventArgs e)
    {
        var flyout = new MenuFlyout { ItemsSource = BuildMenu(), Placement = PlacementMode.TopEdgeAlignedRight };
        flyout.ShowAt(MenuButton);
    }

    private List<Control> BuildMenu()
    {
        var items = new List<Control>();
        if (DataContext is not TerminalPaneViewModel pane)
            return items;
        var tab = pane.Tab;
        var main = tab.Main;

        items.Add(Item("Copy", "Ctrl+Shift+C", () => _ = Terminal.CopySelectionAsync()));
        items.Add(Item("Paste", "Ctrl+Shift+V", () => _ = Terminal.PasteAsync()));
        items.Add(Item("Select all", null, Terminal.SelectAll));
        items.Add(new Separator());

        foreach (var (label, gesture, orientation) in new[]
                 {
                     ("Split right", "Ctrl+Shift+E", Orientation.Horizontal),
                     ("Split down", "Ctrl+Shift+O", Orientation.Vertical),
                 })
        {
            var split = new MenuItem { Header = label };
            var children = new List<Control>
            {
                Item($"Same server ({pane.Title})", gesture, () => main.Split(tab, pane, orientation)),
            };
            var others = main.SavedServers.Where(s => s.Id != pane.Session.Profile.Id).ToList();
            if (others.Count > 0)
                children.Add(new Separator());
            foreach (var server in others)
                children.Add(Item(Describe(server), null, () => main.Split(tab, pane, orientation, server)));
            split.ItemsSource = children;
            split.IsEnabled = !main.IsLocked;
            items.Add(split);
        }

        var broadcast = Item(tab.IsBroadcasting ? "Stop broadcasting input" : "Broadcast input to all panes", "Ctrl+Shift+B",
            () => tab.ToggleBroadcastCommand.Execute(null));
        broadcast.IsEnabled = tab.HasMultiplePanes;
        items.Add(broadcast);
        var saved = main.SavedServers.FirstOrDefault(s => s.Id == pane.Session.Profile.Id);
        var browse = Item("Browse files", null, () => main.OpenFiles(saved ?? pane.Session.Profile));
        browse.IsEnabled = !main.IsLocked;
        items.Add(browse);
        if (tab.HasMultiplePanes)
            items.Add(Item("Move to new tab", null, () => main.MovePaneToNewTab(tab, pane)));
        items.Add(new Separator());
        items.Add(Item(tab.HasMultiplePanes ? "Close pane" : "Close tab", "Ctrl+Shift+W", () => _ = main.ClosePaneAsync(tab, pane)));
        return items;
    }

    private static string Describe(ServerProfile server) =>
        string.IsNullOrWhiteSpace(server.Group) ? server.DisplayName : $"{server.DisplayName}  ({server.Group})";

    private static MenuItem Item(string header, string? gesture, Action action)
    {
        var item = new MenuItem { Header = header };
        if (gesture != null)
            item.InputGesture = KeyGesture.Parse(gesture);
        item.Click += (_, _) => action();
        return item;
    }
}
