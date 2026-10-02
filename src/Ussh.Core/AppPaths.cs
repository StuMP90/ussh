namespace Ussh.Core;

/// <summary>
/// Per-user data locations. Windows: %LOCALAPPDATA%\zssh (redirected into the package's
/// private folder automatically when running as MSIX). Linux: $XDG_CONFIG_HOME/zssh or ~/.config/zssh.
/// Data from before the rename (the app was called uSSH, folder "ussh") is moved across on first run.
/// </summary>
public static class AppPaths
{
    public static string DataDirectory { get; } = Resolve();

    public static string VaultFile => Path.Combine(DataDirectory, "vault.json");
    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    private static string Resolve()
    {
        var overridden = Environment.GetEnvironmentVariable("ZSSH_DATA_DIR") ?? Environment.GetEnvironmentVariable("USSH_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overridden))
            return overridden;

        string root;
        if (OperatingSystem.IsWindows())
        {
            root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }
        else
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            root = string.IsNullOrWhiteSpace(xdg)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
                : xdg;
        }
        return MigrateFromOldName(Path.Combine(root, "ussh"), Path.Combine(root, "zssh"));
    }

    /// <summary>Moves the pre-rename folder to the new name. If that fails, keeps using the old one.</summary>
    internal static string MigrateFromOldName(string oldDirectory, string newDirectory)
    {
        if (Directory.Exists(newDirectory) || !Directory.Exists(oldDirectory))
            return newDirectory;
        try
        {
            Directory.Move(oldDirectory, newDirectory);
            return newDirectory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return oldDirectory;
        }
    }
}
