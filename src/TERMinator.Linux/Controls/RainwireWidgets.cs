using Avalonia;
using Avalonia.Input;
using Avalonia.Media;

namespace TERMinator.Linux.Controls;

/// <summary>
/// The widgets beside a menu's header art (1.1.6, a port of TERMinator-Windows Controls/RainwireWidgets.cs; Rainwire docs/MENU_GFX.md, "Widgets"; the sysop, 2026-10-22: "do the
/// same with terminator weather so we have a little widget below the news ticker but smaller ... on each of these
/// widgets lets add 2 top right buttons, one for close (closing one makes the other take up all the available space)
/// and another right to the left of that for expand, expand will make it show twice as wide"). A column over the
/// right of the header: the news panel (RainwireWire.cs) on top, the weather widget (MU_S_WEATHER) under it, smaller.
/// Each has a close and an expand button on its top border, right. Closing turns it off for good (WidgetOff;w=: the
/// board writes the door's setting, and the door's own switch turns it on again); expanding lasts for the call. A click on the weather says Widget;w=weather (the board opens
/// its weather door). The scene gadgets and the gadget engine are in RainwireGadgets.cs (version 6).
/// </summary>
public sealed partial class RainwireMenu
{
    // ---- Rainwire modules/menu/src/protocol.h: MU_WK_* ----
    private const int WkClear = 0, WkMostly = 1, WkPartly = 2, WkCloudy = 3, WkFog = 4, WkDrizzle = 5, WkRain = 6,
                      WkHeavy = 7, WkFreeze = 8, WkSnow = 9, WkHeavySnow = 10, WkThunder = 11;

    private sealed record WxDay(int Kind, int Pop, string Name, string Hi, string Lo);

    private sealed class Weather
    {
        public int Kind, Width;
        public bool Day;
        public uint[] Colors = new uint[(int)Wc.Count];
        public string Title = "", Place = "", Temp = "", Cond = "", Line1 = "", Line2 = "";
        public List<WxDay> Days = [];
    }

    private Weather? _wx;
    private Rect _wxBox;                              // the weather widget this frame (Empty: none)
    private bool _wxHot;                              // the pointer is over the weather

    /// <summary>MU_S_WEATHER: the weather widget for the menu whose head came just before.</summary>
    private void OnWeather(byte[] p)
    {
        int o = 4, days = p[o + 2], colors = p[o + 3];
        var w = new Weather { Kind = Math.Min((int)p[o], WkThunder), Day = p[o + 1] != 0, Width = Math.Clamp((int)p[o + 4], 15, 50) };
        o += 8;
        for (int i = 0; i < colors; i++, o += 4)
            if (i < (int)Wc.Count) w.Colors[i] = U32(p, o) & 0xFFFFFF;
        string S() => Str(p, ref o, p[o++]);
        w.Title = S();
        w.Place = S();
        w.Temp = S();
        w.Cond = S();
        w.Line1 = S();
        w.Line2 = S();
        for (int i = 0; i < days && o + 4 <= p.Length; i++)
        {
            int kind = Math.Min((int)p[o], WkThunder), pop = p[o + 1];
            o += 4;
            string name = S(), hi = S(), lo = S();
            w.Days.Add(new WxDay(kind, pop == 255 ? -1 : pop, name, hi, lo));
        }
        _wx = w;
        Redraw();
    }

    // ---- the client's own icons (scene icon blocks, BK_ICON; Rainwire internal/scene Icon*) ----

    private const int IcDot = 12, IcStar = 13, IcHeart = 14, IcCheck = 15, IcCross = 16, IcUp = 17, IcDown = 18,
                      IcClock = 19, IcMoon = 20;

