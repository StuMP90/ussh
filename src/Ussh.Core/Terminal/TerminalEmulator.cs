using Ussh.Core.Diagnostics;
using XtermSharp;
using XTerminal = XtermSharp.Terminal;

namespace Ussh.Core.Terminal;

/// <summary>
/// Thread-safe wrapper around the XtermSharp engine for one session.
///
/// The session's reader thread calls <see cref="Feed"/>; the UI renders on its own timer
/// while holding <see cref="SyncRoot"/>, and only when <see cref="ConsumeDirty"/> reports
/// a change. Output bursts are therefore coalesced into at most one repaint per frame
/// instead of one UI dispatch per network packet.
///
/// Scrollback is a fixed-size ring buffer (<c>TerminalOptions.Scrollback</c>), so memory
/// stays flat no matter how long the session runs.
/// </summary>
public sealed class TerminalEmulator : SimpleTerminalDelegate
{
    private int _dirty = 1;
    private int _parserErrors;

    public TerminalEmulator(int cols, int rows, int scrollbackLines)
    {
        Terminal = new XTerminal(this, new TerminalOptions
        {
            Cols = Math.Max(cols, 2),
            Rows = Math.Max(rows, 1),
            Scrollback = Math.Clamp(scrollbackLines, 100, 1_000_000),
            TermName = "xterm-256color",
            // The remote pty already emits CRLF; converting LF would break full-screen apps.
            ConvertEol = false,
        });
    }

    /// <summary>Hold this while reading the buffer or changing terminal state.</summary>
    public object SyncRoot { get; } = new();

    public XTerminal Terminal { get; }

    /// <summary>Bytes the terminal must send back to the server (device status replies, etc.).</summary>
    public event Action<byte[]>? ResponseReady;

    /// <summary>Raised on the caller's thread when the remote sets the window title.</summary>
    public event Action<string>? TitleChanged;

    public void Feed(byte[] data, int count)
    {
        lock (SyncRoot)
        {
            try
            {
                Terminal.Feed(data, count);
            }
            catch (Exception ex)
            {
                // A malformed or unsupported sequence must never take the session down.
                // Drop this chunk and carry on; the next output resynchronises the screen.
                if (Interlocked.Increment(ref _parserErrors) <= 20)
                    Log.Warn(nameof(TerminalEmulator), "Terminal parser error; chunk skipped.", ex);
            }
        }
        Interlocked.Exchange(ref _dirty, 1);
    }

    public void Resize(int cols, int rows)
    {
        lock (SyncRoot)
        {
            try
            {
                Terminal.Resize(Math.Max(cols, 2), Math.Max(rows, 1));
            }
            catch (Exception ex)
            {
                Log.Warn(nameof(TerminalEmulator), $"Resize to {cols}x{rows} failed.", ex);
            }
        }
        MarkDirty();
    }

    /// <summary>Scrolls the viewport through scrollback. Negative is up.</summary>
    public void ScrollViewport(int lines)
    {
        lock (SyncRoot)
        {
            var buffer = Terminal.Buffer;
            var target = Math.Clamp(buffer.YDisp + lines, 0, buffer.YBase);
            buffer.YDisp = target;
        }
        MarkDirty();
    }

    /// <summary>Jumps back to the live screen (called on keyboard input).</summary>
    public void ScrollToBottom()
    {
        lock (SyncRoot)
        {
            var buffer = Terminal.Buffer;
            if (buffer.YDisp == buffer.YBase)
                return;
            buffer.YDisp = buffer.YBase;
        }
        MarkDirty();
    }

    public void MarkDirty() => Interlocked.Exchange(ref _dirty, 1);

    public bool ConsumeDirty() => Interlocked.Exchange(ref _dirty, 0) == 1;

    public override void Send(byte[] data) => ResponseReady?.Invoke(data);

    public override void SetTerminalTitle(XTerminal source, string title) => TitleChanged?.Invoke(title);

    // Never let a remote program move, resize or iconify our window.
    public override bool IsProcessTrusted() => false;
}
