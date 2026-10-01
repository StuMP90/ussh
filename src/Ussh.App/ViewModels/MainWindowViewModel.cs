using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ussh.App.Services;
using Ussh.Core.Diagnostics;
using Ussh.Core.Models;
using Ussh.Core.Security;
using Ussh.Core.Ssh;

namespace Ussh.App.ViewModels;

/// <summary>
/// Owns the vault lifecycle (setup → unlock → auto-lock) and the tab list.
///
/// Locking hides everything behind the admin password screen and drops the decrypted server
/// list from memory, but open sessions keep running underneath. Each session holds its own
/// copy of its profile, so it can still auto-reconnect while the vault is locked.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly Vault _vault;
    private readonly SessionManager _sessions;
    private readonly DialogService _dialogs;
    private readonly DispatcherTimer _autoLockTimer;
    private readonly List<(Guid ServerId, string Fingerprint)> _pendingFingerprints = new();
    private VaultData? _data;
    private DateTime _lastActivityUtc = DateTime.UtcNow;

    public MainWindowViewModel(Vault vault, SessionManager sessions, DialogService dialogs)
    {
        _vault = vault;
        _sessions = sessions;
        _dialogs = dialogs;
        _isSetupRequired = !vault.Exists;
        Servers = new ServersTabViewModel(this, dialogs);
        Tabs.Add(Servers);
        _selectedTab = Servers;

        _sessions.HostKeyTrusted += (session, fingerprint) =>
            Dispatcher.UIThread.Post(() => RememberHostKey(session.Profile.Id, fingerprint));

        _autoLockTimer = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, (_, _) => CheckAutoLock());
        _autoLockTimer.Start();
    }

    public ObservableCollection<TabViewModel> Tabs { get; } = new();
    public ServersTabViewModel Servers { get; }
    public AppSettings Settings => _data?.Settings ?? new AppSettings();
    internal VaultData? Data => _data;

    [ObservableProperty]
    private TabViewModel? _selectedTab;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LockTitle), nameof(LockPrompt), nameof(UnlockButtonText))]
    private bool _isSetupRequired;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LiveSessionNote))]
    private bool _isLocked = true;

    [ObservableProperty]
    private string _password = "";

    [ObservableProperty]
    private string _confirmPassword = "";

    [ObservableProperty]
    private string? _lockError;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UnlockCommand))]
    private bool _isBusy;

    public string LockTitle => IsSetupRequired ? "Welcome to ussh" : "ussh is locked";

    public string LockPrompt => IsSetupRequired
        ? $"Create an admin password. It encrypts your saved servers, passwords and keys, and is required to connect or change server settings. Minimum {Vault.MinimumPasswordLength} characters. It cannot be recovered if forgotten."
        : "Enter the admin password to manage servers and open connections.";

    public string UnlockButtonText => IsSetupRequired ? "Create vault" : "Unlock";

    public string? LiveSessionNote
    {
        get
        {
            var live = Tabs.OfType<TerminalTabViewModel>().Count();
            return IsLocked && live > 0 ? $"{live} session(s) still running in the background." : null;
        }
    }

    /// <summary>Called by the window on any keyboard or pointer input.</summary>
    public void RecordActivity() => _lastActivityUtc = DateTime.UtcNow;

    [RelayCommand(CanExecute = nameof(CanUnlock))]
    private async Task UnlockAsync()
    {
        LockError = null;
        var password = Password;
        if (IsSetupRequired && password != ConfirmPassword)
        {
            LockError = "The passwords don't match.";
            return;
        }

        IsBusy = true;
        try
        {
            // Key derivation is deliberately slow; keep it off the UI thread.
            var setup = IsSetupRequired;
            var data = await Task.Run(() => setup ? _vault.Create(password) : _vault.Unlock(password));
            OnUnlocked(data);
        }
        catch (VaultException ex)
        {
            LockError = ex.Message;
        }
        catch (Exception ex)
        {
            Log.Error("vault", "Unlock failed.", ex);
            LockError = "Could not open the vault: " + ex.Message;
        }
        finally
        {
            Password = "";
            ConfirmPassword = "";
            IsBusy = false;
        }
    }

    private bool CanUnlock() => !IsBusy;

    private void OnUnlocked(VaultData data)
    {
        _data = data;
        if (_pendingFingerprints.Count > 0)
        {
            foreach (var (id, fp) in _pendingFingerprints)
                ApplyFingerprint(id, fp);
            _pendingFingerprints.Clear();
            SaveVault();
        }
        IsSetupRequired = false;
        IsLocked = false;
        RecordActivity();
        Servers.Load(data);
        Log.Info("vault", "Vault unlocked.");
    }

    [RelayCommand]
    public void Lock()
    {
        if (IsLocked)
            return;
        Servers.Unload();
        _vault.Lock();
        _data = null;
        IsLocked = true;
        Log.Info("vault", "Vault locked.");
    }

    private void CheckAutoLock()
    {
        var minutes = _data?.Settings.AutoLockMinutes ?? 0;
        if (!IsLocked && minutes > 0 && DateTime.UtcNow - _lastActivityUtc > TimeSpan.FromMinutes(minutes))
            Lock();
    }

    public bool SaveVault()
    {
        if (_data == null)
            return false;
        try
        {
            _vault.Save(_data);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("vault", "Save failed.", ex);
            _ = _dialogs.AlertAsync("Save failed", "Could not save the vault: " + ex.Message);
            return false;
        }
    }

    public void ChangePassword(string current, string replacement)
    {
        if (_data == null)
            throw new VaultException("The vault is locked.");
        _vault.ChangePassword(current, replacement, _data);
    }

    public void Connect(ServerProfile profile)
    {
        if (IsLocked)
            return;
        var session = _sessions.Open(profile);
        var tab = new TerminalTabViewModel(session, this);
        Tabs.Add(tab);
        SelectedTab = tab;
        session.Start();
        OnPropertyChanged(nameof(LiveSessionNote));
    }

    public async Task CloseTabAsync(TerminalTabViewModel tab)
    {
        if (tab.Session.State == SessionState.Connected &&
            !await _dialogs.ConfirmAsync("Close session", $"Disconnect from {tab.Title}?", "Disconnect"))
            return;

        var index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        if (SelectedTab == null || SelectedTab == tab)
            SelectedTab = Tabs.Count > 0 ? Tabs[Math.Clamp(index - 1, 0, Tabs.Count - 1)] : null;
        tab.Detach();
        await _sessions.CloseAsync(tab.Session);
        OnPropertyChanged(nameof(LiveSessionNote));
    }

    public int ConnectedSessionCount => _sessions.Sessions.Count(s => s.State == SessionState.Connected);

    private void RememberHostKey(Guid serverId, string fingerprint)
    {
        if (_data == null)
        {
            // Locked: apply on next unlock.
            _pendingFingerprints.Add((serverId, fingerprint));
            return;
        }
        if (ApplyFingerprint(serverId, fingerprint))
        {
            SaveVault();
            Servers.OnHostKeyChanged(serverId, fingerprint);
        }
    }

    private bool ApplyFingerprint(Guid serverId, string fingerprint)
    {
        var server = _data?.Servers.FirstOrDefault(s => s.Id == serverId);
        if (server == null)
            return false;
        server.HostKeyFingerprint = fingerprint;
        return true;
    }
}
