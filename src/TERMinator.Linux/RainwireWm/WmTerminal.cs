namespace TERMinator.Linux.RainwireWm;

/// <summary>
/// The terminal behind one Rainwire window: a width x height grid of cells, fed the window's output. A window's
/// screen is what a Rainwire session sends any terminal (ANSI with 24-bit colour, cursor positioning, sixel), in the
/// character set the board detected: CP437 here, so each cell keeps its CP437 code and the window draws it with the
/// same bitmap font as the main screen. Deliberately small: the sequences Rainwire's renderers and its door virtual
/// terminal produce, plus the usual cursor and erase commands, not a full VT500. No Avalonia, so it's testable on its own.
/// </summary>
public sealed class WmTerminal
{
    public struct Cell
    {
        public byte Ch;     // CP437 code
        public int Fg;      // ARGB
        public int Bg;      // ARGB
        public bool Underline;
    }

    /// <summary>A sixel picture, placed with its top-left corner in cell (Col, Row). Clear pixels are 0.</summary>
    public sealed class Picture
    {
        public int Col, Row, Width, Height;
        public int[] Pixels = Array.Empty<int>();
    }

    public const int MinWidth = 20, MaxWidth = 500, MinHeight = 5, MaxHeight = 300;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int CursorX { get; private set; }
    public int CursorY { get; private set; }
    public bool CursorVisible { get; private set; } = true;
    /// <summary>The app asked for mouse reports (1000, 1002 or 1003).</summary>
    public bool MouseReporting => _mouse1000 || _mouse1002 || _mouse1003;
    /// <summary>The app asked for button-motion (1002) or any-motion (1003) reports.</summary>
    public bool MouseMotion => _mouse1002 || _mouse1003;
    /// <summary>The app asked for SGR mouse coordinates (1006), as Rainwire always does.</summary>
    public bool MouseSgr { get; private set; }
    /// <summary>Goes up on every change, so a view knows to redraw.</summary>
    public int Version { get; private set; }
    public List<Picture> Pictures { get; } = new();
    /// <summary>Answers to status queries (cursor position, device attributes), to send back to the window.</summary>
    public Action<byte[]>? Reply { get; set; }

    public static readonly int DefaultFg = unchecked((int)0xFFAAAAAA);
    public static readonly int DefaultBg = unchecked((int)0xFF000000);

    private Cell[] _cells;
    private int _fg = DefaultFg, _bg = DefaultBg, _fgIndex = 7;
    private bool _bold, _reverse, _underline;
    private bool _pendingWrap;
    private int _top, _bottom;   // scroll region, inclusive
    private int _savedX, _savedY;
    private bool _mouse1000, _mouse1002, _mouse1003;

    // Parser
    private enum State { Ground, Esc, EscSkip, Csi, Osc, OscEsc, Dcs, DcsEsc, Ignore, IgnoreEsc }
    private State _state;
    private readonly System.Text.StringBuilder _params = new();
    private readonly List<byte> _dcs = new();
    private const int DcsMax = 4 * 1024 * 1024;

    // The 16 CGA colours, and ANSI colour order to CGA order
    private static readonly int[] Cga =
    {
        unchecked((int)0xFF000000), unchecked((int)0xFF0000AA), unchecked((int)0xFF00AA00), unchecked((int)0xFF00AAAA),
        unchecked((int)0xFFAA0000), unchecked((int)0xFFAA00AA), unchecked((int)0xFFAA5500), unchecked((int)0xFFAAAAAA),
        unchecked((int)0xFF555555), unchecked((int)0xFF5555FF), unchecked((int)0xFF55FF55), unchecked((int)0xFF55FFFF),
        unchecked((int)0xFFFF5555), unchecked((int)0xFFFF55FF), unchecked((int)0xFFFFFF55), unchecked((int)0xFFFFFFFF),
    };
    private static readonly int[] AnsiToCga = { 0, 4, 2, 6, 1, 5, 3, 7 };

    public WmTerminal(int width, int height)
    {
        Width = Math.Clamp(width, MinWidth, MaxWidth);
        Height = Math.Clamp(height, MinHeight, MaxHeight);
        _cells = new Cell[Width * Height];
        Blank(0, _cells.Length);
        _bottom = Height - 1;
    }

    public Cell this[int x, int y] => _cells[y * Width + x];

