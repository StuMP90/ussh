using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Ussh.App.Controls;

/// <summary>Two contents side by side (Horizontal) or stacked (Vertical) with a draggable divider.</summary>
public sealed class SplitPanel : Grid
{
    public static readonly StyledProperty<Orientation> OrientationProperty =
        AvaloniaProperty.Register<SplitPanel, Orientation>(nameof(Orientation));

    public static readonly StyledProperty<object?> FirstProperty =
        AvaloniaProperty.Register<SplitPanel, object?>(nameof(First));

    public static readonly StyledProperty<object?> SecondProperty =
        AvaloniaProperty.Register<SplitPanel, object?>(nameof(Second));

    public static readonly StyledProperty<double> RatioProperty =
        AvaloniaProperty.Register<SplitPanel, double>(nameof(Ratio), 0.5);

    private const double DividerSize = 5;
    private readonly ContentControl _first = new();
    private readonly ContentControl _second = new();
    private readonly GridSplitter _divider = new() { Background = new SolidColorBrush(Color.Parse("#30FFFFFF")) };

    public SplitPanel()
    {
        Children.Add(_first);
        Children.Add(_divider);
        Children.Add(_second);
        UpdateLayout();
    }

    public Orientation Orientation
    {
        get => GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    public object? First
    {
        get => GetValue(FirstProperty);
        set => SetValue(FirstProperty, value);
    }

    public object? Second
    {
        get => GetValue(SecondProperty);
        set => SetValue(SecondProperty, value);
    }

    public double Ratio
    {
        get => GetValue(RatioProperty);
        set => SetValue(RatioProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FirstProperty)
            _first.Content = First;
        else if (change.Property == SecondProperty)
            _second.Content = Second;
        else if (change.Property == OrientationProperty || change.Property == RatioProperty)
            UpdateLayout();
    }

    private new void UpdateLayout()
    {
        var ratio = Math.Clamp(Ratio, 0.05, 0.95);
        RowDefinitions.Clear();
        ColumnDefinitions.Clear();
        var definitions = new[]
        {
            new GridLength(ratio, GridUnitType.Star),
            new GridLength(DividerSize),
            new GridLength(1 - ratio, GridUnitType.Star),
        };
        var horizontal = Orientation == Orientation.Horizontal;
        foreach (var length in definitions)
        {
            if (horizontal)
                ColumnDefinitions.Add(new ColumnDefinition(length) { MinWidth = length.IsStar ? 120 : 0 });
            else
                RowDefinitions.Add(new RowDefinition(length) { MinHeight = length.IsStar ? 60 : 0 });
        }
        Control[] order = { _first, _divider, _second };
        for (var i = 0; i < order.Length; i++)
        {
            SetColumn(order[i], horizontal ? i : 0);
            SetRow(order[i], horizontal ? 0 : i);
        }
        _divider.ResizeDirection = horizontal ? GridResizeDirection.Columns : GridResizeDirection.Rows;
        _divider.Cursor = new Cursor(horizontal ? StandardCursorType.SizeWestEast : StandardCursorType.SizeNorthSouth);
    }
}
