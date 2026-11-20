using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace TERMinator.Linux.Controls;

/// <summary>
/// Scenes version 2 (Rainwire docs/SCENES_V2.md): what the four TERMinator modules did (The Wall, Messages, Files, MRC)
/// as general blocks, so those screens can be scenes built on the board. This file: the new blocks (input, vlist, menu,
/// popover, chip, drawer, viewer), the properties any block may carry (hover toolbars, animations, gradients, glow,
/// accent bars, click events, width ranges), the row ops (OpRows, OpScrollTo, OpAnimate), assets (SceneAsset), and the
/// board's Clipboard, Open, Sound and SceneToast messages. RainwireScene.cs calls in here from its layout, drawing,
/// keys and mouse code. Version 3: flat boxes (DrawFlat), row shares (FracW), the board's pick (VSetSel).
/// Linux port of the Windows client's Controls/RainwireSceneV2.cs (the master), 2026-10-22: the same packets, layout,
/// input and replies, drawn with Avalonia as RainwireScene.cs maps it (rounded rectangles through DrawRectangle's
/// radii, clips, opacity and transforms as using blocks, the clipboard asynchronous, the pointer's modifiers from the
/// release that clicked).
/// </summary>
public sealed partial class RainwireMenu
{
    // ---- Rainwire internal/scene/protocol.go, version 2 ----
    private const byte BkInput = 26, BkVList = 27, BkMenu = 28, BkPopover = 29, BkChip = 30, BkDrawer = 31, BkViewer = 32;
    private const byte PHoverShow = 90, PHoverRoot = 91, PHoverAsk = 92, PAnim = 93, PGrad = 94, PDrift = 95, PGlow = 96,
                       PBar = 97, PEvents = 98, PSelectable = 99, PWMin = 100, PWMax = 101, PPane = 102, PDrop = 103,
                       PDrag = 104, PMaxBytes = 105, PPipes = 106, PCounter = 107, PWarn = 108, PComplete = 109,
                       PTrigger = 110, PHistory = 111, PPaste = 112, PKeys = 113, PDebounce = 114, PAutosave = 115,
                       PWrapCols = 116, PMaxLines = 117, PMono = 118, PTotal = 119, PRowH = 120, PKeep = 121,
                       PNewLabel = 122, POlder = 123, PSeen = 124, PSelect = 125, PSettle = 126, PGroup = 127,
                       PSort = 128, PSortDir = 129, PDivider = 130, PContext = 131, PAnchor = 132, PSide = 133,
                       PMenuBtn = 134, PReport = 135, PClose = 136, PCount = 137, POn = 138, POpens = 139, PScale = 141,
                       PFont = 142, PAnimMs = 143, PAsset = 144,
                       PRadius = 145, PStroke = 146, PFillA = 147,   // a row, column or stack's flat box (Messages, Files and MRC as the modules draw them)
                       PFrac = 148, PFracMin = 149, PFracMax = 150,  // a share of the row's width, between lp limits (the modules' clampf(LW * f, lo, hi))
                       PHoverBg = 151, PHoverOnly = 152;   // MRC: a flat box's fill under the pointer; a block shown only then
    private const byte OpRows = 6, OpScrollTo = 7, OpAnimate = 8;
    private const byte RowsSet = 0, RowsInsert = 1, RowsRemove = 2;
    private const byte AnimSlide = 1, AnimGlow = 2, AnimFlash = 3, AnimPop = 4, AnimFade = 5;
    private const byte EvClick = 1, EvDouble = 2, EvTriple = 4, EvRight = 8, EvLong = 16;
    private const byte FxLink = 8;
    private const byte SideBelow = 0, SideAbove = 1, SideRight = 2, SideLeft = 3;

    /// <summary>The v2 properties' types (KeyType asks here for keys it doesn't know).</summary>
    private static byte KeyTypeV2(byte k) => k switch
    {
        PHoverShow or PHoverRoot or PAnim or PDrift or PEvents or PSelectable or PPane or PDrop or PDrag or PPipes
            or PCounter or PComplete or PHistory or PPaste or PMono or POlder or PSeen or PSelect or PDivider or PContext
            or PSide or PMenuBtn or PReport or PClose or POn or PScale or PFont or PSortDir or PSort or PFillA or PHoverOnly
            or PFitW or PScaleMax => TU8,
        PHoverAsk or PWMin or PWMax or PMaxBytes or PWarn or PDebounce or PAutosave or PWrapCols or PMaxLines or PKeep
            or PSettle or PGroup or PAnchor or POpens or PAnimMs or PAsset => TU16,
        PGrad or PGlow or PBar or PTrigger or PTotal or PCount or PStroke or PHoverBg => TU32,
        PRowH or PRadius or PFrac or PFracMin or PFracMax or PTileMin or PTileRatio or PScrollBy => TF32,
        PNewLabel => TStr,
        PKeys => TStrs,
        _ => KeyTypeV6(k),   // version 6's (RainwireSceneV6.cs)
    };

    private static bool IsPop(byte k) => k is BkMenu or BkPopover or BkDrawer;

    /// <summary>The scene's width in layout units while laying out (P_WMIN/P_WMAX).</summary>
    [ThreadStatic] private static double s_sceneW;

    /// <summary>Whether b shows at this width (P_WMIN, P_WMAX).</summary>
    private static bool WidthOk(SBlock b) =>
        (!b.Has(PWMin) || s_sceneW >= b.U(PWMin)) && (!b.Has(PWMax) || s_sceneW <= b.U(PWMax));

    /// <summary>Device pixels per DIP (WPF's PixelsPerDip): where things snap to whole pixels.</summary>
    private double SceneDpi => VisualRoot?.RenderScaling ?? 1;

    /// <summary>A text face's line height at size, without a field's extra (Windows' FontH).</summary>
    private double FontH(Typeface face, double size) => Text("Hg", face, size, Colors.White).Height;

    /// <summary>A gradient from a on the left to b on the right (WPF's LinearGradientBrush(a, b, 0)).</summary>
    private static LinearGradientBrush HGrad(Color a, Color b) => new()
    {
        StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
        GradientStops = { new GradientStop(a, 0), new GradientStop(b, 1) },
    };

    /// <summary>The modifiers held at the last left click on the surface (VSelectClick's Ctrl and Shift).</summary>
    private KeyModifiers _clickMods;

    // ================================================================
    // Assets (SceneAsset): pictures registered once a call, shown by number
    // ================================================================

    private readonly Dictionary<int, Bitmap> _assets = [];

    private void OnSceneAsset(byte[] p, string? path)
    {
        if (p.Length < 6 || path == null) return;
        if (SceneLoadImage(path) is Bitmap img)
        {
            if (_assets.Count >= 2000) _assets.Clear();
            _assets[U16(p, 4)] = img;
            _sceneGen++;
            Redraw();
        }
    }

    private Bitmap? Asset(SBlock b) => b.Has(PAsset) && _assets.TryGetValue(b.U(PAsset), out var a) ? a : null;

    // ================================================================
    // Board messages: Clipboard, Open, Sound, SceneToast
    // ================================================================

    private static string FieldB64(string msg, string key)
    {
        foreach (var f in msg.Split(';'))
            if (f.StartsWith(key + "=", StringComparison.Ordinal))
                try { return Encoding.UTF8.GetString(Convert.FromBase64String(f[(key.Length + 1)..])); }
                catch (FormatException) { return ""; }
        return "";
    }

    /// <summary>The v2 messages that carry no packet; true when msg was one.</summary>
    private bool HandleV2(string verb, string msg)
    {
        switch (verb)
        {
            case "Clipboard":
                ClipboardSet(FieldB64(msg, "b64"));
                return true;
            case "Open":
            {
                string url = FieldB64(msg, "b64");
                if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                    OpenLink(url);
                return true;
            }
            case "Sound":
                SceneSound(msg.Contains("name=alert") ? 0 : msg.Contains("name=ping") ? 1 : 2);
                return true;
            case "SceneToast":
                _toast = FieldB64(msg, "b64");
                if (_scene is SceneTree ts)   // the scene's own colors (a board Toast's were black on black when none had come)
                {
                    _toastBg = ts.Colors[(int)Sc.Panel] & 0xFFFFFF;
                    _toastFg = ts.Colors[(int)Sc.Text] & 0xFFFFFF;
                }
                _toastUntil = DateTime.UtcNow.AddMilliseconds(8000);
                Redraw();
                return true;
        }
        return false;
    }

