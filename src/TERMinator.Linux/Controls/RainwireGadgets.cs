using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace TERMinator.Linux.Controls;

/// <summary>
/// Scene gadgets and the gadget engine (Rainwire docs/GADGETS.md, and docs/SCENES_V6.md "The gadget engine"; the sysop,
/// 2026-11-02: "maybe we could have those be movable, or just some sort of gadget engine in the client so the BBS can
/// send gadget commands to the client for positioning"). Gadgets are small scenes beside a menu, drawn by
/// RainwireScene.cs into their boxes, with the news panel (RainwireWire.cs) and the weather (RainwireWidgets.cs):
///   - The board places each (GadgetPlace): the column over the right or left of the header, in an order, or a box
///     floating anywhere over the menu screen (shares of it), or the tray only. News and weather take places too.
///   - The caller drags one by its title (to reorder it, dock it in the other column, or float it), resizes it (a
///     floating one by its corner, a docked one's height by its bottom edge), collapses it to its title, and has a
///     right-click menu (collapse, dock right or left, float, tray, hide, reset layout). Each change says
///     GadgetMoved;g=;dock=;order=;x=;y=;w=;h=;collapsed= (the board keeps it per caller); Reset says GadgetReset.
///   - GadgetState: collapse, expand, a count on the title, the title pulsing for attention, removal.
///   - GF_KEEP gadgets stay across menus; GF_TRAY ones also show as pills on the status bar while no menu is up (a
///     door, the terminal), opening as a pop-up there, drawn but quiet: the board may be in a door.
///   - GadgetSize;g=;w=;h= when a gadget's box changes; a click on a field in one gives it the keys until Esc.
/// Linux port of the Windows client's Controls/RainwireGadgets.cs (the master), 2026-11-02. Differences for Avalonia:
/// the right-click menu is an Avalonia ContextMenu opened on the surface; the tray is a Panel (so its Popup has a
/// parent in the tree) whose pills are drawn in Render; its pop-up is light-dismissed (WPF's StaysOpen false); a drag
/// ends when a move says the left button is up (_sceneLeftHeld), as Avalonia has no global button state.
/// </summary>
public sealed partial class RainwireMenu
{
    // ---- Rainwire internal/scene: gadgets (protocol.go, v6.go) ----
    private const byte GfClose = 0x01, GfExpand = 0x02, GfClick = 0x04, GfCollapse = 0x08, GfMove = 0x10, GfResize = 0x20,
                       GfKeep = 0x40, GfTray = 0x80;
    private const int GadgetSlots = 16;
    private const byte DockRight = 0, DockLeft = 1, DockFloat = 2, DockTray = 3;
    private const byte PlCollapsed = 0x01;
    private const byte StAttention = 0x01, StCollapse = 0x02, StExpand = 0x04, StRemove = 0x08;
    private const int SlotNews = 0xFE, SlotWeather = 0xFF;

    private static readonly Cursor SizeAllCursor = new(StandardCursorType.SizeAll),
                                   SizeNWSECursor = new(StandardCursorType.BottomRightCorner);

    /// <summary>A scene gadget: its head (Gadget: slot, flags, share of the column, name, title) and its scene, sent
    /// with the screens' scene packets wrapped with the slot, drawn smaller in its box. Patches can come any time.</summary>
    private sealed class SGadget
    {
        public int Slot, Weight;
        public byte Flags;
        public string Name = "", Title = "";
        public SceneTree? Tree;
        public Rect Box;
        public (double W, double H) SizeCur = (-1, -1);   // GadgetSize: its scene space as last measured
        public DateTime SizeAt;
        public bool SizePending;
        public bool Ready => Tree != null && Tree.Got >= Tree.Want;
    }

    /// <summary>Where a widget or gadget goes: a dock, its order there (-1: its slot's), a floating box or a column's
    /// width and its height share (shares of the menu's area or the header), and whether it's collapsed.</summary>
    private sealed class GPlace
    {
        public byte Dock;
        public int Order = -1;
        public double X, Y, W, H;
        public bool Collapsed;
        public GPlace Copy() => (GPlace)MemberwiseClone();
    }

    private readonly SortedList<int, SGadget> _gadgets = [];   // the scene gadgets, by slot
    private readonly HashSet<string> _gClosed = [], _gWide = [];   // by name: closed for the call; expanded until the menu hides
    private readonly Dictionary<string, GPlace> _gPlace = [];    // where each is now, by name (the board's or the caller's)
    private readonly Dictionary<string, GPlace> _gBoard = [];    // where the board last put each (Reset layout)
    private readonly Dictionary<string, (uint Badge, uint Color, bool Attn)> _gState = [];
    private readonly List<(string Name, Rect Box)> _gStrips = [];   // collapsed ones this frame: their title strips
    private readonly List<(string Name, Rect Box)> _gPills = [];    // ... or, with the status bar, their pills on it
    private readonly List<string> _gMovedLater = [];              // GadgetMoved made while no menu is up: sent at Show
    private Rect _gArea;                                          // the menu screen's area (floating gadgets' room)
    private (string W, int Btn)? _widgetBtnHot;                   // the button under the pointer (0 close, 1 expand, 2 collapse)
    private bool _overWidget;                                     // the pointer is over a widget's box: its cursor is set
    private string? _gTag;                                        // replies made now come from this gadget: they say g=
    private SGadget? _gFocus;                                     // the gadget that has the keys (a field clicked in it)

    /// <summary>A gadget's title strip or edge being dragged: which, moving or resizing, from where, its box then, and
    /// the box it would take now (drawn as a ghost).</summary>
    private (string Name, bool Resize, Point From, Rect Start, Rect Ghost, bool Moved)? _gDrag;

    private bool Wide(string name) => _gWide.Contains(name);

    /// <summary>The scene text scale inside a gadget: smaller than a screen's.</summary>
    private const double GadgetScale = 0.72;

    /// <summary>A widget's or gadget's buttons and what the caller may do with it: news and weather may do all of it.</summary>
    private byte WidgetFlags(string name) =>
        name is "news" or "weather" ? (byte)(GfClose | GfExpand | GfCollapse | GfMove | GfResize)
        : _gadgets.Values.FirstOrDefault(g => g.Name == name)?.Flags ?? 0;

    private GPlace PlaceOf(string name)
    {
        if (!_gPlace.TryGetValue(name, out var p)) _gPlace[name] = p = new GPlace();
        return p;
    }

    private bool Collapsed(string name) => _gPlace.TryGetValue(name, out var p) && p.Collapsed;

    // ================================================================
    // Messages from the board
    // ================================================================

    /// <summary>A scene gadget's head (Gadget): slot, flags, weight, name and title lengths, 3 reserved, name, title.
    /// Its scene follows.</summary>
    private void OnGadget(byte[] p)
    {
        if (p.Length < 12 || p[4] >= GadgetSlots) return;
        int o = 12;
        string name = Str(p, ref o, p[7]), title = Str(p, ref o, p[8]);
        if (name.Length == 0) return;
        foreach (var other in _gadgets.Values.Where(g => g.Name == name && g.Slot != p[4]).ToList()) _gadgets.Remove(other.Slot);
        _gadgets[p[4]] = new SGadget { Slot = p[4], Flags = p[5], Weight = Math.Clamp((int)p[6], 1, 100), Name = name, Title = title };
        TrayUpdate();
        Redraw();
    }

