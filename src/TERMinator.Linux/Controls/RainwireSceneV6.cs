using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace TERMinator.Linux.Controls;

/// <summary>
/// Scenes version 6 (Rainwire docs/SCENES_V6.md), the last of 1.1.6: the scroll position reported (P_REPORTSCROLL →
/// Scrolled) and set (P_SCROLLPOS); scroll bars dragged and their tracks clicked; a click in a pane (P_PANE) giving it
/// the keys; multi-line fields and inputs wrapped by words as typed (their width or P_WRAPCOLS); the split block (a
/// divider the caller drags: Split;id=;at=); clicks that say where (x, y, w, h; col, row on cells; px, py on a picture;
/// cx, cy, tag on a canvas; i on a chart), the middle button, Move and Wheel events, the mouse's back and forward
/// buttons; the scene's size (SF_SIZE → Size); clocks and countdowns the client keeps (P_CLOCK, P_AT → Timer); charts;
/// sliders and number boxes; and the canvas, drawn by SceneDraw commands. The gadget engine is in RainwireGadgets.cs.
/// Linux port of the Windows client's Controls/RainwireSceneV6.cs (the master), 2026-11-02: the same packets, layout,
/// input and replies, drawn with Avalonia. Differences, all for Avalonia's sake:
///   - No global button or modifier state: a drag ends when a move says the left button is up (_sceneLeftHeld, set by
///     the surface's pointer events), and the wheel's Shift comes from the wheel event (_wheelShift, which a sideways
///     wheel or touchpad sets too: Avalonia reports its X delta, WPF's MouseWheel has none).
///   - The canvas isn't a frozen DrawingGroup: its commands are parsed once into drawing steps (rebuilt when they or the
///     call's pictures change) and replayed each frame under a pushed transform; tagged shapes keep a hit test (a
///     line's is the distance to it, as WPF's widened geometry is).
///   - Pointer capture is the press's pointer (_pointer, from the surface), not CaptureMouse.
/// </summary>
public sealed partial class RainwireMenu
{
    // ---- Rainwire internal/scene/v6.go ----
    private const byte BkSplit = 33, BkChart = 34, BkSlider = 35, BkCanvas = 36;
    private const byte PReportScroll = 158, PScrollPos = 159, PSplitAt = 160, PSplitMin = 161, PClock = 162, PAt = 163,
                       PChart = 164, PData = 165, PData2 = 166, PLabels = 167, PMinV = 168, PMaxV = 169, PStep = 170,
                       PSpin = 171, PViewW = 172, PViewH = 173, PUnit = 174, PAxes = 175;
    private const byte EvMiddle = 0x20, EvMove = 0x40, EvWheel = 0x80;
    private const byte SfSize = 0x08;
    private const byte ClockTime = 1, ClockTimeSec = 2, ClockDate = 3, ClockDateTime = 4, ClockCountdown = 5,
                       ClockElapsed = 6, ClockAgo = 7, ClockAt = 8;
    private const byte ChartLine = 0, ChartBar = 1, ChartArea = 2, ChartSpark = 3;
    private const byte AxesLabels = 1, AxesGrid = 2, AxesTip = 4;
    private const byte DrawReplace = 0, DrawAppend = 1;
    private const byte DTag = 1, DLine = 2, DRect = 3, DEllipse = 4, DPoly = 5, DText = 6, DImage = 7, DClear = 8;
    private const int CanvasMaxOps = 20000, CanvasMaxBytes = 1 << 20;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static string F1(double v) => (double.IsFinite(v) ? v : 0).ToString("0.0", Inv);

    private static readonly Cursor SizeWECursor = new(StandardCursorType.SizeWestEast),
                                   SizeNSCursor = new(StandardCursorType.SizeNorthSouth),
                                   ArrowCursor = new(StandardCursorType.Arrow);

    /// <summary>The pointer of the last press on the surface: a drag captures it (Windows' CaptureMouse).</summary>
    private IPointer? _pointer;

    /// <summary>Shift was held with the last wheel turn, or the turn was sideways (WheelIn's horizontal first).</summary>
    private bool _wheelShift;

    private void SurfaceCapture() => _pointer?.Capture(_surface);

    private void SurfaceRelease()
    {
        if (_pointer?.Captured == _surface) _pointer.Capture(null);
    }

    /// <summary>The v6 properties' types (KeyTypeV2 asks here for keys it doesn't know).</summary>
    private static byte KeyTypeV6(byte k) => k switch
    {
        PClock or PChart or PSpin or PAxes => TU8,
        PReportScroll => TU16,
        PAt => TU32,
        PScrollPos or PSplitAt or PSplitMin or PMinV or PMaxV or PStep or PViewW or PViewH => TF32,
        PUnit => TStr,
        PLabels => TStrs,
        PData or PData2 => TF32s,
        _ => 0,
    };

    // ================================================================
    // Scroll position: P_SCROLLPOS applied, P_REPORTSCROLL reported
    // ================================================================

    /// <summary>P_SCROLLPOS (in a record or a Set): the view put there once it's laid out, then the key forgotten so a
    /// later Set of other properties doesn't move it again.</summary>
    private void ScrollPosApply(SBlock b)
    {
        if (!b.Props.TryGetValue(PScrollPos, out var v) || v.Length != 4) return;
        double at = BitConverter.ToSingle(v, 0);
        if (!double.IsFinite(at)) at = 0;
        b.Props.Remove(PScrollPos);
        b.Scroll = at >= 1e6 ? b.ScrollMax : Math.Clamp(at * SU, 0, b.ScrollMax);
        b.AtEnd = b.Scroll >= b.ScrollMax - 1;
        b.AnchorRow = -1;
    }

    /// <summary>Every frame for each tree: the clocks' text, countdowns reaching zero, and scroll positions that came
    /// to rest (Scrolled).</summary>
    private void SceneTimers6(SceneTree s)
    {
        var now = DateTime.UtcNow;
        foreach (var b in s.Order)
        {
            if (b.Pass != s.Pass) continue;
            if (b.Kind == BkText && b.Has(PClock))
            {
                string t = ClockCompute(b);
                if (t != b.ClockText)
                {
                    b.ClockText = t;
                    _sceneGen++;   // measured again: "9:59" to "10:00" is wider
                }
                if (b.U(PClock) == ClockCountdown && b.Id != 0 && b.Has(PAt) && b.TimerFor != b.U32(PAt)
                    && DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= b.U32(PAt))
                {
                    b.TimerFor = b.U32(PAt);
                    Reply($"Timer;id={b.Id}");
                }
            }
            if (b.Id != 0 && b.Has(PReportScroll) && b.Kind is BkScroll or BkVList or BkViewer)
            {
                double su = SU;
                var cur = (Math.Round(b.Scroll / su, 1), Math.Round(b.ScrollMax / su, 1), Math.Round(b.View / su, 1));
                if (cur != b.RepCur)
                {
                    b.RepCur = cur;
                    b.RepAt = now;
                    b.RepPending = true;
                }
                if (b.RepPending && (now - b.RepAt).TotalMilliseconds >= Math.Max(b.U(PReportScroll), 50))
                {
                    b.RepPending = false;
                    bool end = b.ScrollMax - b.Scroll <= su;
                    Reply($"Scrolled;id={b.Id};pos={F1(cur.Item1)};max={F1(cur.Item2)};view={F1(cur.Item3)};end={(end ? 1 : 0)}");
                }
            }
        }
    }

    /// <summary>SF_SIZE: Size;w=;h= (units) when the scene is shown, and again once its space has been still 300 ms
    /// after changing.</summary>
    private void SceneSizeCheck(SceneTree s, Rect space)
    {
        if ((s.Flags & SfSize) == 0 || s != _scene) return;
        var cur = (Math.Round(space.Width / SU, 1), Math.Round(space.Height / SU, 1));
        var now = DateTime.UtcNow;
        if (cur != s.SizeCur)
        {
            bool first = s.SizeCur.W < 0;
            s.SizeCur = cur;
            s.SizeAt = first ? now.AddSeconds(-1) : now;
            s.SizePending = true;
        }
        if (s.SizePending && (now - s.SizeAt).TotalMilliseconds >= 300)
        {
            s.SizePending = false;
            Reply($"Size;w={F1(cur.Item1)};h={F1(cur.Item2)}");
        }
    }

