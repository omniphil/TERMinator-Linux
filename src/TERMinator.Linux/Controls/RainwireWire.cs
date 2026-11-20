using System.Diagnostics;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace TERMinator.Linux.Controls;

/// <summary>
/// The news panel beside a menu's header art (1.1.6, a port of TERMinator-Windows Controls/RainwireWire.cs; Rainwire docs/MENU_GFX.md, "The news panel"; the sysop,
/// 2026-10-22: "a right justified strip on the right side of the ansi header almost full header height ... then in
/// there we have the scrolling news stories"). The board sends Wire (MU_S_WIRE: its width as a share of the header,
/// its scroll speed, colors, title and the stories, newest first) after a Menu's items and before Show, then one
/// WirePic a story (MU_S_WIRE_PIC + path=: its thumbnail in the picture cache). The header art shrinks, keeping its
/// shape, to the left of the panel; the panel is as tall as the art. Stories scroll up slowly and loop; the pointer
/// over the panel holds them still, the wheel scrolls them by hand, a click says Wire;i= (the board opens its news
/// door at that story). A Menu without a Wire has no panel.
/// The art keeps its size; the panel is drawn over its right side by the overlay (above the header picture).
/// </summary>
public sealed partial class RainwireMenu
{
    // ---- Rainwire modules/menu/src/protocol.h ----
    private enum Wc { Bg, Border, Title, Text, Dim, Hover, Tech, Retro, Bbs, Today, Count }
    private const byte WiPicture = 0x01;

    private sealed class WireStory
    {
        public int Section;
        public bool HasPicture;
        public uint Published;
        public string Source = "", Title = "";
        public Bitmap? Picture;
        public double Y, H;     // laid out: from the top of the list, and its height
    }

    private sealed class Wire
    {
        public int Width, Speed;
        public uint[] Colors = new uint[(int)Wc.Count];
        public string Title = "";
        public List<WireStory> Stories = [];
    }

    private Wire? _wire;
    private Wire? _wireKeep;               // the last menu's panel: the same stories again keep their pictures and scroll
    private Rect _wireBox, _wireList;      // the panel and its scrolling list, this frame (Empty: none drawn)
    private double _wireOff;               // how far the list has scrolled (DIPs, looping over _wireLoop)
    private double _wireLoop;              // the list's height plus a gap: one full turn
    private double _wireLaidW = -1, _wireLaidU;
    private DateTime _wireTick = DateTime.UtcNow, _wireHeld;   // the last frame; hand-scrolled until
    private int _wireHover = -1;

    private void WireReset()
    {
        if (_wire != null) _wireKeep = _wire;
        _wire = null;
        _wx = null;   // and the weather widget (RainwireWidgets.cs): each comes with its menu
        _wxBox = default;
        _wxHot = false;
        _widgetBtnHot = null;
        GadgetsForMenu();   // and the scene gadgets: they come with each menu too, but kept ones stay (RainwireGadgets.cs)
        _wireBox = _wireList = default;
        _wireHover = -1;
        _wireLaidW = -1;
    }

    /// <summary>MU_S_WIRE: the panel for the menu whose head came just before.</summary>
    private void OnWire(byte[] p)
    {
        int o = 4, count = p[o], width = p[o + 1], speed = p[o + 2], colors = p[o + 3], titleLen = p[o + 4];
        o += 8;
        var w = new Wire { Width = Math.Clamp(width, 15, 50), Speed = Math.Clamp(speed, 0, 80) };
        for (int i = 0; i < colors; i++, o += 4)
            if (i < (int)Wc.Count) w.Colors[i] = U32(p, o) & 0xFFFFFF;
        w.Title = Str(p, ref o, titleLen);
        for (int i = 0; i < count && o + 8 <= p.Length; i++)
        {
            var s = new WireStory
            {
                Section = Math.Min((int)p[o], 3), HasPicture = (p[o + 1] & WiPicture) != 0, Published = U32(p, o + 4),
            };
            int sl = p[o + 2], tl = p[o + 3];
            o += 8;
            s.Source = Str(p, ref o, sl);
            s.Title = Str(p, ref o, tl);
            w.Stories.Add(s);
        }
        // the same stories again (the board sends the panel with every showing): keep their pictures and the scroll
        var old = _wire ?? _wireKeep;
        if (old != null)
            for (int i = 0; i < Math.Min(old.Stories.Count, w.Stories.Count); i++)
                if (old.Stories[i].Title == w.Stories[i].Title) w.Stories[i].Picture = old.Stories[i].Picture;
        bool same = old != null && old.Stories.Count > 0 && w.Stories.Count > 0 && old.Stories[0].Title == w.Stories[0].Title;
        _wire = w.Stories.Count > 0 ? w : null;
        if (!same) _wireOff = 0;
        _wireLaidW = -1;
        Redraw();
    }

