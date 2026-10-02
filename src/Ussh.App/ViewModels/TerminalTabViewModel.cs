using System.Collections.ObjectModel;
using Avalonia.Layout;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ussh.Core.Ssh;

namespace Ussh.App.ViewModels;

/// <summary>
/// A terminal tab: one or more panes arranged as a tree of splits. Each pane is an independent
/// session (own reconnect, theme and status); the tab adds focus tracking and broadcast input.
/// </summary>
public sealed partial class TerminalTabViewModel : TabViewModel
{
    private static readonly IBrush ConnectedBrush = new SolidColorBrush(Color.Parse("#40C48C"));
    private static readonly IBrush PendingBrush = new SolidColorBrush(Color.Parse("#E0B040"));
    private static readonly IBrush DownBrush = new SolidColorBrush(Color.Parse("#E05555"));

    /// <param name="sessions">One pane per session: 2–3 side by side, more as a grid.</param>
    public TerminalTabViewModel(MainWindowViewModel main, IReadOnlyList<SshSession> sessions) : this(main)
    {
        if (sessions.Count == 0)
            throw new ArgumentException("A tab needs at least one session.", nameof(sessions));
        foreach (var session in sessions)
            Panes.Add(new TerminalPaneViewModel(session, this, main));
        Finish(BuildGrid(Panes), Panes[0]);
    }

    private TerminalTabViewModel(MainWindowViewModel main)
    {
        Main = main;
        CloseTabCommand = new AsyncRelayCommand(() => Main.CloseTabAsync(this));
        _root = null!;
        _focusedPane = null!;
    }

    /// <summary>
    /// A new tab holding the panes of <paramref name="tabs"/> side by side, each keeping its own
    /// layout. Sessions are re-hosted, not reconnected: screens, scrollback and connections carry on.
    /// The old tabs must be detached and removed by the caller.
    /// </summary>
    public static TerminalTabViewModel Combine(MainWindowViewModel main, IReadOnlyList<TerminalTabViewModel> tabs, TerminalTabViewModel? focusFrom = null)
    {
        var combined = new TerminalTabViewModel(main);
        var focusSession = (focusFrom ?? tabs[0]).FocusedPane.Session;
        var roots = tabs.Select(t => combined.Rehost(t.Root)).ToList();
        combined.Finish(Chain(roots, Orientation.Horizontal),
            combined.Panes.FirstOrDefault(p => p.Session == focusSession) ?? combined.Panes[0]);
        return combined;
    }

    private void Finish(LayoutNode root, TerminalPaneViewModel focus)
    {
        Root = root;
        FocusedPane = focus;
        focus.IsFocused = true;
        OnLayoutChanged();
    }

    /// <summary>Copies a layout from another tab, with new pane view models for the same sessions.</summary>
    private LayoutNode Rehost(LayoutNode node)
    {
        switch (node)
        {
            case TerminalPaneViewModel pane:
                var copy = new TerminalPaneViewModel(pane.Session, this, Main);
                Panes.Add(copy);
                return copy;
            case SplitViewModel split:
                return new SplitViewModel(split.Orientation, Rehost(split.First), Rehost(split.Second), split.Ratio);
            default:
                throw new InvalidOperationException();
        }
    }

    public MainWindowViewModel Main { get; }
    public ObservableCollection<TerminalPaneViewModel> Panes { get; } = new();
    public override bool CanClose => true;
    public override IAsyncRelayCommand CloseTabCommand { get; }

    [ObservableProperty] private LayoutNode _root;

    [ObservableProperty] private TerminalPaneViewModel _focusedPane;

    /// <summary>When on, typed and pasted text goes to every pane in the tab.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PaneBarText))]
    private bool _isBroadcasting;

    public bool HasMultiplePanes => Panes.Count > 1;

    public string PaneBarText => IsBroadcasting
        ? $"BROADCASTING: typing goes to all {Panes.Count} panes"
        : $"{Panes.Count} panes";

    partial void OnFocusedPaneChanged(TerminalPaneViewModel? oldValue, TerminalPaneViewModel newValue)
    {
        if (oldValue != null)
            oldValue.IsFocused = false;
        if (newValue != null)
            newValue.IsFocused = true;
    }