    // ================================================================
    // Clocks (P_CLOCK, P_AT)
    // ================================================================

    /// <summary>The text a clock block shows (computed when it has none yet).</summary>
    private string ClockString(SBlock b)
    {
        if (b.ClockText.Length == 0) b.ClockText = ClockCompute(b);
        return b.ClockText;
    }

    private static string ClockCompute(SBlock b)
    {
        var now = DateTime.Now;
        long at = b.Has(PAt) ? b.U32(PAt) : 0, nowU = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        DateTime AtLocal() => DateTimeOffset.FromUnixTimeSeconds(at).LocalDateTime;
        switch (b.U(PClock))
        {
            case ClockTime: return now.ToString("t", CultureInfo.CurrentCulture);
            case ClockTimeSec: return now.ToString("T", CultureInfo.CurrentCulture);
            case ClockDate: return now.ToString("d", CultureInfo.CurrentCulture);
            case ClockDateTime: return now.ToString("g", CultureInfo.CurrentCulture);
            case ClockCountdown: return ClockSpan(Math.Max(at - nowU, 0));
            case ClockElapsed: return ClockSpan(Math.Max(nowU - at, 0));
            case ClockAgo: return Ago(nowU - at);
            case ClockAt: return at > 0 ? AtLocal().ToString("g", CultureInfo.CurrentCulture) : "";
            default: return "";
        }
    }

    /// <summary>4:05, 1:02:03, 2d 4:05:06.</summary>
    private static string ClockSpan(long secs)
    {
        long d = secs / 86400, h = secs / 3600 % 24, m = secs / 60 % 60, sec = secs % 60;
        if (d > 0) return $"{d}d {h}:{m:00}:{sec:00}";
        if (h > 0) return $"{h}:{m:00}:{sec:00}";
        return $"{m}:{sec:00}";
    }

    /// <summary>"just now", "5 min ago", "3 h ago", "yesterday", "4 days ago"; ahead: "in 5 min"...</summary>
    private static string Ago(long secs)
    {
        bool ahead = secs < 0;
        long a = Math.Abs(secs);
        if (a < 45) return "just now";
        string t;
        if (a < 90 * 60) t = $"{Math.Max(a / 60, 1)} min";
        else if (a < 24 * 3600) t = $"{a / 3600} h";
        else if (a < 48 * 3600) return ahead ? "tomorrow" : "yesterday";
        else t = $"{a / 86400} days";
        return ahead ? "in " + t : t + " ago";
    }

    // ================================================================
    // The split block
    // ================================================================

    private double MinPane(SBlock b) => (b.Has(PSplitMin) ? Math.Max(b.F(PSplitMin), 0) : 6) * SU;

    private double SplitShare(SBlock b) => Math.Clamp(b.SplitAt >= 0 ? b.SplitAt : b.Has(PSplitAt) ? b.F(PSplitAt) : 0.5, 0.05, 0.95);

    private Size NaturalV6(SBlock b, double inner, double fixH)
    {
        double u = SU;
        switch (b.Kind)
        {
            case BkSplit:
            {
                var kids = VisibleKids(b);
                double div = 0.6 * u;
                if (kids.Count == 0) return default;
                if (kids.Count == 1) return SMeasure(kids[0], inner);
                bool across = b.U(PAxis) == 0;
                if (across)
                {
                    double each = double.IsInfinity(inner) ? inner : Math.Max((inner - div) / 2, 0);
                    Size a = SMeasure(kids[0], each), c = SMeasure(kids[1], each);
                    return new Size(a.Width + div + c.Width, Math.Max(a.Height, c.Height));
                }
                Size x = SMeasure(kids[0], inner), y = SMeasure(kids[1], inner);
                return new Size(Math.Max(x.Width, y.Width), x.Height + div + y.Height);
            }
            case BkChart:
                return b.U(PChart) == ChartSpark ? new Size(8 * u, 1.5 * u) : new Size(16 * u, 6 * u);
            case BkSlider:
            {
                var (lw, vw) = SliderSides(b);
                return new Size(lw + 12 * u + vw, 1.6 * u);
            }
            case BkCanvas:
            {
                double vw = CanvasW(b), vh = CanvasH(b);
                double w = double.IsInfinity(inner) ? 16 * u : Math.Min(16 * u, inner);
                if (fixH > 0) return new Size(fixH * vw / vh, fixH);
                return new Size(w, w * vh / vw);
            }
        }
        return default;
    }

    /// <summary>The two panes either side of the divider, each at least P_SPLITMIN; one child fills the box.</summary>
    private void ArrangeSplit(SceneTree s, SBlock b, Rect inner, Rect clip)
    {
        var kids = VisibleKids(b);
        b.SplitBar = default;
        if (kids.Count == 0) return;
        if (kids.Count == 1)
        {
            SArrange(s, kids[0], inner, clip, b.Off);
            return;
        }
        bool across = b.U(PAxis) == 0;
        double div = 0.6 * SU, total = (across ? inner.Width : inner.Height) - div, min = MinPane(b);
        double a = total < 2 * min ? total / 2 : Math.Clamp(total * SplitShare(b), min, total - min);
        double dpi = SceneDpi;
        a = Math.Round(a * dpi) / dpi;
        if (across)
        {
            SArrange(s, kids[0], R(inner.X, inner.Y, a, inner.Height), clip, b.Off);
            b.SplitBar = R(inner.X + a, inner.Y, div, inner.Height);
            SArrange(s, kids[1], R(inner.X + a + div, inner.Y, Math.Max(total - a, 0), inner.Height), clip, b.Off);
        }
        else
        {
            SArrange(s, kids[0], R(inner.X, inner.Y, inner.Width, a), clip, b.Off);
            b.SplitBar = R(inner.X, inner.Y + a, inner.Width, div);
            SArrange(s, kids[1], R(inner.X, inner.Y + a + div, inner.Width, Math.Max(total - a, 0)), clip, b.Off);
        }
    }

    /// <summary>The panes, then the divider: a line, brighter under the pointer or while dragged, with a grip.</summary>
    private void DrawSplit(DrawingContext dc, SceneTree s, SBlock b, Rect clip)
    {
        DrawKids(dc, s, b, clip);
        if (IsEmpty(b.SplitBar)) return;
        var bar = b.SplitBar;
        bool across = b.U(PAxis) == 0;
        bool hot = _drag6 is { Kind: Drag6.Split } d && d.B == b || Inset(bar, -3 * _u).Contains(_mouse) && !b.Off;
        var c = hot ? Scol(Sc.Accent) : WithA(Scol(Sc.Border), 170);
        double t = Math.Max(hot ? 2 * _u : 1 * _u, 1);
        dc.DrawRectangle(B(c), null, across ? R(bar.X + (bar.Width - t) / 2, bar.Y, t, bar.Height) : R(bar.X, bar.Y + (bar.Height - t) / 2, bar.Width, t));
        // the grip: three dots in the middle
        double cx = bar.X + bar.Width / 2, cy = bar.Y + bar.Height / 2, g = 4 * _u, r = Math.Max(1.4 * _u, 1);
        for (int i = -1; i <= 1; i++)
            dc.DrawEllipse(B(c), null, across ? new Point(cx, cy + i * g) : new Point(cx + i * g, cy), r, r);
    }

    // ================================================================
    // Panes: a click gives one the keys
    // ================================================================

    private static SBlock? PaneAt(SceneTree s, Point p)
    {
        for (int i = s.Order.Count - 1; i >= 0; i--)
        {
            var b = s.Order[i];
            if (b.Kind is BkScroll or BkVList && b.Flag(PPane) && SFocusable(s, b) && b.Box.Contains(p) && b.Clip.Contains(p)) return b;
        }
        return null;
    }