    /// <summary>MU_S_WIRE_PIC: story index's thumbnail is the file at path.</summary>
    private void OnWirePic(byte[] p, string? path)
    {
        int i = U16(p, 4);
        if (_wire == null || i >= _wire.Stories.Count || path == null) return;
        try
        {
            string full = Path.GetFullPath(path);
            if (!File.Exists(full)) return;
            using var f = File.OpenRead(full);
            _wire.Stories[i].Picture = Bitmap.DecodeToWidth(f, 256);
            Redraw();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"RainwireMenu: news picture {path}: {ex.Message}");
        }
    }

    /// <summary>Places the panel over the right of header hr (the sysop, 2026-10-22: "have the main ansi stay the same
    /// size and just draw the new box over it"): the art keeps its size, the panel covers its right Width %, inset a
    /// little, drawn by the overlay above the header picture. Returns hr unchanged; no panel when it'd be too small.</summary>
    private Rect WireLayout(Rect hr)
    {
        _wireList = default;
        WidgetLayout(hr);   // the column: news on top, weather under it (RainwireWidgets.cs)
        if (IsEmpty(_wireBox)) return hr;
        // the list's place too, here and not in DrawWire: the surface lays out and the overlay draws, in either
        // order, and a click needs both (the list was cleared after the overlay set it, so clicks found no story)
        double pad = 12 * _u;
        _wireList = new Rect(_wireBox.X + pad, _wireBox.Y + pad + 2 * _u, Math.Max(_wireBox.Width - 2 * pad, 1),
                             Math.Max(_wireBox.Height - 2 * pad - 2 * _u, 1));
        return hr;
    }

    private static string WireSection(int s) => s switch { 1 => "RETRO", 2 => "BBS SCENE", 3 => "ON THIS DAY", _ => "TECH" };

    private static string WireAgo(uint t)
    {
        double d = Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (long)t);
        return d < 3600 ? $"{Math.Max(1, (int)(d / 60))}m ago" : d < 86400 ? $"{(int)(d / 3600)}h ago" : $"{(int)(d / 86400)}d ago";
    }

    /// <summary>The panel's background alpha: 80% of the box style's, so the header art shows through it (the sysop,
    /// 2026-10-22: "make the news box slightly see-through so the art shows").</summary>
    private byte WireA => (byte)(BoxA * 0.8);

    private Color Wcol(Wc c) => C(_wire!.Colors[(int)c]);

    private Typeface WireHeadFace => UiBold;
    private const double WireHeadSize = 13.5, WireTagSize = 10.5, WireRowGap = 10;

    /// <summary>The thumbnail's width in the list (0: the panel is too narrow for pictures).</summary>
    private double WireThumbW => _wireList.Width < 200 * _u ? 0 : Math.Min(96 * _u, _wireList.Width * 0.36);

    /// <summary>Each story's place and height at the list's width, when the width or scale changed.</summary>
    private void WireLay()
    {
        if (_wire == null || (_wireLaidW == _wireList.Width && _wireLaidU == _u)) return;
        _wireLaidW = _wireList.Width;
        _wireLaidU = _u;
        double tw = WireThumbW, textW = _wireList.Width - (tw > 0 ? tw + 10 * _u : 0), y = 0;
        foreach (var s in _wire.Stories)
        {
            var head = WireHeadline(s, textW, Colors.White);
            double h = Math.Max(tw * 9 / 16, Text("X", UiBold, WireTagSize, Colors.White).Height + 3 * _u + head.Height);
            s.Y = Math.Round(y);   // whole units apart, so the rows keep their distances as the list moves
            s.H = h;
            y += h + WireRowGap * _u;
        }
        _wireLoop = y + 18 * _u;
    }

    private FormattedText WireHeadline(WireStory s, double width, Color c)
    {
        var ft = Text(s.Title, WireHeadFace, WireHeadSize, c);
        ft.MaxTextWidth = Math.Max(width, 20);
        ft.MaxLineCount = 3;
        ft.Trimming = TextTrimming.WordEllipsis;
        return ft;
    }

    /// <summary>The panel: its box with the title on the top border, the stories scrolling inside, faded at the
    /// edges.</summary>
    private void DrawWire(DrawingContext dc)
    {
        if (_wire == null || IsEmpty(_wireBox)) return;
        var box = _wireBox;
        double r = _st.Radius * _u;
        var pen = _st.Border > 0 ? new Pen(B(Wcol(Wc.Border)), _st.Border * _u) : null;
        var bg = Wcol(Wc.Bg);
        dc.DrawRectangle(B(WithA(bg, WireA)), pen, box, r, r);
        // the title on the top border
        var title = Text(_wire.Title.ToUpperInvariant(), UiBold, 12, Wcol(Wc.Title));
        double tx = box.X + 14 * _u;
        if (_st.Border > 0)
            dc.DrawRectangle(B(WithA(bg, WireA)), null, new Rect(tx - 6 * _u, box.Y - 2 * _u, title.Width + 12 * _u, 4 * _u));
        dc.DrawText(title, new Point(tx, box.Y - title.Height / 2));
        DrawWidgetButtons(dc, box, "news", WidgetFlags("news"), bg, Wcol(Wc.Border), Wcol(Wc.Title));

        WireLay();
        // the scroll: Speed DIPs a second at 1x, held while the pointer is over the panel or just after the wheel
        var now = DateTime.UtcNow;
        double dt = Math.Min((now - _wireTick).TotalSeconds, 0.25);
        _wireTick = now;
        bool loops = _wireLoop - 18 * _u > _wireList.Height;
        if (!loops) _wireOff = 0;
        else if (_wireHover < 0 && !_wireBox.Contains(_mouse) && now > _wireHeld)
            _wireOff += _wire.Speed * _u * dt;
        if (loops) _wireOff = ((_wireOff % _wireLoop) + _wireLoop) % _wireLoop;

        using (dc.PushClip(_wireList))
        {
        double tw = WireThumbW, textW = _wireList.Width - (tw > 0 ? tw + 10 * _u : 0);
        for (int pass = 0; pass < (loops ? 2 : 1); pass++)
        {
            // on whole device pixels: the stories are laid out at fixed distances from here, so they all move
            // together, a pixel at a time (each one snapped on its own made the lines step separately)
            double sc = (VisualRoot?.RenderScaling ?? 1), baseY = Math.Round((_wireList.Y - _wireOff + pass * _wireLoop) * sc) / sc;
            for (int i = 0; i < _wire.Stories.Count; i++)
            {
                var s = _wire.Stories[i];
                double y = baseY + s.Y;
                if (y > _wireList.Bottom || y + s.H < _wireList.Y) continue;
                DrawWireStory(dc, i, s, _wireList.X, y, tw, textW);
            }
            if (loops)   // a thin rule where the list starts over
            {
                double ry = baseY + _wireLoop - 9 * _u;
                dc.DrawRectangle(B(WithA(Wcol(Wc.Border), 90)), null, new Rect(_wireList.X, ry, _wireList.Width, Math.Max(1, _u)));
            }
        }
        }
        // the stories fade into the panel at its top and bottom edges
        double fade = 16 * _u;
        var top = VFade(bg, WireA, 0);
        var bottom = VFade(bg, 0, WireA);
        dc.DrawRectangle(top, null, new Rect(_wireList.X, _wireList.Y, _wireList.Width, fade));
        dc.DrawRectangle(bottom, null, new Rect(_wireList.X, _wireList.Bottom - fade, _wireList.Width, fade));
    }

    private void DrawWireStory(DrawingContext dc, int i, WireStory s, double x, double y, double tw, double textW)
    {
        if (i == _wireHover)
        {
            var hv = Wcol(Wc.Hover);
            dc.DrawRectangle(B(Color.FromArgb(70, hv.R, hv.G, hv.B)), null,
                                    new Rect(x - 5 * _u, y - 4 * _u, _wireList.Width + 10 * _u, s.H + 8 * _u), 4 * _u, 4 * _u);
        }
        if (tw > 0)
        {
            var pic = new Rect(x, y, tw, tw * 9 / 16);
            using (dc.PushClip(new RoundedRect(pic, 3 * _u)))
            {
                if (s.Picture != null)
                    dc.DrawImage(s.Picture, Cover(pic, s.Picture.PixelSize.Width, s.Picture.PixelSize.Height));
                else   // none: the news door's mosaic (dim while a picture is on its way)
                    Mosaic(dc, pic, i, SectionColor(s.Section), s.HasPicture);
            }
            x += tw + 10 * _u;
        }
        var tag = Text(WireSection(s.Section), UiBold, WireTagSize, SectionColor(s.Section));
        dc.DrawText(tag, new Point(x, y));
        var meta = Text("  " + (s.Source.Length > 0 ? s.Source + " · " : "") + WireAgo(s.Published), Ui, WireTagSize, Wcol(Wc.Dim));
        meta.MaxTextWidth = Math.Max(textW - tag.Width, 10);
        meta.MaxLineCount = 1;
        meta.Trimming = TextTrimming.CharacterEllipsis;
        dc.DrawText(meta, new Point(x + tag.Width, y));
        var head = WireHeadline(s, textW, i == _wireHover ? Colors.White : Wcol(Wc.Text));
        dc.DrawText(head, new Point(x, y + tag.Height + 3 * _u));
    }

    /// The news door's placeholder for a story without a picture (newsmodule.c mosaic, the sysop 2026-10-22: "in the app
    /// there is a thumbnail of a bunch of yellow blocks ... we should use the same for the gadget"): a 12 x 9 grid of
    /// blocks in the section's color on a dark ground, denser towards the lower middle like a skyline, the same seeded
    /// sequence; dimmer while a picture is on its way.
    private void Mosaic(DrawingContext dc, Rect r, int seed, Color col, bool dim)
    {
        dc.DrawRectangle(B(Color.FromRgb(0x0C, 0x0E, 0x16)), null, r);
        var block = B(WithA(col, (byte)(dim ? 70 : 150)));
        uint q = unchecked((uint)seed * 2654435761u + 12345u);
        double cw = r.Width / 12, ch = r.Height / 9, gap = Math.Max(cw * 0.08, 0.5);
        for (int j = 0; j < 9; j++)
            for (int i = 0; i < 12; i++)
            {
                q = unchecked(q * 1103515245u + 12345u);
                double bias = (double)j / 9 + 0.35 * (1 - Math.Abs(i - 5.5) / 6);
                if ((q >> 16) % 100 / 100.0 < bias - 0.35)
                    dc.DrawRectangle(block, null, new Rect(r.X + i * cw + gap / 2, r.Y + j * ch + gap / 2, cw - gap, ch - gap));
            }
    }

    private Color SectionColor(int s) => Wcol(s switch { 1 => Wc.Retro, 2 => Wc.Bbs, 3 => Wc.Today, _ => Wc.Tech });

    /// <summary>A w x h picture filling box, cropped to its shape (the thumbnails are 16:9 already).</summary>
    private static Rect Cover(Rect box, double w, double h)
    {
        if (w <= 0 || h <= 0) return box;
        double s = Math.Max(box.Width / w, box.Height / h);
        return new Rect(box.X + (box.Width - w * s) / 2, box.Y + (box.Height - h * s) / 2, w * s, h * s);
    }

    /// <summary>The story under p, or -1.</summary>
    private int WireAt(Point p)
    {
        if (_wire == null || IsEmpty(_wireList) || !_wireList.Contains(p)) return -1;
        bool loops = _wireLoop - 18 * _u > _wireList.Height;
        double ly = p.Y - _wireList.Y + _wireOff;
        if (loops) ly %= _wireLoop;
        for (int i = 0; i < _wire.Stories.Count; i++)
        {
            var s = _wire.Stories[i];
            if (ly >= s.Y - 4 * _u && ly < s.Y + s.H + 4 * _u) return i;
        }
        return -1;
    }

    /// <summary>The pointer moved: true when it is over the panel (the menu's own hover leaves it alone).</summary>
    private bool WireMove(Point p)
    {
        if (WidgetMove(p)) return true;   // a widget's button, or the weather
        if (IsEmpty(_wireBox) || !_wireBox.Contains(p))
        {
            if (_wireHover >= 0)
            {
                _wireHover = -1;
                _surface.Cursor = Cursor.Default;
                Redraw();
            }
            return false;
        }
        int i = WireAt(p);
        if (i != _wireHover)
        {
            _wireHover = i;
            _surface.Cursor = i >= 0 ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
            Redraw();
        }
        return true;
    }

    /// <summary>A click: true when it was on the panel (a story opens in the board's news door).</summary>
    private bool WireClick(Point p)
    {
        if (WidgetClick(p)) return true;   // a widget's button, or the weather
        if (IsEmpty(_wireBox) || !_wireBox.Contains(p)) return false;
        int i = WireAt(p);
        if (i >= 0 && !_waiting && _complete)
        {
            _waiting = true;
            Reply($"Wire;i={i}");
            Redraw();
        }
        return true;
    }

    /// <summary>The wheel (delta in notches, a touchpad's in fractions): true when the pointer is over the panel (it
    /// scrolls the stories, then they move on their own again after a few seconds).</summary>
    private bool WireWheel(double delta)
    {
        if (!IsEmpty(_wxBox) && _wxBox.Contains(_mouse)) return true;   // the weather doesn't scroll
        if (GadgetWheel(delta)) return true;                           // a scene gadget's scrolls and lists
        if (IsEmpty(_wireBox) || !_wireBox.Contains(_mouse)) return false;
        _wireOff -= delta * 48 * _u;
        _wireHeld = DateTime.UtcNow.AddSeconds(4);
        _wireHover = WireAt(_mouse);
        Redraw();
        return true;
    }
}
