using Avalonia.Controls;
using Avalonia.Input;
using Ussh.App.ViewModels;

namespace Ussh.App.Views;

public partial class ServersView : UserControl
{
    public ServersView() => InitializeComponent();

    private void OnServerDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is ServersTabViewModel vm && vm.ConnectCommand.CanExecute(null))
            vm.ConnectCommand.Execute(null);
    }
}
