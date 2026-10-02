namespace Ussh.Core.Models;

/// <summary>Everything stored inside the encrypted vault.</summary>
public sealed class VaultData
{
    public List<ServerProfile> Servers { get; set; } = new();
    public AppSettings Settings { get; set; } = new();
}

public sealed class AppSettings
{
    /// <summary>
    /// Lock the vault after this many minutes without input. 0 disables auto-lock.
    /// Locking hides the UI and blocks connecting/editing; open sessions keep running.
    /// </summary>
    public int AutoLockMinutes { get; set; } = 15;

    public string FontFamily { get; set; } = "Cascadia Mono, JetBrains Mono, DejaVu Sans Mono, Consolas, Menlo, monospace";
    public double FontSize { get; set; } = 14;

    /// <summary>Terminal theme for servers that don't choose their own.</summary>
    public string DefaultTheme { get; set; } = "zSSH Dark";

    /// <summary>What happens when you type <c>exit</c> (servers can override it).</summary>
    public ShellExitAction OnShellExit { get; set; } = ShellExitAction.KeepOpen;
}

/// <summary>What to do with a pane when its remote shell exits cleanly (never on drops or errors).</summary>
public enum ShellExitAction
{
    /// <summary>Keep the pane with its last output and a Reconnect button.</summary>
    KeepOpen,
    /// <summary>Close the pane (and the tab, if it was the last pane).</summary>
    Close,
}
