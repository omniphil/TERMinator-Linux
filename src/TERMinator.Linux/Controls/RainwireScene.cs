using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using TERMinator.Linux.RainwireWm;

namespace TERMinator.Linux.Controls;

/// <summary>
/// Scenes (1.1.6; Rainwire docs/SCENES.md, "Wire format v1"): any board screen sent as a tree of general blocks
/// (panels, rows, columns, scrolls, grids, text, images, character cells, buttons, fields, choices, checks, lists) that
/// this lays out and draws in the space a menu box goes, under the header art and over the status bar. The board sends
/// a Menu head with MuSMenu.screen = 2 (ScrScene), then Scene (colors, focus, how many block records), SceneBlocks,
/// SceneCells, SceneImage, Backdrop and Show; ScenePatch changes the tree later. It never decides anything: it says
/// which button was pressed (Press;id=), a field's Enter (Submit;id=;b64=), a choice's, check's or live field's change
/// (Change;id=;...), a list's selection and Enter (Select / Activate;id=;i=), any key nothing used (Key;vk=;key=;focus=)
/// and Esc (Back). Block kinds and properties a newer board sends are skipped by their lengths; an unknown kind keeps its
/// place and shows its alt text. Layout follows the spec's "Layout": sizes in units of the menu rows' text size,
/// rows and columns as flexbox (gap, padding, grow, shrink to min, align, self, justify, wrap).
/// Linux port of the Windows client's Controls/RainwireScene.cs (the master), 2026-10-21: the same packets, layout,
/// input and replies, drawn with Avalonia (rounded rectangles through DrawRectangle's radii, clips and opacity as
/// using blocks, a cells bitmap through WmPaint).
/// </summary>
public sealed partial class RainwireMenu
{
    /// <summary>The scene protocol version this draws: the scene= key in the Rainwire:Caps answer.</summary>
    public const int SceneVersion = 6;   // version 2: RainwireSceneV2.cs, Rainwire docs/SCENES_V2.md; 3: flat boxes, SF_BLEED, the board's pick and keys with fields (Messages, Files and MRC as their modules look); 4: MRC (P_HOVERBG, P_HOVERONLY, fn=/alt=1;ch= keys, shift=1); 5: drops and drags, the viewer's whole scales, shared grid tiles, the board's scrolling, Click's Ctrl and Shift (Files: RainwireSceneFiles.cs); 6: scroll positions, dividers, click points, clocks, charts, sliders, canvases, the gadget engine (RainwireSceneV6.cs, RainwireGadgets.cs; Rainwire docs/SCENES_V6.md)

    /// <summary>The block kinds this draws: the scenekinds= key ('+' between them; Rainwire scene.KindNames).</summary>
    public const string SceneKinds =
        "panel+row+column+stack+scroll+grid+text+image+cells+rule+progress+spacer+icon+button+field+choice+check+list+item+input+vlist+menu+popover+chip+drawer+viewer+split+chart+slider+canvas";

    // ---- Rainwire internal/scene/protocol.go ----
    private const int ScrScene = 2;
    private const byte SfFull = 0x01, SfOver = 0x02, SfBleed = 0x04;   // SF_BLEED (version 2): the whole window, as a module has it
    private const byte BkPanel = 1, BkRow = 2, BkColumn = 3, BkStack = 4, BkScroll = 5, BkGrid = 6, BkText = 10, BkImage = 11,
                       BkCells = 12, BkRule = 13, BkProgress = 14, BkSpacer = 15, BkIcon = 16, BkButton = 20, BkField = 21, BkChoice = 22,
                       BkCheck = 23, BkList = 24, BkItem = 25;
    private const byte TU8 = 1, TU16 = 2, TU32 = 3, TF32 = 4, TStr = 5, TSpans = 6, TStrs = 7, TF32s = 8;
    private const byte PHidden = 1, PDisabled = 2, PTooltip = 3, PAlt = 4, PRole = 5, PWidth = 6, PHeight = 7, PGrow = 8,
                       PMinW = 9, PMinH = 10, PFg = 11, PBg = 12, PPad = 13, PSelf = 14,
                       PGap = 32, PAlign = 33, PJustify = 34, PWrap = 35, PTitle = 36, PAxis = 37, PFollow = 38, PColumns = 39,
                       PTileW = 40, PTileH = 41, PAuto = 42,
                       PSpans = 64, PText = 65, PLines = 66, PTAlign = 67, PFit = 68, PSmooth = 69, PCols = 70, PRows = 71,
                       PValue = 72, PLabel = 73, PKey = 74, PSubmit = 75, PPlace = 76, PMax = 77, PSecret = 78, PMulti = 79,
                       PLive = 80, PValueStr = 81, POptions = 82, PIndex = 83, PChecked = 84, PWidths = 85, PHeaders = 86,
                       PCellText = 87, PIFlags = 88, PIcon = 89;
    private const byte AStart = 0, ACenter = 1, AEnd = 2, AStretch = 3;
    private const byte JStart = 0, JCenter = 1, JEnd = 2, JBetween = 3;
    private const byte FitContain = 0, FitCover = 1, FitStretch = 2, FitNone = 3;
    private const byte IfBold = 1, IfDim = 2, IfHeading = 4;
    private const byte OpSet = 1, OpReplace = 2, OpRemove = 3, OpInsert = 4, OpFocus = 5;
    private const byte RText = 0, RTitle = 1, RHeading = 2, RDim = 3, RAccent = 4, RMono = 5, RSmall = 6, RGood = 7, RBad = 8,
                       RPanel = 9, RButton = 10, RHot = 11;

    /// <summary>The Scene packet's color slots (C_*); a newer board's extra slots are skipped.</summary>
    private enum Sc { Bg, Panel, Border, Title, Text, Dim, Accent, Accent2, ButtonBg, ButtonFg, ButtonHot, FieldBg, FieldFg, Focus, SelBg, SelFg, Good, Bad, Count }

    /// <summary>The spec's limits ("Limits and security"): more and the scene is refused (Closed;error=), so the board
    /// falls back to text. Cells are capped here too: they become one bitmap of 8 x the font's height pixels a cell.</summary>
    private const int SceneMaxBlocks = 5000, SceneMaxDepth = 32, SceneMaxBytes = 2 << 20;
    private const int CellsMaxCols = 400, CellsMaxRows = 2000, CellsMax = 65536, FieldMulti = 4000;

    /// <summary>One block: its kind, id, parent and properties (only known keys of the right type are kept), its
    /// children in order, what the caller changed (a field's text, a choice's or list's index, a check), and where the
    /// last layout put it.</summary>
    private sealed class SBlock
    {
        public byte Kind;
        public ushort Id, ParentId;
        public SBlock? Parent;
        public int Depth, Bytes;
        public readonly Dictionary<byte, byte[]> Props = [];
        public readonly List<SBlock> Kids = [];

        // the caller's side of a control
        public string Value = "";
        public int Cur, Index;
        public bool On;

        // a scroll's, a list's or a multi-line field's place
        public double Scroll, ScrollMax, View;
        public bool AtEnd = true, ShowSel = true;
        public DateTime AutoHold;   // P_AUTO: still until then (at the top, at the end, after the wheel)
        public bool AutoEnded;      // P_AUTO: it reached the end and holds there before starting over

        // version 2 (RainwireSceneV2.cs)
        public List<SBlock?>? Rows;                  // a vlist's rows by index (null: not here yet)
        public List<double> RowY = [], RowH = [];    // a vlist's rows' places and heights, as last laid out
        public double RowsW = -1;                    // ... for this width
        public int AnchorRow = -1;                   // a vlist's first row in view, kept still when rows come above it
        public double AnchorOff;                     // ... its distance from the view's top
        public int NewCount;                         // a vlist's rows added at the end while scrolled back
        public readonly HashSet<int> Sel = [];       // a vlist's selected rows
        public int SelAnchor = -1, SelCur = -1;      // ... the range anchor and the current row
        public DateTime SelAt, SeenAt;               // ... when the selection, the view last moved (settle)
        public bool SelSent = true, SeenSent = true; // ... reported since
        public (int From, int To) Asked = (-1, -1);  // a vlist's last Rows request
        public DateTime AskedAt, OlderAt;
        public byte Anim;                            // P_ANIM / OpAnimate playing, and when it started
        public DateTime AnimAt;
        public double AnimMs;
        public bool Closed;                          // a pop-up or drawer the caller closed (until the board sends it again)
        public bool Open;                            // a context menu or a button's menu the caller opened, or a drawer
        public Point OpenAt;                         // ... where (a context menu: the pointer)
        public List<string> History = [];            // an input's submitted values (P_HISTORY), newest last
        public int HistAt = -1;                      // ... where Up/Down are; -1 the draft
        public string Draft = "";
        public DateTime ChangedAt, SavedAt;          // an input's last edit, and last autosave
        public bool ChangePending;                   // ... a debounced Change not sent yet
        public int CompleteAt = -1;                  // ... where the word being completed starts

        // a cells block's rows, and the bitmap they're painted into
        public readonly List<SCell[]> Cells = [];
        public Bitmap? Bitmap;
        public bool CellsDirty = true;

        public Bitmap? Image;

        // version 6 (RainwireSceneV6.cs)
        public double SplitAt = -1;                  // a split's share the caller dragged it to (-1: the board's)
        public Rect SplitBar;                        // ... its divider, as laid out
        public (double Pos, double Max, double View) RepCur = (double.NaN, 0, 0);   // P_REPORTSCROLL: the view now
        public DateTime RepAt;                       // ... when it last moved
        public bool RepPending;                      // ... a Scrolled not sent yet
        public string ClockText = "";                // P_CLOCK: the text it shows now
        public uint TimerFor;                        // ... the P_AT its Timer was sent for
        public double Num = double.NaN;              // a slider's value as the caller has it (NaN: P_VALUE)
        public double NumSent = double.NaN;          // ... the last one sent
        public DateTime NumAt;                       // ... when
        public List<(byte Op, byte[] Pay)>? Draw;    // a canvas's commands (SceneDraw)
        public int DrawBytes, DrawAssets = -1;       // ... their size; the assets there were when it was built
        public List<Action<DrawingContext>>? DrawCache;   // ... built once into drawing steps, in canvas coordinates
                                                          //     (Windows: a frozen DrawingGroup; replayed here)
        public List<(int Tag, Func<Point, bool> Hit)>? DrawTags;   // ... its tagged shapes' hit tests, topmost last
        public Rect PicRect;                         // where a cells block's, image's or viewer's picture was drawn
        public Rect Plot;                            // a chart's plot area, as drawn
        public List<(int Start, int End)>? VLines;   // a multi-line field's or input's drawn (wrapped) lines
        public string WrapV = "";                    // ... the text, width and font they were wrapped for
        public double WrapW = -1, WrapSize;
        public Typeface? WrapFace;

        // the layout: the pass that placed it (an older one: hidden now), its box, inside its padding, what of it shows
        public int Pass = -1;
        public Rect Box, Inner, Clip;
        public bool Off;   // disabled, or inside a disabled block
        public int MGen = -1, MInfGen = -1, NGen = -1, FtGen = -1, FtNatGen = -1;
        public double MW, NW, FtW, NMin;
        public Size MSize, MInf;
        public FormattedText? Ft, FtNat;
        public readonly List<(int Start, int Len, Span S)> Runs = [];
        public readonly List<(int Start, int Len, string Url)> Links = [];   // version 2: link spans
        public readonly List<(Rect Box, int I)> Hits = [];   // drawn this frame: a choice's pills, a list's rows
        public double TextX, TextY, LineH;                     // a field's text origin and line height, drawn this frame

        public bool Has(byte k) => Props.ContainsKey(k);

        public int U(byte k, int def = 0)
        {
            if (!Props.TryGetValue(k, out var v)) return def;
            return v.Length switch { 1 => v[0], 2 => v[0] | v[1] << 8, 4 => (int)(BitConverter.ToUInt32(v, 0) & 0x7FFFFFFF), _ => def };
        }

        public uint U32(byte k) => Props.TryGetValue(k, out var v) && v.Length == 4 ? BitConverter.ToUInt32(v, 0) : None;

        public double F(byte k, double def = 0)
        {
            if (!Props.TryGetValue(k, out var v) || v.Length != 4) return def;
            float f = BitConverter.ToSingle(v, 0);
            return float.IsFinite(f) ? f : def;
        }

        public bool Flag(byte k) => U(k) != 0;

        public string S(byte k) => Props.TryGetValue(k, out var v) ? Encoding.UTF8.GetString(v) : "";

        public string[] Strs(byte k)
        {
            if (!Props.TryGetValue(k, out var v) || v.Length < 1) return [];
            var l = new List<string>();
            int o = 1;
            for (int i = 0; i < v[0] && o < v.Length; i++)
            {
                int n = v[o++];
                if (o + n > v.Length) break;
                l.Add(Encoding.UTF8.GetString(v, o, n));
                o += n;
            }
            return [.. l];
        }

        public double[] F32s(byte k)
        {
            if (!Props.TryGetValue(k, out var v) || v.Length < 1) return [];
            int n = Math.Min(v[0], (v.Length - 1) / 4);
            var l = new double[n];
            for (int i = 0; i < n; i++)
            {
                float f = BitConverter.ToSingle(v, 1 + 4 * i);
                l[i] = float.IsFinite(f) ? f : 0;
            }
            return l;
        }

        public Span[] Spans(byte k)
        {
            if (!Props.TryGetValue(k, out var v) || v.Length < 1) return [];
            var l = new List<Span>();
            int o = 1;
            for (int i = 0; i < v[0] && o + 10 <= v.Length; i++)
            {
                uint fg = BitConverter.ToUInt32(v, o), bg = BitConverter.ToUInt32(v, o + 4);
                byte fx = v[o + 8];
                int n = Math.Min(v[o + 9], v.Length - o - 10);
                l.Add(new Span(fg, bg, fx, Encoding.UTF8.GetString(v, o + 10, n)));
                o += 10 + n;
            }
            return [.. l];
        }
    }

    /// <summary>A character cell: a CP437 code and its colors (0x00RRGGBB; None: not set).</summary>
    private readonly record struct SCell(byte Ch, uint Fg, uint Bg);

    /// <summary>A whole scene: its head, the tree under Top (a column of the roots), and its last layout.</summary>
    private sealed class SceneTree
    {
        public byte Flags;
        public ushort Focus;
        public readonly uint[] Colors = new uint[(int)Sc.Count];
        public int Want, Got, Count, Bytes, Pass;
        public readonly SBlock Top = new() { Kind = BkColumn };
        public readonly Dictionary<ushort, SBlock> ById = [];
        public SceneTree? Under;                       // SF_OVER: the scene it's over, drawn dimmed behind it
        public readonly List<SBlock> Order = [];       // the blocks the layout placed, in the tree's order
        public double Scroll, ScrollMax;               // the whole scene's, when it can't fit even shrunk
        public Rect Space;
        public (Rect Space, double U, int Gen, int Arr) LaidFor = (default, 0, -1, -1);
        public readonly List<SBar> Bars = [];          // version 6: the scroll bars drawn this frame (RainwireSceneV6.cs)
        public (double W, double H) SizeCur = (-1, -1);   // version 6: SF_SIZE, the space now and when it changed
        public DateTime SizeAt;
        public bool SizePending;
        public DateTime AutoTick = DateTime.UtcNow;    // P_AUTO's last step
        public readonly List<SBlock> Pops = [];        // pop-ups and drawers open, bottom first (RainwireSceneV2.cs)
    }

    private bool _isScene;
    private SceneTree? _scene, _scenePrev;
    private int _sceneGen, _sceneArr;    // bumped when sizes must be measured again / blocks placed again
    private double _sceneU, _sceneSize, _sceneDpi;
    private DateTime _sceneWaitAt, _sceneMoveAt;
    private ushort _scenePressed;        // the button whose press the board is answering
    private WmFont? _cellFont;
    private bool _cellFontTried;
    private readonly Dictionary<string, Bitmap> _sceneImages = [];
    private uint[] _sc = new uint[(int)Sc.Count];   // the colors of the tree being laid out or drawn

    // ================================================================
    // Messages from the board
    // ================================================================

    /// <summary>The type each known property must have (anything else is ignored); 0: a key this doesn't know.</summary>
    private static byte KeyType(byte k) => k switch
    {
        PHidden or PDisabled or PRole or PSelf or PAlign or PJustify or PWrap or PAxis or PFollow or PColumns or PLines
            or PTAlign or PFit or PSmooth or PSubmit or PSecret or PMulti or PLive or PChecked or PIFlags or PAuto or PIcon => TU8,
        PCols or PRows or PMax or PIndex => TU16,
        PFg or PBg or PKey => TU32,
        PWidth or PHeight or PGrow or PMinW or PMinH or PPad or PGap or PTileW or PTileH or PValue => TF32,
        PTooltip or PAlt or PTitle or PText or PLabel or PPlace or PValueStr => TStr,
        PSpans => TSpans,
        POptions or PHeaders or PCellText => TStrs,
        PWidths => TF32s,
        _ => KeyTypeV2(k),   // version 2's (RainwireSceneV2.cs)
    };

    /// <summary>Properties from p[o..end]: repeated u8 key, u8 type, u16 len, value. Unknown keys and known ones of
    /// another type (or the wrong length) are skipped by their length; false when the list runs past its end.</summary>
    private static bool ReadProps(byte[] p, int o, int end, Dictionary<byte, byte[]> into)
    {
        end = Math.Min(end, p.Length);
        while (o < end)
        {
            if (o + 4 > end) return false;
            byte k = p[o], t = p[o + 1];
            int len = U16(p, o + 2);
            o += 4;
            if (o + len > end) return false;
            byte want = KeyType(k);
            bool fits = t switch { TU8 => len == 1, TU16 => len == 2, TU32 or TF32 => len == 4, _ => true };
            if (want != 0 && want == t && fits) into[k] = p[o..(o + len)];
            o += len;
        }
        return true;
    }

