namespace TERMinator.Linux.RainwireWm;

/// <summary>A start menu entry the board offered (Item).</summary>
public sealed record WmItem(string Id, string Key, string Label);

/// <summary>One of the board's windows, as this terminal draws it.</summary>
public sealed class WmWindow
{
    public int Id { get; init; }
    public string Title { get; set; } = "";
    /// <summary>Where the board suggested it go, in main-screen cells.</summary>
    public int X { get; init; }
    public int Y { get; init; }
    /// <summary>normal, min or max.</summary>
    public string State { get; set; } = "normal";
    public WmTerminal Terminal { get; init; } = null!;
    /// <summary>The user pressed close; the window goes when the board says Close.</summary>
    public bool CloseRequested { get; set; }
    /// <summary>A font the board sent to draw this window's text with; "" for the theme's.</summary>
    public string Font { get; init; } = "";
}

/// <summary>
/// Rainwire's window protocol v1 (Rainwire docs/WINDOWS.md), the terminal's side: what the board has said, and what
/// to tell it. The board runs every window's app and sends each window's screen; this terminal draws the frames and
/// does moving, resizing, stacking and minimizing itself, telling the board only the results.
///
/// TERMinator answers the Query with channels=0 (cryptlib's SSH client can't open "rainwire-window" channels), so
/// every window's screen comes in Data messages and its input goes back in Input messages on the one connection.
///
/// The board is trusted no more than it has to be: sizes are clamped, unknown windows ignored, and Launch only ever
/// sends an item the board offered. No Avalonia in here; the desk layer (RainwireDeskLayer) draws what this holds.
/// </summary>
public sealed class WmSession
{
    public const int MaxWindows = 32;

    private readonly Action<byte[]> _send;
    private readonly Dictionary<int, WmWindow> _windows = new();
    private readonly List<WmItem> _items = new();
    private readonly Dictionary<string, (WmFont Font, int Got)> _fonts = new();
    private int _focused;

    /// <summary>The most fonts one board may send, and the biggest (256 glyphs of 32 rows).</summary>
    public const int MaxFonts = 8;

    public WmSession(Action<byte[]> send) => _send = send;

    /// <summary>Between Hello and Bye: the board's windows are ours to draw.</summary>
    public bool Active { get; private set; }
    /// <summary>channels or framed, from Hello.</summary>
    public string Mode { get; private set; } = "";
    /// <summary>A full-screen program (a raw door) has the main screen until Resume.</summary>
    public bool Suspended { get; private set; }
    public IReadOnlyList<WmItem> Items => _items;
    /// <summary>How the board wants the windows drawn (Theme); the classic look until it says.</summary>
    public WmTheme Theme { get; private set; } = new();
    public IReadOnlyDictionary<int, WmWindow> Windows => _windows;

    public event Action? Started;
    public event Action? ItemsChanged;
    /// <summary>A new Theme, or a font it names finished arriving: redraw everything.</summary>
    public event Action? ThemeChanged;
    public event Action<WmWindow>? Opened;
    public event Action<WmWindow>? TitleChanged;
    public event Action<WmWindow>? Output;
    public event Action<int>? Closed;
    public event Action? SuspendChanged;
    public event Action? Ended;
    /// <summary>The answer to Drag: window, ok, label.</summary>
    public event Action<int, bool, string>? DragInfo;

    // ---- file drops (files=1; WINDOWS.md "File drops", FILES_GFX.md step 3d) ----
    // These come whether or not the desk is up: the Files TRACE module uses them on a plain (not windowed) session.

    /// <summary>FileGo: send exactly these offered files (their n, in order), with no file dialog.</summary>
    public event Action<IReadOnlyList<int>>? FileGo;
    /// <summary>FileNo: n (0 if it answers a DropOut), from (-1 if it answers an offer), reason.</summary>
    public event Action<int, int, string>? FileNo;
    /// <summary>FileGet: the answer to DropOut. from, name, size (-1 if missing).</summary>
    public event Action<int, string, long>? FileGet;
    /// <summary>DragFile (module drags): module, ok, name, size (-1 if missing), reason.</summary>
    public event Action<string, bool, string, long, string>? DragFile;
    /// <summary>DragFile for a scene's row (scene=, after DragFile): the row, ok, the label and hint to show.</summary>
    public event Action<int, bool, string, string>? DragFileScene;

