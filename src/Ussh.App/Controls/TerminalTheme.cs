using Avalonia.Media;

namespace Ussh.App.Controls;

/// <summary>
/// A terminal colour scheme: default colours plus the 16 ANSI colours (the 240 extended
/// colours are the standard xterm cube and greys). Monochrome themes emulate old phosphor
/// screens by mapping every colour to a brightness of a single tint.
/// </summary>
public sealed class TerminalTheme
{
    private TerminalTheme(string name, string foreground, string background, string cursor, string selection,
        string ansi, string? monochromeTint = null)
    {
        Name = name;
        Foreground = Color.Parse(foreground);
        Background = Color.Parse(background);
        Cursor = Color.Parse(cursor);
        Selection = Color.Parse(selection);
        Ansi = ansi.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(c => Color.Parse("#" + c)).ToArray();
        if (Ansi.Count != 16)
            throw new ArgumentException($"Theme {name} needs 16 ANSI colours.");
        MonochromeTint = monochromeTint == null ? null : Color.Parse(monochromeTint);
    }

    public string Name { get; }
    public Color Foreground { get; }
    public Color Background { get; }
    public Color Cursor { get; }
    public Color Selection { get; }
    public IReadOnlyList<Color> Ansi { get; }
    public Color? MonochromeTint { get; }

    public const string DefaultName = "zSSH Dark";

    private const string XtermAnsi = "000000 CD0000 00CD00 CDCD00 0000EE CD00CD 00CDCD E5E5E5 7F7F7F FF0000 00FF00 FFFF00 5C5CFF FF00FF 00FFFF FFFFFF";

    public static IReadOnlyList<TerminalTheme> All { get; } = new[]
    {
        new TerminalTheme(DefaultName, "#D7DAE0", "#15171E", "#40C48C", "#3A5A8C",
            "1D1F28 E05561 8CC265 D18F52 4AA5F0 C162DE 42B3C2 D7DAE0 5C6370 FF616E A5E075 F0A45D 4DC4FF DE73FF 4CD1E0 FFFFFF"),

        // Old school phosphor monitors: every colour becomes a shade of one tint.
        new TerminalTheme("Amber (P3 phosphor)", "#FFB000", "#120A00", "#FFB000", "#5A3A00", XtermAnsi, monochromeTint: "#FFB000"),
        new TerminalTheme("Green (P1 phosphor)", "#33FF66", "#011A07", "#33FF66", "#0E5A22", XtermAnsi, monochromeTint: "#33FF66"),
        new TerminalTheme("White (P4 phosphor)", "#DCE6F0", "#0A0C10", "#DCE6F0", "#3A4450", XtermAnsi, monochromeTint: "#DCE6F0"),

        new TerminalTheme("Campbell", "#CCCCCC", "#0C0C0C", "#FFFFFF", "#264F78",
            "0C0C0C C50F1F 13A10E C19C00 0037DA 881798 3A96DD CCCCCC 767676 E74856 16C60C F9F1A5 3B78FF B4009E 61D6D6 F2F2F2"),
        new TerminalTheme("Solarized Dark", "#839496", "#002B36", "#93A1A1", "#274642",
            "073642 DC322F 859900 B58900 268BD2 D33682 2AA198 EEE8D5 002B36 CB4B16 586E75 657B83 839496 6C71C4 93A1A1 FDF6E3"),
        new TerminalTheme("Solarized Light", "#657B83", "#FDF6E3", "#586E75", "#EEE8D5",
            "073642 DC322F 859900 B58900 268BD2 D33682 2AA198 EEE8D5 002B36 CB4B16 586E75 657B83 839496 6C71C4 93A1A1 FDF6E3"),
        new TerminalTheme("Dracula", "#F8F8F2", "#282A36", "#F8F8F2", "#44475A",
            "21222C FF5555 50FA7B F1FA8C BD93F9 FF79C6 8BE9FD F8F8F2 6272A4 FF6E6E 69FF94 FFFFA5 D6ACFF FF92DF A4FFFF FFFFFF"),
        new TerminalTheme("Nord", "#D8DEE9", "#2E3440", "#D8DEE9", "#434C5E",
            "3B4252 BF616A A3BE8C EBCB8B 81A1C1 B48EAD 88C0D0 E5E9F0 4C566A BF616A A3BE8C EBCB8B 81A1C1 B48EAD 8FBCBB ECEFF4"),
        new TerminalTheme("Gruvbox Dark", "#EBDBB2", "#282828", "#EBDBB2", "#504945",
            "282828 CC241D 98971A D79921 458588 B16286 689D6A A89984 928374 FB4934 B8BB26 FABD2F 83A598 D3869B 8EC07C EBDBB2"),
        new TerminalTheme("Monokai", "#F8F8F2", "#272822", "#F8F8F0", "#49483E",
            "272822 F92672 A6E22E F4BF75 66D9EF AE81FF A1EFE4 F8F8F2 75715E F92672 A6E22E F4BF75 66D9EF AE81FF A1EFE4 F9F8F5"),
        new TerminalTheme("One Dark", "#ABB2BF", "#282C34", "#528BFF", "#3E4451",
            "282C34 E06C75 98C379 E5C07B 61AFEF C678DD 56B6C2 ABB2BF 5C6370 E06C75 98C379 E5C07B 61AFEF C678DD 56B6C2 FFFFFF"),
        new TerminalTheme("Tomorrow Night", "#C5C8C6", "#1D1F21", "#C5C8C6", "#373B41",
            "1D1F21 CC6666 B5BD68 F0C674 81A2BE B294BB 8ABEB7 C5C8C6 969896 CC6666 B5BD68 F0C674 81A2BE B294BB 8ABEB7 FFFFFF"),
        new TerminalTheme("One Light", "#383A42", "#FAFAFA", "#526FFF", "#D7DAE0",
            "383A42 E45649 50A14F C18401 4078F2 A626A4 0184BC A0A1A7 696C77 E45649 50A14F C18401 4078F2 A626A4 0184BC 202227"),
        new TerminalTheme("High Contrast", "#FFFFFF", "#000000", "#FFFF00", "#0050A0",
            "000000 FF5555 55FF55 FFFF55 5C8CFF FF55FF 55FFFF E5E5E5 9A9A9A FF7777 77FF77 FFFF99 8CB0FF FF99FF 99FFFF FFFFFF"),
    };

    public static TerminalTheme Default => All[0];

    /// <summary>Theme by name, falling back to the default (e.g. a theme removed in an update).</summary>
    public static TerminalTheme Find(string? name) =>
        All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) ?? Default;

    public override string ToString() => Name;
}
