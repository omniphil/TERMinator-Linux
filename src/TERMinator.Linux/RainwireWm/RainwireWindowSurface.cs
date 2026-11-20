using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace TERMinator.Linux.RainwireWm;

/// <summary>
/// One Rainwire window's content: its WmTerminal drawn with the main screen's own bitmap font, so boxes, blocks and
/// colours look exactly as they do on the main screen, scaled to the main screen's cell size. Takes the keyboard and
/// mouse for the window and sends them to the board as the window's input. Pressing and dragging (no modifier) starts
/// a Rainwire drag instead; if the board says there's nothing to drag there, the press and release go to the app as a
/// plain click. Ported from Windows' RainwireWm/RainwireWindowSurface.cs 2026-09-30.
/// </summary>
public sealed class RainwireWindowSurface : Control
{
    /// <summary>
    /// The app's first row is its header bar (the BBS name and the screen's title), which TERMinator's own title bar
    /// replaces, so it's kept out of sight: the surface shows rows 1 and down, and mouse rows count from there.
    /// </summary>
    public const int HiddenRows = 1;

    private readonly WmWindow _win;
    private readonly WmSession _session;
    private readonly RainwireDeskLayer _layer;
    private readonly Func<WmFont?> _font;
    private WriteableBitmap? _bitmap;
    private int[] _pixels = Array.Empty<int>();
    private int _drawnVersion = -1;
    private bool _redrawQueued;

    // Mouse: a left press is held until we know whether it is a click or a drag
    private enum Press { None, Held, Dragging, Passing }
    private Press _press;
    private (int X, int Y) _pressCell;
    private bool? _dragOk;           // the board's answer, once it comes
    private Point? _releasedAt;      // released before the answer came
    private (int X, int Y) _lastMotionCell = (-1, -1);
    private IPointer? _pointer;      // the pointer we captured (Avalonia has no IsMouseCaptured)
    private Point _lastLayerPoint;   // where the pointer last was over the desk (Avalonia has no Mouse.GetPosition)

    public RainwireWindowSurface(WmWindow win, WmSession session, RainwireDeskLayer layer, Func<WmFont?> font)
    {
        _win = win;
        _session = session;
        _layer = layer;
        _font = font;
        Focusable = true;
        FocusAdorner = null;
        ClipToBounds = true;
        WmPaint.Apply(this);
        Cursor = new Cursor(StandardCursorType.Ibeam);
    }

    public WmWindow Window => _win;

    /// <summary>The theme, font or smoothing changed: draw it all again.</summary>
    public void Refresh()
    {
        WmPaint.Apply(this);
        _drawnVersion = -1;
        InvalidateVisual();
    }

    private bool IsMouseCaptured => _pointer?.Captured == this;