    /// <summary>n block records from p[o..end] (u8 kind, u16 id, u16 parent, u16 len, properties); a short one ends it.</summary>
    private static List<SBlock> ReadRecords(byte[] p, int o, int end, int n)
    {
        end = Math.Min(end, p.Length);
        var list = new List<SBlock>();
        for (int i = 0; i < n; i++)
        {
            if (o + 7 > end) break;
            var b = new SBlock { Kind = p[o], Id = (ushort)U16(p, o + 1), ParentId = (ushort)U16(p, o + 3) };
            int len = U16(p, o + 5);
            o += 7;
            if (o + len > end || !ReadProps(p, o, o + len, b.Props)) break;
            b.Bytes = len + 7;
            o += len;
            list.Add(b);
        }
        return list;
    }

    /// <summary>The colors until the Scene packet says (a board that sends fewer slots): the menu's.</summary>
    private void SceneDefaultColors(uint[] c)
    {
        uint M(Mc i) => _head.Colors[(int)i];
        c[(int)Sc.Bg] = M(Mc.Bg);
        c[(int)Sc.Panel] = M(Mc.MenuBg);
        c[(int)Sc.Border] = M(Mc.MenuBorder);
        c[(int)Sc.Title] = M(Mc.PanelTitle);
        c[(int)Sc.Text] = M(Mc.Text);
        c[(int)Sc.Dim] = M(Mc.Dim);
        c[(int)Sc.Accent] = M(Mc.Accent);
        c[(int)Sc.Accent2] = M(Mc.Accent2);
        c[(int)Sc.ButtonBg] = M(Mc.MenuBg);
        c[(int)Sc.ButtonFg] = M(Mc.MenuLabel);
        c[(int)Sc.ButtonHot] = M(Mc.SelBg);
        c[(int)Sc.FieldBg] = M(Mc.MenuBg);
        c[(int)Sc.FieldFg] = M(Mc.Text);
        c[(int)Sc.Focus] = M(Mc.SelMarker);
        c[(int)Sc.SelBg] = M(Mc.SelBg);
        c[(int)Sc.SelFg] = M(Mc.SelFg);
        c[(int)Sc.Good] = 0x55FF55;
        c[(int)Sc.Bad] = 0xFF5555;
    }

    /// <summary>A scene's head (Scene, type 19): version, flags, the block records to come, the block focused first,
    /// the colors by slot, the status bar's title. The scene shows once every record has come.</summary>
    private void OnScene(byte[] p)
    {
        if (!_isScene || p.Length < 12) return;   // a scene only follows its Menu head
        var s = SceneHeadRead(p, out string title);
        if (title.Length > 0) _head.Title = title;
        _waiting = false;
        _complete = false;
        _cellFont = null;          // the terminal's font may have changed since the last scene
        _cellFontTried = false;
        if (s.Want > SceneMaxBlocks)
        {
            SceneTooBig();
            return;
        }
        if ((s.Flags & SfOver) != 0 && _scenePrev != null)
        {
            s.Under = _scenePrev;   // a pop-up over the scene before it
            s.Under.Under = null;
        }
        _scenePrev = null;
        _scene = s;
        _sceneGen++;
        if (s.Want == 0) SceneReady();
    }

    /// <summary>A Scene packet's tree, empty: its flags, how many records come, the block focused first, its colors.</summary>
    private SceneTree SceneHeadRead(byte[] p, out string title)
    {
        var s = new SceneTree { Flags = p[5], Want = U16(p, 6), Focus = (ushort)U16(p, 8) };
        int colors = p[10], titleLen = p[11], o = 12;
        SceneDefaultColors(s.Colors);
        for (int i = 0; i < colors && o + 4 <= p.Length; i++, o += 4)
            if (i < (int)Sc.Count) s.Colors[i] = U32(p, o) & 0xFFFFFF;
        title = Str(p, ref o, titleLen);
        return s;
    }

    /// <summary>Block records (SceneBlocks, type 20), in order: each goes under its parent (one that came before), or
    /// is a root (parent 0); a record whose parent isn't there is left out.</summary>
    private void OnSceneBlocks(byte[] p)
    {
        var s = _scene;
        if (s == null || !_isScene) return;
        int r = SceneBlocksInto(s, p);
        if (r < 0) SceneTooBig();
        else if (r > 0) SceneReady();
    }

    /// <summary>SceneBlocks into tree s (a screen's or a gadget's): -1 over the limits, 1 when every record has come,
    /// else 0.</summary>
    private static int SceneBlocksInto(SceneTree s, byte[] p)
    {
        if (p.Length < 8) return 0;
        int first = U16(p, 4), count = U16(p, 6);
        if (first != s.Got || s.Got >= s.Want) return 0;   // out of order: a stale scene's
        foreach (var b in ReadRecords(p, 8, p.Length, Math.Min(count, s.Want - s.Got)))
            if (!SceneAdd(s, b, -1)) return -1;
        s.Got = Math.Min(s.Got + count, s.Want);
        return s.Got >= s.Want ? 1 : 0;
    }

    /// <summary>Character cells (SceneCells, type 21) for a cells block: rows from firstRow, each runs of one color pair
    /// (u16 len, u32 fg, u32 bg, len CP437 codes). A piece arriving, or rows of it changing.</summary>
    private void OnSceneCells(byte[] p)
    {
        if (_scene != null && _isScene) SceneCellsInto(_scene, p);
    }

    /// <summary>SceneCells into tree s (a screen's or a gadget's).</summary>
    private void SceneCellsInto(SceneTree s, byte[] p)
    {
        if (p.Length < 10) return;
        int first = U16(p, 6), rows = U16(p, 8), o = 10;
        if (!s.ById.TryGetValue((ushort)U16(p, 4), out var b) || b.Kind != BkCells) b = null;
        var before = b != null ? CellDims(b) : default;
        for (int r = 0; r < rows && o + 2 <= p.Length; r++)
        {
            int runs = U16(p, o);
            o += 2;
            var row = new List<SCell>();
            for (int i = 0; i < runs && o + 10 <= p.Length; i++)
            {
                int n = U16(p, o);
                uint fg = U32(p, o + 2), bg = U32(p, o + 6);
                o += 10;
                n = Math.Min(n, p.Length - o);
                for (int k = 0; k < n && row.Count < CellsMaxCols; k++) row.Add(new SCell(p[o + k], fg, bg));
                o += n;
            }
            int at = first + r;
            if (b == null || at >= CellsMaxRows) continue;
            while (b.Cells.Count <= at) b.Cells.Add([]);
            b.Cells[at] = [.. row];
        }
        if (b == null) return;
        b.CellsDirty = true;
        if (CellDims(b) != before) _sceneGen++;   // its size changed: lay out again
        Redraw();
    }

    /// <summary>An image block's picture (SceneImage, type 22): the picture-cache file the message's hdr= named.</summary>
    private void OnSceneImage(byte[] p, string? path)
    {
        if (_scene != null && _isScene) SceneImageInto(_scene, p, path);
    }

    /// <summary>SceneImage into tree s (a screen's or a gadget's).</summary>
    private void SceneImageInto(SceneTree s, byte[] p, string? path)
    {
        if (p.Length < 6) return;
        if (!s.ById.TryGetValue((ushort)U16(p, 4), out var b) || b.Kind != BkImage) return;
        b.Image = path != null ? SceneLoadImage(path) : null;
        _sceneGen++;
        Redraw();
    }

