using System.Collections.Concurrent;
using System.Text;

namespace Ussh.Core.Diagnostics;

/// <summary>
/// Minimal thread-safe file logger: one file per day, written by a single background
/// thread so logging never blocks a session's read loop or the UI. Never logs secrets.
/// </summary>
public static class Log
{
    private const int RetainDays = 14;
    private static readonly BlockingCollection<string> Queue = new(boundedCapacity: 10_000);
    private static readonly Thread Writer;

    static Log()
    {
        Writer = new Thread(WriteLoop) { IsBackground = true, Name = "zssh-log" };
        Writer.Start();
    }

    public static void Info(string source, string message) => Enqueue("INF", source, message, null);
    public static void Warn(string source, string message, Exception? ex = null) => Enqueue("WRN", source, message, ex);
    public static void Error(string source, string message, Exception? ex = null) => Enqueue("ERR", source, message, ex);

    private static void Enqueue(string level, string source, string message, Exception? ex)
    {
        var line = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
            .Append(' ').Append(level)
            .Append(" [").Append(source).Append("] ")
            .Append(message);
        if (ex != null)
            line.AppendLine().Append(ex);
        // Drop rather than block if the disk is stuck; logging must never stall a session.
        Queue.TryAdd(line.ToString());
    }

    private static void WriteLoop()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogDirectory);
            PruneOldLogs();
        }
        catch { /* logging is best-effort */ }

        foreach (var line in Queue.GetConsumingEnumerable())
        {
            try
            {
                var file = Path.Combine(AppPaths.LogDirectory, $"zssh-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(file, line + Environment.NewLine);
            }
            catch { /* best-effort */ }
        }
    }

    private static void PruneOldLogs()
    {
        foreach (var file in Directory.EnumerateFiles(AppPaths.LogDirectory, "zssh-*.log"))
        {
            if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-RetainDays))
                File.Delete(file);
        }
    }
}
