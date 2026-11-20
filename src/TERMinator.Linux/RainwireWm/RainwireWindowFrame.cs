using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace TERMinator.Linux.RainwireWm;

/// <summary>
/// The frame around one of the board's windows, drawn as cells in the board's theme exactly as Rainwire's
/// server-drawn desktop draws it: a title row (the title, then minimize, maximize and close on the right), the frame
/// down the sides and along the bottom, a resize grip in the bottom right corner, and a shadow if the theme has one.
/// The window's content (RainwireWindowSurface) sits inside. A window of w x h content cells is w+2 x h+1 cells
/// outside: the app's own header row hides under the title row.
///
/// Moving and resizing go in whole cells of the main screen and happen here with no round trip; the desk layer
/// tells the board the results (Moved, one Resized when a resize ends, State, Focused). The close button only asks:
/// the window goes when the board sends Close.
/// Ported from Windows' RainwireWm/RainwireWindowFrame.cs 2026-09-30.
/// </summary>
public sealed class RainwireWindowFrame : Canvas
{
    private readonly RainwireDeskLayer _layer;
    private readonly WmCellView _chrome;
    // The graphical look (WmChrome.cs) draws under _chrome, which stays as the click layer at opacity 0
    private readonly WmFrameChrome _look;
    private WmOutlineChrome? _lookOutline;
    private bool _focused, _max;
    private bool _resizeW, _resizeH;      // which way the current resize goes (grip: both; an edge: one)
    private double _startLeft, _startTop; // where the frame was in pixels when a move began

    // The resize cursors an edge shows (Avalonia makes them; WPF has them ready in Cursors)
    private static readonly Cursor SizeNwse = new(StandardCursorType.BottomRightCorner);
    private static readonly Cursor SizeWe = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor SizeNs = new(StandardCursorType.SizeNorthSouth);

    private enum Drag { None, Move, Resize, Button }
    private Drag _drag;
    private Point _dragStart;
    private int _startX, _startY, _button;
    private WmCellView? _outline;
    private IPointer? _pointer;   // the pointer a drag captured (Avalonia has no IsMouseCaptured)
    private (int Cols, int Rows) _outlineSize;

    public RainwireWindowSurface Surface { get; }
    public int Id => Surface.Window.Id;
    /// <summary>Where the frame's top-left corner is, in main-screen cells.</summary>
    public int CellX { get; set; }
    public int CellY { get; set; }
    /// <summary>Where it was and how big before it was maximized: cell position and content size.</summary>
    public (int X, int Y, int Cols, int Rows)? Restore { get; set; }

    /// <summary>Outside size in cells, frame included, shadow not.</summary>
    public int OuterW => Surface.Window.Terminal.Width + 2;
    public int OuterH => Surface.Window.Terminal.Height + 1;

    public RainwireWindowFrame(RainwireWindowSurface surface, RainwireDeskLayer layer)
    {
        Surface = surface;
        _layer = layer;
        _chrome = new WmCellView(() => layer.FontFor(""));
        _chrome.PointerPressed += ChromeDown;
        _chrome.PointerMoved += ChromeMove;
        _chrome.PointerReleased += ChromeUp;
        // (Avalonia also drops the capture by itself after a release, by which time ChromeUp has ended the drag)
        _chrome.PointerCaptureLost += (_, _) => EndDrag(false);
        _chrome.PointerExited += (_, _) => SetHover(-1);
        _look = new WmFrameChrome(() => _layer.Theme);
        Children.Add(_look);
        Children.Add(_chrome);
        Children.Add(Surface);
        ApplyLook();
    }

    /// <summary>The look setting changed (or the frame is new): graphical, or the board's cells.</summary>
    public void ApplyLook()
    {
        bool g = WmLook.Graphical;
        _look.IsVisible = g;
        _chrome.Opacity = g ? 0 : 1;
        Redraw();
    }

    public void SetTitle(string title) => Redraw();

    public void SetFocused(bool focused)
    {
        _focused = focused;
        Redraw();
    }

    public void SetMaximized(bool max)
    {
        _max = max;
        Redraw();
    }

    /// <summary>Lays the frame out for its window's size at the main screen's cell size.</summary>
    public void Fit(double cellW, double cellH)
    {
        var t = Surface.Window.Terminal;
        int shadow = _layer.Theme.Shadow != null ? 1 : 0;
        _chrome.Resize(OuterW + shadow, OuterH + shadow);
        _chrome.Width = (OuterW + shadow) * cellW;
        _chrome.Height = (OuterH + shadow) * cellH;
        Width = _chrome.Width;
        Height = _chrome.Height;
        Surface.Width = t.Width * cellW;
        Surface.Height = (t.Height - RainwireWindowSurface.HiddenRows) * cellH;
        SetLeft(Surface, cellW);
        SetTop(Surface, cellH);
        _look.Width = OuterW * cellW;
        _look.Height = OuterH * cellH;
        Redraw();
    }

    /// <summary>The theme or font changed.</summary>
    public void Refresh()
    {
        _chrome.Refresh();
        Surface.Refresh();
    }

