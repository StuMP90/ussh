using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Ussh.App.Views;

public partial class TerminalView : UserControl
{
    public TerminalView() => InitializeComponent();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Switching to a tab should put the keyboard straight into its terminal.
        Dispatcher.UIThread.Post(() => Terminal.Focus(), DispatcherPriority.Input);
    }
}