    /// <summary>A scene packet for a gadget (slot, 3 reserved, then the packet whole): read into its tree as a
    /// screen's is. Over the limits, the gadget is dropped (the menu goes on).</summary>
    private void OnGadgetPart(string verb, byte[] p, string? path)
    {
        if (p.Length < 9 || !_gadgets.TryGetValue(p[4], out var g)) return;
        var inner = p[8..];
        bool ok = true;
        switch (verb)
        {
            case "GadgetScene":
                if (inner.Length < 12) return;
                var t = SceneHeadRead(inner, out _);
                t.Flags = (byte)((t.Flags | SfFull) & ~SfOver);   // it fills its box
                ok = t.Want <= SceneMaxBlocks;
                g.Tree = t;
                break;
            case "GadgetBlocks": if (g.Tree != null) ok = SceneBlocksInto(g.Tree, inner) >= 0; break;
            case "GadgetCells": if (g.Tree != null) SceneCellsInto(g.Tree, inner); break;
            case "GadgetImage": if (g.Tree != null) SceneImageInto(g.Tree, inner, path); break;
            case "GadgetPatch": if (g.Tree != null) ok = ScenePatchInto(g.Tree, inner); break;
            case "GadgetDraw": if (g.Tree != null) SceneDrawInto(g.Tree, inner); break;   // version 6: a canvas's drawing
        }
        if (!ok) _gadgets.Remove(g.Slot);
        _sceneGen++;
        Redraw();
    }

    /// <summary>The name a GadgetPlace or GadgetState slot means: news, weather or a scene gadget's (null: none).</summary>
    private string? SlotName(int slot) =>
        slot == SlotNews ? "news" : slot == SlotWeather ? "weather" : _gadgets.TryGetValue(slot, out var g) ? g.Name : null;

    /// <summary>GadgetPlace: slot, dock, order, flags, then x, y, w, h (f32 shares).</summary>
    private void OnGadgetPlace(byte[] p)
    {
        if (p.Length < 24 || SlotName(p[4]) is not string name) return;
        double F(int o) => BitConverter.ToSingle(p, o) is var f && float.IsFinite(f) ? Math.Clamp(f, 0, 1) : 0;
        var pl = new GPlace
        {
            Dock = Math.Min(p[5], DockTray), Order = p[6], Collapsed = (p[7] & PlCollapsed) != 0,
            X = F(8), Y = F(12), W = F(16), H = F(20),
        };
        _gPlace[name] = pl;
        _gBoard[name] = pl.Copy();
        TrayUpdate();
        Redraw();
    }

    /// <summary>GadgetState: slot, flags (attention, collapse, expand, remove), the count on its title and its color.</summary>
    private void OnGadgetState(byte[] p)
    {
        if (p.Length < 16 || SlotName(p[4]) is not string name) return;
        byte f = p[5];
        if ((f & StRemove) != 0 && p[4] < GadgetSlots)
        {
            _gadgets.Remove(p[4]);
            if (_gFocus?.Name == name) _gFocus = null;
        }
        if ((f & StCollapse) != 0) PlaceOf(name).Collapsed = true;
        if ((f & StExpand) != 0) PlaceOf(name).Collapsed = false;
        bool attn = (f & StAttention) != 0 || _gState.TryGetValue(name, out var was) && was.Attn;   // until the caller looks
        _gState[name] = (U32(p, 8), U32(p, 12), attn);
        TrayUpdate();
        Redraw();
    }

    /// <summary>A new menu: its gadgets come with it; kept ones (GF_KEEP, GF_TRAY) stay until the board removes them.</summary>
    private void GadgetsForMenu()
    {
        foreach (var g in _gadgets.Values.Where(g => (g.Flags & (GfKeep | GfTray)) == 0).ToList()) _gadgets.Remove(g.Slot);
        _gFocus = null;   // the keys go back to the new menu
        TrayUpdate();
    }

    /// <summary>The call ended: every widget open, narrow and where the board puts it; no gadgets.</summary>
    private void WidgetsForget()
    {
        _gClosed.Clear();
        _gWide.Clear();
        _gadgets.Clear();
        _gPlace.Clear();
        _gBoard.Clear();
        _gState.Clear();
        _gMovedLater.Clear();
        _gFocus = null;
        _gDrag = null;
        TrayClose();
        TrayUpdate();
    }

    // ================================================================
    // Layout: the columns and the floating boxes
    // ================================================================

    /// <summary>One widget or gadget to place this frame.</summary>
    private sealed record GEntry(string Name, int Weight, int DefOrder, Action<Rect> Set);

    /// <summary>Places every widget and gadget (header hr, the menu's area _gArea): the right and left columns, each
    /// in order, sharing their height by weight (a collapsed one only its title strip); floating ones in their boxes.
    /// Sets _wireBox, _wxBox, each gadget's Box and the collapsed strips (Empty for one not drawn).</summary>
    private void WidgetLayout(Rect hr)
    {
        _wireBox = _wxBox = default;
        _gStrips.Clear();
        _gPills.Clear();
        foreach (var g in _gadgets.Values) g.Box = default;
        if (!_complete || _isNews || _isPage || _isList || _isForm || _isPic || _isScene) return;
        var all = new List<GEntry>();
        if (_wire != null && !_gClosed.Contains("news")) all.Add(new("news", 66, 0, r => _wireBox = r));
        if (_wx != null && !_gClosed.Contains("weather")) all.Add(new("weather", 34, 1, r => _wxBox = r));
        foreach (var g in _gadgets.Values)
            if (g.Ready && !_gClosed.Contains(g.Name))
            {
                var gg = g;
                all.Add(new(g.Name, g.Weight, 2 + g.Slot, r => gg.Box = r));
            }
        if (all.Count == 0) return;
        // collapsed ones go to the status bar as pills (sysop 2026-11-02: "when minimized maybe it makes sense for them
        // to go to the Rainwire bar at the bottom of the screen"); with no bar, a strip where they were
        if (_st.Status && !IsEmpty(_gArea))
        {
            StatusPills(all.Where(e => Collapsed(e.Name)).Select(e => e.Name).ToList());
            all = all.Where(e => !Collapsed(e.Name)).ToList();
        }
        foreach (var dock in new[] { DockRight, DockLeft })
        {
            var col = all.Where(e => (_gPlace.TryGetValue(e.Name, out var p) ? p.Dock : DockRight) == dock)
                         .OrderBy(e => _gPlace.TryGetValue(e.Name, out var p) && p.Order >= 0 ? p.Order : e.DefOrder).ThenBy(e => e.DefOrder).ToList();
            if (col.Count > 0 && !IsEmpty(hr)) LayoutColumn(hr, col, dock == DockLeft);
        }
        foreach (var e in all)
        {
            if (!_gPlace.TryGetValue(e.Name, out var p) || p.Dock != DockFloat || IsEmpty(_gArea)) continue;
            var a = _gArea;
            double w = Math.Clamp(p.W > 0 ? p.W * a.Width : 220 * _u, Math.Min(110 * _u, a.Width), a.Width);
            double h = Math.Clamp(p.H > 0 ? p.H * a.Height : 160 * _u, Math.Min(60 * _u, a.Height), a.Height);
            double x = Math.Clamp(a.X + p.X * a.Width, a.X, Math.Max(a.Right - w, a.X)), y = Math.Clamp(a.Y + p.Y * a.Height, a.Y + 10 * _u, Math.Max(a.Bottom - h, a.Y + 10 * _u));
            var box = R(x, y, w, h);
            if (p.Collapsed) _gStrips.Add((e.Name, R(x, y, w, StripH)));
            else e.Set(box);
        }
    }

