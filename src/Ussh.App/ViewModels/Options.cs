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

/// <summary>An AWS region in the S3 region picker (Europe first, then the USA, then elsewhere).</summary>
public sealed record RegionOption(string Code, string Label)
{
    /// <summary>The "type your own" entry: new AWS regions, or S3-compatible values like "auto".</summary>
    public static RegionOption Other { get; } = new("", "Other (type a region code)…");

    public bool IsOther => Code.Length == 0;

    public static IReadOnlyList<RegionOption> All { get; } = new[]
    {
        new RegionOption("eu-west-1", "Europe – Ireland (eu-west-1)"),
        new RegionOption("eu-west-2", "Europe – London (eu-west-2)"),
        new RegionOption("eu-west-3", "Europe – Paris (eu-west-3)"),
        new RegionOption("eu-central-1", "Europe – Frankfurt (eu-central-1)"),
        new RegionOption("eu-central-2", "Europe – Zurich (eu-central-2)"),
        new RegionOption("eu-north-1", "Europe – Stockholm (eu-north-1)"),
        new RegionOption("eu-south-1", "Europe – Milan (eu-south-1)"),
        new RegionOption("eu-south-2", "Europe – Spain (eu-south-2)"),
        new RegionOption("us-east-1", "USA – N. Virginia (us-east-1)"),
        new RegionOption("us-east-2", "USA – Ohio (us-east-2)"),
        new RegionOption("us-west-1", "USA – N. California (us-west-1)"),
        new RegionOption("us-west-2", "USA – Oregon (us-west-2)"),
        new RegionOption("ca-central-1", "Canada – Central (ca-central-1)"),
        new RegionOption("ca-west-1", "Canada – Calgary (ca-west-1)"),
        new RegionOption("mx-central-1", "Mexico – Central (mx-central-1)"),
        new RegionOption("sa-east-1", "South America – São Paulo (sa-east-1)"),
        new RegionOption("af-south-1", "Africa – Cape Town (af-south-1)"),
        new RegionOption("me-south-1", "Middle East – Bahrain (me-south-1)"),
        new RegionOption("me-central-1", "Middle East – UAE (me-central-1)"),
        new RegionOption("il-central-1", "Israel – Tel Aviv (il-central-1)"),
        new RegionOption("ap-south-1", "Asia Pacific – Mumbai (ap-south-1)"),
        new RegionOption("ap-south-2", "Asia Pacific – Hyderabad (ap-south-2)"),
        new RegionOption("ap-east-1", "Asia Pacific – Hong Kong (ap-east-1)"),
        new RegionOption("ap-southeast-1", "Asia Pacific – Singapore (ap-southeast-1)"),
        new RegionOption("ap-southeast-2", "Asia Pacific – Sydney (ap-southeast-2)"),
        new RegionOption("ap-southeast-3", "Asia Pacific – Jakarta (ap-southeast-3)"),
        new RegionOption("ap-southeast-4", "Asia Pacific – Melbourne (ap-southeast-4)"),
        new RegionOption("ap-southeast-5", "Asia Pacific – Malaysia (ap-southeast-5)"),
        new RegionOption("ap-southeast-7", "Asia Pacific – Thailand (ap-southeast-7)"),
        new RegionOption("ap-northeast-1", "Asia Pacific – Tokyo (ap-northeast-1)"),
        new RegionOption("ap-northeast-2", "Asia Pacific – Seoul (ap-northeast-2)"),
        new RegionOption("ap-northeast-3", "Asia Pacific – Osaka (ap-northeast-3)"),
        Other,
    };

    public override string ToString() => Label;
}
