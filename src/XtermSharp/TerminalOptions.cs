using System;
namespace XtermSharp {
	public enum CursorStyle {
		BlinkBlock, SteadyBlock, BlinkUnderline, SteadyUnderline, BlinkingBar, SteadyBar
	}

	public class TerminalOptions {
		public int Cols, Rows;
		public bool ConvertEol = true, CursorBlink;
		public string TermName;
		public CursorStyle CursorStyle;
		public bool ScreenReaderMode;
		// zssh: added. Re-wrap long lines on resize. Off by default (like xterm); the reflow
		// implementation is not robust.
		public bool ReflowOnResize;
		// zssh: settable (upstream was get-only, so scrollback was fixed at 1000 lines).
		public int? Scrollback { get; set; }
		public int? TabStopWidth { get; set; }

		public TerminalOptions ()
		{
			Cols = 80;
			Rows = 25;
			TermName = "xterm";
			Scrollback = 1000;
			TabStopWidth = 8;
		}
	}
}
