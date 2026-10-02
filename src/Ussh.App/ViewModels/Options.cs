using Avalonia.Media;
using Ussh.App.Controls;
using Ussh.Core.Models;

namespace Ussh.App.ViewModels;

/// <summary>An entry in the "Jump host" picker. A null Id means connect directly.</summary>
public sealed record JumpHostOption(Guid? Id, string Label)
{
    public static JumpHostOption Direct { get; } = new(null, "None (connect directly)");

    public static JumpHostOption For(ServerProfile server) =>
        new(server.Id, $"{server.DisplayName}  ({server.Username}@{server.Host})");

    public override string ToString() => Label;
}

/// <summary>An entry in a theme picker, with colours for a small preview. A null Name means "use the default".</summary>
public sealed class ThemeOption
{
    private ThemeOption(string? name, string label, TerminalTheme theme)
    {
        Name = name;
        Label = label;
        var palette = TerminalPalette.For(theme);
        Background = palette.Background;
        Foreground = palette.Foreground;
        // Red, green, yellow, blue, magenta, cyan: shows how colourful output will look.
        Swatches = Enumerable.Range(1, 6).Select(i => (IBrush)palette.Resolve(i, isForeground: true)).ToList();
    }

    public string? Name { get; }
    public string Label { get; }
    public IBrush Background { get; }
    public IBrush Foreground { get; }
    public IReadOnlyList<IBrush> Swatches { get; }

    /// <summary>Every theme, optionally preceded by "Default (…)" for per-server pickers.</summary>
    public static IReadOnlyList<ThemeOption> List(string? defaultThemeName = null)
    {
        var options = new List<ThemeOption>();
        if (defaultThemeName != null)
        {
            var fallback = TerminalTheme.Find(defaultThemeName);
            options.Add(new ThemeOption(null, $"Default ({fallback.Name})", fallback));
        }
        options.AddRange(TerminalTheme.All.Select(t => new ThemeOption(t.Name, t.Name, t)));
        return options;
    }

    public override string ToString() => Label;
}