    /// <summary>A focused pane's keys: the arrows and paging go to the board (a pane's keys are the board's, which
    /// answers with P_SCROLLBY); Left and Right move the focus as usual.</summary>
    private bool PaneKey(SceneTree s, SBlock f, Key key)
    {
        if (!f.Flag(PPane)) return false;
        int vk = key switch
        {
            Key.Up => VkUp, Key.Down => VkDown, Key.PageUp => VkPgUp, Key.PageDown => VkPgDn, Key.Home => VkHome,
            Key.End => VkEnd, _ => 0,
        };
        if (vk == 0) return false;
        SceneSendKey(s, vk, false, false, 0);
        return true;
    }

    private void DrawPaneFocus(DrawingContext dc, SBlock b)
    {
        var pen = new Pen(B(WithA(Scol(Sc.Focus), 150)), Math.Max(1.2 * _u, 1));
        dc.DrawRectangle(null, pen, Inset(b.Box, 0.6 * _u), 5 * _u, 5 * _u);
    }

    // ================================================================
    // Wrapping multi-line fields and inputs as typed
    // ================================================================

    /// <summary>v's drawn lines (start, end) wrapped by words at width, or P_WRAPCOLS columns of the font when that's
    /// narrower; the value keeps its own line breaks (a '\n' ends a line, and isn't in it). A word wider than a line
    /// breaks where it must. Kept on b while the text and width stay the same.</summary>
    private List<(int Start, int End)> WrapValue(SBlock b, string v, Typeface face, double size, double width)
    {
        double w = Math.Max(width, 10 * _u);
        int cols = b.U(PWrapCols);
        if (cols > 0) w = Math.Min(w, cols * TextW("0", face, size));
        if (b.VLines != null && b.WrapV == v && Math.Abs(b.WrapW - w) < 0.5 && b.WrapSize == size && b.WrapFace is { } wf && wf.Equals(face))
            return b.VLines;
        var lines = new List<(int, int)>();
        for (int ps = 0; ;)
        {
            int pe = v.IndexOf('\n', ps);
            if (pe < 0) pe = v.Length;
            WrapPara(v, ps, pe, face, size, w, lines);
            if (pe >= v.Length) break;
            ps = pe + 1;
        }
        (b.WrapV, b.WrapW, b.WrapSize, b.WrapFace) = (v, w, size, face);
        return lines;
    }

    private void WrapPara(string v, int ps, int pe, Typeface face, double size, double w, List<(int, int)> lines)
    {
        if (ps >= pe)
        {
            lines.Add((ps, pe));
            return;
        }
        int start = ps;
        while (start < pe)
        {
            if (TextW(v[start..pe], face, size) <= w)
            {
                lines.Add((start, pe));
                return;
            }
            int brk = -1;
            for (int i = start; i < pe;)
            {
                int j = i;
                while (j < pe && v[j] != ' ') j++;   // the word's end
                if (j > start && TextW(v[start..j], face, size) > w) break;
                int jj = j;
                while (jj < pe && v[jj] == ' ') jj++;   // its spaces stay on this line
                brk = jj;
                i = jj;
            }
            if (brk > start && brk < pe)
            {
                lines.Add((start, brk));
                start = brk;
                continue;
            }
            // a word wider than the line: as many characters as fit
            int k = start + 1;
            while (k < pe && TextW(v[start..(k + 1)], face, size) <= w) k++;
            if (k < pe && char.IsLowSurrogate(v[k])) k++;
            lines.Add((start, k));
            start = k;
        }
    }

    /// <summary>The drawn line the caret at cur is on: the last that starts at or before it.</summary>
    private static int VLineOf(List<(int Start, int End)> vl, int cur)
    {
        int at = 0;
        for (int i = 0; i < vl.Count; i++)
            if (vl[i].Start <= cur) at = i;
            else break;
        return at;
    }

    /// <summary>Where the caret may go at the end of drawn line i: before a soft break's last character (the space it
    /// broke at), so it doesn't show on the next line.</summary>
    private static int VLineEffEnd(SBlock b, int i)
    {
        var vl = b.VLines!;
        int e = vl[i].End;
        return i + 1 < vl.Count && vl[i + 1].Start == e && e > vl[i].Start ? e - 1 : e;
    }

    /// <summary>The drawn lines for b's value now, if they were drawn for it (null: they're stale, use '\n' lines).</summary>
    private static List<(int Start, int End)>? VLinesNow(SBlock b) =>
        b.VLines is { Count: > 0 } vl && b.WrapV == b.Value && !b.Flag(PSecret) ? vl : null;

    private int VLineStart(SBlock b, string v, int cur) =>
        VLinesNow(b) is { } vl ? vl[VLineOf(vl, cur)].Start : LineStart(v, cur);

    private int VLineEnd(SBlock b, string v, int cur) =>
        VLinesNow(b) is { } vl ? VLineEffEnd(b, VLineOf(vl, cur)) : LineEnd(v, cur);

    /// <summary>Up (-1) or Down (1) by drawn lines, keeping the caret's x.</summary>
    private bool VLineStep(SBlock b, string v, int d)
    {
        if (VLinesNow(b) is not { } vl) return false;
        int li = VLineOf(vl, b.Cur), t = li + d;
        if (t < 0 || t >= vl.Count) return true;
        double x = TextW(v[vl[li].Start..Math.Min(b.Cur, v.Length)], Ui, SSize);
        b.Cur = ColumnAt(v, vl[t].Start, VLineEffEnd(b, t), x);
        return true;
    }

    // ================================================================
    // Charts
    // ================================================================

    private (double Lo, double Hi) ChartRange(SBlock b, double[] a, double[] c)
    {
        double lo = double.MaxValue, hi = double.MinValue;
        foreach (var v in a.Concat(c)) { lo = Math.Min(lo, v); hi = Math.Max(hi, v); }
        if (lo == double.MaxValue) { lo = 0; hi = 1; }
        if (b.U(PChart) == ChartBar) { lo = Math.Min(lo, 0); hi = Math.Max(hi, 0); }
        if (b.Has(PMinV)) lo = b.F(PMinV);
        if (b.Has(PMaxV)) hi = b.F(PMaxV);
        if (hi - lo < 1e-9) { hi = lo + 1; }
        return (lo, hi);
    }

    private static string ChartNum(double v) =>
        Math.Abs(v) >= 1e6 ? (v / 1e6).ToString("0.#", Inv) + "M" : Math.Abs(v) >= 1e4 ? (v / 1e3).ToString("0.#", Inv) + "k"
        : Math.Abs(v - Math.Round(v)) < 1e-6 ? Math.Round(v).ToString(Inv) : v.ToString("0.##", Inv);