    /// <summary>A row as text, trailing blanks trimmed (for tests and drag labels).</summary>
    public string RowText(int y)
    {
        var sb = new System.Text.StringBuilder(Width);
        for (int x = 0; x < Width; x++)
            sb.Append(Cp437.ToChar(_cells[y * Width + x].Ch));
        return sb.ToString().TrimEnd(' ', '\u0000');
    }

    /// <summary>The whole screen as text, one line per row.</summary>
    public string Text()
    {
        var rows = new string[Height];
        for (int y = 0; y < Height; y++)
            rows[y] = RowText(y);
        return string.Join("\n", rows);
    }

    /// <summary>New size in cells. The contents stay where they are, cut or padded; pictures are dropped.</summary>
    public void Resize(int width, int height)
    {
        width = Math.Clamp(width, MinWidth, MaxWidth);
        height = Math.Clamp(height, MinHeight, MaxHeight);
        if (width == Width && height == Height)
            return;
        var cells = new Cell[width * height];
        for (int i = 0; i < cells.Length; i++)
            cells[i] = new Cell { Ch = (byte)' ', Fg = DefaultFg, Bg = DefaultBg };
        for (int y = 0; y < Math.Min(height, Height); y++)
            for (int x = 0; x < Math.Min(width, Width); x++)
                cells[y * width + x] = _cells[y * Width + x];
        _cells = cells;
        Width = width;
        Height = height;
        _top = 0;
        _bottom = height - 1;
        CursorX = Math.Min(CursorX, width - 1);
        CursorY = Math.Min(CursorY, height - 1);
        _pendingWrap = false;
        Pictures.Clear();
        Version++;
    }

    public void Feed(ReadOnlySpan<byte> data)
    {
        foreach (byte c in data)
            Step(c);
        Version++;
    }

    private void Step(byte c)
    {
        switch (_state)
        {
            case State.Ground:
                Ground(c);
                return;
            case State.Esc:
                Escape(c);
                return;
            case State.EscSkip:   // the one byte after ESC ( and the like
                _state = State.Ground;
                return;
            case State.Csi:
                if (c >= 0x40 && c <= 0x7E)
                {
                    _state = State.Ground;
                    Csi(_params.ToString(), (char)c);
                }
                else if (c == 0x1B)
                    _state = State.Esc;
                else if (c == 0x18 || c == 0x1A)
                    _state = State.Ground;   // CAN, SUB cancel it
                else if (_params.Length < 64)
                    _params.Append((char)c);
                return;
            case State.Osc:
                if (c == 0x07)
                    _state = State.Ground;
                else if (c == 0x1B)
                    _state = State.OscEsc;
                return;
            case State.OscEsc:
                _state = c == '\\' ? State.Ground : State.Osc;
                return;
            case State.Dcs:
                if (c == 0x1B)
                    _state = State.DcsEsc;
                else if (_dcs.Count < DcsMax)
                    _dcs.Add(c);
                return;
            case State.DcsEsc:
                if (c == '\\')
                {
                    _state = State.Ground;
                    Dcs();
                }
                else
                {
                    _state = State.Dcs;
                    if (_dcs.Count < DcsMax)
                    {
                        _dcs.Add(0x1B);
                        _dcs.Add(c);
                    }
                }
                return;
            case State.Ignore:   // APC, PM, SOS: read to ST and drop
                if (c == 0x1B)
                    _state = State.IgnoreEsc;
                return;
            case State.IgnoreEsc:
                _state = c == '\\' ? State.Ground : State.Ignore;
                return;
        }
    }

    private void Ground(byte c)
    {
        switch (c)
        {
            case 0x00:
            case 0x07:   // bell: the main screen rings for the session
                return;
            case 0x08:
                _pendingWrap = false;
                if (CursorX > 0)
                    CursorX--;
                return;
            case 0x09:
                _pendingWrap = false;
                CursorX = Math.Min((CursorX / 8 + 1) * 8, Width - 1);
                return;
            case 0x0A:
            case 0x0B:
            case 0x0C:
                _pendingWrap = false;
                LineFeed();
                return;
            case 0x0D:
                _pendingWrap = false;
                CursorX = 0;
                return;
            case 0x1B:
                _state = State.Esc;
                return;
        }
        // Everything else is a glyph, the other C0 codes included: Rainwire sends 0x10 and 0x11 as the CP437
        // triangles (▸ ◂), the way the main screen shows them.
        Put(c);
    }