    /// <summary>Draws the frame's cells, as Rainwire's window manager does (internal/wm drawWindow).</summary>
    public void Redraw()
    {
        _look.Cols = OuterW;
        _look.Rows = OuterH;
        _look.Focused = _focused;
        _look.Max = _max;
        _look.Title = Surface.Window.Title;
        _look.InvalidateVisual();
        var th = _layer.Theme;
        int w = OuterW, h = OuterH;
        if (_chrome.Cols < w || _chrome.Rows < h)
            return;   // not fitted yet
        _chrome.Clear();
        int frame = _focused ? th.FrameFocus : th.Frame;
        int tfg = _focused ? th.FocusFg : th.TitleFg, tbg = _focused ? th.FocusBg : th.TitleBg;
        if (th.Shadow is int sh)
        {
            for (int y = 1; y <= h; y++)
                _chrome.Put(w, y, (byte)' ', sh, sh);
            for (int x = 1; x <= w; x++)
                _chrome.Put(x, h, (byte)' ', sh, sh);
        }
        for (int y = 1; y < h - 1; y++)
        {
            _chrome.Put(0, y, th.V, frame, th.Bg);
            _chrome.Put(w - 1, y, th.V, frame, th.Bg);
        }
        _chrome.Put(0, h - 1, th.BL, frame, th.Bg);
        for (int x = 1; x < w - 1; x++)
            _chrome.Put(x, h - 1, th.H, frame, th.Bg);
        _chrome.Put(w - 1, h - 1, _max ? th.BR : th.Grip[0], frame, th.Bg);

        // The title bar: the title on the left, the buttons on the right
        _chrome.Fill(0, 0, w, 1, tfg, tbg);
        var btns = Buttons(th);
        var title = WmTheme.Cp(" " + (Surface.Window.Title.Length > 0 ? Surface.Window.Title : "Rainwire"));
        int room = Math.Max(0, w - btns.Length - 1);
        _chrome.Text(0, 0, title.AsSpan(0, Math.Min(title.Length, room)), tfg, tbg);
        _chrome.Text(w - btns.Length, 0, btns, tfg, tbg);
        _chrome.Put(w - 2, 0, th.Buttons[2], th.Hot, tbg);
    }

    /// <summary>" _  □  × ": the buttons as the title row shows them.</summary>
    private static byte[] Buttons(WmTheme th) =>
        new[] { (byte)' ', th.Buttons[0], (byte)' ', (byte)' ', th.Buttons[1], (byte)' ', (byte)' ', th.Buttons[2], (byte)' ' };

    /// <summary>Which title button is at column x of the title row: 0 minimize, 1 maximize, 2 close, -1 none.</summary>
    private int ButtonAt(int x)
    {
        int from = OuterW - 9;
        if (x < from)
            return -1;
        return Math.Min((x - from) / 3, 2);
    }

    // ---- the mouse on the frame ----

    private void SetHover(int button)
    {
        if (_look.Hover == button)
            return;
        _look.Hover = button;
        _look.InvalidateVisual();
    }

    /// <summary>What a press at cell (x, y) of the frame would resize: the grip both ways, an edge one way.</summary>
    private (bool W, bool H) EdgeAt(int x, int y)
    {
        if (_max || y == 0)
            return (false, false);
        bool right = x == OuterW - 1, bottom = y == OuterH - 1;
        return (right, bottom);
    }

    private void ChromeDown(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(_chrome).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
            return;
        _layer.Activate(Id);
        var (x, y) = _chrome.CellAt(e.GetPosition(_chrome));
        _dragStart = e.GetPosition(_layer);
        if (y == 0 && ButtonAt(x) is int b and >= 0)
        {
            _drag = Drag.Button;
            _button = b;
        }
        else if (y == 0)
        {
            if (e.ClickCount == 2)
            {
                _layer.ToggleMaximize(this);
                e.Handled = true;
                return;
            }
            _drag = Drag.Move;
            _startX = CellX;
            _startY = CellY;
            _startLeft = GetLeft(this);
            _startTop = GetTop(this);
        }
        else if (EdgeAt(x, y) is (true, _) or (_, true))
        {
            // The grip both ways; the right or bottom edge one way (2026-10-05)
            (_resizeW, _resizeH) = EdgeAt(x, y);
            _drag = Drag.Resize;
            _outlineSize = (Surface.Window.Terminal.Width, Surface.Window.Terminal.Height);
            if (WmLook.Graphical)
            {
                _lookOutline = new WmOutlineChrome(() => _layer.Theme) { ZIndex = int.MaxValue };
                _layer.Children.Add(_lookOutline);
            }
            else
            {
                _outline = new WmCellView(() => _layer.FontFor("")) { IsHitTestVisible = false };
                _outline.ZIndex = int.MaxValue;
                _layer.Children.Add(_outline);
            }
            DrawOutline();
        }
        else
        {
            e.Handled = true;
            return;
        }
        _pointer = e.Pointer;
        e.Pointer.Capture(_chrome);
        e.Handled = true;
    }