    /// <summary>A line, bar, area or sparkline chart of P_DATA (and P_DATA2) in the block, with value labels, grid lines,
    /// the labels under it, and a tip at the point nearest the pointer, as P_AXES says.</summary>
    private void DrawChart(DrawingContext dc, SBlock b)
    {
        var a = b.F32s(PData);
        var c = b.F32s(PData2);
        var r = b.Inner;
        int kind = b.U(PChart);
        int axes = b.Has(PAxes) ? b.U(PAxes) : kind == ChartSpark ? AxesTip : AxesLabels | AxesGrid | AxesTip;
        if (r.Width < 4 || r.Height < 4 || a.Length == 0) { b.Plot = default; return; }
        var (lo, hi) = ChartRange(b, a, c);
        string unit = b.S(PUnit);
        var labels = b.Strs(PLabels);
        double fs = SmallSize * 0.9, lw = 0, lh = 0, dpi = SceneDpi;
        if ((axes & AxesLabels) != 0 && kind != ChartSpark)
        {
            lw = Math.Max(TextW(ChartNum(hi) + unit, Ui, fs), TextW(ChartNum(lo) + unit, Ui, fs)) + 0.4 * SU;
            if (labels.Length > 0) lh = FontH(Ui, fs) + 0.15 * SU;
        }
        var plot = R(r.X + lw, r.Y + (kind == ChartSpark ? 0 : 0.3 * SU), r.Width - lw, r.Height - lh - (kind == ChartSpark ? 0 : 0.3 * SU));
        b.Plot = plot;
        if (plot.Width < 2 || plot.Height < 2) return;
        double Y(double v) => plot.Bottom - (Math.Clamp(v, lo, hi) - lo) / (hi - lo) * plot.Height;
        var dim = Scol(Sc.Dim);
        if ((axes & AxesGrid) != 0 && kind != ChartSpark)
            for (int i = 0; i <= 2; i++)
            {
                double y = plot.Y + plot.Height * i / 2;
                dc.DrawRectangle(B(WithA(Scol(Sc.Border), 70)), null, R(plot.X, Math.Round(y * dpi) / dpi, plot.Width, Math.Max(_u * 0.8, 1 / dpi)));
            }
        if (lw > 0)
        {
            var top = Text(ChartNum(hi) + unit, Ui, fs, dim);
            var bot = Text(ChartNum(lo) + unit, Ui, fs, dim);
            dc.DrawText(top, new Point(r.X + lw - 0.3 * SU - top.Width, plot.Y - top.Height / 2));
            dc.DrawText(bot, new Point(r.X + lw - 0.3 * SU - bot.Width, plot.Bottom - bot.Height / 2));
        }
        Color c1 = Ucol(b, PFg) ?? Scol(Sc.Accent), c2 = Ucol(b, PGrad) ?? Scol(Sc.Accent2);
        int n = Math.Max(a.Length, c.Length);
        double X(int i) => n <= 1 ? plot.X + plot.Width / 2 : plot.X + plot.Width * i / (n - 1);
        if (kind == ChartBar)
        {
            int series = c.Length > 0 ? 2 : 1;
            double slot = plot.Width / n, bw = Math.Max(slot * 0.7 / series, 1), y0 = Y(Math.Clamp(0, lo, hi));
            for (int i = 0; i < n; i++)
                for (int k = 0; k < series; k++)
                {
                    var d = k == 0 ? a : c;
                    if (i >= d.Length) continue;
                    double x = plot.X + slot * i + slot * 0.15 + bw * k, y = Y(d[i]);
                    dc.DrawRectangle(B(k == 0 ? c1 : c2), null, R(x, Math.Min(y, y0), bw, Math.Max(Math.Abs(y0 - y), 1)), 1.5 * _u, 1.5 * _u);
                }
        }
        else
        {
            foreach (var (d, col) in new[] { (a, c1), (c, c2) })
            {
                if (d.Length == 0) continue;
                var line = new StreamGeometry();
                using (var g = line.Open())
                {
                    g.BeginFigure(new Point(X(0), Y(d[0])), false);
                    for (int i = 1; i < d.Length; i++) g.LineTo(new Point(X(i), Y(d[i])));
                    g.EndFigure(false);
                }
                if (kind is ChartArea or ChartSpark)
                {
                    var area = new StreamGeometry();
                    using (var g = area.Open())
                    {
                        g.BeginFigure(new Point(X(0), plot.Bottom), true);
                        for (int i = 0; i < d.Length; i++) g.LineTo(new Point(X(i), Y(d[i])));
                        g.LineTo(new Point(X(d.Length - 1), plot.Bottom));
                        g.EndFigure(true);
                    }
                    dc.DrawGeometry(B(WithA(col, (byte)(kind == ChartSpark ? 40 : 80))), null, area);
                }
                var pen = new Pen(B(col), Math.Max((kind == ChartSpark ? 1.4 : 1.8) * _u, 1)) { LineJoin = PenLineJoin.Round };
                dc.DrawGeometry(null, pen, line);
            }
        }
        if (lh > 0)   // labels under it, as many as fit
        {
            double room = plot.Width / Math.Max(n, 1);
            int every = Math.Max(1, (int)Math.Ceiling(labels.Max(l => TextW(l, Ui, fs) + 0.4 * SU) / Math.Max(room, 1)));
            for (int i = 0; i < labels.Length && i < n; i += every)
            {
                var t = Text(labels[i], Ui, fs, dim);
                double cx = kind == ChartBar ? plot.X + room * (i + 0.5) : X(i);
                dc.DrawText(t, new Point(Math.Clamp(cx - t.Width / 2, r.X, Math.Max(r.Right - t.Width, r.X)), plot.Bottom + 0.15 * SU));
            }
        }
        // the tip at the point nearest the pointer
        if ((axes & AxesTip) != 0 && !b.Off && plot.Contains(_mouse) && b.Clip.Contains(_mouse) && ChartIndexAt(b, _mouse) is int hi2 and >= 0)
        {
            double cx = kind == ChartBar ? plot.X + plot.Width / n * (hi2 + 0.5) : X(hi2);
            dc.DrawRectangle(B(WithA(Scol(Sc.Text), 60)), null, R(cx, plot.Y, Math.Max(_u, 1), plot.Height));
            string tip = (hi2 < labels.Length ? labels[hi2] + ": " : "") + (hi2 < a.Length ? ChartNum(a[hi2]) + unit : "")
                         + (hi2 < c.Length ? " / " + ChartNum(c[hi2]) + unit : "");
            var ft = Text(tip, UiBold, SmallSize, Scol(Sc.Text));
            double bw = ft.Width + 0.8 * SU, bh = ft.Height + 0.3 * SU;
            double bx = Math.Clamp(cx + 0.4 * SU, r.X, Math.Max(r.Right - bw, r.X)), by = plot.Y;
            dc.DrawRectangle(B(WithA(Mix(Scol(Sc.Panel), Colors.Black, 0.3), 235)), null, R(bx, by, bw, bh), 4 * _u, 4 * _u);
            dc.DrawText(ft, new Point(bx + 0.4 * SU, by + 0.15 * SU));
        }
    }

    /// <summary>The chart's point nearest p across (-1: not over the plot).</summary>
    private int ChartIndexAt(SBlock b, Point p)
    {
        var plot = b.Plot;
        int n = Math.Max(b.F32s(PData).Length, b.F32s(PData2).Length);
        if (IsEmpty(plot) || n == 0 || p.X < plot.X - 4 * _u || p.X > plot.Right + 4 * _u) return -1;
        if (b.U(PChart) == ChartBar) return Math.Clamp((int)((p.X - plot.X) / (plot.Width / n)), 0, n - 1);
        return n <= 1 ? 0 : Math.Clamp((int)Math.Round((p.X - plot.X) / plot.Width * (n - 1)), 0, n - 1);
    }

    // ================================================================
    // Sliders and number boxes
    // ================================================================

    private static double SLo(SBlock b) => b.Has(PMinV) ? b.F(PMinV) : 0;
    private static double SHi(SBlock b) => Math.Max(b.Has(PMaxV) ? b.F(PMaxV) : 100, SLo(b));
    private static double SliderValue(SBlock b) => Math.Clamp(double.IsNaN(b.Num) ? b.F(PValue) : b.Num, SLo(b), SHi(b));

    private static string SliderText(SBlock b, double v)
    {
        double step = b.F(PStep);
        string f = step >= 1 || step <= 0 && SHi(b) - SLo(b) >= 20 ? "0" : step >= 0.1 || step <= 0 ? "0.0" : "0.00";
        return v.ToString(f, CultureInfo.CurrentCulture) + b.S(PUnit);
    }

    /// <summary>The room a slider's label takes at its left and its value at its right.</summary>
    private (double L, double V) SliderSides(SBlock b)
    {
        double u = SU;
        string label = b.S(PLabel);
        double lw = label.Length > 0 ? TextW(label, Ui, SSize) + 0.6 * u : 0;
        double vw = b.Flag(PSpin) ? 0 : Math.Max(TextW(SliderText(b, SHi(b)), UiBold, SSize), TextW(SliderText(b, SLo(b)), UiBold, SSize)) + 0.6 * u;
        return (lw, vw);
    }

    /// <summary>A slider's parts: its track (or a number box's value), and a number box's - and + buttons.</summary>
    private (Rect Track, Rect Minus, Rect Plus) SliderParts(SBlock b)
    {
        var r = b.Box;
        var (lw, vw) = SliderSides(b);
        var room = R(r.X + lw, r.Y, r.Width - lw - vw, r.Height);
        if (!b.Flag(PSpin)) return (R(room.X + 0.5 * SU, room.Y, room.Width - SU, room.Height), default, default);
        double s = Math.Min(room.Height, 1.6 * SU);
        var minus = R(room.X, room.Y + (room.Height - s) / 2, s, s);
        var plus = R(room.Right - s, minus.Y, s, s);
        return (R(minus.Right, minus.Y, plus.X - minus.Right, s), minus, plus);
    }

