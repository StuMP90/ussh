using Avalonia.Media;
using Avalonia.Media.Immutable;
using XColor = XtermSharp.Color;

namespace Ussh.App.Controls;

/// <summary>Immutable brushes for the 256-colour palette plus defaults, shared by all terminals.</summary>
internal sealed class TerminalPalette
{
    public const int DefaultColor = 256;
    public const int InvertedDefaultColor = 257;

    public static TerminalPalette Default { get; } = new();

    private readonly IImmutableSolidColorBrush[] _brushes = new IImmutableSolidColorBrush[256];
    private readonly IImmutableSolidColorBrush[] _dimBrushes = new IImmutableSolidColorBrush[258];

    private TerminalPalette()
    {
        // Slightly softened standard 16 colours (easier on the eyes than pure xterm); 16-255 are the xterm cube.
        var base16 = new[]
        {
            "#1D1F28", "#E05561", "#8CC265", "#D18F52", "#4AA5F0", "#C162DE", "#42B3C2", "#D7DAE0",
            "#5C6370", "#FF616E", "#A5E075", "#F0A45D", "#4DC4FF", "#DE73FF", "#4CD1E0", "#FFFFFF",
        };
        var xterm = XColor.DefaultAnsiColors;
        for (var i = 0; i < 256; i++)
        {
            var color = i < 16 ? Color.Parse(base16[i]) : Color.FromRgb(xterm[i].Red, xterm[i].Green, xterm[i].Blue);
            _brushes[i] = new ImmutableSolidColorBrush(color);
        }
        Foreground = new ImmutableSolidColorBrush(Color.Parse("#D7DAE0"));
        Background = new ImmutableSolidColorBrush(Color.Parse("#15171E"));
        Cursor = new ImmutableSolidColorBrush(Color.Parse("#40C48C"));
        Selection = new ImmutableSolidColorBrush(Color.Parse("#3A5A8C"));
        for (var i = 0; i < 256; i++)
            _dimBrushes[i] = new ImmutableSolidColorBrush(_brushes[i].Color, 0.55);
        _dimBrushes[DefaultColor] = new ImmutableSolidColorBrush(Foreground.Color, 0.55);
        _dimBrushes[InvertedDefaultColor] = new ImmutableSolidColorBrush(Background.Color, 0.55);
    }

    public IImmutableSolidColorBrush Foreground { get; }
    public IImmutableSolidColorBrush Background { get; }
    public IImmutableSolidColorBrush Cursor { get; }
    public IImmutableSolidColorBrush Selection { get; }

    /// <param name="isForeground">Selects what "default colour" (256) means.</param>
    public IImmutableSolidColorBrush Resolve(int index, bool isForeground, bool dim = false)
    {
        if (index is < 0 or > InvertedDefaultColor)
            index = DefaultColor;
        if (dim)
        {
            if (index < 256)
                return _dimBrushes[index];
            return _dimBrushes[(index == DefaultColor) == isForeground ? DefaultColor : InvertedDefaultColor];
        }
        if (index < 256)
            return _brushes[index];
        var useForeground = (index == DefaultColor) == isForeground;
        return useForeground ? Foreground : Background;
    }
}
