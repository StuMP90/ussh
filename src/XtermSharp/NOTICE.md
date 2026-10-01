# Vendored XtermSharp

Source: https://github.com/migueldeicaza/XtermSharp at commit
`1bed529cd20748ef9da43753e8dd553f65fc5582` (MIT, see `LICENSE`).

The upstream NuGet package (`1.0.0-alpha.10`) is unmaintained and throws
`NotImplementedException` from code paths that real servers hit, which would
kill a terminal session. It is vendored so those can be fixed here.

Local changes (search for `ussh:`):

- `Terminal.MatchColor` implemented (nearest 256-colour match). Upstream threw on
  every truecolor SGR sequence (`ESC[38;2;r;g;bm` / `ESC[48;2;r;g;bm`).
- `Terminal.EmitA11yTab` made a no-op (threw when `ScreenReaderMode` was on).
- `Terminal.EmitScroll` cleaned up (dead `throw`).
- `TerminalOptions.Scrollback` / `TabStopWidth` made settable (were get-only,
  fixing scrollback at 1000 lines).
- `EscapeSequenceParser`: numeric parameters clamped to 65535 and at most 32
  parameters. Upstream accumulated unbounded (and overflowed), so a single
  `ESC[999999999M` hung the terminal for minutes.
- `Buffer.RestoreCursor` clamps to the current size (cursor could be restored
  off-screen after a resize, then the next printed character hit a null line).
- `Terminal.ReverseIndex` no longer moves the cursor above row 0.
- `Terminal.UpdateRange` clamps instead of throwing.
- SGR 38/48 (extended colours) bounds-checked; truncated sequences threw.
- `TerminalOptions.ReflowOnResize` added and honoured (default off). Upstream always
  reflowed, and the reflow code throws on common narrowing resizes. Lines are
  now trimmed on narrowing whether or not reflow is on.
- Character width: `Print` used a hard-coded width of 1; it now uses
  `RuneHelper.ConsoleWidth` (wcwidth), extended with emoji ranges.
- `Terminal.Report` (debug output) no longer writes to the console.
- `CSI 20 t` / `CSI 21 t` (report icon/window title) always report an empty title
  (title-report injection; also threw when no title was set).
- `CircularList`: `Trimmed` event invoked null-safely (threw when the list grew past
  capacity during reverse-index scrolling).
- `InsertColumn` / `DeleteColumn` include the scroll region's bottom row and skip
  missing rows (NullReferenceException).
- `Buffer.EnsureLine` added; `Print` uses it so a missing cursor row is materialised
  instead of dereferencing null.
- `Pty.cs` removed (native `forkpty` for local shells; not needed for SSH and
  not allowed in an MSIX/Store build).
