using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace TERMinator.Linux.Controls;

/// <summary>
/// The mouse for a TRACE module, in the mode the module picked (engine_contract.h TE_MOUSE_*):
///   0 off       the mouse stays with the terminal (the default)
///   1 pointer   positions in the module's frame pixels + buttons, the pointer shown
///   2 hidden    the same with the pointer hidden over the picture (the module draws its own)
///   3 captured  relative movement + buttons, the pointer hidden and held inside the picture while the window is
///               active (mouselook). Switching away lets it go; a click on the picture takes it again (an exclusive
///               game takes it straight back when the window is activated with the pointer over it).
/// A module drawn over part of the screen takes the clicks over its picture while the keyboard stays with the door.
///
/// Linux port of the Windows client's Controls/EngineHostLayer.Mouse.cs. The difference is captured mode: Windows
/// reads raw input (WM_INPUT) and pins the pointer with ClipCursor, neither of which Avalonia has. Here the layer
/// captures the pointer, hides it, and takes the movement as the distance from the middle of the layer, warping the
/// pointer back to the middle after each move (X11 XWarpPointer). The module sees the same relative counts either
/// way. Under Wayland there is no warp, so the deltas are plain pointer movement and stop at the screen edge --
/// the picture still turns, it just can't turn forever in one direction.
/// </summary>
public sealed partial class EngineHostLayer
{
    /// <summary>How far the pointer may drift from the middle before it's warped back (device pixels).</summary>
    private const double RecentreDistance = 64;

    private int _mouseMode;
    private bool _captured;
    private int _buttonsHeld;            // pointer modes: bit0 left, bit1 middle, bit2 right
    private readonly HashSet<int> _capturedButtons = [];
    private double _wheelRemainder;
    private (int X, int Y, bool Over, int Buttons) _lastPos = (int.MinValue, 0, false, 0);
    private Point? _lastCapturedPoint;   // captured mode: where the pointer was, to take the difference from
    private IPointer? _capturedPointer;

    /// <summary>The mouse mode the module picked (0 = the mouse is the terminal's).</summary>
    public int MouseMode => _mouseMode;

    private void InitMouse()
    {
        _module.MouseModeChanged += mode => Dispatcher.UIThread.Post(() => SetMouseMode(mode));
        PointerMoved += OnLayerPointerMoved;
        PointerPressed += OnLayerPointerPressed;
        PointerReleased += OnLayerPointerReleased;
        PointerWheelChanged += OnLayerPointerWheel;
        PointerExited += (_, _) =>
        {
            if (_mouseMode is 1 or 2 && _buttonsHeld == 0)
                SendPos(_lastPos.X == int.MinValue ? 0 : _lastPos.X, _lastPos.Y, false);
        };
    }

    private void SetMouseMode(int mode)
    {
        if (mode == _mouseMode || _closed)
            return;
        ReleaseMouse();
        ReleasePointerButtons();
        _mouseMode = mode;
        // Mode 0 on a drawing module: clicks go through to the terminal, as before
        IsHitTestVisible = _exclusiveInput || mode != 0;
        Cursor = mode is 2 or 3 ? new Cursor(StandardCursorType.None) : Cursor.Default;
        _lastPos = (int.MinValue, 0, false, 0);
        if (mode == 3 && _exclusiveInput && IsPointerOver)
            CaptureRelative();
    }

    // ---- pointer modes (1, 2): Avalonia pointer events, mapped to the module's frame pixels ----

    /// <summary>Where a point on the layer falls in the module's frame; false before the first frame.</summary>
    private bool ToFrame(PointerEventArgs e, out int x, out int y, out bool over)
    {
        x = y = 0;
        over = false;
        if (_frameWidth <= 0 || _frameHeight <= 0 || _image.Bounds.Width <= 0 || _image.Bounds.Height <= 0)
            return false;
        Point p = e.GetPosition(_image);
        double fx = p.X * _frameWidth / _image.Bounds.Width, fy = p.Y * _frameHeight / _image.Bounds.Height;
        over = fx >= 0 && fy >= 0 && fx < _frameWidth && fy < _frameHeight;
        x = Math.Clamp((int)Math.Floor(fx), 0, _frameWidth - 1);
        y = Math.Clamp((int)Math.Floor(fy), 0, _frameHeight - 1);
        return true;
    }

    private void SendPos(int x, int y, bool over)
    {
        var pos = (x, y, over, _buttonsHeld);
        if (pos == _lastPos)
            return;
        _lastPos = pos;
        _module.SendMousePos(x, y, over, _buttonsHeld);
    }

