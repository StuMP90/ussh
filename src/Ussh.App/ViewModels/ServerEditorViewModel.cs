using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ussh.App.Services;
using Ussh.Core.Models;
using Ussh.Core.Ssh;

namespace Ussh.App.ViewModels;

/// <summary>Edits a copy of a server profile; nothing changes in the vault until Save.</summary>
public sealed partial class ServerEditorViewModel : ObservableObject
{
    private readonly Guid _id;
    private readonly DialogService _dialogs;

    public ServerEditorViewModel(ServerProfile profile, bool isNew, DialogService dialogs)
    {
        _id = profile.Id;
        _dialogs = dialogs;
        IsNew = isNew;
        _name = profile.Name;
        _group = profile.Group;
        _host = profile.Host;
        _port = profile.Port;
        _username = profile.Username;
        _authMethodIndex = (int)profile.AuthMethod;
        _password = profile.Password ?? "";
        _privateKey = profile.PrivateKey ?? "";
        _privateKeyPassphrase = profile.PrivateKeyPassphrase ?? "";
        _hostKeyFingerprint = profile.HostKeyFingerprint;
        _keepAliveSeconds = profile.KeepAliveSeconds;
        _connectTimeoutSeconds = profile.ConnectTimeoutSeconds;
        _autoReconnect = profile.AutoReconnect;
        _terminalType = profile.TerminalType;
        _scrollbackLines = profile.ScrollbackLines;
        _notes = profile.Notes;
        foreach (var tunnel in profile.Tunnels)
            Tunnels.Add(NewTunnel(tunnel));
        Tunnels.CollectionChanged += (_, _) => IsDirty = true;
        PropertyChanged += OnAnyPropertyChanged;
        IsDirty = isNew;
    }

    public Guid Id => _id;
    public bool IsNew { get; private set; }
    public ObservableCollection<TunnelEditorViewModel> Tunnels { get; } = new();
    public string[] AuthMethods { get; } = { "Password", "Private key" };

    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private string? _validationError;

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _group;
    [ObservableProperty] private string _host;
    [ObservableProperty] private decimal _port;
    [ObservableProperty] private string _username;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPasswordAuth), nameof(IsKeyAuth))]
    private int _authMethodIndex;

    [ObservableProperty] private string _password;
    [ObservableProperty] private string _privateKey;
    [ObservableProperty] private string _privateKeyPassphrase;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HostKeyDisplay), nameof(HasHostKey))]
    private string? _hostKeyFingerprint;

    [ObservableProperty] private decimal _keepAliveSeconds;
    [ObservableProperty] private decimal _connectTimeoutSeconds;
    [ObservableProperty] private bool _autoReconnect;
    [ObservableProperty] private string _terminalType;
    [ObservableProperty] private decimal _scrollbackLines;
    [ObservableProperty] private string _notes;

    public bool IsPasswordAuth => AuthMethodIndex == (int)AuthMethod.Password;
    public bool IsKeyAuth => AuthMethodIndex == (int)AuthMethod.PrivateKey;
    public bool HasHostKey => HostKeyFingerprint != null;
    public string HostKeyDisplay => HostKeyFingerprint == null
        ? "Not yet trusted. You'll be asked to confirm it on first connect."
        : "SHA256:" + HostKeyFingerprint;

    public string Heading => IsNew ? "New server" : (string.IsNullOrWhiteSpace(Name) ? $"{Username}@{Host}" : Name);

    private void OnAnyPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IsDirty) or nameof(ValidationError) or nameof(Heading)
            or nameof(IsPasswordAuth) or nameof(IsKeyAuth) or nameof(HostKeyDisplay) or nameof(HasHostKey))
            return;
        IsDirty = true;
        if (e.PropertyName is nameof(Name) or nameof(Host) or nameof(Username))
            OnPropertyChanged(nameof(Heading));
    }

    [RelayCommand]
    private void AddTunnel() => Tunnels.Add(NewTunnel(new TunnelDefinition()));

    [RelayCommand]
    private async Task ImportKeyAsync()
    {
        try
        {
            var text = await _dialogs.PickFileTextAsync("Import private key");
            if (text != null)
                PrivateKey = text.Trim();
        }
        catch (Exception ex)
        {
            await _dialogs.AlertAsync("Import failed", ex.Message);
        }
    }

    [RelayCommand]
    private void ForgetHostKey() => HostKeyFingerprint = null;

    public void MarkSaved()
    {
        IsNew = false;
        IsDirty = false;
        OnPropertyChanged(nameof(Heading));
    }

    /// <summary>Updates the fingerprint without marking the form dirty (it was saved by the connection).</summary>
    public void SetSavedHostKey(string fingerprint)
    {
        var dirty = IsDirty;
        HostKeyFingerprint = fingerprint;
        IsDirty = dirty;
    }

    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Host))
            return "Host is required.";
        if (Host.Any(char.IsWhiteSpace))
            return "Host must not contain spaces.";
        if (Port is < 1 or > 65535)
            return "Port must be between 1 and 65535.";
        if (string.IsNullOrWhiteSpace(Username))
            return "Username is required.";
        if (IsKeyAuth && KeyValidator.Validate(PrivateKey, PrivateKeyPassphrase) is { } keyError)
            return keyError;

        foreach (var tunnel in Tunnels)
        {
            if (tunnel.Validate() is { } tunnelError)
                return $"Tunnel {Tunnels.IndexOf(tunnel) + 1}: {tunnelError}";
        }
        var clash = Tunnels.Where(t => t.Enabled && t.TypeIndex != (int)TunnelType.Remote)
            .GroupBy(t => (t.BindAddress.Trim(), t.BindPort))
            .FirstOrDefault(g => g.Count() > 1);
        if (clash != null)
            return $"Two tunnels both listen on {clash.Key.Item1}:{clash.Key.BindPort}.";
        return null;
    }

    public ServerProfile ToProfile() => new()
    {
        Id = _id,
        Name = Name.Trim(),
        Group = Group.Trim(),
        Host = Host.Trim(),
        Port = (int)Port,
        Username = Username.Trim(),
        AuthMethod = (AuthMethod)AuthMethodIndex,
        // Keep only the secret for the chosen method.
        Password = IsPasswordAuth && Password.Length > 0 ? Password : null,
        PrivateKey = IsKeyAuth && PrivateKey.Length > 0 ? PrivateKey.Trim() : null,
        PrivateKeyPassphrase = IsKeyAuth && PrivateKeyPassphrase.Length > 0 ? PrivateKeyPassphrase : null,
        HostKeyFingerprint = HostKeyFingerprint,
        KeepAliveSeconds = (int)KeepAliveSeconds,
        ConnectTimeoutSeconds = (int)ConnectTimeoutSeconds,
        AutoReconnect = AutoReconnect,
        TerminalType = string.IsNullOrWhiteSpace(TerminalType) ? "xterm-256color" : TerminalType.Trim(),
        ScrollbackLines = (int)ScrollbackLines,
        Tunnels = Tunnels.Select(t => t.ToDefinition()).ToList(),
        Notes = Notes,
    };

    private TunnelEditorViewModel NewTunnel(TunnelDefinition definition)
    {
        var vm = new TunnelEditorViewModel(definition, t => Tunnels.Remove(t));
        vm.PropertyChanged += (_, _) => IsDirty = true;
        return vm;
    }
}