    private void ChromeMove(object? sender, PointerEventArgs e)
    {
        if (_drag == Drag.None)
        {
            // Under the pointer: a title button lights up, and an edge shows which way it resizes
            var (cx, cy) = _chrome.CellAt(e.GetPosition(_chrome));
            SetHover(cy == 0 && ButtonAt(cx) is int hb and >= 0 ? hb : -1);
            _chrome.Cursor = EdgeAt(cx, cy) switch
            {
                (true, true) => SizeNwse,
                (true, false) => SizeWe,
                (false, true) => SizeNs,
                _ => null,
            };
            return;
        }
        if (_drag is not (Drag.Move or Drag.Resize))
            return;
        var p = e.GetPosition(_layer);
        var (cw, ch) = _layer.CellSize();
        int dx = (int)Math.Round((p.X - _dragStart.X) / cw), dy = (int)Math.Round((p.Y - _dragStart.Y) / ch);
        if (_drag == Drag.Move)
        {
            CellX = _startX + dx;
            CellY = Math.Max(0, _startY + dy);   // keep the title row reachable
            if (WmLook.Graphical)
            {
                // The window follows the pointer smoothly and lands on the grid when let go (EndDrag)
                SetLeft(this, _startLeft + p.X - _dragStart.X);
                SetTop(this, Math.Max(_startTop - _startY * ch, _startTop + p.Y - _dragStart.Y));
            }
            else
                _layer.Place(this);
        }
        else
        {
            var t = Surface.Window.Terminal;
            _outlineSize = (_resizeW ? Math.Clamp(t.Width + dx, WmTerminal.MinWidth, WmTerminal.MaxWidth) : t.Width,
                            _resizeH ? Math.Clamp(t.Height + dy, WmTerminal.MinHeight, WmTerminal.MaxHeight) : t.Height);
            DrawOutline();
        }
    }

    private void ChromeUp(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left)
            return;
        if (_drag == Drag.Button)
        {
            var (x, y) = _chrome.CellAt(e.GetPosition(_chrome));
            var p = e.GetPosition(_chrome);
            bool over = p.X >= 0 && p.Y >= 0 && p.X < _chrome.Bounds.Width && p.Y < _chrome.Bounds.Height;
            int b = _button;
            EndDrag(false);
            // Act on release over the same button, like a desktop
            if (over && y == 0 && ButtonAt(x) == b)
            {
                if (b == 0)
                    _layer.Minimize(this);
                else if (b == 1)
                    _layer.ToggleMaximize(this);
                else
                    _layer.RequestClose(this);
            }
        }
        else
            EndDrag(true);
        e.Handled = true;
    }

    private void EndDrag(bool commit)
    {
        var drag = _drag;
        _drag = Drag.None;
        if (_pointer?.Captured == _chrome)
            _pointer.Capture(null);
        _pointer = null;
        if (drag == Drag.Move)
            _layer.Place(this);   // onto the grid, from wherever the smooth drag left it
        if (drag == Drag.Move && commit && (CellX != _startX || CellY != _startY))
            _layer.Moved(this);
        if (drag == Drag.Resize && _lookOutline != null)
        {
            _layer.Children.Remove(_lookOutline);
            _lookOutline = null;
        }
        if (drag == Drag.Resize && _outline != null)
        {
            _layer.Children.Remove(_outline);
            _outline = null;
        }
        if (drag == Drag.Resize)
        {
            var t = Surface.Window.Terminal;
            if (commit && (_outlineSize.Cols != t.Width || _outlineSize.Rows != t.Height))
                _layer.Resize(this, _outlineSize.Cols, _outlineSize.Rows);
        }
    }

    /// <summary>While resizing, only an outline in the theme's outline colour follows the pointer.</summary>
    private void DrawOutline()
    {
        var (cw, ch) = _layer.CellSize();
        int w = _outlineSize.Cols + 2, h = _outlineSize.Rows + 1;
        if (_lookOutline != null)
        {
            _lookOutline.Cols = _outlineSize.Cols;
            _lookOutline.Rows = _outlineSize.Rows;
            _lookOutline.Width = w * cw;
            _lookOutline.Height = h * ch;
            SetLeft(_lookOutline, GetLeft(this));
            SetTop(_lookOutline, GetTop(this));
            _lookOutline.InvalidateVisual();
            return;
        }
        if (_outline == null)
            return;
        var th = _layer.Theme;
        _outline.Resize(w, h);
        _outline.Width = w * cw;
        _outline.Height = h * ch;
        for (int x = 1; x < w - 1; x++)
        {
            _outline.Put(x, 0, th.H, th.Outline, 0);
            _outline.Put(x, h - 1, th.H, th.Outline, 0);
        }
        for (int y = 1; y < h - 1; y++)
        {
            _outline.Put(0, y, th.V, th.Outline, 0);
            _outline.Put(w - 1, y, th.V, th.Outline, 0);
        }
        _outline.Put(0, 0, th.TL, th.Outline, 0);
        _outline.Put(w - 1, 0, th.TR, th.Outline, 0);
        _outline.Put(0, h - 1, th.BL, th.Outline, 0);
        _outline.Put(w - 1, h - 1, th.BR, th.Outline, 0);
        SetLeft(_outline, GetLeft(this));
        SetTop(_outline, GetTop(this));
    }
}
