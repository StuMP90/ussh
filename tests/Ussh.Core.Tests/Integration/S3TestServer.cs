using System.Diagnostics;
using System.Net.Sockets;
using Amazon.S3;
using Amazon.Runtime;
using Ussh.Core.Models;

namespace Ussh.Core.Tests.Integration;

/// <summary>
/// A local S3 emulator (moto) for tests: no AWS account involved. Uses $USSH_MOTO_SERVER, else
/// "moto_server" on the PATH ("pip install 'moto[server]'"); tests return early without it.
/// </summary>
public sealed class S3TestServer : IDisposable
{
    private Process? _process;

    public int Port { get; } = SshTestServer.FreePort();
    public string Url => $"http://127.0.0.1:{Port}";

    public static string? Executable { get; } = Find();

    public static S3TestServer? TryStart()
    {
        if (Executable == null)
            return null;
        var server = new S3TestServer();
        var psi = new ProcessStartInfo(Executable) { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(server.Port.ToString());
        server._process = Process.Start(psi)!;
        server._process.OutputDataReceived += (_, _) => { };
        server._process.ErrorDataReceived += (_, _) => { };
        server._process.BeginOutputReadLine();
        server._process.BeginErrorReadLine();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var probe = new TcpClient();
                probe.Connect("127.0.0.1", server.Port);
                return server;
            }
            catch (SocketException)
            {
                Thread.Sleep(100);
            }
        }
        server.Dispose();
        throw new InvalidOperationException("moto_server did not start.");
    }

    /// <summary>A profile for <paramref name="bucket"/>, creating the bucket.</summary>
    public ServerProfile CreateBucket(string bucket, string prefix = "")
    {
        using (var client = new AmazonS3Client(new BasicAWSCredentials("test", "test"),
                   new AmazonS3Config { ServiceURL = Url, ForcePathStyle = true, AuthenticationRegion = "us-east-1" }))
            client.PutBucketAsync(bucket).GetAwaiter().GetResult();
        return new ServerProfile
        {
            Name = "bucket " + bucket,
            Kind = ServerKind.S3,
            S3Bucket = bucket,
            S3Region = "us-east-1",
            S3Prefix = prefix,
            S3AccessKeyId = "test",
            S3SecretAccessKey = "test",
            S3ServiceUrl = Url,
        };
    }

    public void Dispose()
    {
        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(5000);
        }
    }

    private static string? Find()
    {
        var configured = Environment.GetEnvironmentVariable("USSH_MOTO_SERVER");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return configured;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(dir, "moto_server");
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }
}
