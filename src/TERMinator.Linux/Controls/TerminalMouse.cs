using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using R = TERMinator.Linux.Controls.TerminalMouseRules;

namespace TERMinator.Linux.Controls;

/// <summary>
/// xterm mouse reporting for the main terminal, as SyncTERM does it. When a board or door turns the mouse on
/// (CSI ? 1000 h, 1002, 1003, 9, with 1006 for SGR), clicks, drags and the wheel over the terminal go to it as
/// mouse reports (native/mouse_bridge.c encodes and sends them); otherwise the mouse is TERMinator's own, exactly as
/// before: left-drag selects, a click opens a URL, the wheel scrolls back. Shift always keeps the mouse for
/// TERMinator, so Shift+drag still selects while a board has it.
///
/// It listens to the canvas's pointer events on the tunnelling route (Avalonia's version of WPF's Preview events),
/// which arrive before TerminalCanvas's own handlers; an event that goes to the board is marked handled, so selection
/// and scrollback never see it. Layers above the canvas (TRACE modules, Rainwire's windows, the screensaver and other
/// overlays) are hit first and keep their mouse. Ported from Windows' Controls/TerminalMouse.cs 2026-09-30.
///
/// A libsyncterm-native.so built before mouse_bridge.c has none of its exports: then Available is false and the
/// canvas behaves exactly as it did.
/// </summary>
internal sealed class TerminalMouse
{
    private const string DllName = "libsyncterm-native";

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void syncterm_mouse_set_enabled(int enabled);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int syncterm_mouse_mode();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int syncterm_mouse_event(int kind, int button, int x, int y);

    public static bool Available { get; private set; } = true;

    private readonly TerminalCanvas _canvas;
    private int _held;                        // a bit per button (left, middle, right) whose press went to the board
    private (int X, int Y) _last = (-1, -1);  // the last cell reported, so a move within a cell sends nothing
    private IPointer? _recapture;             // a button came up with another still held (see OnLostCapture)

    private TerminalMouse(TerminalCanvas canvas)
    {
        _canvas = canvas;
        canvas.AddHandler(InputElement.PointerPressedEvent, OnDown, RoutingStrategies.Tunnel);
        canvas.AddHandler(InputElement.PointerReleasedEvent, OnUp, RoutingStrategies.Tunnel);
        canvas.AddHandler(InputElement.PointerMovedEvent, OnMove, RoutingStrategies.Tunnel);
        canvas.AddHandler(InputElement.PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
        canvas.PointerCaptureLost += OnLostCapture;
    }

    /// <summary>Called from TerminalCanvas's constructor: switches reporting on in the library and starts listening.</summary>
    public static void Attach(TerminalCanvas canvas)
    {
        Call(() => syncterm_mouse_set_enabled(1));
        _ = new TerminalMouse(canvas);
    }

    private static int Mode() => R.Mode(Call(syncterm_mouse_mode));

    private static int Send(int kind, int button, (int X, int Y) cell) =>
        Call(() => syncterm_mouse_event(kind, button, cell.X, cell.Y));

    private static int Call(Func<int> f)
    {
        if (!Available)
            return 0;
        try
        {
            return f();
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            Available = false;
            Debug.WriteLine("TerminalMouse: this libsyncterm-native.so has no mouse reporting (rebuild native): " + ex.Message);
            return 0;
        }
    }

    private static void Call(Action a) => Call(() => { a(); return 0; });

    private (int X, int Y)? Cell(PointerEventArgs e)
    {
        var (col, row) = _canvas.CellAt(e.GetPosition(_canvas));
        return R.ReportCell(col, row, _canvas.Columns, _canvas.Rows);
    }

    // Avalonia names the button that changed in the event's PointerUpdateKind (WPF's ChangedButton)
    private int ButtonOf(PointerEventArgs e) => e.GetCurrentPoint(_canvas).Properties.PointerUpdateKind switch
    {
        PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased => R.Left,
        PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => R.Middle,
        PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => R.Right,
        _ => -1,
    };

    private static bool Shift(PointerEventArgs e) => (e.KeyModifiers & KeyModifiers.Shift) != 0;

    private void OnDown(object? sender, PointerPressedEventArgs e)
    {
        int button = ButtonOf(e);
        if (!R.PressToBoard(Mode(), button, Shift(e), _canvas.IsScrolledBack))
            return;
        if (Cell(e) is not { } cell)
            return;
        if (button == R.Left && _canvas.IsOscLinkCell(cell.X - 1, cell.Y - 1))
            return;  // a click on a board's OSC 8 link is ours (it opens the link), never reported (12.62)
        _canvas.CancelSelection();
        _canvas.Focus();
        if (_held == 0)
            e.Pointer.Capture(_canvas);  // the release and drags reach us even outside the canvas
        _held |= 1 << button;
        _last = cell;
        Send(R.Press, button, cell);
        e.Handled = true;
    }

    private void OnUp(object? sender, PointerReleasedEventArgs e)
    {
        int button = ButtonOf(e);
        if (button < 0 || (_held & (1 << button)) == 0)
            return;  // not a press the board had (Shift+click, or before it turned the mouse on)
        _held &= ~(1 << button);
        Send(R.Release, button, Cell(e) ?? _last);
        if (_held == 0 && e.Pointer.Captured == _canvas)
            e.Pointer.Capture(null);
        else if (_held != 0)
            _recapture = e.Pointer;
        e.Handled = true;
    }

    private void OnMove(object? sender, PointerEventArgs e)
    {
        int mode = Mode();
        if (!R.MoveToBoard(mode, _held != 0, Shift(e)))
            return;
        if (Cell(e) is not { } cell || cell == _last)
            return;
        _last = cell;
        Send(R.Move, R.MoveButton(_held), cell);
        // Not marked handled: the canvas only shows the hand cursor over URLs here, and never selects without a
        // press of its own.
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.Delta.Y == 0)
            return;  // sideways scrolling: WPF's wheel event is vertical only, and so are the reports
        if (!R.WheelToBoard(Mode(), Shift(e), _canvas.IsScrolledBack))
            return;
        if (Cell(e) is not { } cell)
            return;
        int button = e.Delta.Y > 0 ? R.WheelUp : R.WheelDown;
        // Avalonia's delta is 1.0 per notch; the rules count in WPF's 120 per notch
        for (int i = R.Notches((int)Math.Round(e.Delta.Y * 120)); i > 0; i--)
            Send(R.Press, button, cell);
        e.Handled = true;
    }

    // Capture lost mid-press (the window lost focus, a dialog opened): let the board know the buttons are up, so
    // nothing stays held on its side.
    private void OnLostCapture(object? sender, PointerCaptureLostEventArgs e)
    {
        // Avalonia drops the capture by itself on every button release (WPF keeps it until released): a button that
        // came up while another is still held isn't a lost capture, so take it back and keep the held one held
        if (_recapture is { } pointer && _held != 0)
        {
            _recapture = null;
            Dispatcher.UIThread.Post(() =>
            {
                if (_held != 0)
                    pointer.Capture(_canvas);
            });
            return;
        }
        _recapture = null;
        for (int b = R.Left; b <= R.Right; b++)
        {
            if ((_held & (1 << b)) != 0)
                Send(R.Release, b, _last);
        }
        _held = 0;
    }
}