    /// <summary>The board's text onto the clipboard (Avalonia's is asynchronous; a busy one keeps what it had).</summary>
    private async void ClipboardSet(string text)
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } cb) await cb.SetTextAsync(text);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"RainwireMenu: scene clipboard: {ex.Message}");
        }
    }

    /// <summary>The board's Sound: Windows plays the system's Exclamation, Beep and Asterisk; here short tones on the
    /// bell's channel (BellService's): kind 0 alert (two falling notes), 1 ping (one high blip), 2 chime (two rising).</summary>
    private static void SceneSound(int kind)
    {
        const int rate = 44100;
        (double F, int Ms)[] notes = kind switch
        {
            0 => [(660, 140), (440, 220)],
            1 => [(1320, 110)],
            _ => [(880, 130), (1320, 260)],
        };
        var pcm = new List<short>();
        foreach (var (f, ms) in notes)
        {
            int n = rate * ms / 1000;
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / rate, env = Math.Min(1, i / (rate * 0.005)) * Math.Exp(-3.0 * i / n);
                pcm.Add((short)(Math.Sin(2 * Math.PI * f * t) * env * 9000));
            }
        }
        try
        {
            Services.NativeBridge.audio_bridge_play_pcm(4, [.. pcm], pcm.Count, rate, 1);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Debug.WriteLine($"RainwireMenu: scene sound: {ex.Message}");
        }
    }

    // ================================================================
    // Patch ops: OpRows, OpScrollTo, OpAnimate
    // ================================================================

    private bool ScenePatchV2(SceneTree s, byte op, ushort id, byte[] p, int o, int len)
    {
        int end = o + len;
        switch (op)
        {
            case OpRows:
                return !s.ById.TryGetValue(id, out var v) || v.Kind != BkVList || VRows(s, v, p, o, end);
            case OpScrollTo:
                if (s.ById.TryGetValue(id, out var t) && len >= 1) ScrollToBlock(s, t, (p[o] & 1) != 0, (p[o] & 2) != 0);
                return true;
            case OpAnimate:
                if (s.ById.TryGetValue(id, out var a) && len >= 3) StartAnim(a, p[o], U16(p, o + 1));
                return true;
        }
        return true;
    }

    /// <summary>An OpRows payload into vlist v: set, insert or remove rows; row records name parents by place.</summary>
    private bool VRows(SceneTree s, SBlock v, byte[] p, int o, int end)
    {
        if (o + 7 > end) return true;
        v.Rows ??= [];
        byte mode = p[o];
        int first = (int)Math.Min(U32(p, o + 1), VMaxRows), n = U16(p, o + 5);
        o += 7;
        int count = v.Rows.Count;
        if (mode == RowsRemove)
        {
            if (first >= count) return true;
            int k = Math.Min(n, count - first);
            for (int i = first; i < first + k; i++)
                if (v.Rows[i] is SBlock gone) RowDrop(s, gone);
            v.Rows.RemoveRange(first, k);
            if (v.AnchorRow >= first + k) v.AnchorRow -= k;
            else if (v.AnchorRow >= first) v.AnchorRow = first;
            SelShift(v, first, -k);
            v.RowsW = -1;
            return true;
        }
        var rows = new List<SBlock>();
        for (int i = 0; i < n && o + 2 <= end; i++)
        {
            int k = U16(p, o);
            o += 2;
            var recs = ReadRecords(p, o, end, k);
            if (recs.Count == 0) break;
            o += recs.Sum(r => r.Bytes);
            var root = recs[0];
            root.Parent = v;
            root.ParentId = v.Id;
            root.Depth = v.Depth + 1;
            for (int j = 1; j < recs.Count; j++)
            {
                int pi = recs[j].ParentId;
                if (pi < 1 || pi > j) continue;   // a parent by place comes before its children
                var par = recs[pi - 1];
                recs[j].Parent = par;
                recs[j].ParentId = par.Id;
                recs[j].Depth = par.Depth + 1;
                if (recs[j].Depth > SceneMaxDepth) return false;
                par.Kids.Add(recs[j]);
            }
            foreach (var b in recs)
            {
                if (b.Id != 0) s.ById[b.Id] = b;
                s.Bytes += b.Bytes;
                b.Value = b.S(PValueStr);
                b.Cur = b.Value.Length;
                b.Index = b.U(PIndex);
                b.On = b.Flag(PChecked);
                if (b.Has(PAnim)) StartAnim(b, (byte)b.U(PAnim), b.U(PAnimMs));
            }
            if (s.Bytes > SceneMaxBytes) return false;
            rows.Add(root);
        }
        while (v.Rows.Count < first) v.Rows.Add(null);
        if (mode == RowsInsert)
        {
            v.Rows.InsertRange(first, rows);
            if (v.AnchorRow >= first && first < count) v.AnchorRow += rows.Count;   // rows above the view: it stays put
            SelShift(v, first, rows.Count);
            if (first >= count && !v.AtEnd && v.Flag(PFollow)) v.NewCount += rows.Count;   // appended to a log while scrolled back
        }
        else
        {
            for (int i = 0; i < rows.Count; i++)
            {
                if (first + i < v.Rows.Count)
                {
                    if (v.Rows[first + i] is SBlock old) RowDrop(s, old);
                    v.Rows[first + i] = rows[i];
                }
                else v.Rows.Add(rows[i]);
            }
        }
        v.RowsW = -1;
        return true;
    }

    private static void RowDrop(SceneTree s, SBlock b)
    {
        if (b.Id != 0 && s.ById.TryGetValue(b.Id, out var y) && y == b) s.ById.Remove(b.Id);
        s.Bytes -= b.Bytes;
        foreach (var k in b.Kids) RowDrop(s, k);
    }

    private static void SelShift(SBlock v, int at, int by)
    {
        if (v.Sel.Count == 0) return;
        var moved = v.Sel.Select(i => i >= at ? i + by : i).Where(i => i >= 0 && (by > 0 || i < at || i >= at)).ToList();
        v.Sel.Clear();
        foreach (var i in moved) v.Sel.Add(i);
    }

    private const int VMaxRows = 1_000_000;   // a list's rows at most (the board's longest, Files', is 37,440)

    private int VTotal(SBlock v) => Math.Min(Math.Max(v.U(PTotal), v.Rows?.Count ?? 0), VMaxRows);

    // ================================================================
    // Animations (P_ANIM, OpAnimate)
    // ================================================================

    private void StartAnim(SBlock b, byte kind, int ms)
    {
        b.Anim = kind;
        b.AnimAt = DateTime.UtcNow;
        b.AnimMs = ms > 0 ? ms : kind switch { AnimSlide => 450, AnimGlow => 2600, AnimFlash => 900, AnimPop => 320, _ => 400 };
        Redraw();
    }

    /// <summary>b's animation now, 0..1 (1: done or none).</summary>
    private static double AnimT(SBlock b)
    {
        if (b.Anim == 0) return 1;
        double t = (DateTime.UtcNow - b.AnimAt).TotalMilliseconds / Math.Max(b.AnimMs, 1);
        if (t >= 1) { b.Anim = 0; return 1; }
        return Math.Max(t, 0);
    }

    /// <summary>Before a block draws: its animation's transform and opacity pushed; AnimUnpush takes them off after.</summary>
    private DrawingContext.PushedState[]? AnimPush(DrawingContext dc, SBlock b)
    {
        if (b.Anim == 0) return null;
        double t = AnimT(b);
        if (t >= 1) return null;
        var r = b.Box;
        switch (b.Anim)
        {
            case AnimSlide:
            {
                double e = (1 - t) * (1 - t), dpi = SceneDpi;
                return [dc.PushTransform(Matrix.CreateTranslation(0, Math.Round(e * 14 * _u * dpi) / dpi)),
                        dc.PushOpacity(Math.Min(t * 1.6, 1))];
            }
            case AnimFade:
                return [dc.PushOpacity(t)];
            case AnimPop:
            {
                double k = 1 + 0.28 * Math.Sin(t * Math.PI), cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
                return [dc.PushTransform(Matrix.CreateTranslation(-cx, -cy) * Matrix.CreateScale(k, k) * Matrix.CreateTranslation(cx, cy))];
            }
        }
        return null;
    }

    private static void AnimUnpush(DrawingContext.PushedState[]? pushed)
    {
        if (pushed == null) return;
        for (int i = pushed.Length - 1; i >= 0; i--) pushed[i].Dispose();
    }

    /// <summary>After a block draws: the glow and flash animations over it.</summary>
    private void AnimAfter(DrawingContext dc, SBlock b)
    {
        if (b.Anim is not (AnimGlow or AnimFlash or AnimSlide)) return;
        double t = AnimT(b);
        if (t >= 1) return;
        var acc = Scol(Sc.Accent);
        double r = 6 * _u;
        if (b.Anim == AnimFlash)
        {
            double a = Math.Sin(t * Math.PI);
            dc.DrawRectangle(B(WithA(acc, (byte)(90 * a))), null, b.Box, r, r);
            return;
        }
        // glow (and a slide-in's glow, as the Wall's live posts: 2.6 s from when it came)
        double gt = b.Anim == AnimGlow ? t : 0;
        double fade = 1 - gt;
        dc.DrawRectangle(B(WithA(acc, (byte)(45 * fade))), new Pen(B(WithA(acc, (byte)(220 * fade))), 1.2 * _u), b.Box, r, r);
    }

    // ================================================================
    // Any block's v2 look: accent bar, glow, gradient panel, hover-only blocks
    // ================================================================

    /// <summary>Whether a P_HOVERSHOW block is hidden now: its hover root isn't hovered (or selected).</summary>
    private bool HoverHidden(SBlock b)
    {
        for (var x = b; x != null; x = x.Parent)
        {
            if (x.Flag(PHoverOnly))   // P_HOVERONLY: in the flow, shown only while its box (P_HOVERBG) is under the pointer
            {
                var box = x.Parent;
                while (box != null && !box.Has(PHoverBg)) box = box.Parent;
                if (box == null || !IsHover(box)) return true;
            }
            if (!x.Flag(PHoverShow)) continue;
            var root = x.Parent;
            while (root != null && !root.Flag(PHoverRoot) && root.Parent?.Kind != BkVList) root = root.Parent;
            if (root == null) return true;
            bool sel = root.Parent is { Kind: BkVList } v && v.Rows != null && v.Sel.Contains(v.Rows.IndexOf(root));
            if (!(IsHover(root) || sel || x.Box.Contains(_mouse))) return true;
        }
        return false;
    }

    /// <summary>P_BAR and P_GLOW around b, before it draws.</summary>
    private void DrawDecor(DrawingContext dc, SBlock b)
    {
        if (b.Has(PGlow) && (b.U32(PGlow) & 0xFF000000) == 0)
        {
            var g = C(b.U32(PGlow));
            for (int i = 3; i >= 1; i--)
                dc.DrawRectangle(null, new Pen(B(WithA(g, (byte)(40 / i))), 2 * i * _u), Inset(b.Box, -i * _u), 8 * _u, 8 * _u);
        }
        if (b.Has(PBar) && (b.U32(PBar) & 0xFF000000) == 0)
            dc.DrawRectangle(B(C(b.U32(PBar))), null, R(b.Box.X, b.Box.Y + 4 * _u, 3 * _u, Math.Max(b.Box.Height - 8 * _u, 2 * _u)), 1.5 * _u, 1.5 * _u);
    }

    /// <summary>A row child's width as a share of the row (P_FRAC of inner, kept within P_FRACMIN..P_FRACMAX lp), or null.</summary>
    private double? FracW(SBlock k, double inner)
    {
        if (!k.Has(PFrac) || !double.IsFinite(inner)) return null;
        double w = inner * k.F(PFrac), lo = k.F(PFracMin) * _u, hi = k.F(PFracMax) * _u;
        if (hi > 0) w = Math.Min(w, hi);
        return Math.Max(w, lo);
    }

    /// <summary>A row, column or stack's own box, flat as the modules draw theirs: P_BG filled (to P_GRAD across), P_STROKE
    /// its outline, P_RADIUS its corners in the modules' logical pixels, P_FILLA the fill's opacity. Nothing when it has
    /// none of them.</summary>
    private void DrawFlat(DrawingContext dc, SBlock b)
    {
        Color? bg = Ucol(b, PBg), line = Ucol(b, PStroke);
        if (b.Has(PHoverBg) && !_waiting && IsHover(b) && Ucol(b, PHoverBg) is Color hb) bg = hb;   // P_HOVERBG: lit under the pointer
        if (bg == null && line == null) return;
        IBrush? fill = null;
        if (bg is Color c)
        {
            if (b.Has(PFillA)) c = WithA(c, (byte)b.U(PFillA));   // a wash: the modules' dialogs dim what's behind them
            fill = Ucol(b, PGrad) is Color c2 ? HGrad(c, c2) : B(c);
        }
        Pen? pen = line is Color lc ? new Pen(B(lc), Math.Max(_u, 1)) : null;
        double r = b.F(PRadius) * _u;
        var box = pen != null ? Inset(b.Box, pen.Thickness / 2) : b.Box;
        if (r > 0) dc.DrawRectangle(fill, pen, box, r, r);
        else dc.DrawRectangle(fill, pen, box);
    }

    /// <summary>A panel's background as a gradient from its color to P_GRAD, drifting when P_DRIFT (the Wall's topic
    /// banner); true when drawn so (the plain fill is skipped).</summary>
    private bool DrawGradPanel(DrawingContext dc, SBlock b, Color bg, Pen? pen, double radius)
    {
        if (!b.Has(PGrad) || (b.U32(PGrad) & 0xFF000000) != 0) return false;
        var c2 = C(b.U32(PGrad));
        double shift = b.Flag(PDrift) ? 0.5 + 0.5 * Math.Sin(Now / 4000.0) : 0.5;
        var gb = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(bg, 0), new GradientStop(Mix(bg, c2, 0.75), shift), new GradientStop(bg, 1) },
        };
        dc.DrawRectangle(gb, pen, b.Box, radius, radius);
        return true;
    }

    // ================================================================
    // Layout of the new blocks
    // ================================================================

    private Size NaturalV2(SBlock b, double inner, double fixH)
    {
        double u = SU;
        switch (b.Kind)
        {
            case BkInput:
            {
                double lines = b.Flag(PMulti) ? Math.Max(b.U(PLines, 3), 1) : 1;
                return new Size(16 * u, b.Flag(PMulti) ? 0.6 * u + lines * FieldLineH() : 1.8 * u);
            }
            // as wide as they're let be; measured with no limit (a row sizing its children), a fixed natural width:
            // an infinite one made the row's sums NaN and the text measuring threw on every frame
            case BkVList: return new Size(double.IsInfinity(inner) ? 24 * u : inner, fixH > 0 ? fixH : 8 * u);
            case BkViewer: return new Size(double.IsInfinity(inner) ? 24 * u : inner, fixH > 0 ? fixH : 12 * u);
            case BkChip: return ChipSize(b);
            case BkMenu: case BkPopover: case BkDrawer: return default;   // pop-ups take no room in the flow
        }
        return default;
    }

    private Size ChipSize(SBlock b)
    {
        double u = SU, h = 1.35 * u, w = 0.9 * u;
        string t = b.S(PText);
        if (b.Has(PIcon) || b.Has(PAsset)) w += 1.0 * u + (t.Length > 0 || b.Has(PCount) ? 0.3 * u : 0);
        if (t.Length > 0) w += TextW(t, UiBold, SmallSize);
        if (b.Has(PCount)) w += TextW(b.U(PCount).ToString(), UiBold, SmallSize) + (t.Length > 0 ? 0.35 * u : 0);
        if (b.Flag(PClose)) w += 1.0 * u;
        return new Size(w, h);
    }

    /// <summary>A vlist's rows placed: every row's height (measured when it's here and the width changed, else the
    /// estimate), the scroll kept (following the end, or the anchor row still), and the rows in view arranged.</summary>
    private void ArrangeVList(SceneTree s, SBlock v, Rect view, Rect clip)
    {
        v.Rows ??= [];
        int total = VTotal(v);
        double est = (v.Has(PRowH) ? v.F(PRowH) : 1.6) * SU, w = view.Width - 10 * _u;
        bool remeasure = Math.Abs(v.RowsW - w) > 0.25;
        while (v.RowH.Count < total) v.RowH.Add(est);
        if (v.RowH.Count > total) v.RowH.RemoveRange(total, v.RowH.Count - total);
        for (int i = 0; i < total; i++)
        {
            var row = i < v.Rows.Count ? v.Rows[i] : null;
            if (row == null) { v.RowH[i] = est; continue; }
            if (remeasure || row.MGen != _sceneGen) v.RowH[i] = row.Flag(PDivider) ? 1.7 * SU : SMeasure(row, w).Height;
        }
        v.RowsW = w;
        v.RowY.Clear();
        double y = 0;
        for (int i = 0; i < total; i++)
        {
            v.RowY.Add(y);
            y += v.RowH[i];
        }
        double was = v.Scroll;
        v.ScrollMax = Math.Max(y - view.Height, 0);
        v.View = view.Height;
        if (v.Flag(PFollow) && v.AtEnd) v.Scroll = v.ScrollMax;
        else if (v.AnchorRow >= 0 && v.AnchorRow < total) v.Scroll = v.RowY[v.AnchorRow] - v.AnchorOff;
        v.Scroll = Math.Clamp(v.Scroll, 0, v.ScrollMax);
        v.AtEnd = v.Scroll >= v.ScrollMax - 1;
        if (v.AtEnd) v.NewCount = 0;
        ScrollPosApply(v);   // version 6: P_SCROLLPOS
        if (Math.Abs(was - v.Scroll) > 0.5) { v.SeenAt = DateTime.UtcNow; v.SeenSent = false; }
        // the rows in view (and a little either side), placed on whole device pixels so they move as one
        double dpi = SceneDpi, top = Math.Round((view.Y - v.Scroll) * dpi) / dpi;
        var c = Isect(clip, view);
        int firstVis = -1, lastVis = -1, firstMissing = -1, lastMissing = -1;
        for (int i = 0; i < total; i++)
        {
            double ry = top + v.RowY[i];
            if (ry + v.RowH[i] < view.Y - 200 * _u || ry > view.Bottom + 200 * _u) continue;
            bool inView = ry + v.RowH[i] > view.Y && ry < view.Bottom;
            if (inView)
            {
                if (firstVis < 0) { firstVis = i; v.AnchorOff = v.RowY[i] - v.Scroll; }
                lastVis = i;
            }
            var row = i < v.Rows.Count ? v.Rows[i] : null;
            if (row == null)
            {
                if (firstMissing < 0) firstMissing = i;
                lastMissing = i;
                continue;
            }
            SArrange(s, row, R(view.X, ry, w, v.RowH[i]), IsEmpty(c) ? default : c, v.Off);
        }
        v.AnchorRow = firstVis;
        v.SelCur = Math.Min(v.SelCur, total - 1);
        // rows the board hasn't sent: ask for them (64 at a time around what's missing), and older rows near the top
        if (firstMissing >= 0 && v.Id != 0)
        {
            int from = Math.Max(firstMissing - 16, 0), to = Math.Min(Math.Max(lastMissing + 16, from + 63), total - 1);
            if ((from, to) != v.Asked || (DateTime.UtcNow - v.AskedAt).TotalSeconds > 5)
            {
                v.Asked = (from, to);
                v.AskedAt = DateTime.UtcNow;
                Reply($"Rows;id={v.Id};from={from};to={to}");
            }
        }
        if (v.Flag(POlder) && v.Id != 0 && v.Scroll < view.Height * 0.25 && (DateTime.UtcNow - v.OlderAt).TotalSeconds > 2)
        {
            v.OlderAt = DateTime.UtcNow;
            Reply($"Older;id={v.Id}");
        }
        v.Inner = view;
    }

    /// <summary>Bring b into view (centered, flashed) by scrolling the vlists and scrolls it's in.</summary>
    private void ScrollToBlock(SceneTree s, SBlock b, bool center, bool flash)
    {
        SBlock? row = b;
        while (row != null && row.Parent is { Kind: not BkVList }) row = row.Parent;
        if (row?.Parent is { Kind: BkVList } v && v.Rows != null)
        {
            int i = v.Rows.IndexOf(row);
            if (i >= 0 && i < v.RowY.Count)
            {
                v.Scroll = Math.Clamp(v.RowY[i] - (center ? (v.View - v.RowH[i]) / 2 : 0), 0, v.ScrollMax);
                v.AnchorRow = -1;
                v.AtEnd = v.Scroll >= v.ScrollMax - 1;
                _sceneArr++;
            }
        }
        else SceneIntoView(s, b);
        if (flash) StartAnim(b, AnimFlash, 1300);
        Redraw();
    }

    // ================================================================
    // Drawing the new blocks
    // ================================================================

    private void DrawV2(DrawingContext dc, SceneTree s, SBlock b, Rect clip)
    {
        switch (b.Kind)
        {
            case BkInput: DrawInput(dc, s, b); break;
            case BkVList: DrawVList(dc, s, b, clip); break;
            case BkChip: DrawChip(dc, b); break;
            case BkViewer: DrawViewer(dc, s, b); break;
        }
    }

    private void DrawVList(DrawingContext dc, SceneTree s, SBlock v, Rect clip)
    {
        var c = Isect(clip, v.Inner);
        if (!IsEmpty(c) && v.Rows != null)
        {
            using (dc.PushClip(c))
            {
                for (int i = 0; i < v.Rows.Count; i++)
                {
                    var row = v.Rows[i];
                    if (row == null || row.Pass != s.Pass) continue;
                    bool sel = v.Sel.Contains(i), hot = v.U(PSelect) > 0 && IsHover(row);
                    if (sel || hot || i == v.SelCur && Focused(s, v))
                    {
                        var hl = sel ? WithA(Scol(Sc.SelBg), 120) : WithA(Scol(Sc.ButtonHot), 60);
                        dc.DrawRectangle(B(hl), null, row.Box, 6 * _u, 6 * _u);
                    }
                    if (row.Flag(PDivider)) DrawDivider(dc, row);
                    else DrawBlock(dc, s, row, c);
                }
                // rows not here yet: quiet placeholders
                for (int i = 0; i < v.RowY.Count && i < VTotal(v); i++)
                {
                    if (i < v.Rows.Count && v.Rows[i] != null) continue;
                    double ry = v.Inner.Y - v.Scroll + v.RowY[i];
                    if (ry + v.RowH[i] < c.Y || ry > c.Bottom) continue;
                    dc.DrawRectangle(B(WithA(Scol(Sc.Dim), 30)), null, R(v.Inner.X + 4 * _u, ry + 4 * _u, v.Inner.Width * 0.6, Math.Max(v.RowH[i] - 8 * _u, 2)), 4 * _u, 4 * _u);
                }
            }
        }
        SScrollbar(dc, v.Inner, v.Scroll, v.ScrollMax, false, s, v);
        if (v.NewCount > 0)   // "N new ↓": scrolled back while rows came
        {
            string label = v.S(PNewLabel);
            label = label.Length > 0 ? label.Replace("%d", v.NewCount.ToString()) : $"{v.NewCount} new ↓";
            var ft = Text(label, UiBold, SmallSize, Scol(Sc.ButtonFg));
            double w = ft.Width + 1.4 * SU, h = 1.5 * SU;
            var r = R(v.Inner.X + (v.Inner.Width - w) / 2, v.Inner.Bottom - h - 0.6 * SU, w, h);
            dc.DrawRectangle(B(Scol(Sc.Accent)), null, r, h / 2, h / 2);
            dc.DrawText(Text(label, UiBold, SmallSize, Colors.Black), new Point(r.X + 0.7 * SU, MidY(ft, UiBold, SmallSize, r.Y, r.Height)));
            v.Hits.Clear();
            v.Hits.Add((r, -2));   // the chip: a click jumps to the end
        }
    }

    private void DrawDivider(DrawingContext dc, SBlock row)
    {
        var r = row.Box;
        string label = row.S(PText);
        var acc = Ucol(row, PFg) ?? Scol(Sc.Accent);
        double cy = r.Y + r.Height / 2, t = Math.Max(1 * _u, 1);
        if (label.Length == 0)
        {
            dc.DrawRectangle(B(WithA(acc, 160)), null, R(r.X, cy, r.Width, t));
            return;
        }
        var ft = Text(label, UiBold, SmallSize, acc);
        double tw = ft.Width + 1.2 * SU, x0 = r.X + (r.Width - tw) / 2;
        dc.DrawRectangle(B(WithA(acc, 140)), null, R(r.X, cy, Math.Max(x0 - r.X, 0), t));
        dc.DrawRectangle(B(WithA(acc, 140)), null, R(x0 + tw, cy, Math.Max(r.Right - x0 - tw, 0), t));
        dc.DrawText(ft, new Point(x0 + 0.6 * SU, MidY(ft, UiBold, SmallSize, r.Y, r.Height)));
    }

    private void DrawChip(DrawingContext dc, SBlock b)
    {
        var r = b.Box;
        double u = SU, h = r.Height;
        bool on = b.Flag(POn);
        Color bg = Ucol(b, PBg) ?? (on ? Mix(Scol(Sc.Panel), Scol(Sc.Accent), 0.35) : Mix(Scol(Sc.Panel), Scol(Sc.Text), 0.08));
        Color fg = Ucol(b, PFg) ?? (on ? Scol(Sc.Text) : RoleColor(b, Scol(Sc.Text)));
        var pen = new Pen(B(on ? Scol(Sc.Accent) : WithA(Scol(Sc.Border), 160)), (on ? 1.3 : 1) * _u);
        bool hot = IsHover(b) && (b.Id != 0 || b.Flag(PClose));
        dc.DrawRectangle(B(hot ? Mix(bg, Scol(Sc.Text), 0.12) : bg), pen, r, h / 2, h / 2);
        double x = r.X + 0.45 * u;
        if (Asset(b) is Bitmap img)
        {
            DrawFitted(dc, img, img.PixelSize.Width, img.PixelSize.Height, R(x, r.Y + (h - 1.0 * u) / 2, 1.0 * u, 1.0 * u), 0, true);
            x += 1.0 * u + 0.3 * u;
        }
        else if (b.Has(PIcon))
        {
            DrawIcon(dc, b.U(PIcon), fg, R(x, r.Y + (h - 1.0 * u) / 2, 1.0 * u, 1.0 * u));
            x += 1.0 * u + 0.3 * u;
        }
        string t = b.S(PText);
        if (t.Length > 0)
        {
            var ft = Text(t, UiBold, SmallSize, fg);
            dc.DrawText(ft, new Point(x, MidY(ft, UiBold, SmallSize, r.Y, h)));
            x += ft.Width + 0.35 * u;
        }
        if (b.Has(PCount))
        {
            var ft = Text(b.U(PCount).ToString(), UiBold, SmallSize, fg);
            dc.DrawText(ft, new Point(x, MidY(ft, UiBold, SmallSize, r.Y, h)));
        }
        if (b.Flag(PClose))
        {
            double cx = r.Right - 0.6 * u, cy = r.Y + h / 2, a = 0.22 * u;
            var p = new Pen(B(fg), 1.3 * _u, lineCap: PenLineCap.Round);
            dc.DrawLine(p, new Point(cx - a, cy - a), new Point(cx + a, cy + a));
            dc.DrawLine(p, new Point(cx - a, cy + a), new Point(cx + a, cy - a));
        }
    }

    private void DrawViewer(DrawingContext dc, SceneTree s, SBlock b)
    {
        var r = b.Inner;
        if (Asset(b) is Bitmap img && img.PixelSize.Width > 0)
        {
            // the whole-number scale (P_SCALE, else the largest that fits: RainwireSceneFiles.cs), drawn at it exactly,
            // on device pixels; centered, or from the top when it fits the width only (P_FITW), the rest scrolling
            double dpi = SceneDpi;
            double pw = img.PixelSize.Width, ph = img.PixelSize.Height, sc = ViewerScale(b, r, pw, ph);
            double w = pw * sc * _u, h = ph * sc * _u;
            double x = Math.Round((r.X + Math.Max((r.Width - w) / 2, 0)) * dpi) / dpi;
            double y = Math.Round((b.Flag(PFitW) ? r.Y : r.Y + Math.Max((r.Height - h) / 2, 0)) * dpi) / dpi;
            b.View = r.Height;
            b.ScrollMax = Math.Max(h - r.Height, 0);
            b.Scroll = Math.Clamp(b.Scroll, 0, b.ScrollMax);
            ScrollPosApply(b);   // version 6: P_SCROLLPOS
            using (dc.PushClip(r))
                b.PicRect = DrawFitted(dc, img, pw, ph, R(x, y - b.Scroll, w, h), FitStretch, sc < 1);
            SScrollbar(dc, r, b.Scroll, b.ScrollMax, false, s, b);   // version 6: a bar to drag
        }
    }

    // ================================================================
    // The rich input (P_PIPES, P_COUNTER, chips, completion, history, keys)
    // ================================================================

    /// <summary>The Rainwire pipe colors |00-|15 (foreground) and |16-|23 (background), as the board's markup.</summary>
    private static readonly uint[] PipeFg =
    [
        0x000000, 0x0000AA, 0x00AA00, 0x00AAAA, 0xAA0000, 0xAA00AA, 0xAA5500, 0xAAAAAA,
        0x555555, 0x5555FF, 0x55FF55, 0x55FFFF, 0xFF5555, 0xFF55FF, 0xFFFF55, 0xFFFFFF,
    ];

    /// <summary>The input's text as colored runs: a pipe code drawn dim, what follows it in its color.</summary>
    private List<(int Start, int Len, Color Fg, Color? Bg)> PipeRuns(string v, Color def)
    {
        var runs = new List<(int, int, Color, Color?)>();
        Color fg = def;
        Color? bg = null;
        int at = 0;
        for (int i = 0; i < v.Length;)
        {
            if (v[i] == '|' && i + 2 < v.Length && char.IsDigit(v[i + 1]) && char.IsDigit(v[i + 2]))
            {
                if (i > at) runs.Add((at, i - at, fg, bg));
                runs.Add((i, 3, WithA(Scol(Sc.Dim), 150), null));
                int n = (v[i + 1] - '0') * 10 + (v[i + 2] - '0');
                if (n < 16) fg = n == 0 ? def : C(PipeFg[n]);
                else if (n < 24) bg = n == 16 ? null : C(PipeFg[n - 16]);
                i += 3;
                at = i;
                continue;
            }
            i++;
        }
        if (at < v.Length) runs.Add((at, v.Length - at, fg, bg));
        return runs;
    }

    private static int Utf8Len(string v) => Encoding.UTF8.GetByteCount(v);

    /// <summary>The input: a field, plus chips before the text, live pipe colors, and the counter at the right.</summary>
    private void DrawInput(DrawingContext dc, SceneTree s, SBlock b)
    {
        double u = _u, su = SU, size = SSize;
        var r = b.Box;
        bool on = Focused(s, b), multi = b.Flag(PMulti);
        Color bg = Ucol(b, PBg) ?? Scol(Sc.FieldBg), fg = Ucol(b, PFg) ?? Scol(Sc.FieldFg);
        double fr = _st.SelRadius * u;
        var pen = new Pen(B(on ? Scol(Sc.Focus) : WithA(Scol(Sc.Border), 150)), (on ? 1.5 : 1) * u);
        if (!(b.Has(PStroke) && Ucol(b, PStroke) == null))   // P_STROKE none: no box of its own (it's inside the board's)
            dc.DrawRectangle(B(on ? bg : WithA(bg, 220)), pen, Inset(r, 0.5 * u), fr, fr);
        // chips first, inside the left edge
        double x = r.X + 0.5 * su;
        foreach (var k in b.Kids)
        {
            if (k.Kind != BkChip || k.Flag(PHidden)) continue;
            var cs = ChipSize(k);
            k.Box = R(x, r.Y + (multi ? 0.3 * su : (r.Height - cs.Height) / 2), cs.Width, cs.Height);
            k.Pass = s.Pass;
            k.Clip = r;
            DrawChip(dc, k);
            x += cs.Width + 0.35 * su;
        }
        // the counter at the right
        int max = b.U(PMaxBytes) > 0 ? b.U(PMaxBytes) : b.U(PMax);
        double right = r.Right - 0.5 * su;
        if (b.Flag(PCounter) && max > 0)
        {
            int n = b.U(PMaxBytes) > 0 ? Utf8Len(b.Value) : Chars(b.Value);
            int warn = b.Has(PWarn) ? b.U(PWarn) : max * 9 / 10;
            var cc = n >= max ? Color.FromRgb(0xFF, 0x55, 0x55) : n >= warn ? Color.FromRgb(0xFF, 0xB8, 0x4D) : Scol(Sc.Dim);
            var ft = Text($"{n}/{max}", Ui, SmallSize, cc);
            dc.DrawText(ft, new Point(right - ft.Width, multi ? r.Bottom - ft.Height - 0.25 * su : MidY(ft, Ui, SmallSize, r.Y, r.Height)));
            right -= ft.Width + 0.5 * su;
        }
        var room = R(x, r.Y, Math.Max(right - x, 1), r.Height);
        bool caretOn = on && !_waiting && ((DateTime.UtcNow - _caretAt).TotalMilliseconds < 600 || (long)(Now / 530) % 2 == 0);
        bool secret = b.Flag(PSecret);
        string shown = secret ? new string('•', Chars(b.Value)) : b.Value;
        int cur = secret ? Chars(b.Value[..Math.Min(b.Cur, b.Value.Length)]) : Math.Min(b.Cur, b.Value.Length);
        var face = b.Flag(PMono) ? MonoReg : Ui;
        using (dc.PushClip(room))
        {
            if (b.Value.Length == 0 && b.S(PPlace) is { Length: > 0 } place)
            {
                var pt = Fit(place, face, size, Scol(Sc.Dim), Math.Max(room.Width, 1));
                dc.DrawText(pt, new Point(room.X, multi ? r.Y + 0.3 * su : MidY(pt, face, size, r.Y, r.Height)));
            }
            double lh = FontH(face, size) + 2 * u;
            // version 6: a multi-line input's lines wrapped by words at its width (or P_WRAPCOLS), as typed
            var vl = multi ? (b.VLines = WrapValue(b, shown, face, size, room.Width - 4 * u)) : null;
            string[] lines = vl != null ? vl.Select(l => shown[l.Start..l.End]).ToArray() : shown.Split('\n');
            int curLine = vl != null ? VLineOf(vl, cur) : shown[..cur].Count(c => c == '\n');
            int lineStart = vl != null ? vl[curLine].Start : shown[..cur].LastIndexOf('\n') + 1;
            double top = multi ? r.Y + 0.3 * su : 0;
            if (multi)
            {
                double cy = curLine * lh, viewH = Math.Max(r.Height - 0.6 * su, lh);
                if (cy - b.Scroll > viewH - lh) b.Scroll = cy - (viewH - lh);
                if (cy < b.Scroll) b.Scroll = cy;
                b.Scroll = Math.Max(b.Scroll, 0);
            }
            double cxw = TextW(shown[lineStart..cur], face, size), shift = cxw > room.Width - 4 * u ? cxw - (room.Width - 4 * u) : 0;
            for (int li = 0; li < lines.Length; li++)
            {
                string line = lines[li];
                var ft = Text(line.Length > 0 ? line : " ", face, size, fg);
                bool pipes = b.Flag(PPipes) && !secret;
                var runs = pipes ? PipeRuns(line, fg) : null;
                if (runs != null)
                    foreach (var (st, len, c, _) in runs)
                    {
                        if (len <= 0) continue;
                        ft.SetForegroundBrush(B(c), st, len);
                    }
                double ly = multi ? top + li * lh - b.Scroll : MidY(ft, face, size, r.Y, r.Height);
                if (!multi || (ly + lh >= r.Y && ly <= r.Bottom))
                {
                    if (runs != null)
                        foreach (var (st, len, _, bgc) in runs)
                            if (bgc is Color bc && len > 0 && ft.BuildHighlightGeometry(new Point(room.X - shift, ly), st, len) is Geometry g)
                                dc.DrawGeometry(B(bc), null, g);
                    dc.DrawText(ft, new Point(room.X - shift, ly));
                }
            }
            if (caretOn)
            {
                double cy = multi ? top + curLine * lh - b.Scroll + 2 * u : r.Y + 0.3 * su;
                dc.DrawRectangle(B(Scol(Sc.Focus)), null, R(room.X - shift + cxw, cy, 2 * u, multi ? lh - 4 * u : r.Height - 0.6 * su));
            }
            b.TextX = room.X - shift;
            b.TextY = multi ? top - b.Scroll : r.Y;
            b.LineH = lh;
        }
    }

    /// <summary>A key on a focused input before the field's own editing: keys passed to the board (P_KEYS, "empty+"
    /// ones only while it's empty), the completion menu, history; true when used.</summary>
    private bool InputKey(SceneTree s, SBlock b, Key key, bool ctrl, bool shift)
    {
        string name = KeyName(key, ctrl, shift);
        foreach (var k in b.Strs(PKeys))
        {
            bool emptyOnly = k.StartsWith("empty+", StringComparison.Ordinal);
            string kk = emptyOnly ? k[6..] : k;
            if (kk == name && (!emptyOnly || b.Value.Length == 0))
            {
                // with the fields, as a press has them: a key that sends (Messages' Ctrl+Enter) needs what's typed.
                // No Alt here: SceneKey only asks a focused block about keys without it.
                if (FnFor(key) > 1) SceneSendKeyX(s, $"fn={FnFor(key)}");   // F2-F12 (F3 finds again in MRC)
                else SceneSendKey(s, VkFor(key), false, ctrl, CharFor(key), SceneFieldsB64(s));
                return true;
            }
        }
        // a completion menu hanging from this input: it has the arrows, Tab and Enter
        if (OpenMenuFor(s, b) is SBlock menu)
        {
            var items = menu.Kids.Where(k => k.Kind == BkButton && !k.Flag(PHidden)).ToList();
            if (items.Count > 0)
            {
                int i = Math.Max(items.FindIndex(k => k.On), 0);
                switch (key)
                {
                    case Key.Up: items[i].On = false; items[(i + items.Count - 1) % items.Count].On = true; return true;
                    case Key.Down: items[i].On = false; items[(i + 1) % items.Count].On = true; return true;
                    case Key.Tab: case Key.Return: CompletePick(s, b, items[i]); return true;
                    case Key.Escape: menu.Closed = true; return true;
                }
            }
        }
        if (key == Key.Tab && !ctrl && b.U(PComplete) != 0 && CompleteWord(b) is { Length: > 0 } word)
        {
            if (b.U(PComplete) == 2) CompleteLocal(b, word);
            else CompleteAsk(b);
            return true;
        }
        // history: Up on the first line, Down on the last
        int hist = b.U(PHistory);
        if (hist > 0 && key is Key.Up or Key.Down && !ctrl)
        {
            bool multi = b.Flag(PMulti);
            if (multi && b.VLines is { Count: > 0 } vl)   // version 6: the first and last drawn lines
            {
                int at = VLineOf(vl, Math.Min(b.Cur, b.Value.Length));
                if (key == Key.Up && at > 0 || key == Key.Down && at < vl.Count - 1) return false;
            }
            else if (multi && key == Key.Up && b.Value[..b.Cur].Contains('\n')) return false;
            else if (multi && key == Key.Down && b.Value[b.Cur..].Contains('\n')) return false;
            if (b.History.Count == 0) return true;
            if (b.HistAt < 0) b.Draft = b.Value;
            b.HistAt = key == Key.Up ? (b.HistAt < 0 ? b.History.Count - 1 : Math.Max(b.HistAt - 1, 0))
                                     : (b.HistAt < 0 ? -1 : b.HistAt + 1 >= b.History.Count ? -1 : b.HistAt + 1);
            b.Value = b.HistAt < 0 ? b.Draft : b.History[b.HistAt];
            b.Cur = b.Value.Length;
            FieldChanged(b);
            return true;
        }
        return false;
    }

    private static string KeyName(Key key, bool ctrl, bool shift)
    {
        string k = key switch
        {
            Key.Return => "enter", Key.Delete => "delete", Key.Back => "backspace", Key.Escape => "esc", Key.Tab => "tab",
            Key.Up => "up", Key.Down => "down", Key.Left => "left", Key.Right => "right", Key.PageUp => "pgup",
            Key.PageDown => "pgdn", Key.Home => "home", Key.End => "end",
            >= Key.F1 and <= Key.F12 => "f" + (key - Key.F1 + 1),
            >= Key.A and <= Key.Z => ((char)('a' + (key - Key.A))).ToString(),
            >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
            >= Key.NumPad0 and <= Key.NumPad9 => ((char)('0' + (key - Key.NumPad0))).ToString(),
            _ => "",
        };
        return (ctrl ? "ctrl+" : "") + (shift && k.Length > 1 ? "shift+" : "") + k;
    }

    /// <summary>F1-F12's number, 0 for any other key.</summary>
    private static int FnFor(Key key) => key is >= Key.F1 and <= Key.F12 ? key - Key.F1 + 1 : 0;

    private static int VkFor(Key key) => key switch
    {
        Key.Left => VkLeft, Key.Right => VkRight, Key.Up => VkUp, Key.Down => VkDown, Key.Delete => VkDelete,
        Key.Back => VkBackspace, Key.Tab => VkTab, Key.F1 => VkF1, Key.Return => VkEnter, Key.PageUp => VkPgUp,
        Key.PageDown => VkPgDn, Key.Home => VkHome, Key.End => VkEnd, _ => VkChar,
    };

    private static int CharFor(Key key) => key switch
    {
        >= Key.A and <= Key.Z => 'a' + (key - Key.A),
        >= Key.D0 and <= Key.D9 => '0' + (key - Key.D0),
        >= Key.NumPad0 and <= Key.NumPad9 => '0' + (key - Key.NumPad0),
        _ => 0,
    };

    /// <summary>Typed text into an input: limits (P_MAX characters, P_MAXBYTES bytes), "empty+" keys, and the
    /// completion trigger; true when it went in.</summary>
    private bool InputText(SceneTree s, SBlock b, string text)
    {
        if (b.Value.Length == 0 && text.Length == 1)
            foreach (var k in b.Strs(PKeys))
                if (k == "empty+" + char.ToLowerInvariant(text[0]))
                {
                    SceneSendKey(s, VkChar, false, false, text[0]);
                    return true;
                }
        int maxBytes = b.U(PMaxBytes);
        if (maxBytes > 0)
        {
            var sb = new StringBuilder();
            foreach (var rune in text.EnumerateRunes())
            {
                if (Utf8Len(b.Value) + Encoding.UTF8.GetByteCount(sb.ToString()) + rune.Utf8SequenceLength > maxBytes) break;
                sb.Append(rune.ToString());
            }
            text = sb.ToString();
        }
        if (text.Length == 0 || !FieldInsert(b, text)) return false;
        b.HistAt = -1;
        FieldChanged(b);
        if (b.U(PComplete) == 1 && b.Has(PTrigger) && text.EnumerateRunes().Any(r => r.Value == (int)b.U32(PTrigger))) CompleteAsk(b);
        else if (OpenMenuFor(s, b) != null && CompleteWord(b) is { Length: > 0 }) CompleteAsk(b);   // narrow it as they type
        return true;
    }

    /// <summary>The word before the caret (back to a space), "" for none.</summary>
    private static string CompleteWord(SBlock b)
    {
        int i = Math.Min(b.Cur, b.Value.Length);
        int st = i;
        while (st > 0 && !char.IsWhiteSpace(b.Value[st - 1])) st--;
        b.CompleteAt = st;
        return b.Value[st..i];
    }

    private void CompleteAsk(SBlock b)
    {
        if (b.Id == 0) return;
        string w = CompleteWord(b);
        Reply($"Complete;id={b.Id};q={Convert.ToBase64String(Encoding.UTF8.GetBytes(w))}");
    }

    /// <summary>Tab completion from P_OPTIONS (MRC's nicks): the next option starting with the word.</summary>
    private void CompleteLocal(SBlock b, string word)
    {
        var opts = b.Strs(POptions);
        string stem = b.Draft.Length > 0 && word.StartsWith(b.Draft, StringComparison.OrdinalIgnoreCase) ? b.Draft : word;
        var hits = opts.Where(o => o.StartsWith(stem, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hits.Count == 0) return;
        int i = hits.FindIndex(o => o.Equals(word, StringComparison.OrdinalIgnoreCase));
        string pick = hits[(i + 1) % hits.Count];
        b.Draft = stem;
        b.Value = b.Value[..b.CompleteAt] + pick + b.Value[Math.Min(b.Cur, b.Value.Length)..];
        b.Cur = b.CompleteAt + pick.Length;
        FieldChanged(b);
    }

    /// <summary>A completion picked from the menu hanging from input b: the word replaced by the item's label.</summary>
    private void CompletePick(SceneTree s, SBlock b, SBlock item)
    {
        string label = item.S(PLabel);
        if (label.Length == 0) label = item.S(PText);
        CompleteWord(b);
        b.Value = b.Value[..b.CompleteAt] + label + " " + b.Value[Math.Min(b.Cur, b.Value.Length)..];
        b.Cur = b.CompleteAt + label.Length + 1;
        if (OpenMenuFor(s, b) is SBlock m) m.Closed = true;
        FieldChanged(b);
        Redraw();
    }

    private SBlock? OpenMenuFor(SceneTree s, SBlock b) =>
        s.Pops.LastOrDefault(m => m.Kind == BkMenu && !m.Closed && m.U(PAnchor) == b.Id && b.Id != 0);

    /// <summary>A paste into an input: P_PASTE 1 joins lines with spaces; the limits cut it, with a toast. The text
    /// comes later (Avalonia's clipboard is asynchronous), while the same scene is up.</summary>
    private async void InputPaste(SceneTree s, SBlock b)
    {
        if (b.Off) return;
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is not { } cb) return;
            string? got = await cb.GetTextAsync();
            if (string.IsNullOrEmpty(got) || _scene != s || !_isScene || !IsUp) return;
            string t = got.Replace("\r", "");
            if (b.U(PPaste) == 1 || !b.Flag(PMulti)) t = string.Join(' ', t.Split('\n', StringSplitOptions.RemoveEmptyEntries));
            int before = b.Value.Length;
            InputText(s, b, t);
            if (b.Value.Length - before < t.Length)
            {
                _toast = "Pasted text was cut to fit";
                _toastUntil = DateTime.UtcNow.AddMilliseconds(4000);
            }
            Redraw();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"RainwireMenu: scene input paste: {ex.Message}");   // the clipboard is busy: nothing pasted
        }
    }

    /// <summary>An input submitted: its value kept for history (P_HISTORY).</summary>
    private static void InputSubmitted(SBlock b)
    {
        int hist = b.U(PHistory);
        if (hist <= 0 || b.Value.Length == 0) return;
        b.History.Add(b.Value);
        if (b.History.Count > hist) b.History.RemoveAt(0);
        b.HistAt = -1;
    }

    // ================================================================
    // Timers, every frame: debounced changes, autosave, settled selection and view, hover asks
    // ================================================================

    private ushort _hoverAsked;
    private SBlock? _hoverBlock;
    private DateTime _hoverSince;

    private void SceneTimers(SceneTree s)
    {
        var now = DateTime.UtcNow;
        foreach (var b in s.Order)
        {
            if (b.Pass != s.Pass || b.Id == 0) continue;
            if (b.Kind == BkInput)
            {
                if (b.ChangePending && b.Has(PDebounce) && (now - b.ChangedAt).TotalMilliseconds >= b.U(PDebounce))
                {
                    b.ChangePending = false;
                    Reply($"Change;id={b.Id};b64={Convert.ToBase64String(Encoding.UTF8.GetBytes(b.Value))}");
                }
                if (b.Has(PAutosave) && b.ChangedAt > b.SavedAt && (now - b.SavedAt).TotalSeconds >= b.U(PAutosave))
                {
                    b.SavedAt = now;
                    Reply($"Change;id={b.Id};b64={Convert.ToBase64String(Encoding.UTF8.GetBytes(b.Value))}");
                }
            }
            else if (b.Kind == BkVList)
            {
                if (!b.SelSent && (now - b.SelAt).TotalMilliseconds >= (b.Has(PSettle) ? b.U(PSettle) : 0))
                {
                    b.SelSent = true;
                    Reply($"Select;id={b.Id};rows={RowList(b.Sel)}");
                }
                if (b.Flag(PSeen) && !b.SeenSent && (now - b.SeenAt).TotalMilliseconds >= 300 && b.RowY.Count > 0)
                {
                    b.SeenSent = true;
                    int first = Math.Max(b.AnchorRow, 0), last = first;
                    while (last + 1 < b.RowY.Count && b.RowY[last + 1] < b.Scroll + b.View) last++;
                    Reply($"Seen;id={b.Id};first={first};last={last};bottom={(b.AtEnd ? 1 : 0)}");
                }
            }
        }
        // hover asks (P_HOVERASK): the board fills the tooltip
        SBlock? hov = null;
        for (int i = s.Order.Count - 1; i >= 0 && hov == null; i--)
        {
            var b = s.Order[i];
            if (b.Pass == s.Pass && b.Id != 0 && b.Has(PHoverAsk) && IsHover(b)) hov = b;
        }
        if (hov != _hoverBlock)
        {
            _hoverBlock = hov;
            _hoverSince = now;
            if (hov == null) _hoverAsked = 0;
        }
        if (hov != null && _hoverAsked != hov.Id && (now - _hoverSince).TotalMilliseconds >= hov.U(PHoverAsk))
        {
            _hoverAsked = hov.Id;
            Reply($"Hover;id={hov.Id}");
        }
    }

    /// <summary>"3,5-9" for a set of rows.</summary>
    private static string RowList(HashSet<int> sel)
    {
        var l = sel.OrderBy(i => i).ToList();
        var sb = new StringBuilder();
        for (int i = 0; i < l.Count;)
        {
            int j = i;
            while (j + 1 < l.Count && l[j + 1] == l[j] + 1) j++;
            if (sb.Length > 0) sb.Append(',');
            sb.Append(l[i]);
            if (j > i) sb.Append('-').Append(l[j]);
            i = j + 1;
        }
        return sb.ToString();
    }

    // ================================================================
    // Pop-ups: menus, popovers and drawers (context, anchored, a button's, the board's)
    // ================================================================

    /// <summary>The pop-ups to show now: the board's anchored menus and popovers in the tree (unless closed by the
    /// caller), drawers open, and context or button menus the caller opened. Laid out over the scene.</summary>
    private void LayoutPops(SceneTree s, Rect space)
    {
        s.Pops.Clear();
        void Walk(SBlock b)
        {
            foreach (var k in b.Kids)
            {
                if (k.Flag(PHidden)) continue;
                if (IsPop(k.Kind))
                {
                    bool show = k.Kind == BkDrawer ? (k.Open || k.Flag(POn)) && !k.Closed
                              : k.Open || (k.Has(PAnchor) && !k.Flag(PContext) && !k.Closed);
                    if (show) s.Pops.Add(k);
                    continue;
                }
                Walk(k);
            }
            if (b.Rows != null)
                foreach (var row in b.Rows)
                    if (row != null) Walk(row);
        }
        Walk(s.Top);
        foreach (var m in s.Pops)
        {
            double u = SU;
            if (m.Kind == BkDrawer)
            {
                double w = Math.Min(Math.Max(SMeasure(m, space.Width * 0.8).Width, 16 * u), space.Width * 0.85);
                var dr = m.U(PSide) == SideRight ? R(space.Right - w, space.Y, w, space.Height) : R(space.X, space.Y, w, space.Height);
                SArrangePop(s, m, dr, space);
                continue;
            }
            var nat = SMeasure(m, Math.Min(space.Width * 0.6, 40 * u));
            double mw = Math.Max(nat.Width, m.Kind == BkMenu ? 10 * u : 0), mh = Math.Min(nat.Height, space.Height - u);
            Rect anchor = m.Has(PAnchor) && s.ById.TryGetValue((ushort)m.U(PAnchor), out var a) ? a.Box
                        : m.Open && m.OpenAt != default ? new Rect(m.OpenAt, new Size(1, 1))
                        : m.Parent?.Box ?? space;
            double x = anchor.X, y = anchor.Bottom + 0.2 * u;
            switch (m.U(PSide))
            {
                case SideAbove: y = anchor.Y - mh - 0.2 * u; break;
                case SideRight: x = anchor.Right + 0.2 * u; y = anchor.Y; break;
                case SideLeft: x = anchor.X - mw - 0.2 * u; y = anchor.Y; break;
            }
            if (y + mh > space.Bottom) y = anchor.Y - mh - 0.2 * u;   // no room below: above
            x = Math.Clamp(x, space.X, Math.Max(space.Right - mw, space.X));
            y = Math.Clamp(y, space.Y, Math.Max(space.Bottom - mh, space.Y));
            SArrangePop(s, m, R(x, y, mw, mh), space);
        }
    }

    private void SArrangePop(SceneTree s, SBlock m, Rect r, Rect space)
    {
        m.Pass = s.Pass;
        m.Box = r;
        m.Clip = space;
        m.Off = m.Flag(PDisabled);
        s.Order.Add(m);
        var inner = Inset(r, 0.4 * SU);
        m.Inner = inner;
        ArrangeFlex(s, m, inner, false, space);
    }

    private void DrawPops(DrawingContext dc, SceneTree s)
    {
        foreach (var m in s.Pops)
        {
            if (m.Pass != s.Pass) continue;
            double r = (m.Kind == BkDrawer ? 0 : 8) * _u;
            var bg = Mix(Scol(Sc.Panel), Colors.Black, 0.1);
            var pen = new Pen(B(Scol(Sc.Border)), 1 * _u);
            dc.DrawRectangle(B(Color.FromArgb(110, 0, 0, 0)), null, new Rect(m.Box.X + 3 * _u, m.Box.Y + 4 * _u, m.Box.Width, m.Box.Height), r, r);
            dc.DrawRectangle(B(bg), pen, m.Box, r, r);
            if (m.Kind == BkMenu)
                foreach (var k in m.Kids)
                    if (k.Kind == BkButton && k.Pass == s.Pass && (k.On || IsHover(k)))
                        dc.DrawRectangle(B(WithA(Scol(Sc.SelBg), 140)), null, k.Box, 5 * _u, 5 * _u);
            DrawKids(dc, s, m, m.Box);
        }
    }

    /// <summary>The pop-up under p, if any (the topmost).</summary>
    private static SBlock? PopAt(SceneTree s, Point p)
    {
        for (int i = s.Pops.Count - 1; i >= 0; i--)
            if (s.Pops[i].Box.Contains(p)) return s.Pops[i];
        return null;
    }

    /// <summary>Esc or a click outside: the topmost pop-up closes (true), before anything else.</summary>
    private bool PopClose(SceneTree s)
    {
        if (s.Pops.Count == 0) return false;
        var m = s.Pops[^1];
        m.Open = false;
        m.Closed = true;
        if (m.Flag(PReport) && m.Id != 0) Reply($"Back;id={m.Id}");
        s.Pops.RemoveAt(s.Pops.Count - 1);
        _sceneArr++;
        Redraw();
        return true;
    }

    /// <summary>A right-click (or long-press) at p: the context menu of the block under it (P_CONTEXT), else a
    /// Click;btn=r for a block that asks for it; true when used.</summary>
    private bool SceneRightClick(SceneTree s, Point p)
    {
        for (int i = s.Order.Count - 1; i >= 0; i--)
        {
            var b = s.Order[i];
            if (b.Pass != s.Pass || !b.Box.Contains(p) || !b.Clip.Contains(p) || HoverHidden(b)) continue;
            if (b.Kids.FirstOrDefault(k => k.Kind == BkMenu && k.Flag(PContext)) is SBlock menu)
            {
                foreach (var pop in s.Pops) pop.Open = false;
                menu.Open = true;
                menu.Closed = false;
                menu.OpenAt = p;
                _sceneArr++;
                Redraw();
                return true;
            }
            if ((b.U(PEvents) & EvRight) != 0 && b.Id != 0)
            {
                Reply($"Click;id={b.Id};n=1;btn=r" + ClickWhere(b, p));   // version 6: where
                return true;
            }
        }
        return false;
    }

    // ================================================================
    // Clicks for v2: pop-ups, chips, vlist rows, click events, menu buttons, drawers
    // ================================================================

    private ushort _clickId;
    private int _clickN;
    private DateTime _clickAt;

    /// <summary>A click before the v1 controls get it; true when used.</summary>
    private bool SceneClickV2(SceneTree s, Point p)
    {
        // a chip inside an input (its x, or the chip itself): a press
        foreach (var inp in s.Order)
            if (inp.Kind == BkInput && inp.Pass == s.Pass)
                foreach (var k in inp.Kids)
                    if (k.Kind == BkChip && k.Pass == s.Pass && k.Box.Contains(p) && k.Id != 0)
                    {
                        ScenePress(s, k);
                        return true;
                    }
        // a link span
        if (LinkAt(s, p) is (SBlock lb, string url))
        {
            Reply($"Link;id={lb.Id};url={Convert.ToBase64String(Encoding.UTF8.GetBytes(url))}");
            return true;
        }
        // a pop-up open: a click outside closes it (and does nothing else)
        if (s.Pops.Count > 0 && PopAt(s, p) == null)
        {
            PopClose(s);
            return true;
        }
        // a menu item in a pop-up: a completion picked, or a press (the menu closes)
        if (PopAt(s, p) is SBlock pop && pop.Kind == BkMenu)
        {
            var item = pop.Kids.FirstOrDefault(k => k.Kind == BkButton && k.Box.Contains(p) && !k.Off);
            if (item == null) return true;
            if (pop.U(PAnchor) is int aid and > 0 && s.ById.TryGetValue((ushort)aid, out var anchor) && anchor.Kind == BkInput)
                CompletePick(s, anchor, item);
            else
            {
                pop.Open = false;
                if (pop.Has(PAnchor)) pop.Closed = true;
                ScenePress(s, item);
            }
            return true;
        }
        for (int i = s.Order.Count - 1; i >= 0; i--)
        {
            var b = s.Order[i];
            if (b.Pass != s.Pass || b.Off || !b.Box.Contains(p) || !b.Clip.Contains(p) || HoverHidden(b)) continue;
            // a button that opens a menu or a drawer
            if (b.Kind == BkButton && b.Flag(PMenuBtn) && b.Kids.FirstOrDefault(k => k.Kind == BkMenu) is SBlock bm)
            {
                bm.Open = !bm.Open;
                bm.Closed = false;
                bm.OpenAt = default;
                if (!bm.Has(PAnchor)) bm.OpenAt = new Point(b.Box.X, b.Box.Bottom);
                _sceneArr++;
                return true;
            }
            if (b.Kind == BkButton && b.Has(POpens) && s.ById.TryGetValue((ushort)b.U(POpens), out var dr) && dr.Kind == BkDrawer)
            {
                dr.Open = !dr.Open;
                dr.Closed = false;
                _sceneArr++;
                return true;
            }
            // a sortable header (P_SORT): Sort;id=;col=
            if (b.Kind == BkButton && b.Has(PSort) && b.Id != 0)
            {
                Reply($"Sort;id={b.Id};col={b.U(PSort)}");
                return true;
            }
            // a chip: its x, or a press when it has an id
            if (b.Kind == BkChip && (b.Id != 0))
            {
                ScenePress(s, b);
                return true;
            }
            // a vlist: the "N new" chip, a row picked
            if (b.Kind == BkVList)
            {
                if (b.Hits.Any(h => h.I == -2 && h.Box.Contains(p)))
                {
                    b.Scroll = b.ScrollMax;
                    b.AtEnd = true;
                    b.NewCount = 0;
                    b.AnchorRow = -1;
                    _sceneArr++;
                    return true;
                }
                if (b.U(PSelect) > 0 && b.Rows != null)
                {
                    int row = -1;
                    for (int j = 0; j < b.Rows.Count; j++)
                        if (b.Rows[j] is SBlock rb && rb.Pass == s.Pass && rb.Box.Contains(p)) { row = j; break; }
                    if (row >= 0)
                    {
                        VSelectClick(s, b, row);
                        return true;
                    }
                }
            }
            // a plain block with click events
            if ((b.U(PEvents) & (EvClick | EvDouble | EvTriple)) != 0 && b.Id != 0)
            {
                var now = DateTime.UtcNow;
                _clickN = _clickId == b.Id && (now - _clickAt).TotalMilliseconds < 400 ? Math.Min(_clickN + 1, 3) : 1;
                _clickId = b.Id;
                _clickAt = now;
                int ev = b.U(PEvents);
                if ((_clickN == 1 && (ev & EvClick) != 0) || (_clickN == 2 && (ev & EvDouble) != 0) || (_clickN == 3 && (ev & EvTriple) != 0))
                {
                    var km = _clickMods;   // version 5: Ctrl and Shift held (the Files list's picks)
                    Reply($"Click;id={b.Id};n={_clickN};btn=l" + ((km & KeyModifiers.Control) != 0 ? ";ctrl=1" : "") +
                          ((km & KeyModifiers.Shift) != 0 ? ";shift=1" : "") + ClickWhere(b, p));   // version 6: where
                }
                return true;
            }
            if (IsControl(b.Kind)) return false;   // the v1 click has it
        }
        return false;
    }

    /// <summary>A row clicked in a selecting vlist: one, Ctrl toggles, Shift a range; the selected one again (or a
    /// double-click) activates.</summary>
    private void VSelectClick(SceneTree s, SBlock v, int row)
    {
        var mods = _clickMods;
        bool ctrl = (mods & KeyModifiers.Control) != 0, shift = (mods & KeyModifiers.Shift) != 0, many = v.U(PSelect) == 2;
        var now = DateTime.UtcNow;
        bool dbl = v.SelCur == row && (now - _clickAt).TotalMilliseconds < 400;
        _clickAt = now;
        if (v.Id != 0) s.Focus = v.Id;
        if (dbl || (!ctrl && !shift && v.Sel.Count == 1 && v.Sel.Contains(row) && !many))
        {
            if (v.Id != 0) Reply($"Activate;id={v.Id};i={row}");
            return;
        }
        if (many && ctrl)
        {
            if (!v.Sel.Remove(row)) v.Sel.Add(row);
            v.SelAnchor = row;
        }
        else if (many && shift && v.SelAnchor >= 0)
        {
            v.Sel.Clear();
            for (int i = Math.Min(v.SelAnchor, row); i <= Math.Max(v.SelAnchor, row); i++) v.Sel.Add(i);
        }
        else
        {
            v.Sel.Clear();
            v.Sel.Add(row);
            v.SelAnchor = row;
        }
        v.SelCur = row;
        v.SelAt = now;
        v.SelSent = false;
        Redraw();
    }

    /// <summary>The board's pick (P_INDEX set on a vlist): that row alone selected, not reported back as a Select.</summary>
    private static void VSetSel(SBlock v, int row)
    {
        v.Sel.Clear();
        v.Sel.Add(row);
        v.SelCur = row;
        v.SelAnchor = row;
        v.SelSent = true;
    }

    /// <summary>A focused selecting vlist's keys: arrows, Home/End, PgUp/PgDn, Shift ranges, Ctrl+A, Enter.</summary>
    private bool VListKey(SceneTree s, SBlock v, Key key, bool ctrl, bool shift)
    {
        int total = VTotal(v);
        if (total == 0) return false;
        int cur = Math.Clamp(v.SelCur, 0, total - 1), page = Math.Max((int)(v.View / Math.Max((v.Has(PRowH) ? v.F(PRowH) : 1.6) * SU, 1)) - 1, 1);
        int to = key switch
        {
            Key.Up => cur - 1, Key.Down => cur + 1, Key.Home => 0, Key.End => total - 1,
            Key.PageUp => cur - page, Key.PageDown => cur + page, _ => int.MinValue,
        };
        if (key == Key.A && ctrl && v.U(PSelect) == 2)
        {
            v.Sel.Clear();
            for (int i = 0; i < total; i++) v.Sel.Add(i);
            v.SelAt = DateTime.UtcNow;
            v.SelSent = false;
            return true;
        }
        if (key == Key.Return && v.Id != 0)
        {
            Reply($"Activate;id={v.Id};i={cur}");
            return true;
        }
        if (to == int.MinValue) return false;
        to = Math.Clamp(to, 0, total - 1);
        if (shift && v.U(PSelect) == 2)
        {
            if (v.SelAnchor < 0) v.SelAnchor = cur;
            v.Sel.Clear();
            for (int i = Math.Min(v.SelAnchor, to); i <= Math.Max(v.SelAnchor, to); i++) v.Sel.Add(i);
        }
        else
        {
            v.Sel.Clear();
            v.Sel.Add(to);
            v.SelAnchor = to;
        }
        v.SelCur = to;
        v.SelAt = DateTime.UtcNow;
        v.SelSent = false;
        if (to < v.RowY.Count)   // keep it in view
        {
            if (v.RowY[to] < v.Scroll) v.Scroll = v.RowY[to];
            else if (v.RowY[to] + v.RowH[to] > v.Scroll + v.View) v.Scroll = v.RowY[to] + v.RowH[to] - v.View;
            v.AnchorRow = -1;
            _sceneArr++;
        }
        return true;
    }

    /// <summary>A focused input's editing (after InputKey): paste (P_PASTE and the limits; Ctrl+V, and Shift+Insert as
    /// this port's fields take it), Enter submitting a multi-line input with P_SUBMIT (Shift+Enter a new line), then the
    /// field's own keys.</summary>
    private bool InputEdit(SceneTree s, SBlock b, Key key, bool ctrl, bool shift)
    {
        if (ctrl && key == Key.V || shift && key == Key.Insert)
        {
            InputPaste(s, b);
            return true;
        }
        if (key == Key.Return && b.Flag(PMulti))
        {
            if (b.Flag(PSubmit) && !shift)
            {
                SceneSubmit(s, b);
                return true;
            }
            if (!ctrl && InputText(s, b, "\n")) return true;
        }
        return FieldKey(s, b, key, ctrl, shift);
    }

    /// <summary>The link span under p (its block and address), if any.</summary>
    private (SBlock, string)? LinkAt(SceneTree s, Point p)
    {
        for (int i = s.Order.Count - 1; i >= 0; i--)
        {
            var b = s.Order[i];
            if (b.Kind != BkText || b.Pass != s.Pass || b.Links.Count == 0 || !b.Box.Contains(p) || !b.Clip.Contains(p)) continue;
            var ft = TextFt(b, b.Inner.Width);
            var origin = new Point(b.Inner.X, b.Inner.Y);
            foreach (var (st, len, url) in b.Links)
                if (len > 0 && ft.BuildHighlightGeometry(origin, st, len) is Geometry g && g.FillContains(p)) return (b, url);
        }
        return null;
    }

    /// <summary>Whether the pointer at p is over something a click does (a link, a chip, a vlist row to pick, a block
    /// with click events): the hand.</summary>
    private bool SceneHandAt(SceneTree s, Point p)
    {
        if (LinkAt(s, p) != null) return true;
        for (int i = s.Order.Count - 1; i >= 0; i--)
        {
            var b = s.Order[i];
            if (b.Pass != s.Pass || !b.Box.Contains(p) || !b.Clip.Contains(p) || HoverHidden(b)) continue;
            if ((b.Kind == BkChip && b.Id != 0) || (b.U(PEvents) & (EvClick | EvDouble)) != 0) return true;
        }
        return false;
    }

    /// <summary>A right-click on the menu (the surface): a scene's context menu or Click;btn=r.</summary>
    private void OnRightClick(Point p)
    {
        if (!IsUp || _dialog != null) return;
        _active = true;
        if (!_isScene && WidgetRightClick(p)) return;   // version 6: a gadget's own menu (RainwireGadgets.cs)
        if (_isScene && _scene is SceneTree s && _complete && SceneRightClick(s, p)) Redraw();
    }

    /// <summary>Links drawn underlined (always, as a web page's), after their text.</summary>
    private void DrawLinks(DrawingContext dc, SBlock b)
    {
        if (b.Links.Count == 0 || b.Ft == null) return;
        var origin = new Point(b.Inner.X, b.Inner.Y);
        foreach (var (st, len, _) in b.Links)
        {
            if (len <= 0 || b.Ft.BuildHighlightGeometry(origin, st, len) is not Geometry g) continue;
            bool hot = g.FillContains(_mouse);
            var r = g.Bounds;
            dc.DrawRectangle(B(hot ? Scol(Sc.Accent) : WithA(Scol(Sc.Accent), 170)), null, R(r.X, r.Bottom - 1.5 * _u, r.Width, Math.Max(_u, 1)));
        }
    }
}