    private double StripH => 24 * _u;

    /// <summary>The collapsed widgets' pills on the status bar, after its title, as many as fit before its right end.</summary>
    private void StatusPills(List<string> names)
    {
        if (names.Count == 0) return;
        double sh = StatusH * _u, y = _gArea.Bottom, ph = sh - 8 * _u;
        string mins = _minutes != 0xFFFF ? $" · {_minutes}m left" : "";
        double right = _gArea.Width - 12 * _u - TextW(_bbs, UiBold, 13)
                       - TextW($"node {_node} · {(_clock.Length > 0 ? _clock : "--:--")}{mins} · ", Ui, 13) - 16 * _u;
        double x = 12 * _u + TextW(">", UiBold, 13) + 8 * _u + TextW(_head.Title, Ui, 13) + 24 * _u;
        foreach (var name in names)
        {
            double w = PillW(name);
            if (x + w > right) break;
            _gPills.Add((name, R(x, y + (sh - ph) / 2, w, ph)));
            x += w + 6 * _u;
        }
    }

    private double PillW(string name) =>
        TextW(TitleOf(name).ToUpperInvariant(), UiBold, 11) + 22 * _u + (_gState.TryGetValue(name, out var st) && st.Badge > 0 ? 24 * _u : 0);

    /// <summary>A collapsed widget's pill on the status bar: its title and count; lit under the pointer, pulsing for
    /// attention. A click opens it again where it was.</summary>
    private void DrawPill(DrawingContext dc, string name, Rect box)
    {
        var (bg, border, tc) = FrameColors(name);
        var st = _gState.TryGetValue(name, out var s) ? s : default;
        bool hot = box.Contains(_mouse);
        var acc = Mcol(Mc.Accent);
        var edge = st.Attn ? Mix(border, acc, 0.5 + 0.5 * Math.Sin(Now / 1000.0 * 5)) : hot ? acc : border;
        var pen = new Pen(B(edge), Math.Max(_u, 1));
        double r = box.Height / 2;
        dc.DrawRectangle(B(hot ? Mix(bg, acc, 0.25) : WithA(bg, 230)), pen, box, r, r);
        var t = Text(TitleOf(name).ToUpperInvariant(), UiBold, 11, tc);
        dc.DrawText(t, new Point(box.X + 11 * _u, box.Y + (box.Height - t.Height) / 2));
        if (st.Badge > 0)
        {
            var bt = Text(st.Badge > 99 ? "99+" : st.Badge.ToString(), UiBold, 10, Colors.Black);
            double bh = box.Height - 6 * _u, bw = Math.Max(bt.Width + 8 * _u, bh), bx = box.Right - 9 * _u - bw;
            var bc = (st.Color & 0xFF000000) == 0 ? C(st.Color) : acc;
            dc.DrawRectangle(B(bc), null, R(bx, box.Y + 3 * _u, bw, bh), bh / 2, bh / 2);
            dc.DrawText(bt, new Point(bx + (bw - bt.Width) / 2, box.Y + (box.Height - bt.Height) / 2));
        }
    }

    private string? PillAt(Point p) => _gPills.FirstOrDefault(x => x.Box.Contains(p)).Name;

    /// <summary>A column over the right (or left) of the header: each its weight's share of the height (P_H's share
    /// when the caller sized it), a collapsed one its title strip, an expanded one twice as wide.</summary>
    private void LayoutColumn(Rect hr, List<GEntry> col, bool left)
    {
        var p0 = _gPlace.TryGetValue(col[0].Name, out var pp) ? pp : null;
        double pct = p0 != null && p0.W > 0 ? p0.W * 100 : _wire?.Width ?? _wx?.Width ?? 22;
        // room above each for its buttons, which sit on its top border (sysop 2026-10-22: they ran into the top of the
        // screen and the gadget above)
        double inset = 8 * _u, gap = 18 * _u, cw = hr.Width * pct / 100;
        double top = hr.Y + 14 * _u, h = hr.Height - 14 * _u - inset;
        int open = col.Count(e => !Collapsed(e.Name));
        double strips = col.Count(e => Collapsed(e.Name)) * StripH, avail = h - gap * (col.Count - 1) - strips;
        if ((open > 0 && avail < 60 * _u * open) || cw < 110 * _u) return;   // too cramped to read: the header alone
        // a height the caller dragged (H: its share of the column's room, at least 15%) first; the rest share what's
        // left by weight (sysop 2026-11-02: a float's own height, kept as its column share, docked the weather tiny)
        double Share(GEntry e) => _gPlace.TryGetValue(e.Name, out var p) && p.H > 0 ? Math.Clamp(p.H, 0.15, 0.9) : 0;
        var opened = col.Where(e => !Collapsed(e.Name)).ToList();
        double fixedSum = opened.Sum(Share), weights = opened.Where(e => Share(e) == 0).Sum(e => (double)e.Weight);
        double scale = weights > 0 ? Math.Min(1, 0.85 / Math.Max(fixedSum, 1e-9)) : 1 / Math.Max(fixedSum, 1e-9);
        double rest = Math.Max(1 - fixedSum * scale, 0), y = top;
        foreach (var e in col)
        {
            double w = Math.Min(cw * (Wide(e.Name) ? 2 : 1), hr.Width - 2 * inset) - inset;
            double x = left ? hr.X + inset * 2 : hr.Right - inset - w;
            if (Collapsed(e.Name))
            {
                _gStrips.Add((e.Name, R(x, y, w, StripH)));
                y += StripH + gap;
                continue;
            }
            double hh = Share(e) > 0 ? avail * Share(e) * scale : weights > 0 ? avail * rest * e.Weight / weights : avail;
            e.Set(R(x, y, w, hh));
            y += hh + gap;
        }
    }

    // ================================================================
    // The buttons on a widget's top border: close, expand, collapse (right to left)
    // ================================================================

    private static readonly byte[] BtnFlags = [GfClose, GfExpand, GfCollapse];

    private Rect WidgetButton(Rect box, int btn, byte flags, bool strip)
    {
        double s = 14 * _u, x = box.Right - 12 * _u - s;
        for (int i = 0; i < btn; i++)
            if (HasBtn(i, flags, strip)) x -= s + 5 * _u;
        return R(x, strip ? box.Y + (box.Height - s) / 2 : box.Y - s / 2, s, s);
    }

    private static bool HasBtn(int btn, byte flags, bool strip) => (flags & BtnFlags[btn]) != 0 && !(strip && btn == 1);

