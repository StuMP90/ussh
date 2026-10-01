using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;
using Ussh.Core.Diagnostics;
using Ussh.Core.Ssh;
using Ussh.Core.Terminal;
using XtermSharp;
using XTerminal = XtermSharp.Terminal;

namespace Ussh.App.Controls;

/// <summary>
/// Renders a session's terminal buffer and turns keyboard/mouse input into bytes for the server.
///
/// Rendering is pull-based: a ~60fps timer checks the emulator's dirty flag and only then
/// invalidates, so a flood of output costs at most one repaint per frame and an idle
/// terminal costs nothing. Rendering holds the emulator lock briefly while it reads cells.
/// </summary>
public sealed class TerminalControl : Control
{
    public static readonly StyledProperty<SshSession?> SessionProperty =
        AvaloniaProperty.Register<TerminalControl, SshSession?>(nameof(Session));

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        TextElement.FontFamilyProperty.AddOwner<TerminalControl>();

    public static readonly StyledProperty<double> FontSizeProperty =
        TextElement.FontSizeProperty.AddOwner<TerminalControl>();

    private const double Padding = 6;
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(16);
    private static readonly TimeSpan ResizeDebounce = TimeSpan.FromMilliseconds(80);

    private readonly TerminalPalette _palette = TerminalPalette.Default;
    private readonly DispatcherTimer _frameTimer;
    private readonly DispatcherTimer _resizeTimer;
    private Typeface _regular, _bold, _italic, _boldItalic;
    private double _cellWidth = 8, _cellHeight = 16, _baseline = 12;
    private int _cols, _rows;
    private SshSession? _sizedSession;

    // Selection, in absolute buffer coordinates (row = index into Buffer.Lines).
    private (int Col, int Row)? _selectionAnchor;
    private (int Col, int Row)? _selectionEnd;
    private bool _selecting;
    private int _mouseButtonDown = -1;
    private bool _swallowTextInput;

    static TerminalControl()
    {
        FocusableProperty.OverrideDefaultValue<TerminalControl>(true);
        ClipToBoundsProperty.OverrideDefaultValue<TerminalControl>(true);
        CursorProperty.OverrideDefaultValue<TerminalControl>(new Cursor(StandardCursorType.Ibeam));
    }

    public TerminalControl()
    {
        _frameTimer = new DispatcherTimer(FrameInterval, DispatcherPriority.Render, (_, _) => OnFrame());
        _resizeTimer = new DispatcherTimer(ResizeDebounce, DispatcherPriority.Background, (_, _) =>
        {
            _resizeTimer!.Stop();
            ApplySize();
        });
        UpdateFonts();
        ContextMenu = BuildContextMenu();
    }