    /// <summary>A picture from the cache, kept while the scene's blocks may name it again.</summary>
    private Bitmap? SceneLoadImage(string path)
    {
        if (_sceneImages.TryGetValue(path, out var had)) return had;
        try
        {
            var bitmap = new Bitmap(Path.GetFullPath(path));
            if (_sceneImages.Count >= 64) _sceneImages.Clear();
            _sceneImages[path] = bitmap;
            return bitmap;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"RainwireMenu: scene image {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Changes to the scene (ScenePatch, type 23): set, replace, remove, insert, focus; the board has answered,
    /// so the caller may act again.</summary>
    private void OnScenePatch(byte[] p)
    {
        var s = _scene;
        if (s == null || !_isScene || p.Length < 6) return;
        _waiting = false;
        _scenePressed = 0;
        if (!ScenePatchInto(s, p)) SceneTooBig();
    }

    /// <summary>ScenePatch into tree s (a screen's or a gadget's); false when it takes s over the limits.</summary>
    private bool ScenePatchInto(SceneTree s, byte[] p)
    {
        if (p.Length < 6) return true;
        int n = U16(p, 4), o = 6;
        for (int i = 0; i < n && o + 5 <= p.Length; i++)
        {
            byte op = p[o];
            ushort id = (ushort)U16(p, o + 1);
            int len = U16(p, o + 3);
            o += 5;
            if (o + len > p.Length) break;
            if (!ScenePatchOne(s, op, id, p, o, len)) return false;
            o += len;
        }
        _sceneGen++;
        Redraw();
        return true;
    }

    /// <summary>One patch record; false when it takes the scene over its limits. Unknown ops are skipped.</summary>
    private bool ScenePatchOne(SceneTree s, byte op, ushort id, byte[] p, int o, int len)
    {
        int end = o + len;
        switch (op)
        {
            case OpSet:
            {
                if (!s.ById.TryGetValue(id, out var b)) return true;
                var props = new Dictionary<byte, byte[]>();
                ReadProps(p, o, end, props);
                foreach (var (k, v) in props)
                {
                    int was = b.Props.TryGetValue(k, out var old) ? old.Length + 4 : 0;
                    b.Props[k] = v;
                    b.Bytes += v.Length + 4 - was;
                    s.Bytes += v.Length + 4 - was;
                }
                if (props.ContainsKey(PValueStr))
                {
                    b.Value = b.S(PValueStr);
                    b.Cur = b.Value.Length;
                }
                if (props.ContainsKey(PIndex))
                {
                    b.Index = b.U(PIndex);
                    b.ShowSel = true;
                    if (b.Kind == BkVList) VSetSel(b, b.Index);   // version 2: the board picks a row (Messages' click opens it)
                }
                if (props.ContainsKey(PChecked)) b.On = b.Flag(PChecked);
                if (props.ContainsKey(PCols) || props.ContainsKey(PRows)) b.CellsDirty = true;
                if (props.TryGetValue(PScrollBy, out var by)) SceneScrollBy(s, b, by);   // version 5: the board's keys
                if (props.ContainsKey(PSplitAt)) b.SplitAt = -1;                        // version 6: the board moves a divider
                if (props.ContainsKey(PValue)) b.Num = double.NaN;                      // ... or a slider
                if (props.ContainsKey(PData) || props.ContainsKey(PData2)) b.Plot = default;
                return s.Bytes <= SceneMaxBytes;
            }
            case OpRemove:
                if (s.ById.TryGetValue(id, out var gone)) SceneRemove(s, gone);
                return true;
            case OpReplace:
            case OpInsert:
            {
                int q = o, index = -1;
                if (op == OpInsert)
                {
                    if (q + 2 > end) return true;
                    int ix = U16(p, q);
                    if (ix != 0xFFFF) index = ix;
                    q += 2;
                }
                if (q + 2 > end) return true;
                var bs = ReadRecords(p, q + 2, end, U16(p, q));
                if (bs.Count == 0) return true;
                if (op == OpReplace)
                {
                    if (!s.ById.TryGetValue(id, out var old)) return true;
                    bs[0].ParentId = old.Parent == null || old.Parent == s.Top ? (ushort)0 : old.Parent.Id;
                    index = (old.Parent ?? s.Top).Kids.IndexOf(old);
                    SceneRemove(s, old);
                }
                else
                {
                    bs[0].ParentId = id;
                }
                if (!SceneAdd(s, bs[0], index)) return false;
                for (int i = 1; i < bs.Count; i++)
                    if (!SceneAdd(s, bs[i], -1)) return false;
                return true;
            }
            case OpFocus:
                s.Focus = id;
                if (s.ById.TryGetValue(id, out var f)) SceneIntoView(s, f);
                return true;
            case OpRows: case OpScrollTo: case OpAnimate:
                return ScenePatchV2(s, op, id, p, o, len);   // version 2 (RainwireSceneV2.cs)
        }
        return true;
    }

    /// <summary>Puts b under its parent at index (-1: last); false when the scene goes over its limits.</summary>
    private static bool SceneAdd(SceneTree s, SBlock b, int index)
    {
        SBlock par = s.Top;
        if (b.ParentId != 0)
        {
            if (!s.ById.TryGetValue(b.ParentId, out var found)) return true;   // no such parent: left out
            par = found;
        }
        b.Parent = par;
        b.Depth = par == s.Top ? 1 : par.Depth + 1;
        if (b.Depth > SceneMaxDepth) return false;
        if (index < 0 || index >= par.Kids.Count) par.Kids.Add(b);
        else par.Kids.Insert(index, b);
        if (b.Id != 0) s.ById[b.Id] = b;
        s.Count++;
        s.Bytes += b.Bytes;
        b.Value = b.S(PValueStr);
        b.Cur = b.Value.Length;
        b.Index = b.U(PIndex);
        if (b.Kind == BkVList && b.Has(PIndex)) VSetSel(b, b.Index);
        b.On = b.Flag(PChecked);
        return s.Count <= SceneMaxBlocks && s.Bytes <= SceneMaxBytes;
    }

    /// <summary>Takes b and its descendants out of the tree.</summary>
    private static void SceneRemove(SceneTree s, SBlock b)
    {
        void Drop(SBlock x)
        {
            if (x.Id != 0 && s.ById.TryGetValue(x.Id, out var y) && y == x) s.ById.Remove(x.Id);
            s.Count--;
            s.Bytes -= x.Bytes;
            foreach (var k in x.Kids) Drop(k);
        }
        Drop(b);
        b.Parent?.Kids.Remove(b);
    }

    private void SceneReady()
    {
        _caretAt = DateTime.UtcNow;
        _sceneGen++;
        NewsReady();
        Redraw();   // Windows' NewsReady redraws; this one doesn't
    }

    /// <summary>Over the spec's limits: refused, so the board shows the screen's text version.</summary>
    private void SceneTooBig()
    {
        _scene = null;
        _complete = false;
        Reply("Closed;error=scene too big");
        Redraw();
    }

    // ================================================================
    // Layout (docs/SCENES.md "Layout"): measure (natural sizes, cached per block while nothing changed), then place
    // ================================================================

    /// <summary>The unit: the menu rows' text size (rows.size), in DIPs.</summary>
    /// <summary>The scene's text size: the menu rows' (rows.size), times _sScale (gadgets draw smaller,
    /// RainwireWidgets.cs).</summary>
    private double SSize => _st.Size * _sScale;
    private double _sScale = 1;
    private double SU => SSize * _u;
    private double SmallSize => SSize * 0.85;
    private double Units(SBlock b, byte k) => Math.Max(b.F(k), 0) * SU;
    private double FieldLineH() => LineH(Ui, SSize) + 2 * _u;
    private Color Scol(Sc i) => C(_sc[(int)i]);
    private static Color? Ucol(SBlock b, byte k) => b.Has(k) && (b.U32(k) & 0xFF000000) == 0 ? C(b.U32(k)) : null;

    private static Rect R(double x, double y, double w, double h) => new(x, y, Math.Max(w, 0), Math.Max(h, 0));
    private static Rect Inset(Rect r, double d) => R(r.X + d, r.Y + d, r.Width - 2 * d, r.Height - 2 * d);
    private static Rect CenterIn(Rect r, double w, double h) => R(r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2, w, h);

    /// <summary>What a and b share, at least where they meet (Avalonia's Intersect gives 0,0,0,0 when they don't, which
    /// would take a pointer at the window's corner).</summary>
    private static Rect Isect(Rect a, Rect b)
    {
        double x = Math.Max(a.X, b.X), y = Math.Max(a.Y, b.Y);
        return R(x, y, Math.Min(a.Right, b.Right) - x, Math.Min(a.Bottom, b.Bottom) - y);
    }

    /// <summary>a and b touch or overlap (WPF's IntersectsWith; Avalonia's Intersects leaves out a box with no size).</summary>
    private static bool Overlaps(Rect a, Rect b) => a.X <= b.Right && b.X <= a.Right && a.Y <= b.Bottom && b.Y <= a.Bottom;

    private static bool Known(byte k) => k is (>= BkPanel and <= BkGrid) or (>= BkText and <= BkIcon) or (>= BkButton and <= BkItem) or (>= BkInput and <= BkViewer)
        or (>= BkSplit and <= BkCanvas);   // version 6
    private static bool IsControl(byte k) => k is BkButton or BkField or BkChoice or BkCheck or BkList or BkInput or BkVList or BkSlider;

    /// <summary>A block's children that take space: not hidden, not a list's items.</summary>
    private static List<SBlock> VisibleKids(SBlock b)
    {
        var l = new List<SBlock>(b.Kids.Count);
        foreach (var k in b.Kids)
            if (!k.Flag(PHidden) && k.Kind != BkItem && !IsPop(k.Kind) && !k.Flag(PHoverShow) && WidthOk(k)
                && !(k.Kind == BkChip && b.Kind == BkInput)) l.Add(k);   // v2: pop-ups, hover toolbars, an input's chips
        return l;
    }

    /// <summary>Padding inside a block: a panel's 1 unit unless it says; controls keep their own.</summary>
    private double BlockPad(SBlock b) => b.Kind switch
    {
        BkPanel => (b.Has(PPad) ? Math.Max(b.F(PPad), 0) : 1) * SU,
        BkButton or BkField or BkChoice or BkCheck or BkList or BkItem or BkInput or BkVList or BkChip or BkSlider or BkSplit => 0,
        _ => Units(b, PPad),
    };

    /// <summary>A rule across its box (in a column), or down it (in a row).</summary>
    private static bool RuleAcross(SBlock b) => b.Parent?.Kind != BkRow;

    /// <summary>b's natural size when at most maxW wide (text wraps to fit; fixed sizes and minimums apply).</summary>
    private Size SMeasure(SBlock b, double maxW)
    {
        if (b.Flag(PHidden)) return default;
        bool inf = double.IsPositiveInfinity(maxW);
        if (inf && b.MInfGen == _sceneGen) return b.MInf;
        if (!inf && b.MGen == _sceneGen && Math.Abs(b.MW - maxW) < 0.25) return b.MSize;
        double fw = Units(b, PWidth), fh = Units(b, PHeight), pad = BlockPad(b);
        double inner = Math.Max((fw > 0 ? fw : maxW) - 2 * pad, 0);
        var c = Natural(b, inner, fh > 0 ? Math.Max(fh - 2 * pad, 0) : -1);
        double w = fw > 0 ? fw : c.Width + 2 * pad, h = fh > 0 ? fh : c.Height + 2 * pad;
        var size = new Size(Math.Max(w, Units(b, PMinW)), Math.Max(h, Units(b, PMinH)));
        if (inf) { b.MInfGen = _sceneGen; b.MInf = size; }
        else { b.MGen = _sceneGen; b.MW = maxW; b.MSize = size; }
        return size;
    }

    /// <summary>The content's natural size inside the padding.</summary>
    private Size Natural(SBlock b, double inner, double fixH)
    {
        double u = SU;
        switch (b.Kind)
        {
            case BkPanel:   // its children, and room for the title on its border
            {
                var s = FlexNatural(b, inner, false);
                string title = b.S(PTitle);
                double tw = title.Length > 0 && _st.Title != 2 ? TextW(title, UiBold, 17) + 40 * _u - 2 * BlockPad(b) : 0;
                return new Size(Math.Min(Math.Max(s.Width, tw), Math.Max(inner, s.Width)), s.Height);
            }
            case BkColumn: return FlexNatural(b, inner, false);
            case BkRow: return FlexNatural(b, inner, true);
            case BkStack:
            {
                double w = 0, h = 0;
                foreach (var k in VisibleKids(b))
                {
                    var s = SMeasure(k, inner);
                    w = Math.Max(w, s.Width);
                    h = Math.Max(h, s.Height);
                }
                return new Size(w, h);
            }
            case BkScroll:
            {
                var kid = VisibleKids(b).FirstOrDefault();
                if (kid == null) return default;
                return b.U(PAxis) == 1 ? SMeasure(kid, double.PositiveInfinity) : SMeasure(kid, inner);
            }
            case BkGrid:
            {
                int n = VisibleKids(b).Count;
                if (n == 0) return default;
                var (cols, tw, th) = GridDims(b, inner, n);
                int rows = (n + cols - 1) / cols;
                double gap = Units(b, PGap);
                return new Size(cols * tw + gap * (cols - 1), rows * th + gap * (rows - 1));
            }
            case BkImage: case BkCells: return PicNatural(b, inner, fixH);
            case BkRule: return RuleAcross(b) ? new Size(0, u) : new Size(u, 0);
            case BkProgress:
            {
                string label = b.S(PLabel);
                double lw = label.Length > 0 ? TextW(label, Ui, SmallSize) + 0.6 * u : 0;
                return new Size(12 * u + lw, Math.Max(0.8 * u, lw > 0 ? LineH(Ui, SmallSize) : 0));
            }
            case BkSpacer: case BkItem: return default;
            case BkIcon: return new Size(1.5 * u, 1.5 * u);   // P_WIDTH / P_HEIGHT size it
            case BkMenu: case BkPopover: case BkDrawer: return FlexNatural(b, inner, false);   // a pop-up, measured for itself
            case BkInput: case BkVList: case BkViewer: case BkChip: return NaturalV2(b, inner, fixH);
            case BkSplit: case BkChart: case BkSlider: case BkCanvas: return NaturalV6(b, inner, fixH);   // version 6
            case BkButton: return new Size(ButtonTextW(b) + 2 * ButtonPadX(b), 1.6 * u);
            case BkField: return new Size(16 * u, b.Flag(PMulti) ? 1.6 * u + 3 * FieldLineH() : 1.6 * u);
            case BkChoice:
            {
                var places = PillPlaces(b, inner);
                double w = 0, h = 1.6 * u;
                foreach (var r in places)
                {
                    w = Math.Max(w, r.Right);
                    h = Math.Max(h, r.Bottom + 0.1 * u);
                }
                return new Size(w, h);
            }
            case BkCheck: return new Size(1.5 * u + TextW(b.S(PLabel), Ui, SSize), 1.5 * u);
            case BkList:
            {
                double[] ws = b.F32s(PWidths);
                int cols = Math.Max(ws.Length, Math.Max(b.Strs(PHeaders).Length, 1));
                double fix = 0;
                bool flex = ws.Length < cols;
                foreach (var x in ws) { if (x > 0) fix += x * u; else flex = true; }
                double head = b.Strs(PHeaders).Length > 0 ? 1.5 * u : 0;
                return new Size(Math.Max(fix + (flex ? 10 * u : 0), 12 * u) + 0.6 * u + 10 * _u, head + 10 * 1.5 * u + 0.6 * u);
            }
            case BkText:
            default:   // a kind this doesn't know: its alt text in its place
                return TextNatural(b, inner);
        }
    }

    /// <summary>A row's or column's natural size: a column's widest child and their heights; a row's children's natural
    /// widths (shrunk when they don't fit, then their heights at those widths), or its lines when it wraps.</summary>
    private Size FlexNatural(SBlock b, double inner, bool horiz)
    {
        var kids = VisibleKids(b);
        if (kids.Count == 0) return default;
        double gap = Units(b, PGap);
        if (!horiz)
        {
            double w = 0, h = 0;
            foreach (var k in kids)
            {
                var s = SMeasure(k, inner);
                w = Math.Max(w, s.Width);
                h += s.Height;
            }
            return new Size(w, h + gap * (kids.Count - 1));
        }
        if (b.Flag(PWrap) && !double.IsInfinity(inner))
        {
            var lines = WrapLines(kids, inner, gap);
            double w = 0, h = 0;
            foreach (var line in lines)
            {
                var (ws, nat, _) = RowWidths(line, inner, gap);
                double lh = 0;
                for (int i = 0; i < line.Count; i++) lh = Math.Max(lh, SMeasure(line[i], ws[i]).Height);
                w = Math.Max(w, nat <= inner ? nat : ws.Sum() + gap * (line.Count - 1));
                h += lh;
            }
            return new Size(w, h + gap * (lines.Count - 1));
        }
        var (widths, natural, _) = RowWidths(kids, inner, gap);
        double rh = 0;
        for (int i = 0; i < kids.Count; i++) rh = Math.Max(rh, SMeasure(kids[i], widths[i]).Height);
        return new Size(natural <= inner ? natural : widths.Sum() + gap * (kids.Count - 1), rh);
    }

    /// <summary>Children's widths across inner: each its P_WIDTH or natural width; spare room shared by P_GROW; short of
    /// room, the growing ones shrink first (to P_MINW), then the rest, each to its minimum. Natural is their sum before.</summary>
    private (double[] Sizes, double Natural, bool Grew) RowWidths(List<SBlock> kids, double inner, double gap)
    {
        int n = kids.Count;
        var size = new double[n];
        var min = new double[n];
        for (int i = 0; i < n; i++)
        {
            var k = kids[i];
            double fw = FracW(k, inner - gap * Math.Max(n - 1, 0)) ?? Units(k, PWidth);   // version 3: a share of the row
            size[i] = fw > 0 ? fw : SMeasure(k, double.PositiveInfinity).Width;
            if (!double.IsFinite(size[i])) size[i] = 0;   // never let one child's size poison the row's sums
            min[i] = fw > 0 ? fw : MinW(k, size[i]);
            if (!double.IsFinite(min[i])) min[i] = 0;
        }
        double natural = size.Sum() + gap * Math.Max(n - 1, 0);
        bool grew = false;
        if (double.IsInfinity(inner)) return (size, natural, false);
        double spare = inner - natural;
        if (spare > 0)
        {
            double g = 0;
            foreach (var k in kids) g += Math.Max(k.F(PGrow), 0);
            if (g > 0)
            {
                for (int i = 0; i < n; i++) size[i] += spare * Math.Max(kids[i].F(PGrow), 0) / g;
                grew = true;
            }
        }
        else if (spare < 0)
        {
            Shrink(size, min, -spare, i => kids[i].F(PGrow) > 0);
            double left = size.Sum() + gap * Math.Max(n - 1, 0) - inner;
            if (left > 0) Shrink(size, min, left, _ => true);
        }
        return (size, natural, grew);
    }

    /// <summary>Takes over off the sizes picked, each in proportion to its room above its minimum.</summary>
    private static void Shrink(double[] size, double[] min, double over, Func<int, bool> pick)
    {
        double room = 0;
        for (int i = 0; i < size.Length; i++)
            if (pick(i) && size[i] > min[i]) room += size[i] - min[i];
        if (room <= 0) return;
        double take = Math.Min(over, room);
        for (int i = 0; i < size.Length; i++)
            if (pick(i) && size[i] > min[i]) size[i] -= (size[i] - min[i]) * take / room;
    }

    /// <summary>How narrow a block may get in a row that runs short of room (nat: its natural width).</summary>
    private double MinW(SBlock k, double nat)
    {
        if (k.Has(PMinW)) return Units(k, PMinW);
        if (Units(k, PWidth) > 0) return nat;
        double u = SU, pad = BlockPad(k);
        switch (k.Kind)
        {
            case BkText: return Math.Min(nat, 6 * u);
            case BkButton: case BkCheck: case BkRule: return nat;
            case BkChoice: case BkField: case BkProgress: case BkImage: case BkCells: case BkScroll: case BkVList: case BkViewer:
            case BkInput: case BkChart: case BkCanvas: case BkSlider:
                return Math.Min(nat, 3 * u);
            case BkSplit: return Math.Min(nat, 2 * MinPane(k) + 0.6 * u);
            case BkList: return Math.Min(nat, 8 * u);
            case BkRow:
            {
                var kids = VisibleKids(k);
                double gap = Units(k, PGap), sum = 0, most = 0;
                foreach (var c in kids)
                {
                    double m = MinW(c, Units(c, PWidth) > 0 ? Units(c, PWidth) : SMeasure(c, double.PositiveInfinity).Width);
                    sum += m;
                    most = Math.Max(most, m);
                }
                return Math.Min(nat, (k.Flag(PWrap) ? most : sum + gap * Math.Max(kids.Count - 1, 0)) + 2 * pad);
            }
            case BkPanel: case BkColumn: case BkStack:
            {
                double most = 0;
                foreach (var c in VisibleKids(k))
                    most = Math.Max(most, MinW(c, Units(c, PWidth) > 0 ? Units(c, PWidth) : SMeasure(c, double.PositiveInfinity).Width));
                return Math.Min(nat, most + 2 * pad);
            }
            case BkGrid: return Math.Min(nat, GridDims(k, double.PositiveInfinity, 1).Tw + 2 * pad);
            default: return 0;
        }
    }

    /// <summary>How short a block may get in a column that runs short of room, w wide (nat: its natural height):
    /// scrolls, lists and pictures give way; containers as their children do; text and controls don't.</summary>
    private double MinH(SBlock k, double w, double nat)
    {
        if (k.Has(PMinH)) return Units(k, PMinH);
        if (Units(k, PHeight) > 0) return nat;
        if (k.NGen == _sceneGen && Math.Abs(k.NW - w) < 0.25) return Math.Min(k.NMin, nat);
        double u = SU, pad = BlockPad(k), iw = Math.Max(w - 2 * pad, 0), m = nat;
        switch (k.Kind)
        {
            case BkScroll: m = Math.Min(nat, 3 * u); break;
            case BkSplit: case BkChart: case BkCanvas: m = Math.Min(nat, 2 * u); break;   // version 6
            case BkList: m = Math.Min(nat, 6 * u); break;
            case BkImage: case BkCells: m = Math.Min(nat, 2 * u); break;
            case BkField: if (k.Flag(PMulti)) m = Math.Min(nat, 1.6 * u); break;
            case BkPanel: case BkColumn:
            {
                var kids = VisibleKids(k);
                double h = 0;
                foreach (var c in kids)
                {
                    double cw = Units(c, PWidth) > 0 ? Units(c, PWidth) : iw;
                    h += MinH(c, cw, SMeasure(c, cw).Height);
                }
                m = Math.Min(nat, h + Units(k, PGap) * Math.Max(kids.Count - 1, 0) + 2 * pad);
                break;
            }
            case BkRow when !k.Flag(PWrap):
            {
                var kids = VisibleKids(k);
                var (ws, _, _) = RowWidths(kids, iw, Units(k, PGap));
                double h = 0;
                for (int i = 0; i < kids.Count; i++) h = Math.Max(h, MinH(kids[i], ws[i], SMeasure(kids[i], ws[i]).Height));
                m = Math.Min(nat, h + 2 * pad);
                break;
            }
            case BkStack:
            {
                double h = 0;
                foreach (var c in VisibleKids(k)) h = Math.Max(h, MinH(c, iw, SMeasure(c, iw).Height));
                m = Math.Min(nat, h + 2 * pad);
                break;
            }
        }
        k.NGen = _sceneGen;
        k.NW = w;
        k.NMin = m;
        return m;
    }

    /// <summary>A row that wraps: its children in lines, each as many as fit w at their natural widths.</summary>
    private List<List<SBlock>> WrapLines(List<SBlock> kids, double w, double gap)
    {
        var lines = new List<List<SBlock>> { new() };
        double x = 0;
        foreach (var k in kids)
        {
            double kw = Units(k, PWidth) > 0 ? Units(k, PWidth) : Math.Min(SMeasure(k, double.PositiveInfinity).Width, w);
            if (lines[^1].Count > 0 && x + gap + kw > w)
            {
                lines.Add([]);
                x = 0;
            }
            x += (lines[^1].Count > 0 ? gap : 0) + kw;
            lines[^1].Add(k);
        }
        return lines;
    }

    /// <summary>A grid's tiles a row and a tile's size: P_TILEW x P_TILEH units (8 x 6 by default, or the width shared
    /// by P_COLUMNS), P_COLUMNS a row or as many as fit w.</summary>
    private (int Cols, double Tw, double Th) GridDims(SBlock b, double w, int n)
    {
        double u = SU, gap = Units(b, PGap), tw = Units(b, PTileW), th = Units(b, PTileH);
        if (GridShared(b, w) is { } shared) return shared;   // version 5: the gallery's tiles (RainwireSceneFiles.cs)
        int cols = b.U(PColumns);
        bool inf = double.IsInfinity(w);
        if (tw <= 0) tw = cols > 0 && !inf ? Math.Max((w - gap * (cols - 1)) / cols, u) : 8 * u;
        if (th <= 0) th = Units(b, PTileW) > 0 || cols == 0 ? 6 * u * tw / (8 * u) : tw * 0.75;
        if (cols <= 0) cols = inf ? Math.Clamp(n, 1, 4) : Math.Max(1, (int)((w + gap) / (tw + gap)));
        return (cols, tw, th);
    }

    /// <summary>An image's or cells block's natural size: its pixels (cells: 8 x 16 a cell) at the window's scale, its
    /// shape kept, made smaller to fit maxW (or as tall as a fixed height).</summary>
    private Size PicNatural(SBlock b, double maxW, double fixH)
    {
        var (pw, ph) = PicPixels(b);
        if (pw <= 0 || ph <= 0) return default;
        double w = pw * _u, h = ph * _u;
        if (fixH > 0)
        {
            w *= fixH / h;
            h = fixH;
        }
        if (w > maxW)
        {
            h *= maxW / w;
            w = maxW;
        }
        return new Size(w, h);
    }

    private (double W, double H) PicPixels(SBlock b)
    {
        if (b.Kind == BkCells)
        {
            var (cols, rows) = CellDims(b);
            return (cols * 8, rows * 16);
        }
        var img = b.Image ?? Asset(b);   // version 2: an image may name an asset (P_ASSET) instead of carrying one
        return img != null ? (img.PixelSize.Width, img.PixelSize.Height) : (0, 0);
    }

    /// <summary>A cells block's size in cells: P_COLS x P_ROWS, or the rows sent if more, within the caps.</summary>
    private static (int Cols, int Rows) CellDims(SBlock b)
    {
        int cols = b.U(PCols), rows = Math.Max(b.U(PRows), b.Cells.Count);
        foreach (var r in b.Cells) cols = Math.Max(cols, r.Length);
        cols = Math.Min(cols, CellsMaxCols);
        rows = cols > 0 ? Math.Min(rows, Math.Min(CellsMaxRows, CellsMax / cols)) : 0;
        return (cols, rows);
    }

    private bool TextEmpty(SBlock b) =>
        b.Kind == BkText ? b.Spans(PSpans).Length == 0 && b.S(PText).Length == 0 && !b.Has(PClock) : b.S(PAlt).Length == 0;

    /// <summary>A text's natural size: its longest line, or wrapped to maxW; its lines' height.</summary>
    private Size TextNatural(SBlock b, double maxW)
    {
        if (double.IsNaN(maxW) || maxW < 0) maxW = 0;   // FormattedText throws on a NaN width
        if (TextEmpty(b)) return b.Kind == BkText ? new Size(0, TextFt(b, double.PositiveInfinity).Height) : default;
        var ft = TextFt(b, double.PositiveInfinity);
        double nw = Math.Ceiling(ft.WidthIncludingTrailingWhitespace) + 1;
        if (nw <= maxW) return new Size(nw, ft.Height);
        ft = TextFt(b, maxW);
        return new Size(Math.Min(Math.Ceiling(ft.WidthIncludingTrailingWhitespace) + 1, maxW), ft.Height);
    }

    /// <summary>A text's font, size and color by its role (docs/SCENES.md "Colors and roles"), P_FG over it.</summary>
    private (Typeface Face, double Size, Color Fg) TextLook(SBlock b)
    {
        double size = SSize;
        Typeface face = Ui;
        Color fg = Known(b.Kind) ? Scol(Sc.Text) : Scol(Sc.Dim);
        switch (b.U(PRole))
        {
            case RTitle: fg = Scol(Sc.Title); face = UiBold; size *= 1.3; break;
            case RHeading: fg = Scol(Sc.Accent); face = UiBold; break;
            case RDim: fg = Scol(Sc.Dim); break;
            case RAccent: fg = Scol(Sc.Accent); break;
            case RMono: face = MonoReg; break;
            case RSmall: fg = Scol(Sc.Dim); size *= 0.85; break;
            case RGood: fg = Scol(Sc.Good); break;
            case RBad: fg = Scol(Sc.Bad); break;
        }
        if (Ucol(b, PFg) is Color c) fg = c;
        if (b.Flag(PMono)) face = MonoReg;   // version 2
        return (face, size, fg);
    }

    /// <summary>A role's color for a rule or a progress bar's fill (def when it has none).</summary>
    private Color RoleColor(SBlock b, Color def)
    {
        if (!b.Has(PRole)) return def;
        return b.U(PRole) switch
        {
            RTitle => Scol(Sc.Title), RHeading or RAccent => Scol(Sc.Accent), RDim or RSmall => Scol(Sc.Dim),
            RGood => Scol(Sc.Good), RBad => Scol(Sc.Bad), RText or RMono => Scol(Sc.Text), _ => def,
        };
    }

    /// <summary>A text block (or an unknown kind's alt text) as one FormattedText: its spans' colors set on their
    /// ranges, wrapped at w (infinite: one line a '\n'), at most P_LINES lines with an ellipsis, aligned by P_TALIGN.
    /// Kept (one natural, one wrapped) while nothing changed.</summary>
    private FormattedText TextFt(SBlock b, double w)
    {
        bool inf = double.IsPositiveInfinity(w);
        if (inf && b.FtNatGen == _sceneGen && b.FtNat != null) return b.FtNat;
        if (!inf && b.FtGen == _sceneGen && b.Ft != null && Math.Abs(b.FtW - w) < 0.25) return b.Ft;
        var (face, size, fg) = TextLook(b);
        bool clock = b.Kind == BkText && b.Has(PClock);   // version 6: the client's own text (RainwireSceneV6.cs)
        var spans = b.Kind == BkText && !clock ? b.Spans(PSpans) : Array.Empty<Span>();
        b.Runs.Clear();
        string s;
        if (spans.Length > 0)
        {
            var sb = new StringBuilder();
            b.Links.Clear();
            foreach (var sp0 in spans)
            {
                var sp = sp0;
                if (sp.Fx == FxLink && sp.Text.IndexOf('\u0001') is int cut and >= 0)   // version 2: a link span
                {
                    b.Links.Add((sb.Length, cut, sp.Text[(cut + 1)..]));
                    sp = sp with { Text = sp.Text[..cut], Fx = 0 };
                }
                b.Runs.Add((sb.Length, sp.Text.Length, sp));
                sb.Append(sp.Text);
            }
            s = sb.ToString();
        }
        else
        {
            s = clock ? ClockString(b) : b.Kind == BkText ? b.S(PText) : b.S(PAlt);
        }
        var ft = Text(s.Length > 0 ? s : " ", face, size, fg);
        if (!inf)
        {
            ft.MaxTextWidth = double.IsNaN(w) ? 1 : Math.Max(w, 1);   // Math.Max passes NaN through
            ft.TextAlignment = b.U(PTAlign) switch { 1 => TextAlignment.Center, 2 => TextAlignment.Right, _ => TextAlignment.Left };
        }
        int lines = b.U(PLines);
        if (lines > 0)
        {
            ft.MaxLineCount = lines;
            ft.Trimming = TextTrimming.CharacterEllipsis;
        }
        foreach (var (start, len, sp) in b.Runs)
            if (len > 0 && (sp.Fg & 0xFF000000) == 0) ft.SetForegroundBrush(B(C(sp.Fg)), start, len);
        if (b.Has(PGrad) && (b.U32(PGrad) & 0xFF000000) == 0 && b.Runs.Count == 0)   // version 2: gradient text
            ft.SetForegroundBrush(HGrad(fg, C(b.U32(PGrad))));
        if (inf) { b.FtNat = ft; b.FtNatGen = _sceneGen; }
        else { b.Ft = ft; b.FtGen = _sceneGen; b.FtW = w; }
        return ft;
    }

    private double ButtonPadX(SBlock b) => (b.Has(PPad) ? Math.Max(b.F(PPad), 0) : 0.6) * SU;

    private double ButtonTextW(SBlock b)
    {
        var spans = b.Spans(PSpans);
        return spans.Length > 0 ? spans.Sum(sp => TextW(sp.Text, UiBold, SSize)) : TextW(b.S(PLabel), UiBold, SSize);
    }

    /// <summary>A choice's pills, from its top left, wrapped at w.</summary>
    private List<Rect> PillPlaces(SBlock b, double w)
    {
        double u = SU, x = 0, y = 0.1 * u, ph = 1.4 * u, gap = 0.4 * u;
        var list = new List<Rect>();
        foreach (var o in b.Strs(POptions))
        {
            double pw = TextW(o, Ui, SSize) + 1.2 * u;
            if (x > 0 && x + pw > w)
            {
                x = 0;
                y += ph + gap;
            }
            list.Add(new Rect(x, y, pw, ph));
            x += pw + gap;
        }
        return list;
    }

    /// <summary>A whole-window scene is up (SF_BLEED): the menu's header, gadgets and status bar make way.</summary>
    private bool SceneBleeds => _complete && _isScene && _scene is SceneTree t && (t.Flags & SfBleed) != 0;

    /// <summary>The scene's space: where a menu box goes, under the header art and over the status bar, less a unit.</summary>
    private Rect SceneSpace(double w, double h, double hdrBottom)
    {
        if (SceneBleeds) return R(0, 0, w, h);   // no header, no status bar, no margin: Messages, Files, MRC
        double top = hdrBottom, bottom = h - StatusH * _u;
        if (bottom - top < 120 * _u) top = Math.Max(bottom - 120 * _u, 0);
        return Inset(R(0, top, w, bottom - top), SU);
    }

    /// <summary>The whole scene placed in space: as wide as it (SF_OVER: its natural size, centered), its natural height
    /// placed as the menu box is (box.valign; SF_FULL: all of it). Too tall even with every scroll, list and picture at
    /// its minimum, the scene itself scrolls.</summary>
    private void LayoutScene(SceneTree s, Rect space)
    {
        s.Pass++;
        s.Order.Clear();
        s_sceneW = space.Width / Math.Max(_u, 0.01);   // P_WMIN/P_WMAX are in layout units (the modules' lp)
        s.Space = space;
        var top = s.Top;
        double sw = space.Width, sh = space.Height;
        bool over = (s.Flags & SfOver) != 0, full = (s.Flags & SfFull) != 0 && !over;
        var nat = SMeasure(top, sw);
        double w = over ? Math.Min(nat.Width, sw) : sw;
        if (w != sw) nat = SMeasure(top, w);
        double h = full ? sh : Math.Min(nat.Height, sh);
        s.ScrollMax = 0;
        if (nat.Height > sh)
        {
            double m = MinH(top, w, nat.Height);
            if (m > sh)
            {
                h = m;
                s.ScrollMax = m - sh;
            }
        }
        s.Scroll = Math.Clamp(s.Scroll, 0, s.ScrollMax);
        double x = space.X + (sw - w) / 2, y;
        if (s.ScrollMax > 0) y = space.Y - s.Scroll;
        else if (over || _st.VAlign is 0 or 2) y = space.Y + (sh - h) / 2;
        else y = _st.VAlign == 1 ? space.Y : space.Y + sh - h;
        var clip = s.ScrollMax > 0 ? space : Inset(space, -2 * SU);
        SArrange(s, top, R(x, y, w, h), clip, false);
        LayoutPops(s, space);   // version 2: menus, popovers and drawers over it, last (so they're hit first)
    }

    /// <summary>Places b in r (its clip: what of it can show) and its children in it.</summary>
    private void SArrange(SceneTree s, SBlock b, Rect r, Rect clip, bool off)
    {
        b.Pass = s.Pass;
        b.Box = r;
        b.Clip = clip;
        b.Off = off || b.Flag(PDisabled);
        s.Order.Add(b);
        var inner = Inset(r, BlockPad(b));
        b.Inner = inner;
        switch (b.Kind)
        {
            case BkPanel: case BkColumn: ArrangeFlex(s, b, inner, false, clip); break;
            case BkRow: ArrangeFlex(s, b, inner, true, clip); break;
            case BkStack:
            {
                int align = b.Has(PAlign) ? b.U(PAlign) : AStretch;
                foreach (var k in VisibleKids(b))
                {
                    int a = k.Has(PSelf) ? k.U(PSelf) : align;
                    if (a == AStretch)
                    {
                        SArrange(s, k, inner, clip, b.Off);
                        continue;
                    }
                    var m = SMeasure(k, inner.Width);
                    double kw = Math.Min(m.Width, inner.Width), kh = Math.Min(m.Height, inner.Height), f = a == ACenter ? 0.5 : a == AEnd ? 1 : 0;
                    SArrange(s, k, R(inner.X + (inner.Width - kw) * f, inner.Y + (inner.Height - kh) * f, kw, kh), clip, b.Off);
                }
                break;
            }
            case BkScroll: ArrangeScroll(s, b, inner, clip); break;
            case BkSplit: ArrangeSplit(s, b, inner, clip); break;   // version 6
            case BkVList: ArrangeVList(s, b, inner, clip); break;
            case BkGrid:
            {
                var kids = VisibleKids(b);
                double gap = Units(b, PGap);
                var (cols, tw, th) = GridDims(b, inner.Width, kids.Count);
                for (int i = 0; i < kids.Count; i++)
                    SArrange(s, kids[i], R(inner.X + i % cols * (tw + gap), inner.Y + i / cols * (th + gap), tw, th), clip, b.Off);
                break;
            }
        }
        // version 2: hover-only blocks (P_HOVERSHOW) sit over the top-right of their hover root, out of the flow
        foreach (var k in b.Kids)
        {
            if (!k.Flag(PHoverShow) || k.Flag(PHidden)) continue;
            var m = SMeasure(k, double.PositiveInfinity);
            SArrange(s, k, R(r.Right - m.Width - 0.4 * SU, r.Y - m.Height * 0.45, m.Width, m.Height), Inset(clip, -SU), b.Off);
        }
    }

    /// <summary>A row's or column's children along it (wrapping into lines when a row may).</summary>
    private void ArrangeFlex(SceneTree s, SBlock b, Rect inner, bool horiz, Rect clip)
    {
        var kids = VisibleKids(b);
        if (kids.Count == 0) return;
        double gap = Units(b, PGap);
        int align = b.Has(PAlign) ? b.U(PAlign) : AStretch, just = b.U(PJustify);
        if (horiz && b.Flag(PWrap))
        {
            double y = inner.Y;
            foreach (var line in WrapLines(kids, inner.Width, gap))
            {
                var (ws, _, grew) = RowWidths(line, inner.Width, gap);
                double lh = 0;
                for (int i = 0; i < line.Count; i++)
                    lh = Math.Max(lh, Units(line[i], PHeight) > 0 ? Units(line[i], PHeight) : SMeasure(line[i], ws[i]).Height);
                PlaceLine(s, b, line, ws, R(inner.X, y, inner.Width, lh), true, align, just, gap, grew, clip);
                y += lh + gap;
            }
            return;
        }
        if (horiz)
        {
            var (ws, _, grew) = RowWidths(kids, inner.Width, gap);
            PlaceLine(s, b, kids, ws, inner, true, align, just, gap, grew, clip);
            return;
        }
        // a column: each child's width first (stretched, or its own), then the heights along it
        int n = kids.Count;
        var size = new double[n];
        var min = new double[n];
        for (int i = 0; i < n; i++)
        {
            var k = kids[i];
            int a = k.Has(PSelf) ? k.U(PSelf) : align;
            double cw = Units(k, PWidth) > 0 ? Units(k, PWidth) : a == AStretch ? inner.Width : Math.Min(SMeasure(k, inner.Width).Width, inner.Width);
            double fh = Units(k, PHeight);
            size[i] = fh > 0 ? fh : SMeasure(k, cw).Height;
            min[i] = fh > 0 ? fh : MinH(k, cw, size[i]);
        }
        double spare = inner.Height - size.Sum() - gap * (n - 1);
        bool grow = false;
        // SF_FULL: the scene's own blocks fill the space (they sat at their natural height, the Wall's list short)
        bool fill = b == s.Top && (s.Flags & SfFull) != 0 && (s.Flags & SfOver) == 0;
        double Gr(SBlock k) => fill && !k.Has(PGrow) ? 1 : Math.Max(k.F(PGrow), 0);
        if (spare > 0)
        {
            double g = 0;
            foreach (var k in kids) g += Gr(k);
            if (g > 0)
            {
                for (int i = 0; i < n; i++) size[i] += spare * Gr(kids[i]) / g;
                grow = true;
            }
        }
        else if (spare < 0)
        {
            Shrink(size, min, -spare, i => kids[i].F(PGrow) > 0);
            double left = size.Sum() + gap * (n - 1) - inner.Height;
            if (left > 0) Shrink(size, min, left, _ => true);
        }
        PlaceLine(s, b, kids, size, inner, false, align, just, gap, grow, clip);
    }

    /// <summary>Children along r with their main sizes: P_JUSTIFY places them when nothing grew; across, P_ALIGN (a
    /// child's P_SELF over it) stretches them or puts them at the start, center or end.</summary>
    private void PlaceLine(SceneTree s, SBlock b, List<SBlock> kids, double[] main, Rect r, bool horiz, int align, int just,
                           double gap, bool grew, Rect clip)
    {
        int n = kids.Count;
        double len = horiz ? r.Width : r.Height, pos = horiz ? r.X : r.Y, extra = 0;
        double spare = len - main.Sum() - gap * (n - 1);
        if (!grew && spare > 0)
            switch (just)
            {
                case JCenter: pos += spare / 2; break;
                case JEnd: pos += spare; break;
                case JBetween: if (n > 1) extra = spare / (n - 1); break;
            }
        double cross = horiz ? r.Height : r.Width;
        for (int i = 0; i < n; i++)
        {
            var k = kids[i];
            int a = k.Has(PSelf) ? k.U(PSelf) : align;
            double fixedCross = Units(k, horiz ? PHeight : PWidth), cs;
            if (fixedCross > 0) cs = fixedCross;
            else if (a == AStretch) cs = cross;
            else cs = Math.Min(horiz ? SMeasure(k, main[i]).Height : SMeasure(k, cross).Width, cross);
            double co = a == ACenter ? (cross - cs) / 2 : a == AEnd ? cross - cs : 0;
            SArrange(s, k, horiz ? R(pos, r.Y + co, main[i], cs) : R(r.X + co, pos, cs, main[i]), clip, b.Off);
            pos += main[i] + gap + extra;
        }
    }

    /// <summary>A scroll's one child at its natural size along the axis (at least the view), moved by the scroll and
    /// clipped to the view; P_FOLLOW keeps it at the end while it was there.</summary>
    private void ArrangeScroll(SceneTree s, SBlock b, Rect view, Rect clip)
    {
        var kid = VisibleKids(b).FirstOrDefault();
        b.ScrollMax = 0;
        if (kid == null) return;
        var c = Isect(clip, view);
        double bar = 10 * _u;
        if (b.U(PAxis) == 1)
        {
            double cw = SMeasure(kid, double.PositiveInfinity).Width;
            b.ScrollMax = Math.Max(cw - view.Width, 0);
            b.View = view.Width;
            FollowClamp(b);
            ScrollPosApply(b);   // version 6: P_SCROLLPOS
            SArrange(s, kid, R(view.X - b.Scroll, view.Y, Math.Max(cw, view.Width), view.Height - (b.ScrollMax > 0 ? bar : 0)), c, b.Off);
            return;
        }
        double ch = SMeasure(kid, view.Width).Height;
        if (ch > view.Height) ch = SMeasure(kid, Math.Max(view.Width - bar, 0)).Height;
        else bar = 0;
        b.ScrollMax = Math.Max(ch - view.Height, 0);
        b.View = view.Height;
        FollowClamp(b);
        ScrollPosApply(b);   // version 6: P_SCROLLPOS
        // on whole device pixels, so what's in it moves as one (each line snapped on its own steps separately)
        double sc = VisualRoot?.RenderScaling ?? 1, sy = Math.Round((view.Y - b.Scroll) * sc) / sc;
        SArrange(s, kid, R(view.X, sy, view.Width - bar, Math.Max(ch, view.Height)), c, b.Off);
    }

    /// <summary>P_AUTO scrolls crawl by themselves (a news ticker): P_AUTO units a second at 1x, still while the
    /// pointer is over them or just after the wheel; at the end they hold, then start over at the top.</summary>
    private void AutoScroll(SceneTree s)
    {
        var now = DateTime.UtcNow;
        double dt = Math.Min((now - s.AutoTick).TotalSeconds, 0.25);
        s.AutoTick = now;
        foreach (var b in s.Order)
        {
            if (b.Kind != BkScroll || !b.Has(PAuto) || b.Pass != s.Pass || b.ScrollMax <= 0 || now < b.AutoHold) continue;
            if (b.Box.Contains(_mouse) && b.Clip.Contains(_mouse)) continue;
            if (b.Scroll >= b.ScrollMax - 0.5)
            {
                if (!b.AutoEnded)
                {
                    b.AutoEnded = true;
                    b.AutoHold = now.AddSeconds(2.5);
                }
                else
                {
                    b.AutoEnded = false;
                    SetScroll(b, 0);
                    b.AutoHold = now.AddSeconds(1.5);
                }
                continue;
            }
            SetScroll(b, b.Scroll + b.U(PAuto) * _u * dt);
        }
    }

    private static void FollowClamp(SBlock b)
    {
        if (b.Flag(PFollow) && b.AtEnd) b.Scroll = b.ScrollMax;
        b.Scroll = Math.Clamp(b.Scroll, 0, b.ScrollMax);
    }

    private void SetScroll(SBlock b, double v)
    {
        b.Scroll = Math.Clamp(v, 0, b.ScrollMax);
        b.AnchorRow = -1;   // a vlist scrolled by hand: no anchor row until it's laid out again
        b.AtEnd = b.Scroll >= b.ScrollMax - 1;
        _sceneArr++;
    }

    /// <summary>Scrolls b's scrolls (and the scene) so b shows: the focus moved to it.</summary>
    private void SceneIntoView(SceneTree s, SBlock b)
    {
        if (b.Pass != s.Pass) return;
        for (var a = b.Parent; a != null; a = a.Parent)
        {
            if (a.Kind != BkScroll || a.Pass != s.Pass || a.ScrollMax <= 0 || a.U(PAxis) == 1) continue;
            if (b.Box.Top < a.Inner.Top) SetScroll(a, a.Scroll - (a.Inner.Top - b.Box.Top));
            else if (b.Box.Bottom > a.Inner.Bottom) SetScroll(a, a.Scroll + Math.Min(b.Box.Bottom - a.Inner.Bottom, b.Box.Top - a.Inner.Top));
        }
        if (s.ScrollMax > 0)
        {
            if (b.Box.Top < s.Space.Top) s.Scroll = Math.Max(s.Scroll - (s.Space.Top - b.Box.Top), 0);
            else if (b.Box.Bottom > s.Space.Bottom) s.Scroll = Math.Min(s.Scroll + b.Box.Bottom - s.Space.Bottom, s.ScrollMax);
            _sceneArr++;
        }
    }

    // ================================================================
    // Drawing
    // ================================================================

    /// <summary>The scene in its space: SF_OVER over the scene before it, dimmed; the tooltip of what's under the pointer.</summary>
    private void DrawScene(DrawingContext dc, double w, double h, double hdrBottom)
    {
        var s = _scene;
        if (s == null) return;
        if (_sceneU != _u || _sceneSize != _st.Size || _sceneDpi != SceneDpi)
        {
            _sceneU = _u;
            _sceneSize = _st.Size;
            _sceneDpi = SceneDpi;
            _sceneGen++;
        }
        var space = SceneSpace(w, h, hdrBottom);
        SceneSizeCheck(s, space);   // version 6: SF_SIZE
        IDisposable? faded = null;
        try
        {
            if ((s.Flags & SfOver) != 0)
            {
                if (s.Under != null) DrawTree(dc, s.Under, space);
                dc.DrawRectangle(B(Color.FromArgb(110, 0, 0, 0)), null, R(0, 0, w, h - StatusH * _u));
            }
            faded = dc.PushOpacity(FadeIn());
            DrawTree(dc, s, space);
        }
        catch (Exception ex)
        {
            // A scene this client can't lay out or draw (the scene under an SF_OVER one too): closed once, so the
            // board falls back (the module or the text screen), instead of the same exception on every frame.
            Debug.WriteLine("RainwireMenu: scene draw failed: " + ex);
            _scene = null;
            _complete = false;
            Reply("Closed;error=scene draw " + ex.GetType().Name);
            Avalonia.Threading.Dispatcher.UIThread.Post(Redraw);
        }
        finally
        {
            faded?.Dispose();   // the opacity popped only if it was pushed
        }
        _sc = s.Colors;
        SceneTooltip(dc, s, w, h);
        DrawSceneDrop(dc);        // version 5: a drop target lit, a row's drag label (RainwireSceneFiles.cs)
        DrawSceneDrag(dc, w, h);
    }

    private void DrawTree(DrawingContext dc, SceneTree s, Rect space)
    {
        _sc = s.Colors;
        s.Bars.Clear();   // version 6: drawn again below, for dragging
        AutoScroll(s);
        SceneTimers(s);   // version 2: debounced changes, autosave, settled selections, hover asks
        SceneTimers6(s);  // version 6: clocks, countdowns, scroll positions (RainwireSceneV6.cs)
        var key = (space, _u, _sceneGen, _sceneArr);
        if (s.LaidFor != key)
        {
            LayoutScene(s, space);
            s.LaidFor = key;
        }
        if (s.ScrollMax > 0)
        {
            using (dc.PushClip(space))
                DrawBlock(dc, s, s.Top, space);
            SScrollbar(dc, Inset(space, -0.5 * SU), s.Scroll, s.ScrollMax, false, s, null);
        }
        else
        {
            DrawBlock(dc, s, s.Top, Inset(space, -2 * SU));
        }
        DrawPops(dc, s);   // version 2: menus, popovers and drawers over everything
    }

    /// <summary>One block and what's in it; a disabled one dim. Anything outside clip isn't drawn.</summary>
    private void DrawBlock(DrawingContext dc, SceneTree s, SBlock b, Rect clip)
    {
        if (b.Pass != s.Pass || !Overlaps(b.Box, clip) || IsPop(b.Kind) || HoverHidden(b)) return;
        var pushed = AnimPush(dc, b);   // version 2: P_ANIM and OpAnimate
        DrawDecor(dc, b);
        // an input inside the board's own box (P_STROKE none) isn't dimmed when off: the board's box shows its state
        bool dimmed = b.Flag(PDisabled) && !(b.Kind == BkInput && b.Has(PStroke) && Ucol(b, PStroke) == null);
        DrawingContext.PushedState? dim = dimmed ? dc.PushOpacity(0.45) : null;
        if (Ucol(b, PBg) is Color bg && b.Kind is not (BkPanel or BkButton or BkField or BkChoice or BkCheck or BkList or BkProgress
                                                      or BkRow or BkColumn or BkStack or BkGrid))   // those: DrawFlat (corners, gradient, wash)
            dc.DrawRectangle(B(bg), null, b.Box);
        switch (b.Kind)
        {
            case BkPanel:
                DrawSPanel(dc, b);
                DrawKids(dc, s, b, clip);
                break;
            case BkRow: case BkColumn: case BkStack: case BkGrid:
                DrawFlat(dc, b);   // version 2: a flat box under them, when it has one
                DrawKids(dc, s, b, clip);
                break;
            case BkScroll:
            {
                var c = Isect(clip, b.Inner);
                if (!IsEmpty(c))
                    using (dc.PushClip(c))
                        DrawKids(dc, s, b, c);
                SScrollbar(dc, b.Inner, b.Scroll, b.ScrollMax, b.U(PAxis) == 1, s, b);
                if (b.Flag(PPane) && Focused(s, b)) DrawPaneFocus(dc, b);   // version 6: a pane clicked into
                break;
            }
            case BkText: DrawSText(dc, b); break;
            case BkImage:
                if ((b.Image ?? Asset(b)) is Bitmap img && img.PixelSize.Width > 0 && img.PixelSize.Height > 0)
                    b.PicRect = DrawFitted(dc, img, img.PixelSize.Width, img.PixelSize.Height, b.Inner, b.U(PFit), b.Flag(PSmooth));
                break;
            case BkCells:
            {
                var (cols, rows) = CellDims(b);
                if (CellsBitmap(b) is Bitmap bmp) b.PicRect = DrawFitted(dc, bmp, cols * 8, rows * 16, b.Inner, b.U(PFit), b.Flag(PSmooth));
                break;
            }
            case BkRule:
            {
                var r = b.Box;
                Color c = Ucol(b, PFg) ?? (b.Has(PRole) ? RoleColor(b, Scol(Sc.Border)) : WithA(Scol(Sc.Border), 160));
                double t = Math.Max(1 * _u, 1);
                dc.DrawRectangle(B(c), null, RuleAcross(b) ? R(r.X, r.Y + (r.Height - t) / 2, r.Width, t) : R(r.X + (r.Width - t) / 2, r.Y, t, r.Height));
                break;
            }
            case BkProgress: DrawSProgress(dc, b); break;
            case BkSpacer: case BkItem: break;
            case BkIcon:   // the client's own icons (RainwireWidgets.cs DrawIcon): its color P_FG, else its role's
                DrawIcon(dc, b.U(PIcon), Ucol(b, PFg) ?? RoleColor(b, Scol(Sc.Text)), b.Inner);
                break;
            case BkButton: DrawSButton(dc, s, b); break;
            case BkField: DrawSField(dc, s, b); break;
            case BkChoice: DrawSChoice(dc, s, b); break;
            case BkCheck: DrawSCheck(dc, s, b); break;
            case BkList: DrawSList(dc, s, b); break;
            case BkInput: case BkVList: case BkChip: case BkViewer: DrawV2(dc, s, b, clip); break;
            case BkSplit: DrawSplit(dc, s, b, clip); break;   // version 6 (RainwireSceneV6.cs)
            case BkChart: DrawChart(dc, b); break;
            case BkSlider: DrawSlider(dc, s, b); break;
            case BkCanvas: DrawCanvas(dc, b); break;
            default: DrawSText(dc, b); break;   // a kind this doesn't know: its alt text
        }
        dim?.Dispose();
        AnimAfter(dc, b);
        AnimUnpush(pushed);
    }

    private void DrawKids(DrawingContext dc, SceneTree s, SBlock b, Rect clip)
    {
        foreach (var k in b.Kids) DrawBlock(dc, s, k, clip);
    }

    private bool Focused(SceneTree s, SBlock b) => (s == _scene || s == _gFocus?.Tree) && s.Focus != 0 && s.Focus == b.Id;

    private bool IsHover(SBlock b) => !b.Off && b.Box.Contains(_mouse) && b.Clip.Contains(_mouse);

    /// <summary>A panel in the sysop's box style (radius, border, opacity, shadow), its title on the top border.</summary>
    private void DrawSPanel(DrawingContext dc, SBlock b)
    {
        Color bg = Ucol(b, PBg) ?? Scol(Sc.Panel);
        Panel(dc, b.Box, bg, Scol(Sc.Border), _st.Radius * 8 / 6 * _u);
        if (b.Has(PGrad))   // version 2: a gradient to P_GRAD, drifting with P_DRIFT
            DrawGradPanel(dc, b, bg, _st.Border > 0 ? new Pen(B(Scol(Sc.Border)), _st.Border * _u) : null, _st.Radius * 8 / 6 * _u);
        string title = b.S(PTitle);
        if (title.Length == 0 || _st.Title == 2) return;
        double u = _u, bh = Math.Max(4, 4 + (_st.Border - 1.5)) * u, tx = b.Box.X + 20 * u;
        var t = Fit(title, UiBold, 17, Ucol(b, PFg) ?? Scol(Sc.Title), Math.Max(b.Box.Width - 40 * u, 1));
        if (_st.Border > 0) dc.DrawRectangle(B(WithA(bg, BoxA)), null, R(tx - 8 * u, b.Box.Y - bh / 2, t.Width + 16 * u, bh));
        dc.DrawText(t, new Point(tx, b.Box.Y - t.Height / 2));
    }

    /// <summary>Styled text in its box: span backgrounds, pulsing (between its color and the panel's), shimmering (a
    /// glint sweeping across), blinking backgrounds, as the menus' spans.</summary>
    private void DrawSText(DrawingContext dc, SBlock b)
    {
        var r = b.Inner;
        if (TextEmpty(b) || r.Width <= 0) return;
        var ft = TextFt(b, r.Width);
        var origin = new Point(r.X, r.Y);
        var (_, _, def) = TextLook(b);
        foreach (var (start, len, sp) in b.Runs)
        {
            if (len == 0) continue;
            if ((sp.Bg & 0xFF000000) == 0 && !(sp.Fx == FxBlink && (long)(Now / 500) % 2 == 1)
                && ft.BuildHighlightGeometry(origin, start, len) is Geometry g)
                dc.DrawGeometry(B(C(sp.Bg)), null, g);
            Color fg = (sp.Fg & 0xFF000000) == 0 ? C(sp.Fg) : def;
            if (sp.Fx == FxPulse)
            {
                ft.SetForegroundBrush(B(Mix(Mix(fg, Scol(Sc.Panel), 0.5), fg, 0.5 + 0.5 * Math.Sin(Now / 1000.0 * 3.6))), start, len);
            }
            else if (sp.Fx == FxShimmer && ft.BuildHighlightGeometry(origin, start, len) is Geometry sg)
            {
                // Windows sets one absolute-mapped gradient brush on the range; here each character gets its own
                // color from the same glint (as DrawSLine's shimmer), since a text run's brush space isn't the screen's
                var bx = sg.Bounds;
                double pos = bx.X + (Now / 1400.0 % 1.8) * (bx.Width + 60 * _u) - 30 * _u;
                for (int i = 0; i < len;)
                {
                    int n = i + 1 < len && char.IsHighSurrogate(sp.Text[i]) ? 2 : 1;
                    if (ft.BuildHighlightGeometry(origin, start + i, n) is Geometry cg)
                    {
                        var cb = cg.Bounds;
                        double glint = 1 - Math.Clamp(Math.Abs(cb.X + cb.Width / 2 - pos) / (24 * _u), 0, 1);
                        ft.SetForegroundBrush(B(Mix(fg, Colors.White, glint * 0.85)), start + i, n);
                    }
                    i += n;
                }
            }
        }
        using (dc.PushClip(Inset(b.Box, -2 * _u)))
        {
            dc.DrawText(ft, origin);
            DrawLinks(dc, b);   // version 2: link spans underlined
        }
    }

    /// <summary>A picture (pw x ph its shape) in r by P_FIT: contain, cover, stretch or its natural size, centered;
    /// by nearest pixel unless smooth.</summary>
    private Rect DrawFitted(DrawingContext dc, Bitmap src, double pw, double ph, Rect r, int fit, bool smooth)
    {
        if (r.Width <= 0 || r.Height <= 0 || pw <= 0 || ph <= 0) return default;
        Rect d;
        switch (fit)
        {
            case FitStretch: d = r; break;
            case FitCover: { double sc = Math.Max(r.Width / pw, r.Height / ph); d = CenterIn(r, pw * sc, ph * sc); break; }
            case FitNone: d = CenterIn(r, pw * _u, ph * _u); break;
            default: { double sc = Math.Min(r.Width / pw, r.Height / ph); d = CenterIn(r, pw * sc, ph * sc); break; }
        }
        var mode = smooth ? BitmapInterpolationMode.HighQuality : BitmapInterpolationMode.None;
        using (dc.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = mode }))
        {
            if (fit is FitCover or FitNone)
                using (dc.PushClip(r))
                    dc.DrawImage(src, d);
            else
                dc.DrawImage(src, d);
        }
        return d;   // version 6: where it went (click positions)
    }

