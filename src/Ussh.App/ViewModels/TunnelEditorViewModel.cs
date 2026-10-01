using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ussh.Core.Models;

namespace Ussh.App.ViewModels;

public sealed partial class TunnelEditorViewModel : ObservableObject
{
    private readonly Action<TunnelEditorViewModel> _remove;

    public TunnelEditorViewModel(TunnelDefinition definition, Action<TunnelEditorViewModel> remove)
    {
        _remove = remove;
        _enabled = definition.Enabled;
        _typeIndex = (int)definition.Type;
        _bindAddress = definition.BindAddress;
        _bindPort = definition.BindPort;
        _destinationHost = definition.DestinationHost;
        _destinationPort = definition.DestinationPort;
    }

    public string[] Types { get; } = { "Local (-L)", "Remote (-R)", "Dynamic SOCKS (-D)" };

    [ObservableProperty] private bool _enabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDestination), nameof(Explanation))]
    private int _typeIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Explanation))]
    private string _bindAddress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Explanation))]
    private decimal _bindPort;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Explanation))]
    private string _destinationHost;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Explanation))]
    private decimal _destinationPort;

    public bool HasDestination => TypeIndex != (int)TunnelType.Dynamic;

    public string Explanation => (TunnelType)TypeIndex switch
    {
        TunnelType.Local => $"Connections to {BindAddress}:{BindPort} on this computer go through the server to {DestinationHost}:{DestinationPort}.",
        TunnelType.Remote => $"Connections to {BindAddress}:{BindPort} on the server come back through this computer to {DestinationHost}:{DestinationPort}.",
        TunnelType.Dynamic => $"SOCKS5 proxy on {BindAddress}:{BindPort}; traffic exits from the server.",
        _ => "",
    };

    [RelayCommand]
    private void Remove() => _remove(this);

    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(BindAddress))
            return "listen address is required.";
        if (BindPort is < 1 or > 65535)
            return "listen port must be between 1 and 65535.";
        if (HasDestination)
        {
            if (string.IsNullOrWhiteSpace(DestinationHost))
                return "destination host is required.";
            if (DestinationPort is < 1 or > 65535)
                return "destination port must be between 1 and 65535.";
        }
        return null;
    }

    public TunnelDefinition ToDefinition() => new()
    {
        Enabled = Enabled,
        Type = (TunnelType)TypeIndex,
        BindAddress = BindAddress.Trim(),
        BindPort = (int)BindPort,
        DestinationHost = DestinationHost.Trim(),
        DestinationPort = (int)DestinationPort,
    };
}