    /// <summary>A widget's buttons (those its flags have), on its top border at the right (a strip's: inside it).</summary>
    private void DrawWidgetButtons(DrawingContext dc, Rect box, string name, byte flags, Color bg, Color border, Color fg, bool strip = false)
    {
        for (int btn = 0; btn < 3; btn++)
        {
            if (!HasBtn(btn, flags, strip)) continue;
            var b = WidgetButton(box, btn, flags, strip);
            bool hot = _widgetBtnHot is { } h && h.W == name && h.Btn == btn;
            var pen = new Pen(B(hot ? fg : border), Math.Max(1, 1.2 * _u));
            dc.DrawRectangle(B(hot ? Mix(bg, border, 0.5) : WithA(bg, 240)), pen, b, 4 * _u, 4 * _u);
            var g = new Pen(B(fg), 1.4 * _u) { LineCap = PenLineCap.Round };
            double cx = b.X + b.Width / 2, cy = b.Y + b.Height / 2, a = 3 * _u;
            switch (btn)
            {
                case 0:
                    dc.DrawLine(g, new Point(cx - a, cy - a), new Point(cx + a, cy + a));
                    dc.DrawLine(g, new Point(cx - a, cy + a), new Point(cx + a, cy - a));
                    break;
                case 1:
                {
                    // two chevrons pointing out (expand) or in (back to narrow)
                    double d = Wide(name) ? -1 : 1, e = 1.5 * _u;
                    foreach (int side in new[] { -1, 1 })
                    {
                        double tip = cx + side * (e + 2 * _u) * d, back = tip - side * 2.5 * _u * d;
                        dc.DrawLine(g, new Point(back, cy - 3 * _u), new Point(tip, cy));
                        dc.DrawLine(g, new Point(tip, cy), new Point(back, cy + 3 * _u));
                    }
                    break;
                }
                case 2:   // a chevron: down to collapse to the status bar (up with no bar), down to open a strip again
                {
                    double d = strip || _st.Status ? 1 : -1;   // down: to the status bar (or, a strip, open again); up with no bar
                    dc.DrawLine(g, new Point(cx - a, cy - d * 1.5 * _u), new Point(cx, cy + d * 1.5 * _u));
                    dc.DrawLine(g, new Point(cx, cy + d * 1.5 * _u), new Point(cx + a, cy - d * 1.5 * _u));
                    break;
                }
            }
        }
    }

    /// <summary>Every widget drawn now: its name, box, button flags, and whether it's a collapsed strip.</summary>
    private IEnumerable<(string Name, Rect Box, byte Flags, bool Strip)> WidgetBoxes()
    {
        if (!IsEmpty(_wireBox)) yield return ("news", _wireBox, WidgetFlags("news"), false);
        if (!IsEmpty(_wxBox)) yield return ("weather", _wxBox, WidgetFlags("weather"), false);
        foreach (var g in _gadgets.Values)
            if (!IsEmpty(g.Box)) yield return (g.Name, g.Box, g.Flags, false);
        foreach (var (name, box) in _gStrips) yield return (name, box, WidgetFlags(name), true);
    }

    private (string W, int Btn)? WidgetButtonAt(Point p)
    {
        foreach (var (name, box, flags, strip) in WidgetBoxes())
            for (int btn = 0; btn < 3; btn++)
                if (HasBtn(btn, flags, strip) && WidgetButton(box, btn, flags, strip).Contains(p)) return (name, btn);
        return null;
    }

    /// <summary>The title strip a drag starts from (the band its title sits on), and a resize grip.</summary>
    private Rect TitleBand(Rect box, bool strip) => strip ? box : R(box.X, box.Y - 9 * _u, box.Width, 22 * _u);

    private (string Name, bool Resize, Rect Box)? GrabAt(Point p)
    {
        foreach (var (name, box, flags, strip) in WidgetBoxes().Reverse())
        {
            bool floating = _gPlace.TryGetValue(name, out var pl) && pl.Dock == DockFloat;
            if (!strip && (flags & GfResize) != 0)
            {
                var grip = floating ? R(box.Right - 14 * _u, box.Bottom - 14 * _u, 16 * _u, 16 * _u) : R(box.X, box.Bottom - 4 * _u, box.Width, 9 * _u);
                if (grip.Contains(p)) return (name, true, box);
            }
            if ((flags & GfMove) != 0 && TitleBand(box, strip).Contains(p)) return (name, false, box);
        }
        return null;
    }

    private SGadget? GadgetAt(Point p) => _gadgets.Values.FirstOrDefault(g => !IsEmpty(g.Box) && g.Box.Contains(p));

    // ================================================================
    // The pointer over the widgets: hover, clicks, drags, the wheel, keys
    // ================================================================

    /// <summary>The pointer over a widget's button, the weather or a scene gadget: true when it's theirs (hover, the
    /// hand over what a click does, the sizing arrows over a grip).</summary>
    private bool WidgetMove(Point p)
    {
        if (_gDrag != null) return true;
        var btn = WidgetButtonAt(p);
        bool wx = btn == null && !IsEmpty(_wxBox) && _wxBox.Contains(p);
        var g = btn == null && !wx ? GadgetAt(p) : null;
        if (!Equals(btn, _widgetBtnHot) || wx != _wxHot)
        {
            _widgetBtnHot = btn;
            _wxHot = wx;
            Redraw();
        }
        // pointing at one ends its call for attention
        foreach (var (name, box, _, _) in WidgetBoxes())
            if (box.Contains(p) && _gState.TryGetValue(name, out var st) && st.Attn)
            {
                _gState[name] = st with { Attn = false };
                Redraw();
            }
        if (PillAt(p) is string pill)   // a collapsed one's pill on the status bar: a click opens it
        {
            if (_gState.TryGetValue(pill, out var ps) && ps.Attn) _gState[pill] = ps with { Attn = false };
            _surface.Cursor = HandCursor;
            _overWidget = true;
            Redraw();
            return true;
        }
        var grab = btn == null ? GrabAt(p) : null;
        bool strip = _gStrips.Any(s => s.Box.Contains(p));
        if (btn != null || wx || g != null || grab != null || strip)
        {
            Cursor? cur = btn != null || wx ? HandCursor : null;
            if (cur == null && grab is { } gr)
                cur = !gr.Resize ? SizeAllCursor : _gPlace.TryGetValue(gr.Name, out var pl) && pl.Dock == DockFloat ? SizeNWSECursor : SizeNSCursor;
            if (cur == null && g?.Tree != null)
            {
                var t = g.Tree;
                InGadget(g, () => MoveEvents(t, p));
                cur = InGadget(g, () => CursorAt6(t, p))
                      ?? (SceneHit(t, p) is { Off: false } hit ? hit.Kind is BkField or BkInput ? IBeamCursor : HandCursor
                          : (g.Flags & GfClick) != 0 || InGadget(g, () => SceneHandAt(t, p)) ? HandCursor : null);
            }
            _surface.Cursor = cur ?? Cursor.Default;
            _overWidget = true;
            return true;
        }
        if (_overWidget)
        {
            _overWidget = false;
            _surface.Cursor = Cursor.Default;   // off them: the news (WireMove) and the menu set their own
        }
        return false;
    }

    /// <summary>The left button down at p over the widgets: a title strip or grip starts a drag; a scroll bar, divider
    /// or slider in a gadget starts its own. True when one started (no click follows).</summary>
    private bool GadgetDown(Point p)
    {
        if (WidgetButtonAt(p) != null) return false;
        if (GrabAt(p) is { } gr)
        {
            _gDrag = (gr.Name, gr.Resize, p, gr.Box, gr.Box, false);
            SurfaceCapture();
            return true;
        }
        if (GadgetAt(p) is SGadget g && g.Tree != null && InGadget(g, () => SceneDown6(g.Tree, p, g)))
        {
            _gFocus = g;
            return true;
        }
        return false;
    }

