using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ussh.App.Controls;
using Ussh.Core.Ssh;

namespace Ussh.App.ViewModels;

/// <summary>One terminal (one SSH session) inside a tab. A tab holds one or more panes.</summary>
public sealed partial class TerminalPaneViewModel : LayoutNode
{
    private static readonly IBrush ConnectedBrush = new SolidColorBrush(Color.Parse("#40C48C"));
    private static readonly IBrush PendingBrush = new SolidColorBrush(Color.Parse("#E0B040"));
    private static readonly IBrush DownBrush = new SolidColorBrush(Color.Parse("#E05555"));
    private static readonly IBrush FocusBrush = new SolidColorBrush(Color.Parse("#4A90E2"));
    private static readonly IBrush BroadcastBrush = new SolidColorBrush(Color.Parse("#F0A030"));

    private readonly DispatcherTimer _uptimeTimer;

    public TerminalPaneViewModel(SshSession session, TerminalTabViewModel tab, MainWindowViewModel main)
    {
        Session = session;
        Tab = tab;
        Title = session.Profile.DisplayName;
        SendInput = text => Tab.SendInput(this, text);
        _fontFamily = FontFamily.Parse(main.Settings.FontFamily);
        _fontSize = main.Settings.FontSize;
        _theme = main.ResolveTheme(session.Profile);

        session.StateChanged += OnStateChanged;
        session.Emulator.TitleChanged += OnRemoteTitleReceived;
        _uptimeTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => OnPropertyChanged(nameof(Uptime)));
        _uptimeTimer.Start();
        Refresh();
    }

    public SshSession Session { get; }
    public TerminalTabViewModel Tab { get; }
    public string Title { get; }

    /// <summary>Where typed and pasted text goes: this pane, or every pane while broadcasting.</summary>
    public Action<string> SendInput { get; }

    [ObservableProperty] private IBrush? _indicator;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Highlight))]
    private bool _isFocused;

    /// <summary>Pane border: accent when focused among several panes, amber on every pane while broadcasting.</summary>
    public IBrush Highlight => Tab.IsBroadcasting ? BroadcastBrush
        : IsFocused && Tab.HasMultiplePanes ? FocusBrush
        : Brushes.Transparent;

    public void RefreshHighlight() => OnPropertyChanged(nameof(Highlight));
    [ObservableProperty] private FontFamily _fontFamily;
    [ObservableProperty] private double _fontSize;
    [ObservableProperty] private TerminalTheme _theme;

    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _showBanner;
    [ObservableProperty] private bool _canReconnect;
    [ObservableProperty] private string? _remoteTitle;
    [ObservableProperty] private string? _tunnelSummary;

    public string Endpoint => $"{Session.Profile.Username}@{Session.Route}";

    public void ApplyAppearance(TerminalTheme theme, FontFamily fontFamily, double fontSize)
    {
        Theme = theme;
        FontFamily = fontFamily;
        FontSize = fontSize;
    }

    public string Uptime => Session.ConnectedSince is { } since
        ? "up " + FormatDuration(DateTimeOffset.Now - since)
        : "";

    [RelayCommand]
    private void Reconnect() => Session.Reconnect();

    [RelayCommand]
    private void Disconnect() => Session.Disconnect();

    [RelayCommand]
    private Task CloseAsync() => Tab.Main.ClosePaneAsync(Tab, this);

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
        Tab.OnPaneStateChanged();
    }

    private static string FormatDuration(TimeSpan span) =>
        span.TotalDays >= 1 ? $"{(int)span.TotalDays}d {span.Hours}h {span.Minutes}m"
        : span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m"
        : $"{span.Minutes}m {span.Seconds}s";
}
