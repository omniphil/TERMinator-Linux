using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Rendering;
using Avalonia.Threading;

namespace TERMinator.Linux.Controls;

/// <summary>
/// A Rainwire board's menus drawn by TERMinator itself (1.1.6; Rainwire docs/MENU_GFX.md, "Native menus (TERMinator
/// 1.1.6)"). The board sends the menu as Rainwire:UI messages (native/apc/ui_bridge.c queues them, ConnectionManager
/// polls them): Hello once a call, Tick, Menu + Items, then Show; Hide gives the terminal back. The packets are
/// Rainwire's Menu module protocol (Rainwire modules/menu/src/protocol.h), unchanged, and this draws the same screen
/// the module (menumod.c) draws:
///   the header picture (the board's GIF, still playing, or a PNG) as wide as the terminal picture, as tall as its
///     rows on the text screen
///   the menu box: the title on its top border, "Esc Back" on its bottom one, the items in the menu's layout, each
///     label in its own colors (spans the board worked out), pulsing, shimmering, blinking where it says
///   the help line, and a status bar (the menu, the node, the clock, minutes left, "Rainwire")
///   the rain behind it all where the board's look has it, and a dialog over it (Quote of the day)
/// Or, in the box's place, the Updates & News screen (a Menu head with MfNews, then News + NewsItems; Rainwire 0.9.137,
/// docs/MENU_GFX.md "Updates & News"): the board's news in a panel, scrolled here, the stats bar under it; Enter, Esc,
/// Space, Q or a click says Back.
/// Or a page (MfPage: Page + PageLines, System Info and the like, in the mono font, scrolled here) or a list (MfList:
/// List + ListItems, user lists and the like; moves are local, Enter/a click picks, other keys go back as Key), Rainwire
/// 0.9.141, docs/MENU_GFX.md "Pages and lists". Or a form (MfPage and MfList both: Form + FormFields; Rainwire 0.9.142,
/// "Forms"): edited here, every value back in one FormDone when it's submitted or canceled. Or a picture screen
/// (MuSMenu.screen 1: Picture + Chart; Rainwire 0.9.143, "Charts"): tabs, lines, and a chart drawn here from its numbers,
/// with tooltips; every key goes back as Key.
/// Or a scene (1.1.6, RainwireScene.cs; Rainwire docs/SCENES.md): any screen as a tree of blocks, laid out here.
/// It never decides anything: it says which item was picked (Pick;i= its index, or key= a typed key), Back, Key (a
/// dialog's key), and Ping (active=1 when the caller did something) every 10 s; the board does the rest. Answers go
/// out through Send.
/// Linux port of the Windows client's Controls/RainwireMenu.cs (the master), 2026-10-20: same protocol, layout and
/// replies, in Avalonia; the header GIF plays through TerminalView's frame decoder.
/// </summary>
public sealed partial class RainwireMenu : Panel
{
    // ---- protocol.h ----
    private const int ProtoVersion = 3;
    private const uint None = 0xFF000000;
    private const byte LayoutColumns = 1, LayoutGrid = 2;
    private const byte PosLeft = 1, PosRight = 2;
    private const byte MfGradient = 0x01, MfBrackets = 0x02, MfRoot = 0x04, MfShadow = 0x08, MfRain = 0x10, MfNews = 0x20,
                       MfPage = 0x40, MfList = 0x80;
    private const int MaxPage = 2000, MaxList = 500;
    private const byte LfSubBelow = 0x01, LfBig = 0x02;          // MuSList.flags
    private const byte LiHeading = 0x01, LiBold = 0x02, LiDim = 0x04;   // MuListItem.flags
    private const int VkChar = 0, VkLeft = 1, VkRight = 2, VkDelete = 3, VkBackspace = 4, VkTab = 5, VkUp = 6, VkDown = 7, VkF1 = 8;
    /// <summary>A page's colors (MU_S_PAGE) and a list's (MU_S_LIST), in protocol.h's order.</summary>
    private enum Pc { PanelBg, Border, Title, Header, Text, Rule, Foot, Count }
    private enum Lc { PanelBg, Border, Title, Text, Dim, Bright, Accent, Accent2, Mark, Foot, Count }
    private const int MaxFields = 32, MaxValue = 1000;   // a field's value: MU_MAX_VALUE bytes
    private const byte FfSecret = 0x01, FfChoice = 0x02;  // MuFormField.flags
    private const byte CForm = 7, FormSubmit = 1, FormCancel = 2;
    /// <summary>A form's colors (MU_S_FORM), in protocol.h's order.</summary>
    private const byte ScrPicture = 1;   // MuSMenu.screen
    /// <summary>MuSPicture.hasChart (MU_PIC_*) and flags (MU_PICF_*): a chart, or an image (Rainwire's ANSI Studio:
    /// the picture the Picture message names, path= from hdr=), smooth or by whole pixels.</summary>
    private const byte PicChart = 1, PicImage = 2, PicfSmooth = 0x01;
    private const int VkEnter = 9, VkPgUp = 10, VkPgDn = 11, VkHome = 12, VkEnd = 13;   // a picture screen's keys
    private enum Xc { PanelBg, Border, Title, Text, Dim, Accent, Accent2, Grid, TabBg, TabFg, Count }
    private enum Ck { Bars, Stacked, HBars, Line, Heat, Share, Gauge, Spark }
    private enum Fc { FormBg, Border, Title, Label, LabelOn, FieldBg, FieldBgOn, FieldText, Marker, Hint, Error, Count }
    private const byte NsLabelFirst = 0x01;
    private const int NewsLinks = 0x0001;   // MuSNews.flags: web addresses in the text are links
    private const int MaxNews = 60;
    /// <summary>The news screen's colors (MU_S_NEWS), in protocol.h's order.</summary>
    private enum Nc { PanelBg, Border, Title, LatestDate, LatestHead, LatestText, Rule, Date, Head, Text, Foot, StatValue, StatLabel, Count }
    private const byte IfSeparator = 0x01, IfColumn = 0x02;
    private const byte FxShimmer = 1, FxPulse = 2, FxBlink = 3;
    private const int MaxItems = 120, MaxLines = 40;
    private enum Mc
    {
        Bg, Text, Dim, Bright, Accent, Accent2, StatusBg, StatusText, StatusHint, BarTitle, BarClock, PanelTitle,
        PanelShadow, SelFg, SelBg, SelBg2, SelKey, SelMarker, MenuBg, MenuBorder, MenuKey, MenuBracket, MenuLabel,
        MenuTag, MenuHelp, RainHead, RainBody, RainTail, Count,
    }

    /// <summary>The menu protocol version this draws: the ui= key in the Rainwire:Caps answer.</summary>
    public const int Version = ProtoVersion;

    /// <summary>The board screens this app draws itself: the uiscreens= key ('+' between them as more come).</summary>
    public const string Screens = "menu+dialog+news+page+list+form+picture+art+wire+weather+gadgets";

    private sealed record Span(uint Fg, uint Bg, byte Fx, string Text);

    /// <summary>A box over the menu that waits for a key (MU_S_DIALOG): Quote of the day and the like.</summary>
    private sealed record Dialog(string Title, string Foot, uint Bg, uint Border, uint Fg, uint FootFg, Span[][] Lines);

    private sealed class Item
    {
        public int Key;
        public uint KeyFg, KeyBg;
        public byte Flags;
        public Span[] Spans = [];
        public string Tag = "", Help = "";
        public Rect Box;        // where the layout put it (DIPs); Width 0: not drawn
        public bool Selectable => (Flags & (IfSeparator | IfColumn)) == 0;
    }

    /// <summary>The sysop's settings for how the next screen is drawn (MU_S_STYLE, the theme's gfx block; Rainwire
    /// docs/MENU_GFX.md "Drawing the style"): each at its default (today's look) until a Style sets it.</summary>
    private sealed class Style
    {
        public int VAlign;                  // 0 auto, 1 top, 2 middle, 3 bottom
        public double X, Y, Width;          // % of the window
        public double Radius = 6, Border = 1.5, Padding = 16, Opacity = 100;
        public int Shadow, Title;           // shadow 0 soft, 1 hard, 2 none (MfShadow still says whether); title 0 border, 1 inside, 2 none
        public int Font;                    // 0 sans, 1 mono
        public double Size = 17, Height = 26, Gap;
        public int Key;                     // 0 auto, 1 bracket, 2 spaced, 3 chip, 4 none
        public int Sel;                     // 0 bar, 1 outline, 2 glow, 3 underline
        public bool Marker = true;
        public double SelRadius = 4;
        public int Fit;                     // 0 stretch, 1 fit, 2 none
        public bool Help = true, Status = true, Hover = true;
        public double Dim = 10, Fade = 160;
    }

    private sealed record NewsItem(string Date, Span[] Title, Span[] Text);

    /// <summary>A form's field: edited here (Cur is a UTF-16 index into Value, never inside a surrogate pair).</summary>
    private sealed class FField
    {
        public byte Flags;
        public int Max;            // characters it may hold; 0 no limit (MaxValue bytes)
        public string Label = "", Value = "", Help = "", SubmitIf = "";
        public int Cur;
        public string[] Choices = [];
        public bool Choice => (Flags & FfChoice) != 0;
        public bool Secret => (Flags & FfSecret) != 0;
    }

    /// <summary>A list's row: its flags (heading, bold, dim), indent steps, mark, sub line and columns' spans.</summary>
    private sealed class LRow
    {
        public byte Flags;
        public int Indent;
        public string Mark = "", Sub = "";
        public Span[][] Cols = [];
        public double Y, H;     // where it goes from the top of the rows (DIPs)
        public bool Ok => (Flags & LiHeading) == 0;
    }
    private sealed record NewsStat(bool LabelFirst, string Value, string Label);

    private sealed class Head
    {
        public int Selected, Count, Layout, Columns, Position, Flags, Width, TileCols, TileRows, HdrRows, ScreenRows, Screen;
        public string Title = "";
        public uint[] Colors = new uint[(int)Mc.Count];
    }

    /// <summary>A reply for the board: the text after "Rainwire:UI;" (MainWindow wraps it in an APC and sends it).</summary>
    public event Action<string>? Send;

    /// <summary>Up over the terminal, with the keyboard.</summary>
    public bool IsUp { get; private set; }

    /// <summary>The menu went down (Hide, or the call ended): the keyboard goes back to the terminal.</summary>
    public event Action? Hidden;

    /// <summary>The menu came up or went down (MainWindow fits it over the terminal canvas then).</summary>
    public event Action? UpChanged;

    /// <summary>Where the terminal's picture is, in this control's coordinates (null: all of it). The header lands
    /// on the text screen's header rows: the picture leaves out the app's status bar and any letterbox bands.</summary>
    public Func<Rect?>? PictureRect { get; set; }

    /// <summary>TERMinator's Font smoothing (the terminal picture scaled smoothly): pixel-art headers follow it, so
    /// they look as the same art does on the text screen (crisp when off, soft when on).</summary>
    public bool SmoothPixels
    {
        get => _smoothPixels;
        set { _smoothPixels = value; ScaleHeader(); }
    }
    private bool _smoothPixels;
    private bool _headerPixelArt;

    /// <summary>How much darker than the theme's colors everything the menu draws is (sysop 2026-10-20: the graphical
    /// menus, header art included, read brighter than the text ones): one translucent black layer on top.</summary>
    private const double Dim = 0.10;
    private readonly Avalonia.Controls.Shapes.Rectangle _dim = new()
    {
        Fill = new ImmutableSolidColorBrush(Color.FromArgb((byte)(255 * Dim), 0, 0, 0)), IsHitTestVisible = false,
    };

    private readonly Surface _surface;
    private readonly Overlay _overlay;   // the dialog, above the header picture (an Image over the surface)
    private readonly Image _header;
    private readonly DispatcherTimer _anim, _ping, _rainTick, _gifTimer, _hideLater;

    private Head _head = new();
    private Style _st = new();
    private readonly List<Item> _items = [];
    private int _want;             // items the current menu's head said are coming
    private bool _complete;        // every item has come
    private int _sel;
    private bool _waiting;         // picked: the board is answering
    private bool _active;          // the caller did something since the last message
    private string _bbs = "", _clock = "";
    private int _node, _minutes = 0xFFFF;
    private string? _headerPath;
    private int _headerW, _headerH;
    private Views.TerminalView.PicFrame[]? _headerFrames;   // the header's frames (one for a still picture)
    private int _headerFrame, _headerGeneration;
    private DateTime _shownAt;
    private Dialog? _dialog;       // up over the menu until the board's next Dialog, Menu, Show or Hide
    private string _toast = "";    // MU_S_TOAST: in the status bar's place until _toastUntil (someone logged on...)
    private Avalonia.Media.Imaging.Bitmap? _backdrop; // MU_S_BACKDROP: the screen's background art, behind the menu
    private string? _backdropPath;
    private int _bdScreenCols, _bdScreenRows, _bdCols, _bdRows;
    private uint _toastFg, _toastBg;
    private DateTime _toastUntil;
    private bool _dialogKeyed;     // its key has gone to the board

    // The news screen (MU_S_NEWS, MU_S_NEWS_ITEMS): in the box's place when the head has MfNews
    private bool _isNews;
    private bool _newsLinks;       // MuSNews.flags has NewsLinks
    private Point _mouse = new(-1, -1);
    private readonly List<(Rect R, string Url)> _linkHits = [];   // the links drawn last, on screen
    private readonly List<NewsItem> _news = [];
    private int _newsWant = -1;    // items its head said are coming (-1: no head yet)
    private readonly uint[] _nc = new uint[(int)Nc.Count];
    private string _earlier = "", _foot = "";
    private NewsStat[][] _stats = [];
    private double _scroll, _scrollTo, _scrollMax, _viewH;   // DIPs
    private DateTime _scrollAt;

    // A page (MU_S_PAGE, MU_S_PAGE_LINES) or a list (MU_S_LIST, MU_S_LIST_ITEMS): in the box's place when the head has
    // MfPage or MfList
    private bool _isPage, _isList;
    private readonly uint[] _pc = new uint[(int)Pc.Count], _lc = new uint[(int)Lc.Count];
    private Span[] _pageHdr = [];
    private readonly List<Span[]> _lines = [];
    private int _linesWant = -1;
    private (int Width, string Title)[] _cols = [];
    private string _lempty = "", _lfooter = "", _lkeys = "", _ledge = "";
    private byte _lflags;
    private int _lstart, _lsel, _rowsWant = -1;
    private readonly List<LRow> _rows = [];
    private readonly List<(Rect R, int I)> _rowHits = [];   // the rows drawn last, on screen
    private bool _listSnap;        // a list just came: its first layout jumps to the selection, no smooth scroll

    // A form (MU_S_FORM, MU_S_FORM_FIELDS): when the head has MfPage and MfList both
    private bool _isForm;
    private readonly uint[] _fc = new uint[(int)Fc.Count];
    private readonly List<FField> _fields = [];
    private int _fieldsWant = -1, _fact;
    private string _fhint = "", _ferr = "";
    private DateTime _caretAt;
    private readonly List<(Rect R, int Field, int Choice, double TextX)> _formHits = [];   // the fields drawn last

    // A picture screen (MU_S_PICTURE, MU_S_CHART): when the head's screen is ScrPicture
    private sealed class Chart
    {
        public Ck Kind;
        public int Every;
        public double Max, Value;
        public string Unit = "";
        public string[] Labels = [];
        public (Color C, string Name, double[] V)[] Series = [];
        public string[] RowL = [], ColL = [];
        public double[] Heat = [];
    }
    private bool _isPic, _hasChart;
    private readonly uint[] _xc = new uint[(int)Xc.Count];
    private string[] _tabs = [];
    private int _tab;
    private Span[][] _heads = [], _notes = [];
    private Chart? _chart;
    private readonly List<(Rect R, int Tab)> _tabHits = [];
    private Avalonia.Media.Imaging.Bitmap? _picImage;   // a picture screen's image (MU_PIC_IMAGE), in the chart's place
    private string? _picImagePath;
    private bool _picSmooth;