    /// <summary>A widget being dragged: its ghost follows the pointer. True while one is.</summary>
    private bool GadgetDragMove(Point p)
    {
        if (_gDrag is not { } d) return false;
        if (!_sceneLeftHeld)   // let go where the surface didn't hear it: the drag ends there
        {
            GadgetDragEnd(p);
            return true;
        }
        double dx = p.X - d.From.X, dy = p.Y - d.From.Y;
        bool moved = d.Moved || Math.Abs(dx) + Math.Abs(dy) > 5 * _u;
        var r = d.Start;
        bool floating = _gPlace.TryGetValue(d.Name, out var pl) && pl.Dock == DockFloat;
        Rect ghost = !d.Resize ? new Rect(r.X + dx, r.Y + dy, r.Width, r.Height)
                   : floating ? R(r.X, r.Y, Math.Max(r.Width + dx, 110 * _u), Math.Max(r.Height + dy, 60 * _u))
                   : R(r.X, r.Y, r.Width, Math.Max(r.Height + dy, 60 * _u));
        _gDrag = d with { Ghost = ghost, Moved = moved };
        _surface.Cursor = d.Resize ? (floating ? SizeNWSECursor : SizeNSCursor) : SizeAllCursor;
        Redraw();
        return true;
    }

    /// <summary>The drag let go: where the ghost is decides the place (a column's side docks it there at that place in
    /// its order; elsewhere it floats); a resize sets its box or its share of the column. Not moved: a click.</summary>
    private bool GadgetDragEnd(Point p)
    {
        if (_gDrag is not { } d) return false;
        _gDrag = null;
        SurfaceRelease();
        _surface.Cursor = Cursor.Default;
        if (!d.Moved)
        {
            Redraw();
            return false;   // a press and release in place: a click on its title (GF_CLICK opens its door)
        }
        var pl = PlaceOf(d.Name);
        double bw = Bounds.Width, bh = Bounds.Height;
        var a = IsEmpty(_gArea) ? R(0, 0, bw, bh) : _gArea;
        var g = d.Ghost;
        var hr = HeaderRect(bw, bh);
        if (d.Resize)
        {
            if (pl.Dock == DockFloat)
            {
                pl.W = g.Width / a.Width;
                pl.H = g.Height / a.Height;
            }
            else   // its share of the column's room: what the open ones in it take now
            {
                double room = WidgetBoxes().Where(w => !w.Strip && (_gPlace.TryGetValue(w.Name, out var wp) ? wp.Dock : DockRight) == pl.Dock)
                                           .Sum(w => w.Box.Height);
                pl.H = Math.Clamp(g.Height / Math.Max(room, 1), 0.15, 0.9);
            }
            GadgetMovedSay(d.Name);
        }
        else
        {
            double cx = g.X + g.Width / 2, cy = g.Y + g.Height / 2;
            double colW = hr.Width * (_wire?.Width ?? _wx?.Width ?? 22) / 100;
            byte dock = DockFloat;
            if (!IsEmpty(hr) && cy >= hr.Y && cy <= hr.Bottom + 20 * _u)
            {
                if (cx >= hr.Right - colW) dock = DockRight;
                else if (cx <= hr.X + colW) dock = DockLeft;
            }
            if (dock == DockFloat)
            {
                pl.Dock = DockFloat;
                pl.X = Math.Clamp((g.X - a.X) / a.Width, 0, 1);
                pl.Y = Math.Clamp((g.Y - a.Y) / a.Height, 0, 1);
                if (pl.W <= 0 || pl.H <= 0) { pl.W = g.Width / a.Width; pl.H = g.Height / a.Height; }
                GadgetMovedSay(d.Name);
            }
            else Redock(d.Name, dock, cy);
        }
        Redraw();
        return true;
    }

    /// <summary>name docked in a column at height y among the others there: every one in that column renumbered (and
    /// told to the board, as each order changed).</summary>
    private void Redock(string name, byte dock, double y)
    {
        var pl = PlaceOf(name);
        var others = WidgetBoxes().Where(w => w.Name != name && (_gPlace.TryGetValue(w.Name, out var p) ? p.Dock : DockRight) == dock)
                                  .OrderBy(w => w.Box.Y).Select(w => (w.Name, w.Box)).ToList();
        int at = others.Count(o => o.Box.Y + o.Box.Height / 2 < y);
        var order = others.Select(o => o.Name).ToList();
        order.Insert(Math.Min(at, order.Count), name);
        if (pl.Dock != dock) pl.W = pl.H = 0;   // a floating box's size means nothing in a column: its usual height there
        pl.Dock = dock;
        for (int i = 0; i < order.Count; i++)
        {
            var op = PlaceOf(order[i]);
            if (op.Dock != dock || op.Order != i || order[i] == name)
            {
                op.Dock = dock;
                op.Order = i;
                GadgetMovedSay(order[i]);
            }
        }
    }

    /// <summary>GadgetMoved for name as it's placed now (kept until the next Show while no menu is up).</summary>
    private void GadgetMovedSay(string name)
    {
        var pl = PlaceOf(name);
        string msg = $"GadgetMoved;g={name};dock={pl.Dock};order={Math.Max(pl.Order, 0)};x={pl.X.ToString("0.000", Inv)};y={pl.Y.ToString("0.000", Inv)};" +
                     $"w={pl.W.ToString("0.000", Inv)};h={pl.H.ToString("0.000", Inv)};collapsed={(pl.Collapsed ? 1 : 0)}";
        if (IsUp) Send?.Invoke(msg);
        else
        {
            _gMovedLater.RemoveAll(m => m.StartsWith($"GadgetMoved;g={name};", StringComparison.Ordinal));
            _gMovedLater.Add(msg);
        }
        TrayUpdate();
    }

    /// <summary>The menu came up: the moves made while it was down go to the board.</summary>
    private void GadgetMovesFlush()
    {
        foreach (var m in _gMovedLater) Send?.Invoke(m);
        _gMovedLater.Clear();
    }

    /// <summary>A click on a widget's button (close, expand or collapse it), the weather (its door), or a scene gadget
    /// (a control in it, else its own click): true when it was one of theirs.</summary>
    private bool WidgetClick(Point p)
    {
        if (WidgetButtonAt(p) is { } b)
        {
            switch (b.Btn)
            {
                case 0:   // closed here at once, and off on the board (the news and weather in their doors: sysop 2026-10-22)
                    _gClosed.Add(b.W);
                    Reply("WidgetOff;w=" + b.W);
                    break;
                case 1:
                    if (!_gWide.Remove(b.W)) _gWide.Add(b.W);
                    break;
                case 2:
                    PlaceOf(b.W).Collapsed = !Collapsed(b.W);
                    GadgetMovedSay(b.W);
                    break;
            }
            _widgetBtnHot = null;
            _wireLaidW = -1;   // the news list's width changes
            _surface.Cursor = Cursor.Default;
            Redraw();
            return true;
        }
        if (PillAt(p) is string pill)   // a pill on the status bar: open again where it was
        {
            PlaceOf(pill).Collapsed = false;
            GadgetMovedSay(pill);
            _surface.Cursor = Cursor.Default;
            Redraw();
            return true;
        }
        if (_gStrips.Any(s => s.Box.Contains(p))) return true;   // a collapsed one's title: its buttons open it
        if (!IsEmpty(_wxBox) && _wxBox.Contains(p))
        {
            if (!_waiting && _complete)
            {
                _waiting = true;
                Reply("Widget;w=weather");
                Redraw();
            }
            return true;
        }
        if (GadgetAt(p) is not { } g)
        {
            _gFocus = null;   // a click outside the gadgets: the keys go back to the menu
            return false;
        }
        var t = g.Tree;
        bool control = t != null && (SceneHit(t, p) is { Off: false } || InGadget(g, () => SceneHandAt(t, p) || PaneAt(t, p) != null));
        if (control && t != null)
        {
            InGadget(g, () => SceneClick(p));
            _gFocus = SceneFocused(t) is { Kind: BkField or BkInput or BkSlider or BkVList or BkList or BkScroll } ? g : _gFocus;
        }
        else if ((g.Flags & GfClick) != 0 && !_waiting && _complete)
        {
            _waiting = true;
            Reply("Widget;w=" + g.Name);
        }
        Redraw();
        return true;
    }

