using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Ussh.Core.Tests.Integration;

/// <summary>
/// Runs tests/ssh-test-server/server.py (Python + paramiko) on a free localhost port.
/// Tests that need it return early when Python/paramiko aren't available.
/// </summary>
public sealed class SshTestServer : IDisposable
{
    public const string Password = "correct-horse-battery";

    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "ussh-ssh-" + Guid.NewGuid().ToString("N"));
    private Process? _process;

    private SshTestServer() => Directory.CreateDirectory(_workDir);

    public int Port { get; private set; }
    public string HostKeyFile { get; private set; } = "";
    public string? ClientPrivateKey { get; private set; }

    public static bool IsAvailable { get; } = CheckAvailable();

    public static SshTestServer? TryStart(bool withClientKey = false)
    {
        if (!IsAvailable)
            return null;
        var server = new SshTestServer { Port = FreePort() };
        server.HostKeyFile = Path.Combine(server._workDir, "host_key");
        if (withClientKey)
            server.ClientPrivateKey = server.GenerateClientKey();
        server.Start();
        return server;
    }

    public void Start()
    {
        var args = new List<string>
        {
            ServerScript, "--port", Port.ToString(), "--host-key", HostKeyFile, "--password", Password,
        };
        if (ClientPrivateKey != null)
        {
            args.Add("--authorized-key");
            args.Add(Path.Combine(_workDir, "client_key.pub"));
        }

        var psi = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        _process = Process.Start(psi)!;
        _process.ErrorDataReceived += (_, _) => { };
        _process.BeginErrorReadLine();

        var ready = _process.StandardOutput.ReadLineAsync();
        if (!ready.Wait(TimeSpan.FromSeconds(15)) || ready.Result != "READY")
            throw new InvalidOperationException("SSH test server did not start.");
    }

    /// <summary>Kills the server and every connection it holds, like a crash or network loss.</summary>
    public void Stop()
    {
        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(5000);
        }
        _process = null;
    }

    /// <summary>Replaces the host key, so the next connection presents a different one.</summary>
    public void RotateHostKey()
    {
        Stop();
        File.Delete(HostKeyFile);
        Start();
    }

    public void Dispose()
    {
        Stop();
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private string GenerateClientKey()
    {
        var keyFile = Path.Combine(_workDir, "client_key");
        var psi = new ProcessStartInfo("ssh-keygen") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-q", "-t", "ed25519", "-N", "", "-f", keyFile })
            psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        process.WaitForExit(10_000);
        return File.ReadAllText(keyFile);
    }

    private static string ServerScript
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "tests", "ssh-test-server", "server.py");
                if (File.Exists(candidate))
                    return candidate;
            }
            throw new FileNotFoundException("tests/ssh-test-server/server.py not found.");
        }
    }

    private static bool CheckAvailable()
    {
        try
        {
            _ = ServerScript;
            var psi = new ProcessStartInfo("python3", "-c \"import paramiko\"") { RedirectStandardError = true };
            using var process = Process.Start(psi)!;
            return process.WaitForExit(10_000) && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
