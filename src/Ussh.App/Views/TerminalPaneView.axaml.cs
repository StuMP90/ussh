using Avalonia.Controls;

namespace Ussh.App.Views;

// Keyboard focus on tab switch is handled by MainWindow (the view is recycled between tabs).
public partial class TerminalView : UserControl
{
    public TerminalView() => InitializeComponent();
}
