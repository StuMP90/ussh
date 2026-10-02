using System.Diagnostics;
using System.Text;
using Ussh.Core.Models;
using Ussh.Core.Ssh;
using Ussh.Core.Tests.Integration;

// Long-running stability check for zssh sessions.
//
// Opens N sessions, keeps them busy with mixed output, and every 20s checks that each one still
// echoes a unique marker. Reports reconnects, failed checks and process memory/threads/handles.
//
//   Local chaos run (spawns tests/ssh-test-server, kills/restarts it at random):
//     dotnet run --project tools/Ussh.Soak -- --sessions 10 --minutes 30 --chaos-seconds 120
//   Against a real server (no chaos; pull the network cable / sleep the laptop yourself):
//     dotnet run --project tools/Ussh.Soak -- --host myserver --user me --password '...' --hours 72

var options = Options.Parse(args);
SshTestServer? localServer = null;
ServerProfile profile;
if (options.Host == null)
{
    localServer = SshTestServer.TryStart() ?? throw new InvalidOperationException("python3 + paramiko required for local mode.");
    profile = new ServerProfile { Host = "127.0.0.1", Port = localServer.Port, Username = Environment.UserName, Password = SshTestServer.Password };
}
else
{
    profile = new ServerProfile { Host = options.Host, Port = options.Port, Username = options.User ?? Environment.UserName };
    if (options.KeyFile != null)
    {
        profile.AuthMethod = AuthMethod.PrivateKey;
        profile.PrivateKey = File.ReadAllText(options.KeyFile);
    }
    else
    {
        profile.Password = options.Password ?? throw new ArgumentException("--password or --key required");
    }
}
profile.KeepAliveSeconds = 15;
profile.ScrollbackLines = 5000;

var sessions = new List<SshSession>();
var stats = new Dictionary<SshSession, SessionStats>();
for (var i = 0; i < options.Sessions; i++)
{
    var session = new SshSession(profile, new TrustOnFirstUse());
    var s = new SessionStats();
    stats[session] = s;
    session.StateChanged += x =>
    {
        if (x.State == SessionState.Reconnecting && s.LastState == SessionState.Connected)
        {
            s.Drops++;
            s.DroppedAt = Stopwatch.GetTimestamp();
        }
        if (x.State == SessionState.Connected && s.DroppedAt != 0)
        {
            s.Reconnects++;
            s.MaxRecovery = TimeSpan.FromTicks(Math.Max(s.MaxRecovery.Ticks, Stopwatch.GetElapsedTime(s.DroppedAt).Ticks));
            s.DroppedAt = 0;
        }
        if (x.State is SessionState.Failed or SessionState.Disconnected)
            Console.WriteLine($"  !! session {sessions.IndexOf(x)} {x.State}: {x.StatusMessage}");
        s.LastState = x.State;
    };
    sessions.Add(session);
    session.Start();
}

var process = Process.GetCurrentProcess();
var started = DateTime.UtcNow;
var end = started + options.Duration;
var random = new Random(42);
var nextChaos = options.ChaosSeconds > 0 ? DateTime.UtcNow.AddSeconds(options.ChaosSeconds) : DateTime.MaxValue;
var nextReport = DateTime.UtcNow;
long checks = 0, checkFailures = 0, marker = 0;
long? baselineMemory = null;

Console.WriteLine($"Soaking {options.Sessions} session(s) against {profile.Host}:{profile.Port} for {options.Duration}." +
                  (options.ChaosSeconds > 0 ? $" Chaos every ~{options.ChaosSeconds}s." : ""));

while (DateTime.UtcNow < end)
{
    // Workload: mostly small output, sometimes a burst, to every connected session.
    foreach (var session in sessions.Where(s => s.State == SessionState.Connected))
    {
        var roll = random.Next(100);
        session.Send(roll < 5 ? "seq 1 20000 | tail -n 3\r" : roll < 30 ? "date; ls -la / | head -n 20\r" : "\r");
    }

    // Liveness: every connected session must echo a fresh marker within 15s.
    var pending = new List<(SshSession Session, string Marker)>();
    foreach (var session in sessions.Where(s => s.State == SessionState.Connected))
    {
        var m = $"soak-{Interlocked.Increment(ref marker)}-ok";
        // Quote-split so the marker appears only in the output, not in the echoed command line.
        session.Send($"echo {m[..^1]}''{m[^1]}\r");
        pending.Add((session, m));
    }
    var deadline = DateTime.UtcNow.AddSeconds(15);
    foreach (var (session, m) in pending)
    {
        checks++;
        while (!ScreenContains(session, m) && DateTime.UtcNow < deadline && session.State == SessionState.Connected)
            await Task.Delay(100);
        if (session.State == SessionState.Connected && !ScreenContains(session, m))
        {
            checkFailures++;
            Console.WriteLine($"  !! session {sessions.IndexOf(session)} did not echo {m}");
        }
    }

    if (localServer != null && DateTime.UtcNow >= nextChaos)
    {
        var downFor = random.Next(1, 20);
        Console.WriteLine($"[{Elapsed()}] CHAOS: killing server for {downFor}s");
        localServer.Stop();
        await Task.Delay(TimeSpan.FromSeconds(downFor));
        localServer.Start();
        nextChaos = DateTime.UtcNow.AddSeconds(options.ChaosSeconds * (0.5 + random.NextDouble()));
    }

    if (DateTime.UtcNow >= nextReport)
    {
        Report();
        nextReport = DateTime.UtcNow.AddSeconds(options.ReportSeconds);
    }
    await Task.Delay(TimeSpan.FromSeconds(5));
}