    /// <summary>The terminal's bitmap font (TERMinator's window manager draws with the same one); null without it.</summary>
    private WmFont? CellFont()
    {
        if (_cellFont != null || _cellFontTried) return _cellFont;
        _cellFontTried = true;
        try
        {
            var buffer = new byte[256 * 32 + 2];
            int len = Services.NativeBridge.syncterm_get_font_bitmap(buffer, buffer.Length);
            int height = len > 2 ? buffer[1] : 0;
            if (height > 0 && height <= 32 && len - 2 >= 256 * height) _cellFont = new WmFont { Data = buffer[2..len], Height = height };
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _cellFont = null;
        }
        return _cellFont;
    }

    /// <summary>A cells block painted once into a bitmap, 8 x the font's height pixels a cell, exactly as the window
    /// manager paints a window (WmPaint.Glyph): unset colors light gray on clear. Without the font, the backgrounds and
    /// each character in the mono font instead.</summary>
    private Bitmap? CellsBitmap(SBlock b)
    {
        var (cols, rows) = CellDims(b);
        if (cols == 0 || rows == 0) return null;
        var font = CellFont();
        int fh = font?.Height ?? 16, pw = cols * 8, ph = rows * fh;
        if (!b.CellsDirty && b.Bitmap != null && b.Bitmap.PixelSize.Width == pw && b.Bitmap.PixelSize.Height == ph) return b.Bitmap;
        var px = new int[pw * ph];
        for (int y = 0; y < rows; y++)
        {
            var row = y < b.Cells.Count ? b.Cells[y] : Array.Empty<SCell>();
            for (int x = 0; x < cols; x++)
            {
                var c = x < row.Length ? row[x] : new SCell(32, None, None);
                int fg = (c.Fg & 0xFF000000) != 0 ? unchecked((int)0xFFAAAAAA) : unchecked((int)(0xFF000000 | c.Fg));
                int bg = (c.Bg & 0xFF000000) != 0 ? 0 : unchecked((int)(0xFF000000 | c.Bg));
                WmPaint.Glyph(px, pw, x, y, c.Ch, fg, bg, font, fh);
            }
        }
        var wb = WmPaint.NewBitmap(pw, ph);
        WmPaint.Copy(px, pw, ph, wb);
        Bitmap bmp = wb;
        if (font == null)
        {
            var rtb = new RenderTargetBitmap(new PixelSize(pw, ph), new Vector(96, 96));
            using (var dc = rtb.CreateDrawingContext())
            {
                dc.DrawImage(wb, new Rect(0, 0, pw, ph));
                for (int y = 0; y < rows && y < b.Cells.Count; y++)
                    for (int x = 0; x < b.Cells[y].Length && x < cols; x++)
                    {
                        var c = b.Cells[y][x];
                        if (c.Ch is 0 or 32 or 255) continue;
                        var col = (c.Fg & 0xFF000000) != 0 ? Color.FromRgb(0xAA, 0xAA, 0xAA) : C(c.Fg);
                        var ft = new FormattedText(Cp437.ToChar(c.Ch).ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                                   MonoReg, 14, B(col));
                        dc.DrawText(ft, new Point(x * 8 + (8 - ft.Width) / 2, y * fh + (fh - ft.Height) / 2));
                    }
            }
            bmp = rtb;
        }
        b.Bitmap = bmp;
        b.CellsDirty = false;
        return bmp;
    }