    /// <summary>A right-click on a widget's title or strip: the client's own menu for it. True when it was one.</summary>
    private bool WidgetRightClick(Point p)
    {
        foreach (var (name, box, flags, strip) in WidgetBoxes().Concat(_gPills.Select(x => (x.Name, x.Box, WidgetFlags(x.Name), true))))
        {
            if (!TitleBand(box, strip).Contains(p) && !(strip && box.Contains(p))) continue;
            var menu = new ContextMenu();
            void Item(string label, bool enabled, Action act)
            {
                var mi = new MenuItem { Header = label, IsEnabled = enabled };
                mi.Click += (_, _) => { act(); Redraw(); };
                menu.Items.Add(mi);
            }
            var pl = PlaceOf(name);
            if ((flags & GfCollapse) != 0)
                Item(pl.Collapsed ? "_Expand" : "_Collapse", true, () => { pl.Collapsed = !pl.Collapsed; GadgetMovedSay(name); });
            if ((flags & GfResize) != 0)   // back to its usual size: its weight's share of a column, or a float's 220 x 160
                Item("Reset _Size", pl.H > 0 || pl.Dock == DockFloat && pl.W > 0, () =>
                {
                    pl.H = 0;
                    if (pl.Dock == DockFloat) pl.W = 0;
                    GadgetMovedSay(name);
                });
            if ((flags & GfMove) != 0)
            {
                Item("Dock _Right", pl.Dock != DockRight, () => Redock(name, DockRight, double.MaxValue));
                Item("Dock _Left", pl.Dock != DockLeft, () => Redock(name, DockLeft, double.MaxValue));
                Item("_Float", pl.Dock != DockFloat, () =>
                {
                    var a = IsEmpty(_gArea) ? R(0, 0, Bounds.Width, Bounds.Height) : _gArea;
                    pl.Dock = DockFloat;
                    pl.X = Math.Clamp((box.X - a.X) / a.Width, 0, 1);
                    pl.Y = Math.Clamp((box.Y - a.Y) / a.Height, 0, 1);
                    pl.W = box.Width / a.Width;
                    pl.H = Math.Max(box.Height, 160 * _u) / a.Height;
                    GadgetMovedSay(name);
                });
                if (_gadgets.Values.Any(g => g.Name == name && (g.Flags & GfTray) != 0))
                    Item("Move to the _Tray", pl.Dock != DockTray, () => { pl.Dock = DockTray; GadgetMovedSay(name); });
            }
            if ((flags & GfClose) != 0)
                Item("_Hide", true, () => { _gClosed.Add(name); Reply("WidgetOff;w=" + name); });
            if (menu.Items.Count > 0) menu.Items.Add(new Separator());
            Item("Reset _Layout", true, GadgetReset);
            menu.Open(_surface);
            return true;
        }
        return false;
    }

    /// <summary>Reset layout: every widget where the board last put it, and the board forgets the caller's places.</summary>
    private void GadgetReset()
    {
        _gPlace.Clear();
        foreach (var (k, v) in _gBoard) _gPlace[k] = v.Copy();
        _gWide.Clear();
        _gMovedLater.Clear();
        if (IsUp) Send?.Invoke("GadgetReset");
        else _gMovedLater.Add("GadgetReset");
        TrayUpdate();
    }

    /// <summary>The wheel over a scene gadget (notches): the scroll, list or slider under the pointer (a crawling one
    /// holds a while). True when the pointer is over a gadget.</summary>
    private bool GadgetWheel(double delta)
    {
        if (GadgetAt(_mouse) is not { } g) return false;
        if (g.Tree != null)
        {
            var t = g.Tree;
            InGadget(g, () => WheelIn(t, delta));
            _sceneGen++;
            Redraw();
        }
        return true;
    }

    /// <summary>A key for the gadget that has the keys (a field clicked in it): its own, Esc gives them back to the
    /// menu. True when it's taken (always, while one has them).</summary>
    private bool GadgetKey(Key key, KeyModifiers mods)
    {
        if (_gFocus is not { Tree: SceneTree t } g || !_gadgets.ContainsValue(g))
        {
            _gFocus = null;
            return false;
        }
        if (key == Key.Escape && t.Pops.Count == 0)
        {
            _gFocus = null;
            Redraw();
            return true;
        }
        InGadget(g, () => SceneKey(key, mods));
        Redraw();
        return true;
    }

    private bool GadgetText(string text)
    {
        if (_gFocus is not { Tree: not null } g || !_gadgets.ContainsValue(g)) return false;
        InGadget(g, () => SceneText(text));
        Redraw();
        return true;
    }

    /// <summary>Runs f as if gadget g's scene were the screen's (the scene code works on _scene): its replies say g=,
    /// at the gadget's text scale; the menu's own state is put back after.</summary>
    private T InGadget<T>(SGadget g, Func<T> f)
    {
        var (scene, isScene, waiting, pressed, tag, scale, complete) = (_scene, _isScene, _waiting, _scenePressed, _gTag, _sScale, _complete);
        _scene = g.Tree;
        _isScene = true;
        _waiting = false;
        _gTag = g.Name;
        _sScale = GadgetScale;
        _complete = true;
        try { return f(); }
        finally
        {
            (_scene, _isScene, _waiting, _scenePressed, _gTag, _sScale, _complete) = (scene, isScene, waiting, pressed, tag, scale, complete);
        }
    }

    private void InGadget(SGadget g, Action f) => InGadget(g, () => { f(); return 0; });

    // ================================================================
    // Drawing
    // ================================================================

    /// <summary>The scene gadgets: each a box like the weather's in its scene's colors (panel, border, title), the
    /// title on the top border with its count, its buttons, and its scene drawn inside at GadgetScale; the collapsed
    /// strips; and the ghost of one being dragged.</summary>
    private void DrawGadgets(DrawingContext dc)
    {
        List<int>? failed = null;
        foreach (var g in _gadgets.Values)
        {
            if (!g.Ready || IsEmpty(g.Box)) continue;
            try { DrawGadget(dc, g, g.Box, true); }
            catch (Exception ex)
            {
                // One gadget that can't draw goes, so the others and the dialog over them still do
                System.Diagnostics.Debug.WriteLine("RainwireMenu: gadget draw failed: " + ex);
                (failed ??= []).Add(g.Slot);
            }
        }
        if (failed != null)
            foreach (int slot in failed) _gadgets.Remove(slot);
        foreach (var (name, box) in _gStrips) DrawStrip(dc, name, box);
        foreach (var (name, box) in _gPills) DrawPill(dc, name, box);
        if (_gDrag is { Moved: true } d)
        {
            var a = Mcol(Mc.Accent);
            var pen = new Pen(B(WithA(a, 220)), 2 * _u) { DashStyle = DashStyle.Dash };
            dc.DrawRectangle(B(WithA(a, 30)), pen, d.Ghost, 8 * _u, 8 * _u);
        }
    }

