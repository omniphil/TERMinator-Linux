using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using TERMinator.Linux.Services;

namespace TERMinator.Linux.RainwireWm;

/// <summary>
/// The desk: the board's windows drawn over the main screen, which stays behind them as the desktop, with a taskbar
/// on the main screen's bottom row (the start button, then a button per window) and the start menu above it.
///
/// TERMinator is only the engine here. Everything is drawn as cells on the main screen's own grid, with the bitmap
/// font, in the colours and glyphs the board sent in its Theme (and a font it sent, if it names one), exactly as
/// Rainwire's server-drawn desktop looks. Windows sit and move in whole main-screen cells, so their text lines up
/// with the main screen's. Empty parts of the layer let the mouse through to the main screen.
///
/// What the user does here goes to the board through WmSession: Moved when a move ends, one Resized when a resize
/// ends, State on minimize, maximize and restore, Focused when a window comes to the front, Close from the close
/// button (the window goes when the board says so), Launch from the start menu, Drag and Drop between windows.
/// Ported from Windows' RainwireWm/RainwireDeskLayer.cs 2026-09-30 (WPF Visibility is IsVisible, Panel.ZIndex is
/// ZIndex, ActualWidth/Height are Bounds; the start menu's subtree mouse capture is a tunnelling press handler on the
/// window while it's open).
/// </summary>
public sealed class RainwireDeskLayer : Canvas
{
    private readonly WmSession _session;
    private readonly Func<Control?> _mainScreen;
    private readonly Action _focusMain;
    private readonly Func<WmFont?> _terminalFont;
    private readonly Dictionary<int, RainwireWindowFrame> _frames = new();
    private readonly List<int> _opened = new();   // taskbar order
    private readonly WmCellView _taskbar;
    private readonly WmCellView _menu;
    private readonly WmCellView _dragLabel;
    private int _z = 1;
    private int _focusedId;

    // Taskbar and start menu hit areas, in cells
    private (int From, int To) _startHit;
    private readonly List<(int From, int To, int Id)> _taskHits = new();
    private int _menuSel = -1;   // -1: the start menu is shut
    private bool _startArmed;    // the start button is pressed: the menu opens on the release over it
    private TopLevel? _menuTop;  // where the "press outside shuts the menu" handler is while it's open
    // The graphical look (WmChrome.cs): drawn under the taskbar's and menu's cell views, which keep taking the clicks
    private readonly WmBarChrome _barLook;
    private readonly WmMenuChrome _menuLook;