    /// <summary>A bar: its fill to P_VALUE percent (below 0: a piece sliding to and fro, busy), its label right of it.</summary>
    private void DrawSProgress(DrawingContext dc, SBlock b)
    {
        double u = SU, v = b.F(PValue);
        var r = b.Inner;
        string label = b.S(PLabel);
        double lw = 0;
        if (label.Length > 0)
        {
            var ft = Fit(label, Ui, SmallSize, Scol(Sc.Dim), Math.Max(r.Width * 0.5, 1));
            lw = ft.Width + 0.6 * u;
            dc.DrawText(ft, new Point(r.Right - ft.Width, r.Y + (r.Height - ft.Height) / 2));
        }
        double th = Math.Min(0.5 * u, r.Height), rad = th / 2;
        var track = R(r.X, r.Y + (r.Height - th) / 2, r.Width - lw, th);
        dc.DrawRectangle(B(WithA(Scol(Sc.Text), 40)), null, track, rad, rad);
        Color fill = Ucol(b, PFg) ?? RoleColor(b, Scol(Sc.Accent));
        if (v < 0)
        {
            double seg = track.Width * 0.3, t = Now / 1300.0 % 2, f = t < 1 ? t : 2 - t;
            f = f * f * (3 - 2 * f);
            dc.DrawRectangle(B(fill), null, R(track.X + (track.Width - seg) * f, track.Y, seg, th), rad, rad);
        }
        else if (v > 0)
        {
            dc.DrawRectangle(B(fill), null, R(track.X, track.Y, track.Width * Math.Min(v, 100) / 100, th), rad, rad);
        }
    }