    /// <summary>A slider's value set to v (on its step, in its range); sent now when final, or while dragging when
    /// P_LIVE (at most 10 a second).</summary>
    private void SliderSet(SBlock b, double v, bool final)
    {
        double lo = SLo(b), hi = SHi(b), step = b.F(PStep);
        if (step > 0) v = lo + Math.Round((v - lo) / step) * step;
        v = Math.Clamp(v, lo, hi);
        b.Num = v;
        var now = DateTime.UtcNow;
        if (b.Id != 0 && !b.Off && v != b.NumSent && (final || b.Flag(PLive) && (now - b.NumAt).TotalMilliseconds >= 100))
        {
            b.NumSent = v;
            b.NumAt = now;
            Reply($"Change;id={b.Id};v={v.ToString("R", Inv)}");
        }
        Redraw();
    }

    private double SliderStep(SBlock b) => b.F(PStep) > 0 ? b.F(PStep) : (SHi(b) - SLo(b)) / 100;

    private bool SliderKey(SBlock b, Key key)
    {
        double v = SliderValue(b), st = SliderStep(b);
        switch (key)
        {
            case Key.Left: case Key.Down: SliderSet(b, v - st, true); return true;
            case Key.Right: case Key.Up: SliderSet(b, v + st, true); return true;
            case Key.PageDown: SliderSet(b, v - 10 * st, true); return true;
            case Key.PageUp: SliderSet(b, v + 10 * st, true); return true;
            case Key.Home: SliderSet(b, SLo(b), true); return true;
            case Key.End: SliderSet(b, SHi(b), true); return true;
        }
        return false;
    }

    private void DrawSlider(DrawingContext dc, SceneTree s, SBlock b)
    {
        double u = _u, su = SU;
        var r = b.Box;
        bool focus = Focused(s, b);
        double v = SliderValue(b), lo = SLo(b), hi = SHi(b), f = hi > lo ? (v - lo) / (hi - lo) : 0;
        string label = b.S(PLabel);
        if (label.Length > 0)
        {
            var lt = Fit(label, Ui, SSize, Ucol(b, PFg) ?? Scol(Sc.Text), Math.Max(r.Width * 0.5, 1));
            dc.DrawText(lt, new Point(r.X, MidY(lt, Ui, SSize, r.Y, r.Height)));
        }
        var (track, minus, plus) = SliderParts(b);
        var vt = Text(SliderText(b, v), UiBold, SSize, Scol(Sc.Text));
        if (b.Flag(PSpin))
        {
            double rad = Math.Max(_st.SelRadius, 4) * u;
            var pen = new Pen(B(focus ? Scol(Sc.Focus) : WithA(Scol(Sc.Border), 170)), (focus ? 1.5 : 1) * u);
            dc.DrawRectangle(B(Scol(Sc.FieldBg)), pen, R(minus.X, minus.Y, plus.Right - minus.X, minus.Height), rad, rad);
            foreach (var (box, sign) in new[] { (minus, -1), (plus, 1) })
            {
                bool hv = !b.Off && box.Contains(_mouse);
                if (hv) dc.DrawRectangle(B(WithA(Scol(Sc.SelBg), 90)), null, Inset(box, 2 * u), rad, rad);
                var gp = new Pen(B(Scol(Sc.Text)), 1.6 * u) { LineCap = PenLineCap.Round };
                double cx = box.X + box.Width / 2, cy = box.Y + box.Height / 2, a = box.Height * 0.2;
                dc.DrawLine(gp, new Point(cx - a, cy), new Point(cx + a, cy));
                if (sign > 0) dc.DrawLine(gp, new Point(cx, cy - a), new Point(cx, cy + a));
            }
            dc.DrawText(vt, new Point(track.X + (track.Width - vt.Width) / 2, MidY(vt, UiBold, SSize, track.Y, track.Height)));
            return;
        }
        double th = 0.3 * su, cy2 = r.Y + r.Height / 2;
        var line = R(track.X, cy2 - th / 2, track.Width, th);
        dc.DrawRectangle(B(WithA(Scol(Sc.Text), 45)), null, line, th / 2, th / 2);
        Color acc = Ucol(b, PFg) ?? Scol(Sc.Accent);
        dc.DrawRectangle(B(acc), null, R(line.X, line.Y, line.Width * f, th), th / 2, th / 2);
        double kx = track.X + track.Width * f, kr = 0.45 * su;
        bool hot = !b.Off && (_drag6 is { Kind: Drag6.Slider } d && d.B == b || Inset(R(kx - kr, cy2 - kr, 2 * kr, 2 * kr), -3 * u).Contains(_mouse));
        if (focus) dc.DrawEllipse(null, new Pen(B(WithA(Scol(Sc.Focus), 160)), 2 * u), new Point(kx, cy2), kr + 3 * u, kr + 3 * u);
        dc.DrawEllipse(B(hot ? Mix(acc, Colors.White, 0.25) : acc), new Pen(B(Mix(acc, Colors.Black, 0.3)), Math.Max(u, 1)), new Point(kx, cy2), kr, kr);
        dc.DrawText(vt, new Point(r.Right - vt.Width, MidY(vt, UiBold, SSize, r.Y, r.Height)));
    }

    // ================================================================
    // The canvas (SceneDraw)
    // ================================================================

    private static double CanvasW(SBlock b) => b.Has(PViewW) && b.F(PViewW) > 0 ? b.F(PViewW) : 100;
    private static double CanvasH(SBlock b) => b.Has(PViewH) && b.F(PViewH) > 0 ? b.F(PViewH) : 100;

    private void OnSceneDraw(byte[] p)
    {
        if (_scene != null && _isScene) SceneDrawInto(_scene, p);
    }

    /// <summary>SceneDraw into tree s (a screen's or a gadget's): u16 id, u8 mode, u8 reserved, u16 count, then
    /// commands (u8 op, u16 len, payload); unknown ops are kept and skipped when drawn. Past the caps, the drawing
    /// stops there.</summary>
    private void SceneDrawInto(SceneTree s, byte[] p)
    {
        if (p.Length < 10 || !s.ById.TryGetValue((ushort)U16(p, 4), out var b) || b.Kind != BkCanvas) return;
        if (p[6] == DrawReplace || b.Draw == null)
        {
            b.Draw = [];
            b.DrawBytes = 0;
        }
        int n = U16(p, 8), o = 10;
        for (int i = 0; i < n && o + 3 <= p.Length; i++)
        {
            int len = U16(p, o + 1);
            if (o + 3 + len > p.Length) break;
            if (b.Draw.Count < CanvasMaxOps && b.DrawBytes + len <= CanvasMaxBytes)
            {
                b.Draw.Add((p[o], p[(o + 3)..(o + 3 + len)]));
                b.DrawBytes += len + 3;
            }
            o += 3 + len;
        }
        b.DrawCache = null;
        Redraw();
    }

    private static Color? DCol(uint c) => (c & 0xFF000000) != 0 ? null : C(c);