// Let any in-flight reconnects finish before judging.
var settle = DateTime.UtcNow.AddSeconds(90);
while (sessions.Any(s => s.State != SessionState.Connected) && DateTime.UtcNow < settle)
    await Task.Delay(500);
Report();

var notConnected = sessions.Count(s => s.State != SessionState.Connected);
var growthMb = (GC.GetTotalMemory(true) - (baselineMemory ?? 0)) / 1024.0 / 1024.0;
Console.WriteLine();
Console.WriteLine($"RESULT: {checks} checks, {checkFailures} failed; {stats.Values.Sum(s => s.Drops)} drops, " +
                  $"{stats.Values.Sum(s => s.Reconnects)} reconnects, worst recovery {stats.Values.Max(s => s.MaxRecovery).TotalSeconds:0.0}s; " +
                  $"{notConnected} session(s) not connected at end; managed heap growth {growthMb:0.0} MB.");

foreach (var session in sessions)
    await session.DisposeAsync();
localServer?.Dispose();
return checkFailures == 0 && notConnected == 0 ? 0 : 1;

void Report()
{
    process.Refresh();
    var managed = GC.GetTotalMemory(forceFullCollection: true);
    // Baseline after the first few minutes, once caches and scrollback have filled.
    if (baselineMemory == null && DateTime.UtcNow - started > TimeSpan.FromMinutes(Math.Min(3, options.Duration.TotalMinutes / 3)))
        baselineMemory = managed;
    var states = string.Join(" ", sessions.GroupBy(s => s.State).Select(g => $"{g.Key}={g.Count()}"));
    Console.WriteLine($"[{Elapsed()}] {states} | checks {checks} failed {checkFailures} | " +
                      $"reconnects {stats.Values.Sum(s => s.Reconnects)} | managed {managed / 1048576.0:0.0} MB, " +
                      $"working set {process.WorkingSet64 / 1048576.0:0.0} MB, threads {process.Threads.Count}, handles {process.HandleCount}");
}

string Elapsed() => (DateTime.UtcNow - started).ToString(@"d\.hh\:mm\:ss");

static bool ScreenContains(SshSession session, string text)
{
    lock (session.Emulator.SyncRoot)
    {
        var buffer = session.Emulator.Terminal.Buffer;
        var from = Math.Max(0, buffer.Lines.Length - 200);
        for (var i = from; i < buffer.Lines.Length; i++)
            if (buffer.TranslateBufferLineToString(i, true, 0, -1).ToString()!.Contains(text))
                return true;
    }
    return false;
}

sealed class SessionStats
{
    public SessionState LastState;
    public int Drops, Reconnects;
    public long DroppedAt;
    public TimeSpan MaxRecovery;
}

sealed class TrustOnFirstUse : IHostKeyVerifier
{
    public Task<bool> VerifyAsync(HostKeyInfo hostKey, CancellationToken cancellationToken)
    {
        if (hostKey.IsChanged)
            Console.WriteLine($"  !! host key changed for {hostKey.Host}; accepting (soak test only)");
        return Task.FromResult(true);
    }
}

sealed record Options(string? Host, int Port, string? User, string? Password, string? KeyFile, int Sessions,
    TimeSpan Duration, int ChaosSeconds, int ReportSeconds)
{
    public static Options Parse(string[] args)
    {
        string? Get(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var duration = Get("--hours") is { } h ? TimeSpan.FromHours(double.Parse(h))
            : TimeSpan.FromMinutes(double.Parse(Get("--minutes") ?? "10"));
        return new Options(Get("--host"), int.Parse(Get("--port") ?? "22"), Get("--user"), Get("--password"), Get("--key"),
            int.Parse(Get("--sessions") ?? "5"), duration, int.Parse(Get("--chaos-seconds") ?? "0"),
            int.Parse(Get("--report-seconds") ?? "60"));
    }
}