    /// <summary>The window's output changed: redraw once the dispatcher gets to it.</summary>
    public void MarkDirty()
    {
        if (_redrawQueued)
            return;
        _redrawQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _redrawQueued = false;
            InvalidateVisual();
        }, DispatcherPriority.Render);
    }

    protected override void OnGotFocus(GotFocusEventArgs e)
    {
        base.OnGotFocus(e);
        _drawnVersion = -1;   // redraw with the cursor
        InvalidateVisual();
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        _drawnVersion = -1;
        InvalidateVisual();
    }

    public override void Render(DrawingContext dc)
    {
        var t = _win.Terminal;
        var font = _font();
        int fh = font?.Height ?? 16;
        int pw = t.Width * 8, ph = t.Height * fh;
        if (_bitmap == null || _bitmap.PixelSize.Width != pw || _bitmap.PixelSize.Height != ph)
        {
            _bitmap?.Dispose();
            _bitmap = WmPaint.NewBitmap(pw, ph);
            _pixels = new int[pw * ph];
            _drawnVersion = -1;
        }
        if (_drawnVersion != t.Version)
        {
            Paint(t, font, pw, fh);
            WmPaint.Copy(_pixels, pw, ph, _bitmap);
            _drawnVersion = t.Version;
        }
        var size = Bounds.Size;
        dc.FillRectangle(Brushes.Black, new Rect(size));
        double rowH = size.Height / Math.Max(1, t.Height - HiddenRows);
        using (dc.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = WmPaint.Interpolation }))
            dc.DrawImage(_bitmap, new Rect(0, 0, pw, ph), new Rect(0, -HiddenRows * rowH, size.Width, t.Height * rowH));
    }

    private void Paint(WmTerminal t, WmFont? font, int pw, int fh)
    {
        for (int cy = 0; cy < t.Height; cy++)
        {
            for (int cx = 0; cx < t.Width; cx++)
            {
                var cell = t[cx, cy];
                WmPaint.Glyph(_pixels, pw, cx, cy, cell.Ch, cell.Fg | unchecked((int)0xFF000000),
                    cell.Bg | unchecked((int)0xFF000000), font, fh, cell.Underline);
            }
        }
        foreach (var pic in t.Pictures)
        {
            int ox = pic.Col * 8, oy = pic.Row * fh;
            for (int y = 0; y < pic.Height && oy + y < t.Height * fh; y++)
                for (int x = 0; x < pic.Width && ox + x < pw; x++)
                {
                    int p = pic.Pixels[y * pic.Width + x];
                    if ((p & unchecked((int)0xFF000000)) != 0)
                        _pixels[(oy + y) * pw + ox + x] = p;
                }
        }
        if (IsFocused && t.CursorVisible && t.CursorX < t.Width && t.CursorY < t.Height)
        {
            // An underline cursor in the cell's text colour, as the main screen shows it
            int fg = t[t.CursorX, t.CursorY].Fg | unchecked((int)0xFF000000);
            for (int row = fh - 2; row < fh; row++)
                for (int col = 0; col < 8; col++)
                    _pixels[(t.CursorY * fh + row) * pw + t.CursorX * 8 + col] = fg;
        }
    }

    // ---- keyboard ----

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var key = e.Key;
        var mods = e.KeyModifiers;

        // Window keys: Ctrl+Tab and Ctrl+Shift+Tab switch windows, Ctrl+F4 closes this one
        if (key == Key.Tab && (mods & KeyModifiers.Control) != 0)
        {
            _layer.CycleFocus((mods & KeyModifiers.Shift) == 0);
            e.Handled = true;
            return;
        }
        if (key == Key.F4 && mods == KeyModifiers.Control)
        {
            _session.RequestClose(_win.Id);
            e.Handled = true;
            return;
        }
        if ((mods & KeyModifiers.Alt) != 0)
            return;   // Alt keys are TERMinator's own (menus, full screen)

        string? seq = key switch
        {
            Key.Enter => "\r",
            Key.Back => "\b",
            Key.Tab when (mods & KeyModifiers.Shift) != 0 => "\x1b[Z",
            Key.Tab => "\t",
            Key.Escape => "\x1b",
            Key.Up => "\x1b[A",
            Key.Down => "\x1b[B",
            Key.Right => "\x1b[C",
            Key.Left => "\x1b[D",
            Key.Home => "\x1b[H",
            Key.End => "\x1b[F",
            Key.Insert => "\x1b[2~",
            Key.Delete => "\x1b[3~",
            Key.PageUp => "\x1b[5~",
            Key.PageDown => "\x1b[6~",
            Key.F1 => "\x1bOP",
            Key.F2 => "\x1bOQ",
            Key.F3 => "\x1bOR",
            Key.F4 => "\x1bOS",
            Key.F5 => "\x1b[15~",
            Key.F6 => "\x1b[17~",
            Key.F7 => "\x1b[18~",
            Key.F8 => "\x1b[19~",
            Key.F9 => "\x1b[20~",
            Key.F10 => "\x1b[21~",
            Key.F11 => "\x1b[23~",
            Key.F12 => "\x1b[24~",
            _ => null,
        };
        if (seq == null && mods == KeyModifiers.Control)
        {
            if (key >= Key.A && key <= Key.Z)
                seq = ((char)(key - Key.A + 1)).ToString();          // Ctrl+A..Z: 0x01-0x1A
            else if (key == Key.OemOpenBrackets)
                seq = "\x1b";
        }
        if (seq != null)
        {
            _session.Input(_win.Id, Encoding.ASCII.GetBytes(seq));
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        var bytes = new List<byte>();
        foreach (char c in e.Text ?? "")
            if (Cp437.FromChar(c) is byte b)
                bytes.Add(b);
        if (bytes.Count > 0)
        {
            _session.Input(_win.Id, bytes.ToArray());
            e.Handled = true;
        }
        base.OnTextInput(e);
    }

    // ---- mouse ----

    /// <summary>The 0-based cell under a point in this surface.</summary>
    public (int X, int Y) CellAt(Point p)
    {
        var t = _win.Terminal;
        double cw = Bounds.Width / t.Width, ch = Bounds.Height / Math.Max(1, t.Height - HiddenRows);
        int x = (int)Math.Floor(p.X / Math.Max(cw, 1e-6)), y = (int)Math.Floor(p.Y / Math.Max(ch, 1e-6)) + HiddenRows;
        return (Math.Clamp(x, 0, t.Width - 1), Math.Clamp(y, HiddenRows, t.Height - 1));
    }

    private void Report(int button, (int X, int Y) cell, bool press)
    {
        var t = _win.Terminal;
        if (!t.MouseReporting)
            return;
        int x = cell.X + 1, y = cell.Y + 1;   // mouse reports are 1-based within the window
        byte[] b;
        if (t.MouseSgr)
            b = Encoding.ASCII.GetBytes($"\x1b[<{button};{x};{y}{(press ? 'M' : 'm')}");
        else
        {
            int code = press ? button : 3;
            b = new byte[] { 0x1b, (byte)'[', (byte)'M', (byte)(32 + code), (byte)Math.Min(32 + x, 255), (byte)Math.Min(32 + y, 255) };
        }
        _session.Input(_win.Id, b);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        Focus();
        _layer.Activate(_win.Id);
        var cell = CellAt(e.GetPosition(this));
        _lastLayerPoint = e.GetPosition(_layer);
        var kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
        if (kind == PointerUpdateKind.LeftButtonPressed && e.KeyModifiers == KeyModifiers.None)
        {
            // Held until it's either a click (released in place) or a drag (moved to another cell)
            _press = Press.Held;
            _pressCell = cell;
            _dragOk = null;
            _releasedAt = null;
            _pointer = e.Pointer;
            e.Pointer.Capture(this);
        }
        else if (kind is PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.RightButtonPressed)
            Report(kind == PointerUpdateKind.MiddleButtonPressed ? 1 : 2, cell, true);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var pos = e.GetPosition(this);
        var cell = CellAt(pos);
        _lastLayerPoint = e.GetPosition(_layer);
        bool leftDown = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed;
        switch (_press)
        {
            case Press.Held when leftDown && cell != _pressCell:
                // Moved off the cell it was pressed in: a drag. The board says what, if anything, is there.
                _press = Press.Dragging;
                _session.Drag(_win.Id, _pressCell.X, _pressCell.Y);
                break;
            case Press.Dragging when _dragOk == true:
                _layer.MoveDragLabel(_lastLayerPoint);
                break;
            case Press.Passing when leftDown && _win.Terminal.MouseMotion && cell != _lastMotionCell:
                // A drag the app wants (button-motion reporting): button 0 plus motion
                _lastMotionCell = cell;
                Report(32, cell, true);
                break;
        }
        base.OnPointerMoved(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        var pos = e.GetPosition(this);
        var cell = CellAt(pos);
        _lastLayerPoint = e.GetPosition(_layer);
        if (e.InitialPressMouseButton == MouseButton.Left)
        {
            switch (_press)
            {
                case Press.Held:
                    Report(0, _pressCell, true);
                    Report(0, cell, false);
                    EndPress();
                    break;
                case Press.Dragging when _dragOk == null:
                    _releasedAt = _lastLayerPoint;   // finish when the board answers
                    break;
                case Press.Dragging:
                    FinishDrag(_lastLayerPoint);
                    break;
                case Press.Passing:
                    Report(0, cell, false);
                    EndPress();
                    break;
            }
        }
        else if (e.InitialPressMouseButton is MouseButton.Middle or MouseButton.Right)
            Report(e.InitialPressMouseButton == MouseButton.Middle ? 1 : 2, cell, false);
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (e.Delta.Y != 0)   // sideways scrolling isn't reported (WPF's wheel is vertical only)
            Report(e.Delta.Y > 0 ? 64 : 65, CellAt(e.GetPosition(this)), true);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        // (Avalonia also drops the capture by itself when the button is released)
        if (_press == Press.Dragging && _dragOk == null && _releasedAt != null)
            return;   // still waiting on the board's answer to finish it
        if (_press != Press.None && !IsMouseCaptured)
        {
            _layer.HideDragLabel();
            _press = Press.None;
        }
    }

    /// <summary>The board's answer to our Drag.</summary>
    public void OnDragInfo(bool ok, string label)
    {
        if (_press != Press.Dragging || _dragOk != null)
            return;
        _dragOk = ok;
        if (ok)
        {
            _layer.ShowDragLabel(label, _lastLayerPoint);
            if (_releasedAt is Point at)
                FinishDrag(at);
        }
        else
        {
            // Nothing to drag there: it was a click (or a drag the app wants) after all
            _press = Press.Passing;
            Report(0, _pressCell, true);
            if (_releasedAt is Point at)
            {
                Report(0, CellAt(_layer.TranslatePoint(at, this) ?? default), false);
                EndPress();
            }
        }
    }

    private void FinishDrag(Point layerPoint)
    {
        if (_dragOk == true)
        {
            var (to, tx, ty) = _layer.DropTarget(layerPoint);
            _session.Drop(_win.Id, _pressCell.X, _pressCell.Y, to, tx, ty);
        }
        EndPress();
    }

    private void EndPress()
    {
        _press = Press.None;
        _layer.HideDragLabel();
        if (IsMouseCaptured)
            _pointer!.Capture(null);
        _pointer = null;
    }
}