    private void Put(byte ch)
    {
        if (_pendingWrap)
        {
            _pendingWrap = false;
            CursorX = 0;
            LineFeed();
        }
        int fg = _fg, bg = _bg;
        if (_bold && _fgIndex >= 0 && _fgIndex < 8)
            fg = Cga[_fgIndex + 8];
        if (_reverse)
            (fg, bg) = (bg, fg);
        _cells[CursorY * Width + CursorX] = new Cell { Ch = ch, Fg = fg, Bg = bg, Underline = _underline };
        if (CursorX == Width - 1)
            _pendingWrap = true;
        else
            CursorX++;
    }

    private void LineFeed()
    {
        if (CursorY == _bottom)
            ScrollUp(1);
        else if (CursorY < Height - 1)
            CursorY++;
    }

    private void Escape(byte c)
    {
        _state = State.Ground;
        switch ((char)c)
        {
            case '[':
                _params.Clear();
                _state = State.Csi;
                break;
            case ']':
                _state = State.Osc;
                break;
            case 'P':
                _dcs.Clear();
                _state = State.Dcs;
                break;
            case '_':
            case '^':
            case 'X':
                _state = State.Ignore;
                break;
            case '(':
            case ')':
            case '*':
            case '+':
            case '#':
            case '%':
                _state = State.EscSkip;
                break;
            case '7':
                _savedX = CursorX;
                _savedY = CursorY;
                break;
            case '8':
                CursorX = _savedX;
                CursorY = _savedY;
                _pendingWrap = false;
                break;
            case 'D':
                LineFeed();
                break;
            case 'E':
                CursorX = 0;
                LineFeed();
                break;
            case 'M':
                if (CursorY == _top)
                    ScrollDown(1);
                else if (CursorY > 0)
                    CursorY--;
                break;
            case 'c':
                Reset();
                break;
        }
    }

    private void Reset()
    {
        _fg = DefaultFg;
        _bg = DefaultBg;
        _fgIndex = 7;
        _bold = _reverse = _underline = false;
        _top = 0;
        _bottom = Height - 1;
        CursorX = CursorY = 0;
        CursorVisible = true;
        _pendingWrap = false;
        Blank(0, _cells.Length);
        Pictures.Clear();
    }

