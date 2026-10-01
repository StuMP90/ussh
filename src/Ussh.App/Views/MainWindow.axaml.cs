using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ussh.App.Controls;
using Ussh.App.ViewModels;

namespace Ussh.App.Views;

public partial class MainWindow : Window
{
    private bool _closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        // Any input counts as activity for auto-lock, including input handled by the terminal.
        AddHandler(KeyDownEvent, (_, _) => ViewModel?.RecordActivity(), RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerPressedEvent, (_, _) => ViewModel?.RecordActivity(), RoutingStrategies.Tunnel, handledEventsToo: true);
        // Tunnel so app shortcuts win over the terminal, which otherwise consumes every key.
        AddHandler(KeyDownEvent, OnShortcutKeyDown, RoutingStrategies.Tunnel);
        Opened += (_, _) => FocusPassword();
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (ViewModel != null)
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
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
        else
            return;
        e.Handled = true;
    }

    private void OnTabSelectionChanged(object? sender, SelectionChangedEventArgs e) => FocusSelectedTerminal();

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
            .FirstOrDefault(t => ReferenceEquals(t.Session, tab.Session));
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