    public RainwireDeskLayer(WmSession session, Func<Control?> mainScreen, Action focusMain, Func<WmFont?> font)
    {
        _session = session;
        _mainScreen = mainScreen;
        _focusMain = focusMain;
        _terminalFont = font;
        Background = null;   // hit-testing falls through to the main screen where there's no window
        ClipToBounds = true;
        IsVisible = false;

        _taskbar = new WmCellView(() => FontFor(""));
        _taskbar.PointerPressed += TaskbarDown;
        _taskbar.PointerReleased += TaskbarUp;
        _taskbar.PointerMoved += (_, e) => StartHover(_taskbar.CellAt(e.GetPosition(_taskbar)).X is var x && x >= _startHit.From && x < _startHit.To);
        _taskbar.PointerExited += (_, _) => StartHover(false);
        _taskbar.ZIndex = int.MaxValue - 2;
        Children.Add(_taskbar);
        _barLook = new WmBarChrome(() => Theme) { ZIndex = int.MaxValue - 3 };
        Children.Add(_barLook);

        _menu = new WmCellView(() => FontFor("")) { Focusable = true, IsVisible = false, FocusAdorner = null };
        _menu.PointerMoved += (_, e) => MenuHover(e.GetPosition(_menu));
        _menu.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left)
                MenuClick(e.GetPosition(_menu));
        };
        _menu.KeyDown += MenuKey;
        _menu.LostFocus += (_, _) => CloseMenu();
        _menu.ZIndex = int.MaxValue - 1;
        // Same z as the menu, added before it, so it draws underneath
        _menuLook = new WmMenuChrome(() => Theme) { IsVisible = false, ZIndex = int.MaxValue - 1 };
        Children.Add(_menuLook);
        Children.Add(_menu);
        _taskbar.Opacity = WmLook.Graphical ? 0 : 1;
        _menu.Opacity = WmLook.Graphical ? 0 : 1;

        _dragLabel = new WmCellView(() => FontFor("")) { IsHitTestVisible = false, IsVisible = false };
        _dragLabel.ZIndex = int.MaxValue;
        Children.Add(_dragLabel);

        SizeChanged += (_, _) => Relayout();

        _session.Started += () =>
        {
            IsVisible = true;
            // The first Open usually comes in the same poll as Hello: lay out now, so window sizes see the desk's size
            UpdateLayout();
            Relayout();
        };
        _session.ThemeChanged += () =>
        {
            foreach (var f in _frames.Values)
                f.Refresh();
            _taskbar.Refresh();
            _menu.Refresh();
            _dragLabel.Refresh();
            Relayout();   // a shadow may have come or gone
        };
        _session.Ended += EndDesk;
        _session.Opened += OnOpened;
        _session.Closed += OnClosed;
        _session.TitleChanged += w =>
        {
            if (_frames.TryGetValue(w.Id, out var f))
                f.SetTitle(w.Title);
            RefreshTaskbar();
        };
        _session.Output += w =>
        {
            if (_frames.TryGetValue(w.Id, out var f))
                f.Surface.MarkDirty();
        };
        _session.SuspendChanged += OnSuspendChanged;
        _session.DragInfo += (id, ok, label) =>
        {
            if (_frames.TryGetValue(id, out var f))
                f.Surface.OnDragInfo(ok, label);
        };
    }

    private double ActualWidth => Bounds.Width;
    private double ActualHeight => Bounds.Height;

    /// <summary>
    /// A drag the board said ok to (DragInfo) left the TERMinator window with the button down: run the OS drag
    /// (from, x, y as Drag said, the label). True if it ran. Set by RainwireFileDrops (files=1).
    /// </summary>
    public Func<int, int, int, string, bool>? DragOutOfWindow { get; set; }

    /// <summary>Where the main screen draws its picture inside itself, if not all of it.</summary>
    public Func<Control, Rect?>? PictureRect { get; set; }

    /// <summary>The board's look. ("new": Avalonia controls have a Theme of their own, the ControlTheme, which this hides.)</summary>
    public new WmTheme Theme => _session.Theme;

    /// <summary>
    /// The font to draw with: a font the board sent by that name, else the one its theme names, else the terminal's
    /// own (the main screen's).
    /// </summary>
    public WmFont? FontFor(string name) =>
        _session.FontNamed(name) ?? _session.FontNamed(_session.Theme.Font) ?? _terminalFont();

    /// <summary>The look setting changed: the graphical look, or the board's cells (WmChrome.cs).</summary>
    public void ApplyLook(bool graphical)
    {
        WmLook.Graphical = graphical;
        _taskbar.Opacity = graphical ? 0 : 1;
        _menu.Opacity = graphical ? 0 : 1;
        _barLook.IsVisible = graphical;
        foreach (var f in _frames.Values)
            f.ApplyLook();
        RefreshTaskbar();
        if (_menuSel >= 0)
            DrawMenu();
    }

    private void StartHover(bool over)
    {
        if (_barLook.StartHover == over)
            return;
        _barLook.StartHover = over;
        _barLook.InvalidateVisual();
    }

    /// <summary>The Font smoothing setting changed: everything follows the main screen.</summary>
    public void ApplySmoothing(bool smooth)
    {
        WmPaint.Smooth = smooth;
        foreach (var f in _frames.Values)
            f.Refresh();
        _taskbar.Refresh();
        _menu.Refresh();
        _dragLabel.Refresh();
    }

    // ---- geometry: everything is in the main screen's cells ----

    /// <summary>The main screen picture's top-left corner in this layer, its cell size, and its size in cells.</summary>
    private (Point Origin, double CellW, double CellH, int Cols, int Rows) Metrics()
    {
        var screen = _mainScreen();
        int cols = 0, rows = 0;
        try
        {
            cols = NativeBridge.syncterm_get_screen_width();
            rows = NativeBridge.syncterm_get_screen_height();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        if (screen == null || screen.Bounds.Width < 1 || cols <= 0 || rows <= 0 || !screen.IsEffectivelyVisible)
            return (new Point(0, 0), 8, 16, Math.Max(1, (int)(ActualWidth / 8)), Math.Max(2, (int)(ActualHeight / 16)));
        // The picture, not the whole control: the main screen letterboxes its bitmap to a forced aspect
        var pic = PictureRect?.Invoke(screen) ?? new Rect(0, 0, screen.Bounds.Width, screen.Bounds.Height);
        var origin = screen.TranslatePoint(pic.TopLeft, this) ?? pic.TopLeft;
        return (origin, pic.Width / cols, pic.Height / rows, cols, rows);
    }

    /// <summary>One main-screen cell's size in layer pixels.</summary>
    public (double CellW, double CellH) CellSize()
    {
        var m = Metrics();
        return (m.CellW, m.CellH);
    }

    /// <summary>Puts a frame at its cell position.</summary>
    public void Place(RainwireWindowFrame frame)
    {
        var m = Metrics();
        SetLeft(frame, m.Origin.X + frame.CellX * m.CellW);
        SetTop(frame, m.Origin.Y + frame.CellY * m.CellH);
    }

    /// <summary>The desk or the main screen changed size: everything again, on the grid.</summary>
    private void Relayout()
    {
        var (_, cw, ch, _, _) = Metrics();
        foreach (var f in _frames.Values)
        {
            f.Fit(cw, ch);
            Place(f);
        }
        RefreshTaskbar();
        if (_menuSel >= 0)
            DrawMenu();
    }

    // ---- windows ----

    private void OnOpened(WmWindow win)
    {
        var surface = new RainwireWindowSurface(win, _session, this, () => FontFor(win.Font));
        var frame = new RainwireWindowFrame(surface, this);
        _frames[win.Id] = frame;
        _opened.Add(win.Id);
        Children.Add(frame);

        var (_, cw, ch, cols, rows) = Metrics();
        // The board suggests a size; fit it inside the desk if it's too big (the client decides), and say so.
        // The desk is every row but the taskbar's; a window is two columns and one row bigger than its content.
        int maxCols = Math.Max(WmTerminal.MinWidth, cols - 2), maxRows = Math.Max(WmTerminal.MinHeight, rows - 2);
        if (win.Terminal.Width > maxCols || win.Terminal.Height > maxRows)
            _session.Resized(win.Id, Math.Min(win.Terminal.Width, maxCols), Math.Min(win.Terminal.Height, maxRows));
        frame.Fit(cw, ch);
        frame.CellX = Math.Clamp(win.X, 0, Math.Max(0, cols - frame.OuterW));
        frame.CellY = Math.Clamp(win.Y, 0, Math.Max(0, rows - 1 - frame.OuterH));
        Place(frame);

        string state = win.State;
        win.State = "normal";   // SetState reports only changes: start from what the frame shows
        if (state == "max")
            ToggleMaximize(frame);
        else if (state == "min")
            Minimize(frame);
        RefreshTaskbar();
        if (state != "min")
            Activate(win.Id);
    }

    private void OnClosed(int id)
    {
        if (!_frames.Remove(id, out var frame))
            return;
        Children.Remove(frame);
        _opened.Remove(id);
        RefreshTaskbar();
        if (_focusedId == id)
        {
            _focusedId = 0;
            FocusTopmostOrMain();
        }
    }

    private void FocusTopmostOrMain()
    {
        var top = _frames.Values.Where(f => f.IsVisible).OrderByDescending(f => f.ZIndex).FirstOrDefault();
        if (top != null)
            Activate(top.Id);
        else
            _focusMain();
    }

    /// <summary>Brings a window to the front, gives it the keyboard, and tells the board.</summary>
    public void Activate(int id)
    {
        if (!_frames.TryGetValue(id, out var frame))
            return;
        if (!frame.IsVisible)
        {
            frame.IsVisible = true;
            _session.SetState(id, frame.Restore != null ? "max" : "normal");
        }
        frame.ZIndex = ++_z;
        foreach (var f in _frames.Values)
            f.SetFocused(f == frame);
        _focusedId = id;
        if (!frame.Surface.IsFocused)
            frame.Surface.Focus();
        _session.Focused(id);
        RefreshTaskbar();
    }

    /// <summary>Ctrl+Tab: the next (or previous) window, by taskbar order.</summary>
    public void CycleFocus(bool forward)
    {
        var order = _frames.Values.OrderBy(f => _opened.IndexOf(f.Id)).ToList();
        if (order.Count == 0)
            return;
        int at = order.FindIndex(f => f.Id == _focusedId);
        int next = ((at < 0 ? 0 : at) + (forward ? 1 : -1) + order.Count) % order.Count;
        Activate(order[next].Id);
    }

    public void Minimize(RainwireWindowFrame frame)
    {
        frame.IsVisible = false;
        frame.SetFocused(false);
        _session.SetState(frame.Id, "min");
        if (_focusedId == frame.Id)
        {
            _focusedId = 0;
            FocusTopmostOrMain();
        }
        RefreshTaskbar();
    }

    public void ToggleMaximize(RainwireWindowFrame frame)
    {
        var (_, cw, ch, cols, rows) = Metrics();
        var t = frame.Surface.Window.Terminal;
        if (frame.Restore is { } r)
        {
            frame.Restore = null;
            _session.Resized(frame.Id, r.Cols, r.Rows);
            frame.CellX = r.X;
            frame.CellY = r.Y;
            frame.SetMaximized(false);
            _session.SetState(frame.Id, "normal");
        }
        else
        {
            frame.Restore = (frame.CellX, frame.CellY, t.Width, t.Height);
            // Every cell above the taskbar, as the server-drawn desktop maximizes
            _session.Resized(frame.Id, cols - 2, rows - 2);
            frame.CellX = 0;
            frame.CellY = 0;
            frame.SetMaximized(true);
            _session.SetState(frame.Id, "max");
        }
        frame.Fit(cw, ch);
        Place(frame);
        frame.Surface.MarkDirty();
    }

    /// <summary>The grip was released: the window takes its new size, and the board hears once.</summary>
    public void Resize(RainwireWindowFrame frame, int cols, int rows)
    {
        var (cw, ch) = CellSize();
        if (frame.Restore != null)
        {
            frame.Restore = null;
            frame.SetMaximized(false);
            _session.SetState(frame.Id, "normal");
        }
        _session.Resized(frame.Id, cols, rows);
        frame.Fit(cw, ch);
        Place(frame);
        frame.Surface.MarkDirty();
    }

    /// <summary>A move ended: tell the board where, in main-screen cells, for the saved layout.</summary>
    public void Moved(RainwireWindowFrame frame) => _session.Moved(frame.Id, frame.CellX, frame.CellY);

    /// <summary>Moves a frame to a cell and tells the board, as a finished drag does.</summary>
    public void MoveTo(RainwireWindowFrame frame, int x, int y)
    {
        frame.CellX = x;
        frame.CellY = Math.Max(0, y);
        Place(frame);
        Moved(frame);
    }

    public void RequestClose(RainwireWindowFrame frame) => _session.RequestClose(frame.Id);

    // ---- suspend, resume, the end ----

    private void OnSuspendChanged()
    {
        // A full-screen program (a raw or TRACE door) runs on the main screen: the windows step aside until Resume
        CloseMenu();
        IsVisible = !_session.Suspended;
        if (_session.Suspended)
            _focusMain();
        else
            FocusTopmostOrMain();
    }

    /// <summary>
    /// A modal dialog (a file transfer) just closed. Resume usually came while it was still open, so its focus
    /// went nowhere, and closing the dialog gave the keyboard back to the main screen, which the board ignores
    /// while windows are open: keys did nothing until a click.
    /// </summary>
    public void RefocusAfterDialog()
    {
        if (_session.Active && !_session.Suspended)
            FocusTopmostOrMain();
    }

    private void EndDesk()
    {
        CloseMenu();
        foreach (var id in _frames.Keys.ToList())
            OnClosed(id);
        HideDragLabel();
        IsVisible = false;
        _focusMain();
    }

    // ---- the taskbar: the main screen's bottom row, as the server-drawn desktop draws it ----

    private void RefreshTaskbar()
    {
        var (origin, cw, ch, cols, rows) = Metrics();
        var th = Theme;
        _taskbar.Resize(cols, 1);
        _taskbar.Width = cols * cw;
        _taskbar.Height = ch;
        SetLeft(_taskbar, origin.X);
        SetTop(_taskbar, origin.Y + (rows - 1) * ch);
        _taskbar.Fill(0, 0, cols, 1, th.BarFg, th.BarBg);
        _barLook.Width = _taskbar.Width;
        _barLook.Height = _taskbar.Height;
        SetLeft(_barLook, origin.X);
        SetTop(_barLook, origin.Y + (rows - 1) * ch);
        _barLook.Cols = cols;
        _barLook.Tasks = new List<WmBarChrome.Task>();
        _barLook.InvalidateVisual();

        // The graphical look's start button is just the menu mark and an R (sysop 2026-10-05), so it takes less room
        var start = WmLook.Graphical ? Concat(" ", th.Start, " R ") : Concat(" ", th.Start, " ", th.StartText, " ");
        bool open = _menuSel >= 0;
        _taskbar.Text(0, 0, start, open ? th.SelFg : th.Hot, open ? th.SelBg : th.BarBg);
        _startHit = (0, start.Length);
        _barLook.Start = _startHit;
        _barLook.MenuOpen = open;

        _taskHits.Clear();
        int x = start.Length + 1;
        var list = _opened.Where(_frames.ContainsKey).ToList();
        int room = cols - 2 - x;
        if (list.Count == 0 || room < 6)
            return;
        int each = Math.Min(22, room / list.Count);
        for (int i = 0; i < list.Count && each >= 5; i++)
        {
            var frame = _frames[list[i]];
            var label = WmTheme.Cp($" {(char)('1' + Math.Min(i, 8))} {frame.Surface.Window.Title}");
            label = label.Length > each - 1 ? label[..(each - 1)] : label;
            int fg = th.BarFg, bg = th.BarBg;
            bool focused = list[i] == _focusedId && frame.IsVisible;
            if (focused)
                (fg, bg) = (th.TaskFg, th.TaskBg);
            else if (!frame.IsVisible)
                fg = th.DimFg;
            _taskbar.Fill(x, 0, each - 1, 1, fg, bg);
            _taskbar.Text(x, 0, label, fg, bg);
            _taskHits.Add((x, x + each - 1, list[i]));
            _barLook.Tasks.Add(new WmBarChrome.Task(x, x + each - 1,
                $"{(char)('1' + Math.Min(i, 8))}  {frame.Surface.Window.Title}", focused, !frame.IsVisible));
            x += each;
        }
    }

    private void TaskbarUp(object? sender, PointerReleasedEventArgs e)
    {
        if (!_startArmed || e.InitialPressMouseButton != MouseButton.Left)
            return;
        _startArmed = false;
        e.Handled = true;
        var p = e.GetPosition(_taskbar);
        var (x, _) = _taskbar.CellAt(p);
        bool over = p.Y >= 0 && p.Y < _taskbar.Bounds.Height && x >= _startHit.From && x < _startHit.To;
        if (over && _menuSel < 0)
            OpenMenu();   // let go anywhere else: nothing, as a button does
    }

    private void TaskbarDown(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(_taskbar).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
            return;
        var (x, _) = _taskbar.CellAt(e.GetPosition(_taskbar));
        e.Handled = true;
        if (x >= _startHit.From && x < _startHit.To)
        {
            // Opens on the release, as the Windows client does (sysop 2026-10-12); a press while it's open shuts it.
            // Avalonia gives the release to the control that had the press.
            if (_menuSel >= 0)
                CloseMenu();
            else
                _startArmed = true;
            return;
        }
        foreach (var (from, to, id) in _taskHits)
        {
            if (x < from || x >= to || !_frames.TryGetValue(id, out var frame))
                continue;
            // The focused window's button minimizes it; any other brings that window up
            if (id == _focusedId && frame.IsVisible)
                Minimize(frame);
            else
                Activate(id);
            return;
        }
    }

    private static byte[] Concat(params object[] parts)
    {
        var b = new List<byte>();
        foreach (var p in parts)
            b.AddRange(p is byte[] bytes ? bytes : WmTheme.Cp((string)p));
        return b.ToArray();
    }

    // ---- the start menu: a box above the start button with the board's Items ----

    /// <summary>Opens the start menu, as the start button does.</summary>
    public void OpenMenu()
    {
        if (_session.Items.Count == 0)
            return;
        _menuSel = 0;
        _menu.IsVisible = true;
        _menuLook.IsVisible = WmLook.Graphical;
        DrawMenu();
        RefreshTaskbar();
        // A press anywhere else shuts it (WPF captures the mouse to the menu's subtree; Avalonia has no subtree
        // capture, so a tunnelling handler on the window sees every press first while the menu is open)
        _menuTop = TopLevel.GetTopLevel(this);
        _menuTop?.AddHandler(PointerPressedEvent, MenuPress, RoutingStrategies.Tunnel);
        // Focus once the press that opened it is done (Avalonia moves focus on the press itself)
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_menuSel >= 0)
                _menu.Focus();
        });
    }

    private void CloseMenu()
    {
        if (_menuSel < 0)
            return;
        _menuSel = -1;
        _menu.IsVisible = false;
        _menuLook.IsVisible = false;
        _menuTop?.RemoveHandler(PointerPressedEvent, MenuPress);
        _menuTop = null;
        RefreshTaskbar();
        if (_frames.TryGetValue(_focusedId, out var f) && f.IsVisible)
            f.Surface.Focus();
    }

    private void DrawMenu()
    {
        var (origin, cw, ch, _, rows) = Metrics();
        var th = Theme;
        var items = _session.Items;
        int wid = th.MenuText.Length + 4;
        foreach (var it in items)
            wid = Math.Max(wid, it.Label.Length + 8);
        int h = items.Count + 2;
        _menu.Resize(wid, h);
        _menu.Width = wid * cw;
        _menu.Height = h * ch;
        SetLeft(_menu, origin.X);
        SetTop(_menu, origin.Y + Math.Max(0, rows - 1 - h) * ch);
        _menuLook.Width = _menu.Width;
        _menuLook.Height = _menu.Height;
        SetLeft(_menuLook, origin.X);
        SetTop(_menuLook, origin.Y + Math.Max(0, rows - 1 - h) * ch);
        _menuLook.Cols = wid;
        _menuLook.Rows = h;
        _menuLook.Selected = _menuSel;
        _menuLook.Items = items.Select(it => (it.Key, it.Label)).ToList();
        _menuLook.InvalidateVisual();

        _menu.Fill(0, 0, wid, h, th.PopFg, th.PopBg);
        for (int x = 1; x < wid - 1; x++)
        {
            _menu.Put(x, 0, th.H, th.FrameFocus, th.PopBg);
            _menu.Put(x, h - 1, th.H, th.FrameFocus, th.PopBg);
        }
        for (int y = 1; y < h - 1; y++)
        {
            _menu.Put(0, y, th.V, th.FrameFocus, th.PopBg);
            _menu.Put(wid - 1, y, th.V, th.FrameFocus, th.PopBg);
        }
        _menu.Put(0, 0, th.TL, th.FrameFocus, th.PopBg);
        _menu.Put(wid - 1, 0, th.TR, th.FrameFocus, th.PopBg);
        _menu.Put(0, h - 1, th.BL, th.FrameFocus, th.PopBg);
        _menu.Put(wid - 1, h - 1, th.BR, th.FrameFocus, th.PopBg);
        _menu.Text(2, 0, Concat(" ", th.MenuText, " "), th.Hot, th.PopBg);
        for (int i = 0; i < items.Count; i++)
        {
            int fg = th.PopFg, bg = th.PopBg;
            if (i == _menuSel)
                (fg, bg) = (th.SelFg, th.SelBg);
            _menu.Fill(1, 1 + i, wid - 2, 1, fg, bg);
            _menu.Text(1, 1 + i, $" {items[i].Key}  {items[i].Label}", fg, bg);
            _menu.Text(2, 1 + i, items[i].Key, th.Hot, bg);
        }
    }

    private int MenuItemAt(Point p)
    {
        if (p.X < 0 || p.Y < 0 || p.X >= _menu.Bounds.Width || p.Y >= _menu.Bounds.Height)
            return -1;
        var (x, y) = _menu.CellAt(p);
        int i = y - 1;
        return x > 0 && x < _menu.Cols - 1 && i >= 0 && i < _session.Items.Count ? i : -1;
    }

    private void MenuHover(Point p)
    {
        int i = MenuItemAt(p);
        if (i >= 0 && i != _menuSel)
        {
            _menuSel = i;
            DrawMenu();
        }
    }

    private void MenuPress(object? sender, PointerPressedEventArgs e)
    {
        // A press outside the box shuts the menu, and is used up
        var p = e.GetPosition(_menu);
        if (p.X < 0 || p.Y < 0 || p.X >= _menu.Bounds.Width || p.Y >= _menu.Bounds.Height)
        {
            CloseMenu();
            e.Handled = true;
        }
    }

    private void MenuClick(Point p)
    {
        int i = MenuItemAt(p);
        if (i >= 0)
            Pick(i);
    }

    private void MenuKey(object? sender, KeyEventArgs e)
    {
        var items = _session.Items;
        e.Handled = true;
        switch (e.Key)
        {
            case Key.Up:
                _menuSel = (_menuSel + items.Count - 1) % items.Count;
                DrawMenu();
                return;
            case Key.Down:
            case Key.Tab:
                _menuSel = (_menuSel + 1) % items.Count;
                DrawMenu();
                return;
            case Key.Enter:
            case Key.Space:
                Pick(_menuSel);
                return;
            case Key.Escape:
                CloseMenu();
                return;
        }
        // A hotkey picks its item
        string k = e.Key is >= Key.A and <= Key.Z ? ((char)('A' + (e.Key - Key.A))).ToString()
                 : e.Key is >= Key.D0 and <= Key.D9 ? ((char)('0' + (e.Key - Key.D0))).ToString() : "";
        int at = k.Length > 0 ? items.ToList().FindIndex(it => string.Equals(it.Key, k, StringComparison.OrdinalIgnoreCase)) : -1;
        if (at >= 0)
            Pick(at);
        else
            e.Handled = false;
    }

    private void Pick(int i)
    {
        var items = _session.Items;
        if (i < 0 || i >= items.Count)
            return;
        string id = items[i].Id;
        CloseMenu();
        _session.Launch(id);
    }

    // ---- drag and drop ----

    public void ShowDragLabel(string text, Point at)
    {
        var th = Theme;
        var (cw, ch) = CellSize();
        var label = WmTheme.Cp(" » " + text + " ");
        _dragLabel.Resize(label.Length, 1);
        _dragLabel.Width = label.Length * cw;
        _dragLabel.Height = ch;
        _dragLabel.Text(0, 0, label, th.SelFg, th.SelBg);
        _dragLabel.IsVisible = true;
        MoveDragLabel(at);
    }

    public void MoveDragLabel(Point at)
    {
        var (cw, ch) = CellSize();
        SetLeft(_dragLabel, at.X + cw);
        SetTop(_dragLabel, at.Y + ch);
    }

    public void HideDragLabel() => _dragLabel.IsVisible = false;

    /// <summary>Where a drop landed: the topmost window under the point and the cell in it, or the desktop (0).</summary>
    public (int To, int X, int Y) DropTarget(Point layerPoint)
    {
        foreach (var f in _frames.Values.Where(f => f.IsVisible).OrderByDescending(f => f.ZIndex))
        {
            if (this.TranslatePoint(layerPoint, f.Surface) is not { } p)
                continue;
            if (p.X >= 0 && p.Y >= 0 && p.X < f.Surface.Bounds.Width && p.Y < f.Surface.Bounds.Height)
            {
                var (x, y) = f.Surface.CellAt(p);
                return (f.Id, x, y);
            }
        }
        return (0, 0, 0);
    }
}
