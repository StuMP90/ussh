using Avalonia.Input;

namespace Ussh.App.Controls;

/// <summary>Maps non-text keys to the byte sequences an xterm sends.</summary>
internal static class KeyEncoder
{
    private const string Esc = "\u001b";

    /// <summary>Returns the sequence for <paramref name="key"/>, or null if it should arrive as text input.</summary>
    public static string? Encode(Key key, KeyModifiers modifiers, bool applicationCursor)
    {
        var shift = modifiers.HasFlag(KeyModifiers.Shift);
        var alt = modifiers.HasFlag(KeyModifiers.Alt);
        var ctrl = modifiers.HasFlag(KeyModifiers.Control);
        // xterm modifier parameter: 1 + shift(1) + alt(2) + ctrl(4)
        var mod = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0);

        string Cursor(char final) =>
            mod > 1 ? $"{Esc}[1;{mod}{final}" : applicationCursor ? $"{Esc}O{final}" : $"{Esc}[{final}";
        string Tilde(int code) => mod > 1 ? $"{Esc}[{code};{mod}~" : $"{Esc}[{code}~";
        string Ss3(char final) => mod > 1 ? $"{Esc}[1;{mod}{final}" : $"{Esc}O{final}";

        switch (key)
        {
            case Key.Up: return Cursor('A');
            case Key.Down: return Cursor('B');
            case Key.Right: return Cursor('C');
            case Key.Left: return Cursor('D');
            case Key.Home: return Cursor('H');
            case Key.End: return Cursor('F');
            case Key.Insert: return Tilde(2);
            case Key.Delete: return Tilde(3);
            case Key.PageUp: return Tilde(5);
            case Key.PageDown: return Tilde(6);
            case Key.F1: return Ss3('P');
            case Key.F2: return Ss3('Q');
            case Key.F3: return Ss3('R');
            case Key.F4: return Ss3('S');
            case Key.F5: return Tilde(15);
            case Key.F6: return Tilde(17);
            case Key.F7: return Tilde(18);
            case Key.F8: return Tilde(19);
            case Key.F9: return Tilde(20);
            case Key.F10: return Tilde(21);
            case Key.F11: return Tilde(23);
            case Key.F12: return Tilde(24);
            case Key.Enter: return alt ? Esc + "\r" : "\r";
            case Key.Back: return ctrl ? "\b" : alt ? Esc + "\u007f" : "\u007f";
            case Key.Tab: return shift ? $"{Esc}[Z" : "\t";
            case Key.Escape: return Esc;
        }

        // AltGr is reported as Ctrl+Alt on Windows; leave those for text input.
        if (ctrl && !alt)
        {
            if (key is >= Key.A and <= Key.Z)
                return ((char)(key - Key.A + 1)).ToString();
            switch (key)
            {
                case Key.Space:
                case Key.D2: return "\0";
                case Key.OemOpenBrackets: case Key.D3: return Esc;
                case Key.OemPipe: case Key.OemBackslash: case Key.D4: return "\u001c";
                case Key.OemCloseBrackets: case Key.D5: return "\u001d";
                case Key.D6: return "\u001e";
                case Key.OemMinus: case Key.D7: return "\u001f";
                case Key.D8: return "\u007f";
            }
        }

        if (alt && !ctrl)
        {
            if (key is >= Key.A and <= Key.Z)
            {
                var c = (char)('a' + (key - Key.A));
                return Esc + (shift ? char.ToUpperInvariant(c) : c);
            }
            if (key is >= Key.D0 and <= Key.D9 && !shift)
                return Esc + (char)('0' + (key - Key.D0));
            if (key == Key.OemPeriod && !shift)
                return Esc + ".";
        }

        return null;
    }
}