    /// <summary>The distance from p to the segment a-b (a canvas line's hit test).</summary>
    private static double SegDist(Point p, Point a, Point b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, l2 = dx * dx + dy * dy;
        double t = l2 <= 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / l2, 0, 1);
        double x = a.X + t * dx - p.X, y = a.Y + t * dy - p.Y;
        return Math.Sqrt(x * x + y * y);
    }

    /// <summary>The canvas's commands as drawing steps in its own coordinates, and its tagged shapes' hit tests; built
    /// again when the commands or the call's pictures change. (Windows builds one frozen DrawingGroup; Avalonia has no
    /// such retained drawing to hand a DrawingContext, so the steps are replayed each frame.)</summary>
    private List<Action<DrawingContext>> CanvasBuild(SBlock b)
    {
        if (b.DrawCache != null && b.DrawAssets == _assets.Count) return b.DrawCache;
        var steps = new List<Action<DrawingContext>>();
        var tags = new List<(int, Func<Point, bool>)>();
        double vw = CanvasW(b), vh = CanvasH(b);
        int tag = 0;
        foreach (var (op, d) in b.Draw ?? [])
        {
            float F(int at) => at + 4 <= d.Length ? BitConverter.ToSingle(d, at) is var f && float.IsFinite(f) ? f : 0 : 0;
            uint U(int at) => at + 4 <= d.Length ? BitConverter.ToUInt32(d, at) : 0xFF000000;
            Pen? P(uint col, double w) => DCol(col) is Color c && w > 0
                ? new Pen(B(c), w) { LineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }
                : null;
            Func<Point, bool>? hit = null;
            switch (op)
            {
                case DTag: if (d.Length >= 2) tag = d[0] | d[1] << 8; break;
                case DLine:
                    if (d.Length >= 24 && P(U(16), F(20)) is Pen lp)
                    {
                        Point a = new(F(0), F(4)), e = new(F(8), F(12));
                        steps.Add(dc => dc.DrawLine(lp, a, e));
                        double half = Math.Max(lp.Thickness, vw / 100) / 2;
                        hit = p => SegDist(p, a, e) <= half;
                    }
                    break;
                case DRect:
                    if (d.Length >= 32)
                    {
                        var rect = new Rect(F(0), F(4), Math.Max(F(8), 0), Math.Max(F(12), 0));
                        double rr = Math.Max(F(28), 0);
                        IBrush? fill = DCol(U(16)) is Color fc ? B(fc) : null;
                        var pen = P(U(20), F(24));
                        steps.Add(dc => dc.DrawRectangle(fill, pen, rect, rr, rr));
                        var rg = new RectangleGeometry(rect, rr, rr);
                        hit = p => rg.FillContains(p);
                    }
                    break;
                case DEllipse:
                    if (d.Length >= 28)
                    {
                        Point ctr = new(F(0), F(4));
                        double rx = Math.Abs(F(8)), ry = Math.Abs(F(12));
                        IBrush? fill = DCol(U(16)) is Color fc ? B(fc) : null;
                        var pen = P(U(20), F(24));
                        steps.Add(dc => dc.DrawEllipse(fill, pen, ctr, rx, ry));
                        hit = p => rx > 0 && ry > 0 && Math.Pow((p.X - ctr.X) / rx, 2) + Math.Pow((p.Y - ctr.Y) / ry, 2) <= 1;
                    }
                    break;
                case DPoly:
                    if (d.Length >= 15)
                    {
                        int n = d[13] | d[14] << 8;
                        if (n < 2 || 15 + 8 * n > d.Length) break;
                        bool closed = d[12] != 0;
                        bool filled = closed && DCol(U(0)) != null;
                        var pg = new StreamGeometry();
                        using (var g = pg.Open())
                        {
                            g.BeginFigure(new Point(F(15), F(19)), filled);
                            for (int k = 1; k < n; k++) g.LineTo(new Point(F(15 + 8 * k), F(19 + 8 * k)));
                            g.EndFigure(closed);
                        }
                        IBrush? fill = closed && DCol(U(0)) is Color fc ? B(fc) : null;
                        var pen = P(U(4), F(8));
                        steps.Add(dc => dc.DrawGeometry(fill, pen, pg));
                        hit = p => pg.FillContains(p);
                    }
                    break;
                case DText:
                    if (d.Length >= 20)
                    {
                        int len = d[18] | d[19] << 8;
                        if (20 + len > d.Length || F(8) <= 0 || DCol(U(12)) is not Color tc) break;
                        var ft = new FormattedText(Encoding.UTF8.GetString(d, 20, len), Inv, FlowDirection.LeftToRight,
                                                   d[17] != 0 ? UiBold : Ui, F(8), B(tc));
                        double x = F(0) - (d[16] == 1 ? ft.Width / 2 : d[16] == 2 ? ft.Width : 0);
                        var at = new Point(x, F(4));
                        steps.Add(dc => dc.DrawText(ft, at));
                        var box = new Rect(x, F(4), ft.Width, ft.Height);
                        hit = p => box.Contains(p);
                    }
                    break;
                case DImage:
                    if (d.Length >= 19 && _assets.TryGetValue(d[0] | d[1] << 8, out var img))
                    {
                        var rect = new Rect(F(2), F(6), Math.Max(F(10), 0), Math.Max(F(14), 0));
                        var mode = d[18] != 0 ? BitmapInterpolationMode.HighQuality : BitmapInterpolationMode.None;
                        steps.Add(dc =>
                        {
                            using (dc.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = mode }))
                                dc.DrawImage(img, rect);
                        });
                        hit = p => rect.Contains(p);
                    }
                    break;
                case DClear:
                    if (DCol(U(0)) is Color cc)
                    {
                        var all = new Rect(0, 0, vw, vh);
                        var fill = B(cc);
                        steps.Add(dc => dc.DrawRectangle(fill, null, all));
                    }
                    break;
            }
            if (hit != null && tag != 0) tags.Add((tag, hit));
        }
        b.DrawCache = steps;
        b.DrawTags = tags;
        b.DrawAssets = _assets.Count;
        return steps;
    }

    /// <summary>The canvas's coordinates into its box by P_FIT: (offset, scale x, scale y).</summary>
    private (double X, double Y, double Sx, double Sy) CanvasMap(SBlock b)
    {
        var r = b.Inner;
        double vw = CanvasW(b), vh = CanvasH(b), sx = r.Width / vw, sy = r.Height / vh;
        switch (b.U(PFit))
        {
            case FitStretch: break;
            case FitCover: sx = sy = Math.Max(sx, sy); break;
            case FitNone: sx = sy = _u; break;
            default: sx = sy = Math.Min(sx, sy); break;
        }
        return (r.X + (r.Width - vw * sx) / 2, r.Y + (r.Height - vh * sy) / 2, sx, sy);
    }

    private void DrawCanvas(DrawingContext dc, SBlock b)
    {
        var r = b.Inner;
        if (r.Width < 1 || r.Height < 1 || b.Draw == null) return;
        var steps = CanvasBuild(b);
        var (x, y, sx, sy) = CanvasMap(b);
        b.PicRect = R(x, y, CanvasW(b) * sx, CanvasH(b) * sy);
        using (dc.PushClip(r))
        using (dc.PushTransform(Matrix.CreateScale(sx, sy) * Matrix.CreateTranslation(x, y)))
        {
            DrawingContext.PushedState? aliased = b.Has(PSmooth) && !b.Flag(PSmooth)
                ? dc.PushRenderOptions(new RenderOptions { EdgeMode = EdgeMode.Aliased }) : null;
            try
            {
                foreach (var step in steps) step(dc);
            }
            finally
            {
                aliased?.Dispose();
            }
        }
    }

    /// <summary>A point in the canvas's coordinates.</summary>
    private Point CanvasPoint(SBlock b, Point p)
    {
        var (x, y, sx, sy) = CanvasMap(b);
        return new Point((p.X - x) / Math.Max(sx, 1e-9), (p.Y - y) / Math.Max(sy, 1e-9));
    }

    /// <summary>The topmost tagged shape at p (0: none).</summary>
    private int CanvasTagAt(SBlock b, Point p)
    {
        if (b.Kind != BkCanvas || b.Draw == null) return 0;
        CanvasBuild(b);
        var cp = CanvasPoint(b, p);
        if (b.DrawTags == null) return 0;
        for (int i = b.DrawTags.Count - 1; i >= 0; i--)
            if (b.DrawTags[i].Hit(cp)) return b.DrawTags[i].Tag;
        return 0;
    }

    // ================================================================
    // Where a click, move or wheel landed
    // ================================================================

    /// <summary>";x=;y=;w=;h=" in units from b's top-left, then what's under p on cells (col, row), a picture viewer
    /// (px, py), a canvas (cx, cy, tag) or a chart (i).</summary>
    private string ClickWhere(SBlock b, Point p)
    {
        double su = SU;
        var sb = new StringBuilder($";x={F1((p.X - b.Box.X) / su)};y={F1((p.Y - b.Box.Y) / su)};w={F1(b.Box.Width / su)};h={F1(b.Box.Height / su)}");
        var pr = b.PicRect;
        if (b.Kind == BkCells && !IsEmpty(pr) && pr.Contains(p))
        {
            var (cols, rows) = CellDims(b);
            sb.Append($";col={Math.Min((int)((p.X - pr.X) / pr.Width * cols), cols - 1)};row={Math.Min((int)((p.Y - pr.Y) / pr.Height * rows), rows - 1)}");
        }
        else if (b.Kind is BkViewer or BkImage && !IsEmpty(pr) && pr.Contains(p) && (b.Image ?? Asset(b)) is Bitmap img)
        {
            int pw = img.PixelSize.Width, ph = img.PixelSize.Height;
            sb.Append($";px={Math.Min((int)((p.X - pr.X) / pr.Width * pw), pw - 1)};py={Math.Min((int)((p.Y - pr.Y) / pr.Height * ph), ph - 1)}");
        }
        else if (b.Kind == BkCanvas)
        {
            var cp = CanvasPoint(b, p);
            sb.Append($";cx={F1(cp.X)};cy={F1(cp.Y)}");
            if (CanvasTagAt(b, p) is int tag and > 0) sb.Append($";tag={tag}");
        }
        else if (b.Kind == BkChart && ChartIndexAt(b, p) is int i and >= 0)
        {
            sb.Append($";i={i}");
        }
        return sb.ToString();
    }

    // ================================================================
    // Move and wheel events, the middle button, back and forward
    // ================================================================

    private (SceneTree S, SBlock B)? _moveOn;
    private string _moveKey = "";
    private DateTime _moveAt;

    /// <summary>P_EVENTS move: Move as the pointer goes over a block (at most 10 a second; on cells and canvases only
    /// when the cell or tag changes), and Move;out=1 when it leaves.</summary>
    private void MoveEvents(SceneTree s, Point p)
    {
        SBlock? hit = null;
        for (int i = s.Order.Count - 1; i >= 0 && hit == null; i--)
        {
            var b = s.Order[i];
            if (b.Pass == s.Pass && b.Id != 0 && !b.Off && (b.U(PEvents) & EvMove) != 0 && b.Box.Contains(p) && b.Clip.Contains(p)) hit = b;
        }
        if (_moveOn is { } on && (on.S != s || on.B != hit))
        {
            if (on.S == s) Reply($"Move;id={on.B.Id};out=1");
            _moveOn = null;
            _moveKey = "";
        }
        if (hit == null) return;
        _moveOn = (s, hit);
        string where = ClickWhere(hit, p);
        string key = hit.Kind == BkCells ? Part(where, "col") + "," + Part(where, "row")
                   : hit.Kind == BkCanvas ? Part(where, "tag") + "," + Part(where, "cx") + "," + Part(where, "cy")
                   : where;
        if (hit.Kind == BkCanvas && Part(where, "tag").Length > 0) key = Part(where, "tag");
        var now = DateTime.UtcNow;
        if (key == _moveKey || (now - _moveAt).TotalMilliseconds < 100) return;
        _moveKey = key;
        _moveAt = now;
        Reply($"Move;id={hit.Id}" + where);
    }

    private static string Part(string fields, string k)
    {
        int at = fields.IndexOf(";" + k + "=", StringComparison.Ordinal);
        if (at < 0) return "";
        int st = at + k.Length + 2, end = fields.IndexOf(';', st);
        return end < 0 ? fields[st..] : fields[st..end];
    }

    /// <summary>The wheel in tree s (notches, up or left positive; a touchpad's are fractions): a block that takes it
    /// (P_EVENTS wheel), a slider, the scroll, list or viewer under the pointer (Shift or a sideways wheel: a horizontal
    /// one first), the focused pane, else the whole scene. A touchpad's small deltas scroll by as much as they say.</summary>
    private void WheelIn(SceneTree s, double notches)
    {
        var p = _mouse;
        bool shift = _wheelShift;
        for (int i = s.Order.Count - 1; i >= 0; i--)
        {
            var b = s.Order[i];
            if (b.Pass != s.Pass || b.Off || !b.Box.Contains(p) || !b.Clip.Contains(p)) continue;
            if ((b.U(PEvents) & EvWheel) != 0 && b.Id != 0)
            {
                Reply($"Wheel;id={b.Id};d={(-notches).ToString("0.##", Inv)}" + ClickWhere(b, p));
                return;
            }
            if (b.Kind == BkSlider)
            {
                SliderSet(b, SliderValue(b) + Math.Sign(notches) * SliderStep(b), true);
                return;
            }
        }
        double step = 4.5 * SU * -notches;
        SBlock? target = null, across = null;
        for (int i = s.Order.Count - 1; i >= 0 && target == null; i--)
        {
            var b = s.Order[i];
            if (b.Kind is not (BkScroll or BkVList or BkViewer or BkList) || b.Pass != s.Pass || b.ScrollMax <= 0
                || !b.Box.Contains(p) || !b.Clip.Contains(p)) continue;
            bool horiz = b.Kind == BkScroll && b.U(PAxis) == 1;
            if (horiz == shift) target = b;
            else across ??= b;
        }
        target ??= across ?? (SceneFocused(s) is { Kind: BkScroll or BkVList } f && f.ScrollMax > 0 ? f : null);
        if (target != null)
        {
            ScrollBlock(target, target.Scroll + step);
            target.AutoHold = DateTime.UtcNow.AddSeconds(4);   // a crawling one holds a while
            target.AutoEnded = false;
        }
        else if (s.ScrollMax > 0)
        {
            s.Scroll = Math.Clamp(s.Scroll + step, 0, s.ScrollMax);
            _sceneArr++;
        }
    }

    /// <summary>b scrolled to v by the caller (wheel, bar), each kind its own way.</summary>
    private void ScrollBlock(SBlock b, double v)
    {
        if (b.Kind is BkList or BkViewer)
        {
            b.Scroll = Math.Clamp(v, 0, b.ScrollMax);
            b.ShowSel = false;
        }
        else SetScroll(b, v);
        Redraw();
    }

    /// <summary>The middle button at p: Click;btn=m for a block that asks for it (P_EVENTS middle).</summary>
    private void SceneMiddle(SceneTree s, Point p)
    {
        for (int i = s.Order.Count - 1; i >= 0; i--)
        {
            var b = s.Order[i];
            if (b.Pass != s.Pass || b.Off || b.Id == 0 || !b.Box.Contains(p) || !b.Clip.Contains(p) || HoverHidden(b)) continue;
            if ((b.U(PEvents) & EvMiddle) != 0)
            {
                Reply($"Click;id={b.Id};n=1;btn=m" + ClickWhere(b, p));
                return;
            }
        }
    }

    /// <summary>The mouse's middle button (the surface).</summary>
    private void OnMiddle(Point p)
    {
        if (!IsUp || _dialog != null) return;
        _active = true;
        if (_isScene && _scene is SceneTree s && _complete) SceneMiddle(s, p);
        else if (GadgetAt(p) is SGadget g && g.Tree != null) InGadget(g, () => SceneMiddle(g.Tree, p));
    }

    /// <summary>The mouse's back button: Esc (a pop-up first, the scene's Esc button, Back; a menu's Esc). Forward: a
    /// key only screens that know it use (Key;nav=fwd).</summary>
    private void OnNav(bool forward)
    {
        if (!IsUp) return;
        _active = true;
        if (!forward)
        {
            HandleKey(Key.Escape);
            return;
        }
        if (_isScene && _scene is SceneTree s && _complete && _dialog == null) SceneSendKeyX(s, "nav=fwd");
    }

    // ================================================================
    // Dragging: scroll bars, dividers, sliders
    // ================================================================

    /// <summary>The left button down (the surface): a screen's bars, dividers and sliders, or the gadgets' (their
    /// titles, grips, and what's in them). True when a drag started (no click follows).</summary>
    private bool Down6(Point p)
    {
        if (!IsUp || _dialog != null) return false;
        if (_isScene) return _scene is SceneTree s && _complete && SceneDown6(s, p, null);
        return GadgetDown(p);
    }

    /// <summary>The left button up: true when it ended a drag.</summary>
    private bool Up6(Point p) => SceneUp6() | GadgetDragEnd(p);

    private enum Drag6 { Bar, Split, Slider }

    /// <summary>A scroll bar, divider or slider being dragged: in which tree, which block (a bar's null: the scene's
    /// own), where in the thumb it was taken, and the gadget it's in (null: the screen).</summary>
    private (Drag6 Kind, SceneTree S, SBlock? B, double Grab, SGadget? G)? _drag6;
    private (SceneTree S, SBlock? B)? _barDrag => _drag6 is { Kind: Drag6.Bar } d ? (d.S, d.B) : null;
    private SBlock? _splitClicked;
    private DateTime _splitClickedAt;

    /// <summary>The left button went down at p in tree s: a scroll bar (its thumb dragged, its track paged), a divider
    /// or a slider; true when it was one (no click follows).</summary>
    private bool SceneDown6(SceneTree s, Point p, SGadget? g)
    {
        for (int i = s.Bars.Count - 1; i >= 0; i--)
        {
            var bar = s.Bars[i];
            if (!bar.Track.Contains(p)) continue;
            if (bar.Thumb.Contains(p))
            {
                _drag6 = (Drag6.Bar, s, bar.B, bar.Horiz ? p.X - bar.Thumb.X : p.Y - bar.Thumb.Y, g);
                SurfaceCapture();
            }
            else
            {
                double view = bar.B is SBlock vb && vb.View > 0 ? vb.View : s.Space.Height, cur = bar.B?.Scroll ?? s.Scroll;
                bool fwd = bar.Horiz ? p.X > bar.Thumb.Right : p.Y > bar.Thumb.Bottom;
                BarSet(s, bar.B, cur + (fwd ? 1 : -1) * view * 0.9);
            }
            Redraw();
            return true;
        }
        for (int i = s.Order.Count - 1; i >= 0; i--)
        {
            var b = s.Order[i];
            if (b.Pass != s.Pass || b.Off || !b.Clip.Contains(p)) continue;
            if (b.Kind == BkSplit && !IsEmpty(b.SplitBar) && Inset(b.SplitBar, -3 * _u).Contains(p))
            {
                var now = DateTime.UtcNow;
                if (_splitClicked == b && (now - _splitClickedAt).TotalMilliseconds < 400)   // a double-click: the board's place
                {
                    b.SplitAt = -1;
                    _sceneArr++;
                    if (b.Id != 0) Reply($"Split;id={b.Id};at={SplitShare(b).ToString("0.000", Inv)}");
                    _splitClicked = null;
                    Redraw();
                    return true;
                }
                _splitClicked = b;
                _splitClickedAt = now;
                _drag6 = (Drag6.Split, s, b, 0, g);
                SurfaceCapture();
                return true;
            }
            if (b.Kind == BkSlider && b.Box.Contains(p))
            {
                if (b.Id != 0) s.Focus = b.Id;
                var (track, minus, plus) = SliderParts(b);
                if (b.Flag(PSpin))
                {
                    if (!IsEmpty(minus) && minus.Contains(p)) SliderSet(b, SliderValue(b) - SliderStep(b), true);
                    else if (!IsEmpty(plus) && plus.Contains(p)) SliderSet(b, SliderValue(b) + SliderStep(b), true);
                    return true;
                }
                _drag6 = (Drag6.Slider, s, b, 0, g);
                SurfaceCapture();
                SliderFromX(b, track, p.X, false);
                return true;
            }
        }
        return false;
    }

    private void SliderFromX(SBlock b, Rect track, double x, bool final)
    {
        double f = track.Width > 0 ? Math.Clamp((x - track.X) / track.Width, 0, 1) : 0;
        SliderSet(b, SLo(b) + f * (SHi(b) - SLo(b)), final);
    }

    /// <summary>b's scroll (null: the scene's own) set to v by its bar.</summary>
    private void BarSet(SceneTree s, SBlock? b, double v)
    {
        if (b == null)
        {
            s.Scroll = Math.Clamp(v, 0, s.ScrollMax);
            _sceneArr++;
        }
        else ScrollBlock(b, v);
    }

    /// <summary>The pointer moved while something is dragged: true while it is. (The left button let go outside the
    /// window ends it: the surface's move says it's up.)</summary>
    private bool SceneDrag6(Point p)
    {
        if (_drag6 is not { } d) return false;
        if (!_sceneLeftHeld)
        {
            SceneUp6();
            return true;
        }
        void Go()
        {
            switch (d.Kind)
            {
                case Drag6.Bar:
                {
                    var bar = d.S.Bars.FirstOrDefault(x => x.B == d.B);
                    if (IsEmpty(bar.Track) || bar.Room <= 0) break;
                    double at = bar.Horiz ? p.X - d.Grab - bar.Track.X : p.Y - d.Grab - bar.Track.Y;
                    BarSet(d.S, d.B, Math.Clamp(at / bar.Room, 0, 1) * bar.Max);
                    break;
                }
                case Drag6.Split:
                {
                    var b = d.B!;
                    var inner = b.Inner;
                    bool across = b.U(PAxis) == 0;
                    double total = (across ? inner.Width : inner.Height) - 0.6 * SU;
                    if (total <= 0) break;
                    double a = (across ? p.X - inner.X : p.Y - inner.Y) - 0.3 * SU;
                    b.SplitAt = Math.Clamp(a / total, 0.05, 0.95);
                    _sceneArr++;
                    break;
                }
                case Drag6.Slider:
                    SliderFromX(d.B!, SliderParts(d.B!).Track, p.X, false);
                    break;
            }
        }
        if (d.G != null) InGadget(d.G, Go);
        else Go();
        Redraw();
        return true;
    }

    /// <summary>The left button came up: a drag ends (a divider says where it went, a slider sends its value); true
    /// when one did (no click follows).</summary>
    private bool SceneUp6()
    {
        if (_drag6 is not { } d) return false;
        _drag6 = null;
        SurfaceRelease();
        void Done()
        {
            if (d.Kind == Drag6.Split && d.B is SBlock b && b.Id != 0 && b.SplitAt >= 0)
                Reply($"Split;id={b.Id};at={SplitShare(b).ToString("0.000", Inv)}");
            if (d.Kind == Drag6.Slider && d.B is SBlock sl) SliderSet(sl, SliderValue(sl), true);
        }
        if (d.G != null) InGadget(d.G, Done);
        else Done();
        Redraw();
        return true;
    }

    /// <summary>The pointer's shape over what v6 adds: the sizing arrows over a divider, a hand over a slider, a
    /// tagged canvas shape or a chart that takes clicks, the arrow over a scroll bar. Null: none of those.</summary>
    private Cursor? CursorAt6(SceneTree s, Point p)
    {
        if (_drag6 is { Kind: Drag6.Split } ds) return ds.B!.U(PAxis) == 0 ? SizeWECursor : SizeNSCursor;
        if (_drag6 != null) return ArrowCursor;
        foreach (var bar in s.Bars)
            if (bar.Track.Contains(p)) return ArrowCursor;
        for (int i = s.Order.Count - 1; i >= 0; i--)
        {
            var b = s.Order[i];
            if (b.Pass != s.Pass || b.Off || !b.Clip.Contains(p)) continue;
            if (b.Kind == BkSplit && !IsEmpty(b.SplitBar) && Inset(b.SplitBar, -3 * _u).Contains(p))
                return b.U(PAxis) == 0 ? SizeWECursor : SizeNSCursor;
            if (!b.Box.Contains(p)) continue;
            if (b.Kind == BkSlider) return HandCursor;
            int ev = b.U(PEvents);
            if (b.Kind == BkCanvas && (ev & (EvClick | EvDouble)) != 0 && CanvasTagAt(b, p) > 0) return HandCursor;
            if (b.Kind == BkChart && (ev & EvClick) != 0 && ChartIndexAt(b, p) >= 0) return HandCursor;
        }
        return null;
    }

    /// <summary>A scroll bar as drawn (RainwireScene.cs SScrollbar): its block (null: the scene's own), track, thumb,
    /// axis, how far it scrolls and how far the thumb travels.</summary>
    private readonly record struct SBar(SBlock? B, Rect Track, Rect Thumb, bool Horiz, double Max, double Room);
}
