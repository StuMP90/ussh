using System.Collections.ObjectModel;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ussh.App.Controls;
using Ussh.App.Services;
using Ussh.Core.Diagnostics;
using Ussh.Core.Files;
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

        _sessions.HostKeyTrusted += (_, serverId, fingerprint) =>
            Dispatcher.UIThread.Post(() => RememberHostKey(serverId, fingerprint));

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

    public string LockTitle => IsSetupRequired ? "Welcome to zSSH" : "zSSH is locked";

    public string LockPrompt => IsSetupRequired
        ? $"Create an admin password. It encrypts your saved servers, passwords and keys, and is required to connect or change server settings. Minimum {Vault.MinimumPasswordLength} characters. It cannot be recovered if forgotten."
        : "Enter the admin password to manage servers and open connections.";

    public string AppVersion => AppInfo.DisplayVersion;
    public string AppVersionNumber => AppInfo.Version;

    public string UnlockButtonText => IsSetupRequired ? "Create vault" : "Unlock";

    public string? LiveSessionNote
    {
        get
        {
            var live = Tabs.OfType<TerminalTabViewModel>().Sum(t => t.Panes.Count);
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

    /// <summary>Raised after locking (e.g. to forget in-memory passphrases).</summary>
    public event Action? Locked;

    /// <summary>
    /// "Forgot the admin password?": after a stark warning and typing RESET, sets the old vault
    /// aside (renamed, not deleted) and starts again with a new, empty vault.
    /// </summary>
    [RelayCommand]
    private async Task ForgotPasswordAsync()
    {
        if (!await _dialogs.ConfirmAsync("Forgot the admin password?",
                "The admin password can't be recovered: it's the key that encrypts everything zSSH stores.\n\n" +
                "Starting again means losing ALL saved servers, passwords, private keys, S3 access keys and " +
                "settings. You'll need to add them again.\n\n" +
                "The old vault isn't deleted: it's renamed and kept, encrypted, in the same folder. If you remember " +
                "the password later, it can be restored by renaming it back to vault.json.",
                "I understand, continue", danger: true))
            return;
        var typed = await _dialogs.PromptTextAsync("Confirm reset", "Type RESET to set the old vault aside and start again:", "");
        if (typed?.Trim() != "RESET")
            return;
        try
        {
            var aside = ResetForgottenVault();
            await _dialogs.AlertAsync("Vault set aside",
                $"The old vault was kept as:\n{aside}\n\nCreate a new admin password to continue.");
        }
        catch (Exception ex)
        {
            Log.Error("vault", "Setting the vault aside failed.", ex);
            LockError = "Couldn't set the old vault aside: " + ex.Message;
        }
    }

    /// <summary>Sets the vault aside and returns to first-run setup. Returns where it was kept.</summary>
    internal string ResetForgottenVault()
    {
        var aside = _vault.SetAsideForgotten();
        _pendingFingerprints.Clear();
        Log.Warn("vault", $"Admin password forgotten: vault set aside as {aside}; starting a new vault.");
        LockError = null;
        Password = ConfirmPassword = "";
        IsSetupRequired = true;
        return aside;
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
        Locked?.Invoke();
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

    /// <summary>Asks the window to put the keyboard into the selected tab's focused pane.</summary>
    public event Action? TerminalFocusRequested;

    /// <summary>Opens one server in a new tab.</summary>
    public void Connect(ServerProfile profile) => ConnectTogether(new[] { profile });

    /// <summary>Opens several servers as panes of one new tab (side by side, or a grid for 4+).</summary>
    public void ConnectTogether(IReadOnlyList<ServerProfile> profiles)
    {
        if (IsLocked || profiles.Count == 0)
            return;
        var sessions = new List<SshSession>();
        foreach (var profile in profiles)
        {
            if (OpenSession(profile) is not { } session)
            {
                foreach (var opened in sessions)
                    _ = _sessions.CloseAsync(opened);
                return;
            }
            sessions.Add(session);
        }
        var tab = new TerminalTabViewModel(this, sessions);
        Tabs.Add(tab);
        SelectedTab = tab;
        foreach (var session in sessions)
            session.Start();
        OnPropertyChanged(nameof(LiveSessionNote));
        TerminalFocusRequested?.Invoke();
    }

    /// <summary>
    /// Splits <paramref name="pane"/>: Horizontal puts the new pane to the right, Vertical below.
    /// With no profile, the new pane connects to the same server.
    /// </summary>
    public void Split(TerminalTabViewModel tab, TerminalPaneViewModel pane, Orientation orientation, ServerProfile? profile = null)
    {
        if (IsLocked)
            return;
        profile ??= SavedProfile(pane.Session.Profile.Id) ?? pane.Session.Profile;
        if (OpenSession(profile) is not { } session)
            return;
        tab.AddSplit(pane, orientation, session);
        session.Start();
        OnPropertyChanged(nameof(LiveSessionNote));
        TerminalFocusRequested?.Invoke();
    }

    /// <summary>Saved servers, for the "split with…" menus.</summary>
    public IReadOnlyList<ServerProfile> SavedServers =>
        _data?.Servers.OrderBy(s => s.Group, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase).ToList()
        ?? new List<ServerProfile>();

    private ServerProfile? SavedProfile(Guid id) => _data?.Servers.FirstOrDefault(s => s.Id == id);

    private SshSession? OpenSession(ServerProfile profile)
    {
        if (_data == null)
            return null;
        try
        {
            return _sessions.Open(profile, JumpHostResolver.Resolve(profile, _data.Servers));
        }
        catch (JumpHostConfigurationException ex)
        {
            _ = _dialogs.AlertAsync("Can't connect", ex.Message);
            return null;
        }
    }

    // ---------------------------------------------------------------- file browser tabs

    /// <summary>Opens the dual-pane file browser for a server (SSH, SFTP-only or S3).</summary>
    public void OpenFiles(ServerProfile profile)
    {
        if (IsLocked || _data == null)
            return;
        // Private copies: the tab keeps working (and reconnecting) while the vault is locked.
        var target = profile.Clone();
        IReadOnlyList<ServerProfile> jumpHosts;
        try
        {
            jumpHosts = target.Kind == ServerKind.S3
                ? Array.Empty<ServerProfile>()
                : JumpHostResolver.Resolve(target, _data.Servers).Select(j => j.Clone()).ToList();
        }
        catch (JumpHostConfigurationException ex)
        {
            _ = _dialogs.AlertAsync("Can't browse files", ex.Message);
            return;
        }

        var connector = _sessions.CreateConnector();
        connector.HostKeyTrusted += (serverId, fingerprint) =>
            Dispatcher.UIThread.Post(() => RememberHostKey(serverId, fingerprint));
        IFileSystem Create() => target.Kind == ServerKind.S3
            ? CreateS3FileSystem(target)
            : new SftpFileSystem(target.DisplayName, token => connector.ConnectSftpAsync(target, jumpHosts, token));

        // Browsing and transfers get separate connections, so big transfers can't stall browsing.
        var tab = new FilesTabViewModel(this, target, Create(), Create(), _dialogs);
        Tabs.Add(tab);
        SelectedTab = tab;
        _ = tab.InitializeAsync();
    }

    private static IFileSystem CreateS3FileSystem(ServerProfile profile) => new S3FileSystem(profile);

    public async Task CloseFilesTabAsync(FilesTabViewModel tab)
    {
        // The live count (the tab's own figure refreshes on a timer and can lag a finished transfer).
        var running = tab.Queue.ActiveCount;
        if (running > 0 && !await _dialogs.ConfirmAsync("Close file browser",
                $"{running} transfer(s) are still running. Cancel them and close?", "Cancel transfers and close", danger: true))
            return;
        RemoveTab(tab);
        await tab.ShutdownAsync();
        OnTransfersChanged();
    }

    /// <summary>Transfers running in all file browser tabs (live, for the header and quit warning).</summary>
    public int ActiveTransfers => Tabs.OfType<FilesTabViewModel>().Sum(t => t.Queue.ActiveCount);

    public string TransfersLabel => $"⇅ {ActiveTransfers} transfer(s)";

    public void OnTransfersChanged()
    {
        OnPropertyChanged(nameof(ActiveTransfers));
        OnPropertyChanged(nameof(TransfersLabel));
    }

    /// <summary>Header indicator: jump to a tab with transfers running.</summary>
    [RelayCommand]
    private void ShowTransfers()
    {
        var tabs = Tabs.OfType<FilesTabViewModel>().Where(t => t.ActiveTransfers > 0).ToList();
        if (tabs.Count == 0)
            return;
        var next = tabs.FirstOrDefault(t => Tabs.IndexOf(t) > Tabs.IndexOf(SelectedTab!)) ?? tabs[0];
        SelectedTab = next;
    }

    // ---------------------------------------------------------------- combining tabs

    /// <summary>The selected terminal tab plus any Ctrl+clicked ones, in tab order.</summary>
    public IReadOnlyList<TerminalTabViewModel> CombineCandidates =>
        Tabs.OfType<TerminalTabViewModel>().Where(t => t.IsMarked || t == SelectedTab).ToList();

    public bool CanCombine => CombineCandidates.Count >= 2;

    public string CombineLabel => $"Combine {CombineCandidates.Count} tabs into a split";

    /// <summary>Ctrl+click on a tab header: mark or unmark it for combining.</summary>
    public void ToggleMark(TerminalTabViewModel tab)
    {
        if (tab == SelectedTab)
            return; // the selected tab is always included
        tab.IsMarked = !tab.IsMarked;
        OnCombineChanged();
    }

    public void ClearMarks()
    {
        foreach (var tab in Tabs)
            tab.IsMarked = false;
        OnCombineChanged();
    }

    partial void OnSelectedTabChanged(TabViewModel? value)
    {
        if (value != null && !_closingTab)
        {
            _tabHistory.Remove(value);
            _tabHistory.Add(value);
        }
        OnCombineChanged();
    }

    // Tabs in the order they were last used, most recent last (for "back to the previous tab").
    private readonly List<TabViewModel> _tabHistory = new();
    private bool _closingTab;

    /// <summary>
    /// Removes a tab and, if it was showing, goes back to the tab used before it (not just its
    /// neighbour, which was often the Servers tab).
    /// </summary>
    private void RemoveTab(TabViewModel tab)
    {
        var wasSelected = SelectedTab == tab || SelectedTab == null;
        _tabHistory.Remove(tab);
        var previous = _tabHistory.LastOrDefault(t => Tabs.Contains(t) && t != tab);
        var index = Tabs.IndexOf(tab);
        _closingTab = true;
        try
        {
            Tabs.Remove(tab);
        }
        finally
        {
            _closingTab = false;
        }
        if (wasSelected || SelectedTab == null || !Tabs.Contains(SelectedTab))
            SelectedTab = previous ?? (Tabs.Count > 0 ? Tabs[Math.Clamp(index - 1, 0, Tabs.Count - 1)] : null);
    }

    private void OnCombineChanged()
    {
        OnPropertyChanged(nameof(CanCombine));
        OnPropertyChanged(nameof(CombineLabel));
        CombineMarkedCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanCombine))]
    private void CombineMarked() => CombineTabs(CombineCandidates);

    /// <summary>Merges tabs into one split tab (each keeps its layout). No session is reconnected.</summary>
    public void CombineTabs(IReadOnlyList<TerminalTabViewModel> tabs)
    {
        var ordered = Tabs.OfType<TerminalTabViewModel>().Where(tabs.Contains).ToList();
        if (ordered.Count < 2)
            return;
        var focusFrom = SelectedTab as TerminalTabViewModel;
        var combined = TerminalTabViewModel.Combine(this, ordered, ordered.Contains(focusFrom!) ? focusFrom : null);
        var index = Tabs.IndexOf(ordered[0]);
        ClearMarks();
        foreach (var tab in ordered)
        {
            tab.Detach();
            Tabs.Remove(tab);
        }
        Tabs.Insert(index, combined);
        SelectedTab = combined;
        RefreshAppearance();
        TerminalFocusRequested?.Invoke();
    }

    /// <summary>Moves one pane out into its own tab, next to the current one. No reconnect.</summary>
    public void MovePaneToNewTab(TerminalTabViewModel tab, TerminalPaneViewModel pane)
    {
        if (tab.Panes.Count <= 1)
            return;
        tab.RemovePane(pane);
        var single = new TerminalTabViewModel(this, new[] { pane.Session });
        Tabs.Insert(Tabs.IndexOf(tab) + 1, single);
        SelectedTab = single;
        RefreshAppearance();
        TerminalFocusRequested?.Invoke();
    }

    /// <summary>Turns every pane of a split tab into its own tab. No reconnect.</summary>
    public void SeparatePanes(TerminalTabViewModel tab)
    {
        if (tab.Panes.Count <= 1)
            return;
        var index = Tabs.IndexOf(tab);
        var sessions = tab.Panes.Select(p => p.Session).ToList();
        tab.Detach();
        Tabs.Remove(tab);
        TerminalTabViewModel? first = null;
        foreach (var session in sessions)
        {
            var single = new TerminalTabViewModel(this, new[] { session });
            Tabs.Insert(index++, single);
            first ??= single;
        }
        SelectedTab = first;
        RefreshAppearance();
        TerminalFocusRequested?.Invoke();
    }

    public async Task ClosePaneAsync(TerminalTabViewModel tab, TerminalPaneViewModel pane)
    {
        if (tab.Panes.Count <= 1)
        {
            await CloseTabAsync(tab);
            return;
        }
        if (pane.Session.State == SessionState.Connected &&
            !await _dialogs.ConfirmAsync("Close pane", $"Disconnect from {pane.Title}?", "Disconnect"))
            return;
        tab.RemovePane(pane);
        TerminalFocusRequested?.Invoke();
        await _sessions.CloseAsync(pane.Session);
        OnPropertyChanged(nameof(LiveSessionNote));
    }

    public async Task CloseTabAsync(TerminalTabViewModel tab)
    {
        var connected = tab.Panes.Count(p => p.Session.State == SessionState.Connected);
        if (connected > 0 && !await _dialogs.ConfirmAsync("Close tab",
                connected == 1 ? $"Disconnect from {tab.Title}?" : $"Disconnect all {connected} sessions in this tab?", "Disconnect"))
            return;

        RemoveTab(tab);
        tab.Detach();
        OnCombineChanged();
        await Task.WhenAll(tab.Panes.Select(p => _sessions.CloseAsync(p.Session)));
        OnPropertyChanged(nameof(LiveSessionNote));
    }

    /// <summary>What to do when this server's shell exits: its own choice, else the default from settings.</summary>
    public ShellExitAction ResolveShellExitAction(ServerProfile profile) =>
        (SavedProfile(profile.Id) ?? profile).OnShellExit ?? Settings.OnShellExit;

    /// <summary>The theme for a server: its own choice, else the default from settings.</summary>
    public TerminalTheme ResolveTheme(ServerProfile profile) =>
        TerminalTheme.Find(profile.ThemeName ?? Settings.DefaultTheme);

    /// <summary>Re-applies theme and font to open tabs after a server or settings change.</summary>
    public void RefreshAppearance()
    {
        if (_data == null)
            return;
        var font = FontFamily.Parse(Settings.FontFamily);
        foreach (var pane in Tabs.OfType<TerminalTabViewModel>().SelectMany(t => t.Panes))
        {
            var saved = SavedProfile(pane.Session.Profile.Id) ?? pane.Session.Profile;
            pane.ApplyAppearance(ResolveTheme(saved), font, Settings.FontSize);
        }
    }

    public void SelectRelativeTab(int delta)
    {
        if (Tabs.Count == 0)
            return;
        var index = SelectedTab == null ? 0 : Tabs.IndexOf(SelectedTab);
        SelectedTab = Tabs[((index + delta) % Tabs.Count + Tabs.Count) % Tabs.Count];
    }

    /// <summary>Selects the tab at <paramref name="index"/>; -1 selects the last tab.</summary>
    public void SelectTabAt(int index)
    {
        if (Tabs.Count == 0)
            return;
        SelectedTab = Tabs[index < 0 ? Tabs.Count - 1 : Math.Min(index, Tabs.Count - 1)];
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
