using System.Text;
using Ussh.Core.Terminal;
using XtermSharp;

namespace Ussh.Core.Tests;

public class TerminalTests
{
    private static XtermSharp.Terminal NewTerminal(int cols = 80, int rows = 24, int scrollback = 1000) =>
        new(new SimpleTerminalDelegate(), new TerminalOptions { Cols = cols, Rows = rows, Scrollback = scrollback, ConvertEol = false });

    private static string Row(XtermSharp.Terminal t, int row) =>
        t.Buffer.TranslateBufferLineToString(t.Buffer.YBase + row, true, 0, -1).ToString()!;

    [Fact]
    public void TruecolorSequencesDoNotThrow()
    {
        // Regression: upstream XtermSharp threw NotImplementedException from MatchColor here.
        var t = NewTerminal();
        t.Feed("\u001b[38;2;255;0;0mred\u001b[48;2;0;0;255mblue\u001b[0m");

        Assert.Equal("redblue", Row(t, 0));
        var fg = (t.Buffer.GetChar(0, t.Buffer.YBase).Attribute >> 9) & 0x1ff;
        var color = Color.DefaultAnsiColors[fg];
        Assert.Equal((255, 0, 0), (color.Red, color.Green, color.Blue));
    }

    [Fact]
    public void Utf8SplitAcrossChunksIsDecoded()
    {
        var t = NewTerminal();
        var bytes = Encoding.UTF8.GetBytes("héllo → ✓");
        foreach (var b in bytes)
            t.Feed(new[] { b }, 1);

        Assert.Equal("héllo → ✓", Row(t, 0));
    }

    [Fact]
    public void WideCharactersOccupyTwoColumns()
    {
        var t = NewTerminal();
        t.Feed("a漢b😀c");

        // a(1) 漢(2) b(1) 😀(2) c(1): the cursor must agree with the remote shell's idea of width.
        Assert.Equal(7, t.Buffer.X);
    }

    [Fact]
    public void RestoreCursorAfterShrinkStaysOnScreen()
    {
        var t = NewTerminal(80, 50);
        t.Feed("\u001b[45;10H\u001b7");
        t.Resize(80, 20);
        t.Feed("\u001b8X");

        Assert.InRange(t.Buffer.Y, 0, 19);
    }

    [Fact]
    public void HugeRepeatCountsDoNotHang()
    {
        var t = NewTerminal();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        t.Feed("x\u001b[999999999b\u001b[999999999M\u001b[999999999L\u001b[999999999S\u001b[999999999@");

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
    }

    [Fact]
    public void NarrowingResizeDoesNotThrow()
    {
        var t = NewTerminal(120, 30);
        for (var i = 0; i < 200; i++)
            t.Feed(new string((char)('a' + i % 26), 110) + "\r\n");
        for (var cols = 119; cols >= 2; cols -= 7)
            t.Resize(cols, 30 - cols % 20);
        t.Resize(200, 60);
    }

    [Fact]
    public void ScrollbackIsBounded()
    {
        var emulator = new TerminalEmulator(80, 24, scrollbackLines: 500);
        var line = Encoding.UTF8.GetBytes("the quick brown fox jumps over the lazy dog\r\n");
        for (var i = 0; i < 20_000; i++)
            emulator.Feed(line, line.Length);

        Assert.True(emulator.Terminal.Buffer.Lines.Length <= 24 + 500);
    }

    [Fact]
    public void FuzzedInputNeverThrows()
    {
        // Feeds the raw engine (not the guarded wrapper) so any parser crash fails this test.
        var fragments = new[]
        {
            "\u001b[", "\u001b]", "\u001bP", "\u001b(", "\u001b)", "\u001b#", "\u001b[?", "\u001b[>", "\u001b[!",
            "\u001b7", "\u001b8", "\u001bD", "\u001bM", "\u001bE", "\u001bc", "\u001b=", "\u001b>",
            "38;2;", "48;5;", ";", "m", "H", "J", "K", "r", "h", "l", "@", "P", "L", "M", "S", "T", "X", "Z",
            "t", "q", "s", "u", "c", "n", "x", "g", "d", "G", "A", "B", "C", "D", "E", "F", "b", "\u0007", "\u001b\\",
            "$", "\"", "'", " ", "*", "`", "e", "a", "I", "i", "z", "{", "|", "}", "~", "p",
            "1049", "47", "1047", "1048", "1000", "1006", "2004", "25", "7", "6", "69", "999999", "-1", "0", "65535", "4294967296",
            "\r", "\n", "\t", "\b", "\u000b", "\u000c", "\u000e", "\u000f", "é", "✓", "漢", "😀", "́", "‍",
        };

        for (var seed = 0; seed < 300; seed++)
        {
            var random = new Random(seed);
            var t = NewTerminal(random.Next(2, 200), random.Next(1, 80), random.Next(0, 300));
            var input = new StringBuilder();
            for (var chunk = 0; chunk < 200; chunk++)
            {
                input.Clear();
                var parts = random.Next(1, 30);
                for (var i = 0; i < parts; i++)
                    input.Append(random.Next(4) == 0 ? random.Next(0, 1000).ToString() : fragments[random.Next(fragments.Length)]);
                try
                {
                    t.Feed(input.ToString());
                    if (random.Next(50) == 0)
                        t.Resize(random.Next(2, 200), random.Next(1, 80));
                    var bytes = new byte[random.Next(1, 64)];
                    random.NextBytes(bytes);
                    t.Feed(bytes, bytes.Length);
                }
                catch (Exception ex)
                {
                    Assert.Fail($"seed {seed} chunk {chunk} input {Escape(input.ToString())}: {ex}");
                }
            }
        }
    }

    private static string Escape(string s) =>
        string.Concat(s.Select(c => c < 32 || c == 127 ? $"\\x{(int)c:x2}" : c.ToString()));
}