    public RainwireMenu()
    {
        IsVisible = false;
        // The menu takes the keyboard focus while it's up, so the terminal (which sends keys only while it has the
        // focus, TerminalCanvas) doesn't, and keys turn into typed text the menu reads (HandleText)
        Focusable = true;
        FocusAdorner = null;
        _surface = new Surface(this);
        _header = new Image
        {
            Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
        };
        _overlay = new Overlay(this) { IsHitTestVisible = false };   // clicks go to the surface (OnClick)
        Children.Add(_surface);
        Children.Add(_header);
        Children.Add(_overlay);
        Children.Add(_dim);   // over everything: the menu, the header, the dialog
        _anim = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(40) };
        _anim.Tick += (_, _) =>
        {
            _surface.InvalidateVisual();
            if (_wire != null || _wx != null || _gadgets.Count > 0) _overlay.InvalidateVisual();   // the news panel scrolls, the buttons light
        };
        _rainTick = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(150) };
        _rainTick.Tick += (_, _) => RainStep();
        _ping = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _ping.Tick += (_, _) => Reply(_active ? "Ping;active=1" : "Ping");
        _gifTimer = new DispatcherTimer();
        _gifTimer.Tick += (_, _) => NextHeaderFrame();
        // Hide keeps the picture up a moment: the board has already drawn the next screen as terminal text, but the
        // terminal canvas repaints on its own cycle, so collapsing at once showed its old picture for a frame (the
        // sysop saw the connect ANSI flash after answering "Log in hidden?")
        _hideLater = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _hideLater.Tick += (_, _) =>
        {
            _hideLater.Stop();
            if (!IsUp) IsVisible = false;
        };
        SizeChanged += (_, _) => Redraw();
    }

    // ================================================================
    // Messages from the board
    // ================================================================

    /// <summary>
    /// One queued Rainwire:UI message, as native/apc/ui_bridge.c hands it over: "Verb[;b64=...][;path=...]". UI thread.
    /// </summary>
    public void Handle(string msg)
    {
        string verb = msg;
        string? b64 = null, path = null;
        int semi = msg.IndexOf(';');
        if (semi >= 0)
        {
            verb = msg[..semi];
            string rest = msg[(semi + 1)..];
            int p = rest.IndexOf("path=", StringComparison.Ordinal);
            if (p >= 0 && (p == 0 || rest[p - 1] == ';'))
            {
                path = rest[(p + 5)..];
                rest = rest[..Math.Max(0, p - 1)];
            }
            foreach (var f in rest.Split(';'))
                if (f.StartsWith("b64=", StringComparison.Ordinal))
                    b64 = f[4..];
        }
        byte[]? packet = null;
        if (b64 != null)
        {
            try { packet = Convert.FromBase64String(b64); }
            catch (FormatException) { return; }
        }
        try
        {
            switch (verb)
            {
                case "Hello": if (packet != null) OnHello(packet); break;
                case "Tick": if (packet != null) OnTick(packet); break;
                case "Menu": if (packet != null) OnMenu(packet, path); break;
                case "Items": if (packet != null) OnItems(packet); break;
                case "Style": if (packet != null) OnStyle(packet); break;
                case "Page": if (packet != null) OnPage(packet); break;
                case "PageLines": if (packet != null) OnPageLines(packet); break;
                case "List": if (packet != null) OnList(packet); break;
                case "ListItems": if (packet != null) OnListItems(packet); break;
                case "Form": if (packet != null) OnForm(packet); break;
                case "FormFields": if (packet != null) OnFormFields(packet); break;
                case "Picture": if (packet != null) OnPicture(packet, path); break;
                case "Chart": if (packet != null) OnChart(packet); break;
                case "News": if (packet != null) OnNews(packet); break;
                case "NewsItems": if (packet != null) OnNewsItems(packet); break;
                case "Dialog": if (packet != null) OnDialog(packet); break;
                case "Toast": if (packet != null) OnToast(packet); break;
                case "Backdrop": if (packet != null) OnBackdrop(packet, path); break;
                case "Scene": if (packet != null) OnScene(packet); break;
                case "SceneBlocks": if (packet != null) OnSceneBlocks(packet); break;
                case "SceneCells": if (packet != null) OnSceneCells(packet); break;
                case "SceneImage": if (packet != null) OnSceneImage(packet, path); break;
                case "ScenePatch": if (packet != null) OnScenePatch(packet); break;
                case "Wire": if (packet != null) OnWire(packet); break;
                case "WirePic": if (packet != null) OnWirePic(packet, path); break;
                case "Weather": if (packet != null) OnWeather(packet); break;
                case "Gadget": if (packet != null) OnGadget(packet); break;
                case "SceneAsset": if (packet != null) OnSceneAsset(packet, path); break;   // scenes v2 (RainwireSceneV2.cs)
                case "Clipboard": case "Open": case "Sound": case "SceneToast": HandleV2(verb, msg); break;
                case "GadgetScene": case "GadgetBlocks": case "GadgetCells": case "GadgetImage": case "GadgetPatch":
                case "GadgetDraw":
                    if (packet != null) OnGadgetPart(verb, packet, path);
                    break;
                case "SceneDraw": if (packet != null) OnSceneDraw(packet); break;         // scenes v6 (RainwireSceneV6.cs)
                case "GadgetPlace": if (packet != null) OnGadgetPlace(packet); break;     // the gadget engine (RainwireGadgets.cs)
                case "GadgetState": if (packet != null) OnGadgetState(packet); break;
                case "Show": Show(); break;
                case "Hide": Hide(); break;
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException)
        {
            Debug.WriteLine($"RainwireMenu: a bad {verb}: {ex.Message}");   // a short packet: ignored, as the module does
        }
    }

    /// <summary>The call ended: down, and everything forgotten.</summary>
    public void Reset()
    {
        if (_dragRow != null) SceneDragEnd();   // a row's drag out: the drag state goes with the screen
        Hide(now: true);
        _items.Clear();
        _complete = false;
        _bbs = _clock = _toast = "";
        _backdrop = null;
        _backdropPath = null;
        _picImage = null;
        _picImagePath = null;
        _isScene = false;
        _scene = _scenePrev = null;
        _sceneImages.Clear();
        _cellFont = null;
        _cellFontTried = false;
        _dialog = null;
        WireReset();
        _wireKeep = null;
        WidgetsForget();
        _assets.Clear();   // scenes v2: the call's pictures
        SetHeader(null);
    }

    private static int U16(byte[] b, int o) => b[o] | b[o + 1] << 8;
    private static uint U32(byte[] b, int o) => (uint)(b[o] | b[o + 1] << 8 | b[o + 2] << 16 | b[o + 3] << 24);
    private static string Str(byte[] b, ref int o, int n)
    {
        n = Math.Min(n, b.Length - o);
        string s = Encoding.UTF8.GetString(b, o, Math.Max(n, 0));
        o += Math.Max(n, 0);
        return s;
    }

    private void OnHello(byte[] p)
    {
        int o = 4;
        _minutes = U16(p, o + 2);
        int colours = U16(p, o + 4), bbsLen = p[o + 6];
        _node = p[o + 7];
        o += 8 + 4 * colours;
        _bbs = Str(p, ref o, bbsLen);
    }

    private void OnTick(byte[] p)
    {
        _minutes = U16(p, 4);
        int o = 8;
        _clock = Str(p, ref o, p[6]);
        if (IsUp) Redraw();
    }

    private void OnMenu(byte[] p, string? path)
    {
        if (_dragRow != null) SceneDragEnd();   // a new screen mid-drag: no drag state left behind
        int o = 4;
        var h = new Head
        {
            Selected = U16(p, o), Count = U16(p, o + 2), Layout = p[o + 6], Columns = p[o + 7], Position = p[o + 8],
            Flags = p[o + 9], Width = U16(p, o + 10), TileCols = p[o + 12], TileRows = p[o + 13], HdrRows = p[o + 15],
            ScreenRows = p[o + 80], Screen = p[o + 81],
        };
        int titleLen = p[o + 4], colors = p[o + 14];
        o += 84;
        for (int i = 0; i < colors; i++, o += 4)
            if (i < (int)Mc.Count) h.Colors[i] = U32(p, o) & 0xFFFFFF;
        h.Title = Str(p, ref o, titleLen);
        _head = h;
        _dialog = null;
        WireReset();   // a news panel comes with the menu that has one (Wire, before Show)
        _items.Clear();
        _want = Math.Min(h.Count, MaxItems);
        // a scene (MU_SCR_SCENE, 1.1.6; RainwireScene.cs): its Scene head and blocks come next; a pop-up scene
        // (SF_OVER) is drawn over the scene before it, so that one is kept until the head says
        bool scene = h.Screen == ScrScene;
        _scenePrev = scene && _isScene && _complete ? _scene : null;
        _scene = null;
        _isScene = scene;
        _isNews = !scene && (h.Flags & MfNews) != 0;
        _isPic = h.Screen == ScrPicture;   // a screen the flags don't name: checked first
        _isForm = !_isPic && !scene && (h.Flags & (MfPage | MfList)) == (MfPage | MfList);   // both bits: a form, not a page or a list
        _isPage = !_isPic && !scene && !_isForm && (h.Flags & MfPage) != 0;
        _isList = !_isPic && !scene && !_isForm && (h.Flags & MfList) != 0;
        _complete = _want == 0 && !_isNews && !_isPage && !_isList && !_isForm && !_isPic && !_isScene;   // those wait for their own packets
        if (_isPic) { _hasChart = false; _chart = null; _picImage = null; }
        if (_isForm) _fieldsWant = -1;
        if (_isNews) _newsWant = -1;
        if (_isPage) _linesWant = -1;
        if (_isList) _rowsWant = -1;
        _layoutKey = default;
        SetHeader(path);
    }

    /// <summary>MU_S_STYLE (before every Menu head, the news's too): back to the defaults, then each key it has
    /// (numbers in tenths), clamped as the module does (menumod.c on_style); ids from a newer Rainwire are skipped.</summary>
    private void OnStyle(byte[] p)
    {
        var st = new Style();
        int count = p[5];
        for (int i = 0, o = 8; i < count && o + 4 <= p.Length; i++, o += 4)
        {
            int v = (short)(p[o + 2] | p[o + 3] << 8);
            double f = v / 10.0;
            switch (p[o])
            {
                case 1: st.VAlign = Math.Clamp(v, 0, 3); break;
                case 2: st.X = Math.Clamp(f, -50, 50); break;
                case 3: st.Y = Math.Clamp(f, -50, 50); break;
                case 4: st.Width = Math.Clamp(f, 0, 100); break;
                case 5: st.Radius = Math.Clamp(f, 0, 24); break;
                case 6: st.Border = Math.Clamp(f, 0, 4); break;
                case 7: st.Padding = Math.Clamp(f, 4, 40); break;
                case 8: st.Opacity = Math.Clamp(f, 30, 100); break;
                case 9: st.Shadow = Math.Clamp(v, 0, 2); break;
                case 10: st.Title = Math.Clamp(v, 0, 2); break;
                case 11: st.Font = Math.Clamp(v, 0, 1); break;
                case 12: st.Size = Math.Clamp(f, 12, 28); break;
                case 13: st.Height = Math.Clamp(f, 18, 48); break;
                case 14: st.Gap = Math.Clamp(f, 0, 12); break;
                case 15: st.Key = Math.Clamp(v, 0, 4); break;
                case 16: st.Sel = Math.Clamp(v, 0, 3); break;
                case 17: st.Marker = v != 0; break;
                case 18: st.SelRadius = Math.Clamp(f, 0, 16); break;
                case 19: st.Fit = Math.Clamp(v, 0, 2); break;
                case 20: st.Help = v != 0; break;
                case 21: st.Status = v != 0; break;
                case 22: st.Dim = Math.Clamp(f, 0, 30); break;
                case 23: st.Fade = Math.Clamp(f, 0, 600); break;
                case 24: st.Hover = v != 0; break;
            }
        }
        _st = st;
        _dim.Fill = new ImmutableSolidColorBrush(Color.FromArgb((byte)(255 * st.Dim / 100), 0, 0, 0));
        _layoutKey = default;
        Redraw();
    }

    /// <summary>The news head: its colors, "Earlier", the foot, the stats rows; the items follow.</summary>
    private void OnNews(byte[] p)
    {
        int o = 4, items = U16(p, o), colors = p[o + 2], earlierLen = p[o + 3], footLen = p[o + 4], rows = p[o + 5];
        _newsLinks = (U16(p, o + 6) & NewsLinks) != 0;
        o += 8;
        for (int i = 0; i < colors; i++, o += 4)
            if (i < (int)Nc.Count) _nc[i] = U32(p, o) & 0xFFFFFF;
        _earlier = Str(p, ref o, earlierLen);
        _foot = Str(p, ref o, footLen);
        var stats = new List<NewsStat[]>();
        for (int r = 0; r < rows && r < 4 && o < p.Length; r++)
        {
            int parts = p[o++];
            var row = new List<NewsStat>();
            for (int k = 0; k < parts && o + 4 <= p.Length; k++)
            {
                byte flags = p[o];
                int vl = p[o + 1], ll = p[o + 2];
                o += 4;
                string v = Str(p, ref o, vl), l = Str(p, ref o, ll);
                if (k < 8) row.Add(new NewsStat((flags & NsLabelFirst) != 0, v, l));
            }
            stats.Add([.. row]);
        }
        _stats = [.. stats];
        _news.Clear();
        _newsWant = Math.Min(items, MaxNews);
        _layoutKey = default;
        if (_isNews && _newsWant == 0) NewsReady();
    }

    private void OnNewsItems(byte[] p)
    {
        int first = U16(p, 4), count = U16(p, 6), o = 8;
        if (_newsWant < 0 || first != _news.Count) return;   // out of order: a stale screen's
        for (int i = 0; i < count && _news.Count < _newsWant; i++)
        {
            int dateLen = p[o], titleSpans = p[o + 1], textSpans = U16(p, o + 2);
            o += 4;
            string date = Str(p, ref o, dateLen);
            var title = ReadSpans(p, ref o, titleSpans);
            _news.Add(new NewsItem(date, title, ReadSpans(p, ref o, textSpans)));
        }
        if (_isNews && _news.Count >= _newsWant) NewsReady();
    }

    // ---- pages and lists: reading (menumod.c on_page, on_page_lines, on_list, on_list_items) ----

    private void OnPage(byte[] p)
    {
        int o = 4, lines = U16(p, o), colors = p[o + 2], hdrSpans = p[o + 3];
        o += 8;
        for (int i = 0; i < colors; i++, o += 4)
            if (i < (int)Pc.Count) _pc[i] = U32(p, o) & 0xFFFFFF;
        _pageHdr = ReadSpans(p, ref o, hdrSpans);
        _lines.Clear();
        _linesWant = Math.Min(lines, MaxPage);
        if (_isPage && _linesWant == 0) NewsReady();
    }

    private void OnPageLines(byte[] p)
    {
        int first = U16(p, 4), count = U16(p, 6), o = 8;
        if (_linesWant < 0 || first != _lines.Count) return;   // out of order: a stale screen's
        for (int i = 0; i < count && _lines.Count < _linesWant && o < p.Length; i++)
        {
            int n = p[o++];
            _lines.Add(ReadSpans(p, ref o, n));
        }
        if (_isPage && _lines.Count >= _linesWant) NewsReady();
    }

    private void OnList(byte[] p)
    {
        int o = 4, items = U16(p, o), selected = U16(p, o + 2), cols = p[o + 4], colors = p[o + 5];
        _lflags = p[o + 6];
        int emptyLen = p[o + 7], footerLen = p[o + 8], keysLen = p[o + 9], edgeLen = p[o + 10];
        o += 12;
        for (int i = 0; i < colors; i++, o += 4)
            if (i < (int)Lc.Count) _lc[i] = U32(p, o) & 0xFFFFFF;
        var cs = new List<(int, string)>();
        for (int c = 0; c < cols && o + 2 <= p.Length; c++)
        {
            int width = p[o], tl = p[o + 1];
            o += 2;
            string t = Str(p, ref o, tl);
            if (c < 16) cs.Add((width, t));
        }
        _cols = [.. cs];
        _lempty = Str(p, ref o, emptyLen);
        _lfooter = Str(p, ref o, footerLen);
        _lkeys = Str(p, ref o, keysLen);
        _ledge = Str(p, ref o, edgeLen);
        _lstart = selected;
        _rows.Clear();
        _rowsWant = Math.Min(items, MaxList);
        if (_isList && _rowsWant == 0) ListReady();
    }

    private void OnListItems(byte[] p)
    {
        int first = U16(p, 4), count = U16(p, 6), o = 8;
        if (_rowsWant < 0 || first != _rows.Count) return;
        for (int i = 0; i < count && _rows.Count < _rowsWant && o + 8 <= p.Length; i++)
        {
            var r = new LRow { Flags = p[o], Indent = p[o + 1] };
            int markLen = p[o + 2], subLen = p[o + 3], cols = p[o + 4];
            o += 8;
            r.Mark = Str(p, ref o, markLen);
            r.Sub = Str(p, ref o, subLen);
            var cs = new Span[cols][];
            for (int c = 0; c < cols; c++)
            {
                int n = o < p.Length ? p[o++] : 0;
                cs[c] = ReadSpans(p, ref o, n);
            }
            r.Cols = cs;
            _rows.Add(r);
        }
        if (_isList && _rows.Count >= _rowsWant) ListReady();
    }

    // ---- picture screens: reading (menumod.c on_picture, on_chart) ----

    /// <summary>A picture screen's head; with MU_PIC_IMAGE, the image (path) in the chart's place: Rainwire's ANSI
    /// Studio, the art made cell for cell (drawn by whole pixels) or the picture it came from (MU_PICF_SMOOTH), the
    /// tabs its keys as buttons.</summary>
    private void OnPicture(byte[] p, string? path)
    {
        int o = 4, tabs = p[o], tab = p[o + 1], heads = p[o + 2], notes = p[o + 3], colors = p[o + 4];
        _hasChart = p[o + 5] == PicChart;
        _picSmooth = (p[o + 6] & PicfSmooth) != 0;
        _picImage = p[o + 5] == PicImage && path != null ? LoadPicImage(path) : null;
        o += 8;
        for (int i = 0; i < colors; i++, o += 4)
            if (i < (int)Xc.Count) _xc[i] = U32(p, o) & 0xFFFFFF;
        var ts = new List<string>();
        for (int i = 0; i < tabs && o < p.Length; i++)
        {
            int l = p[o++];
            string t = Str(p, ref o, l);
            if (ts.Count < 16) ts.Add(t);
        }
        _tabs = [.. ts];
        _tab = tab;
        Span[][] Lines(int n)
        {
            var ls = new List<Span[]>();
            for (int i = 0; i < n && o < p.Length; i++)
            {
                int k = p[o++];
                var l = ReadSpans(p, ref o, k);
                if (ls.Count < 16) ls.Add(l);
            }
            return [.. ls];
        }
        _heads = Lines(heads);
        _notes = Lines(notes);
        _chart = null;
        if (_isPic && !_hasChart) NewsReady();
    }

    /// <summary>A picture screen's image from the picture cache (kept while it's the same file).</summary>
    private Avalonia.Media.Imaging.Bitmap? LoadPicImage(string path)
    {
        if (path == _picImagePath && _picImage != null) return _picImage;
        try
        {
            var bitmap = new Avalonia.Media.Imaging.Bitmap(Path.GetFullPath(path));
            _picImagePath = path;
            return bitmap;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"RainwireMenu: picture {path}: {ex.Message}");
            _picImagePath = null;
            return null;
        }
    }

    private static double F32(byte[] p, int o)
    {
        float f = BitConverter.Int32BitsToSingle((int)U32(p, o));
        return float.IsFinite(f) ? f : 0;
    }

    private void OnChart(byte[] p)
    {
        int o = 4;
        var c = new Chart { Kind = (Ck)p[o], Every = p[o + 6], Max = F32(p, o + 8), Value = F32(p, o + 12) };
        int series = p[o + 1], labels = U16(p, o + 2), hr = p[o + 4], hc = p[o + 5], unitLen = p[o + 7];
        o += 16;
        c.Unit = Str(p, ref o, unitLen);
        string[] Strings(int n)
        {
            var a = new List<string>();
            for (int i = 0; i < n && o < p.Length; i++)
            {
                int l = p[o++];
                a.Add(Str(p, ref o, l));
            }
            return [.. a];
        }
        c.Labels = Strings(labels);
        var ss = new List<(Color, string, double[])>();
        for (int s = 0; s < series && s < 8 && o + 8 <= p.Length; s++)
        {
            Color col = C(U32(p, o) & 0xFFFFFF);
            int nl = p[o + 4];
            o += 8;
            string name = Str(p, ref o, nl);
            var v = new double[c.Labels.Length];
            for (int i = 0; i < v.Length && o + 4 <= p.Length; i++, o += 4) v[i] = F32(p, o);
            ss.Add((col, name, v));
        }
        c.Series = [.. ss];
        c.RowL = Strings(hr);
        c.ColL = Strings(hc);
        c.Heat = new double[c.RowL.Length * c.ColL.Length];
        for (int i = 0; i < c.Heat.Length && o + 4 <= p.Length; i++, o += 4) c.Heat[i] = F32(p, o);
        _chart = c;
        if (_isPic) NewsReady();
        Redraw();
    }

    // ---- forms: reading (menumod.c on_form, on_form_fields) ----

    private void OnForm(byte[] p)
    {
        int o = 4, fields = p[o], active = p[o + 1], colors = p[o + 2], hintLen = p[o + 3], errorLen = p[o + 4];
        o += 8;
        for (int i = 0; i < colors; i++, o += 4)
            if (i < (int)Fc.Count) _fc[i] = U32(p, o) & 0xFFFFFF;
        _fhint = Str(p, ref o, hintLen);
        _ferr = Str(p, ref o, errorLen);
        _fields.Clear();
        _formHits.Clear();   // the last form's, until this one is drawn
        _fieldsWant = Math.Min(fields, MaxFields);
        _fact = active;
    }

    private void OnFormFields(byte[] p)
    {
        int first = U16(p, 4), count = U16(p, 6), o = 8;
        if (_fieldsWant < 0 || first != _fields.Count) return;
        for (int i = 0; i < count && _fields.Count < _fieldsWant && o + 12 <= p.Length; i++)
        {
            var f = new FField { Flags = p[o], Max = U16(p, o + 2) };
            int labelLen = p[o + 1], valueLen = U16(p, o + 4), cursor = U16(p, o + 6), helpLen = p[o + 8],
                submitLen = p[o + 9], choices = p[o + 10];
            o += 12;
            f.Label = Str(p, ref o, labelLen);
            f.Value = Str(p, ref o, valueLen);
            f.Help = Str(p, ref o, helpLen);
            f.SubmitIf = Str(p, ref o, submitLen);
            var cs = new List<string>();
            for (int c = 0; c < choices && o < p.Length; c++)
            {
                int l = p[o++];
                string ch = Str(p, ref o, l);
                if (cs.Count < 32) cs.Add(ch);
            }
            f.Choices = [.. cs];
            f.Cur = f.Value.Length;   // cursor: characters back from the end
            for (int k = 0; k < cursor && f.Cur > 0; k++) f.Cur -= f.Cur >= 2 && char.IsLowSurrogate(f.Value[f.Cur - 1]) ? 2 : 1;
            _fields.Add(f);
        }
        if (_isForm && _fields.Count >= _fieldsWant)
        {
            _fact = Math.Clamp(_fact, 0, Math.Max(_fields.Count - 1, 0));
            NewsReady();
            _caretAt = DateTime.UtcNow;
        }
    }

    private bool RowOk(int i) => i >= 0 && i < _rows.Count && _rows[i].Ok;

    private void ListReady()
    {
        _lsel = _lstart < _rows.Count ? _lstart : 0;
        while (_lsel < _rows.Count - 1 && !RowOk(_lsel)) _lsel++;
        NewsReady();
        _listSnap = true;
    }

    private void NewsReady()
    {
        _complete = true;
        _scroll = _scrollTo = 0;
        _layoutKey = default;
    }

    private void OnItems(byte[] p)
    {
        int first = U16(p, 4), count = U16(p, 6), o = 8;
        if (first != _items.Count) return;   // out of order: a stale menu's
        for (int i = 0; i < count && _items.Count < _want; i++)
        {
            var it = new Item { Key = (int)U32(p, o), KeyFg = U32(p, o + 4), KeyBg = U32(p, o + 8), Flags = p[o + 12] };
            int spans = p[o + 13], tagLen = p[o + 14], helpLen = p[o + 15];
            o += 16;
            it.Spans = ReadSpans(p, ref o, spans);
            it.Tag = Str(p, ref o, tagLen);
            it.Help = Str(p, ref o, helpLen);
            _items.Add(it);
        }
        if (_items.Count >= _want)
        {
            _complete = true;
            _sel = Math.Clamp(_head.Selected, 0, Math.Max(0, _items.Count - 1));
            while (_sel < _items.Count - 1 && !_items[_sel].Selectable) _sel++;
        }
    }

    private static Span[] ReadSpans(byte[] p, ref int o, int spans)
    {
        var list = new Span[spans];
        for (int k = 0; k < spans; k++)
        {
            uint fg = U32(p, o), bg = U32(p, o + 4);
            byte fx = p[o + 8];
            int len = p[o + 9];
            o += 10;
            list[k] = new Span(fg, bg, fx, Str(p, ref o, len));
        }
        return list;
    }

    /// <summary>A dialog over the menu (Rainwire's Quote of the day): drawn until the board sends the menu again; the
    /// key or click that closes it goes back as Key.</summary>
    private void OnDialog(byte[] p)
    {
        if (!IsUp)
        {
            Reply("Closed;error=no menu");   // the board only sends one over a menu that is up
            return;
        }
        int o = 4, titleLen = p[o + 1], footLen = p[o + 2], lines = Math.Min((int)p[o + 3], MaxLines);
        uint bg = U32(p, o + 4) & 0xFFFFFF, border = U32(p, o + 8) & 0xFFFFFF, fg = U32(p, o + 12) & 0xFFFFFF,
             footFg = U32(p, o + 16) & 0xFFFFFF;
        o += 20;
        string title = Str(p, ref o, titleLen), foot = Str(p, ref o, footLen);
        var ls = new Span[lines][];
        for (int i = 0; i < lines; i++)
        {
            int spans = p[o++];
            ls[i] = ReadSpans(p, ref o, spans);
        }
        _dialog = new Dialog(title, foot, bg, border, fg, footFg, ls);
        _dialogKeyed = false;
        Redraw();
    }

    /// <summary>A line in the status bar's place for 8 s (MU_TOAST_MS), as the text screen's yellow toast: someone
    /// logged on or off, a message waiting.</summary>
    /// <summary>The screen's background art (art.background), sent with every menu: the caller's screen, the art's
    /// cells (0: none) and its picture (path, from hdr=). Drawn as the text screen places it (DrawBackdrop).</summary>
    private void OnBackdrop(byte[] p, string? path)
    {
        _bdScreenCols = p[4];
        _bdScreenRows = p[5];
        _bdCols = p[6];
        _bdRows = p[7];
        if (_bdCols == 0 || _bdRows == 0 || path == null)
        {
            _backdrop = null;
            _backdropPath = null;
        }
        else if (path != _backdropPath)
        {
            try
            {
                _backdrop = new Avalonia.Media.Imaging.Bitmap(Path.GetFullPath(path));
                _backdropPath = path;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RainwireMenu: backdrop {path}: {ex.Message}");
                _backdrop = null;
                _backdropPath = null;
            }
        }
        Redraw();
    }

    /// <summary>The background art over the terminal picture: from its top, centered across the screen's columns,
    /// cols x rows cells (already cut above the status row), as Rainwire's text screen draws it.</summary>
    private void DrawBackdrop(DrawingContext dc, double w, double h)
    {
        if (_backdrop == null || _bdScreenCols <= 0 || _bdScreenRows <= 0) return;
        var pic = PictureRect?.Invoke() is Rect r && r.Width > 0 && r.Height > 0 ? r : new Rect(0, 0, w, h);
        double cw = pic.Width / _bdScreenCols, ch = pic.Height / _bdScreenRows;
        int x0 = Math.Max((_bdScreenCols - _bdCols) / 2, 0);
        dc.DrawImage(_backdrop, new Rect(pic.X + x0 * cw, pic.Y, _bdCols * cw, _bdRows * ch));
    }

    private void OnToast(byte[] p)
    {
        int o = 4;
        _toastFg = U32(p, o) & 0xFFFFFF;
        _toastBg = U32(p, o + 4) & 0xFFFFFF;
        int len = p[o + 8];
        o += 12;
        _toast = Str(p, ref o, len);
        _toastUntil = DateTime.UtcNow.AddMilliseconds(8000);
        Redraw();
    }

    private void DialogKey(string reply)
    {
        if (_dialogKeyed) return;
        _dialogKeyed = true;
        Reply(reply);
        Redraw();
    }

    /// <summary>
    /// The menu's header picture: the file in the board's picture cache, decoded off the UI thread into its frames
    /// (TerminalView.DecodePicFrames, Skia's codecs, as TERMinator:Pic): a GIF plays, looping. Until it's decoded the
    /// menu draws without a header.
    /// </summary>
    private async void SetHeader(string? path)
    {
        if (path == _headerPath) return;
        _headerPath = path;
        int generation = ++_headerGeneration;
        StopHeader();
        if (path == null) return;
        Views.TerminalView.PicFrame[]? frames = null;
        try
        {
            string full = Path.GetFullPath(path);
            if (File.Exists(full))
                frames = await Task.Run(() => Views.TerminalView.DecodePicFrames(full));
            // Pixel art (the board's GIFs, and the VGA PNGs it makes from ANSI headers) stays crisp when enlarged
            if (frames is { Length: > 0 })
            {
                bool gif = Path.GetExtension(full).Equals(".gif", StringComparison.OrdinalIgnoreCase);
                _headerPixelArt = gif || frames[0].Bitmap.PixelSize.Width <= 1280 && IsVgaPng(full);
                ScaleHeader();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"RainwireMenu: header {path}: {ex.Message}");
        }
        if (generation != _headerGeneration)   // another menu's header came meanwhile
        {
            Views.TerminalView.DisposePicFrames(frames);
            return;
        }
        if (frames is not { Length: > 0 })
        {
            _headerPath = null;
            return;
        }
        _headerFrames = frames;
        _headerFrame = 0;
        _header.Source = frames[0].Bitmap;
        _headerW = frames[0].Bitmap.PixelSize.Width;
        _headerH = frames[0].Bitmap.PixelSize.Height;
        if (frames.Length > 1)
        {
            _gifTimer.Interval = frames[0].Duration;
            _gifTimer.Start();
        }
        Redraw();
    }

    private void NextHeaderFrame()
    {
        if (_headerFrames is not { Length: > 1 } frames)
        {
            _gifTimer.Stop();
            return;
        }
        _headerFrame = (_headerFrame + 1) % frames.Length;
        _header.Source = frames[_headerFrame].Bitmap;
        _gifTimer.Stop();
        _gifTimer.Interval = frames[_headerFrame].Duration;
        _gifTimer.Start();
    }

    private void StopHeader()
    {
        _gifTimer.Stop();
        _header.Source = null;
        Views.TerminalView.DisposePicFrames(_headerFrames);
        _headerFrames = null;
        _headerW = _headerH = 0;
    }

    /// <summary>Pixel art crisp (no interpolation) unless TERMinator's Font smoothing is on, as the terminal picture
    /// is scaled; any other picture smooth.</summary>
    private void ScaleHeader()
    {
        RenderOptions.SetBitmapInterpolationMode(_header, _headerPixelArt && !_smoothPixels
            ? Avalonia.Media.Imaging.BitmapInterpolationMode.None
            : Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
        // the background art is ANSI drawn in the VGA font: pixel art, as the header
        RenderOptions.SetBitmapInterpolationMode(_surface, _smoothPixels
            ? Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality
            : Avalonia.Media.Imaging.BitmapInterpolationMode.None);
        _surface.InvalidateVisual();
    }

    /// <summary>A header Rainwire drew from ANSI art: 8x16 cells, so whole multiples of 8 by 16.</summary>
    private static bool IsVgaPng(string path) => Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase)
                                                 && Path.GetFileName(path).StartsWith("rw-", StringComparison.Ordinal);

    private void Show()
    {
        if (!_complete) return;
        bool was = IsUp;
        IsUp = true;
        GadgetMovesFlush();   // version 6: gadgets the caller moved in the tray while the menu was down
        TrayUpdate();
        _waiting = false;
        _dialog = null;
        _hideLater.Stop();
        IsVisible = true;
        if (!was)
        {
            _ping.Start();
            _anim.Start();
            _rainTick.Start();
            UpChanged?.Invoke();
        }
        _lastInput = DateTime.UtcNow;
        _shownAt = DateTime.UtcNow;
        Redraw();
        Dispatcher.UIThread.Post(() => { if (IsUp) Focus(); }, DispatcherPriority.Input);
        Reply("Shown");
    }

    /// <summary>Down: the keys go back at once; the picture stays ~120 ms (now: at once) while the terminal repaints.</summary>
    private void Hide(bool now = false)
    {
        if (!IsUp)
        {
            if (now)
            {
                _hideLater.Stop();
                IsVisible = false;
            }
            return;
        }
        IsUp = false;
        _waiting = false;
        _dialog = null;
        _gWide.Clear();   // the menu gives way to a screen or a door (News, Weather...): the gadgets come back at their normal width
        if (now) IsVisible = false;
        else _hideLater.Start();
        _ping.Stop();
        _anim.Stop();
        _rainTick.Stop();
        _gFocus = null;
        TrayUpdate();   // version 6: tray gadgets show on the status bar while no menu is up
        UpChanged?.Invoke();
        Hidden?.Invoke();
    }

    /// <summary>Sends s; anything but Shown also tells the board about the caller's activity, so it starts over.</summary>
    private void Reply(string s)
    {
        if (_trayQuiet) return;   // a tray gadget drawn while no menu is up: the board may be in a door
        if (_gTag != null)   // from a scene gadget (RainwireWidgets.cs): it says which, g=<name>, after the verb
        {
            int i = s.IndexOf(';');
            s = i < 0 ? s + ";g=" + _gTag : s[..i] + ";g=" + _gTag + s[i..];
        }
        if (s != "Shown")
            _active = false;
        Send?.Invoke(s);
    }

    // ================================================================
    // Input (MainWindow routes the keyboard here while the menu is up)
    // ================================================================

    private bool Selectable(int i) => i >= 0 && i < _items.Count && _items[i].Selectable && _items[i].Box.Width > 0;

    private void Pick(int index, int key)
    {
        if (_waiting || !_complete) return;
        _waiting = true;
        Reply(index >= 0 ? $"Pick;i={index}" : $"Pick;key={key}");
        Redraw();
    }

    private void Step(int d)
    {
        for (int i = _sel + d; i >= 0 && i < _items.Count; i += d)
            if (Selectable(i)) { _sel = i; return; }
    }

    /// <summary>The nearest item that way: Left/Right to the next column, Up/Down (a grid) to the next row of tiles.</summary>
    private void Move2D(int dc, int dr)
    {
        if (!Selectable(_sel)) { Step(1); return; }
        var cur = _items[_sel].Box;
        int best = -1;
        double bestD = double.MaxValue;
        for (int i = 0; i < _items.Count; i++)
        {
            if (!Selectable(i) || i == _sel) continue;
            var b = _items[i].Box;
            double dx = b.X + b.Width / 2 - (cur.X + cur.Width / 2), dy = b.Y + b.Height / 2 - (cur.Y + cur.Height / 2);
            if (dc != 0 && (dc > 0 ? dx <= 1 : dx >= -1)) continue;
            if (dr != 0 && (dr > 0 ? dy <= 1 : dy >= -1)) continue;
            double d = dc != 0 ? Math.Abs(dx) + 3 * Math.Abs(dy) : Math.Abs(dy) + 3 * Math.Abs(dx);
            if (d < bestD) { bestD = d; best = i; }
        }
        if (best >= 0) _sel = best;
    }

    /// <summary>A key while the menu is up; true when the menu used it (arrows, Enter, Esc...). Any other key is left
    /// alone so it becomes typed text (HandleText): the item's hotkey.</summary>
    public bool HandleKey(Key key, KeyModifiers mods = KeyModifiers.None)
    {
        if (!IsUp) return false;
        _active = true;
        _lastInput = DateTime.UtcNow;
        if (_dialog == null && !_isScene && _gFocus != null && GadgetKey(key, mods)) return true;   // version 6: a gadget has the keys
        if (_dialog != null)
        {
            // as the text dialog: any key closes it; letters, digits and Space come as text (HandleText)
            switch (key)
            {
                case Key.Return: DialogKey("Key;key=13"); return true;
                case Key.Escape: DialogKey("Key;key=27"); return true;
                case Key.Back: DialogKey("Key;key=8"); return true;
                case Key.Tab: DialogKey("Key;key=9"); return true;
                case Key.Up: case Key.Down: case Key.Left: case Key.Right: case Key.Home: case Key.End:
                case Key.PageUp: case Key.PageDown:
                    DialogKey("Key;key=13"); return true;
                default: return false;
            }
        }
        if (_isScene) return SceneKey(key, mods);   // RainwireScene.cs
        if (_isPic)   // every key goes to the screen (it moves tabs, ranges and pages itself); Esc is Back
        {
            bool alt = (mods & KeyModifiers.Alt) != 0, ctrl = (mods & KeyModifiers.Control) != 0;
            switch (key)
            {
                case Key.Escape: NewsBack(); break;
                case Key.Left: PicKey(VkLeft, alt, false, 0); break;
                case Key.Right: PicKey(VkRight, alt, false, 0); break;
                case Key.Up: PicKey(VkUp, alt, false, 0); break;
                case Key.Down: PicKey(VkDown, alt, false, 0); break;
                case Key.PageUp: PicKey(VkPgUp, false, false, 0); break;
                case Key.PageDown: PicKey(VkPgDn, false, false, 0); break;
                case Key.Home: PicKey(VkHome, false, false, 0); break;
                case Key.End: PicKey(VkEnd, false, false, 0); break;
                case Key.Return: PicKey(VkEnter, false, false, 0); break;
                case Key.Tab: PicKey(VkTab, false, false, 0); break;
                case Key.Delete: PicKey(VkDelete, false, false, 0); break;
                case Key.Back: PicKey(VkBackspace, false, false, 0); break;
                case Key.F1: PicKey(VkF1, false, false, 0); break;
                default:
                    if (ctrl && key >= Key.A && key <= Key.Z) { PicKey(VkChar, false, true, 'A' + (key - Key.A)); break; }
                    return false;   // typed characters come as text (HandleText)
            }
            return true;
        }
        if (_isForm)
        {
            bool ctrl = (mods & KeyModifiers.Control) != 0, shift = (mods & KeyModifiers.Shift) != 0;
            if (ctrl && key == Key.V || shift && key == Key.Insert) { Paste(); return true; }
            if (ctrl) return true;   // no other Ctrl keys in a field (and no text for them)
            if (!FormKey(key)) return false;   // typed characters come as text (HandleText)
            Redraw();
            return true;
        }
        if (_isPage)
        {
            double line = PageLH;
            switch (key)
            {
                case Key.Up: NewsScroll(-line); break;
                case Key.Down: NewsScroll(line); break;
                case Key.PageUp: NewsScroll(-_viewH * 0.9); break;
                case Key.PageDown: case Key.Tab: NewsScroll(_viewH * 0.9); break;
                case Key.Home: _scrollTo = 0; break;
                case Key.End: _scrollTo = _scrollMax; break;
                case Key.Return: case Key.Escape: NewsBack(); break;
                default: return false;   // Space and Q come as text (HandleText)
            }
            Redraw();
            return true;
        }
        if (_isList)
        {
            bool alt = (mods & KeyModifiers.Alt) != 0, ctrl = (mods & KeyModifiers.Control) != 0;
            switch (key)
            {
                case Key.Up: if (alt) ListKey(VkUp, true, false, 0); else ListStep(-1); break;
                case Key.Down: if (alt) ListKey(VkDown, true, false, 0); else ListStep(1); break;
                case Key.PageUp: ListPage(-1); break;
                case Key.PageDown: ListPage(1); break;
                case Key.Home: _lsel = -1; ListStep(1); break;
                case Key.End: _lsel = _rows.Count; ListStep(-1); break;
                case Key.Return: if (RowOk(_lsel) && !_waiting) { _waiting = true; Reply($"Pick;i={_lsel}"); } break;
                case Key.Escape: NewsBack(); break;
                case Key.Left: ListKey(VkLeft, alt, false, 0); break;
                case Key.Right: ListKey(VkRight, alt, false, 0); break;
                case Key.Tab: ListKey(VkTab, false, false, 0); break;
                case Key.Delete: ListKey(VkDelete, false, false, 0); break;
                case Key.Back: ListKey(VkBackspace, false, false, 0); break;
                case Key.F1: ListKey(VkF1, false, false, 0); break;
                default:
                    if (ctrl && key >= Key.A && key <= Key.Z)   // Ctrl+letter: no text comes for it
                    {
                        ListKey(VkChar, false, true, 'A' + (key - Key.A));
                        break;
                    }
                    return false;   // typed characters come as text (HandleText): the screen's action keys
            }
            Redraw();
            return true;
        }
        if (_isNews)
        {
            double line = (LineH(Ui, 13) + NewsLine * _u);
            switch (key)
            {
                case Key.Up: NewsScroll(-line); break;
                case Key.Down: NewsScroll(line); break;
                case Key.PageUp: NewsScroll(-_viewH * 0.9); break;
                case Key.PageDown: case Key.Tab: NewsScroll(_viewH * 0.9); break;
                case Key.Home: _scrollTo = 0; break;
                case Key.End: _scrollTo = _scrollMax; break;
                case Key.Return: case Key.Escape: NewsBack(); break;
                default: return false;   // Space and Q come as text (HandleText)
            }
            Redraw();
            return true;
        }
        bool grid = _head.Layout == LayoutGrid;
        switch (key)
        {
            case Key.Up: if (grid) Move2D(0, -1); else Step(-1); break;
            case Key.Down: if (grid) Move2D(0, 1); else Step(1); break;
            case Key.Tab: Step(1); break;
            case Key.Left: if (grid) Step(-1); else Move2D(-1, 0); break;
            case Key.Right: if (grid) Step(1); else Move2D(1, 0); break;
            case Key.Home: case Key.PageUp: _sel = -1; Step(1); break;
            case Key.End: case Key.PageDown: _sel = _items.Count; Step(-1); break;
            case Key.Return: if (Selectable(_sel)) Pick(_sel, 0); break;
            case Key.Escape:
                if (!_waiting && _complete) { _waiting = true; Reply("Back"); }
                break;
            default: return false;   // letters, digits and symbols come as text (HandleText)
        }
        Redraw();
        return true;
    }

    /// <summary>Typed text while the menu is up: each printable character picks its item (the board finds it).</summary>
    public void HandleText(string? text)
    {
        if (!IsUp || string.IsNullOrEmpty(text)) return;
        _active = true;
        _lastInput = DateTime.UtcNow;
        if (_dialog != null)
        {
            foreach (var rune in text.EnumerateRunes())
                if (rune.Value >= 0x20 && rune.Value != 0x7F)
                {
                    DialogKey($"Key;key={rune.Value}");
                    return;
                }
            return;
        }
        if (_isScene)
        {
            SceneText(text);
            return;
        }
        if (GadgetText(text)) return;   // version 6: a gadget has the keys
        if (_isPic)
        {
            foreach (var rune in text.EnumerateRunes())
                if (rune.Value >= 0x20 && rune.Value != 0x7F)
                {
                    PicKey(VkChar, false, false, rune.Value);
                    return;
                }
            return;
        }
        if (_isForm)
        {
            foreach (var rune in text.EnumerateRunes()) FormTyped(rune);
            Redraw();
            return;
        }
        if (_isList)   // any typed character goes to the screen (an action key)
        {
            foreach (var rune in text.EnumerateRunes())
                if (rune.Value >= 0x20 && rune.Value != 0x7F)
                {
                    ListKey(VkChar, false, false, rune.Value);
                    return;
                }
            return;
        }
        if (_isNews || _isPage)   // as the text screen: Space or Q go on
        {
            if (text is " " or "q" or "Q") NewsBack();
            return;
        }
        foreach (var rune in text.EnumerateRunes())
            if (rune.Value > 0x20 && rune.Value != 0x7F)
            {
                Pick(-1, rune.Value);
                return;
            }
    }

    private void NewsBack()
    {
        if (_waiting || !_complete) return;
        _waiting = true;
        Reply("Back");
    }

    /// <summary>The web address word t[p..word] starts with, as Rainwire's module finds it (menumod.c link_len):
    /// https://, http:// or www. (any case) and at least 3 more characters, less trailing punctuation; its length or 0.</summary>
    private static int LinkLen(string t, int p, int word)
    {
        int n = word - p;
        string w = t.Substring(p, n);
        int skip = w.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? 8
                 : w.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? 7
                 : w.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? 4 : 0;
        if (skip == 0) return 0;
        while (n > skip && ".,;:!?)]}'\"".Contains(w[n - 1])) n--;
        return n - skip >= 3 ? n : 0;
    }

    private string? LinkAt(Point p)
    {
        foreach (var (r, url) in _linkHits)
            if (r.Contains(p)) return url;
        return null;
    }

    /// <summary>A news link opens in the browser here (www. as http://), when it's a plain web address
    /// (TraceSessionManager.IsWebUrl: http/https, at most 2048 bytes); the XDG launcher first, xdg-open as the fallback,
    /// as the home screen opens links. Nothing goes to the board.</summary>
    private void OpenLink(string link)
    {
        string url = link.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "http://" + link : link;
        if (!Services.TraceSessionManager.IsWebUrl(url)) return;
        try
        {
            if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
            {
                _ = launcher.LaunchUriAsync(new Uri(url)).ContinueWith(t =>
                {
                    if (t.IsFaulted || !t.Result) XdgOpen(url);
                }, TaskScheduler.Default);
                return;
            }
        }
        catch (Exception ex) when (ex is UriFormatException or InvalidOperationException)
        {
            Debug.WriteLine($"RainwireMenu: link {url}: {ex.Message}");
        }
        XdgOpen(url);
    }

    private static void XdgOpen(string url)
    {
        try { Process.Start(new ProcessStartInfo("xdg-open", url) { UseShellExecute = false }); }
        catch (Exception ex) { Debug.WriteLine($"RainwireMenu: xdg-open {url}: {ex.Message}"); }
    }

    private void NewsScroll(double by) => _scrollTo = Math.Clamp(_scrollTo + by, 0, _scrollMax);

    // ---- forms: editing (menumod.c form_key, form_typed, form_click, form_enter, form_send) ----

    private static int Chars(string s) => s.EnumerateRunes().Count();

    private static int ChoiceIndex(FField f) => Array.IndexOf(f.Choices, f.Value);

    private static void ChoiceSet(FField f, int c)
    {
        if (c < 0 || c >= f.Choices.Length) return;
        f.Value = f.Choices[c];
        f.Cur = f.Value.Length;
    }

    private static int PrevStop(string s, int at) => at <= 0 ? 0 : at >= 2 && char.IsLowSurrogate(s[at - 1]) && char.IsHighSurrogate(s[at - 2]) ? at - 2 : at - 1;
    private static int NextStop(string s, int at) => at >= s.Length ? s.Length : char.IsHighSurrogate(s[at]) && at + 1 < s.Length ? at + 2 : at + 1;

    private void FormNext(int d)
    {
        if (_fields.Count == 0) return;
        _fact = (_fact + d + _fields.Count) % _fields.Count;
        _caretAt = DateTime.UtcNow;
    }

    /// <summary>Enter: the next field, or the form submitted on the last one (or a value matching its submitIf).</summary>
    private void FormEnter()
    {
        if (_fact < 0 || _fact >= _fields.Count) return;   // a key before every field came
        var f = _fields[_fact];
        string v = f.Value.Trim(' ');
        if (_fact == _fields.Count - 1 || f.SubmitIf.Length > 0 && string.Equals(v, f.SubmitIf, StringComparison.OrdinalIgnoreCase))
            FormSend(FormSubmit);
        else
            FormNext(1);
    }

    /// <summary>A key in the form; false for keys that come as typed text.</summary>
    private bool FormKey(Key key)
    {
        if (_fact < 0 || _fact >= _fields.Count) return key is Key.Escape or Key.Return;   // a key before every field came
        var f = _fields[_fact];
        _caretAt = DateTime.UtcNow;
        switch (key)
        {
            case Key.Left:
                if (f.Choice) { if (f.Choices.Length > 0) ChoiceSet(f, (ChoiceIndex(f) - 1 + f.Choices.Length) % f.Choices.Length); }
                else f.Cur = PrevStop(f.Value, f.Cur);
                return true;
            case Key.Right:
                if (f.Choice) { if (f.Choices.Length > 0) ChoiceSet(f, (ChoiceIndex(f) + 1) % f.Choices.Length); }
                else f.Cur = NextStop(f.Value, f.Cur);
                return true;
            case Key.Home: if (!f.Choice) f.Cur = 0; return true;
            case Key.End: if (!f.Choice) f.Cur = f.Value.Length; return true;
            case Key.Back:
                if (!f.Choice && f.Cur > 0)
                {
                    int at = PrevStop(f.Value, f.Cur);
                    f.Value = f.Value.Remove(at, f.Cur - at);
                    f.Cur = at;
                }
                return true;
            case Key.Delete:
                if (!f.Choice && f.Cur < f.Value.Length) f.Value = f.Value.Remove(f.Cur, NextStop(f.Value, f.Cur) - f.Cur);
                return true;
            case Key.Tab: case Key.Down: FormNext(1); return true;
            case Key.Up: FormNext(-1); return true;
            case Key.Return: FormEnter(); return true;
            case Key.Escape: FormSend(FormCancel); return true;
            default: return false;
        }
    }

    /// <summary>A typed character: inserted at the caret (to the field's max characters); in a choice field Space steps
    /// it and a letter picks the choice it starts.</summary>
    private void FormTyped(Rune r)
    {
        if (_fact < 0 || _fact >= _fields.Count) return;   // typed before every field came
        var f = _fields[_fact];
        _caretAt = DateTime.UtcNow;
        int c = r.Value;
        if (f.Choice)
        {
            if (f.Choices.Length == 0) return;
            if (c == ' ') { ChoiceSet(f, (ChoiceIndex(f) + 1) % f.Choices.Length); return; }
            for (int k = 0; k < f.Choices.Length; k++)
                if (f.Choices[k].Length > 0 && char.ToLowerInvariant(f.Choices[k][0]) == char.ToLowerInvariant((char)c)) ChoiceSet(f, k);
            return;
        }
        if (c < ' ' || c == 0x7F) return;
        if (f.Max > 0 && Chars(f.Value) >= f.Max) return;
        string u = r.ToString();
        if (Encoding.UTF8.GetByteCount(f.Value) + Encoding.UTF8.GetByteCount(u) > MaxValue) return;
        f.Value = f.Value.Insert(f.Cur, u);
        f.Cur += u.Length;
    }

    /// <summary>Ctrl+V or Shift+Insert: the clipboard's first line typed in at the caret.</summary>
    private async void Paste()
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is not { } cb) return;
            string? text = await cb.GetTextAsync();
            if (string.IsNullOrEmpty(text) || !_isForm || !IsUp) return;
            int nl = text.IndexOfAny(['\r', '\n']);
            if (nl >= 0) text = text[..nl];
            foreach (var rune in text.EnumerateRunes()) FormTyped(rune);
            Redraw();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"RainwireMenu: paste: {ex.Message}");
        }
    }

    /// <summary>A click: a choice's pill picks it; a field becomes active with the caret under the pointer.</summary>
    private void FormClick(Point p)
    {
        if (_waiting || !_complete) return;   // the fields still coming: the hits are the last form's
        foreach (var (r, fi, ci, tx) in _formHits)
        {
            if (!r.Contains(p) || fi >= _fields.Count) continue;
            _caretAt = DateTime.UtcNow;
            _fact = fi;
            var f = _fields[fi];
            if (ci >= 0) { ChoiceSet(f, ci); return; }
            if (f.Secret) { f.Cur = f.Value.Length; return; }
            double x = p.X - tx;
            int best = f.Value.Length;
            for (int at = 0; at <= f.Value.Length; at = NextStop(f.Value, at))
            {
                if (TextW(f.Value[..at], Ui, 13) >= x) { best = at; break; }
                if (at == f.Value.Length) break;
            }
            f.Cur = best;
            return;
        }
    }

    /// <summary>The form ends: every value back to the board (MU_C_FORM, the whole packet), submitted or canceled.</summary>
    private void FormSend(byte action)
    {
        if (_waiting || !_complete) return;
        var b = new List<byte> { CForm, 0, 0, 0, action, (byte)_fact, 0, 0 };
        int n = 0;
        foreach (var f in _fields)
        {
            byte[] v = Encoding.UTF8.GetBytes(f.Value);
            if (b.Count + 2 + v.Length > 4000 - 16) break;   // MU_MAX_PACKET
            b.Add((byte)(v.Length & 255));
            b.Add((byte)(v.Length >> 8));
            b.AddRange(v);
            n++;
        }
        b[6] = (byte)n;
        _waiting = true;
        Reply("FormDone;b64=" + Convert.ToBase64String(b.ToArray()));
    }

    /// <summary>A list's moves: to the nearest row that isn't a heading that way.</summary>
    private void ListStep(int d)
    {
        for (int i = _lsel + d; i >= 0 && i < _rows.Count; i += d)
            if (RowOk(i)) { _lsel = i; return; }
    }

    private void ListPage(int d)
    {
        double target = RowOk(_lsel) ? _rows[_lsel].Y + d * _viewH * 0.9 : 0;
        int best = _lsel;
        for (int i = 0; i < _rows.Count; i++)
        {
            if (!RowOk(i)) continue;
            if (d > 0 ? _rows[i].Y <= target : _rows[i].Y >= target) { best = i; if (d < 0) break; }
        }
        _lsel = best;
    }

    /// <summary>A picture screen's key: always i=0 (it has no rows).</summary>
    private void PicKey(int vk, bool alt, bool ctrl, int cp)
    {
        if (_waiting || !_complete) return;
        _waiting = true;
        Reply($"Key;i=0;vk={vk};key={cp}" + (alt ? ";alt=1" : "") + (ctrl ? ";ctrl=1" : ""));
    }

    /// <summary>A key the list doesn't use: the screen acts on it (Key), with the selected row.</summary>
    private void ListKey(int vk, bool alt, bool ctrl, int cp)
    {
        if (_waiting || !_complete) return;
        _waiting = true;
        Reply($"Key;i={Math.Max(_lsel, 0)};vk={vk};key={cp}" + (alt ? ";alt=1" : "") + (ctrl ? ";ctrl=1" : ""));
    }

    private int RowAt(Point p)
    {
        foreach (var (r, i) in _rowHits)
            if (r.Contains(p) && RowOk(i)) return i;
        return -1;
    }

    private int HitItem(Point p)
    {
        for (int i = 0; i < _items.Count; i++)
            if (Selectable(i) && _items[i].Box.Contains(p))
                return i;
        return -1;
    }

    private void OnMove(Point p)
    {
        _lastInput = DateTime.UtcNow;
        _mouse = p;
        if (SceneDrag6(p) || GadgetDragMove(p)) return;   // version 6: a scroll bar, divider, slider or gadget being dragged
        if (_isScene)
        {
            SceneMove(p);
            return;
        }
        if (WireMove(p)) return;   // over the news panel beside the header
        if (_isNews)
        {
            _surface.Cursor = LinkAt(p) != null ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
            return;   // the animation timer redraws the lit link
        }
        if (_isPic) return;   // the animation timer redraws the hover and the tooltip
        if (_isList)
        {
            if (_waiting || _dialog != null || !_st.Hover) return;
            int r = RowAt(p);
            if (r >= 0 && r != _lsel) { _lsel = r; _active = true; }
            return;   // the animation timer redraws the hover
        }
        if (_waiting || _dialog != null || !_st.Hover) return;   // hover off: the pointer doesn't select
        int i = HitItem(p);
        if (i >= 0 && i != _sel)
        {
            _sel = i;
            _active = true;
            Redraw();
        }
    }

    private void OnClick(Point p)
    {
        if (!IsUp) return;   // its picture lingering after Hide
        _active = true;
        _lastInput = DateTime.UtcNow;
        if (_dialog != null)
        {
            DialogKey("Key;click=1");
            return;
        }
        if (_isScene)
        {
            SceneClick(p);
            return;
        }
        if (WireClick(p)) return;   // a story in the news panel
        if (_isNews)
        {
            if (LinkAt(p) is { } url) OpenLink(url);   // a link opens here; the board isn't told
            else NewsBack();
            return;
        }
        if (_isPic)   // a tab
        {
            foreach (var (r, t) in _tabHits)
                if (r.Contains(p) && t != _tab && !_waiting)
                {
                    _waiting = true;
                    Reply($"Pick;i={t}");
                    break;
                }
            return;
        }
        if (_isForm)
        {
            FormClick(p);
            Redraw();
            return;
        }
        if (_isPage)
        {
            NewsBack();
            return;
        }
        if (_isList)   // a click selects a row; a click on the selected one opens it
        {
            int r = RowAt(p);
            if (r >= 0 && r == _lsel && !_waiting) { _waiting = true; Reply($"Pick;i={r}"); }
            else if (r >= 0) _lsel = r;
            Redraw();
            return;
        }
        int i = HitItem(p);
        if (i >= 0)
        {
            _sel = i;
            Pick(i, 0);
        }
    }

    /// <summary>The wheel (notches, up/left positive; a touchpad's in fractions). Version 6: a sideways wheel (or
    /// Shift with the wheel) scrolls a scene's horizontal scrolls first (WheelIn); menus take only the vertical one.</summary>
    private void OnWheel(Vector d, KeyModifiers mods)
    {
        if (!IsUp) return;
        _active = true;
        _lastInput = DateTime.UtcNow;
        bool sideways = d.Y == 0 && d.X != 0;
        double delta = sideways ? d.X : d.Y;
        _wheelShift = sideways || (mods & KeyModifiers.Shift) != 0;
        if (_dialog != null || delta == 0) return;
        if (_isScene)
        {
            SceneWheel(delta);
            return;
        }
        if (sideways)   // only a gadget's scrolls go sideways
        {
            GadgetWheel(delta);
            return;
        }
        if (WireWheel(delta)) return;   // over the news panel: its stories
        if (_isNews || _isPage) NewsScroll(-delta * 60 * _u);
        else if (_isList) ListStep(delta > 0 ? -1 : 1);
        else Step(delta > 0 ? -1 : 1);
        Redraw();
    }

    // ================================================================
    // Drawing (the module's layout, menumod.c, in DIPs: one unit is the module's logical pixel at this window size)
    // ================================================================

    private sealed class Surface : Control, ICustomHitTest
    {
        private readonly RainwireMenu _m;
        // A click is a press and a release both on the surface. The release alone was taken as one, so a module that
        // closes on its press (Files' Leave) brought the menu back under the button and the release picked whatever
        // was there (sysop 2026-10-22, on Windows: the news gadget opened the News door). Avalonia's implicit capture
        // sends a release to the element that got the press, but the flags make sure of it, as Windows has them.
        private bool _left, _right;
        public Surface(RainwireMenu m)
        {
            _m = m;
            PointerMoved += (_, e) =>
            {
                // version 5: a scene row's drag reads whether the left button is still held (RainwireSceneFiles.cs)
                _m._sceneLeftHeld = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed;
                _m.OnMove(e.GetPosition(this));
            };
            PointerPressed += (_, e) =>   // left unhandled, as before
            {
                var pp = e.GetCurrentPoint(this).Properties;
                _m._pointer = e.Pointer;   // version 6: captured for a bar, divider, slider or gadget drag
                switch (pp.PointerUpdateKind)   // version 6: the middle button, and the mouse's back and forward buttons
                {
                    case PointerUpdateKind.MiddleButtonPressed: _m.OnMiddle(e.GetPosition(this)); e.Handled = true; return;
                    case PointerUpdateKind.XButton1Pressed: _m.OnNav(false); e.Handled = true; return;
                    case PointerUpdateKind.XButton2Pressed: _m.OnNav(true); e.Handled = true; return;
                }
                if (pp.IsLeftButtonPressed)
                {
                    _left = true;
                    var p = e.GetPosition(this);
                    _m._sceneLeftHeld = true;
                    if (_m.Down6(p)) { _left = false; e.Handled = true; return; }   // version 6: a bar, divider, slider or gadget dragged
                    if (_m._isScene) _m.SceneButtonDown(p);   // a drag out may start (version 5)
                }
                if (pp.IsRightButtonPressed) _right = true;
            };
            PointerReleased += (_, e) =>
            {
                if (e.InitialPressMouseButton == MouseButton.Left)
                {
                    _m._clickMods = e.KeyModifiers;
                    if (_m._isScene && _m.SceneButtonUp()) _left = false;   // a row's drag ended: no click
                    else _m._sceneLeftHeld = false;
                    if (_m.Up6(e.GetPosition(this))) _left = false;           // version 6: a drag ended: no click
                    if (_left) _m.OnClick(e.GetPosition(this));
                    _left = false;
                    e.Handled = true;
                }
                else if (e.InitialPressMouseButton == MouseButton.Right)
                {
                    if (_right) _m.OnRightClick(e.GetPosition(this));   // a scene's context menu (RainwireSceneV2.cs)
                    _right = false;
                    e.Handled = true;
                }
            };
            PointerWheelChanged += (_, e) => { _m.OnWheel(e.Delta, e.KeyModifiers); e.Handled = true; };
            PointerExited += (_, _) => _left = _right = false;
            PropertyChanged += (_, e) =>
            {
                if (e.Property == IsVisibleProperty) _left = _right = false;
            };
        }
        public bool HitTest(Point point) => true;   // all of it takes the mouse, drawn or not
        public override void Render(DrawingContext dc) => _m.Draw(dc, Bounds.Width, Bounds.Height);
    }

    private sealed class Overlay : Control
    {
        private readonly RainwireMenu _m;
        public Overlay(RainwireMenu m) => _m = m;
        public override void Render(DrawingContext dc)
        {
            if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
            if (!_m.SceneBleeds)   // a whole-window scene has no header for them; a dialog still shows over it
            {
                _m.DrawWire(dc);      // the news panel, over the header picture (RainwireWire.cs)
                _m.DrawWeather(dc);   // and the weather widget under it (RainwireWidgets.cs)
                _m.DrawGadgets(dc);   // and the scene gadgets (RainwireWidgets.cs)
            }
            if (_m._dialog != null) _m.DrawDialog(dc, _m._dialog, Bounds.Width, Bounds.Height);
        }
    }

    // Inter (embedded, Program.cs WithInterFont): Segoe UI's stand-in, the UI face the app already ships
    private static readonly FontFamily UiFamily = new("fonts:Inter#Inter");
    private static readonly Typeface Ui = new(UiFamily, FontStyle.Normal, FontWeight.Normal);
    private static readonly Typeface UiBold = new(UiFamily, FontStyle.Normal, FontWeight.SemiBold);

    private const double Gap = 18, HdrGap = 26;
    private double StatusH => _st.Status ? 26 : 0;   // off: their room goes to the box
    private double HelpH => _st.Help ? 28 : 0;
    private double Pad => _st.Padding;               // inside the box
    private double KeyW => _st.Key == 4 ? 0 : 30;     // the key's column; none when keys aren't shown
    private double RowMax => _st.Height;             // a row's natural height

    /// <summary>The label font: rows.font and rows.size, at the exact size (the module has two sizes and takes the
    /// nearer; this draws any).</summary>
    private Typeface LabelFace => _st.Font == 1 ? Mono : UiBold;
    private double LabelSize => _st.Size;
    private static readonly Typeface Mono = new(new FontFamily("JetBrains Mono, DejaVu Sans Mono, monospace"), FontStyle.Normal, FontWeight.Bold);

    /// <summary>The room the title takes inside the box (box.title = inside), else 0 (DIPs).</summary>
    private double TitleInside => _st.Title == 1 && _head.Title.Length > 0 ? LineH(UiBold, 17) + 10 * _u : 0;

    private double _u = 1;        // DIPs per module logical pixel
    private double _rowH = 26;
    private Rect _box;

    private static Color C(uint rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    private Color Mcol(Mc i) => C(_head.Colors[(int)i]);
    private static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
    }
    private static int Luma(Color c) => (int)(c.R * 0.299 + c.G * 0.587 + c.B * 0.114);
    private static IBrush B(Color c) => new ImmutableSolidColorBrush(c);

    private FormattedText Text(string s, Typeface face, double size, Color c) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size * _u, B(c));

    /// <summary>Capital height over the em size, per face: measured once from an "H"'s outline (Avalonia 11's font
    /// metrics have no cap height). Inter's is about 0.73.</summary>
    private static readonly Dictionary<Typeface, double> s_caps = [];
    private static double CapsHeight(Typeface face)
    {
        if (s_caps.TryGetValue(face, out double caps))
            return caps;
        caps = 0.73;
        try
        {
            var ft = new FormattedText("H", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 100, Brushes.White);
            if (ft.BuildGeometry(default) is { } g && g.Bounds.Height > 0)
                caps = Math.Clamp((ft.Baseline - g.Bounds.Top) / 100, 0.4, 1);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"RainwireMenu: cap height: {ex.Message}");
        }
        s_caps[face] = caps;
        return caps;
    }

    /// <summary>Where to draw ft (face at size) so its capitals sit in the middle of a row: a line box has more room
    /// above the letters than below, so centering the box put the text low in the selection bar.</summary>
    private double MidY(FormattedText ft, Typeface face, double size, double top, double height) =>
        top + height / 2 + CapsHeight(face) * size * _u / 2 - ft.Baseline;

    private double TextW(string s, Typeface face, double size) => Text(s, face, size, Colors.White).WidthIncludingTrailingWhitespace;

    private double LabelW(Item it) => it.Spans.Sum(s => TextW(s.Text, LabelFace, LabelSize));

    private double TagW(Item it) => it.Tag.Length > 0 ? TextW(it.Tag, Ui, 13) + 12 * _u : 0;

    private static bool IsEmpty(Rect r) => r.Width <= 0 || r.Height <= 0;

    /// <summary>The terminal picture in this control's coordinates, or all of it.</summary>
    private Rect Picture(double w, double h) =>
        PictureRect?.Invoke() is Rect r && r.Width > 0 && r.Height > 0 ? r : new Rect(0, 0, w, h);

    /// <summary>The header's place: over the text screen's header rows (the picture's width, hdrRows/screenRows of
    /// its height), else full width in its own shape; at most 60% of the height.</summary>
    private Rect HeaderRect(double w, double h)
    {
        if (_headerPath == null || _headerW <= 0 || _st.Fit == 2) return default;   // header.fit = none: no picture
        var pic = Picture(w, h);
        double rows = _head.HdrRows > 0 && _head.ScreenRows > 0 ? pic.Height * _head.HdrRows / _head.ScreenRows : 0;
        double hw = pic.Width, hh = rows > 0 && _st.Fit == 0 ? rows : _headerH * hw / _headerW;
        if (_st.Fit == 1 && rows > 0 && hh > rows)   // fit: its own shape, no taller than its rows (narrower, centered)
        {
            hw *= rows / hh;
            hh = rows;
        }
        if (hh > h * 0.6)
        {
            hw *= h * 0.6 / hh;
            hh = h * 0.6;
        }
        return new Rect(pic.X + (pic.Width - hw) / 2, pic.Y, hw, hh);
    }

    private void LayoutMenu(double w, double h, double top)
    {
        foreach (var it in _items) it.Box = default;
        double bottom = h - (StatusH + HelpH + 6) * _u;
        double room = bottom - top - (2 * Pad + 8) * _u;
        var vis = _items.Where(i => (i.Flags & IfColumn) == 0).ToList();
        int breaks = _items.Count(i => (i.Flags & IfColumn) != 0);
        if (_head.Layout == LayoutGrid)
        {
            LayoutTiles(w, top, vis);
            PlaceBox(w, h, top);
            return;
        }
        var cols = new List<List<Item>> { new() };
        if (breaks > 0)
        {
            foreach (var it in _items)
            {
                if ((it.Flags & IfColumn) != 0) { if (cols[^1].Count > 0 && cols.Count < 8) cols.Add([]); continue; }
                cols[^1].Add(it);
            }
        }
        else
        {
            int want = _head.Layout == LayoutColumns ? (_head.Columns is >= 2 and <= 4 ? _head.Columns : 2) : 1;
            int fits = Math.Max(1, (int)(room / (22 * _u)));
            while (want < 8 && (vis.Count + want - 1) / want > fits) want++;
            int per = Math.Max(1, (vis.Count + want - 1) / want);
            cols = vis.Chunk(per).Select(c => c.ToList()).ToList();
            if (cols.Count == 0) cols.Add([]);
        }
        foreach (var c in cols)   // a blank separator at the top of a column takes no row
            while (c.Count > 0 && (c[0].Flags & IfSeparator) != 0 && c[0].Spans.Length == 0)
                c.RemoveAt(0);
        int rows = Math.Max(1, cols.Max(c => c.Count));
        // a row and the gap under it, at its natural height (shrunk when the menu must fit)
        double pitchMax = (RowMax + _st.Gap) * _u;   // rows.height 18 or 19 with a small gap: under the 20 floor
        double pitch = Math.Clamp(room / rows, Math.Min(20 * _u, pitchMax), pitchMax);
        _rowH = pitch > (_st.Gap + 14) * _u ? pitch - _st.Gap * _u : pitch;
        var cw = cols.Select(c => Math.Max(120 * _u, c.Count == 0 ? 0 : c.Max(it => (KeyW + 12 + 16) * _u + LabelW(it) + TagW(it)))).ToArray();
        double total = cw.Sum(), want2 = total + Gap * _u * (cols.Count - 1) + 2 * Pad * _u;
        if (_head.Width > 0)
        {
            double set = (_head.Width >= 80 ? w : w * _head.Width / 80.0) - 20 * _u;
            want2 = Math.Max(want2, set);
        }
        if (_st.Width > 0) want2 = Math.Max(want2, w * _st.Width / 100);   // the sysop's width, % of the window
        if (_head.Title.Length > 0 && _st.Title == 0)   // a title on the top border fits on it, 18 in from either corner
            want2 = Math.Max(want2, Text(_head.Title, UiBold, 17, Mcol(Mc.PanelTitle)).Width + 36 * _u);
        want2 = Math.Min(want2, w - 20 * _u);
        double extra = (want2 - 2 * Pad * _u - Gap * _u * (cols.Count - 1) - total) / cols.Count;
        double head = TitleInside;
        double bh = rows * (_rowH + _st.Gap * _u) - _st.Gap * _u + 2 * Pad * _u + head;
        double bx = _head.Position == PosLeft ? 10 * _u : _head.Position == PosRight ? w - want2 - 10 * _u : (w - want2) / 2;
        _box = new Rect(bx, top + 6 * _u, Math.Max(want2, 1), bh);
        double x = _box.X + Pad * _u;
        for (int c = 0; c < cols.Count; c++)
        {
            cw[c] += extra;
            for (int k = 0; k < cols[c].Count; k++)
                cols[c][k].Box = new Rect(x, _box.Y + Pad * _u + head + k * (_rowH + _st.Gap * _u), Math.Max(cw[c], 1), _rowH);
            x += cw[c] + Gap * _u;
        }
        PlaceBox(w, h, top);
    }

    private void LayoutTiles(double w, double top, List<Item> vis)
    {
        double tw = (_head.TileCols > 0 ? _head.TileCols : 18) * 9 * _u, th = (_head.TileRows > 0 ? _head.TileRows : 5) * 13 * _u;
        double gap = 10 * _u;
        int across = Math.Max(1, (int)((w - (2 * Pad + 40) * _u) / (tw + gap)));
        double y = 0;
        int c = 0, maxc = 1;
        var placed = new List<(Item it, Rect r)>();
        foreach (var it in vis)
        {
            if ((it.Flags & IfSeparator) != 0)
            {
                if (c > 0) { y += th + gap; c = 0; }
                if (it.Spans.Length > 0) { placed.Add((it, new Rect(0, y, 1, 24 * _u))); y += 28 * _u; }
                continue;
            }
            if (c >= across) { y += th + gap; c = 0; }
            placed.Add((it, new Rect(c * (tw + gap), y, tw, th)));
            maxc = Math.Max(maxc, ++c);
        }
        if (c > 0) y += th + gap;
        double bw = maxc * (tw + gap) - gap + 2 * Pad * _u;
        double bx = _head.Position == PosLeft ? 10 * _u : _head.Position == PosRight ? w - bw - 10 * _u : (w - bw) / 2;
        double head = TitleInside;
        _box = new Rect(bx, top + 6 * _u, bw, Math.Max(y - gap, 0) + 2 * Pad * _u + head);
        foreach (var (it, r) in placed)
            it.Box = new Rect(_box.X + Pad * _u + r.X, _box.Y + Pad * _u + head + r.Y,
                              (it.Flags & IfSeparator) != 0 ? bw - 2 * Pad * _u : r.Width, r.Height);
    }

    /// <summary>Where the box goes up and down (box.valign), as the module does (menumod.c place_box): auto, centered in
    /// the room between the header and the help line, never higher than it starts (low under a tall header, in the
    /// middle under a small logo); top; middle of the window, not over the header; bottom. Then the sysop's nudge
    /// (box.x, box.y), never off the window. Grid layouts too.</summary>
    private void PlaceBox(double w, double h, double top)
    {
        double bottom = h - (StatusH + HelpH + 4) * _u, y = _box.Y;
        switch (_st.VAlign)
        {
            case 1: y = top; break;
            case 2: y = Math.Max((h - StatusH * _u - _box.Height) / 2, top); break;
            case 3: y = Math.Max(bottom - _box.Height, top); break;
            default: y = Math.Max(y, top + (bottom - top - _box.Height) / 2); break;
        }
        y += h * _st.Y / 100;
        double x = _box.X + w * _st.X / 100;
        if (_st.X != 0 || _st.Y != 0 || _st.VAlign != 0)   // moved by the sysop: kept inside the window; a default box
        {                                                   // taller than its room stays where it always was
            x = Math.Clamp(x, 4 * _u, Math.Max(w - _box.Width - 4 * _u, 4 * _u));
            y = Math.Clamp(y, 4 * _u, Math.Max(h - StatusH * _u - _box.Height - 4 * _u, 4 * _u));
        }
        var d = new Vector(x - _box.X, y - _box.Y);
        if (d == default) return;
        _box = _box.Translate(d);
        foreach (var it in _items)
            if (!IsEmpty(it.Box)) it.Box = it.Box.Translate(d);
    }

    private void Draw(DrawingContext dc, double w, double h)
    {
        if (w <= 0 || h <= 0) return;
        _gArea = R(0, 0, w, Math.Max(h - StatusH * Math.Max(h / 620.0, 0.75), 0));   // version 6: floating gadgets' room
        // a module's screen as a scene (SF_BLEED) is the module's size: never under a device pixel to its logical pixel
        // (gfx.h set_display), so a window under 620 pixels tall lays out as the module does
        double dpi = VisualRoot?.RenderScaling ?? 1;
        _u = SceneBleeds ? Math.Max(h / 620.0, 1.0 / Math.Max(dpi, 0.5)) : Math.Max(h / 620.0, 0.75);
        dc.DrawRectangle(B(Mcol(Mc.Bg)), null, new Rect(0, 0, w, h));
        DrawBackdrop(dc, w, h);
        if ((_head.Flags & MfRain) != 0) DrawRain(dc, w, h);
        var hr = SceneBleeds ? default : WireLayout(HeaderRect(w, h));   // the news panel, if this menu has one, goes over the header's right
        PlaceHeader(hr);
        double top = (IsEmpty(hr) ? 0 : hr.Bottom) + HdrGap * _u;
        if (_complete && _isScene)
        {
            DrawScene(dc, w, h, IsEmpty(hr) ? 0 : hr.Bottom);
        }
        else if (_complete && _isNews)
        {
            DrawNews(dc, w, h, IsEmpty(hr) ? 0 : hr.Bottom);
        }
        else if (_complete && _isPic)
        {
            DrawPicture(dc, w, h, IsEmpty(hr) ? 0 : hr.Bottom);
        }
        else if (_complete && _isForm)
        {
            DrawForm(dc, w, h, IsEmpty(hr) ? 0 : hr.Bottom);
        }
        else if (_complete && _isPage)
        {
            DrawPage(dc, w, h, IsEmpty(hr) ? 0 : hr.Bottom);
        }
        else if (_complete && _isList)
        {
            DrawList(dc, w, h, IsEmpty(hr) ? 0 : hr.Bottom);
        }
        else if (_complete)
        {
            LayoutMenu(w, h, top);
            DrawBox(dc);
            for (int i = 0; i < _items.Count; i++) DrawItem(dc, i);
            double fade = FadeIn();
            if (fade < 1)
                dc.DrawRectangle(B(Color.FromArgb((byte)(255 * (1 - fade)), Mcol(Mc.MenuBg).R, Mcol(Mc.MenuBg).G, Mcol(Mc.MenuBg).B)),
                                 null, _box, _st.Radius * _u, _st.Radius * _u);
            if (_st.Help) DrawHelp(dc, w, h);
        }
        if (_st.Status && !SceneBleeds) DrawStatus(dc, w, h);
    }

    private Rect _headerAt;

    /// <summary>Puts the header image where Draw worked it out. Layout can't change during a render pass, so a move is
    /// posted for straight after it.</summary>
    private void PlaceHeader(Rect hr)
    {
        if (hr == _headerAt) return;
        _headerAt = hr;
        Dispatcher.UIThread.Post(() =>
        {
            var at = _headerAt;
            if (IsEmpty(at))
            {
                _header.IsVisible = false;
                return;
            }
            _header.Width = at.Width;
            _header.Height = at.Height;
            _header.Margin = new Thickness(at.X, at.Y, 0, 0);
            _header.IsVisible = true;
        }, DispatcherPriority.Render);
    }

    private void Redraw()
    {
        _surface.InvalidateVisual();
        _overlay.InvalidateVisual();
    }

    /// <summary>The dialog over the dimmed menu, centered: the title on its top border, the lines, the foot at the
    /// bottom right (render's dialog, look.go, in the module's units).</summary>
    private void DrawDialog(DrawingContext dc, Dialog d, double w, double h)
    {
        dc.DrawRectangle(B(Color.FromArgb(110, 0, 0, 0)), null, new Rect(0, 0, w, h));
        const double size = 16;
        double lineH = 24 * _u, padX = 28 * _u, padY = 22 * _u;
        double tw = d.Lines.Length == 0 ? 0 : d.Lines.Max(l => l.Sum(s => TextW(s.Text, Ui, size)));
        var foot = Text(d.Foot, Ui, 13, C(d.FootFg));
        var title = Text(d.Title, UiBold, 17, Mcol(Mc.PanelTitle));
        double bw = Math.Max(Math.Min(Math.Max(Math.Max(tw, foot.Width), title.Width + 40 * _u) + 2 * padX, w - 24 * _u), 1);
        double bh = Math.Max(Math.Min(d.Lines.Length * lineH + 2 * padY + foot.Height + 8 * _u, h - 24 * _u), 1);
        var box = new Rect((w - bw) / 2, (h - bh) / 2, bw, bh);
        double r = _st.Radius * _u;   // the sysop's box.radius, border and opacity (and dim, the layer over it all)
        Panel(dc, box, C(d.Bg), C(d.Border), r);
        if (d.Title.Length > 0)
        {
            double tx = box.X + 18 * _u;
            if (_st.Border > 0)
                dc.DrawRectangle(B(WithA(C(d.Bg), BoxA)), null, new Rect(tx - 8 * _u, box.Y - 2 * _u - _st.Border * _u / 2, title.Width + 16 * _u, (4 + _st.Border) * _u));
            dc.DrawText(title, new Point(tx, box.Y - title.Height / 2));
        }
        using (dc.PushClip(new Rect(box.X + padX, box.Y + padY, Math.Max(box.Width - 2 * padX, 0), Math.Max(box.Height - 2 * padY, 0))))
        {
            double y = box.Y + padY;
            foreach (var line in d.Lines)
            {
                double x = box.X + padX;
                foreach (var s in line)
                {
                    double sw = TextW(s.Text, Ui, size);
                    if (s.Bg != None) dc.DrawRectangle(B(C(s.Bg)), null, new Rect(x, y + 2 * _u, sw, lineH - 4 * _u));
                    var ft = Text(s.Text, Ui, size, s.Fg != None ? C(s.Fg) : C(d.Fg));
                    dc.DrawText(ft, new Point(x, y + (lineH - ft.Height) / 2));
                    x += sw;
                }
                y += lineH;
            }
        }
        if (!_dialogKeyed)
            dc.DrawText(foot, new Point(box.Right - padX - foot.Width, box.Bottom - padY / 2 - foot.Height));
    }

    // ---- the rain (Rainwire render/look.go: the "rain" background, on the text screen's grid) ----

    private sealed class Drop { public int X, Y, N; public bool Slow; }
    private readonly List<Drop> _drops = [];
    private int _dropRows, _rainTicks;
    private DateTime _lastInput;
    private const int RainCols = 80;
    private static readonly TimeSpan RainFor = TimeSpan.FromSeconds(30);   // as the text screen's animation

    private static uint Hash(int a, int b)
    {
        uint v = (uint)a * 2654435761 ^ (uint)b * 40503;
        v ^= v >> 13;
        v *= 0x5bd1e995;
        return v ^ v >> 15;
    }

    private int RainRows => _head.ScreenRows > 0 ? _head.ScreenRows : 25;

    /// <summary>The streaks, placed as the text screen places them (a column in six, its start and length from it).</summary>
    private void RainSeed()
    {
        _drops.Clear();
        int h = _dropRows = RainRows;
        for (int x = 2; x < RainCols - 2; x++)
        {
            uint hx = Hash(x, 7);
            if (hx % 6 != 0) continue;
            _drops.Add(new Drop { X = x, Y = 1 + (int)(hx / 6 % (uint)Math.Max(h - 4, 1)), N = 2 + (int)(hx / 37 % 4), Slow = (hx & 0x100) != 0 });
        }
    }

    /// <summary>One row down every 150 ms (slow streaks every other tick); still after 30 s without a key or the mouse.</summary>
    private void RainStep()
    {
        if (!IsUp || (_head.Flags & MfRain) == 0 || DateTime.UtcNow - _lastInput > RainFor) return;
        if (_dropRows != RainRows || _drops.Count == 0) RainSeed();
        int h = RainRows;
        _rainTicks++;
        foreach (var d in _drops)
        {
            if (d.Slow && _rainTicks % 2 == 0) continue;
            if (++d.Y >= h - 1)
            {
                uint r = Hash(d.X, _rainTicks);
                d.N = 2 + (int)(r % 4);
                d.Y = 1 - d.N - (int)(r / 4 % (uint)Math.Max(h / 2, 1));
            }
        }
        _surface.InvalidateVisual();
    }

    /// <summary>Each streak: its tail faint, its body brighter, its head (dashed, as the text's ╎) brightest; the
    /// header and the box draw over it.</summary>
    private void DrawRain(DrawingContext dc, double w, double h)
    {
        if (_dropRows != RainRows || _drops.Count == 0) RainSeed();
        var pic = Picture(w, h);
        int rows = RainRows;
        double cw = pic.Width / RainCols, ch = pic.Height / rows, lw = Math.Max(1, cw * 0.14);
        IBrush head = B(Mcol(Mc.RainHead)), body = B(Mcol(Mc.RainBody)), tail = B(Mcol(Mc.RainTail));
        foreach (var d in _drops)
            for (int i = 0; i < d.N; i++)
            {
                int y = d.Y + i;
                if (y < 1 || y >= rows - 1) continue;
                double x = pic.X + (d.X + 0.5) * cw - lw / 2, top = pic.Y + y * ch;
                if (i == d.N - 1)
                {
                    dc.DrawRectangle(head, null, new Rect(x, top + ch * 0.08, lw, ch * 0.34));
                    dc.DrawRectangle(head, null, new Rect(x, top + ch * 0.58, lw, ch * 0.34));
                }
                else
                {
                    dc.DrawRectangle(i == 0 ? tail : body, null, new Rect(x, top, lw, ch));
                }
            }
    }

    /// <summary>The box's (and the news panel's) background alpha: box.opacity.</summary>
    private byte BoxA => (byte)(_st.Opacity * 255 / 100);

    /// <summary>A panel in the sysop's box style (box.radius, border, opacity, shadow), as the module's panel(): the
    /// menu's box and the news panel (r: its radius at the default, scaled with box.radius).</summary>
    private void Panel(DrawingContext dc, Rect box, Color bg, Color border, double r)
    {
        if ((_head.Flags & MfShadow) != 0)
        {
            var sh = Mcol(Mc.PanelShadow);
            bool hard = _st.Shadow == 1;
            double off = (hard ? 4 : 6) * _u;
            byte a = hard ? (byte)220 : (byte)(90 * BoxA / 255);
            dc.DrawRectangle(B(Color.FromArgb(a, sh.R, sh.G, sh.B)), null, box.Translate(new Vector(off, off)), r, r);
        }
        dc.DrawRectangle(B(WithA(bg, BoxA)), _st.Border > 0 ? new Pen(B(border), _st.Border * _u) : null, box, r, r);
    }

    private void DrawBox(DrawingContext dc)
    {
        double r = _st.Radius * _u, bw = _st.Border * _u;
        Panel(dc, _box, Mcol(Mc.MenuBg), Mcol(Mc.MenuBorder), r);
        if (_head.Title.Length > 0 && _st.Title == 0)   // the title on the top border, the border cut under it
        {
            var t = Text(_head.Title, UiBold, 17, Mcol(Mc.PanelTitle));
            double tx = _box.X + 18 * _u;
            if (bw > 0)
                dc.DrawRectangle(B(WithA(Mcol(Mc.MenuBg), BoxA)), null, new Rect(tx - 8 * _u, _box.Y - 2 * _u - bw / 2, t.Width + 16 * _u, 4 * _u + bw));
            dc.DrawText(t, new Point(tx, _box.Y - t.Height / 2));
        }
        else if (_head.Title.Length > 0 && _st.Title == 1)   // inside, as a heading over the items
        {
            var t = Text(_head.Title, UiBold, 17, Mcol(Mc.PanelTitle));
            dc.DrawText(t, new Point(_box.X + (Pad + 4) * _u, _box.Y + (Pad - 2) * _u));
        }
        if ((_head.Flags & MfRoot) == 0)
        {
            var t = Text("Esc Back", Ui, 13, Mcol(Mc.Dim));
            double tx = _box.Right - t.Width - 18 * _u;
            if (bw > 0)
                dc.DrawRectangle(B(WithA(Mcol(Mc.MenuBg), BoxA)), null, new Rect(tx - 6 * _u, _box.Bottom - 2 * _u - bw / 2, t.Width + 12 * _u, 4 * _u + bw));
            dc.DrawText(t, new Point(tx, _box.Bottom - t.Height / 2));
        }
    }

    /// <summary>The box's fade-in, 0..1 (1: done): box fade ms (0: none).</summary>
    private double FadeIn() => _st.Fade <= 0 ? 1 : Math.Clamp((DateTime.UtcNow - _shownAt).TotalMilliseconds / _st.Fade, 0, 1);

    private static double Now => Environment.TickCount64;

    /// <summary>A label in its spans' colors from x, cut at maxW. On the selection a span keeps its own color only when
    /// it reads there.</summary>
    private void DrawLabel(DrawingContext dc, Item it, double x, Rect row, double maxW, bool sel, Color selBg)
    {
        Color baseC = sel ? Mcol(Mc.SelFg) : (it.Flags & IfSeparator) != 0 ? Mcol(Mc.MenuTag) : Mcol(Mc.MenuLabel);
        Color under = sel ? selBg : Mcol(Mc.MenuBg);
        using var clip = dc.PushClip(new Rect(x, row.Y, Math.Max(maxW, 0), row.Height));
        double x0 = x;
        foreach (var s in it.Spans)
        {
            if (x >= x0 + maxW) break;
            Color fg = baseC;
            if (s.Fg != None)
            {
                fg = C(s.Fg);
                if (sel && Math.Abs(Luma(fg) - Luma(selBg)) < 60) fg = baseC;
            }
            double sw = TextW(s.Text, LabelFace, LabelSize);
            if (!sel && s.Bg != None && !(s.Fx == FxBlink && (long)(Now / 500) % 2 == 1))
                dc.DrawRectangle(B(C(s.Bg)), null, new Rect(x, row.Y + 3 * _u, sw, row.Height - 6 * _u));
            if (s.Fx == FxPulse)
            {
                double t = 0.5 + 0.5 * Math.Sin(Now / 1000.0 * 3.6);
                fg = Mix(Mix(fg, under, 0.5), fg, t);
            }
            if (s.Fx == FxShimmer)
            {
                double pos = (Now / 1400.0 % 1.8) * (sw + 60 * _u) - 30 * _u, cx = x;
                foreach (var rune in s.Text.EnumerateRunes())
                {
                    string ch = rune.ToString();
                    double cw = TextW(ch, LabelFace, LabelSize);
                    double glint = 1 - Math.Clamp(Math.Abs(cx + cw / 2 - x - pos) / (24 * _u), 0, 1);
                    var ft = Text(ch, LabelFace, LabelSize, Mix(fg, Colors.White, glint * 0.85));
                    dc.DrawText(ft, new Point(cx, MidY(ft, LabelFace, LabelSize, row.Y, row.Height)));
                    cx += cw;
                }
            }
            else
            {
                var ft = Text(s.Text, LabelFace, LabelSize, fg);
                dc.DrawText(ft, new Point(x, MidY(ft, LabelFace, LabelSize, row.Y, row.Height)));
            }
            x += sw;
        }
    }

    private void DrawItem(DrawingContext dc, int i)
    {
        var it = _items[i];
        if (IsEmpty(it.Box)) return;
        var r = it.Box;
        if ((it.Flags & IfSeparator) != 0)
        {
            if (it.Spans.Length > 0) DrawLabel(dc, it, r.X + 4 * _u, r, r.Width - 8 * _u, false, default);
            return;
        }
        bool sel = i == _sel, grid = _head.Layout == LayoutGrid;
        Color selBg = Mcol(Mc.SelBg);
        var bar = new Rect(r.X, r.Y + 1 * _u, r.Width, Math.Max(r.Height - 2 * _u, 1));
        double sr = _st.SelRadius * _u;
        if (sel && _st.Sel == 1)   // select.shape outline: a stroke in sel.bg, the text on the box
        {
            byte a = _waiting ? (byte)170 : (byte)255;
            dc.DrawRectangle(null, new Pen(B(WithA(Mcol(Mc.SelBg), a)), 1.5 * _u), bar, sr, sr);
            selBg = Mcol(Mc.MenuBg);
            if (_st.Marker) dc.DrawRectangle(B(Mcol(Mc.SelMarker)), null, new Rect(bar.X, bar.Y, 3 * _u, bar.Height));
        }
        else if (sel && _st.Sel == 2)   // glow: a soft halo and a lighter bar
        {
            byte a = _waiting ? (byte)170 : (byte)255;
            var halo = new Rect(r.X - 3 * _u, r.Y - 2 * _u, r.Width + 6 * _u, r.Height + 4 * _u);
            dc.DrawRectangle(B(WithA(Mcol(Mc.SelBg), (byte)(55 * a / 255))), null, halo, sr + 3 * _u, sr + 3 * _u);
            dc.DrawRectangle(B(WithA(Mcol(Mc.SelBg), (byte)(150 * a / 255))), null, bar, sr, sr);
            selBg = Mix(Mcol(Mc.MenuBg), Mcol(Mc.SelBg), 150 / 255.0);
            if (_st.Marker) dc.DrawRectangle(B(Mcol(Mc.SelMarker)), null, new Rect(bar.X, bar.Y, 3 * _u, bar.Height));
        }
        else if (sel && _st.Sel == 3)   // underline: a 2 px line under the row, no marker
        {
            byte a = _waiting ? (byte)170 : (byte)255;
            dc.DrawRectangle(B(WithA(Mcol(Mc.SelBg), a)), null, new Rect(r.X + 8 * _u, r.Bottom - 3 * _u, Math.Max(r.Width - 16 * _u, 1), 2 * _u));
            selBg = Mcol(Mc.MenuBg);
        }
        else if (sel)
        {
            byte a = _waiting ? (byte)170 : (byte)255;
            if ((_head.Flags & MfGradient) != 0)
            {
                var g = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(WithA(Mcol(Mc.SelBg), a), 0), new GradientStop(WithA(Mcol(Mc.SelBg2), a), 1) },
                };
                dc.DrawRectangle(g, null, bar);
                selBg = Mix(Mcol(Mc.SelBg), Mcol(Mc.SelBg2), 0.5);
            }
            else
            {
                dc.DrawRectangle(B(WithA(Mcol(Mc.SelBg), a)), null, bar, sr, sr);
            }
            if (_st.Marker) dc.DrawRectangle(B(Mcol(Mc.SelMarker)), null, new Rect(bar.X, bar.Y, 3 * _u, bar.Height));   // the bar's height
        }
        else if (grid)
        {
            dc.DrawRectangle(null, new Pen(B(WithA(Mcol(Mc.MenuBorder), 200)), 1 * _u), r, 5 * _u, 5 * _u);
        }
        string key = it.Key > 0 && it.Key <= 0x10FFFF && (it.Key < 0xD800 || it.Key > 0xDFFF) ? char.ConvertFromUtf32(it.Key) : "";
        Color kfg = sel ? (it.KeyFg != None ? Colors.White : Mcol(Mc.SelKey)) : it.KeyFg != None ? C(it.KeyFg) : Mcol(Mc.MenuKey);
        bool brackets = (_head.Flags & MfBrackets) != 0;
        if (grid)
        {
            if (_st.Key == 3)   // a chip: the key in a pill of its color
            {
                var kt = Text(key, UiBold, 13, Luma(kfg) > 140 ? Color.FromRgb(0x10, 0x10, 0x10) : Colors.White);
                double cw = kt.Width + 14 * _u, ch = kt.Height + 4 * _u;
                dc.DrawRectangle(B(kfg), null, new Rect(r.X + (r.Width - cw) / 2, r.Y + 4 * _u, cw, ch), ch / 2, ch / 2);
                dc.DrawText(kt, new Point(r.X + (r.Width - kt.Width) / 2, r.Y + 6 * _u));
            }
            else if (_st.Key != 4)
            {
                var kt = Text(brackets ? $"[{key}]" : key, UiBold, 13, kfg);
                dc.DrawText(kt, new Point(r.X + (r.Width - kt.Width) / 2, r.Y + 6 * _u));
            }
            double lw = LabelW(it), room = r.Width - 12 * _u;
            DrawLabel(dc, it, r.X + (r.Width - Math.Min(lw, room)) / 2, new Rect(r.X, r.Y + r.Height / 2 - 12 * _u, r.Width, 26 * _u),
                      room, sel, selBg);
            if (it.Tag.Length > 0)
            {
                var tt = Text(it.Tag, Ui, 11, sel ? Mcol(Mc.SelFg) : Mcol(Mc.MenuTag));
                dc.DrawText(tt, new Point(r.X + (r.Width - tt.Width) / 2, r.Bottom - 18 * _u));
            }
        }
        else
        {
            double kx = r.X + 10 * _u;
            var parts = brackets ? new[] { ("[", sel ? Mcol(Mc.SelFg) : Mcol(Mc.MenuBracket)), (key, kfg), ("]", sel ? Mcol(Mc.SelFg) : Mcol(Mc.MenuBracket)) }
                                 : new[] { (key, kfg) };
            double kw = parts.Sum(p => TextW(p.Item1, LabelFace, LabelSize));
            if (!sel && it.KeyBg != None && _st.Key < 3)
                dc.DrawRectangle(B(C(it.KeyBg)), null, new Rect(kx - 2 * _u, r.Y + 3 * _u, kw + 4 * _u, r.Height - 6 * _u));
            if (_st.Key == 3)   // a chip: the key in a pill of its color, its text dark or white by contrast
            {
                var ft = Text(key, LabelFace, LabelSize, Luma(kfg) > 140 ? Color.FromRgb(0x10, 0x10, 0x10) : Colors.White);
                double cw = ft.WidthIncludingTrailingWhitespace + 12 * _u, ch = Math.Max(r.Height - 8 * _u, 1);
                dc.DrawRectangle(B(kfg), null, new Rect(kx - 4 * _u, r.Y + 4 * _u, cw, ch), ch / 2, ch / 2);
                dc.DrawText(ft, new Point(kx + 2 * _u, MidY(ft, LabelFace, LabelSize, r.Y, r.Height)));
            }
            else if (_st.Key != 4)   // none: no key shown (it still works)
            {
                foreach (var (s, c) in parts)
                {
                    var ft = Text(s, LabelFace, LabelSize, c);
                    dc.DrawText(ft, new Point(kx, MidY(ft, LabelFace, LabelSize, r.Y, r.Height)));
                    kx += ft.WidthIncludingTrailingWhitespace;
                }
            }
            double lx = r.X + (KeyW + 12 + (brackets && _st.Key < 3 ? 10 : 0)) * _u, tw = TagW(it);
            DrawLabel(dc, it, lx, r, r.Width - (lx - r.X) - tw - 6 * _u, sel, selBg);
            if (tw > 0)
            {
                var tt = Text(it.Tag, Ui, 13, sel ? Mcol(Mc.SelFg) : Mcol(Mc.MenuTag));
                dc.DrawText(tt, new Point(r.Right - 8 * _u - tt.Width, MidY(tt, Ui, 13, r.Y, r.Height)));
            }
        }
    }

    private static Color WithA(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    // ================================================================
    // The news screen (Rainwire modules/menu/src/menumod.c draw_news, news_body, flow: the reference)
    // ================================================================

    private const double NewsLine = 6, StatH = 24;   // added to a face's line height; a row of the stats bar

    /// <summary>A run of laid-out news text (or a date, or the Earlier label) in content coordinates; Ft cached for
    /// a plain one (pulse and shimmer change every frame).</summary>
    private sealed class Seg
    {
        public double X, Y, W, LineH;
        public string Text = "";
        public Typeface Face;
        public double Size;
        public uint Fg, Bg = None;
        public byte Fx;
        public int LinkLen;      // the run starts with a web address this long (chars); 0: none
        public string? Url;
        public FormattedText? Ft;
    }

    private readonly List<Seg> _segs = [];
    private double _vTop, _vBot;   // the panel's inside, on screen: a link half out of it isn't clickable
    private readonly List<(double X, double Y, double W)> _rules = [];
    private double _contentH;
    private (double W, double U, int N) _layoutKey;   // what _segs were laid out for

    private Color Ncol(Nc i) => C(_nc[(int)i]);

    private double LineH(Typeface face, double size) => Text("Hg", face, size, Colors.White).Height;

    /// <summary>Spans laid out from (x, y), wrapped at words to w ('\n' breaks the line); y ends under the last line.
    /// A span keeps its own color (none: def) and background.</summary>
    private void Flow(Typeface face, double size, Span[] spans, double x, double w, ref double y, uint def)
    {
        if (spans.Length == 0) return;
        double lh = LineH(face, size) + NewsLine * _u, cx = x;
        foreach (var s in spans)
        {
            uint fg = s.Fg != None ? s.Fg & 0xFFFFFF : def;
            string t = s.Text;
            int p = 0;
            while (p < t.Length)
            {
                if (t[p] == '\n') { cx = x; y += lh; p++; continue; }
                int q = p;   // a word and the spaces after it
                while (q < t.Length && t[q] != ' ' && t[q] != '\n') q++;
                int word = q;
                while (q < t.Length && t[q] == ' ') q++;
                double ww = TextW(t[p..word], face, size);
                if (cx > x && cx + ww > x + w)
                {
                    cx = x;
                    y += lh;
                    while (p < q && t[p] == ' ') p++;   // no spaces at a line's start
                }
                string run = t[p..q];
                double wall = TextW(run, face, size);
                int ln = _newsLinks ? LinkLen(t, p, word) : 0;
                if (run.Length > 0)
                    _segs.Add(new Seg { X = cx, Y = y, W = wall, LineH = lh, Text = run, Face = face, Size = size, Fg = fg,
                                        Bg = s.Bg, Fx = s.Fx, LinkLen = ln, Url = ln > 0 ? t[p..(p + ln)] : null });
                cx += wall;
                p = q;
            }
        }
        y += lh;
    }

    /// <summary>A line of one color at (x, y) in a news line's box.</summary>
    private void Put(string text, Typeface face, double size, double x, double y, uint fg) =>
        _segs.Add(new Seg { X = x, Y = y, W = TextW(text, face, size), LineH = LineH(face, size), Text = text, Face = face,
                            Size = size, Fg = fg });

    /// <summary>The news laid out in content coordinates, w across: the newest first and larger, then the rule with
    /// "Earlier" and the older ones beside a date column.</summary>
    private void LayoutNews(double w)
    {
        _segs.Clear();
        _rules.Clear();
        double y = 0, boldH = LineH(UiBold, 13), dateW = 0;
        for (int i = 1; i < _news.Count; i++)
            if (_news[i].Date.Length > 0) dateW = Math.Max(dateW, TextW(_news[i].Date, UiBold, 13) + 18 * _u);
        if (dateW > w * 0.3) dateW = 0;   // too narrow: dates go over their titles
        for (int i = 0; i < _news.Count; i++)
        {
            var it = _news[i];
            if (i == 0)
            {
                if (it.Date.Length > 0)
                {
                    Put(it.Date, UiBold, 13, 0, y, _nc[(int)Nc.LatestDate]);
                    y += boldH + 4 * _u;
                }
                Flow(UiBold, 17, it.Title, 0, w, ref y, _nc[(int)Nc.LatestHead]);
                y += 2 * _u;
                Flow(Ui, 13, it.Text, 0, w, ref y, _nc[(int)Nc.LatestText]);
                continue;
            }
            if (i == 1)   // the rule over the older ones, "Earlier" on it
            {
                y += 14 * _u;
                double tw = _earlier.Length > 0 ? TextW(_earlier, UiBold, 13) + 16 * _u : 0;
                if (tw > 0) Put(_earlier, UiBold, 13, 0, y, _nc[(int)Nc.Title]);
                _rules.Add((tw, y + boldH / 2, w - tw));
                y += boldH + 12 * _u;
            }
            else
            {
                y += 12 * _u;
            }
            if (it.Date.Length > 0)
            {
                Put(it.Date, UiBold, 13, 0, y + NewsLine * _u / 2 + 1 * _u, _nc[(int)Nc.Date]);
                if (dateW == 0) y += boldH + 2 * _u;
            }
            Flow(UiBold, 13, it.Title, dateW, w - dateW, ref y, _nc[(int)Nc.Head]);
            Flow(Ui, 13, it.Text, dateW, w - dateW, ref y, _nc[(int)Nc.Text]);
        }
        _contentH = y;
    }

    private void DrawNews(DrawingContext dc, double W, double H, double top)
    {
        double statsH = _stats.Length > 0 ? (_stats.Length * StatH + 10) * _u : 0;
        double x = 14 * _u, w = W - 28 * _u, y = top + 14 * _u;
        double bottom = H - (StatusH + 8) * _u - statsH - (statsH > 0 ? 8 * _u : 0);
        if (bottom - y < 80 * _u) y = Math.Max(bottom - 80 * _u, 14 * _u);   // a tall header: the panel still gets room
        double h = Math.Max(bottom - y, 1), r = _st.Radius * 8 / 6 * _u;   // 8 at the default radius, as before
        var panel = new Rect(x, y, Math.Max(w, 1), h);
        Color bg = Ncol(Nc.PanelBg);
        Panel(dc, panel, bg, Ncol(Nc.Border), r);

        // the inside, scrolled smoothly; a thin bar on the right when it doesn't all fit
        double ix = x + 24 * _u, iw = Math.Max(w - 58 * _u, 1), iy = y + 22 * _u, ih = Math.Max(h - 40 * _u, 1);
        _viewH = ih;
        _vTop = iy;
        _vBot = iy + ih;
        var key = (iw, _u, _news.Count);
        if (key != _layoutKey)
        {
            _layoutKey = key;
            LayoutNews(iw);
        }
        _scrollMax = Math.Max(_contentH - ih, 0);
        _scrollTo = Math.Clamp(_scrollTo, 0, _scrollMax);
        var now = DateTime.UtcNow;
        double dt = Math.Clamp((now - _scrollAt).TotalMilliseconds, 0, 200);
        _scrollAt = now;
        _scroll += (_scrollTo - _scroll) * (1 - Math.Pow(0.65, dt / 20));   // the module's 35% a 20 ms frame
        if (Math.Abs(_scrollTo - _scroll) < 0.5) _scroll = _scrollTo;
        using (dc.PushClip(new Rect(x + 2 * _u, iy - 6 * _u, Math.Max(w - 4 * _u, 0), ih + 12 * _u)))
        {
            double oy = iy - _scroll;
            foreach (var (rx, ry, rw) in _rules)
                dc.DrawRectangle(B(WithA(Ncol(Nc.Rule), 200)), null, new Rect(ix + rx, oy + ry, Math.Max(rw, 0), Math.Max(1 * _u, 1)));
            _linkHits.Clear();
            foreach (var sg in _segs)
            {
                double sy = oy + sg.Y;
                if (sy + sg.LineH < iy - 6 * _u || sy > iy + ih + 6 * _u) continue;
                DrawSeg(dc, sg, ix + sg.X, sy, bg);
            }
            if (_scroll < _scrollMax - 1)   // more below: the text fades into the panel
                dc.DrawRectangle(VFade(bg, 0, 225), null, new Rect(x + 2 * _u, iy + ih - 26 * _u, Math.Max(w - 4 * _u, 0), 32 * _u));
            if (_scroll > 1)
                dc.DrawRectangle(VFade(bg, 240, 0), null, new Rect(x + 2 * _u, iy - 6 * _u, Math.Max(w - 4 * _u, 0), 16 * _u));
        }
        if (_scrollMax > 0)
        {
            double th = Math.Clamp(ih * ih / (ih + _scrollMax), Math.Min(24 * _u, ih), ih), ty = iy + (ih - th) * (_scroll / _scrollMax);
            dc.DrawRectangle(B(WithA(Ncol(Nc.Border), 50)), null, new Rect(x + w - 16 * _u, iy, 4 * _u, ih), 2 * _u, 2 * _u);
            dc.DrawRectangle(B(WithA(Ncol(Nc.Border), 220)), null, new Rect(x + w - 16 * _u, ty, 4 * _u, th), 2 * _u, 2 * _u);
        }
        if (_head.Title.Length > 0)   // the title on the top border, the border cut under it
        {
            var t = Text(_head.Title, UiBold, 17, Ncol(Nc.Title));
            double tx = x + 20 * _u;
            if (_st.Border > 0)
                dc.DrawRectangle(B(WithA(bg, BoxA)), null, new Rect(tx - 8 * _u, y - 2 * _u - _st.Border * _u / 2, t.Width + 16 * _u, (4 + _st.Border) * _u));
            dc.DrawText(t, new Point(tx, y - t.Height / 2));
        }
        if (_foot.Length > 0)   // "Enter continues" on the bottom border, right
        {
            var t = Text(_foot, Ui, 13, Ncol(Nc.Foot));
            double tx = x + w - t.Width - 22 * _u;
            if (_st.Border > 0)
                dc.DrawRectangle(B(WithA(bg, BoxA)), null, new Rect(tx - 6 * _u, y + h - 2 * _u - _st.Border * _u / 2, t.Width + 12 * _u, (4 + _st.Border) * _u));
            dc.DrawText(t, new Point(tx, y + h - t.Height / 2));
        }
        double fade = FadeIn();
        if (fade < 1) dc.DrawRectangle(B(WithA(bg, (byte)(255 * (1 - fade)))), null, panel, r, r);
        if (statsH > 0)   // the stats bar: a strip of its own under the panel
        {
            var strip = new Rect(x, bottom + 8 * _u, Math.Max(w, 1), statsH);
            dc.DrawRectangle(B(WithA(bg, BoxA)), _st.Border > 0 ? new Pen(B(WithA(Ncol(Nc.Border), 120)), 1 * _u) : null, strip, r, r);
            for (int i = 0; i < _stats.Length; i++) DrawStatsRow(dc, _stats[i], x + w / 2, strip.Y + 5 * _u + i * StatH * _u);
        }
    }

    // ================================================================
    // Pages and lists (Rainwire modules/menu/src/menumod.c screen_panel, draw_page, draw_list: the reference)
    // ================================================================

    private static readonly Typeface MonoReg = new(new FontFamily("JetBrains Mono, DejaVu Sans Mono, monospace"));
    private double PageLH => LineH(MonoReg, 12) + 3 * _u;
    private const double MarkW = 14;

    private Color Pcol(Pc i) => C(_pc[(int)i]);
    private Color Lcol(Lc i) => C(_lc[(int)i]);

    /// <summary>Text cut to maxW with an ellipsis, on one line.</summary>
    private FormattedText Fit(string s, Typeface face, double size, Color c, double maxW)
    {
        var t = Text(s, face, size, c);
        t.MaxTextWidth = Math.Max(maxW, 1);
        t.MaxLineCount = 1;
        t.Trimming = TextTrimming.CharacterEllipsis;
        return t;
    }

    /// <summary>A line of spans from x, cut at w, centered in a row h tall: each span its own color (none: def),
    /// background, pulse, shimmer or blink.</summary>
    private void DrawSLine(DrawingContext dc, Typeface face, double size, Span[] spans, double x, double y, double w, double h,
                           Color def, Color under)
    {
        using var clip = dc.PushClip(new Rect(x, y, Math.Max(w, 0), Math.Max(h, 0)));
        double x0 = x;
        foreach (var s in spans)
        {
            if (x >= x0 + w) break;
            Color fg = s.Fg != None ? C(s.Fg) : def;
            double sw = TextW(s.Text, face, size);
            if (s.Bg != None && !(s.Fx == FxBlink && (long)(Now / 500) % 2 == 1))
                dc.DrawRectangle(B(C(s.Bg)), null, new Rect(x, y + 1 * _u, sw, Math.Max(h - 2 * _u, 1)));
            if (s.Fx == FxPulse)
            {
                double t = 0.5 + 0.5 * Math.Sin(Now / 1000.0 * 3.6);
                fg = Mix(Mix(fg, under, 0.5), fg, t);
            }
            if (s.Fx == FxShimmer)
            {
                double pos = (Now / 1400.0 % 1.8) * (sw + 60 * _u) - 30 * _u, cx = x;
                foreach (var rune in s.Text.EnumerateRunes())
                {
                    string ch = rune.ToString();
                    double cw = TextW(ch, face, size);
                    double glint = 1 - Math.Clamp(Math.Abs(cx + cw / 2 - x - pos) / (24 * _u), 0, 1);
                    var ft = Text(ch, face, size, Mix(fg, Colors.White, glint * 0.85));
                    dc.DrawText(ft, new Point(cx, y + (h - ft.Height) / 2));
                    cx += cw;
                }
            }
            else
            {
                var ft = Text(s.Text, face, size, fg);
                dc.DrawText(ft, new Point(x, y + (h - ft.Height) / 2));
            }
            x += sw;
        }
    }

    /// <summary>Eases the scroll toward its target, as the news does.</summary>
    private void ScrollEase()
    {
        _scrollTo = Math.Clamp(_scrollTo, 0, _scrollMax);
        var now = DateTime.UtcNow;
        double dt = Math.Clamp((now - _scrollAt).TotalMilliseconds, 0, 200);
        _scrollAt = now;
        _scroll += (_scrollTo - _scroll) * (1 - Math.Pow(0.65, dt / 20));
        if (Math.Abs(_scrollTo - _scroll) < 0.5) _scroll = _scrollTo;
    }

    /// <summary>The panel a page or a list goes in, under the header, down to above the status bar, in the sysop's box
    /// style; the title on its top border, "Esc Back" (and a list's edge keys and footer) on its bottom one. Returns
    /// its inside.</summary>
    private Rect ScreenPanel(DrawingContext dc, double W, double H, double top, Color bg, Color border, Color title, Color foot,
                             string edge, string footer)
    {
        bool big = _isList && (_lflags & LfBig) != 0;
        double x = (big ? 8 : 14) * _u, w = Math.Max(W - 2 * x, 1), y = top + 14 * _u;
        double bottom = H - (StatusH + 8) * _u;
        if (bottom - y < 80 * _u) y = Math.Max(bottom - 80 * _u, 14 * _u);
        double h = Math.Max(bottom - y, 1), r = _st.Radius * 8 / 6 * _u, bw = _st.Border * _u;
        var panel = new Rect(x, y, w, h);
        Panel(dc, panel, bg, border, r);
        var cut = B(WithA(bg, BoxA));
        if (_head.Title.Length > 0 && _st.Title != 2)
        {
            var t = Text(_head.Title, UiBold, 17, title);
            double tx = x + 20 * _u;
            if (bw > 0) dc.DrawRectangle(cut, null, new Rect(tx - 8 * _u, y - 2 * _u - bw / 2, t.Width + 16 * _u, 4 * _u + bw));
            dc.DrawText(t, new Point(tx, y - t.Height / 2));
        }
        double rx = x + w - 22 * _u;
        void OnBorder(string s, double right)
        {
            var t = Text(s, Ui, 13, foot);
            if (bw > 0) dc.DrawRectangle(cut, null, new Rect(right - t.Width - 6 * _u, y + h - 2 * _u - bw / 2, t.Width + 12 * _u, 4 * _u + bw));
            dc.DrawText(t, new Point(right - t.Width, y + h - t.Height / 2));
        }
        OnBorder("Esc Back", rx);
        rx -= TextW("Esc Back", Ui, 13) + 20 * _u;
        if (edge.Length > 0) OnBorder(edge, rx);
        if (footer.Length > 0) OnBorder(footer, x + 20 * _u + TextW(footer, Ui, 13));
        double fade = FadeIn();
        if (fade < 1) dc.DrawRectangle(B(WithA(bg, (byte)(255 * (1 - fade)))), null, panel, r, r);
        return new Rect(x + 22 * _u, y + 20 * _u, Math.Max(w - 54 * _u, 1), Math.Max(h - 38 * _u, 1));
    }

    /// <summary>The scroll bar on the panel's right and the fade at the bottom when there is more, as the news.</summary>
    private void ScreenScrollbar(DrawingContext dc, Rect inner, Color bg, Color border)
    {
        if (_scrollMax <= 0) return;
        if (_scroll < _scrollMax - 1)
            dc.DrawRectangle(VFade(bg, 0, 220), null, new Rect(inner.X - 8 * _u, inner.Bottom - 24 * _u, inner.Width + 16 * _u, 24 * _u));
        double ih = inner.Height, th = Math.Clamp(ih * ih / (ih + _scrollMax), Math.Min(24 * _u, ih), ih), ty = inner.Y + (ih - th) * (_scroll / _scrollMax);
        dc.DrawRectangle(B(WithA(border, 50)), null, new Rect(inner.Right + 12 * _u, inner.Y, 4 * _u, ih), 2 * _u, 2 * _u);
        dc.DrawRectangle(B(WithA(border, 220)), null, new Rect(inner.Right + 12 * _u, ty, 4 * _u, th), 2 * _u, 2 * _u);
    }

    /// <summary>A page: its column headings and a rule, then its lines in the mono font (columns lined up with spaces
    /// stay lined up; pipe colors kept), scrolled.</summary>
    private void DrawPage(DrawingContext dc, double W, double H, double top)
    {
        Color bg = Pcol(Pc.PanelBg);
        var inner = ScreenPanel(dc, W, H, top, bg, Pcol(Pc.Border), Pcol(Pc.Title), Pcol(Pc.Foot), "", "");
        double ix = inner.X, iy = inner.Y, iw = inner.Width, ih = inner.Height, lh = PageLH;
        if (_pageHdr.Length > 0)
        {
            DrawSLine(dc, MonoReg, 12, _pageHdr, ix, iy, iw, lh, Pcol(Pc.Header), bg);
            dc.DrawRectangle(B(WithA(Pcol(Pc.Rule), 200)), null, new Rect(ix, iy + lh + 2 * _u, iw, Math.Max(1 * _u, 1)));
            iy += lh + 8 * _u;
            ih = Math.Max(ih - lh - 8 * _u, 1);
        }
        _viewH = ih;
        _scrollMax = Math.Max(_lines.Count * lh - ih, 0);
        ScrollEase();
        using (dc.PushClip(new Rect(ix - 4 * _u, iy, iw + 8 * _u, ih)))
            for (int i = Math.Max((int)(_scroll / lh), 0); i < _lines.Count; i++)
            {
                double y = iy + i * lh - _scroll;
                if (y > iy + ih) break;
                DrawSLine(dc, MonoReg, 12, _lines[i], ix, y, iw, lh, Pcol(Pc.Text), bg);
            }
        ScreenScrollbar(dc, new Rect(ix, iy, iw, ih), bg, Pcol(Pc.Border));
    }

    private double RowH(LRow r)
    {
        if (!r.Ok) return LineH(UiBold, 13) + 12 * _u;
        double h = _st.Height * _u;
        if ((_lflags & LfSubBelow) == 0 && r.Sub.Length > 0) h += LineH(Ui, 11) + 2 * _u;
        return h;
    }

    /// <summary>The columns' left edges and widths across iw: a column's width is its text cells, the ones of width 0
    /// share what is left; all shrunk together when they don't fit.</summary>
    private (double[] X, double[] W) ListCols(double ix, double iw)
    {
        double cell = TextW("0", Ui, 13) + 0.5 * _u, fixedW = 0;
        int flex = 0;
        foreach (var c in _cols) { if (c.Width > 0) fixedW += c.Width * cell + 12 * _u; else flex++; }
        double room = iw - MarkW * _u, scale = 1;
        if (fixedW > room - flex * 60 * _u) scale = (room - flex * 60 * _u) / Math.Max(fixedW, 1);
        scale = Math.Max(scale, 0.3);
        double rest = room - fixedW * scale, x = ix + MarkW * _u;
        var cx = new double[_cols.Length];
        var cw = new double[_cols.Length];
        for (int c = 0; c < _cols.Length; c++)
        {
            cw[c] = Math.Max(_cols[c].Width > 0 ? _cols[c].Width * cell * scale + 12 * _u * scale : (flex > 0 ? rest / flex : 0), 20 * _u);
            cx[c] = x;
            x += cw[c];
        }
        return (cx, cw);
    }

    /// <summary>A list: the column titles and a rule, the rows (headings bold in accent2, never selected; bold rows
    /// bright, dim rows dim; the mark left of the first column; a sub as a small second line, or the selected row's
    /// under a rule at the bottom), the selection in the style's shape, the keys line at the bottom.</summary>
    private void DrawList(DrawingContext dc, double W, double H, double top)
    {
        Color bg = Lcol(Lc.PanelBg), border = Lcol(Lc.Border);
        var inner = ScreenPanel(dc, W, H, top, bg, border, Lcol(Lc.Title), Lcol(Lc.Foot), _ledge, _lfooter);
        double ix = inner.X, iy = inner.Y, iw = inner.Width, ih = inner.Height;
        var (cx, cw) = ListCols(ix, iw);
        if (_cols.Any(c => c.Title.Length > 0))   // the column titles and a rule
        {
            double hh = LineH(UiBold, 13) + 6 * _u;
            for (int c = 0; c < _cols.Length; c++)
            {
                if (_cols[c].Title.Length == 0) continue;
                var t = Fit(_cols[c].Title, UiBold, 13, Lcol(Lc.Accent), cw[c] - 8 * _u);
                dc.DrawText(t, new Point(cx[c] + 4 * _u, iy + (hh - t.Height) / 2));
            }
            dc.DrawRectangle(B(WithA(border, 160)), null, new Rect(ix, iy + hh + 1 * _u, iw, Math.Max(1 * _u, 1)));
            iy += hh + 6 * _u;
            ih -= hh + 6 * _u;
        }
        // the bottom: the selected row's sub (sub below) and the screen's keys, under a rule
        string sub = (_lflags & LfSubBelow) != 0 && RowOk(_lsel) ? _rows[_lsel].Sub : "";
        double foot = (sub.Length > 0 ? LineH(Ui, 13) + 6 * _u : 0) + (_lkeys.Length > 0 ? LineH(Ui, 11) + 6 * _u : 0);
        if (foot > 0)
        {
            ih -= foot + 8 * _u;
            double fy = iy + ih + 6 * _u;
            dc.DrawRectangle(B(WithA(border, 160)), null, new Rect(ix, fy, iw, Math.Max(1 * _u, 1)));
            fy += 5 * _u;
            if (sub.Length > 0)
            {
                dc.DrawText(Fit(sub, Ui, 13, Lcol(Lc.Text), iw), new Point(ix, fy));
                fy += LineH(Ui, 13) + 6 * _u;
            }
            if (_lkeys.Length > 0) dc.DrawText(Fit(_lkeys, Ui, 11, Lcol(Lc.Foot), iw), new Point(ix, fy));
        }
        ih = Math.Max(ih, 1);
        _viewH = ih;
        _rowHits.Clear();
        if (_rows.Count == 0)
        {
            if (_lempty.Length > 0)
            {
                var t = Text(_lempty, Ui, 13, Lcol(Lc.Dim));
                dc.DrawText(t, new Point(ix + (iw - t.Width) / 2, iy + ih / 2 - 8 * _u));
            }
            return;
        }
        double y = 0;   // each row's place, from the top of the rows
        foreach (var r in _rows) { r.Y = y; r.H = RowH(r); y += r.H; }
        _scrollMax = Math.Max(y - ih, 0);
        if (RowOk(_lsel))   // keep the selection in view
        {
            var r = _rows[_lsel];
            if (r.Y < _scrollTo) _scrollTo = r.Y;
            if (r.Y + r.H > _scrollTo + ih) _scrollTo = r.Y + r.H - ih;
        }
        if (_listSnap)   // shown at its selection at once (Last callers opens on its newest row, at the bottom)
        {
            _listSnap = false;
            _scrollTo = Math.Clamp(_scrollTo, 0, _scrollMax);
            _scroll = _scrollTo;
        }
        ScrollEase();
        using (dc.PushClip(new Rect(ix - 10 * _u, iy, iw + 20 * _u, ih)))
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                var r = _rows[i];
                double ry = iy + r.Y - _scroll;
                if (ry + r.H < iy) continue;
                if (ry > iy + ih) break;
                if (!r.Ok)   // a heading
                {
                    double hx = ix;
                    foreach (var col in r.Cols)
                    {
                        DrawSLine(dc, UiBold, 13, col, hx, ry + 4 * _u, iw - (hx - ix), r.H - 4 * _u, Lcol(Lc.Accent2), bg);
                        hx += TextW(" ", UiBold, 13) + col.Sum(s => TextW(s.Text, UiBold, 13));
                    }
                    continue;
                }
                bool sel = i == _lsel;
                Color under = bg, def = (r.Flags & LiDim) != 0 ? Lcol(Lc.Dim) : (r.Flags & LiBold) != 0 ? Lcol(Lc.Bright) : Lcol(Lc.Text);
                double rh = _st.Height * _u, sr = _st.SelRadius * _u;
                var bar = new Rect(ix - 6 * _u, ry + 1 * _u, iw + 12 * _u, Math.Max(rh - 2 * _u, 1));
                var rowRect = new Rect(ix - 6 * _u, ry, iw + 12 * _u, r.H);
                if (sel)
                {
                    byte a = _waiting ? (byte)170 : (byte)255;
                    switch (_st.Sel)
                    {
                        case 1:
                            dc.DrawRectangle(null, new Pen(B(WithA(Mcol(Mc.SelBg), a)), 1.5 * _u), bar, sr, sr);
                            break;
                        case 2:
                            dc.DrawRectangle(B(WithA(Mcol(Mc.SelBg), (byte)(55 * a / 255))), null,
                                             new Rect(bar.X - 3 * _u, bar.Y - 3 * _u, bar.Width + 6 * _u, bar.Height + 6 * _u), sr + 3 * _u, sr + 3 * _u);
                            dc.DrawRectangle(B(WithA(Mcol(Mc.SelBg), (byte)(150 * a / 255))), null, bar, sr, sr);
                            under = Mix(under, Mcol(Mc.SelBg), 150 / 255.0);
                            break;
                        case 3:
                            dc.DrawRectangle(B(WithA(Mcol(Mc.SelBg), a)), null, new Rect(ix, ry + rh - 3 * _u, iw, 2 * _u));
                            break;
                        default:
                            if ((_head.Flags & MfGradient) != 0)
                            {
                                dc.DrawRectangle(new LinearGradientBrush
                                {
                                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                                    EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                                    GradientStops = { new GradientStop(WithA(Mcol(Mc.SelBg), a), 0), new GradientStop(WithA(Mcol(Mc.SelBg2), a), 1) },
                                }, null, bar);
                                under = Mix(Mcol(Mc.SelBg), Mcol(Mc.SelBg2), 0.5);
                            }
                            else
                            {
                                dc.DrawRectangle(B(WithA(Mcol(Mc.SelBg), a)), null, bar, sr, sr);
                                under = Mcol(Mc.SelBg);
                            }
                            break;
                    }
                    if (_st.Marker && _st.Sel != 3) dc.DrawRectangle(B(Mcol(Mc.SelMarker)), null, new Rect(bar.X, bar.Y, 3 * _u, bar.Height));
                    if (_st.Sel is 0 or 2) def = Mcol(Mc.SelFg);
                }
                else if (_st.Hover && rowRect.Contains(_mouse))
                {
                    dc.DrawRectangle(B(Mix(bg, Mcol(Mc.SelBg), 60 / 255.0)), null, bar, sr, sr);
                }
                var face = (r.Flags & LiBold) != 0 ? UiBold : Ui;
                if (r.Mark.Length > 0)
                {
                    var mt = Text(r.Mark, Ui, 13, sel ? def : Lcol(Lc.Mark));
                    dc.DrawText(mt, new Point(ix, ry + (rh - mt.Height) / 2));
                }
                for (int c = 0; c < r.Cols.Length && c < _cols.Length; c++)
                {
                    double ind = c == 0 ? r.Indent * 14 * _u : 0, x = cx[c] + 4 * _u + ind, w = cw[c] - 8 * _u - ind;
                    if (w > 4 * _u) DrawSLine(dc, face, 13, r.Cols[c], x, ry, w, rh, def, under);
                }
                if ((_lflags & LfSubBelow) == 0 && r.Sub.Length > 0)
                    dc.DrawText(Fit(r.Sub, Ui, 11, sel ? def : Lcol(Lc.Dim), iw - 30 * _u),
                                new Point((cx.Length > 0 ? cx[0] : ix) + 4 * _u + r.Indent * 14 * _u, ry + rh - 2 * _u));
                _rowHits.Add((rowRect, i));
            }
        }
        ScreenScrollbar(dc, new Rect(ix, iy, iw, ih), bg, border);
    }

    // ================================================================
    // Forms (Rainwire modules/menu/src/menumod.c draw_form: the reference)
    // ================================================================

    private Color Fcol(Fc i) => C(_fc[(int)i]);

    /// <summary>A box like the menu's in the room under the header, 62% of the window wide: labels in a column, each value
    /// in a rounded field (the active one lit, with the marker stripe and a blinking caret), a choice field's choices as
    /// pills; under the fields the error and the active field's help (else the hint).</summary>
    private void DrawForm(DrawingContext dc, double W, double H, double top)
    {
        _formHits.Clear();
        if (_fields.Count == 0) return;
        const double rowL = 36;
        double labelW = _fields.Max(f => TextW(f.Label, UiBold, 13));
        double w = Math.Clamp(W * 0.62, 440 * _u, Math.Max(W - 40 * _u, 440 * _u)), pad = (_st.Padding + 6) * _u;
        labelW = Math.Min(labelW, w * 0.4);
        double uiH = LineH(Ui, 13);
        double foot = (_ferr.Length > 0 ? uiH + 6 * _u : 0) + uiH + 12 * _u;
        double h = _fields.Count * rowL * _u + 2 * pad + foot + (_st.Title == 1 ? LineH(UiBold, 17) + 10 * _u : 0);
        double bottom = H - (StatusH + 8) * _u, gap = 26 * _u;
        double x = (W - w) / 2 + W * _st.X / 100, y = top + gap + (bottom - top - gap - h) / 2;
        if (_st.VAlign == 1) y = top + gap;
        else if (_st.VAlign == 3) y = bottom - h;
        y += H * _st.Y / 100;
        if (y + h > bottom) y = bottom - h;
        if (y < top + gap && bottom - h >= top + gap) y = top + gap;
        if (y < 4 * _u) y = 4 * _u;
        var box = new Rect(x, y, w, Math.Max(h, 1));
        double r = _st.Radius * _u;
        Color bg = Fcol(Fc.FormBg);
        Panel(dc, box, bg, Fcol(Fc.Border), r);
        double fy = y + pad;
        if (_head.Title.Length > 0 && _st.Title == 0)
        {
            var t = Text(_head.Title, UiBold, 17, Fcol(Fc.Title));
            double tx = x + 18 * _u, bw = _st.Border * _u;
            if (bw > 0) dc.DrawRectangle(B(WithA(bg, BoxA)), null, new Rect(tx - 8 * _u, y - 2 * _u - bw / 2, t.Width + 16 * _u, 4 * _u + bw));
            dc.DrawText(t, new Point(tx, y - t.Height / 2));
        }
        else if (_head.Title.Length > 0 && _st.Title == 1)
        {
            dc.DrawText(Text(_head.Title, UiBold, 17, Fcol(Fc.Title)), new Point(x + pad, fy - 4 * _u));
            fy += LineH(UiBold, 17) + 10 * _u;
        }
        double lx = x + pad, vx = lx + labelW + 18 * _u, vw = Math.Max(x + w - pad - vx, 1);
        var now = DateTime.UtcNow;
        bool caretOn = (now - _caretAt).TotalMilliseconds < 600 || (long)(Now / 530) % 2 == 0;
        for (int i = 0; i < _fields.Count; i++)
        {
            var f = _fields[i];
            bool on = i == _fact;
            double ry = fy + i * rowL * _u, bh = (rowL - 8) * _u;
            var lab = Fit(f.Label, UiBold, 13, on ? Fcol(Fc.LabelOn) : Fcol(Fc.Label), labelW);
            dc.DrawText(lab, new Point(lx, ry + (bh - lab.Height) / 2));
            if (f.Choice)   // the choices as pills, the value's filled in the marker color
            {
                double cx = vx;
                int sel = ChoiceIndex(f);
                for (int c = 0; c < f.Choices.Length; c++)
                {
                    double cw = TextW(f.Choices[c], Ui, 13) + 22 * _u;
                    if (cx + cw > vx + vw) break;
                    var pill = new Rect(cx, ry + 2 * _u, cw, Math.Max(bh - 4 * _u, 1));
                    double pr = pill.Height / 2;
                    if (c == sel) dc.DrawRectangle(B(Fcol(Fc.Marker)), null, pill, pr, pr);
                    else dc.DrawRectangle(null, new Pen(B(WithA(on ? Fcol(Fc.LabelOn) : Fcol(Fc.Label), 140)), 1 * _u), pill, pr, pr);
                    Color tc = c == sel ? (Luma(Fcol(Fc.Marker)) > 140 ? Color.FromRgb(0x10, 0x10, 0x10) : Colors.White) : Fcol(Fc.Label);
                    var ct = Text(f.Choices[c], Ui, 13, tc);
                    dc.DrawText(ct, new Point(cx + (cw - ct.Width) / 2, pill.Y + (pill.Height - ct.Height) / 2));
                    _formHits.Add((new Rect(cx, ry, cw, bh), i, c, 0));
                    cx += cw + 8 * _u;
                }
                if (on) dc.DrawRectangle(B(Fcol(Fc.Marker)), null, new Rect(vx - 10 * _u, ry + 4 * _u, 3 * _u, Math.Max(bh - 8 * _u, 1)));
            }
            else
            {
                var field = new Rect(vx, ry, vw, bh);
                dc.DrawRectangle(B(on ? Fcol(Fc.FieldBgOn) : WithA(Fcol(Fc.FieldBg), 150)), null, field, _st.SelRadius * _u, _st.SelRadius * _u);
                if (on && _st.Marker) dc.DrawRectangle(B(Fcol(Fc.Marker)), null, new Rect(vx, ry, 3 * _u, bh));
                string shown = f.Secret ? new string('•', Chars(f.Value)) : f.Value;
                string before = f.Secret ? new string('•', Chars(f.Value[..f.Cur])) : f.Value[..f.Cur];
                double tx = vx + 10 * _u, tw = vw - 20 * _u, cxw = TextW(before, Ui, 13);
                double shift = cxw > tw - 4 * _u ? cxw - (tw - 4 * _u) : 0;   // keep the caret in view
                var vt = Text(shown, Ui, 13, Fcol(Fc.FieldText));
                using (dc.PushClip(new Rect(tx, ry, Math.Max(tw, 0), bh)))
                    dc.DrawText(vt, new Point(tx - shift, ry + (bh - vt.Height) / 2));
                if (on && caretOn)
                    dc.DrawRectangle(B(Fcol(Fc.Marker)), null, new Rect(tx - shift + cxw, ry + 6 * _u, 2 * _u, Math.Max(bh - 12 * _u, 1)));
                _formHits.Add((field, i, -1, tx - shift));
            }
        }
        double hy = fy + _fields.Count * rowL * _u + 4 * _u;
        if (_ferr.Length > 0)
        {
            dc.DrawText(Fit(_ferr, Ui, 13, Fcol(Fc.Error), w - 2 * pad), new Point(lx, hy));
            hy += uiH + 6 * _u;
        }
        string hint = _fact < _fields.Count && _fields[_fact].Help.Length > 0 ? _fields[_fact].Help : _fhint;
        if (hint.Length > 0) dc.DrawText(Fit(hint, Ui, 13, Fcol(Fc.Hint), w - 2 * pad), new Point(lx, hy));
        double fade = FadeIn();
        if (fade < 1) dc.DrawRectangle(B(WithA(bg, (byte)(255 * (1 - fade)))), null, box, r, r);
    }

    // ================================================================
    // Picture screens and charts (Rainwire modules/menu/src/menumod.c draw_picture, draw_chart, chart_vertical,
    // chart_hbars, chart_heat, chart_share, tooltip: the reference)
    // ================================================================

    private Color Xcol(Xc i) => C(_xc[(int)i]);
    private double PicLH => LineH(MonoReg, 12) + 4 * _u;

    private void DrawPicture(DrawingContext dc, double W, double H, double top)
    {
        Color bg = Xcol(Xc.PanelBg);
        var inner = ScreenPanel(dc, W, H, top, bg, Xcol(Xc.Border), Xcol(Xc.Title), Xcol(Xc.Dim), "", "");
        double ix = inner.X, iy = inner.Y, iw = inner.Width, ih = inner.Height;
        _tabHits.Clear();
        if (_tabs.Length > 0)   // the tabs as pills, the current one lit; a click opens one
        {
            double tx = ix, th = 26 * _u;
            for (int i = 0; i < _tabs.Length; i++)
            {
                var t = Text(_tabs[i], UiBold, 13, i == _tab ? Xcol(Xc.TabFg) : Xcol(Xc.Text));
                double tw = t.Width + 26 * _u;
                if (tx + tw > ix + iw) break;
                var pill = new Rect(tx, iy, tw, th);
                bool on = i == _tab, hv = pill.Contains(_mouse);
                if (on) dc.DrawRectangle(B(Xcol(Xc.TabBg)), null, pill, th / 2, th / 2);
                else if (hv) dc.DrawRectangle(B(WithA(Xcol(Xc.TabBg), 70)), null, pill, th / 2, th / 2);
                else dc.DrawRectangle(null, new Pen(B(WithA(Xcol(Xc.Border), 160)), 1 * _u), pill, th / 2, th / 2);
                dc.DrawText(t, new Point(tx + (tw - t.Width) / 2, iy + (th - t.Height) / 2));
                _tabHits.Add((pill, i));
                tx += tw + 8 * _u;
            }
            iy += th + 14 * _u;
            ih -= th + 14 * _u;
        }
        double lh = PicLH;
        foreach (var l in _heads)   // the lines above the chart, in the mono font (their columns line up)
        {
            DrawSLine(dc, MonoReg, 12, l, ix, iy, iw, lh, Xcol(Xc.Text), bg);
            iy += lh;
            ih -= lh;
        }
        if (_heads.Length > 0) { iy += 10 * _u; ih -= 10 * _u; }
        double notesH = _notes.Length > 0 ? _notes.Length * lh + 10 * _u : 0;
        for (int i = 0; i < _notes.Length; i++)
            DrawSLine(dc, MonoReg, 12, _notes[i], ix, iy + ih - notesH + 10 * _u + i * lh, iw, lh, Xcol(Xc.Dim), bg);
        if (_hasChart && _chart != null) DrawChart(dc, _chart, ix, iy, iw, ih - notesH - 6 * _u, W, H);
        else if (_picImage != null && ih - notesH - 6 * _u > 0) DrawPicImage(dc, ix, iy, iw, ih - notesH - 6 * _u);
    }

    /// <summary>A picture screen's image, as large as fits, centered: art crisp (nearest pixel, any scale: whole
    /// multiples only left a 640-pixel piece small in a big window), a photo smoothly.</summary>
    private void DrawPicImage(DrawingContext dc, double x, double y, double w, double h)
    {
        var img = _picImage;
        if (img == null || img.PixelSize.Width <= 0 || img.PixelSize.Height <= 0) return;
        double pw = img.PixelSize.Width, ph = img.PixelSize.Height, s = Math.Min(w / pw, h / ph);
        var r = new Rect(x + (w - pw * s) / 2, y + (h - ph * s) / 2, pw * s, ph * s);
        using (dc.PushRenderOptions(new RenderOptions
        {
            BitmapInterpolationMode = _picSmooth ? Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality
                                                 : Avalonia.Media.Imaging.BitmapInterpolationMode.None,
        }))
            dc.DrawImage(img, r);
    }

    private static double Nice(double v)
    {
        if (v <= 0) return 1;
        double e = Math.Pow(10, Math.Floor(Math.Log10(v))), m = v / e;
        return (m <= 1 ? 1 : m <= 2 ? 2 : m <= 2.5 ? 2.5 : m <= 5 ? 5 : 10) * e;
    }

    /// <summary>A number as the axis and the tooltips show it: whole numbers plain, big ones in thousands.</summary>
    private static string Num(double v)
    {
        double a = Math.Abs(v);
        var ci = CultureInfo.InvariantCulture;
        if (a >= 100000) return (v / 1000).ToString("0", ci) + "k";
        if (a >= 10000) return (v / 1000).ToString("0.0", ci) + "k";
        return v == Math.Floor(v) ? v.ToString("0", ci) : v.ToString("0.0", ci);
    }

    private static double Val(Chart c, int s, int i) => s < c.Series.Length && i < c.Series[s].V.Length ? c.Series[s].V[i] : 0;
    private static double Total(Chart c, int i) { double t = 0; for (int s = 0; s < c.Series.Length; s++) t += Val(c, s, i); return t; }

    private void TextC(DrawingContext dc, string s, Typeface f, double size, Color c, double cx, double y)
    {
        var t = Text(s, f, size, c);
        dc.DrawText(t, new Point(cx - t.Width / 2, y));
    }

    /// <summary>The tooltip: lines in a small box by the pointer, kept inside the window; a colored square where a
    /// line has a color (default: none).</summary>
    private void Tooltip(DrawingContext dc, IReadOnlyList<(string Text, Color? C)> lines, double W, double H)
    {
        double lh = LineH(Ui, 11) + 3 * _u, w = 0;
        foreach (var (t, c) in lines) w = Math.Max(w, TextW(t, Ui, 11) + (c != null ? 14 * _u : 0));
        double bw = w + 16 * _u, bh = lines.Count * lh + 10 * _u;
        double bx = _mouse.X + 14 * _u, by = _mouse.Y + 10 * _u;
        if (bx + bw > W - 4 * _u) bx = _mouse.X - bw - 10 * _u;
        if (by + bh > H - 4 * _u) by = _mouse.Y - bh - 6 * _u;
        double r = 6 * _u;
        dc.DrawRectangle(B(Color.FromArgb(90, 0, 0, 0)), null, new Rect(bx + 2 * _u, by + 3 * _u, bw, bh), r, r);
        dc.DrawRectangle(B(WithA(Mix(Xcol(Xc.PanelBg), Colors.Black, 60 / 255.0), 245)), new Pen(B(Xcol(Xc.Border)), 1 * _u),
                         new Rect(bx, by, bw, bh), r, r);
        for (int i = 0; i < lines.Count; i++)
        {
            double tx = bx + 8 * _u, ty = by + 5 * _u + i * lh;
            if (lines[i].C is { } c)
            {
                dc.DrawRectangle(B(c), null, new Rect(tx, ty + lh / 2 - 5 * _u, 8 * _u, 8 * _u), 2 * _u, 2 * _u);
                tx += 14 * _u;
            }
            dc.DrawText(Text(lines[i].Text, Ui, 11, i == 0 ? Xcol(Xc.Accent) : Xcol(Xc.Text)), new Point(tx, ty));
        }
    }

    private void DrawChart(DrawingContext dc, Chart c, double x, double y, double w, double h, double W, double H)
    {
        if (w <= 0 || h <= 0) return;
        switch (c.Kind)
        {
            case Ck.HBars: ChartHBars(dc, c, x, y, w, h); break;
            case Ck.Heat: ChartHeat(dc, c, x, y, w, h, W, H); break;
            case Ck.Share: ChartShare(dc, c, x, y, w); break;
            case Ck.Gauge:   // a bar of value out of max, its caption beside it
            {
                double gw = w * 0.6, gh = 22 * _u, f = c.Max > 0 ? Math.Clamp(c.Value / c.Max, 0, 1) : 0;
                Color col = c.Series.Length > 0 ? c.Series[0].C : Xcol(Xc.Accent);
                dc.DrawRectangle(B(WithA(Xcol(Xc.Grid), 50)), null, new Rect(x, y, gw, gh), gh / 2, gh / 2);
                if (f > 0) dc.DrawRectangle(B(WithA(col, 240)), null, new Rect(x, y, Math.Max(gw * f, gh), gh), gh / 2, gh / 2);
                if (c.Unit.Length > 0)
                {
                    var t = Text(c.Unit, Ui, 13, Xcol(Xc.Text));
                    dc.DrawText(t, new Point(x + gw + 14 * _u, y + (gh - t.Height) / 2));
                }
                break;
            }
            case Ck.Spark:
            {
                int n = c.Labels.Length;
                if (n < 2 || c.Series.Length == 0) break;
                double topV = 0, sh = Math.Clamp(h, 20 * _u, 60 * _u);
                for (int i = 0; i < n; i++) topV = Math.Max(topV, Val(c, 0, i));
                topV = Nice(topV);
                Color col = c.Series[0].C;
                var pen = new Pen(B(col), 2 * _u);
                double step = w / (n - 1), lx = 0, ly = 0;
                for (int i = 0; i < n; i++)
                {
                    double cx = x + step * i, cy = y + sh - sh * Math.Clamp(Val(c, 0, i) / topV, 0, 1);
                    dc.DrawRectangle(B(WithA(col, 35)), null, new Rect(cx - step / 2, cy, step, Math.Max(y + sh - cy, 0)));
                    if (i > 0) dc.DrawLine(pen, new Point(lx, ly), new Point(cx, cy));
                    lx = cx; ly = cy;
                }
                break;
            }
            default: ChartVertical(dc, c, x, y, w, h, W, H); break;
        }
    }

    /// <summary>Bars, stacked bars and the line: a y axis with gridlines and its numbers, the x labels under (as many as
    /// fit), a legend for more than one series; the slot under the pointer lit, with a tooltip.</summary>
    private void ChartVertical(DrawingContext dc, Chart c, double x, double y, double w, double h, double W, double H)
    {
        int n = c.Labels.Length;
        if (n <= 0) return;
        bool stacked = c.Kind == Ck.Stacked, line = c.Kind == Ck.Line;
        double topV = c.Max;
        if (topV <= 0)
            for (int i = 0; i < n; i++)
            {
                double v = stacked ? Total(c, i) : 0;
                if (!stacked) for (int s = 0; s < c.Series.Length; s++) v = Math.Max(v, Val(c, s, i));
                topV = Math.Max(topV, v);
            }
        topV = Nice(topV);
        double smallH = LineH(Ui, 11);
        double aw = TextW(Num(topV), Ui, 11) + 10 * _u, lh = smallH + 6 * _u;
        double leg = c.Series.Length > 1 ? LineH(Ui, 13) + 10 * _u : 0;
        double px = x + aw, pw = w - aw, py = y + 6 * _u, ph = h - lh - 6 * _u - leg;
        if (pw < 20 * _u || ph < 20 * _u) return;
        for (int k = 0; k <= 4; k++)   // gridlines and their numbers
        {
            double gy = py + ph - ph * k / 4;
            dc.DrawRectangle(B(WithA(Xcol(Xc.Grid), k == 0 ? (byte)160 : (byte)50)), null, new Rect(px, gy, pw, Math.Max(1 * _u, 1)));
            var t = Text(Num(topV * k / 4), Ui, 11, Xcol(Xc.Accent));
            dc.DrawText(t, new Point(px - 6 * _u - t.Width, gy - smallH / 2));
        }
        double slot = pw / n, bw = Math.Max(slot * 0.72, 1);
        int hov = new Rect(px, py, pw, ph + lh).Contains(_mouse) ? Math.Clamp((int)((_mouse.X - px) / slot), 0, n - 1) : -1;
        if (hov >= 0) dc.DrawRectangle(B(WithA(Xcol(Xc.Text), 18)), null, new Rect(px + hov * slot, py, slot, ph));
        if (line)   // each series a line with the area under it filled
        {
            foreach (var se in c.Series)
            {
                var pen = new Pen(B(se.C), 2.2 * _u, lineCap: PenLineCap.Round);
                double lx = 0, ly = 0;
                for (int i = 0; i < n; i++)
                {
                    double v = i < se.V.Length ? se.V[i] : 0;
                    double cx = px + slot * (i + 0.5), cy = py + ph - ph * Math.Clamp(v / topV, 0, 1);
                    dc.DrawRectangle(B(WithA(se.C, 40)), null, new Rect(cx - slot / 2, cy, slot, Math.Max(py + ph - cy, 0)));
                    if (i > 0) dc.DrawLine(pen, new Point(lx, ly), new Point(cx, cy));
                    if (slot >= 10 * _u || i == hov)
                    {
                        double pr = (i == hov ? 4.5 : 2.5) * _u;
                        dc.DrawEllipse(B(se.C), null, new Point(cx, cy), pr, pr);
                    }
                    lx = cx; ly = cy;
                }
            }
        }
        else
        {
            for (int i = 0; i < n; i++)
            {
                double bx = px + slot * i + (slot - bw) / 2, baseY = py + ph;
                byte a = i == hov ? (byte)255 : (byte)220;
                for (int s = 0; s < c.Series.Length; s++)
                {
                    double v = Val(c, s, i), bh = ph * Math.Clamp(v / topV, 0, 1);
                    if (!stacked && c.Series.Length > 1)   // side by side
                    {
                        double sw = bw / c.Series.Length, sr = sw > 6 * _u ? 2 * _u : 0;
                        dc.DrawRectangle(B(WithA(c.Series[s].C, a)), null, new Rect(bx + s * sw, baseY - bh, Math.Max(sw - 1 * _u, 0.5), bh), sr, sr);
                        continue;
                    }
                    if (bh <= 0) continue;
                    bool topmost = true;
                    for (int s2 = s + 1; s2 < c.Series.Length && stacked; s2++) if (Val(c, s2, i) > 0) topmost = false;
                    double rr = topmost && bw > 6 * _u ? 3 * _u : 0;
                    dc.DrawRectangle(B(WithA(c.Series[s].C, a)), null, new Rect(bx, baseY - bh, bw, bh), rr, rr);
                    if (stacked) baseY -= bh;
                }
            }
        }
        // the x labels: every few, as many as fit without touching
        double lw = c.Labels.Max(l => TextW(l, Ui, 11));
        int every = Math.Max(c.Every, 1);
        while (every < n && slot * every < lw + 8 * _u) every++;
        for (int i = 0; i < n; i += every) TextC(dc, c.Labels[i], Ui, 11, Xcol(Xc.Dim), px + slot * (i + 0.5), py + ph + 4 * _u);
        if (leg > 0)   // each series' color and name, centered
        {
            double tw = c.Series.Sum(se => TextW(se.Name, Ui, 13) + 34 * _u);
            double lx = px + (pw - tw) / 2, ly = py + ph + lh + 6 * _u, uiH = LineH(Ui, 13);
            foreach (var se in c.Series)
            {
                dc.DrawRectangle(B(se.C), null, new Rect(lx, ly + uiH / 2 - 5 * _u, 10 * _u, 10 * _u), 2 * _u, 2 * _u);
                var t = Text(se.Name, Ui, 13, Xcol(Xc.Text));
                dc.DrawText(t, new Point(lx + 16 * _u, ly));
                lx += 16 * _u + t.WidthIncludingTrailingWhitespace + 18 * _u;
            }
        }
        if (hov >= 0)
        {
            var ls = new List<(string, Color?)> { (c.Labels[hov], null) };
            foreach (var se in c.Series.Take(8))
                ls.Add(($"{se.Name}{(se.Name.Length > 0 ? ": " : "")}{Num(hov < se.V.Length ? se.V[hov] : 0)} {c.Unit}", se.C));
            if (stacked && c.Series.Length > 1) ls.Add(($"{Num(Total(c, hov))} {c.Unit}", null));
            Tooltip(dc, ls, W, H);
        }
    }

    /// <summary>Horizontal bars: a label each, a track, the bar, its number and its share of the total.</summary>
    private void ChartHBars(DrawingContext dc, Chart c, double x, double y, double w, double h)
    {
        int n = c.Labels.Length;
        if (n <= 0) return;
        double topV = c.Max, total = 0, lw = 0;
        for (int i = 0; i < n; i++)
        {
            double v = Val(c, 0, i);
            total += v;
            if (c.Max <= 0) topV = Math.Max(topV, v);
            lw = Math.Max(lw, TextW(c.Labels[i], Ui, 13));
        }
        if (topV <= 0) topV = 1;
        lw = Math.Clamp(lw + 12 * _u, 60 * _u, w * 0.35);
        double rh = Math.Clamp(h / n, 16 * _u, 30 * _u), nw = TextW("000000 100%", Ui, 13) + 12 * _u;
        double bx = x + lw, bwMax = Math.Max(w - lw - nw, 1);
        Color col = c.Series.Length > 0 ? c.Series[0].C : Xcol(Xc.Accent);
        for (int i = 0; i < n; i++)
        {
            double ry = y + i * rh;
            if (ry + rh > y + h + 1) break;
            bool on = new Rect(x, ry, w, rh).Contains(_mouse);
            if (on) dc.DrawRectangle(B(WithA(Xcol(Xc.Text), 16)), null, new Rect(x - 4 * _u, ry, w + 8 * _u, rh));
            var lt = Fit(c.Labels[i], Ui, 13, Xcol(Xc.Text), lw - 12 * _u);
            dc.DrawText(lt, new Point(x, ry + (rh - lt.Height) / 2));
            double v = Val(c, 0, i), bwid = bwMax * Math.Clamp(v / topV, 0, 1), tr = rh * 0.3;
            dc.DrawRectangle(B(WithA(Xcol(Xc.Grid), 40)), null, new Rect(bx, ry + rh * 0.2, bwMax, rh * 0.6), tr, tr);
            if (bwid > 0) dc.DrawRectangle(B(WithA(col, on ? (byte)255 : (byte)225)), null, new Rect(bx, ry + rh * 0.2, Math.Max(bwid, rh * 0.6), rh * 0.6), tr, tr);
            var nt = Text($"{Num(v)}  {(total > 0 ? 100 * v / total : 0).ToString("0", CultureInfo.InvariantCulture)}%", Ui, 13,
                          on ? Xcol(Xc.Accent) : Xcol(Xc.Dim));
            dc.DrawText(nt, new Point(bx + bwid + 8 * _u < bx + bwMax ? bx + bwid + 8 * _u : bx + bwMax + 8 * _u, ry + (rh - nt.Height) / 2));
        }
    }

    /// <summary>A heat map: rows by columns, each rounded cell from a dim panel shade to the accent by its value; row and
    /// column labels (as many as fit); a tooltip on the cell under the pointer.</summary>
    private void ChartHeat(DrawingContext dc, Chart c, double x, double y, double w, double h, double W, double H)
    {
        int hr = c.RowL.Length, hc = c.ColL.Length;
        if (hr <= 0 || hc <= 0) return;
        double topV = c.Max;
        if (c.Max <= 0) foreach (var v in c.Heat) topV = Math.Max(topV, v);
        if (topV <= 0) topV = 1;
        double lw = c.RowL.Max(l => TextW(l, Ui, 11)) + 10 * _u, lh = LineH(Ui, 11) + 6 * _u;
        double cw = (w - lw) / hc, rh = Math.Clamp((h - lh) / hr, 10 * _u, 34 * _u);
        Color hot = Xcol(Xc.Accent), cold = Mix(Xcol(Xc.PanelBg), Xcol(Xc.Grid), 50 / 255.0);
        double labw = c.ColL.Max(l => TextW(l, Ui, 11));
        int every = 1;
        while (every < hc && cw * every < labw + 6 * _u) every++;
        for (int col = 0; col < hc; col += every) TextC(dc, c.ColL[col], Ui, 11, Xcol(Xc.Dim), x + lw + cw * (col + 0.5), y);
        int hR = -1, hC = -1;
        for (int r = 0; r < hr; r++)
        {
            double ry = y + lh + r * rh;
            var rt = Text(c.RowL[r], Ui, 11, Xcol(Xc.Dim));
            dc.DrawText(rt, new Point(x, ry + (rh - rt.Height) / 2));
            for (int col = 0; col < hc; col++)
            {
                double v = c.Heat[r * hc + col], f = Math.Clamp(v / topV, 0, 1), cx = x + lw + col * cw;
                var cell = new Rect(cx + 1 * _u, ry + 1 * _u, Math.Max(cw - 2 * _u, 0.5), Math.Max(rh - 2 * _u, 0.5));
                double cr = cw > 10 * _u && rh > 10 * _u ? 3 * _u : 0;
                dc.DrawRectangle(B(Mix(cold, hot, f > 0 ? 0.15 + 0.85 * f : 0)), null, cell, cr, cr);
                if (new Rect(cx, ry, cw, rh).Contains(_mouse))
                {
                    hR = r; hC = col;
                    dc.DrawRectangle(null, new Pen(B(Xcol(Xc.Text)), 1.5 * _u), cell, 3 * _u, 3 * _u);
                }
            }
        }
        if (hR >= 0) Tooltip(dc, [($"{c.RowL[hR]} {c.ColL[hC]}", null), ($"{Num(c.Heat[hR * hc + hC])} {c.Unit}", null)], W, H);
    }

    private static readonly uint[] ShareColors = [0x55FFFF, 0xFF55FF, 0xFFFF55, 0x55FF55, 0x5555FF, 0xFF5555, 0xFFFFFF, 0xAAAAAA];

    /// <summary>Parts of a whole as one bar, a legend with values and percents under it.</summary>
    private void ChartShare(DrawingContext dc, Chart c, double x, double y, double w)
    {
        bool bySeries = c.Series.Length > 1;
        int n = bySeries ? c.Series.Length : c.Labels.Length;
        double total = 0;
        for (int i = 0; i < n; i++) total += bySeries ? Val(c, i, 0) : Val(c, 0, i);
        if (total <= 0) return;
        double bh = 30 * _u, cx = x, uiH = LineH(Ui, 13);
        for (int i = 0; i < n; i++)
        {
            double v = bySeries ? Val(c, i, 0) : Val(c, 0, i), pw = w * v / total;
            Color col = bySeries ? c.Series[i].C : C(i < ShareColors.Length ? ShareColors[i] : 0xAAAAAA);
            if (pw > 0.5) dc.DrawRectangle(B(WithA(col, 230)), null, new Rect(cx, y, pw, bh));
            double ly = y + bh + 12 * _u + (i / 3) * (uiH + 8 * _u), lx = x + (i % 3) * (w / 3);
            dc.DrawRectangle(B(col), null, new Rect(lx, ly + 3 * _u, 10 * _u, 10 * _u), 2 * _u, 2 * _u);
            string name = bySeries ? c.Series[i].Name : c.Labels[i];
            dc.DrawText(Text($"{name}  {Num(v)}  {(100 * v / total).ToString("0", CultureInfo.InvariantCulture)}%", Ui, 13, Xcol(Xc.Text)),
                        new Point(lx + 16 * _u, ly));
            cx += pw;
        }
        dc.DrawRectangle(null, new Pen(B(WithA(Xcol(Xc.Border), 200)), 1 * _u), new Rect(x, y, w, bh), 4 * _u, 4 * _u);
    }

    private static LinearGradientBrush VFade(Color c, byte from, byte to) => new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(WithA(c, from), 0), new GradientStop(WithA(c, to), 1) },
    };

    /// <summary>One laid-out run at (x, y): its background, then its text, pulsing or shimmering where it says.</summary>
    private void DrawSeg(DrawingContext dc, Seg sg, double x, double y, Color panel)
    {
        if (sg.Bg != None && !(sg.Fx == FxBlink && (long)(Now / 500) % 2 == 1))
            dc.DrawRectangle(B(C(sg.Bg)), null, new Rect(x, y + 1 * _u, sg.W, Math.Max(sg.LineH - 2 * _u, 1)));
        double ty = y + NewsLine * _u / 2;
        Color fg = C(sg.Fg);
        if (sg.Url != null)   // a link: underlined, brighter with a solid line under the pointer, clickable
        {
            string link = sg.Text[..sg.LinkLen], rest = sg.Text[sg.LinkLen..];
            double lw = TextW(link, sg.Face, sg.Size);
            var r = new Rect(x, y, lw, sg.LineH);
            if (y >= _vTop - sg.LineH / 2 && y + sg.LineH <= _vBot + sg.LineH / 2) _linkHits.Add((r, sg.Url));
            bool on = r.Contains(_mouse);
            Color c = on ? Mix(fg, Colors.White, 0.45) : fg;
            var lt = Text(link, sg.Face, sg.Size, c);
            dc.DrawRectangle(B(on ? c : WithA(fg, 153)), null,
                             new Rect(x, ty + lt.Baseline + 1 * _u, lw, on ? Math.Max(1.5 * _u, 1) : Math.Max(1 * _u, 1)));
            dc.DrawText(lt, new Point(x, ty));
            if (rest.Length > 0) dc.DrawText(Text(rest, sg.Face, sg.Size, fg), new Point(x + lw, ty));
            return;
        }
        if (sg.Fx == FxShimmer)
        {
            double pos = (Now / 1400.0 % 1.8) * (sg.W + 60 * _u) - 30 * _u, cx = x;
            foreach (var rune in sg.Text.EnumerateRunes())
            {
                string ch = rune.ToString();
                double cw = TextW(ch, sg.Face, sg.Size);
                double glint = 1 - Math.Clamp(Math.Abs(cx + cw / 2 - x - pos) / (24 * _u), 0, 1);
                dc.DrawText(Text(ch, sg.Face, sg.Size, Mix(fg, Colors.White, glint * 0.85)), new Point(cx, ty));
                cx += cw;
            }
            return;
        }
        if (sg.Fx == FxPulse)
        {
            double t = 0.5 + 0.5 * Math.Sin(Now / 1000.0 * 3.6);
            dc.DrawText(Text(sg.Text, sg.Face, sg.Size, Mix(Mix(fg, panel, 0.5), fg, t)), new Point(x, ty));
            return;
        }
        sg.Ft ??= Text(sg.Text, sg.Face, sg.Size, fg);
        dc.DrawText(sg.Ft, new Point(x, ty));
    }

    /// <summary>One row of the stats bar, centered on cx: values bright, labels in the accent, a small square between
    /// parts.</summary>
    private void DrawStatsRow(DrawingContext dc, NewsStat[] row, double cx, double y)
    {
        double gap = 26 * _u, rowH = StatH * _u, total = 0;
        var parts = new List<(FormattedText Ft, Typeface Face, double Size, double Gap)>();
        for (int k = 0; k < row.Length; k++)
        {
            var st = row[k];
            string a = st.LabelFirst ? st.Label : st.Value, b = st.LabelFirst ? st.Value : st.Label;
            var (fa, ca) = st.LabelFirst ? (Ui, Ncol(Nc.StatLabel)) : (UiBold, Ncol(Nc.StatValue));
            var (fb, cb) = st.LabelFirst ? (UiBold, Ncol(Nc.StatValue)) : (Ui, Ncol(Nc.StatLabel));
            double before = k > 0 ? gap : 0;
            if (a.Length > 0) { parts.Add((Text(a, fa, 13, ca), fa, 13, before)); before = 0; }
            if (b.Length > 0) { parts.Add((Text(b, fb, 13, cb), fb, 13, before + (a.Length > 0 ? 5 * _u : 0))); }
        }
        foreach (var p in parts) total += p.Gap + p.Ft.WidthIncludingTrailingWhitespace;
        double x = cx - total / 2;
        bool first = true;
        foreach (var p in parts)
        {
            if (!first && p.Gap >= gap)
                dc.DrawRectangle(B(Ncol(Nc.Border)), null, new Rect(x + gap / 2 - 2.5 * _u, y + rowH / 2 - 2.5 * _u, 5 * _u, 5 * _u));
            first = false;
            x += p.Gap;
            dc.DrawText(p.Ft, new Point(x, MidY(p.Ft, p.Face, p.Size, y, rowH)));
            x += p.Ft.WidthIncludingTrailingWhitespace;
        }
    }

    private void DrawHelp(DrawingContext dc, double w, double h)
    {
        string help = Selectable(_sel) ? _items[_sel].Help : "";
        if (help.Length == 0) return;
        var t = Text(help, Ui, 13, Mcol(Mc.MenuHelp));
        t.MaxTextWidth = Math.Max(w - 40 * _u, 1);
        t.MaxLineCount = 1;
        t.Trimming = TextTrimming.CharacterEllipsis;
        double y = h - (StatusH + HelpH) * _u;
        dc.DrawText(t, new Point((w - t.Width) / 2, y + (HelpH * _u - t.Height) / 2));
    }

    private void DrawStatus(DrawingContext dc, double w, double h)
    {
        double y = h - StatusH * _u, sh = StatusH * _u;
        if (_toast.Length > 0 && DateTime.UtcNow < _toastUntil)   // the text screen's toast: the whole bar, its own colors
        {
            dc.DrawRectangle(B(C(_toastBg)), null, new Rect(0, y, w, sh));
            var tt = Text(_toast, UiBold, 13, C(_toastFg));
            dc.DrawText(tt, new Point(12 * _u, MidY(tt, UiBold, 13, y, sh)));
            return;
        }
        dc.DrawRectangle(B(Mcol(Mc.StatusBg)), null, new Rect(0, y, w, sh));
        var arrow = Text(">", UiBold, 13, Mcol(Mc.Accent));
        dc.DrawText(arrow, new Point(12 * _u, y + (sh - arrow.Height) / 2));
        var title = Text(_head.Title, Ui, 13, Mcol(Mc.StatusText));
        dc.DrawText(title, new Point(12 * _u + arrow.Width + 8 * _u, y + (sh - title.Height) / 2));
        string mins = _minutes != 0xFFFF ? $" · {_minutes}m left" : "";
        var bbs = Text(_bbs, UiBold, 13, Mcol(Mc.BarTitle));
        // the text bar's end (Rainwire's bar.version, "Rainwire"): its letters in the logo's blue-to-purple fade, as
        // the text bar draws them (render/look.go rainwireFade)
        int at = _bbs.IndexOf("Rainwire", StringComparison.Ordinal);
        for (int i = 0; at >= 0 && i < 8; i++)
            bbs.SetForegroundBrush(B(Mix(Color.FromRgb(0x5A, 0xD1, 0xFF), Color.FromRgb(0xA7, 0x8B, 0xFA), i / 7.0)), at + i, 1);
        var right = Text($"node {_node} · {(_clock.Length > 0 ? _clock : "--:--")}{mins} · ", Ui, 13, Mcol(Mc.StatusHint));
        dc.DrawText(bbs, new Point(w - 12 * _u - bbs.Width, y + (sh - bbs.Height) / 2));
        dc.DrawText(right, new Point(w - 12 * _u - bbs.Width - right.WidthIncludingTrailingWhitespace, y + (sh - right.Height) / 2));
    }
}
