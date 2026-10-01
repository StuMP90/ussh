using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ussh.Core.Ssh;

namespace Ussh.App.ViewModels;

public sealed partial class TerminalTabViewModel : TabViewModel
{
    private static readonly IBrush ConnectedBrush = new SolidColorBrush(Color.Parse("#40C48C"));
    private static readonly IBrush PendingBrush = new SolidColorBrush(Color.Parse("#E0B040"));
    private static readonly IBrush DownBrush = new SolidColorBrush(Color.Parse("#E05555"));

    private readonly MainWindowViewModel _main;
    private readonly DispatcherTimer _uptimeTimer;

    public TerminalTabViewModel(SshSession session, MainWindowViewModel main)
    {
        Session = session;
        _main = main;
        Title = session.Profile.DisplayName;
        FontFamily = FontFamily.Parse(main.Settings.FontFamily);
        FontSize = main.Settings.FontSize;
        CloseTabCommand = new AsyncRelayCommand(() => _main.CloseTabAsync(this));

        session.StateChanged += OnStateChanged;
        session.Emulator.TitleChanged += OnRemoteTitleReceived;
        _uptimeTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => OnPropertyChanged(nameof(Uptime)));
        _uptimeTimer.Start();
        Refresh();
    }

    public SshSession Session { get; }
    public FontFamily FontFamily { get; }
    public double FontSize { get; }
    public override bool CanClose => true;
    public override IAsyncRelayCommand CloseTabCommand { get; }

    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _showBanner;
    [ObservableProperty] private bool _canReconnect;
    [ObservableProperty] private string? _remoteTitle;
    [ObservableProperty] private string? _tunnelSummary;

    public string Endpoint => $"{Session.Profile.Username}@{Session.Profile.Host}:{Session.Profile.Port}";

    public string Uptime => Session.ConnectedSince is { } since
        ? "up " + FormatDuration(DateTimeOffset.Now - since)
        : "";

    [RelayCommand]
    private void Reconnect() => Session.Reconnect();

    [RelayCommand]
    private void Disconnect() => Session.Disconnect();

    /// <summary>Stops UI updates from a closed session.</summary>
    public void Detach()
    {
        _uptimeTimer.Stop();
        Session.StateChanged -= OnStateChanged;
        Session.Emulator.TitleChanged -= OnRemoteTitleReceived;
    }

    private void OnStateChanged(SshSession _) => Dispatcher.UIThread.Post(Refresh);

    private void OnRemoteTitleReceived(string title) => Dispatcher.UIThread.Post(() => RemoteTitle = title);

    private void Refresh()
    {
        var state = Session.State;
        StatusMessage = Session.StatusMessage;
        IsConnected = state == SessionState.Connected;
        ShowBanner = state is not (SessionState.Connected or SessionState.Closed);
        CanReconnect = state is SessionState.Disconnected or SessionState.Failed or SessionState.Reconnecting;
        Indicator = state switch
        {
            SessionState.Connected => ConnectedBrush,
            SessionState.Connecting or SessionState.Reconnecting => PendingBrush,
            _ => DownBrush,
        };
        var tunnels = Session.Tunnels;
        TunnelSummary = tunnels.Count == 0
            ? null
            : string.Join("   ", tunnels.Select(t => (t.Active ? "● " : "✕ ") + t.Definition.Describe() + (t.Error != null ? $" ({t.Error})" : "")));
        OnPropertyChanged(nameof(Uptime));
    }

    private static string FormatDuration(TimeSpan span) =>
        span.TotalDays >= 1 ? $"{(int)span.TotalDays}d {span.Hours}h {span.Minutes}m"
        : span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m"
        : $"{span.Minutes}m {span.Seconds}s";
}
