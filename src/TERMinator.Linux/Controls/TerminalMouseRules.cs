namespace TERMinator.Linux.Controls;

/// <summary>
/// Who gets a mouse event on the main terminal: the board (an xterm mouse report, native/mouse_bridge.c) or
/// TERMinator itself (selection, scrollback, URL clicks). Plain values only, so tools/RainwireWmHarness can test it.
/// </summary>
internal static class TerminalMouseRules
{
    // The board's tracking modes, as native/mouse_bridge.c reports them (syncterm_mouse_mode, low 16 bits)
    public const int Off = 0, X10 = 9, Normal = 1000, ButtonEvent = 1002, AnyEvent = 1003;

    // Event kinds and buttons, as syncterm_mouse_event takes them
    public const int Press = 0, Release = 1, Move = 2;
    public const int Left = 0, Middle = 1, Right = 2, WheelUp = 3, WheelDown = 4;

    /// <summary>The tracking mode from syncterm_mouse_mode's value (which also carries the SGR flag).</summary>
    public static int Mode(int modeWord) => modeWord & 0xFFFF;

    /// <summary>
    /// Whether a button press goes to the board. Shift keeps the mouse for TERMinator (Shift+drag selects), as in
    /// most terminals; so does a scrolled-back view, where a click first returns to the live screen.
    /// </summary>
    public static bool PressToBoard(int mode, int button, bool shift, bool scrolledBack) =>
        mode != Off && !shift && !scrolledBack && button is >= Left and <= Right;

    /// <summary>
    /// Whether the wheel goes to the board. X10 never reports the wheel (SyncTERM keeps it for scrollback there);
    /// the other modes report it, and Shift+wheel still scrolls back.
    /// </summary>
    public static bool WheelToBoard(int mode, bool shift, bool scrolledBack) =>
        (mode is Normal or ButtonEvent or AnyEvent) && !shift && !scrolledBack;

    /// <summary>
    /// Whether a move to a new cell is reported: every move in any-event tracking, and moves with a button held in
    /// button-event tracking. Shift with no button held is the user's own (starting a selection).
    /// </summary>
    public static bool MoveToBoard(int mode, bool anyHeld, bool shift) =>
        (mode == AnyEvent && !(shift && !anyHeld)) || (mode == ButtonEvent && anyHeld);

    /// <summary>The lowest held button for a move report, or -1 when none is held. held is a bit per button.</summary>
    public static int MoveButton(int held) =>
        (held & 1) != 0 ? Left : (held & 2) != 0 ? Middle : (held & 4) != 0 ? Right : -1;

    /// <summary>Wheel reports for one wheel event in WPF units (TerminalMouse scales Avalonia's 1.0 per notch to 120): one per notch, at least one.</summary>
    public static int Notches(int delta) => Math.Max(1, Math.Abs(delta) / 120);

    /// <summary>A 0-based cell as the 1-based position reports use, or null outside the screen.</summary>
    public static (int X, int Y)? ReportCell(int col, int row, int cols, int rows) =>
        col < 0 || row < 0 || col >= cols || row >= rows ? null : (col + 1, row + 1);
}
