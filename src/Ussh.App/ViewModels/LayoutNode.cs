using Avalonia.Layout;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Ussh.App.ViewModels;

/// <summary>A node in a tab's pane layout: a terminal pane or a split of two nodes.</summary>
public abstract class LayoutNode : ObservableObject
{
    public SplitViewModel? Parent { get; internal set; }
}

/// <summary>Two nodes side by side (Horizontal) or stacked (Vertical).</summary>
public sealed partial class SplitViewModel : LayoutNode
{
    public SplitViewModel(Orientation orientation, LayoutNode first, LayoutNode second, double ratio = 0.5)
    {
        Orientation = orientation;
        _ratio = ratio;
        _first = Adopt(first);
        _second = Adopt(second);
    }

    public Orientation Orientation { get; }

    [ObservableProperty] private LayoutNode _first;
    [ObservableProperty] private LayoutNode _second;

    /// <summary>Share of the space given to <see cref="First"/> (0–1).</summary>
    [ObservableProperty] private double _ratio;

    public void Replace(LayoutNode old, LayoutNode replacement)
    {
        if (ReferenceEquals(First, old))
            First = Adopt(replacement);
        else if (ReferenceEquals(Second, old))
            Second = Adopt(replacement);
        else
            throw new InvalidOperationException("Node is not a child of this split.");
    }

    public LayoutNode Other(LayoutNode child) => ReferenceEquals(First, child) ? Second : First;

    private LayoutNode Adopt(LayoutNode node)
    {
        node.Parent = this;
        return node;
    }
}
