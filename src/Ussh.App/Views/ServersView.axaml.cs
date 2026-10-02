using Avalonia.Controls;
using Avalonia.Input;
using Ussh.App.ViewModels;

namespace Ussh.App.Views;

public partial class ServersView : UserControl
{
    public ServersView() => InitializeComponent();

    private void OnServerSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is ServersTabViewModel vm)
            vm.SelectedServers = ServerList.SelectedItems?.OfType<ServerItemViewModel>().ToList() ?? new List<ServerItemViewModel>();
    }

    private void OnServerDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is ServersTabViewModel vm && vm.ConnectCommand.CanExecute(null))
            vm.ConnectCommand.Execute(null);
    }
}
