using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ussh.App.Services;
using Ussh.Core.Models;
using Ussh.Core.Security;
using Ussh.Core.Ssh;

namespace Ussh.App.ViewModels;

public sealed partial class ServerItemViewModel : ObservableObject
{
    public ServerItemViewModel(ServerProfile profile) => Profile = profile;

    public ServerProfile Profile { get; private set; }
    public string Name => Profile.DisplayName;
    public string Address => $"{Profile.Username}@{Profile.Host}" + (Profile.Port == 22 ? "" : $":{Profile.Port}");
    public string Group => Profile.Group;
    public bool HasGroup => !string.IsNullOrWhiteSpace(Profile.Group);
    public int TunnelCount => Profile.Tunnels.Count(t => t.Enabled);
    public string? TunnelBadge => TunnelCount > 0 ? $"{TunnelCount} tunnel(s)" : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVia))]
    private string? _via;

    public bool HasVia => Via != null;

    public void Update(ServerProfile profile)
    {
        Profile = profile;
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>The "Servers" tab: server list, editor, and app settings.</summary>
public sealed partial class ServersTabViewModel : TabViewModel
{
    private readonly MainWindowViewModel _main;
    private readonly DialogService _dialogs;
    private readonly List<ServerItemViewModel> _all = new();

    public ServersTabViewModel(MainWindowViewModel main, DialogService dialogs)
    {
        _main = main;
        _dialogs = dialogs;
        Title = "Servers";
        CloseTabCommand = new AsyncRelayCommand(() => Task.CompletedTask, () => false);
    }

    public override bool CanClose => false;
    public override IAsyncRelayCommand CloseTabCommand { get; }

    public ObservableCollection<ServerItemViewModel> FilteredServers { get; } = new();

    [ObservableProperty] private string _filter = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand), nameof(DuplicateServerCommand), nameof(DeleteServerCommand))]
    private ServerItemViewModel? _selectedServer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyHint))]
    private ServerEditorViewModel? _editor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyHint))]
    private bool _isSettingsVisible;

    // Settings
    [ObservableProperty] private decimal _autoLockMinutes;
    [ObservableProperty] private decimal _fontSize;
    [ObservableProperty] private string _fontFamily = "";
    [ObservableProperty] private string _currentPassword = "";
    [ObservableProperty] private string _newPassword = "";
    [ObservableProperty] private string _confirmNewPassword = "";
    [ObservableProperty] private string? _settingsMessage;
    [ObservableProperty] private ThemeOption? _defaultTheme;

    public IReadOnlyList<ThemeOption> SettingsThemeOptions { get; } = ThemeOption.List();

    public bool ShowEmptyHint => Editor == null && !IsSettingsVisible;
    public string VaultLocation => Ussh.Core.AppPaths.VaultFile;

    public void Load(VaultData data)
    {
        _all.Clear();
        _all.AddRange(data.Servers.Select(s => new ServerItemViewModel(s)));
        AutoLockMinutes = data.Settings.AutoLockMinutes;
        FontSize = (decimal)data.Settings.FontSize;
        FontFamily = data.Settings.FontFamily;
        DefaultTheme = SettingsThemeOptions.FirstOrDefault(o => o.Name == data.Settings.DefaultTheme) ?? SettingsThemeOptions[0];
        ApplyFilter();
    }

    /// <summary>Drops every reference to decrypted data (called on lock).</summary>
    public void Unload()
    {
        _all.Clear();
        FilteredServers.Clear();
        SelectedServer = null;
        Editor = null;
        IsSettingsVisible = false;
        CurrentPassword = NewPassword = ConfirmNewPassword = "";
        SettingsMessage = null;
    }

    partial void OnFilterChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        UpdateVia();
        var terms = Filter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var desired = _all
            .Where(i => terms.All(t =>
                i.Name.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                i.Address.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                i.Group.Contains(t, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(i => i.Group, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Sync in place rather than Clear()+Add, so the selected item never leaves the
        // collection (which would null the ListBox selection and drop the open editor).
        for (var i = FilteredServers.Count - 1; i >= 0; i--)
        {
            if (!desired.Contains(FilteredServers[i]))
                FilteredServers.RemoveAt(i);
        }
        for (var i = 0; i < desired.Count; i++)
        {
            var current = FilteredServers.IndexOf(desired[i]);
            if (current == i)
                continue;
            if (current >= 0)
                FilteredServers.Move(current, i);
            else
                FilteredServers.Insert(i, desired[i]);
        }
    }

    /// <summary>Shows "via bastion" under servers that use a jump host.</summary>
    private void UpdateVia()
    {
        var servers = _main.Data?.Servers;
        if (servers == null)
            return;
        foreach (var item in _all)
        {
            try
            {
                var chain = JumpHostResolver.Resolve(item.Profile, servers);
                item.Via = chain.Count == 0 ? null : "via " + string.Join(" → ", chain.Select(c => c.DisplayName));
            }
            catch (JumpHostConfigurationException)
            {
                item.Via = "jump host missing";
            }
        }
    }

    private ServerEditorViewModel NewEditor(ServerProfile profile, bool isNew)
    {
        var servers = _main.Data?.Servers ?? new List<ServerProfile>();
        var jumpOptions = new List<JumpHostOption> { JumpHostOption.Direct };
        jumpOptions.AddRange(servers
            .Where(s => JumpHostResolver.CanUseAsJumpHost(profile.Id, s, servers))
            .OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(JumpHostOption.For));
        return new ServerEditorViewModel(profile, isNew, _dialogs, jumpOptions,
            ThemeOption.List(_main.Data?.Settings.DefaultTheme ?? TerminalThemeDefaultName));
    }

    private const string TerminalThemeDefaultName = Ussh.App.Controls.TerminalTheme.DefaultName;

    partial void OnSelectedServerChanged(ServerItemViewModel? value)
    {
        if (value == null)
        {
            if (Editor is { IsNew: false })
                Editor = null;
            return;
        }
        IsSettingsVisible = false;
        if (Editor?.Id != value.Profile.Id)
            Editor = NewEditor(value.Profile.Clone(), isNew: false);
    }

    [RelayCommand]
    private void AddServer()
    {
        SelectedServer = null;
        IsSettingsVisible = false;
        Editor = NewEditor(new ServerProfile(), isNew: true);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void DuplicateServer()
    {
        var source = SelectedServer!.Profile.Clone();
        source.Id = Guid.NewGuid();
        source.Name = string.IsNullOrWhiteSpace(source.Name) ? "" : source.Name + " (copy)";
        source.HostKeyFingerprint = null;
        SelectedServer = null;
        Editor = NewEditor(source, isNew: true);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteServerAsync()
    {
        var item = SelectedServer!;
        var data = _main.Data;
        if (data == null)
            return;
        var dependants = data.Servers.Where(s => s.JumpHostId == item.Profile.Id).ToList();
        var warning = dependants.Count == 0 ? "" :
            $"\n\nIt is the jump host for: {string.Join(", ", dependants.Select(d => d.DisplayName))}. They will connect directly instead.";
        if (!await _dialogs.ConfirmAsync("Delete server", $"Delete \"{item.Name}\" ({item.Address})?\n\nIts saved password/key will be removed from the vault.{warning}", "Delete", danger: true))
            return;
        data.Servers.RemoveAll(s => s.Id == item.Profile.Id);
        foreach (var dependant in dependants)
            dependant.JumpHostId = null;
        if (_main.SaveVault())
        {
            _all.Remove(item);
            SelectedServer = null;
            Editor = null;
            ApplyFilter();
        }
    }

    [RelayCommand]
    private void SaveServer()
    {
        var editor = Editor;
        var data = _main.Data;
        if (editor == null || data == null)
            return;

        editor.ValidationError = editor.Validate();
        if (editor.ValidationError != null)
            return;

        var profile = editor.ToProfile();
        var index = data.Servers.FindIndex(s => s.Id == profile.Id);
        if (index >= 0)
            data.Servers[index] = profile;
        else
            data.Servers.Add(profile);
        if (!_main.SaveVault())
            return;

        editor.MarkSaved();
        _main.RefreshAppearance();
        var item = _all.FirstOrDefault(i => i.Profile.Id == profile.Id);
        if (item == null)
        {
            item = new ServerItemViewModel(profile);
            _all.Add(item);
        }
        else
        {
            item.Update(profile);
        }
        ApplyFilter();
        // The editor already belongs to this server, so selecting it keeps the editor as-is.
        SelectedServer = item;
    }

    [RelayCommand]
    private void RevertServer()
    {
        if (SelectedServer != null)
            Editor = NewEditor(SelectedServer.Profile.Clone(), isNew: false);
        else
            Editor = null;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ConnectAsync()
    {
        if (SelectedServer == null)
            return;
        var targets = SelectedServers.Count > 1 ? SelectedServers.ToList() : new List<ServerItemViewModel> { SelectedServer };
        if (Editor is { IsDirty: true } editor && targets.Any(t => t.Profile.Id == editor.Id))
        {
            if (!await _dialogs.ConfirmAsync("Unsaved changes", "Save changes to this server before connecting?", "Save and connect"))
                return;
            SaveServer();
            if (editor.ValidationError != null)
                return;
        }
        // Profiles are re-read after a possible save (SaveServer replaces the saved object).
        _main.ConnectTogether(targets.Select(t => t.Profile).ToList());
    }

    /// <summary>All selected servers (Ctrl/Shift+click). Several open side by side in one tab.</summary>
    public IReadOnlyList<ServerItemViewModel> SelectedServers
    {
        get => _selectedServers;
        set
        {
            _selectedServers = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ConnectLabel));
        }
    }

    private IReadOnlyList<ServerItemViewModel> _selectedServers = Array.Empty<ServerItemViewModel>();

    public string ConnectLabel => SelectedServers.Count > 1 ? $"Connect {SelectedServers.Count} side by side" : "Connect";

    private bool HasSelection() => SelectedServer != null;

    [RelayCommand]
    private void ShowSettings()
    {
        SelectedServer = null;
        Editor = null;
        IsSettingsVisible = true;
        SettingsMessage = null;
    }

    [RelayCommand]
    private void SaveSettings()
    {
        var data = _main.Data;
        if (data == null)
            return;
        data.Settings.AutoLockMinutes = (int)Math.Clamp(AutoLockMinutes, 0, 24 * 60);
        data.Settings.FontSize = (double)Math.Clamp(FontSize, 6, 48);
        data.Settings.FontFamily = string.IsNullOrWhiteSpace(FontFamily) ? new AppSettings().FontFamily : FontFamily.Trim();
        data.Settings.DefaultTheme = DefaultTheme?.Name ?? TerminalThemeDefaultName;
        if (_main.SaveVault())
        {
            SettingsMessage = "Settings saved.";
            _main.RefreshAppearance();
        }
    }

    [RelayCommand]
    private async Task ChangePasswordAsync()
    {
        SettingsMessage = null;
        if (NewPassword != ConfirmNewPassword)
        {
            SettingsMessage = "The new passwords don't match.";
            return;
        }
        var (current, replacement) = (CurrentPassword, NewPassword);
        try
        {
            await Task.Run(() => _main.ChangePassword(current, replacement));
            SettingsMessage = "Admin password changed.";
        }
        catch (VaultException ex)
        {
            SettingsMessage = ex.Message;
        }
        finally
        {
            CurrentPassword = NewPassword = ConfirmNewPassword = "";
        }
    }

    public void OnHostKeyChanged(Guid serverId, string fingerprint)
    {
        var item = _all.FirstOrDefault(i => i.Profile.Id == serverId);
        item?.Update(_main.Data!.Servers.First(s => s.Id == serverId));
        if (Editor != null && Editor.Id == serverId)
            Editor.SetSavedHostKey(fingerprint);
    }
}
