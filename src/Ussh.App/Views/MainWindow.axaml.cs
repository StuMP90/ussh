using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
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
        if (e.PropertyName == nameof(MainWindowViewModel.IsLocked) && ViewModel?.IsLocked == true)
            FocusPassword();
    }

    private void FocusPassword() => Dispatcher.UIThread.Post(() => PasswordBox.Focus(), DispatcherPriority.Input);

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        var vm = ViewModel;
        if (_closeConfirmed || vm == null || vm.ConnectedSessionCount == 0)
            return;

        e.Cancel = true;
        var dialogs = new Services.DialogService(this);
        if (await dialogs.ConfirmAsync("Quit ussh", $"{vm.ConnectedSessionCount} session(s) are connected. Disconnect them and quit?", "Quit", danger: true))
        {
            _closeConfirmed = true;
            Close();
        }
    }
}