    /// <summary>A key as a label shows it.</summary>
    private static string KeyName(int key) => key switch
    {
        27 => "Esc", 13 => "Enter", 32 => "Space", 9 => "Tab", 8 => "Backspace",
        > 32 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF) => char.ConvertFromUtf32(key),
        _ => "",
    };

    /// <summary>A button: rounded, lit under the pointer, a ring when focused, darker while the board answers its press;
    /// its label centered, the hotkey in it marked ("K Keep": the K in the hot color; else the key's first letter
    /// underlined).</summary>
    private void DrawSButton(DrawingContext dc, SceneTree s, SBlock b)
    {
        double u = _u, size = SSize;
        var r = b.Box;
        bool hot = b.U(PRole) == RHot, focus = Focused(s, b), hover = s == _scene && !_waiting && IsHover(b);
        bool pressed = s == _scene && _waiting && _scenePressed != 0 && _scenePressed == b.Id;
        Color bg = Ucol(b, PBg) ?? (hot ? Scol(Sc.ButtonHot) : Scol(Sc.ButtonBg));
        Color fg = Ucol(b, PFg) ?? (hot ? Scol(Sc.SelFg) : Scol(Sc.ButtonFg));
        if (pressed) bg = Mix(bg, Colors.Black, 0.25);
        else if (hover) bg = hot ? Mix(bg, Colors.White, 0.15) : Mix(bg, Scol(Sc.ButtonHot), 0.3);
        var box = Inset(r, 0.5 * u);
        double rad = Math.Min(box.Height / 2, Math.Max(_st.SelRadius, 4) * u);
        var pen = new Pen(B(focus ? (hot ? Scol(Sc.Focus) : Scol(Sc.ButtonHot)) : WithA(Scol(Sc.Border), 200)), (focus ? 2 : 1) * u);
        dc.DrawRectangle(B(bg), pen, box, rad, rad);
        double px = ButtonPadX(b);
        var room = R(r.X + px, r.Y, r.Width - 2 * px, r.Height);
        var spans = b.Spans(PSpans);
        if (spans.Length > 0)
        {
            double tw = spans.Sum(sp => TextW(sp.Text, UiBold, size));
            double x = room.X + Math.Max((room.Width - tw) / 2, 0);
            DrawSLine(dc, UiBold, size, spans, x, r.Y, room.Right - x, r.Height, fg, bg);
            return;
        }
        string label = b.S(PLabel);
        if (label.Length == 0 || room.Width <= 0) return;
        var ft = Fit(label, UiBold, size, fg, room.Width + u);   // + u: a button at its natural width cut its label ("Answ…")
        ft.TextAlignment = TextAlignment.Center;
        int key = b.U(PKey);
        string name = KeyName(key);
        if (name.Length > 0 && label.Length > name.Length && label.StartsWith(name, StringComparison.OrdinalIgnoreCase) && label[name.Length] == ' ')
        {
            if (hot) ft.SetTextDecorations(TextDecorations.Underline, 0, name.Length);
            else ft.SetForegroundBrush(B(Scol(Sc.ButtonHot)), 0, name.Length);
        }
        else if (name.Length > 0 && key > 32)
        {
            int at = label.IndexOf(name, StringComparison.OrdinalIgnoreCase);
            if (at >= 0)
            {
                ft.SetTextDecorations(TextDecorations.Underline, at, name.Length);
                if (!hot) ft.SetForegroundBrush(B(Scol(Sc.ButtonHot)), at, name.Length);
            }
        }
        dc.DrawText(ft, new Point(room.X, MidY(ft, UiBold, size, r.Y, r.Height)));
    }

    /// <summary>A text field as the forms draw theirs: rounded, lit and marked when focused, the caret blinking, the
    /// placeholder while empty, a secret as dots; more lines when P_MULTI, scrolled to the caret.</summary>
    private void DrawSField(DrawingContext dc, SceneTree s, SBlock b)
    {
        double u = _u, su = SU, size = SSize;
        var r = b.Box;
        bool on = Focused(s, b), multi = b.Flag(PMulti), secret = b.Flag(PSecret);
        Color bg = Ucol(b, PBg) ?? Scol(Sc.FieldBg), fg = Ucol(b, PFg) ?? Scol(Sc.FieldFg);
        double fr = _st.SelRadius * u;
        var pen = new Pen(B(on ? Scol(Sc.Focus) : WithA(Scol(Sc.Border), 150)), (on ? 1.5 : 1) * u);
        dc.DrawRectangle(B(on ? bg : WithA(bg, 200)), pen, Inset(r, 0.5 * u), fr, fr);
        if (on && _st.Marker) dc.DrawRectangle(B(Scol(Sc.Focus)), null, R(r.X, r.Y, 3 * u, r.Height));
        var room = R(r.X + 0.5 * su, r.Y, r.Width - su, r.Height);
        bool caretOn = on && !_waiting && ((DateTime.UtcNow - _caretAt).TotalMilliseconds < 600 || (long)(Now / 530) % 2 == 0);
        string shown = secret ? new string('•', Chars(b.Value)) : b.Value;
        int cur = secret ? Chars(b.Value[..Math.Min(b.Cur, b.Value.Length)]) : Math.Min(b.Cur, b.Value.Length);
        using var clip = dc.PushClip(room);
        if (b.Value.Length == 0 && b.S(PPlace) is { Length: > 0 } place)
        {
            var pt = Fit(place, Ui, size, Scol(Sc.Dim), Math.Max(room.Width, 1));
            dc.DrawText(pt, new Point(room.X, multi ? r.Y + 0.3 * su : MidY(pt, Ui, size, r.Y, r.Height)));
        }
        if (!multi || secret)
        {
            double cxw = TextW(shown[..cur], Ui, size), shift = cxw > room.Width - 4 * u ? cxw - (room.Width - 4 * u) : 0;
            var vt = Text(shown, Ui, size, fg);
            dc.DrawText(vt, new Point(room.X - shift, MidY(vt, Ui, size, r.Y, r.Height)));
            if (caretOn) dc.DrawRectangle(B(Scol(Sc.Focus)), null, R(room.X - shift + cxw, r.Y + 0.3 * su, 2 * u, r.Height - 0.6 * su));
            b.TextX = room.X - shift;
        }
        else
        {
            // version 6: lines wrapped by words at the field's width (or P_WRAPCOLS), as typed (RainwireSceneV6.cs)
            double lh = FieldLineH(), top = r.Y + 0.3 * su, viewH = Math.Max(r.Height - 0.6 * su, lh);
            var vl = b.VLines = WrapValue(b, shown, Ui, size, room.Width - 4 * u);
            int line = VLineOf(vl, cur);
            double cy = line * lh;
            if (cy - b.Scroll > viewH - lh) b.Scroll = cy - (viewH - lh);
            if (cy < b.Scroll) b.Scroll = cy;
            b.Scroll = Math.Max(b.Scroll, 0);
            double cxw = TextW(shown[vl[line].Start..cur], Ui, size);
            for (int i = 0; i < vl.Count; i++)
            {
                double y = top + i * lh - b.Scroll;
                if (y + lh < r.Y || y > r.Bottom) continue;
                dc.DrawText(Text(shown[vl[i].Start..vl[i].End], Ui, size, fg), new Point(room.X, y));
            }
            if (caretOn) dc.DrawRectangle(B(Scol(Sc.Focus)), null, R(room.X + cxw, top + cy - b.Scroll + 2 * u, 2 * u, lh - 4 * u));
            b.TextX = room.X;
            b.TextY = top - b.Scroll;
            b.LineH = lh;
        }
    }

    /// <summary>A choice as a row of pills (wrapping when they don't fit): the chosen one filled, a ring on it when focused.</summary>
    private void DrawSChoice(DrawingContext dc, SceneTree s, SBlock b)
    {
        double u = _u, size = SSize;
        var r = b.Box;
        var opts = b.Strs(POptions);
        bool on = Focused(s, b);
        b.Hits.Clear();
        var places = PillPlaces(b, r.Width);
        for (int i = 0; i < places.Count && i < opts.Length; i++)
        {
            var pill = new Rect(r.X + places[i].X, r.Y + places[i].Y, places[i].Width, places[i].Height);
            double rad = pill.Height / 2;
            bool sel = i == b.Index, hv = s == _scene && !b.Off && pill.Contains(_mouse) && b.Clip.Contains(_mouse);
            if (sel)
                dc.DrawRectangle(B(Scol(Sc.SelBg)), on ? new Pen(B(Scol(Sc.Focus)), 2 * u) : null, pill, rad, rad);
            else
                dc.DrawRectangle(hv ? B(WithA(Scol(Sc.SelBg), 60)) : null, new Pen(B(WithA(on ? Scol(Sc.Focus) : Scol(Sc.Border), 160)), 1 * u),
                                 pill, rad, rad);
            var ct = Fit(opts[i], Ui, size, sel ? Scol(Sc.SelFg) : Ucol(b, PFg) ?? Scol(Sc.Text), pill.Width);
            dc.DrawText(ct, new Point(pill.X + (pill.Width - ct.Width) / 2, MidY(ct, Ui, size, pill.Y, pill.Height)));
            b.Hits.Add((pill, i));
        }
    }

    /// <summary>A checkbox and its label: filled with a tick when on, a ring when focused.</summary>
    private void DrawSCheck(DrawingContext dc, SceneTree s, SBlock b)
    {
        double u = _u, su = SU, size = SSize;
        var r = b.Box;
        bool on = Focused(s, b), hv = s == _scene && IsHover(b);
        double bs = Math.Max(Math.Min(0.9 * su, r.Height - 2 * u), 4 * u);
        var box = new Rect(r.X + 0.1 * su, r.Y + (r.Height - bs) / 2, bs, bs);
        Color fill = b.On ? Scol(Sc.Accent) : hv ? Mix(Scol(Sc.FieldBg), Scol(Sc.SelBg), 0.3) : Scol(Sc.FieldBg);
        var pen = new Pen(B(on ? Scol(Sc.Focus) : WithA(Scol(Sc.Border), 200)), (on ? 2 : 1) * u);
        dc.DrawRectangle(B(fill), pen, box, 3 * u, 3 * u);
        if (b.On)
        {
            var tick = new StreamGeometry();
            using (var g = tick.Open())
            {
                g.BeginFigure(new Point(box.X + bs * 0.22, box.Y + bs * 0.52), false);
                g.LineTo(new Point(box.X + bs * 0.42, box.Y + bs * 0.72));
                g.LineTo(new Point(box.X + bs * 0.78, box.Y + bs * 0.30));
                g.EndFigure(false);
            }
            var tc = Luma(fill) > 140 ? Color.FromRgb(0x10, 0x10, 0x10) : Colors.White;
            dc.DrawGeometry(null, new Pen(B(tc), 2 * u, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), tick);
        }
        string label = b.S(PLabel);
        if (label.Length == 0) return;
        double lx = box.Right + 0.5 * su;
        var ft = Fit(label, Ui, size, Ucol(b, PFg) ?? Scol(Sc.Text), Math.Max(r.Right - lx, 1));
        dc.DrawText(ft, new Point(lx, MidY(ft, Ui, size, r.Y, r.Height)));
    }

    /// <summary>A list's columns' left edges and widths across w: P_WIDTHS in units, those of 0 (or none) sharing the rest.</summary>
    private (double[] X, double[] W) SListCols(SBlock b, double x, double w, int n)
    {
        var ws = b.F32s(PWidths);
        double u = SU, fix = 0;
        int flex = 0;
        for (int c = 0; c < n; c++)
        {
            if (c < ws.Length && ws[c] > 0) fix += ws[c] * u;
            else flex++;
        }
        double scale = flex == 0 && fix > w ? w / fix : fix > w ? 0.5 * w / fix : 1, rest = Math.Max(w - fix * scale, 0);
        var cx = new double[n];
        var cw = new double[n];
        for (int c = 0; c < n; c++)
        {
            cw[c] = c < ws.Length && ws[c] > 0 ? ws[c] * u * scale : rest / Math.Max(flex, 1);
            cx[c] = x;
            x += cw[c];
        }
        return (cx, cw);
    }

    /// <summary>A list's items (its BK_ITEM children; an item's index is its place among them).</summary>
    private static List<SBlock> Items(SBlock list)
    {
        var l = new List<SBlock>();
        foreach (var k in list.Kids)
            if (k.Kind == BkItem) l.Add(k);
        return l;
    }

    private static bool ItemOk(SBlock it) => !it.Flag(PHidden) && (it.U(PIFlags) & IfHeading) == 0;

    /// <summary>A list as today's lists: column titles and a rule, items as rows 1.5 units tall (headings bold in the
    /// second accent, never selected; bold and dim items), the selection lit (dimmer without the focus), scrolled to it.</summary>
    private void DrawSList(DrawingContext dc, SceneTree s, SBlock b)
    {
        double u = _u, su = SU, size = SSize, rowH = 1.5 * su;
        var r = b.Box;
        bool on = Focused(s, b);
        var pen = new Pen(B(WithA(on ? Scol(Sc.Focus) : Scol(Sc.Border), (byte)(on ? 220 : 110))), 1 * u);
        double fr = _st.SelRadius * u;
        dc.DrawRectangle(Ucol(b, PBg) is Color lbg ? B(lbg) : null, pen, Inset(r, 0.5 * u), fr, fr);
        var inner = Inset(r, 0.3 * su);
        var heads = b.Strs(PHeaders);
        var items = Items(b);
        int ncols = Math.Max(heads.Length, Math.Max(b.F32s(PWidths).Length, 1));
        foreach (var it in items) ncols = Math.Max(ncols, Math.Min(it.Strs(PCellText).Length, 16));
        double rowsW = Math.Max(inner.Width - 10 * u, 1);
        var (cx, cw) = SListCols(b, inner.X, rowsW, ncols);
        double y = inner.Y;
        if (heads.Length > 0)
        {
            for (int c = 0; c < heads.Length && c < ncols; c++)
            {
                if (heads[c].Length == 0 || cw[c] <= 8 * u) continue;
                var ft = Fit(heads[c], UiBold, size, Scol(Sc.Accent), cw[c] - 8 * u);
                dc.DrawText(ft, new Point(cx[c] + 4 * u, MidY(ft, UiBold, size, y, rowH)));
            }
            dc.DrawRectangle(B(WithA(Scol(Sc.Border), 160)), null, R(inner.X, y + rowH, inner.Width, Math.Max(1 * u, 1)));
            y += rowH + 0.2 * su;
        }
        var view = R(inner.X, y, inner.Width, inner.Bottom - y);
        double total = 0, selY = -1;
        foreach (var (it, i) in items.Select((it, i) => (it, i)))
        {
            if (it.Flag(PHidden)) continue;
            if (i == b.Index) selY = total;
            total += rowH;
        }
        b.View = view.Height;
        b.ScrollMax = Math.Max(total - view.Height, 0);
        if (b.ShowSel && selY >= 0)
        {
            if (selY < b.Scroll) b.Scroll = selY;
            if (selY + rowH > b.Scroll + view.Height) b.Scroll = selY + rowH - view.Height;
            b.ShowSel = false;
        }
        b.Scroll = Math.Clamp(b.Scroll, 0, b.ScrollMax);
        b.Hits.Clear();
        using (dc.PushClip(view))
        {
            double yy = 0, sr = _st.SelRadius * u;
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                if (it.Flag(PHidden)) continue;
                double ry = view.Y + yy - b.Scroll;
                yy += rowH;
                if (ry + rowH < view.Y || ry > view.Bottom) continue;
                int flags = it.U(PIFlags);
                bool heading = (flags & IfHeading) != 0, sel = !heading && i == b.Index;
                var bar = R(view.X, ry + 1 * u, rowsW, rowH - 2 * u);
                if (sel)
                {
                    dc.DrawRectangle(B(on ? Scol(Sc.SelBg) : WithA(Scol(Sc.SelBg), 150)), null, bar, sr, sr);
                    if (_st.Marker && on) dc.DrawRectangle(B(Scol(Sc.Focus)), null, R(bar.X, bar.Y, 3 * u, bar.Height));
                }
                else if (!heading && s == _scene && !b.Off && bar.Contains(_mouse) && b.Clip.Contains(_mouse))
                {
                    dc.DrawRectangle(B(WithA(Scol(Sc.SelBg), 60)), null, bar, sr, sr);
                }
                var face = heading || (flags & IfBold) != 0 ? UiBold : Ui;
                Color col = sel ? Scol(Sc.SelFg) : heading ? Scol(Sc.Accent2) : Ucol(it, PFg) ?? ((flags & IfDim) != 0 ? Scol(Sc.Dim) : Scol(Sc.Text));
                var texts = it.Strs(PCellText);
                if (heading)
                {
                    var ft = Fit(string.Join("  ", texts), face, size, col, Math.Max(rowsW - 8 * u, 1));
                    dc.DrawText(ft, new Point(view.X + 4 * u, MidY(ft, face, size, ry, rowH)));
                    continue;
                }
                for (int c = 0; c < texts.Length && c < ncols; c++)
                {
                    if (cw[c] <= 8 * u || texts[c].Length == 0) continue;
                    var ft = Fit(texts[c], face, size, col, cw[c] - 8 * u);
                    dc.DrawText(ft, new Point(cx[c] + 4 * u, MidY(ft, face, size, ry, rowH)));
                }
                b.Hits.Add((Isect(R(view.X, ry, rowsW, rowH), view), i));
            }
        }
        SScrollbar(dc, view, b.Scroll, b.ScrollMax, false, s, b);
    }

    /// <summary>A thin bar on view's right (or bottom) edge when there's more than shows. Version 6: wider under the
    /// pointer, and kept (s.Bars, for block b; null: the scene's own) so it can be dragged (RainwireSceneV6.cs).</summary>
    private void SScrollbar(DrawingContext dc, Rect view, double scroll, double max, bool horiz, SceneTree? s = null, SBlock? b = null)
    {
        if (max <= 0 || IsEmpty(view)) return;
        double u = _u;
        Color c = Scol(Sc.Border);
        var track = horiz ? R(view.X, view.Bottom - 10 * u, view.Width, 10 * u) : R(view.Right - 10 * u, view.Y, 10 * u, view.Height);
        bool dragged = _barDrag is { } bd && bd.S == s && bd.B == b;
        bool hot = dragged || track.Contains(_mouse);
        double t = hot ? 7 * u : 4 * u, off = hot ? 8 * u : 6 * u, a = t / 2;
        if (horiz)
        {
            double iw = view.Width, tw = Math.Clamp(iw * iw / (iw + max), Math.Min(24 * u, iw), iw), tx = view.X + (iw - tw) * (scroll / max);
            dc.DrawRectangle(B(WithA(c, (byte)(hot ? 80 : 50))), null, R(view.X, view.Bottom - off, iw, t), a, a);
            dc.DrawRectangle(B(WithA(dragged ? Scol(Sc.Accent) : c, 220)), null, R(tx, view.Bottom - off, tw, t), a, a);
            s?.Bars.Add(new SBar(b, track, R(tx, track.Y, tw, track.Height), true, max, iw - tw));
            return;
        }
        double ih = view.Height, th = Math.Clamp(ih * ih / (ih + max), Math.Min(24 * u, ih), ih), ty = view.Y + (ih - th) * (scroll / max);
        dc.DrawRectangle(B(WithA(c, (byte)(hot ? 80 : 50))), null, R(view.Right - off, view.Y, t, ih), a, a);
        dc.DrawRectangle(B(WithA(dragged ? Scol(Sc.Accent) : c, 220)), null, R(view.Right - off, ty, t, th), a, a);
        s?.Bars.Add(new SBar(b, track, R(track.X, ty, track.Width, th), false, max, ih - th));
    }

    /// <summary>P_TOOLTIP of what's under the pointer, once it has rested there half a second.</summary>
    private void SceneTooltip(DrawingContext dc, SceneTree s, double w, double h)
    {
        if ((DateTime.UtcNow - _sceneMoveAt).TotalMilliseconds < 500) return;
        SBlock? hit = null;
        for (int i = s.Order.Count - 1; i >= 0 && hit == null; i--)
        {
            var b = s.Order[i];
            if (b.Pass == s.Pass && b.Has(PTooltip) && b.Box.Contains(_mouse) && b.Clip.Contains(_mouse)) hit = b;
        }
        if (hit == null) return;
        double u = _u;
        var ft = Text(hit.S(PTooltip), Ui, 13, Scol(Sc.Text));
        ft.MaxTextWidth = Math.Max(Math.Min(400 * u, w - 40 * u), 1);
        double bw = ft.Width + 16 * u, bh = ft.Height + 10 * u, bx = _mouse.X + 14 * u, by = _mouse.Y + 18 * u;
        if (bx + bw > w - 4 * u) bx = Math.Max(_mouse.X - bw - 10 * u, 4 * u);
        if (by + bh > h - 4 * u) by = Math.Max(_mouse.Y - bh - 6 * u, 4 * u);
        dc.DrawRectangle(B(Color.FromArgb(90, 0, 0, 0)), null, new Rect(bx + 2 * u, by + 3 * u, bw, bh), 6 * u, 6 * u);
        dc.DrawRectangle(B(WithA(Mix(Scol(Sc.Panel), Colors.Black, 0.25), 245)), new Pen(B(Scol(Sc.Border)), 1 * u),
                         new Rect(bx, by, bw, bh), 6 * u, 6 * u);
        dc.DrawText(ft, new Point(bx + 8 * u, by + 5 * u));
    }

    // ================================================================
    // Input (docs/SCENES.md "Events")
    // ================================================================

    /// <summary>A control that can have the focus: an id (else it sends nothing), placed, not disabled.</summary>
    private static bool SFocusable(SceneTree s, SBlock b) => b.Id != 0 && b.Pass == s.Pass && !b.Off
        && (IsControl(b.Kind) && !(b.Kind == BkVList && b.U(PSelect) == 0 && !b.Flag(PPane))   // a list nothing is picked in (MRC's log) leaves the keys where they were
            || b.Kind == BkScroll && b.Flag(PPane));   // version 6: a pane takes the focus (its keys) when clicked or tabbed to

    private static SBlock? SceneFocused(SceneTree s) =>
        s.Focus != 0 && s.ById.TryGetValue(s.Focus, out var b) && SFocusable(s, b) ? b : null;

    private static List<SBlock> Focusables(SceneTree s) => s.Order.Where(b => SFocusable(s, b)).ToList();

    private void FocusTo(SceneTree s, SBlock b)
    {
        s.Focus = b.Id;
        _caretAt = DateTime.UtcNow;
        SceneIntoView(s, b);
    }

    /// <summary>Tab / Shift+Tab: the next or previous control in the tree's order, round; false when there's none.</summary>
    private bool FocusStep(SceneTree s, int d)
    {
        var list = Focusables(s);
        if (list.Count == 0) return false;
        int at = SceneFocused(s) is SBlock f ? list.IndexOf(f) : -1;
        FocusTo(s, list[at < 0 ? (d > 0 ? 0 : list.Count - 1) : (at + d + list.Count) % list.Count]);
        return true;
    }

    /// <summary>An arrow: the nearest control that way (as the menus' grid moves); false when there's none.</summary>
    private bool FocusMove(SceneTree s, SBlock? f, int dx, int dy)
    {
        var list = Focusables(s);
        if (list.Count == 0) return false;
        if (f == null)
        {
            FocusTo(s, dx + dy > 0 ? list[0] : list[^1]);
            return true;
        }
        var cur = f.Box;
        SBlock? best = null;
        double bestD = double.MaxValue;
        foreach (var b in list)
        {
            if (b == f) continue;
            double ddx = b.Box.X + b.Box.Width / 2 - (cur.X + cur.Width / 2), ddy = b.Box.Y + b.Box.Height / 2 - (cur.Y + cur.Height / 2);
            if (dx != 0 && (dx > 0 ? ddx <= 1 : ddx >= -1)) continue;
            if (dy != 0 && (dy > 0 ? ddy <= 1 : ddy >= -1)) continue;
            double d = dx != 0 ? Math.Abs(ddx) + 3 * Math.Abs(ddy) : Math.Abs(ddy) + 3 * Math.Abs(ddx);
            if (d < bestD) { bestD = d; best = b; }
        }
        if (best == null) return false;
        FocusTo(s, best);
        return true;
    }

    private static bool KeyMatch(int k, int cp) =>
        k == cp || k is > 32 and < 0x10000 && cp is > 32 and < 0x10000 && char.ToUpperInvariant((char)k) == char.ToUpperInvariant((char)cp);

    /// <summary>The button whose P_KEY is cp (letters either case), placed and enabled.</summary>
    private static SBlock? HotkeyButton(SceneTree s, int cp)
    {
        foreach (var b in s.Order)
            if (b.Kind == BkButton && b.Pass == s.Pass && !b.Off && b.Id != 0 && b.Has(PKey) && KeyMatch(b.U(PKey), cp))
                return b;
        return null;
    }

    /// <summary>The board said nothing for 2 s after a press, submit or key (one it had no answer for): input again.</summary>
    private void SceneWaitCheck()
    {
        if (_waiting && (DateTime.UtcNow - _sceneWaitAt).TotalSeconds > 2)
        {
            _waiting = false;
            _scenePressed = 0;
        }
    }

    private void SceneWait(ushort pressed)
    {
        _waiting = true;
        _sceneWaitAt = DateTime.UtcNow;
        _scenePressed = pressed;
    }

    /// <summary>Every field's value, for Submit and a submitting Press: repeated u16 id, u16 len, UTF-8, in base64.</summary>
    private static string SceneFieldsB64(SceneTree s)
    {
        var bytes = new List<byte>();
        void Walk(SBlock b)
        {
            if ((b.Kind == BkField || b.Kind == BkInput) && b.Id != 0)
            {
                var v = Encoding.UTF8.GetBytes(b.Value);
                int n = Math.Min(v.Length, 0xFFFF);
                bytes.AddRange([(byte)b.Id, (byte)(b.Id >> 8), (byte)n, (byte)(n >> 8)]);
                bytes.AddRange(v.AsSpan(0, n).ToArray());
            }
            foreach (var k in b.Kids) Walk(k);
        }
        Walk(s.Top);
        return Convert.ToBase64String(bytes.ToArray());
    }

    private void ScenePress(SceneTree s, SBlock b)
    {
        if (_waiting || b.Off || b.Id == 0) return;
        SceneWait(b.Id);
        Reply($"Press;id={b.Id}" + (b.Flag(PSubmit) ? ";b64=" + SceneFieldsB64(s) : ""));
    }

    private void SceneSubmit(SceneTree s, SBlock f)
    {
        if (_waiting || f.Off || f.Id == 0) return;
        SceneWait(0);
        Reply($"Submit;id={f.Id};b64={SceneFieldsB64(s)}");
        if (f.Kind == BkInput) InputSubmitted(f);
    }

    private void SceneBack()
    {
        if (_waiting) return;
        SceneWait(0);
        Reply("Back");
    }

    /// <summary>A key nothing on the scene used: to the board with the focused block (vk and key as a list's Key).</summary>
    private void SceneSendKey(SceneTree s, int vk, bool alt, bool ctrl, int cp, string? fields = null)
    {
        if (_waiting) return;
        SceneWait(0);
        bool shift = (_keyMods & KeyModifiers.Shift) != 0;
        Reply($"Key;vk={vk};key={cp};focus={SceneFocused(s)?.Id ?? 0}" + (alt ? ";alt=1" : "") + (ctrl ? ";ctrl=1" : "") +
              (shift ? ";shift=1" : "") + (fields != null ? ";b64=" + fields : ""));
    }

    /// <summary>The modifiers of the last key down on the scene (SceneKey): Avalonia has no global Keyboard.Modifiers,
    /// so SceneSendKey and SceneSendKeyX read Shift and Ctrl from here (typed text follows its own key down).</summary>
    private KeyModifiers _keyMods;

    /// <summary>A key only a screen that asks for it uses (F2-F12 as fn=, Alt plus a letter or digit as alt=1;ch=): key=0
    /// so an older board drops it, and no wait for an answer (a screen that doesn't use it sends none).</summary>
    private void SceneSendKeyX(SceneTree s, string what)
    {
        if (_waiting) return;
        var mods = _keyMods;
        Reply($"Key;vk=0;key=0;focus={SceneFocused(s)?.Id ?? 0};{what}" + ((mods & KeyModifiers.Control) != 0 ? ";ctrl=1" : "") +
              ((mods & KeyModifiers.Shift) != 0 ? ";shift=1" : ""));
    }

    private void SChoiceSet(SBlock b, int i)
    {
        if (b.Off || i == b.Index || i < 0 || i >= b.Strs(POptions).Length) return;
        b.Index = i;
        if (b.Id != 0) Reply($"Change;id={b.Id};i={i}");
    }

    private void CheckToggle(SBlock b)
    {
        if (b.Off) return;
        b.On = !b.On;
        if (b.Id != 0) Reply($"Change;id={b.Id};on={(b.On ? 1 : 0)}");
    }

    /// <summary>A P_LIVE field's value goes as it changes.</summary>
    private void FieldChanged(SBlock b)
    {
        b.ChangedAt = DateTime.UtcNow;   // P_AUTOSAVE counts from here (SceneTimers)
        if (!b.Flag(PLive) || b.Id == 0) return;
        if (b.Has(PDebounce)) b.ChangePending = true;   // sent when typing pauses (SceneTimers): the Messages To field's names
        else Reply($"Change;id={b.Id};b64={Convert.ToBase64String(Encoding.UTF8.GetBytes(b.Value))}");
    }

    private void ListSelect(SBlock b, int i)
    {
        if (i == b.Index) return;
        b.Index = i;
        b.ShowSel = true;
        if (b.Id != 0) Reply($"Select;id={b.Id};i={i}");
    }

    private void ListActivate(SBlock b, int i)
    {
        if (_waiting || b.Off || b.Id == 0) return;
        SceneWait(0);
        Reply($"Activate;id={b.Id};i={i}");
    }

    /// <summary>A key on the scene; true when it was used. Typed characters are left to come as text (SceneText).</summary>
    private bool SceneKey(Key key, KeyModifiers mods)
    {
        var s = _scene;
        if (s == null || !_complete)
        {
            if (key == Key.Escape) NewsBack();
            return key == Key.Escape;
        }
        SceneWaitCheck();
        _keyMods = mods;
        bool alt = (mods & KeyModifiers.Alt) != 0, ctrl = (mods & KeyModifiers.Control) != 0, shift = (mods & KeyModifiers.Shift) != 0;
        var f = SceneFocused(s);
        if (key == Key.Escape && !(f?.Kind == BkInput && OpenMenuFor(s, f) != null) && PopClose(s))   // version 2: pop-ups first
        {
            Redraw();
            return true;
        }
        if (f != null) _caretAt = DateTime.UtcNow;
        if (f != null && !alt)
        {
            bool used = f.Kind switch
            {
                BkField => FieldKey(s, f, key, ctrl, shift),
                BkInput => InputKey(s, f, key, ctrl, shift) || InputEdit(s, f, key, ctrl, shift),   // version 2
                BkVList => VListKey(s, f, key, ctrl, shift),
                BkList => !ctrl && SListKey(f, key),
                BkChoice => !ctrl && key is Key.Left or Key.Right && ChoiceStep(f, key == Key.Left ? -1 : 1),
                BkScroll => !ctrl && PaneKey(s, f, key),   // version 6 (RainwireSceneV6.cs)
                BkSlider => !ctrl && SliderKey(f, key),
                _ => false,
            };
            if (used)
            {
                Redraw();
                return true;
            }
        }
        switch (key)
        {
            case Key.Tab:
                if (ctrl || !FocusStep(s, shift ? -1 : 1)) SceneSendKey(s, VkTab, alt, ctrl, 0);
                break;
            case Key.Escape:
                if (HotkeyButton(s, 27) is SBlock esc) ScenePress(s, esc);
                else SceneBack();
                break;
            case Key.Return:
                if (f?.Kind == BkButton) ScenePress(s, f);
                else if (f?.Kind == BkCheck) CheckToggle(f);
                else if (HotkeyButton(s, 13) is SBlock enter) ScenePress(s, enter);
                else SceneSendKey(s, VkEnter, alt, ctrl, 0);
                break;
            case Key.Left: case Key.Right: case Key.Up: case Key.Down:
            {
                int dx = key == Key.Left ? -1 : key == Key.Right ? 1 : 0, dy = key == Key.Up ? -1 : key == Key.Down ? 1 : 0;
                if (alt || ctrl || !FocusMove(s, f, dx, dy))
                    SceneSendKey(s, key switch { Key.Left => VkLeft, Key.Right => VkRight, Key.Up => VkUp, _ => VkDown }, alt, ctrl, 0);
                break;
            }
            case Key.PageUp: case Key.PageDown: case Key.Home: case Key.End:
                if (alt || ctrl || !SceneScrollKey(s, key))
                    SceneSendKey(s, key switch { Key.PageUp => VkPgUp, Key.PageDown => VkPgDn, Key.Home => VkHome, _ => VkEnd }, alt, ctrl, 0);
                break;
            case Key.Delete: SceneSendKey(s, VkDelete, alt, ctrl, 0); break;
            case Key.Back: SceneSendKey(s, VkBackspace, alt, ctrl, 0); break;
            case Key.F1: SceneSendKey(s, VkF1, alt, ctrl, 0); break;
            default:
                if (ctrl && key >= Key.A && key <= Key.Z)   // Ctrl+letter: a key, not text
                {
                    SceneSendKey(s, VkChar, alt, true, 'A' + (key - Key.A));
                    break;
                }
                if (key >= Key.F2 && key <= Key.F12)
                {
                    SceneSendKeyX(s, $"fn={key - Key.F1 + 1}");
                    break;
                }
                if (alt && !ctrl && (key is >= Key.A and <= Key.Z or >= Key.D0 and <= Key.D9))   // Alt+letter or digit (MRC's Alt+1...9, Alt+V)
                {
                    SceneSendKeyX(s, $"alt=1;ch={(key <= Key.D9 ? '0' + (key - Key.D0) : 'A' + (key - Key.A))}");
                    break;
                }
                return false;   // typed characters come as text (SceneText)
        }
        Redraw();
        return true;
    }

    /// <summary>Typed text: a focused field takes it; else each character is a button's hotkey, Space presses a focused
    /// button or toggles a check, a letter picks a focused choice's option it starts, or it goes to the board as a Key.</summary>
    private void SceneText(string text)
    {
        var s = _scene;
        if (s == null || !_complete) return;   // Ctrl+letter went as a key (SceneKey); no text comes for it here
        SceneWaitCheck();
        var f = SceneFocused(s);
        if (f?.Kind == BkInput)   // version 2: limits, "empty+" keys, completion
        {
            InputText(s, f, text);
            _caretAt = DateTime.UtcNow;
            Redraw();
            return;
        }
        if (f?.Kind == BkField)
        {
            if (FieldInsert(f, text)) FieldChanged(f);
            _caretAt = DateTime.UtcNow;
            Redraw();
            return;
        }
        foreach (var rune in text.EnumerateRunes())
        {
            int cp = rune.Value;
            if (cp < 0x20 || cp == 0x7F) continue;
            if (HotkeyButton(s, cp) is SBlock hk) ScenePress(s, hk);
            else if (f?.Kind == BkCheck && cp == ' ') CheckToggle(f);
            else if (f?.Kind == BkButton && cp == ' ') ScenePress(s, f);
            else if (f?.Kind == BkChoice && cp == ' ') ChoiceStep(f, 1);
            else if (!(f?.Kind == BkChoice && ChoiceLetter(f, cp))) SceneSendKey(s, VkChar, false, false, cp);
            break;
        }
        Redraw();
    }

    private bool ChoiceStep(SBlock b, int d)
    {
        int n = b.Strs(POptions).Length;
        if (n > 0) SChoiceSet(b, ((b.Index + d) % n + n) % n);
        return true;
    }

    /// <summary>A letter picks the next option it starts (round from the chosen one); false when none does.</summary>
    private bool ChoiceLetter(SBlock b, int cp)
    {
        var opts = b.Strs(POptions);
        for (int k = 1; k <= opts.Length; k++)
        {
            int i = (b.Index + k) % opts.Length;
            if (opts[i].Length > 0 && KeyMatch(opts[i][0], cp))
            {
                SChoiceSet(b, i);
                return true;
            }
        }
        return false;
    }

    /// <summary>A focused list's keys: Up/Down to the next item that isn't a heading (Select), PgUp/PgDn a view's worth,
    /// Home/End, Enter Activate.</summary>
    private bool SListKey(SBlock b, Key key)
    {
        var items = Items(b);
        int Next(int from, int d)
        {
            for (int i = from + d; i >= 0 && i < items.Count; i += d)
                if (ItemOk(items[i])) return i;
            return -1;
        }
        int page = Math.Max((int)(b.View / (1.5 * SU)) - 1, 1), to = -1;
        switch (key)
        {
            case Key.Up: to = Next(b.Index, -1); break;
            case Key.Down: to = Next(b.Index, 1); break;
            case Key.Home: to = Next(-1, 1); break;
            case Key.End: to = Next(items.Count, -1); break;
            case Key.PageUp: case Key.PageDown:
            {
                int d = key == Key.PageUp ? -1 : 1;
                for (int k = 0, at = b.Index; k < page; k++)
                {
                    int n = Next(at, d);
                    if (n < 0) break;
                    to = at = n;
                }
                break;
            }
            case Key.Return:
                if (b.Index >= 0 && b.Index < items.Count && ItemOk(items[b.Index])) ListActivate(b, b.Index);
                return true;
            default: return false;
        }
        if (to >= 0) ListSelect(b, to);
        return true;
    }

    /// <summary>PgUp/PgDn/Home/End with nothing focused that takes them: the scroll under the pointer (or the first
    /// one that scrolls), else the whole scene; false when nothing scrolls.</summary>
    private bool SceneScrollKey(SceneTree s, Key key)
    {
        var sc = ScrollAt(s, _mouse, false)
                 ?? s.Order.FirstOrDefault(b => b.Kind == BkVList && b.Flag(PFollow) && b.Pass == s.Pass && b.ScrollMax > 0)   // a log (MRC's)
                 ?? s.Order.FirstOrDefault(b => b.Kind == BkScroll && b.Pass == s.Pass && b.ScrollMax > 0 && !b.Flag(PPane));
        if (sc != null && sc.Flag(PPane)) return false;   // version 5: a pane's keys are the board's (P_SCROLLBY answers)
        if (sc != null)
        {
            double page = sc.View * 0.9;
            SetScroll(sc, key switch { Key.PageUp => sc.Scroll - page, Key.PageDown => sc.Scroll + page, Key.Home => 0, _ => sc.ScrollMax });
            return true;
        }
        if (s.ScrollMax <= 0) return false;
        double sp = s.Space.Height * 0.9;
        s.Scroll = Math.Clamp(key switch { Key.PageUp => s.Scroll - sp, Key.PageDown => s.Scroll + sp, Key.Home => 0, _ => s.ScrollMax }, 0, s.ScrollMax);
        _sceneArr++;
        return true;
    }

    /// <summary>The innermost scroll (or list, with lists) under p that has more than shows.</summary>
    private static SBlock? ScrollAt(SceneTree s, Point p, bool lists)
    {
        for (int i = s.Order.Count - 1; i >= 0; i--)
        {
            var b = s.Order[i];
            if ((b.Kind == BkScroll || b.Kind == BkVList || b.Kind == BkViewer || lists && b.Kind == BkList) && b.Pass == s.Pass && b.ScrollMax > 0 && b.Box.Contains(p) && b.Clip.Contains(p))
                return b;
        }
        return null;
    }

    /// <summary>A focused field's editing keys (the form's, and lines when P_MULTI); true when used.</summary>
    private bool FieldKey(SceneTree s, SBlock b, Key key, bool ctrl, bool shift)
    {
        bool multi = b.Flag(PMulti) && !b.Flag(PSecret);
        if (shift && key == Key.Insert)   // paste, as the form's Shift+Insert
        {
            if (!b.Off) FieldPaste(b, multi);
            return true;
        }
        if (ctrl)
        {
            if (key == Key.Return)
            {
                SceneSubmit(s, b);
                return true;
            }
            if (key == Key.V)   // paste, to the field's max (one line: its line breaks as spaces)
            {
                if (!b.Off) FieldPaste(b, multi);
                return true;
            }
            return false;   // other Ctrl keys go to the board
        }
        if (b.Off) return false;
        string v = b.Value;
        b.Cur = Math.Clamp(b.Cur, 0, v.Length);
        switch (key)
        {
            case Key.Left: b.Cur = PrevStop(v, b.Cur); return true;
            case Key.Right: b.Cur = NextStop(v, b.Cur); return true;
            case Key.Home: b.Cur = multi ? VLineStart(b, v, b.Cur) : 0; return true;   // version 6: the drawn (wrapped) line
            case Key.End: b.Cur = multi ? VLineEnd(b, v, b.Cur) : v.Length; return true;
            case Key.Back:
                if (b.Cur > 0)
                {
                    int at = PrevStop(v, b.Cur);
                    b.Value = v.Remove(at, b.Cur - at);
                    b.Cur = at;
                    FieldChanged(b);
                }
                return true;
            case Key.Delete:
                if (b.Cur < v.Length)
                {
                    b.Value = v.Remove(b.Cur, NextStop(v, b.Cur) - b.Cur);
                    FieldChanged(b);
                }
                return true;
            case Key.Return:
                if (!multi) SceneSubmit(s, b);
                else if (FieldInsert(b, "\n")) FieldChanged(b);
                return true;
            case Key.Up: case Key.Down:
            {
                if (!multi) return false;   // one line: the arrows move the focus
                if (VLinesNow(b) != null) return VLineStep(b, v, key == Key.Up ? -1 : 1);   // version 6: by drawn lines (stale just after typing: by its own lines below)
                int ls = LineStart(v, b.Cur);
                double x = TextW(v[ls..b.Cur], Ui, SSize);
                if (key == Key.Up)
                {
                    if (ls == 0) return true;
                    int ps = LineStart(v, ls - 1);
                    b.Cur = ColumnAt(v, ps, ls - 1, x);
                }
                else
                {
                    int le = LineEnd(v, b.Cur);
                    if (le >= v.Length) return true;
                    b.Cur = ColumnAt(v, le + 1, LineEnd(v, le + 1), x);
                }
                return true;
            }
            default: return false;
        }
    }

    /// <summary>The clipboard's text into a field (it comes later: Avalonia's clipboard is asynchronous), while the
    /// same scene is up.</summary>
    private async void FieldPaste(SBlock b, bool multi)
    {
        var s = _scene;
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is not { } cb) return;
            string? t = await cb.GetTextAsync();
            if (string.IsNullOrEmpty(t) || _scene != s || !_isScene || !IsUp) return;
            t = t.Replace("\r", "");
            if (FieldInsert(b, multi ? t : t.Replace('\n', ' '))) FieldChanged(b);
            Redraw();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"RainwireMenu: scene paste: {ex.Message}");
        }
    }

    private static int LineStart(string v, int at) => at <= 0 ? 0 : v.LastIndexOf('\n', at - 1) + 1;

    private static int LineEnd(string v, int at) => v.IndexOf('\n', Math.Min(at, v.Length)) is int e and >= 0 ? e : v.Length;

    /// <summary>The place in v[start..end] nearest x from the line's start.</summary>
    private int ColumnAt(string v, int start, int end, double x)
    {
        for (int at = start; ; at = NextStop(v, at))
        {
            if (TextW(v[start..at], Ui, SSize) >= x) return at;
            if (at >= end) return end;
        }
    }

    /// <summary>Typed text into a field at the caret, as far as P_MAX characters and the byte cap allow (one line: the
    /// form's 1000 bytes); true when anything went in.</summary>
    private static bool FieldInsert(SBlock b, string text)
    {
        if (b.Off) return false;
        bool multi = b.Flag(PMulti) && !b.Flag(PSecret), changed = false;
        int max = b.U(PMax), cap = multi ? FieldMulti : MaxValue, n = Chars(b.Value), bytes = Encoding.UTF8.GetByteCount(b.Value);
        b.Cur = Math.Clamp(b.Cur, 0, b.Value.Length);
        var sb = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value < 0x20 && !(multi && rune.Value == '\n') || rune.Value == 0x7F) continue;
            if (max > 0 && n >= max) break;
            int rb = rune.Utf8SequenceLength;
            if (bytes + rb > cap) break;
            sb.Append(rune.ToString());
            n++;
            bytes += rb;
            changed = true;
        }
        if (!changed) return false;
        b.Value = b.Value.Insert(b.Cur, sb.ToString());
        b.Cur += sb.Length;
        return true;
    }

    /// <summary>The control under p (the innermost, drawn last), or null.</summary>
    private SBlock? SceneHit(SceneTree s, Point p)
    {
        for (int i = s.Order.Count - 1; i >= 0; i--)
        {
            var b = s.Order[i];
            if (IsControl(b.Kind) && b.Pass == s.Pass && b.Box.Contains(p) && b.Clip.Contains(p) && !HoverHidden(b)) return b;
        }
        return null;
    }

    private static readonly Cursor HandCursor = new(StandardCursorType.Hand), IBeamCursor = new(StandardCursorType.Ibeam);

    private void SceneMove(Point p)
    {
        if (SceneDragMove(p)) return;   // version 5: a row dragged out (RainwireSceneFiles.cs)
        if (SceneDrag6(p)) return;      // version 6: a scroll bar, divider or slider being dragged (RainwireSceneV6.cs)
        _sceneMoveAt = DateTime.UtcNow;
        var s = _scene;
        if (s == null || !_complete) return;
        var hit = SceneHit(s, p);
        _surface.Cursor = CursorAt6(s, p) ?? (hit == null || hit.Off ? (SceneHandAt(s, p) ? HandCursor : Cursor.Default)
                        : hit.Kind is BkField or BkInput ? IBeamCursor : hit.Kind == BkVList && hit.U(PSelect) == 0 ? Cursor.Default : HandCursor);
        MoveEvents(s, p);   // version 6: P_EVENTS move
        _surface.InvalidateVisual();
    }

    /// <summary>A click: a button presses (and takes the focus), a field takes the focus with the caret under the
    /// pointer, a pill picks its option, a check toggles, a list row is selected (the selected one activated).</summary>
    private void SceneClick(Point p)
    {
        var s = _scene;
        if (s == null || !_complete) return;
        SceneWaitCheck();
        if (SceneClickV2(s, p))   // version 2: pop-ups, menu buttons, chips, vlist rows, click events, links
        {
            Redraw();
            return;
        }
        var b = SceneHit(s, p);
        if (b == null || b.Off)
        {
            if (PaneAt(s, p) is SBlock pane) s.Focus = pane.Id;   // version 6: a click in a pane gives it the keys
            Redraw();
            return;
        }
        if (SFocusable(s, b)) s.Focus = b.Id;
        _caretAt = DateTime.UtcNow;
        switch (b.Kind)
        {
            case BkButton: ScenePress(s, b); break;
            case BkField: case BkInput: FieldClick(b, p); break;
            case BkChoice:
                foreach (var (box, i) in b.Hits)
                    if (box.Contains(p)) { SChoiceSet(b, i); break; }
                break;
            case BkCheck: CheckToggle(b); break;
            case BkList:
                foreach (var (box, i) in b.Hits)
                    if (box.Contains(p))
                    {
                        if (i == b.Index) ListActivate(b, i);
                        else ListSelect(b, i);
                        break;
                    }
                break;
        }
        Redraw();
    }

    /// <summary>The caret to the click (as the form's): by line, then by the text's width from the field's left.</summary>
    private void FieldClick(SBlock b, Point p)
    {
        string v = b.Value;
        if (b.Flag(PSecret))
        {
            b.Cur = v.Length;
            return;
        }
        if (b.Flag(PMulti) && b.LineH > 0 && b.VLines is { Count: > 0 } vl)   // version 6: the drawn (wrapped) lines
        {
            int li = Math.Clamp((int)Math.Floor((p.Y - b.TextY) / b.LineH), 0, vl.Count - 1);
            b.Cur = ColumnAt(v, vl[li].Start, VLineEffEnd(b, li), p.X - b.TextX);
            return;
        }
        int start = 0, end = v.Length;
        if (b.Flag(PMulti) && b.LineH > 0)
        {
            int line = Math.Max((int)((p.Y - b.TextY) / b.LineH), 0);
            for (int k = 0; k < line && LineEnd(v, start) < v.Length; k++) start = LineEnd(v, start) + 1;
            end = LineEnd(v, start);
        }
        b.Cur = ColumnAt(v, start, end, p.X - b.TextX);
    }

    /// <summary>The wheel (delta in notches, up positive; a touchpad's are fractions; version 6: RainwireSceneV6.cs
    /// WheelIn): a block that takes it (P_EVENTS wheel), a slider, the scroll, list or viewer under the pointer, the
    /// focused pane, else the whole scene.</summary>
    private void SceneWheel(double delta)
    {
        var s = _scene;
        if (s == null || !_complete) return;
        WheelIn(s, delta);
        Redraw();
    }
}
