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

/// <summary>An entry in a "when the shell exits" picker. A null Value means "use the default".</summary>
public sealed record ShellExitOption(ShellExitAction? Value, string Label)
{
    private static string Describe(ShellExitAction action) => action switch
    {
        ShellExitAction.Close => "Close the pane",
        _ => "Keep the pane open (show Reconnect)",
    };

    /// <summary>Both actions, optionally preceded by "Default (…)" for per-server pickers.</summary>
    public static IReadOnlyList<ShellExitOption> List(ShellExitAction? defaultAction = null)
    {
        var options = new List<ShellExitOption>();
        if (defaultAction is { } fallback)
            options.Add(new ShellExitOption(null, $"Default ({Describe(fallback)})"));
        options.Add(new ShellExitOption(ShellExitAction.KeepOpen, Describe(ShellExitAction.KeepOpen)));
        options.Add(new ShellExitOption(ShellExitAction.Close, Describe(ShellExitAction.Close)));
        return options;
    }

    public override string ToString() => Label;
}

/// <summary>An entry in the server "Type" picker.</summary>
public sealed record ServerKindOption(ServerKind Kind, string Label)
{
    public static IReadOnlyList<ServerKindOption> All { get; } = new[]
    {
        new ServerKindOption(ServerKind.Ssh, "SSH server (terminal and files)"),
        new ServerKindOption(ServerKind.SftpOnly, "SFTP only (files)"),
        new ServerKindOption(ServerKind.S3, "Amazon S3 or S3-compatible bucket (files)"),
    };

    public override string ToString() => Label;
}