    /// <summary>Handles one message from the board, as the native bridge queued it (without the prefix).</summary>
    public void Handle(ReadOnlySpan<byte> body)
    {
        var m = WmMessage.Parse(body);
        if (m != null)
            Handle(m);
    }

    public void Handle(WmMessage m)
    {
        if (m.Verb == "Hello")
        {
            if (!m.TryInt("v", out int v) || v < 1)
                return;
            EndDesk();   // a second Hello starts over
            Active = true;
            Mode = m.Get("mode");
            Started?.Invoke();
            return;
        }
        if (HandleFileVerb(m))
            return;
        if (!Active)
            return;

        m.TryInt("id", out int id);
        _windows.TryGetValue(id, out var win);
        switch (m.Verb)
        {
            case "Item":
                string itemId = m.Get("id");
                if (itemId.Length > 0 && itemId.Length <= 64 && _items.Count < 64 && _items.All(i => i.Id != itemId))
                {
                    _items.Add(new WmItem(itemId, m.Get("key"), m.Get("label")));
                    ItemsChanged?.Invoke();
                }
                break;
            case "Open":
                if (win != null || id <= 0 || _windows.Count >= MaxWindows
                    || !m.TryInt("w", out int w) || !m.TryInt("h", out int h))
                    return;
                m.TryInt("x", out int x);
                m.TryInt("y", out int y);
                string state = m.Get("state");
                win = new WmWindow
                {
                    Id = id,
                    Title = m.Get("title"),
                    X = x,
                    Y = y,
                    State = state is "min" or "max" ? state : "normal",
                    Terminal = new WmTerminal(w, h),
                    Font = m.Get("font").Length <= 64 ? m.Get("font") : "",
                };
                int wid = id;
                win.Terminal.Reply = bytes => Input(wid, bytes);
                _windows[id] = win;
                _focused = id;
                Opened?.Invoke(win);
                break;
            case "Theme":
                Theme = WmTheme.From(m);
                ThemeChanged?.Invoke();
                break;
            case "Font":
                AddFont(m);
                break;
            case "Title":
                if (win != null)
                {
                    win.Title = m.Get("title");
                    TitleChanged?.Invoke(win);
                }
                break;
            case "Data":
                if (win != null && m.Data() is { } data)
                {
                    win.Terminal.Feed(data);
                    Output?.Invoke(win);
                }
                break;
            case "Close":
                if (win != null)
                {
                    _windows.Remove(id);
                    if (_focused == id)
                        _focused = 0;
                    Closed?.Invoke(id);
                }
                break;
            case "DragInfo":
                if (win != null)
                    DragInfo?.Invoke(id, m.Get("ok") == "1", m.Get("label"));
                break;
            case "Suspend":
                if (!Suspended)
                {
                    Suspended = true;
                    SuspendChanged?.Invoke();
                }
                break;
            case "Resume":
                if (Suspended)
                {
                    Suspended = false;
                    SuspendChanged?.Invoke();
                }
                break;
            case "Bye":
                EndDesk();
                break;
        }
    }

    /// <summary>The connection went: forget everything, quietly.</summary>
    public void Reset() => EndDesk();

