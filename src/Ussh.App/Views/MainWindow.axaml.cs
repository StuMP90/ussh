using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ussh.App.Controls;
using Ussh.App.ViewModels;

namespace Ussh.App.Views;

public partial class MainWindow : Window
{
    private bool _closeConfirmed;

    public static readonly IValueConverter MarkedBorder = new FuncValueConverter<bool, IBrush>(marked =>
        marked ? new SolidColorBrush(Color.Parse("#4A90E2")) : Brushes.Transparent);

    public MainWindow()
    {
        InitializeComponent();
        // Any input counts as activity for auto-lock, including input handled by the terminal.
        AddHandler(KeyDownEvent, (_, _) => ViewModel?.RecordActivity(), RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerPressedEvent, (_, _) => ViewModel?.RecordActivity(), RoutingStrategies.Tunnel, handledEventsToo: true);
        // Tunnel so app shortcuts win over the terminal, which otherwise consumes every key.
        AddHandler(KeyDownEvent, OnShortcutKeyDown, RoutingStrategies.Tunnel);
        // Tab headers: Ctrl+click marks tabs for combining; right-click opens the tab menu.
        TabStrip.AddHandler(PointerPressedEvent, OnTabStripPointerPressed, RoutingStrategies.Tunnel);
        TabStrip.AddHandler(ContextRequestedEvent, OnTabStripContextRequested, RoutingStrategies.Bubble);
        Opened += (_, _) => FocusPassword();
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (ViewModel != null)
        {
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
            ViewModel.TerminalFocusRequested += FocusSelectedTerminal;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainWindowViewModel.IsLocked))
            return;
        if (ViewModel?.IsLocked == true)
            FocusPassword();
        else
            FocusSelectedTerminal();
    }

    /// <summary>
    /// Application shortcuts:
    ///   Ctrl+Tab / Ctrl+PageDown         next tab
    ///   Ctrl+Shift+Tab / Ctrl+PageUp     previous tab
    ///   Alt+1..8                         tab 1..8 (Alt+9 = last tab)
    ///   Ctrl+Shift+L                     lock
    ///   Ctrl+Shift+E / Ctrl+Shift+O      split the focused pane right / down (same server)
    ///   Ctrl+Shift+W                     close the focused pane (or the tab, if it's the last)
    ///   Ctrl+Shift+B                     toggle broadcast input
    ///   Alt+Arrow                        move to the neighbouring pane (only with several panes)
    /// </summary>
    private void OnShortcutKeyDown(object? sender, KeyEventArgs e)
    {
        var vm = ViewModel;
        if (vm == null || vm.IsLocked)
            return;
        var mods = e.KeyModifiers;
        const KeyModifiers ctrlShift = KeyModifiers.Control | KeyModifiers.Shift;

        if ((mods == KeyModifiers.Control && e.Key is Key.Tab or Key.PageDown))
            vm.SelectRelativeTab(1);
        else if ((mods == ctrlShift && e.Key == Key.Tab) || (mods == KeyModifiers.Control && e.Key == Key.PageUp))
            vm.SelectRelativeTab(-1);
        else if (mods == KeyModifiers.Alt && e.Key is >= Key.D1 and <= Key.D9)
            vm.SelectTabAt(e.Key == Key.D9 ? -1 : e.Key - Key.D1);
        else if (mods == ctrlShift && e.Key == Key.L)
            vm.Lock();
        else if (vm.SelectedTab is TerminalTabViewModel tab && HandlePaneShortcut(vm, tab, e.Key, mods))
        {
        }
        else
            return;
        e.Handled = true;
    }

    private bool HandlePaneShortcut(MainWindowViewModel vm, TerminalTabViewModel tab, Key key, KeyModifiers mods)
    {
        const KeyModifiers ctrlShift = KeyModifiers.Control | KeyModifiers.Shift;
        if (mods == ctrlShift)
        {
            switch (key)
            {
                case Key.E:
                    vm.Split(tab, tab.FocusedPane, Avalonia.Layout.Orientation.Horizontal);
                    return true;
                case Key.O:
                    vm.Split(tab, tab.FocusedPane, Avalonia.Layout.Orientation.Vertical);
                    return true;
                case Key.W:
                    _ = vm.ClosePaneAsync(tab, tab.FocusedPane);
                    return true;
                case Key.B:
                    tab.ToggleBroadcastCommand.Execute(null);
                    return true;
            }
        }
        // Alt+Arrow only moves between panes when there are several; otherwise it reaches the shell.
        if (mods == KeyModifiers.Alt && tab.HasMultiplePanes && key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            FocusNeighbourPane(tab, key);
            return true;
        }
        return false;
    }

    /// <summary>Moves focus to the nearest pane in the arrow's direction, using on-screen positions.</summary>
    private void FocusNeighbourPane(TerminalTabViewModel tab, Key direction)
    {
        var terminals = TabStrip.GetVisualDescendants().OfType<TerminalControl>().Where(t => t.IsEffectivelyVisible).ToList();
        var current = terminals.FirstOrDefault(t => ReferenceEquals(t.Session, tab.FocusedPane.Session));
        if (current == null)
            return;
        var from = Center(current);
        var target = terminals
            .Where(t => t != current)
            .Select(t => (Terminal: t, Delta: Center(t) - from))
            .Where(c => direction switch
            {
                Key.Left => c.Delta.X < -1,
                Key.Right => c.Delta.X > 1,
                Key.Up => c.Delta.Y < -1,
                _ => c.Delta.Y > 1,
            })
            // Prefer panes straight across: distance along the arrow plus double the sideways offset.
            .OrderBy(c => direction is Key.Left or Key.Right
                ? Math.Abs(c.Delta.X) + 2 * Math.Abs(c.Delta.Y)
                : Math.Abs(c.Delta.Y) + 2 * Math.Abs(c.Delta.X))
            .Select(c => c.Terminal)
            .FirstOrDefault();
        target?.Focus();

        Avalonia.Point Center(TerminalControl t) =>
            t.TranslatePoint(new Avalonia.Point(t.Bounds.Width / 2, t.Bounds.Height / 2), this) ?? default;
    }

    private void OnTabSelectionChanged(object? sender, SelectionChangedEventArgs e) => FocusSelectedTerminal();

    private void OnClearMarksClick(object? sender, RoutedEventArgs e) => ViewModel?.ClearMarks();

    private static TabItem? TabItemAt(object? source) =>
        (source as Visual)?.GetSelfAndVisualAncestors().OfType<TabItem>().FirstOrDefault();

    private void OnTabStripPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel is not { } vm || TabItemAt(e.Source) is not { } item)
            return;
        if (!e.GetCurrentPoint(item).Properties.IsLeftButtonPressed)
            return;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (ctrl && item.DataContext is TerminalTabViewModel tab)
        {
            vm.ToggleMark(tab);
            e.Handled = true; // mark without switching tabs
        }
        else if (!ctrl)
        {
            vm.ClearMarks();
        }
    }

    private void OnTabStripContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (ViewModel is not { } vm || TabItemAt(e.Source) is not { DataContext: TerminalTabViewModel tab } item)
            return;
        var others = vm.Tabs.OfType<TerminalTabViewModel>().Where(t => t != tab).ToList();
        var items = new List<Control>();

        var combineWith = new MenuItem { Header = "Combine with", IsEnabled = others.Count > 0 };
        combineWith.ItemsSource = others.Select(other => MenuItemFor(other.Title, () => vm.CombineTabs(new[] { tab, other }))).ToList();
        items.Add(combineWith);
        if (others.Count >= 2)
            items.Add(MenuItemFor($"Combine all {others.Count + 1} terminal tabs", () => vm.CombineTabs(vm.Tabs.OfType<TerminalTabViewModel>().ToList())));
        if (vm.CanCombine && vm.CombineCandidates.Contains(tab))
            items.Add(MenuItemFor(vm.CombineLabel, () => vm.CombineMarkedCommand.Execute(null)));
        if (tab.HasMultiplePanes)
        {
            items.Add(new Separator());
            items.Add(MenuItemFor($"Separate {tab.Panes.Count} panes into tabs", () => vm.SeparatePanes(tab)));
        }
        items.Add(new Separator());
        items.Add(MenuItemFor("Close tab", () => _ = vm.CloseTabAsync(tab)));

        new ContextMenu { ItemsSource = items }.Open(item);
        e.Handled = true;
    }

    private static MenuItem MenuItemFor(string header, Action action)
    {
        var menuItem = new MenuItem { Header = header };
        menuItem.Click += (_, _) => action();
        return menuItem;
    }

    /// <summary>
    /// Puts the keyboard into the selected tab's terminal. Posted at background priority so it
    /// runs after the tab-header click (and the content swap) has finished; doing it earlier
    /// loses focus back to whatever was clicked. The tab content view is also recycled between
    /// terminal tabs, so this can't rely on the view being re-attached.
    /// </summary>
    private void FocusSelectedTerminal() => Dispatcher.UIThread.Post(() =>
    {
        if (ViewModel is not { IsLocked: false, SelectedTab: TerminalTabViewModel tab })
            return;
        var terminal = TabStrip.GetVisualDescendants().OfType<TerminalControl>()
            .FirstOrDefault(t => ReferenceEquals(t.Session, tab.FocusedPane.Session));
        terminal?.Focus();
    }, DispatcherPriority.Background);

    private void FocusPassword() => Dispatcher.UIThread.Post(() => PasswordBox.Focus(), DispatcherPriority.Input);

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        var vm = ViewModel;
        if (_closeConfirmed || vm == null || vm.ConnectedSessionCount == 0)
            return;

        e.Cancel = true;
        var dialogs = new Services.DialogService(this);
        if (await dialogs.ConfirmAsync("Quit uSSH", $"{vm.ConnectedSessionCount} session(s) are connected. Disconnect them and quit?", "Quit", danger: true))
        {
            _closeConfirmed = true;
            Close();
        }
    }
}