    partial void OnIsBroadcastingChanged(bool value)
    {
        foreach (var pane in Panes)
            pane.RefreshHighlight();
    }

    [RelayCommand]
    private void ToggleBroadcast() => IsBroadcasting = !IsBroadcasting && HasMultiplePanes;

    public void SendInput(TerminalPaneViewModel from, string text)
    {
        foreach (var pane in IsBroadcasting ? Panes.ToList() : new List<TerminalPaneViewModel> { from })
        {
            pane.Session.Emulator.ScrollToBottom();
            pane.Session.Send(text);
        }
    }

    /// <summary>Splits <paramref name="target"/>, putting a new pane for <paramref name="session"/> right of or below it.</summary>
    public TerminalPaneViewModel AddSplit(TerminalPaneViewModel target, Orientation orientation, SshSession session)
    {
        var pane = new TerminalPaneViewModel(session, this, Main);
        var parent = target.Parent;
        var split = new SplitViewModel(orientation, target, pane);
        if (parent == null)
        {
            split.Parent = null;
            Root = split;
        }
        else
        {
            parent.Replace(target, split);
        }
        Panes.Add(pane);
        FocusedPane = pane;
        OnLayoutChanged();
        return pane;
    }

    /// <summary>Removes a pane (not the last one) and lets its sibling take the space.</summary>
    public void RemovePane(TerminalPaneViewModel pane)
    {
        if (Panes.Count <= 1 || !Panes.Contains(pane))
            return;
        var parent = pane.Parent!;
        var sibling = parent.Other(pane);
        var grandparent = parent.Parent;
        if (grandparent == null)
        {
            sibling.Parent = null;
            Root = sibling;
        }
        else
        {
            grandparent.Replace(parent, sibling);
        }
        Panes.Remove(pane);
        pane.Detach();
        if (FocusedPane == pane)
            FocusedPane = FirstPane(sibling);
        if (!HasMultiplePanes)
            IsBroadcasting = false;
        OnLayoutChanged();
    }

    public void Detach()
    {
        foreach (var pane in Panes)
            pane.Detach();
    }

    /// <summary>Called by panes when their session state changes (on the UI thread).</summary>
    public void OnPaneStateChanged()
    {
        Title = Panes.Count switch
        {
            0 => "",
            1 => Panes[0].Title,
            2 => $"{Panes[0].Title} | {Panes[1].Title}",
            _ => $"{Panes[0].Title} +{Panes.Count - 1}",
        };
        var states = Panes.Select(p => p.Session.State).ToList();
        Indicator = states.All(s => s == SessionState.Connected) ? ConnectedBrush
            : states.Any(s => s is SessionState.Connecting or SessionState.Reconnecting) ? PendingBrush
            : DownBrush;
    }

    private void OnLayoutChanged()
    {
        OnPropertyChanged(nameof(HasMultiplePanes));
        OnPropertyChanged(nameof(PaneBarText));
        foreach (var pane in Panes)
            pane.RefreshHighlight();
        OnPaneStateChanged();
    }

    private static TerminalPaneViewModel FirstPane(LayoutNode node) => node switch
    {
        TerminalPaneViewModel pane => pane,
        SplitViewModel split => FirstPane(split.First),
        _ => throw new InvalidOperationException(),
    };

    /// <summary>2–3 panes side by side; more as a near-square grid of rows.</summary>
    private static LayoutNode BuildGrid(IReadOnlyList<TerminalPaneViewModel> panes)
    {
        var columns = panes.Count <= 3 ? panes.Count : (int)Math.Ceiling(Math.Sqrt(panes.Count));
        var rows = panes.Chunk(columns).Select(row => Chain(row.Cast<LayoutNode>().ToList(), Orientation.Horizontal)).ToList();
        return Chain(rows, Orientation.Vertical);
    }

    /// <summary>Nests binary splits so that every item gets an equal share.</summary>
    private static LayoutNode Chain(IReadOnlyList<LayoutNode> nodes, Orientation orientation) =>
        nodes.Count == 1
            ? nodes[0]
            : new SplitViewModel(orientation, nodes[0], Chain(nodes.Skip(1).ToList(), orientation), 1.0 / nodes.Count);
}
