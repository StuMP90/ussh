using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Ussh.App.ViewModels;

public abstract partial class TabViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private IBrush? _indicator;

    /// <summary>Ctrl+clicked, to be combined with the selected tab into one split tab.</summary>
    [ObservableProperty]
    private bool _isMarked;

    public abstract bool CanClose { get; }

    public abstract IAsyncRelayCommand CloseTabCommand { get; }
}
