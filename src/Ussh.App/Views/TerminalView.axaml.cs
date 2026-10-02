using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Ussh.App.Views;

/// <summary>A terminal tab: its pane layout plus the pane/broadcast bar.</summary>
public partial class TerminalView : UserControl
{
    public static readonly IValueConverter BroadcastBackground = new FuncValueConverter<bool, IBrush>(on =>
        on ? new SolidColorBrush(Color.Parse("#80B06A10")) : new SolidColorBrush(Color.Parse("#10FFFFFF")));

    public TerminalView() => InitializeComponent();
}