    /// <summary>Icon icon in rect r, colored col where it has one color (the weather ones keep their own).</summary>
    private void DrawIcon(DrawingContext dc, int icon, Color col, Rect r)
    {
        double s = Math.Min(r.Width, r.Height);
        if (s < 4) return;
        var c = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
        if (icon <= WkThunder)
        {
            DrawWxIcon(dc, icon, true, c, s);
            return;
        }
        var fill = B(col);
        var pen = new Pen(fill, Math.Max(s * 0.11, 1)) { LineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        Geometry Poly(params Point[] pts)
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(pts[0], true);
                for (int i = 1; i < pts.Length; i++) ctx.LineTo(pts[i]);
                ctx.EndFigure(true);
            }
            return g;
        }
        Point P(double x, double y) => new(c.X + x * s, c.Y + y * s);
        switch (icon)
        {
            case IcDot: dc.DrawEllipse(fill, null, c, s * 0.22, s * 0.22); break;
            case IcStar:
            {
                var pts = new Point[10];
                for (int i = 0; i < 10; i++)
                {
                    double a = -Math.PI / 2 + i * Math.PI / 5, rr = i % 2 == 0 ? 0.48 : 0.2;
                    pts[i] = P(Math.Cos(a) * rr, Math.Sin(a) * rr + 0.04);
                }
                dc.DrawGeometry(fill, null, Poly(pts));
                break;
            }
            case IcHeart:
                dc.DrawEllipse(fill, null, P(-0.18, -0.12), s * 0.22, s * 0.22);
                dc.DrawEllipse(fill, null, P(0.18, -0.12), s * 0.22, s * 0.22);
                dc.DrawGeometry(fill, null, Poly(P(-0.39, -0.04), P(0.39, -0.04), P(0, 0.4)));
                break;
            case IcCheck:
                dc.DrawLine(pen, P(-0.32, 0.02), P(-0.1, 0.26));
                dc.DrawLine(pen, P(-0.1, 0.26), P(0.34, -0.26));
                break;
            case IcCross:
                dc.DrawLine(pen, P(-0.28, -0.28), P(0.28, 0.28));
                dc.DrawLine(pen, P(-0.28, 0.28), P(0.28, -0.28));
                break;
            case IcUp: dc.DrawGeometry(fill, null, Poly(P(0, -0.36), P(0.36, 0.26), P(-0.36, 0.26))); break;
            case IcDown: dc.DrawGeometry(fill, null, Poly(P(0, 0.36), P(0.36, -0.26), P(-0.36, -0.26))); break;
            case IcClock:
                dc.DrawEllipse(null, pen, c, s * 0.4, s * 0.4);
                dc.DrawLine(pen, c, P(0, -0.24));
                dc.DrawLine(pen, c, P(0.17, 0.06));
                break;
            case IcMoon: SunOrMoon(dc, false, c, s * 0.5, col); break;
        }
    }

    // ---- the weather widget ----

    private Color Xw(Wc c) => C(_wx!.Colors[(int)c]);

    /// <summary>The weather: a box like the news panel's, the title on the top border; inside, the icon with the
    /// temperature, the conditions and the place beside it, the feels-like and high/low line and the wind line under
    /// them as room allows, and the days ahead along the bottom (or, expanded, down the right half).</summary>
    private void DrawWeather(DrawingContext dc)
    {
        if (_wx == null || IsEmpty(_wxBox)) return;
        var w = _wx;
        var box = _wxBox;
        var bg = Xw(Wc.Bg);
        double r = _st.Radius * _u;
        var pen = _st.Border > 0 ? new Pen(B(Xw(Wc.Border)), _st.Border * _u) : null;
        dc.DrawRectangle(B(WithA(bg, WireA)), pen, box, r, r);
        if (_wxHot)
            dc.DrawRectangle(B(WithA(Xw(Wc.Hover), 40)), null, box, r, r);
        var title = Text(w.Title.ToUpperInvariant(), UiBold, 12, Xw(Wc.Title));
        double tx = box.X + 14 * _u;
        if (_st.Border > 0)
            dc.DrawRectangle(B(WithA(bg, WireA)), null, new Rect(tx - 6 * _u, box.Y - 2 * _u, title.Width + 12 * _u, 4 * _u));
        dc.DrawText(title, new Point(tx, box.Y - title.Height / 2));
        DrawWidgetButtons(dc, box, "weather", WidgetFlags("weather"), bg, Xw(Wc.Border), Xw(Wc.Title));

        double pad = 12 * _u;
        var inner = new Rect(box.X + pad, box.Y + pad + 2 * _u, Math.Max(box.Width - 2 * pad, 1), Math.Max(box.Height - 2 * pad - 2 * _u, 1));
        using var clip = dc.PushClip(inner);
        // expanded: the days down the right (it needed 360 units, more than an expanded gadget has at 80x25 in a large
        // window, so it fell back to the narrow layout and showed no days)
        bool wide = Wide("weather") && inner.Width > 160 * _u;
        var now = wide ? new Rect(inner.X, inner.Y, inner.Width * 0.40, inner.Height) : inner;   // expanded: room for 5 days
        // now, compact (the sysop, 2026-10-22: the place was cut off): the place on its own line; then the icon, the
        // temperature, and beside it the conditions over the feels-like and high/low line; then the wind line
        double ly = WxLine(dc, w.Place, Ui, 10.5, Xw(Wc.Dim), now.X, now.Y - 2 * _u, now.Width) + 2 * _u;
        var temp = Text(w.Temp, UiBold, 22, Xw(Wc.Text));
        double rowH = Math.Max(Math.Min(temp.Height, now.Bottom - ly), 0), icon = Math.Min(rowH, 32 * _u);
        if (icon >= 8 * _u) DrawWxIcon(dc, w.Kind, w.Day, new Point(now.X + icon / 2, ly + rowH / 2), icon);
        double x = now.X + (icon >= 8 * _u ? icon + 8 * _u : 0);
        dc.DrawText(temp, new Point(x, ly));
        x += temp.Width + 10 * _u;
        double cy = ly + Math.Max(rowH - 2 * Text("X", Ui, 10.5, Colors.White).Height - 2 * _u, 0) / 2;
        cy = WxLine(dc, w.Cond, UiBold, 12, Xw(Wc.Text), x, cy, Math.Max(now.Right - x, 10));
        WxLine(dc, w.Line1, Ui, 10.5, Xw(Wc.Dim), x, cy, Math.Max(now.Right - x, 10));
        ly += rowH + 2 * _u;
        double line = Text("X", Ui, 10.5, Colors.White).Height;
        if (ly + line <= now.Bottom + 1) ly = WxLine(dc, w.Line2, Ui, 10.5, Xw(Wc.Dim), now.X, ly, now.Width);
        // the days ahead: down the right when wide, else along the bottom when there's room
        Rect days = wide ? new Rect(now.Right + 12 * _u, inner.Y, inner.Right - now.Right - 12 * _u, inner.Height)
                         : new Rect(inner.X, ly + 6 * _u, inner.Width, inner.Bottom - ly - 6 * _u);
        if (w.Days.Count > 0 && days.Height >= 28 * _u && days.Width >= 60 * _u)   // the icons only when there's room
        {
            if (!wide) dc.DrawRectangle(B(WithA(Xw(Wc.Border), 80)), null, new Rect(days.X, days.Y - 4 * _u, days.Width, Math.Max(1, _u)));
            int n = Math.Min(w.Days.Count, Math.Max(1, (int)(days.Width / ((wide ? 32 : 46) * _u))));
            double cw = days.Width / n;
            for (int i = 0; i < n; i++)
            {
                var d = w.Days[i];
                double cx = days.X + cw * i + cw / 2;
                var name = Text(d.Name, UiBold, 10.5, Xw(Wc.Dim));
                dc.DrawText(name, new Point(cx - name.Width / 2, days.Y));
                // the high and low on one line when the column has room, else the high over the low (dimmed)
                // (the sysop, 2026-10-22: "the 5 day temperatures are a little bit cramped")
                var hl = Text(d.Hi + " " + d.Lo, Ui, 10.5, Xw(Wc.Text));
                bool stack = hl.Width > cw - 6 * _u;
                var hi = stack ? Text(d.Hi, UiBold, 10.5, Xw(Wc.Text)) : hl;
                var lo = stack ? Text(d.Lo, Ui, 10.5, Xw(Wc.Dim)) : null;
                if (stack)
                {
                    // the icon beside the high-over-low, as big as the column leaves ("we lost the small weather icons")
                    double sw = Math.Max(hi.Width, lo!.Width), sh = hi.Height + lo.Height - 1 * _u;
                    double ic = Math.Min(Math.Min(cw - sw - 7 * _u, sh), 20 * _u);
                    if (ic < 8 * _u) ic = 0;
                    double gx = cx - (ic + (ic > 0 ? 3 * _u : 0) + sw) / 2, gy = days.Y + name.Height + 1 * _u;
                    if (ic > 0) DrawWxIcon(dc, d.Kind, true, new Point(gx + ic / 2, gy + sh / 2), ic);
                    double sx = gx + ic + (ic > 0 ? 3 * _u : 0) + sw / 2;
                    dc.DrawText(hi, new Point(sx - hi.Width / 2, gy));
                    dc.DrawText(lo, new Point(sx - lo.Width / 2, gy + hi.Height - 1 * _u));
                    continue;
                }
                double ih = Math.Min(Math.Min(cw * 0.6, days.Height - name.Height - hi.Height - 4 * _u), 34 * _u);
                if (ih >= 10 * _u) DrawWxIcon(dc, d.Kind, true, new Point(cx, days.Y + name.Height + ih / 2 + 1 * _u), ih);
                dc.DrawText(hi, new Point(cx - hi.Width / 2, days.Y + name.Height + Math.Max(ih, 0) + 2 * _u));
            }
        }
    }

    /// <summary>One line of text cut to width with an ellipsis; returns the y under it.</summary>
    private double WxLine(DrawingContext dc, string s, Typeface face, double size, Color c, double x, double y, double width)
    {
        if (s.Length == 0) return y;
        var ft = Text(s, face, size, c);
        ft.MaxTextWidth = Math.Max(width, 10);
        ft.MaxLineCount = 1;
        ft.Trimming = TextTrimming.CharacterEllipsis;
        dc.DrawText(ft, new Point(x, y));
        return y + ft.Height;
    }

    // ---- the weather icons (MU_WK_*), drawn at size s around center c ----

    private void DrawWxIcon(DrawingContext dc, int kind, bool day, Point c, double s)
    {
        if (s < 6) return;
        // the weather widget's colors, or (a scene's icon with no weather widget) the same defaults Rainwire sends
        Color Ic(Wc k, uint def) => _wx != null ? Xw(k) : C(def);
        var sun = Ic(Wc.Tech, 0xF2D23C);
        var rain = Ic(Wc.Retro, 0x5CA8F0);
        var snow = Ic(Wc.Bbs, 0xE8F0FF);
        var bolt = Ic(Wc.Tech, 0xF2D23C);
        var cloud = day ? Color.FromRgb(0xDC, 0xE2, 0xEC) : Color.FromRgb(0xA4, 0xAE, 0xC0);
        var dark = Color.FromRgb(0x8C, 0x96, 0xA8);
        switch (kind)
        {
            case WkClear:
                SunOrMoon(dc, day, c, s * 0.5, sun);
                break;
            case WkMostly:
            case WkPartly:
                SunOrMoon(dc, day, new Point(c.X - s * 0.14, c.Y - s * 0.14), s * 0.38, sun);
                Cloud(dc, new Point(c.X + s * 0.1, c.Y + s * 0.14), s * (kind == WkMostly ? 0.5 : 0.66), cloud);
                break;
            case WkCloudy:
                Cloud(dc, new Point(c.X - s * 0.12, c.Y - s * 0.1), s * 0.55, dark);
                Cloud(dc, new Point(c.X + s * 0.06, c.Y + s * 0.08), s * 0.78, cloud);
                break;
            case WkFog:
            {
                var p = new Pen(B(cloud), s * 0.08) { LineCap = PenLineCap.Round };
                for (int i = 0; i < 3; i++)
                {
                    double y = c.Y - s * 0.22 + i * s * 0.22, inset = i == 1 ? 0.06 : 0;
                    dc.DrawLine(p, new Point(c.X - s * (0.4 - inset), y), new Point(c.X + s * (0.4 - inset), y));
                }
                break;
            }
            default:   // a cloud with something falling, or a storm
            {
                Cloud(dc, new Point(c.X, c.Y - s * 0.14), s * 0.8, kind == WkThunder || kind == WkHeavy ? dark : cloud);
                double y0 = c.Y + s * 0.14;
                if (kind == WkThunder)
                {
                    var g = new StreamGeometry();
                    using (var ctx = g.Open())
                    {
                        ctx.BeginFigure(new Point(c.X + s * 0.04, y0 - s * 0.04), true);
                        ctx.LineTo(new Point(c.X - s * 0.12, y0 + s * 0.2));
                        ctx.LineTo(new Point(c.X + s * 0.0, y0 + s * 0.2));
                        ctx.LineTo(new Point(c.X - s * 0.08, y0 + s * 0.4));
                        ctx.LineTo(new Point(c.X + s * 0.16, y0 + s * 0.12));
                        ctx.LineTo(new Point(c.X + s * 0.03, y0 + s * 0.12));
                        ctx.EndFigure(true);
                    }
                    dc.DrawGeometry(B(bolt), null, g);
                    break;
                }
                bool flakes = kind == WkSnow || kind == WkHeavySnow;
                int n = kind is WkDrizzle or WkSnow ? 3 : 4;
                var drop = new Pen(B(rain), s * 0.065) { LineCap = PenLineCap.Round };
                for (int i = 0; i < n; i++)
                {
                    double x = c.X - s * 0.27 + i * s * 0.54 / (n - 1);
                    if (flakes || (kind == WkFreeze && i % 2 == 1))
                        dc.DrawEllipse(B(snow), null, new Point(x, y0 + s * (i % 2 == 0 ? 0.16 : 0.28)), s * 0.055, s * 0.055);
                    else
                    {
                        double len = kind == WkDrizzle ? 0.12 : 0.22;
                        dc.DrawLine(drop, new Point(x + s * 0.04, y0 + s * 0.08), new Point(x - s * 0.04, y0 + s * (0.08 + len)));
                    }
                }
                break;
            }
        }
    }

    /// <summary>The sun (a disc with rays) or, at night, the moon (a crescent), radius r.</summary>
    private void SunOrMoon(DrawingContext dc, bool day, Point c, double r, Color sun)
    {
        if (!day)
        {
            var moon = new CombinedGeometry(GeometryCombineMode.Exclude,
                                            new EllipseGeometry(new Rect(c.X - r * 0.7, c.Y - r * 0.7, r * 1.4, r * 1.4)),
                                            new EllipseGeometry(new Rect(c.X + r * 0.38 - r * 0.6, c.Y - r * 0.28 - r * 0.6, r * 1.2, r * 1.2)));
            dc.DrawGeometry(B(Color.FromRgb(0xEE, 0xE8, 0xC8)), null, moon);
            return;
        }
        var ray = new Pen(B(sun), r * 0.14) { LineCap = PenLineCap.Round };
        for (int i = 0; i < 8; i++)
        {
            double a = i * Math.PI / 4, cs = Math.Cos(a), sn = Math.Sin(a);
            dc.DrawLine(ray, new Point(c.X + cs * r * 0.68, c.Y + sn * r * 0.68), new Point(c.X + cs * r * 0.95, c.Y + sn * r * 0.95));
        }
        dc.DrawEllipse(B(sun), null, c, r * 0.48, r * 0.48);
    }

    /// <summary>A cloud w wide centered on c: three puffs on a flat base.</summary>
    private static void Cloud(DrawingContext dc, Point c, double w, Color col)
    {
        var b = B(col);
        double h = w * 0.5;
        dc.DrawRectangle(b, null, new Rect(c.X - w / 2, c.Y, w, h * 0.5), h * 0.25, h * 0.25);
        dc.DrawEllipse(b, null, new Point(c.X - w * 0.22, c.Y + h * 0.05), w * 0.2, w * 0.2);
        dc.DrawEllipse(b, null, new Point(c.X + w * 0.05, c.Y - h * 0.12), w * 0.27, w * 0.27);
        dc.DrawEllipse(b, null, new Point(c.X + w * 0.28, c.Y + h * 0.08), w * 0.18, w * 0.18);
    }
}