    public SshSession? Session
    {
        get => GetValue(SessionProperty);
        set => SetValue(SessionProperty, value);
    }

    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    private TerminalEmulator? Emulator => Session?.Emulator;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _frameTimer.Start();
        _sizedSession = null;
        _resizeTimer.Start();
        Emulator?.MarkDirty();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _frameTimer.Stop();
        _resizeTimer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FontFamilyProperty || change.Property == FontSizeProperty)
        {
            UpdateFonts();
            ScheduleResize();
            InvalidateVisual();
        }
        else if (change.Property == SessionProperty)
        {
            ClearSelection();
            _sizedSession = null;
            ScheduleResize();
            InvalidateVisual();
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ScheduleResize();
    }

    private void ScheduleResize()
    {
        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    private void UpdateFonts()
    {
        var family = FontFamily ?? FontFamily.Default;
        _regular = new Typeface(family);
        _bold = new Typeface(family, FontStyle.Normal, FontWeight.Bold);
        _italic = new Typeface(family, FontStyle.Italic);
        _boldItalic = new Typeface(family, FontStyle.Italic, FontWeight.Bold);

        var size = FontSize > 0 ? FontSize : 14;
        var sample = new FormattedText("MMMMMMMMMM", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _regular, size, Brushes.White);
        _cellWidth = Math.Max(1, sample.WidthIncludingTrailingWhitespace / 10);
        _cellHeight = Math.Max(1, Math.Ceiling(sample.Height));
        _baseline = sample.Baseline;
    }

    private void ApplySize()
    {
        var session = Session;
        if (session == null || Bounds.Width <= 0 || Bounds.Height <= 0)
            return;
        var cols = Math.Max(2, (int)((Bounds.Width - 2 * Padding) / _cellWidth));
        var rows = Math.Max(1, (int)((Bounds.Height - 2 * Padding) / _cellHeight));
        if (cols == _cols && rows == _rows && ReferenceEquals(_sizedSession, session))
            return;
        _cols = cols;
        _rows = rows;
        _sizedSession = session;
        session.Resize(cols, rows, (int)Bounds.Width, (int)Bounds.Height);
        InvalidateVisual();
    }

    private void OnFrame()
    {
        if (Emulator?.ConsumeDirty() == true)
            InvalidateVisual();
    }

    // ------------------------------------------------------------------ rendering

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(_palette.Background, new Rect(Bounds.Size));
        var emulator = Emulator;
        if (emulator == null)
            return;
        try
        {
            lock (emulator.SyncRoot)
                RenderBuffer(context, emulator.Terminal);
        }
        catch (Exception ex)
        {
            // A rendering bug must not take the window (and every other session) down.
            Log.Error(nameof(TerminalControl), "Render failed.", ex);
        }
    }

    private void RenderBuffer(DrawingContext context, XTerminal terminal)
    {
        var buffer = terminal.Buffer;
        var cols = terminal.Cols;
        var rows = terminal.Rows;
        var top = buffer.YDisp;
        var fontSize = FontSize > 0 ? FontSize : 14;
        var (selStart, selEnd) = NormalizedSelection();
        var run = new StringBuilder();

        for (var row = 0; row < rows; row++)
        {
            var lineIndex = top + row;
            if (lineIndex >= buffer.Lines.Length)
                break;
            var line = buffer.Lines[lineIndex];
            if (line == null)
                continue;

            var y = Padding + row * _cellHeight;
            var length = Math.Min(cols, line.Length);

            // Pass 1: backgrounds, merged into runs of equal colour.
            var bgStart = 0;
            IBrush? bgBrush = null;
            for (var col = 0; col <= length; col++)
            {
                IBrush? brush = null;
                if (col < length)
                {
                    var style = Resolve(line[col].Attribute, IsSelected(col, lineIndex, selStart, selEnd));
                    brush = ReferenceEquals(style.Background, _palette.Background) ? null : style.Background;
                }
                if (!ReferenceEquals(brush, bgBrush) || col == length)
                {
                    if (bgBrush != null)
                        context.FillRectangle(bgBrush, new Rect(Padding + bgStart * _cellWidth, y, (col - bgStart) * _cellWidth, _cellHeight));
                    bgBrush = brush;
                    bgStart = col;
                }
            }

            // Pass 2: text. ASCII cells with the same style are drawn as one run; anything else
            // (wide, box drawing, emoji) is placed on its own cell so font fallback widths can't
            // push later columns out of alignment.
            run.Clear();
            var runStart = 0;
            CellStyle runStyle = default;
            for (var col = 0; col < length; col++)
            {
                var cell = line[col];
                if (cell.Width == 0)
                    continue; // right half of a wide character
                var style = Resolve(cell.Attribute, IsSelected(col, lineIndex, selStart, selEnd));
                var codepoint = Codepoint(cell);
                var simple = codepoint is >= 0x20 and < 0x7f && cell.Width == 1;

                if (run.Length > 0 && (!simple || !style.SameText(runStyle)))
                {
                    DrawRun(context, run.ToString(), runStart, y, runStyle, fontSize);
                    run.Clear();
                }
                if (simple)
                {
                    if (run.Length == 0)
                    {
                        runStart = col;
                        runStyle = style;
                    }
                    run.Append((char)codepoint);
                }
                else if (codepoint > 0x20 && !style.Invisible)
                {
                    DrawRun(context, ToText(codepoint), col, y, style, fontSize);
                }
            }
            if (run.Length > 0)
                DrawRun(context, run.ToString(), runStart, y, runStyle, fontSize);
        }

        DrawCursor(context, terminal, fontSize);
    }

    private void DrawRun(DrawingContext context, string text, int col, double y, CellStyle style, double fontSize)
    {
        if (style.Invisible || string.IsNullOrWhiteSpace(text) && !style.Underline && !style.Strike)
            return;
        var x = Padding + col * _cellWidth;
        var typeface = style.Bold ? (style.Italic ? _boldItalic : _bold) : (style.Italic ? _italic : _regular);
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, fontSize, style.Foreground);
        // Align on the primary font's baseline: glyphs from fallback fonts (CJK, emoji, box
        // drawing) have their own line metrics and would otherwise sit higher or lower.
        context.DrawText(formatted, new Point(x, y + _baseline - formatted.Baseline));

        var width = text.Length * _cellWidth;
        if (style.Underline)
        {
            var uy = Math.Floor(y + _cellHeight - 2) + 0.5;
            context.DrawLine(new Pen(style.Foreground, 1), new Point(x, uy), new Point(x + width, uy));
        }
        if (style.Strike)
        {
            var sy = Math.Floor(y + _cellHeight / 2) + 0.5;
            context.DrawLine(new Pen(style.Foreground, 1), new Point(x, sy), new Point(x + width, sy));
        }
    }

    private void DrawCursor(DrawingContext context, XTerminal terminal, double fontSize)
    {
        var buffer = terminal.Buffer;
        if (terminal.CursorHidden || buffer.YDisp != buffer.YBase || Session?.State != SessionState.Connected)
            return;
        var col = Math.Clamp(buffer.X, 0, terminal.Cols - 1);
        var row = Math.Clamp(buffer.Y, 0, terminal.Rows - 1);
        var rect = new Rect(Padding + col * _cellWidth, Padding + row * _cellHeight, _cellWidth, _cellHeight);

        if (!IsFocused)
        {
            context.DrawRectangle(new Pen(_palette.Cursor, 1), rect.Deflate(0.5));
            return;
        }

        context.FillRectangle(_palette.Cursor, rect);
        var line = buffer.Lines[buffer.YBase + row];
        if (line != null && col < line.Length)
        {
            var codepoint = Codepoint(line[col]);
            if (codepoint > 0x20)
            {
                var text = new FormattedText(ToText(codepoint), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _regular, fontSize, _palette.Background);
                context.DrawText(text, new Point(rect.X, rect.Y + _baseline - text.Baseline));
            }
        }
    }

    private readonly record struct CellStyle(IBrush Foreground, IBrush Background, bool Bold, bool Italic, bool Underline, bool Strike, bool Invisible)
    {
        public bool SameText(CellStyle other) =>
            ReferenceEquals(Foreground, other.Foreground) && Bold == other.Bold && Italic == other.Italic &&
            Underline == other.Underline && Strike == other.Strike && Invisible == other.Invisible;
    }

    private CellStyle Resolve(int attribute, bool selected)
    {
        var flags = (FLAGS)(attribute >> 18);
        var fg = (attribute >> 9) & 0x1ff;
        var bg = attribute & 0x1ff;
        var bold = flags.HasFlag(FLAGS.BOLD);
        // Classic behaviour: bold makes the 8 base colours bright.
        if (bold && fg < 8)
            fg += 8;

        IBrush foreground = _palette.Resolve(fg, isForeground: true, dim: flags.HasFlag(FLAGS.DIM));
        IBrush background = _palette.Resolve(bg, isForeground: false);
        if (flags.HasFlag(FLAGS.INVERSE))
        {
            (foreground, background) = (_palette.Resolve(bg, isForeground: false), _palette.Resolve(fg, isForeground: true));
        }
        if (selected)
            background = _palette.Selection;

        return new CellStyle(foreground, background, bold, flags.HasFlag(FLAGS.ITALIC),
            flags.HasFlag(FLAGS.UNDERLINE), flags.HasFlag(FLAGS.CrossedOut), flags.HasFlag(FLAGS.INVISIBLE));
    }

    /// <summary>The cell's character; empty cells (a sentinel rune with Code 0) read as a space.</summary>
    private static uint Codepoint(CharData cell) => cell.IsNullChar() ? 0x20 : (uint)cell.Rune;

    private static string ToText(uint codepoint) =>
        codepoint <= 0x10FFFF && (codepoint < 0xD800 || codepoint > 0xDFFF)
            ? char.ConvertFromUtf32((int)codepoint)
            : "�";

    // ------------------------------------------------------------------ keyboard

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        _swallowTextInput = false;
        var session = Session;
        if (session == null || e.Handled)
            return;

        var mods = e.KeyModifiers;
        var ctrlShift = mods.HasFlag(KeyModifiers.Control) && mods.HasFlag(KeyModifiers.Shift);

        // Local shortcuts (not sent to the server).
        if ((ctrlShift && e.Key == Key.C) || (mods == KeyModifiers.Control && e.Key == Key.Insert))
        {
            _ = CopySelectionAsync();
            e.Handled = true;
            return;
        }
        if ((ctrlShift && e.Key == Key.V) || (mods == KeyModifiers.Shift && e.Key == Key.Insert))
        {
            _ = PasteAsync();
            e.Handled = true;
            return;
        }
        if (mods == KeyModifiers.Shift && e.Key is Key.PageUp or Key.PageDown)
        {
            Emulator?.ScrollViewport((e.Key == Key.PageUp ? -1 : 1) * Math.Max(1, _rows - 1));
            e.Handled = true;
            return;
        }
        if (mods == KeyModifiers.Shift && e.Key is Key.Home or Key.End)
        {
            Emulator?.ScrollViewport(e.Key == Key.Home ? int.MinValue / 2 : int.MaxValue / 2);
            e.Handled = true;
            return;
        }

        bool applicationCursor;
        lock (session.Emulator.SyncRoot)
            applicationCursor = session.Emulator.Terminal.ApplicationCursor;

        var sequence = KeyEncoder.Encode(e.Key, mods, applicationCursor);
        if (sequence == null)
            return;

        SendInput(sequence);
        e.Handled = true;
        // Some platforms also raise TextInput for keys we've just encoded (e.g. Alt+x on X11).
        _swallowTextInput = mods.HasFlag(KeyModifiers.Alt) || mods.HasFlag(KeyModifiers.Control);
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (_swallowTextInput)
        {
            _swallowTextInput = false;
            e.Handled = true;
            return;
        }
        var text = e.Text;
        if (string.IsNullOrEmpty(text) || text.All(char.IsControl))
            return;
        SendInput(text);
        e.Handled = true;
    }

    private void SendInput(string text)
    {
        var session = Session;
        if (session == null)
            return;
        session.Emulator.ScrollToBottom();
        session.Send(text);
    }

    // ------------------------------------------------------------------ mouse

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var emulator = Emulator;
        if (emulator == null)
            return;

        var point = e.GetCurrentPoint(this);
        var (col, row) = CellAt(point.Position);
        var button = point.Properties.PointerUpdateKind switch
        {
            PointerUpdateKind.LeftButtonPressed => 0,
            PointerUpdateKind.MiddleButtonPressed => 1,
            PointerUpdateKind.RightButtonPressed => 2,
            _ => -1,
        };

        if (button >= 0 && ReportsMouse(e.KeyModifiers))
        {
            SendMouse(button, release: false, col, row, e.KeyModifiers);
            _mouseButtonDown = button;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (button == 1)
        {
            _ = PasteAsync(); // middle-click paste, as on X11 terminals
            e.Handled = true;
            return;
        }

        if (button != 0)
            return; // right button: context menu

        int top;
        lock (emulator.SyncRoot)
            top = emulator.Terminal.Buffer.YDisp;
        var absolute = (col, top + row);

        if (e.ClickCount == 2)
            SelectWord(absolute);
        else if (e.ClickCount >= 3)
            SelectLine(absolute.Item2);
        else
        {
            _selectionAnchor = absolute;
            _selectionEnd = absolute;
            _selecting = true;
            e.Pointer.Capture(this);
        }
        emulator.MarkDirty();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var emulator = Emulator;
        if (emulator == null)
            return;
        var (col, row) = CellAt(e.GetPosition(this));

        if (_mouseButtonDown >= 0)
        {
            lock (emulator.SyncRoot)
            {
                var terminal = emulator.Terminal;
                if (terminal.MouseMode.SendMotionEvent())
                    terminal.SendMouseMotion(terminal.EncodeMouseButton(_mouseButtonDown, false, false, false, false), col, row);
            }
            return;
        }

        if (_selecting)
        {
            int top;
            lock (emulator.SyncRoot)
                top = emulator.Terminal.Buffer.YDisp;
            _selectionEnd = (col, top + row);
            emulator.MarkDirty();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_mouseButtonDown >= 0)
        {
            var (col, row) = CellAt(e.GetPosition(this));
            SendMouse(_mouseButtonDown, release: true, col, row, e.KeyModifiers);
            _mouseButtonDown = -1;
            e.Pointer.Capture(null);
            return;
        }
        if (_selecting)
        {
            _selecting = false;
            e.Pointer.Capture(null);
            if (_selectionAnchor == _selectionEnd)
                ClearSelection();
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var emulator = Emulator;
        if (emulator == null || e.Delta.Y == 0)
            return;
        var up = e.Delta.Y > 0;
        var lines = Math.Max(1, (int)Math.Round(Math.Abs(e.Delta.Y) * 3));
        var (col, row) = CellAt(e.GetPosition(this));

        if (ReportsMouse(e.KeyModifiers))
        {
            for (var i = 0; i < Math.Min(lines, 5); i++)
                SendMouse(up ? 4 : 5, release: false, col, row, e.KeyModifiers);
        }
        else
        {
            bool alternate, applicationCursor;
            lock (emulator.SyncRoot)
            {
                alternate = emulator.Terminal.Buffers.IsAlternateBuffer;
                applicationCursor = emulator.Terminal.ApplicationCursor;
            }
            if (alternate)
            {
                // Full-screen apps (less, man, vim without mouse) have no scrollback: send arrows.
                var arrow = KeyEncoder.Encode(up ? Key.Up : Key.Down, KeyModifiers.None, applicationCursor)!;
                Session?.Send(string.Concat(Enumerable.Repeat(arrow, lines)));
            }
            else
            {
                emulator.ScrollViewport(up ? -lines : lines);
            }
        }
        e.Handled = true;
    }

    private bool ReportsMouse(KeyModifiers modifiers)
    {
        var emulator = Emulator;
        if (emulator == null || modifiers.HasFlag(KeyModifiers.Shift))
            return false; // Shift bypasses mouse reporting so text can still be selected.
        lock (emulator.SyncRoot)
            return emulator.Terminal.MouseMode != MouseMode.Off;
    }

    private void SendMouse(int button, bool release, int col, int row, KeyModifiers modifiers)
    {
        var emulator = Emulator;
        if (emulator == null)
            return;
        lock (emulator.SyncRoot)
        {
            var terminal = emulator.Terminal;
            if (release && !terminal.MouseMode.SendButtonRelease())
                return;
            var flags = terminal.EncodeMouseButton(button, release, modifiers.HasFlag(KeyModifiers.Shift),
                modifiers.HasFlag(KeyModifiers.Alt), modifiers.HasFlag(KeyModifiers.Control));
            terminal.SendEvent(flags, col, row);
        }
    }

    private (int Col, int Row) CellAt(Point position)
    {
        var col = (int)Math.Floor((position.X - Padding) / _cellWidth);
        var row = (int)Math.Floor((position.Y - Padding) / _cellHeight);
        return (Math.Clamp(col, 0, Math.Max(0, _cols - 1)), Math.Clamp(row, 0, Math.Max(0, _rows - 1)));
    }

    // ------------------------------------------------------------------ selection & clipboard

    private ((int Col, int Row) Start, (int Col, int Row) End) NormalizedSelection()
    {
        if (_selectionAnchor is not { } a || _selectionEnd is not { } b)
            return ((-1, -1), (-1, -1));
        return (a.Row < b.Row || (a.Row == b.Row && a.Col <= b.Col)) ? (a, b) : (b, a);
    }

    private static bool IsSelected(int col, int row, (int Col, int Row) start, (int Col, int Row) end)
    {
        if (start.Row < 0 || row < start.Row || row > end.Row)
            return false;
        if (row == start.Row && col < start.Col)
            return false;
        if (row == end.Row && col > end.Col)
            return false;
        return true;
    }

    private void ClearSelection()
    {
        _selectionAnchor = null;
        _selectionEnd = null;
        Emulator?.MarkDirty();
    }

    private void SelectWord((int Col, int Row) at)
    {
        var emulator = Emulator!;
        lock (emulator.SyncRoot)
        {
            var line = emulator.Terminal.Buffer.Lines[at.Row];
            if (line == null)
                return;
            static bool IsWordChar(uint c) => c > 0x20 && !" \t\"'`()[]{}<>|;,".Contains((char)Math.Min(c, 0xFFFF));
            int start = at.Col, end = at.Col;
            while (start > 0 && IsWordChar(Codepoint(line[start - 1])))
                start--;
            while (end < line.Length - 1 && IsWordChar(Codepoint(line[end + 1])))
                end++;
            _selectionAnchor = (start, at.Row);
            _selectionEnd = (end, at.Row);
        }
    }

    private void SelectLine(int row)
    {
        _selectionAnchor = (0, row);
        _selectionEnd = (Math.Max(0, _cols - 1), row);
    }

    private string GetSelectedText()
    {
        var emulator = Emulator;
        var (start, end) = NormalizedSelection();
        if (emulator == null || start.Row < 0)
            return "";
        var text = new StringBuilder();
        lock (emulator.SyncRoot)
        {
            var lines = emulator.Terminal.Buffer.Lines;
            for (var row = start.Row; row <= end.Row && row < lines.Length; row++)
            {
                var line = lines[row];
                if (line == null)
                    continue;
                var from = row == start.Row ? start.Col : 0;
                var to = row == end.Row ? Math.Min(end.Col + 1, line.Length) : line.Length;
                if (from < to)
                    text.Append(line.TranslateToString(true, from, to).ToString());
                var next = row + 1 < lines.Length ? lines[row + 1] : null;
                if (row < end.Row && !(next?.IsWrapped ?? false))
                    text.Append(Environment.NewLine);
            }
        }
        return text.ToString();
    }

    private async Task CopySelectionAsync()
    {
        var text = GetSelectedText();
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null && text.Length > 0)
            await clipboard.SetTextAsync(text);
    }

    private async Task PasteAsync()
    {
        var session = Session;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (session == null || clipboard == null)
            return;
        var text = await clipboard.TryGetTextAsync();
        if (string.IsNullOrEmpty(text))
            return;
        text = text.Replace("\r\n", "\r").Replace('\n', '\r');
        bool bracketed;
        lock (session.Emulator.SyncRoot)
            bracketed = session.Emulator.Terminal.BracketedPasteMode;
        if (bracketed)
            text = "\u001b[200~" + text.Replace("\u001b[201~", "") + "\u001b[201~";
        SendInput(text);
    }

    private ContextMenu BuildContextMenu()
    {
        var copy = new MenuItem { Header = "Copy", InputGesture = new KeyGesture(Key.C, KeyModifiers.Control | KeyModifiers.Shift) };
        copy.Click += (_, _) => _ = CopySelectionAsync();
        var paste = new MenuItem { Header = "Paste", InputGesture = new KeyGesture(Key.V, KeyModifiers.Control | KeyModifiers.Shift) };
        paste.Click += (_, _) => _ = PasteAsync();
        var selectAll = new MenuItem { Header = "Select all" };
        selectAll.Click += (_, _) =>
        {
            var emulator = Emulator;
            if (emulator == null)
                return;
            lock (emulator.SyncRoot)
            {
                _selectionAnchor = (0, 0);
                _selectionEnd = (Math.Max(0, _cols - 1), emulator.Terminal.Buffer.Lines.Length - 1);
            }
            emulator.MarkDirty();
        };
        return new ContextMenu { Items = { copy, paste, new Separator(), selectAll } };
    }
}
