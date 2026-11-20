namespace TERMinator.Linux.RainwireWm;

/// <summary>A bitmap font: 256 CP437 glyphs, 8 pixels wide, one byte per row (bit 7 on the left).</summary>
public sealed class WmFont
{
    public byte[] Data { get; init; } = Array.Empty<byte>();
    public int Height { get; init; } = 16;
}

/// <summary>
/// How the board wants its windows to look (the Theme message): the colours, the frame and button glyphs, the start
/// button's text and the font. TERMinator is only the engine: it draws the frames, taskbar and start menu as cells in
/// these colours and glyphs, with the same bitmap font as the window text, exactly as Rainwire's server-drawn desktop
/// would. Glyphs are CP437 codes (the board sends characters CP437 has). No Avalonia, so the harness can test it.
/// </summary>
public sealed class WmTheme
{
    // ARGB colours
    public int Bg, Frame, FrameFocus, TitleFg, TitleBg, FocusFg, FocusBg, Outline;
    public int BarFg, BarBg, TaskFg, TaskBg, DimFg, Hot, PopFg, PopBg, SelFg, SelBg;
    /// <summary>The colour of a window's shadow, or null for no shadow.</summary>
    public int? Shadow;
    /// <summary>
    /// The fades a look like the board's modern one draws (Rainwire 0.9.19): the focused title bar and the start menu's
    /// selection from FocusBg to FocusBg2, the focused taskbar button from TaskBg to TaskBg2, and the start button from
    /// StartFrom to StartTo with its text in StartFg. Null when the board sent none: draw those flat.
    /// </summary>
    public int? FocusBg2, TaskBg2, StartFrom, StartTo, StartFg;

    /// <summary>Frame corners and lines: top-left, top-right, bottom-left, bottom-right, horizontal, vertical.</summary>
    public byte[] Box = Cp("╔╗╚╝═║");
    /// <summary>The title bar's buttons: minimize, maximize, close.</summary>
    public byte[] Buttons = Cp("-■x");
    public byte[] Start = Cp("≡"), Grip = Cp("┘");
    public byte[] StartText = Cp("Rainwire"), MenuText = Cp("Start");
    /// <summary>The same two texts as the board sent them, for the graphical look's own fonts.</summary>
    public string StartLabel = "Rainwire", MenuLabel = "Start";
    /// <summary>A font the board sent (Font messages) to draw with; "" for the terminal's own.</summary>
    public string Font = "";

    /// <summary>What to draw with until the board says: the classic look, as Rainwire draws it for SyncTERM.</summary>
    public WmTheme()
    {
        Bg = Rgb(0x000000);
        Frame = Rgb(0xAAAAAA); FrameFocus = Rgb(0xFFFFFF);
        TitleFg = Rgb(0xAAAAAA); TitleBg = Rgb(0x0000AA);
        FocusFg = Rgb(0xFFFFFF); FocusBg = Rgb(0x00AAAA);
        Outline = Rgb(0xFFFF55);
        BarFg = Rgb(0x000000); BarBg = Rgb(0x00AAAA);
        TaskFg = Rgb(0xFFFFFF); TaskBg = Rgb(0x0000AA);
        DimFg = Rgb(0x555555); Hot = Rgb(0xFFFF55);
        PopFg = Rgb(0xFFFFFF); PopBg = Rgb(0x0000AA);
        SelFg = Rgb(0x000000); SelBg = Rgb(0x00AAAA);
    }

    public byte TL => Box[0];
    public byte TR => Box[1];
    public byte BL => Box[2];
    public byte BR => Box[3];
    public byte H => Box[4];
    public byte V => Box[5];

    /// <summary>The look from a Theme message; anything missing or malformed keeps its default.</summary>
    public static WmTheme From(WmMessage m)
    {
        var t = new WmTheme();
        void Col(string key, ref int into)
        {
            if (ParseRgb(m.Get(key)) is int c)
                into = c;
        }
        Col("bg", ref t.Bg);
        Col("frame", ref t.Frame);
        Col("frame_focus", ref t.FrameFocus);
        Col("title_fg", ref t.TitleFg);
        Col("title_bg", ref t.TitleBg);
        Col("focus_fg", ref t.FocusFg);
        Col("focus_bg", ref t.FocusBg);
        Col("outline", ref t.Outline);
        Col("bar_fg", ref t.BarFg);
        Col("bar_bg", ref t.BarBg);
        Col("task_fg", ref t.TaskFg);
        Col("task_bg", ref t.TaskBg);
        Col("dim_fg", ref t.DimFg);
        Col("hot", ref t.Hot);
        Col("pop_fg", ref t.PopFg);
        Col("pop_bg", ref t.PopBg);
        Col("sel_fg", ref t.SelFg);
        Col("sel_bg", ref t.SelBg);
        t.Shadow = ParseRgb(m.Get("shadow"));
        t.FocusBg2 = ParseRgb(m.Get("focus_bg2"));
        t.TaskBg2 = ParseRgb(m.Get("task_bg2"));
        t.StartFrom = ParseRgb(m.Get("start_from"));
        t.StartTo = ParseRgb(m.Get("start_to"));
        t.StartFg = ParseRgb(m.Get("start_fg"));

        if (Cp(m.Get("box")) is { Length: 6 } box)
            t.Box = box;
        if (Cp(m.Get("buttons")) is { Length: 3 } buttons)
            t.Buttons = buttons;
        if (Cp(m.Get("start")) is { Length: 1 } start)
            t.Start = start;
        if (Cp(m.Get("grip")) is { Length: 1 } grip)
            t.Grip = grip;
        string startText = m.Get("start_text"), menuText = m.Get("menu_text");
        if (startText.Length is > 0 and <= 40)
            (t.StartText, t.StartLabel) = (Cp(startText), startText);
        if (menuText.Length is > 0 and <= 40)
            (t.MenuText, t.MenuLabel) = (Cp(menuText), menuText);
        string font = m.Get("font");
        if (font.Length <= 64)
            t.Font = font;
        return t;
    }

    /// <summary>rrggbb as an opaque ARGB colour, or null.</summary>
    public static int? ParseRgb(string s)
    {
        if (s.Length != 6)
            return null;
        int v = 0;
        foreach (char c in s)
        {
            int d = c is >= '0' and <= '9' ? c - '0' : c is >= 'a' and <= 'f' ? c - 'a' + 10 : c is >= 'A' and <= 'F' ? c - 'A' + 10 : -1;
            if (d < 0)
                return null;
            v = v * 16 + d;
        }
        return Rgb(v);
    }

    private static int Rgb(int rgb) => unchecked((int)0xFF000000) | rgb;

    /// <summary>Text as CP437 codes for the bitmap font: '?' for anything CP437 lacks, and never a control code.</summary>
    public static byte[] Cp(string s)
    {
        var b = new byte[s.Length];
        for (int i = 0; i < s.Length; i++)
            b[i] = Cp437.FromChar(s[i]) ?? (byte)'?';
        return b;
    }
}