    /// <summary>The colors a widget's frame takes: a gadget's scene's, the news panel's, the weather's, else the menu's.</summary>
    private (Color Bg, Color Border, Color Title) FrameColors(string name)
    {
        if (_gadgets.Values.FirstOrDefault(x => x.Name == name)?.Tree is SceneTree t)
            return (C(t.Colors[(int)Sc.Panel]), C(t.Colors[(int)Sc.Border]), C(t.Colors[(int)Sc.Title]));
        if (name == "news" && _wire != null) return (Wcol(Wc.Bg), Wcol(Wc.Border), Wcol(Wc.Title));
        if (name == "weather" && _wx != null) return (Xw(Wc.Bg), Xw(Wc.Border), Xw(Wc.Title));
        return (Mcol(Mc.MenuBg), Mcol(Mc.MenuBorder), Mcol(Mc.PanelTitle));
    }

    private string TitleOf(string name) =>
        _gadgets.Values.FirstOrDefault(g => g.Name == name) is { Title.Length: > 0 } sg ? sg.Title   // news and weather are scene gadgets from Rainwire 0.9.196
        : name == "news" ? _wire?.Title ?? "News" : name == "weather" ? _wx?.Title ?? "Weather" : name;

    /// <summary>The title (pulsing for attention) and its count, at x on the line y; returns the x after them.</summary>
    private double DrawGTitle(DrawingContext dc, string name, Color bg, Color tc, double x, double y, bool onBorder)
    {
        var st = _gState.TryGetValue(name, out var s) ? s : default;
        var col = st.Attn ? Mix(tc, Mcol(Mc.Accent), 0.5 + 0.5 * Math.Sin(Now / 1000.0 * 5)) : tc;
        var title = Text(TitleOf(name).ToUpperInvariant(), UiBold, 12, col);
        double bw = 0;
        FormattedText? badge = null;
        if (st.Badge > 0)
        {
            badge = Text(st.Badge > 999 ? "999+" : st.Badge.ToString(), UiBold, 10.5, Colors.Black);
            bw = badge.Width + 10 * _u + 6 * _u;
        }
        if (onBorder && _st.Border > 0)
            dc.DrawRectangle(B(WithA(bg, WireA)), null, R(x - 6 * _u, y - 2 * _u, title.Width + bw + 12 * _u, 4 * _u));
        dc.DrawText(title, new Point(x, y - title.Height / 2));
        x += title.Width;
        if (badge != null)
        {
            var bc = (st.Color & 0xFF000000) == 0 ? C(st.Color) : Mcol(Mc.Accent);
            double h = badge.Height + 2 * _u, bx = x + 6 * _u;
            dc.DrawRectangle(B(bc), null, R(bx, y - h / 2, badge.Width + 10 * _u, h), h / 2, h / 2);
            dc.DrawText(badge, new Point(bx + 5 * _u, y - badge.Height / 2));
            x = bx + badge.Width + 10 * _u;
        }
        return x;
    }

    /// <summary>A collapsed widget: a strip with its title, count and buttons.</summary>
    private void DrawStrip(DrawingContext dc, string name, Rect box)
    {
        var (bg, border, tc) = FrameColors(name);
        double r = Math.Min(_st.Radius * _u, box.Height / 2);
        var pen = _st.Border > 0 ? new Pen(B(border), _st.Border * _u) : null;
        dc.DrawRectangle(B(WithA(bg, WireA)), pen, box, r, r);
        DrawGTitle(dc, name, bg, tc, box.X + 12 * _u, box.Y + box.Height / 2, false);
        DrawWidgetButtons(dc, box, name, WidgetFlags(name), bg, border, tc, true);
    }

    /// <summary>Gadget g in box: its frame, title, buttons (with them: on a menu), a resize grip, and its scene. Its
    /// replies made while drawing (timers) say g=; its size is reported when it changes.</summary>
    private void DrawGadget(DrawingContext dc, SGadget g, Rect box, bool buttons)
    {
        var t = g.Tree!;
        Color bg = C(t.Colors[(int)Sc.Panel]), border = C(t.Colors[(int)Sc.Border]), tc = C(t.Colors[(int)Sc.Title]);
        double r = _st.Radius * _u;
        var pen = _st.Border > 0 || _gFocus == g ? new Pen(B(_gFocus == g ? Mcol(Mc.Accent) : border), Math.Max(_st.Border, 1) * _u) : null;
        dc.DrawRectangle(B(WithA(bg, WireA)), pen, box, r, r);
        if (g.Title.Length > 0) DrawGTitle(dc, g.Name, bg, tc, box.X + 14 * _u, box.Y, true);
        if (buttons)
        {
            DrawWidgetButtons(dc, box, g.Name, g.Flags, bg, border, tc);
            if ((g.Flags & GfResize) != 0 && _gPlace.TryGetValue(g.Name, out var pl) && pl.Dock == DockFloat)
            {
                var gp = new Pen(B(WithA(border, 200)), Math.Max(_u, 1));
                for (int i = 1; i <= 3; i++)
                    dc.DrawLine(gp, new Point(box.Right - 4 * _u - 4 * i * _u, box.Bottom - 4 * _u), new Point(box.Right - 4 * _u, box.Bottom - 4 * _u - 4 * i * _u));
            }
        }
        double pad = 10 * _u;
        var inner = R(box.X + pad, box.Y + pad + 2 * _u, Math.Max(box.Width - 2 * pad, 1), Math.Max(box.Height - 2 * pad - 2 * _u, 1));
        using var clip = dc.PushClip(inner);
        var (was, tag) = (_sScale, _gTag);
        _sScale = GadgetScale;
        _gTag = g.Name;
        try
        {
            DrawTree(dc, t, inner);
            GadgetSizeCheck(g, inner);
        }
        finally
        {
            (_sScale, _gTag) = (was, tag);
        }
    }

    /// <summary>GadgetSize;g=;w=;h= (the scene space in its units) when first drawn and when it changes (after 300 ms).</summary>
    private void GadgetSizeCheck(SGadget g, Rect inner)
    {
        if (!IsUp) return;
        var cur = (Math.Round(inner.Width / SU, 1), Math.Round(inner.Height / SU, 1));
        var now = DateTime.UtcNow;
        if (cur != g.SizeCur)
        {
            bool first = g.SizeCur.W < 0;
            g.SizeCur = cur;
            g.SizeAt = first ? now.AddSeconds(-1) : now;
            g.SizePending = true;
        }
        if (g.SizePending && (now - g.SizeAt).TotalMilliseconds >= 300)
        {
            g.SizePending = false;
            Send?.Invoke($"GadgetSize;g={g.Name};w={F1(cur.Item1)};h={F1(cur.Item2)}");
        }
    }

    // ================================================================
    // The tray: GF_TRAY gadgets on the status bar while no menu is up
    // ================================================================

