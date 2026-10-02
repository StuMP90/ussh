using System.Collections.Concurrent;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using XColor = XtermSharp.Color;

namespace Ussh.App.Controls;

/// <summary>Immutable brushes for one theme's 256-colour palette plus defaults; cached per theme.</summary>
internal sealed class TerminalPalette
{
    public const int DefaultColor = 256;
    public const int InvertedDefaultColor = 257;

    private static readonly ConcurrentDictionary<string, TerminalPalette> Cache = new();

    // Separate foreground/background sets: monochrome themes render explicit background colours
    // dimmer than text, so text drawn on them stays readable.
    private readonly IImmutableSolidColorBrush[] _foreground = new IImmutableSolidColorBrush[256];
    private readonly IImmutableSolidColorBrush[] _background = new IImmutableSolidColorBrush[256];
    private readonly IImmutableSolidColorBrush[] _dim = new IImmutableSolidColorBrush[258];

    public static TerminalPalette Default => For(TerminalTheme.Default);

    public static TerminalPalette For(TerminalTheme theme) => Cache.GetOrAdd(theme.Name, _ => new TerminalPalette(theme));

    private TerminalPalette(TerminalTheme theme)
    {
        Foreground = new ImmutableSolidColorBrush(theme.Foreground);
        Background = new ImmutableSolidColorBrush(theme.Background);
        Cursor = new ImmutableSolidColorBrush(theme.Cursor);
        Selection = new ImmutableSolidColorBrush(theme.Selection);

        var xterm = XColor.DefaultAnsiColors;
        for (var i = 0; i < 256; i++)
        {
            var color = i < 16 ? theme.Ansi[i] : Color.FromRgb(xterm[i].Red, xterm[i].Green, xterm[i].Blue);
            if (theme.MonochromeTint is { } tint)
            {
                var luminance = (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255;
                _foreground[i] = new ImmutableSolidColorBrush(Mix(theme.Background, tint, 0.5 + 0.5 * luminance));
                _background[i] = new ImmutableSolidColorBrush(Mix(theme.Background, tint, 0.45 * luminance));
            }
            else
            {
                _foreground[i] = _background[i] = new ImmutableSolidColorBrush(color);
            }
            _dim[i] = new ImmutableSolidColorBrush(_foreground[i].Color, 0.55);
        }
        _dim[DefaultColor] = new ImmutableSolidColorBrush(Foreground.Color, 0.55);
        _dim[InvertedDefaultColor] = new ImmutableSolidColorBrush(Background.Color, 0.55);
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
                return _dim[index];
            return _dim[(index == DefaultColor) == isForeground ? DefaultColor : InvertedDefaultColor];
        }
        if (index < 256)
            return isForeground ? _foreground[index] : _background[index];
        var useForeground = (index == DefaultColor) == isForeground;
        return useForeground ? Foreground : Background;
    }

    private static Color Mix(Color from, Color to, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        byte Channel(byte a, byte b) => (byte)Math.Round(a + (b - a) * amount);
        return Color.FromRgb(Channel(from.R, to.R), Channel(from.G, to.G), Channel(from.B, to.B));
    }
}