    /// <summary>Which button an event is about: 1 left, 2 middle, 3 right, 6 back, 7 forward.</summary>
    private static int ButtonNumber(PointerUpdateKind kind) => kind switch
    {
        PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased => 1,
        PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => 2,
        PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => 3,
        PointerUpdateKind.XButton1Pressed or PointerUpdateKind.XButton1Released => 6,
        _ => 7,
    };

    private static int HeldBit(int button) => button switch { 1 => 1, 2 => 2, 3 => 4, _ => 0 };

    private void OnLayerPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_mouseMode == 3)
        {
            HandleCapturedMove(e);
            e.Handled = true;
            return;
        }
        if (_mouseMode is not (1 or 2))
            return;
        if (ToFrame(e, out int x, out int y, out bool over))
            SendPos(x, y, over);
        e.Handled = true;
    }

    private void OnLayerPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_exclusiveInput)
            Focus();   // a game has the keyboard; a drawing module leaves it with the door
        switch (_mouseMode)
        {
            case 1 or 2:
                if (!ToFrame(e, out int x, out int y, out bool over))
                    return;
                SendPos(x, y, over);
                int button = ButtonNumber(e.GetCurrentPoint(this).Properties.PointerUpdateKind);
                _buttonsHeld |= HeldBit(button);
                _module.SendMouseButton(button, true);
                SendPos(x, y, over);   // the held buttons changed
                e.Pointer.Capture(this);   // a drag that leaves the picture still ends here
                _capturedPointer = e.Pointer;
                e.Handled = true;
                break;
            case 3:
                if (!_captured)
                {
                    CaptureRelative(e.Pointer);   // this click only takes the mouse; the module doesn't see it
                }
                else
                {
                    // Once captured, the buttons ARE the game's (fire, alt-fire, zoom). Windows gets these from
                    // raw input; there is no such path here, so they come from the ordinary pointer events.
                    int captured = ButtonNumber(e.GetCurrentPoint(this).Properties.PointerUpdateKind);
                    if (_capturedButtons.Add(captured))
                        _module.SendMouseButton(captured, true);
                }
                e.Handled = true;
                break;
        }
    }

    private void OnLayerPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_mouseMode is 3)
        {
            int captured = ButtonNumber(e.GetCurrentPoint(this).Properties.PointerUpdateKind);
            if (_capturedButtons.Remove(captured))
                _module.SendMouseButton(captured, false);
            e.Handled = true;
            return;
        }
        if (_mouseMode is not (1 or 2))
            return;
        int button = ButtonNumber(e.GetCurrentPoint(this).Properties.PointerUpdateKind);
        int bit = HeldBit(button);
        if (bit != 0 && (_buttonsHeld & bit) == 0)
            return;   // pressed before the mode was picked
        _buttonsHeld &= ~bit;
        if (ToFrame(e, out int x, out int y, out bool over))
            SendPos(x, y, over);
        _module.SendMouseButton(button, false);
        if (_buttonsHeld == 0)
            ReleasePointerCapture();
        e.Handled = true;
    }

    private void OnLayerPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_mouseMode is not (1 or 2 or 3))
            return;
        if (_mouseMode is 1 or 2 && ToFrame(e, out int x, out int y, out bool over))
            SendPos(x, y, over);
        SendWheel(e.Delta.Y);
        e.Handled = true;
    }

    /// <summary>Wheel movement in notches (Avalonia counts 1.0 a notch): a press then a release each.</summary>
    private void SendWheel(double notches)
    {
        _wheelRemainder += notches;
        while (Math.Abs(_wheelRemainder) >= 1.0)
        {
            int button = _wheelRemainder > 0 ? 4 : 5;
            _module.SendMouseButton(button, true);
            _module.SendMouseButton(button, false);
            _wheelRemainder -= Math.Sign(_wheelRemainder);
        }
    }

    private void ReleasePointerButtons()
    {
        foreach (int button in new[] { 1, 2, 3 })
            if ((_buttonsHeld & HeldBit(button)) != 0)
                _module.SendMouseButton(button, false);
        _buttonsHeld = 0;
        _wheelRemainder = 0;
        ReleasePointerCapture();
    }

    private void ReleasePointerCapture()
    {
        _capturedPointer?.Capture(null);
        _capturedPointer = null;
    }

    // ---- captured mode (3): the pointer held inside the picture, movement as relative counts ----

    private void CaptureRelative(IPointer? pointer = null)
    {
        if (_captured || _mouseMode != 3 || _window is not { IsActive: true } || !IsVisible)
            return;
        _captured = true;
        _lastCapturedPoint = null;
        if (pointer != null)
        {
            pointer.Capture(this);
            _capturedPointer = pointer;
        }
        Recentre();
    }

    /// <summary>
    /// Captured movement: the difference from where the pointer was last seen, then back to the middle of the layer
    /// so it can keep going the same way. The first move after a warp is the warp itself, which Recentre discounts.
    /// </summary>
    private void HandleCapturedMove(PointerEventArgs e)
    {
        if (!_captured)
            return;
        Point p = e.GetPosition(this);
        if (_lastCapturedPoint is { } last)
        {
            int dx = (int)Math.Round(p.X - last.X), dy = (int)Math.Round(p.Y - last.Y);
            if (dx != 0 || dy != 0)
                _module.SendMouseMove(dx, dy);
        }
        _lastCapturedPoint = p;
        Point middle = new(Bounds.Width / 2, Bounds.Height / 2);
        if (Math.Abs(p.X - middle.X) > RecentreDistance || Math.Abs(p.Y - middle.Y) > RecentreDistance)
            Recentre();
    }

    /// <summary>Puts the pointer back in the middle of the layer (X11 only; elsewhere movement is simply not warped).</summary>
    private void Recentre()
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0 || _window == null)
            return;
        Point middle = new(Bounds.Width / 2, Bounds.Height / 2);
        if (!X11.WarpPointerTo(_window, this, middle))
            return;
        _lastCapturedPoint = middle;   // the warp itself must not count as movement
    }

    private void ReleaseMouse()
    {
        if (!_captured)
            return;
        _captured = false;
        _lastCapturedPoint = null;
        ReleasePointerCapture();
        foreach (int button in _capturedButtons)
            _module.SendMouseButton(button, false);   // nothing stays held down in the game
        _capturedButtons.Clear();
    }

    /// <summary>The window lost focus or the layer went away: give the mouse back and let go of held buttons.</summary>
    private void MouseLost()
    {
        ReleaseMouse();
        ReleasePointerButtons();
    }

    /// <summary>The window became active again: an exclusive game in captured mode takes the mouse straight back.</summary>
    private void MouseRegained()
    {
        if (_mouseMode == 3 && _exclusiveInput && IsPointerOver)
            CaptureRelative();
    }

    private void FreeMouse() => MouseLost();

    /// <summary>
    /// Just enough X11 to put the pointer back where we want it. Wayland has no equivalent a client may call, so
    /// everything here quietly does nothing there and captured movement is plain pointer movement instead.
    /// </summary>
    private static class X11
    {
        private static IntPtr s_display;
        private static bool s_tried;

        [DllImport("libX11.so.6")]
        private static extern IntPtr XOpenDisplay(IntPtr name);

        [DllImport("libX11.so.6")]
        private static extern int XWarpPointer(IntPtr display, IntPtr srcWindow, IntPtr destWindow,
                                               int srcX, int srcY, uint srcWidth, uint srcHeight, int destX, int destY);

        [DllImport("libX11.so.6")]
        private static extern int XFlush(IntPtr display);

        private static IntPtr Display
        {
            get
            {
                if (!s_tried)
                {
                    s_tried = true;
                    try { s_display = XOpenDisplay(IntPtr.Zero); }
                    catch (DllNotFoundException) { s_display = IntPtr.Zero; }
                    catch (EntryPointNotFoundException) { s_display = IntPtr.Zero; }
                }
                return s_display;
            }
        }

        /// <summary>Moves the pointer to a point on a control. False when this isn't X11 (nothing was moved).</summary>
        public static bool WarpPointerTo(Window window, Visual control, Point point)
        {
            if (Display == IntPtr.Zero || window.TryGetPlatformHandle() is not { } handle || handle.Handle == IntPtr.Zero)
                return false;
            // X11 wants the position in the window's own pixels: through the window, then out of device-independent units
            if (control.TranslatePoint(point, window) is not { } inWindow)
                return false;
            double scale = window.RenderScaling;
            XWarpPointer(Display, IntPtr.Zero, handle.Handle, 0, 0, 0, 0,
                         (int)Math.Round(inWindow.X * scale), (int)Math.Round(inWindow.Y * scale));
            XFlush(Display);
            return true;
        }
    }
}