    private static int[] Numbers(string p, out char marker)
    {
        marker = '\0';
        if (p.Length > 0 && (p[0] == '?' || p[0] == '<' || p[0] == '=' || p[0] == '>'))
        {
            marker = p[0];
            p = p[1..];
        }
        if (p.Length == 0)
            return Array.Empty<int>();
        var parts = p.Split(';');
        var n = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            int v = 0;
            foreach (char ch in parts[i])
            {
                if (ch < '0' || ch > '9')
                    break;
                v = Math.Min(v * 10 + (ch - '0'), 99999);
            }
            n[i] = v;
        }
        return n;
    }

    private static int Arg(int[] n, int i, int def) => i < n.Length && n[i] > 0 ? n[i] : def;

    private void Csi(string p, char final)
    {
        var n = Numbers(p, out char marker);
        if (marker == '?')
        {
            if (final == 'h' || final == 'l')
                PrivateMode(n, final == 'h');
            return;
        }
        if (marker != '\0')
            return;   // ESC[=...c, ESC[>c and the like: nothing for a window
        _pendingWrap = false;
        switch (final)
        {
            case 'H':
            case 'f':
                CursorY = Math.Clamp(Arg(n, 0, 1) - 1, 0, Height - 1);
                CursorX = Math.Clamp(Arg(n, 1, 1) - 1, 0, Width - 1);
                break;
            case 'A':
                CursorY = Math.Max(CursorY - Arg(n, 0, 1), 0);
                break;
            case 'B':
                CursorY = Math.Min(CursorY + Arg(n, 0, 1), Height - 1);
                break;
            case 'C':
                CursorX = Math.Min(CursorX + Arg(n, 0, 1), Width - 1);
                break;
            case 'D':
                CursorX = Math.Max(CursorX - Arg(n, 0, 1), 0);
                break;
            case 'E':
                CursorY = Math.Min(CursorY + Arg(n, 0, 1), Height - 1);
                CursorX = 0;
                break;
            case 'F':
                CursorY = Math.Max(CursorY - Arg(n, 0, 1), 0);
                CursorX = 0;
                break;
            case 'G':
            case '`':
                CursorX = Math.Clamp(Arg(n, 0, 1) - 1, 0, Width - 1);
                break;
            case 'd':
                CursorY = Math.Clamp(Arg(n, 0, 1) - 1, 0, Height - 1);
                break;
            case 'J':
                EraseDisplay(n.Length > 0 ? n[0] : 0);
                break;
            case 'K':
                EraseLine(n.Length > 0 ? n[0] : 0);
                break;
            case 'X':
                Blank(CursorY * Width + CursorX, Math.Min(Arg(n, 0, 1), Width - CursorX));
                break;
            case '@':
                InsertChars(Arg(n, 0, 1));
                break;
            case 'P':
                DeleteChars(Arg(n, 0, 1));
                break;
            case 'L':
                if (CursorY >= _top && CursorY <= _bottom)
                    ScrollRegion(CursorY, _bottom, -Arg(n, 0, 1));
                break;
            case 'M':
                if (CursorY >= _top && CursorY <= _bottom)
                    ScrollRegion(CursorY, _bottom, Arg(n, 0, 1));
                break;
            case 'S':
                ScrollUp(Arg(n, 0, 1));
                break;
            case 'T':
                ScrollDown(Arg(n, 0, 1));
                break;
            case 'r':
            {
                int top = Arg(n, 0, 1) - 1, bottom = Arg(n, 1, Height) - 1;
                if (top < bottom && bottom < Height)
                {
                    _top = top;
                    _bottom = bottom;
                }
                else
                {
                    _top = 0;
                    _bottom = Height - 1;
                }
                CursorX = CursorY = 0;
                break;
            }
            case 's':
                _savedX = CursorX;
                _savedY = CursorY;
                break;
            case 'u':
                CursorX = _savedX;
                CursorY = _savedY;
                break;
            case 'm':
                Sgr(n);
                break;
            case 'n':
                if (Arg(n, 0, 0) == 6)
                    Reply?.Invoke(System.Text.Encoding.ASCII.GetBytes($"\x1b[{CursorY + 1};{CursorX + 1}R"));
                else if (Arg(n, 0, 0) == 5)
                    Reply?.Invoke("\x1b[0n"u8.ToArray());
                break;
            case 'c':
                Reply?.Invoke("\x1b[?62;22c"u8.ToArray());
                break;
        }
    }

    private void PrivateMode(int[] n, bool on)
    {
        foreach (int mode in n)
        {
            switch (mode)
            {
                case 25:
                    CursorVisible = on;
                    break;
                case 9:
                case 1000:
                    _mouse1000 = on;
                    break;
                case 1002:
                    _mouse1002 = on;
                    break;
                case 1003:
                    _mouse1003 = on;
                    break;
                case 1006:
                    MouseSgr = on;
                    break;
                case 47:
                case 1047:
                case 1049:
                    // The alternate screen: a window has only one, so switching either way starts it blank
                    Blank(0, _cells.Length);
                    Pictures.Clear();
                    break;
            }
        }
    }

    private void Sgr(int[] n)
    {
        if (n.Length == 0)
            n = new[] { 0 };
        for (int i = 0; i < n.Length; i++)
        {
            int v = n[i];
            switch (v)
            {
                case 0:
                    _fg = DefaultFg;
                    _bg = DefaultBg;
                    _fgIndex = 7;
                    _bold = _reverse = _underline = false;
                    break;
                case 1:
                    _bold = true;
                    break;
                case 22:
                    _bold = false;
                    break;
                case 4:
                    _underline = true;
                    break;
                case 24:
                    _underline = false;
                    break;
                case 7:
                    _reverse = true;
                    break;
                case 27:
                    _reverse = false;
                    break;
                case >= 30 and <= 37:
                    _fgIndex = AnsiToCga[v - 30];
                    _fg = Cga[_fgIndex];
                    break;
                case >= 90 and <= 97:
                    _fgIndex = -1;
                    _fg = Cga[AnsiToCga[v - 90] + 8];
                    break;
                case 39:
                    _fgIndex = 7;
                    _fg = DefaultFg;
                    break;
                case >= 40 and <= 47:
                    _bg = Cga[AnsiToCga[v - 40]];
                    break;
                case >= 100 and <= 107:
                    _bg = Cga[AnsiToCga[v - 100] + 8];
                    break;
                case 49:
                    _bg = DefaultBg;
                    break;
                case 38:
                case 48:
                {
                    int? colour = null;
                    if (i + 1 < n.Length && n[i + 1] == 2 && i + 4 < n.Length)
                    {
                        colour = unchecked((int)0xFF000000) | (Math.Min(n[i + 2], 255) << 16) | (Math.Min(n[i + 3], 255) << 8) | Math.Min(n[i + 4], 255);
                        i += 4;
                    }
                    else if (i + 1 < n.Length && n[i + 1] == 5 && i + 2 < n.Length)
                    {
                        colour = Colour256(n[i + 2]);
                        i += 2;
                    }
                    else
                        i = n.Length;   // malformed: ignore the rest
                    if (colour is int c)
                    {
                        if (v == 38)
                        {
                            _fg = c;
                            _fgIndex = -1;
                        }
                        else
                            _bg = c;
                    }
                    break;
                }
            }
        }
    }

    private static int Colour256(int i)
    {
        if (i < 16)
            return Cga[i < 8 ? AnsiToCga[i] : AnsiToCga[i - 8] + 8];
        if (i < 232)
        {
            i -= 16;
            int[] level = { 0, 95, 135, 175, 215, 255 };
            return unchecked((int)0xFF000000) | (level[i / 36 % 6] << 16) | (level[i / 6 % 6] << 8) | level[i % 6];
        }
        int g = 8 + (Math.Min(i, 255) - 232) * 10;
        return unchecked((int)0xFF000000) | (g << 16) | (g << 8) | g;
    }

    private void Blank(int from, int count)
    {
        int bg = _reverse ? _fg : _bg;
        for (int i = from; i < from + count && i < _cells.Length; i++)
            _cells[i] = new Cell { Ch = (byte)' ', Fg = DefaultFg, Bg = bg };
    }

    private void EraseDisplay(int how)
    {
        int at = CursorY * Width + CursorX;
        switch (how)
        {
            case 0:
                Blank(at, _cells.Length - at);
                break;
            case 1:
                Blank(0, at + 1);
                break;
            default:
                Blank(0, _cells.Length);
                Pictures.Clear();
                break;
        }
    }

    private void EraseLine(int how)
    {
        int row = CursorY * Width;
        switch (how)
        {
            case 0:
                Blank(row + CursorX, Width - CursorX);
                break;
            case 1:
                Blank(row, CursorX + 1);
                break;
            default:
                Blank(row, Width);
                break;
        }
    }

    private void InsertChars(int count)
    {
        int row = CursorY * Width;
        count = Math.Min(count, Width - CursorX);
        for (int x = Width - 1; x >= CursorX + count; x--)
            _cells[row + x] = _cells[row + x - count];
        Blank(row + CursorX, count);
    }

    private void DeleteChars(int count)
    {
        int row = CursorY * Width;
        count = Math.Min(count, Width - CursorX);
        for (int x = CursorX; x < Width - count; x++)
            _cells[row + x] = _cells[row + x + count];
        Blank(row + Width - count, count);
    }

    private void ScrollUp(int count) => ScrollRegion(_top, _bottom, count);

    private void ScrollDown(int count) => ScrollRegion(_top, _bottom, -count);

    /// <summary>Moves rows top..bottom up by count (down when negative), blanking what opens up.</summary>
    private void ScrollRegion(int top, int bottom, int count)
    {
        int rows = bottom - top + 1;
        if (count == 0 || rows <= 0)
            return;
        int n = Math.Min(Math.Abs(count), rows);
        if (count > 0)
        {
            Array.Copy(_cells, (top + n) * Width, _cells, top * Width, (rows - n) * Width);
            Blank((bottom - n + 1) * Width, n * Width);
        }
        else
        {
            Array.Copy(_cells, top * Width, _cells, (top + n) * Width, (rows - n) * Width);
            Blank(top * Width, n * Width);
        }
        Pictures.Clear();
    }

    private void Dcs()
    {
        // ESC P <params> q <sixel data> ESC \ is a sixel picture; any other DCS is dropped
        int q = _dcs.IndexOf((byte)'q');
        if (q < 0)
            return;
        for (int i = 0; i < q; i++)
            if (!(_dcs[i] >= '0' && _dcs[i] <= '9' || _dcs[i] == ';'))
                return;
        var pic = Sixel.Decode(_dcs.GetRange(0, q).ToArray(), _dcs.GetRange(q + 1, _dcs.Count - q - 1).ToArray(), _bg);
        if (pic == null)
            return;
        pic.Col = CursorX;
        pic.Row = CursorY;
        if (Pictures.Count >= 16)
            Pictures.RemoveAt(0);
        Pictures.Add(pic);
    }

    /// <summary>A sixel decoder: RGB and HLS colour registers, repeats, raster attributes, clear background.</summary>
    public static class Sixel
    {
        public const int MaxPixels = 2048 * 2048;

        public static Picture? Decode(byte[] parameters, byte[] data, int background)
        {
            var p = System.Text.Encoding.ASCII.GetString(parameters).Split(';');
            bool clear = p.Length > 1 && p[1] == "1";   // P2=1: unset pixels stay clear
            var palette = new int[256];
            for (int i = 0; i < 16; i++)
                palette[i] = Cga[i];
            int colour = palette[0];
            int x = 0, band = 0, w = 0, h = 0;
            var set = new Dictionary<int, int>();   // y * 4096 + x -> colour
            int i2 = 0;

            int Number(ref int at)
            {
                int v = 0;
                while (at < data.Length && data[at] >= '0' && data[at] <= '9')
                    v = Math.Min(v * 10 + (data[at++] - '0'), 99999);
                return v;
            }

            while (i2 < data.Length)
            {
                byte c = data[i2];
                if (c == '"')
                {
                    i2++;
                    for (int k = 0; k < 4; k++)   // Pan;Pad;Ph;Pv
                    {
                        int v = Number(ref i2);
                        if (k == 2) w = Math.Max(w, Math.Min(v, 2048));
                        if (k == 3) h = Math.Max(h, Math.Min(v, 2048));
                        if (i2 < data.Length && data[i2] == ';') i2++; else break;
                    }
                }
                else if (c == '#')
                {
                    i2++;
                    int reg = Number(ref i2) & 255;
                    if (i2 < data.Length && data[i2] == ';')
                    {
                        i2++;
                        int kind = Number(ref i2);
                        int[] v = new int[3];
                        for (int k = 0; k < 3; k++)
                        {
                            if (i2 < data.Length && data[i2] == ';')
                            {
                                i2++;
                                v[k] = Number(ref i2);
                            }
                        }
                        palette[reg] = kind == 1 ? Hls(v[0], v[1], v[2]) : unchecked((int)0xFF000000)
                            | (Pct(v[0]) << 16) | (Pct(v[1]) << 8) | Pct(v[2]);
                    }
                    colour = palette[reg];
                }
                else if (c == '!')
                {
                    i2++;
                    int count = Math.Min(Number(ref i2), 2048);
                    if (i2 < data.Length && data[i2] >= 63 && data[i2] <= 126)
                    {
                        Paint(data[i2] - 63, count);
                        i2++;
                    }
                }
                else if (c == '$')
                {
                    x = 0;
                    i2++;
                }
                else if (c == '-')
                {
                    x = 0;
                    band++;
                    i2++;
                }
                else if (c >= 63 && c <= 126)
                {
                    Paint(c - 63, 1);
                    i2++;
                }
                else
                    i2++;

                if (set.Count > MaxPixels)
                    return null;
            }

            void Paint(int bits, int count)
            {
                for (int k = 0; k < count && x < 2048; k++, x++)
                {
                    for (int dy = 0; dy < 6; dy++)
                        if ((bits & 1 << dy) != 0 && band * 6 + dy < 2048)
                            set[(band * 6 + dy) * 4096 + x] = colour;
                    w = Math.Max(w, x + 1);
                }
                if (bits != 0)
                    h = Math.Max(h, band * 6 + 6);
            }

            if (w == 0 || h == 0)
                return null;
            var pixels = new int[w * h];
            if (!clear)
                Array.Fill(pixels, background);
            foreach (var kv in set)
            {
                int py = kv.Key / 4096, px = kv.Key % 4096;
                if (px < w && py < h)
                    pixels[py * w + px] = kv.Value;
            }
            return new Picture { Width = w, Height = h, Pixels = pixels };
        }

        private static int Pct(int v) => (Math.Min(v, 100) * 255 + 50) / 100;

        private static int Hls(int hue, int light, int sat)
        {
            // Sixel HLS: hue 0-360 with blue at 0, lightness and saturation 0-100
            double hh = ((hue + 240) % 360) / 360.0, l = Math.Min(light, 100) / 100.0, s = Math.Min(sat, 100) / 100.0;
            double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
            double Channel(double t)
            {
                t = (t % 1 + 1) % 1;
                return t < 1 / 6.0 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2 / 3.0 ? p + (q - p) * (2 / 3.0 - t) * 6 : p;
            }
            int r = (int)(Channel(hh + 1 / 3.0) * 255 + 0.5), g = (int)(Channel(hh) * 255 + 0.5), b = (int)(Channel(hh - 1 / 3.0) * 255 + 0.5);
            return unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
        }
    }
}
