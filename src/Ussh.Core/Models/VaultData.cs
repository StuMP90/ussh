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
    public string DefaultTheme { get; set; } = "uSSH Dark";
}