    /// <summary>The file-drop verbs, taken in and out of windowed mode. True when m was one.</summary>
    private bool HandleFileVerb(WmMessage m)
    {
        switch (m.Verb)
        {
            case "FileGo":
                var list = new List<int>();
                foreach (var part in m.Get("n").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (int.TryParse(part, out int n) && n > 0 && n <= 100000 && !list.Contains(n))
                        list.Add(n);
                if (list.Count > 0)
                    FileGo?.Invoke(list);
                return true;
            case "FileNo":
                int no = m.TryInt("n", out int nn) ? nn : 0;
                int from = m.TryInt("from", out int ff) ? ff : -1;
                FileNo?.Invoke(no, from, m.Get("reason"));
                return true;
            case "FileGet":
                m.TryInt("from", out int gf);
                FileGet?.Invoke(gf, m.Get("name"), ParseSize(m.Get("size")));
                return true;
            case "DragFile":
                DragFile?.Invoke(m.Get("module"), m.Get("ok") == "1", m.Get("name"), ParseSize(m.Get("size")), m.Get("reason"));
                if (int.TryParse(m.Get("scene"), out int dragRow) && dragRow > 0)
                    DragFileScene?.Invoke(dragRow, m.Get("ok") == "1", m.Get("label"), m.Get("hint"));
                return true;
        }
        return false;
    }

    /// <summary>A byte count (0 to 2^53-1), or -1.</summary>
    private static long ParseSize(string s) =>
        s.Length is > 0 and <= 16 && s.All(char.IsAsciiDigit) && long.TryParse(s, out long v) && v <= (1L << 53) - 1 ? v : -1;

    private void EndDesk()
    {
        bool was = Active;
        foreach (int id in _windows.Keys.ToList())
        {
            _windows.Remove(id);
            Closed?.Invoke(id);
        }
        _items.Clear();
        _fonts.Clear();
        Theme = new WmTheme();
        Active = false;
        Suspended = false;
        Mode = "";
        _focused = 0;
        if (was)
            Ended?.Invoke();
    }

    /// <summary>A font the board sent, once all of it has arrived; null for the terminal's own.</summary>
    public WmFont? FontNamed(string name) =>
        name.Length > 0 && _fonts.TryGetValue(name, out var f) && f.Got == f.Font.Data.Length ? f.Font : null;

    /// <summary>One piece of a font: name, h (rows per glyph), at (byte offset) and b64, in order.</summary>
    private void AddFont(WmMessage m)
    {
        string name = m.Get("name");
        if (name.Length is 0 or > 64 || !m.TryInt("h", out int h) || h < 6 || h > 32
            || !m.TryInt("at", out int at) || m.Data() is not { } data)
            return;
        if (!_fonts.TryGetValue(name, out var f) || f.Font.Height != h || at == 0)
        {
            if (at != 0 || (!_fonts.ContainsKey(name) && _fonts.Count >= MaxFonts))
                return;
            f = (new WmFont { Data = new byte[256 * h], Height = h }, 0);
        }
        if (at != f.Got || at + data.Length > f.Font.Data.Length)
            return;   // out of order or too long: ignore it, the font stays incomplete
        data.CopyTo(f.Font.Data, at);
        f.Got += data.Length;
        _fonts[name] = f;
        if (f.Got == f.Font.Data.Length)
            ThemeChanged?.Invoke();
    }

    // ---- to the board ----

    private void Send(WmMessage m)
    {
        if (Active)
            _send(m.Encode());
    }

    /// <summary>The user resized a window: resize its terminal and tell the board, once, when the drag ends.</summary>
    public void Resized(int id, int w, int h)
    {
        if (!_windows.TryGetValue(id, out var win))
            return;
        w = Math.Clamp(w, WmTerminal.MinWidth, WmTerminal.MaxWidth);
        h = Math.Clamp(h, WmTerminal.MinHeight, WmTerminal.MaxHeight);
        if (w == win.Terminal.Width && h == win.Terminal.Height)
            return;
        win.Terminal.Resize(w, h);
        Send(new WmMessage("Resized", "id", id.ToString(), "w", w.ToString(), "h", h.ToString()));
    }

    public void Moved(int id, int x, int y)
    {
        if (_windows.ContainsKey(id))
            Send(new WmMessage("Moved", "id", id.ToString(), "x", Math.Clamp(x, -99999, 99999).ToString(),
                "y", Math.Clamp(y, -99999, 99999).ToString()));
    }

    public void SetState(int id, string state)
    {
        if (state is not ("normal" or "min" or "max") || !_windows.TryGetValue(id, out var win) || win.State == state)
            return;
        win.State = state;
        Send(new WmMessage("State", "id", id.ToString(), "state", state));
    }

    public void Focused(int id)
    {
        if (id == _focused || !_windows.ContainsKey(id))
            return;
        _focused = id;
        Send(new WmMessage("Focused", "id", id.ToString()));
    }

    /// <summary>The close button: ask, and wait for the board's Close.</summary>
    public void RequestClose(int id)
    {
        if (!_windows.TryGetValue(id, out var win) || win.CloseRequested)
            return;
        win.CloseRequested = true;
        Send(new WmMessage("Close", "id", id.ToString()));
    }

    /// <summary>A start menu choice. Only an item the board offered is ever sent.</summary>
    public bool Launch(string itemId)
    {
        if (!_items.Any(i => i.Id == itemId))
            return false;
        Send(new WmMessage("Launch", "item", itemId));
        return true;
    }

    /// <summary>Keys and mouse reports for a window, in Input messages of at most 2048 bytes each.</summary>
    public void Input(int id, ReadOnlySpan<byte> bytes)
    {
        if (!_windows.ContainsKey(id) || Suspended)
            return;
        foreach (var m in WmMessage.DataMessages("Input", id, bytes))
            Send(m);
    }

    /// <summary>A drag began at (x, y), 0-based cells in the window's content. The board answers DragInfo.</summary>
    public void Drag(int id, int x, int y)
    {
        if (_windows.ContainsKey(id))
            Send(new WmMessage("Drag", "id", id.ToString(), "x", x.ToString(), "y", y.ToString()));
    }

    /// <summary>
    /// One file of a batch dropped from the OS: on window id (0 the desktop) at (x, y) in its content cells, or, with
    /// module set, on that TRACE module at (x, y) in its frame pixels. Sent in and out of windowed mode.
    /// </summary>
    public void FileOffer(int n, int count, int id, string? module, int x, int y, string name, long size, int scene = 0)
    {
        var kv = new List<string> { "n", n.ToString(), "count", count.ToString() };
        if (module != null)
            kv.AddRange(new[] { "module", module });
        if (scene > 0)   // dropped on a Rainwire scene's drop target (P_DROP): the Files module's drop, naming the block
            kv.AddRange(new[] { "scene", scene.ToString() });
        else
            kv.AddRange(new[] { "id", id.ToString() });
        kv.AddRange(new[] { "x", Math.Clamp(x, -99999, 99999).ToString(), "y", Math.Clamp(y, -99999, 99999).ToString(),
            "name", name, "size", size.ToString() });
        _send(new WmMessage("FileOffer", kv.ToArray()).Encode());
    }

    /// <summary>A drag from window from (at x, y, as Drag said) let go on the OS desktop.</summary>
    public void DropOut(int from, int x, int y) =>
        _send(new WmMessage("DropOut", "from", from.ToString(), "x", x.ToString(), "y", y.ToString()).Encode());

    /// <summary>A drag out of a TRACE module (armed by DragFile) let go on the OS desktop.</summary>
    public void DropOutModule(string module, int scene = 0) =>
        _send((scene > 0 ? new WmMessage("DropOut", "module", module, "scene", scene.ToString()) : new WmMessage("DropOut", "module", module)).Encode());

    /// <summary>Dropped: the drag began at (x, y) in from and ended at (tx, ty) in to (0 for the desktop).</summary>
    public void Drop(int from, int x, int y, int to, int tx, int ty)
    {
        if (!_windows.ContainsKey(from))
            return;
        if (to != 0 && !_windows.ContainsKey(to))
            to = 0;
        Send(new WmMessage("Drop", "from", from.ToString(), "x", x.ToString(), "y", y.ToString(),
            "to", to.ToString(), "tx", tx.ToString(), "ty", ty.ToString()));
    }
}
