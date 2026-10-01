namespace Ussh.Core;

/// <summary>
/// Per-user data locations. Windows: %LOCALAPPDATA%\ussh (redirected into the package's
/// private folder automatically when running as MSIX). Linux: $XDG_CONFIG_HOME/ussh or ~/.config/ussh.
/// </summary>
public static class AppPaths
{
    public static string DataDirectory { get; } = Resolve();

    public static string VaultFile => Path.Combine(DataDirectory, "vault.json");
    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    private static string Resolve()
    {
        var overridden = Environment.GetEnvironmentVariable("USSH_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overridden))
            return overridden;

        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ussh");

        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var root = string.IsNullOrWhiteSpace(xdg)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
            : xdg;
        return Path.Combine(root, "ussh");
    }
}