    private TrayBar? _tray;

    /// <summary>The tray's pills, for MainWindow to put on TERMinator's status bar.</summary>
    public Control Tray => _tray ??= new TrayBar(this);

    /// <summary>The gadgets the tray shows now: tray ones while no menu is up, and those put in the tray always.</summary>
    private List<SGadget> TrayGadgets() => _gadgets.Values.Where(g => g.Ready && !_gClosed.Contains(g.Name) &&
        ((g.Flags & GfTray) != 0 && !IsUp || _gPlace.TryGetValue(g.Name, out var p) && p.Dock == DockTray)).ToList();

    private void TrayUpdate()
    {
        if (_tray == null) return;
        bool any = TrayGadgets().Count > 0;
        _tray.IsVisible = any;
        _tray.Ticking(any);
        _tray.InvalidateMeasure();
        _tray.InvalidateVisual();
        if (!any) TrayClose();
    }

    private void TrayClose() => _tray?.CloseView();

    /// <summary>Gadget g opened from its pill: a pop-up above the status bar showing it (quiet while no menu is up).</summary>
    private void TrayOpen(SGadget g, Rect pill) => _tray?.OpenView(g, pill);

    /// <summary>The pills: each tray gadget's title and count; and the pop-up a pill opens (its visual and logical child,
    /// so the Popup has a place in the tree).</summary>
    private sealed class TrayBar : Control, Avalonia.Rendering.ICustomHitTest
    {
        private readonly RainwireMenu _m;
        private readonly List<(Rect Box, SGadget G)> _pills = [];
        private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(500) };
        private readonly Popup _pop;
        private static readonly IBrush White = new SolidColorBrush(Colors.White), Black = new SolidColorBrush(Colors.Black);

        public TrayBar(RainwireMenu m)
        {
            _m = m;
            IsVisible = false;
            VerticalAlignment = VerticalAlignment.Center;
            HorizontalAlignment = HorizontalAlignment.Right;
            Margin = new Thickness(16, 0, 16, 0);
            Cursor = new Cursor(StandardCursorType.Hand);
            _pop = new Popup
            {
                PlacementTarget = this, Placement = PlacementMode.TopEdgeAlignedLeft, IsLightDismissEnabled = true,   // WPF Top: left edges aligned
            };
            LogicalChildren.Add(_pop);
            VisualChildren.Add(_pop);
            _tick.Tick += (_, _) => InvalidateVisual();   // counts and attention pulses
            PointerReleased += (_, e) =>
            {
                if (e.InitialPressMouseButton != MouseButton.Left) return;
                var p = e.GetPosition(this);
                foreach (var (box, g) in _pills)
                    if (box.Contains(p))
                    {
                        if (_m._gState.TryGetValue(g.Name, out var st)) _m._gState[g.Name] = st with { Attn = false };
                        _m.TrayOpen(g, box);
                        break;
                    }
                e.Handled = true;
            };
        }

        public void Ticking(bool on)
        {
            if (on) _tick.Start();
            else _tick.Stop();
        }

        public void OpenView(SGadget g, Rect pill)
        {
            if (_pop.IsOpen && _pop.Tag == g)
            {
                _pop.IsOpen = false;
                return;
            }
            _pop.IsOpen = false;
            _pop.Child = new TrayView(_m, g) { Width = 340, Height = 250 };
            _pop.Tag = g;
            _pop.HorizontalOffset = pill.X;
            _pop.IsOpen = true;
        }

        public void CloseView()
        {
            _pop.IsOpen = false;
            _pop.Child = null;
        }

        private static FormattedText Label(SGadget g) => new(g.Title.Length > 0 ? g.Title : g.Name, CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, UiBold, 12, White);

        protected override Size MeasureOverride(Size available)
        {
            _pop.Measure(available);
            double w = 0;
            foreach (var g in _m.TrayGadgets()) w += Label(g).Width + 30 + (_m._gState.TryGetValue(g.Name, out var s) && s.Badge > 0 ? 26 : 0);
            return new Size(w, 20);
        }

        protected override Size ArrangeOverride(Size final)
        {
            _pop.Arrange(new Rect(0, 0, 0, 0));
            return final;
        }

        public bool HitTest(Point point) => true;   // the pills' gaps take the click too

        public override void Render(DrawingContext dc)
        {
            _pills.Clear();
            double x = 0;
            foreach (var g in _m.TrayGadgets())
            {
                var st = _m._gState.TryGetValue(g.Name, out var s) ? s : default;
                var ft = Label(g);
                double w = ft.Width + 20 + (st.Badge > 0 ? 26 : 0);
                var box = new Rect(x, 0, w, 20);
                var accent = g.Tree != null ? C(g.Tree.Colors[(int)Sc.Accent]) : Color.FromRgb(0x55, 0xAA, 0xFF);
                byte a = st.Attn ? (byte)(120 + 100 * Math.Abs(Math.Sin(DateTime.UtcNow.Ticks / 4e6))) : (byte)70;
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(a, accent.R, accent.G, accent.B)), new Pen(new SolidColorBrush(accent), 1), box, 10, 10);
                dc.DrawText(ft, new Point(x + 10, (20 - ft.Height) / 2));
                if (st.Badge > 0)
                {
                    var bt = new FormattedText(st.Badge > 99 ? "99+" : st.Badge.ToString(), CultureInfo.InvariantCulture,
                                               FlowDirection.LeftToRight, UiBold, 10.5, Black);
                    var bb = new Rect(x + 14 + ft.Width, 3, Math.Max(bt.Width + 8, 14), 14);
                    dc.DrawRectangle(White, null, bb, 7, 7);
                    dc.DrawText(bt, new Point(bb.X + (bb.Width - bt.Width) / 2, bb.Y + (14 - bt.Height) / 2));
                }
                _pills.Add((box, g));
                x += w + 6;
            }
        }
    }

    /// <summary>A tray gadget opened from its pill: drawn as on a menu, but quiet (nothing it does reaches the board
    /// while no menu is up), redrawn twice a second for its clocks.</summary>
    private sealed class TrayView : Control
    {
        private readonly RainwireMenu _m;
        private readonly SGadget _g;
        private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(500) };

        public TrayView(RainwireMenu m, SGadget g)
        {
            _m = m;
            _g = g;
            _tick.Tick += (_, _) => InvalidateVisual();
            AttachedToVisualTree += (_, _) => _tick.Start();
            DetachedFromVisualTree += (_, _) => _tick.Stop();
        }

        public override void Render(DrawingContext dc)
        {
            double w = Bounds.Width, h = Bounds.Height;
            if (_g.Tree == null || w <= 0) return;
            var u = _m._u;
            if (u <= 0) _m._u = 1;
            try
            {
                var box = new Rect(6, 14, w - 12, h - 20);
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(235, 0x10, 0x12, 0x1A)), null, new Rect(0, 0, w, h), 10, 10);
                _m._trayQuiet = !_m.IsUp;
                _m.DrawGadget(dc, _g, box, false);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("RainwireMenu: tray gadget draw failed: " + ex); }
            finally
            {
                _m._trayQuiet = false;
                if (u <= 0) _m._u = u;
            }
        }
    }

    /// <summary>A tray gadget is being drawn while no menu is up: it says nothing to the board.</summary>
    private bool _trayQuiet;
}
