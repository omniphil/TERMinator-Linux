using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace TERMinator.Linux.Controls;

/// <summary>
/// Scenes version 5 (Rainwire docs/SCENES_V2.md, "Files as built"): what the Files scene needs beyond version 4, so it
/// looks and behaves as the Files TRACE module does.
///   - P_DROP: files dragged from the desktop over a drop target light it; dropped, they go the module's way
///     (RainwireWm/RainwireFileDrops.cs): one Rainwire:WM FileOffer each, with module=rwfiles and scene=&lt;block&gt;.
///   - P_DRAG: a row pressed and moved past 6 px sends DragOut;id=&lt;row&gt; (the module's FM_C_DRAG) and shows a label
///     by the pointer; the board answers with the window protocol's DragFile (scene=, label=, hint=). Let go: DragOut;id=0.
///   - P_FITW, P_SCALEMAX: a viewer's whole-number scale fitting its width only (ANSI art, from the top), and the
///     largest it takes. P_TILEMIN, P_TILERATIO: a grid's tiles as many a row as fit, sharing the row, their height
///     following their width (the gallery). P_SCROLLBY (a Set): a scroll, vlist or viewer scrolled by the board's keys;
///     P_PANE on a scroll or vlist leaves PgUp/PgDn/Home/End to the board.
/// Linux port of the Windows client's Controls/RainwireSceneFiles.cs (the master), 2026-10-22. One difference: Avalonia
/// 11's X11 backend has no XDnD drag source (RainwireWm/RainwireFileDrops.cs), so a row taken out of the window can't
/// become an OS drag. The label hides while the pointer is outside, and letting go anywhere sends DragOut;id=0, which
/// ends the board's drag as letting go inside does on Windows.
/// </summary>
public sealed partial class RainwireMenu
{
    private const byte PFitW = 153, PScaleMax = 154, PTileMin = 155, PTileRatio = 156, PScrollBy = 157;

    // ================================================================
    // Rows dragged out (P_DRAG)
    // ================================================================

    private SBlock? _dragRow;            // pressed on a P_DRAG row
    private Point _dragFrom;
    private bool _dragging;              // moved past 6 px: DragOut sent, the label shown
    private bool _dragOk, _dragLeft;     // the board allowed it (DragFile ok=1); the pointer left the window
    private string _dragLabel = "", _dragHint = "";
    /// <summary>The left button is held on the surface (Avalonia has no global Mouse.LeftButton; the surface's
    /// pointer events keep this, Windows reads the mouse instead).</summary>
    private bool _sceneLeftHeld;

    /// <summary>The scene row being dragged out (DragOut sent), or 0.</summary>
    public ushort SceneDragRow => _dragging && _dragRow != null ? _dragRow.Id : (ushort)0;

    /// <summary>The left button went down at p: a drag out may start from a P_DRAG row there.</summary>
    private void SceneButtonDown(Point p)
    {
        _sceneLeftHeld = true;
        _dragRow = null;
        _dragging = _dragOk = _dragLeft = false;
        var s = _scene;
        if (s == null || !_complete || !_isScene || s.Pops.Count > 0) return;
        for (int i = s.Order.Count - 1; i >= 0; i--)
        {
            var b = s.Order[i];
            if (b.Pass == s.Pass && b.Flag(PDrag) && b.Id != 0 && b.Box.Contains(p) && b.Clip.Contains(p))
            {
                _dragRow = b;
                _dragFrom = p;
                return;
            }
        }
    }

    /// <summary>The pointer moved with the button maybe down: true while a row is being dragged (nothing else
    /// happens under it). The press's implicit pointer capture keeps the moves coming outside the window.</summary>
    private bool SceneDragMove(Point p)
    {
        if (_dragRow == null) return false;
        if (!_sceneLeftHeld)
        {
            if (!_dragging) _dragRow = null;
            return _dragging;
        }
        if (!_dragging)
        {
            if (Math.Abs(p.X - _dragFrom.X) + Math.Abs(p.Y - _dragFrom.Y) <= 6 * _u) return false;
            _dragging = true;
            _dragLabel = RowLabel(_dragRow);
            _dragHint = "";
            Reply($"DragOut;id={_dragRow.Id}");
        }
        _dragLeft = PointerOutsideWindow(p);   // no OS drag to start here: the label just hides
        _surface.Cursor = Avalonia.Input.Cursor.Default;
        _surface.InvalidateVisual();
        return true;
    }

    /// <summary>The left button came up: true when it ended a drag (no click). Wherever it's let go: DragOut;id=0.</summary>
    private bool SceneButtonUp()
    {
        _sceneLeftHeld = false;
        if (!_dragging)
        {
            _dragRow = null;
            return false;
        }
        Reply("DragOut;id=0");
        SceneDragEnd();
        return true;
    }

    private void SceneDragEnd()
    {
        _dragRow = null;
        _dragging = _dragOk = _dragLeft = false;
        Redraw();
    }

    /// <summary>DragFile for scene row id: allowed (the label and hint to show by the pointer), or not (the board
    /// says why in the status bar; the drag ends).</summary>
    public void SceneDragAnswer(ushort id, bool ok, string label, string hint)
    {
        if (!_dragging || _dragRow == null || _dragRow.Id != id) return;
        if (!ok)
        {
            SceneDragEnd();
            return;
        }
        _dragOk = true;
        if (label.Length > 0) _dragLabel = label;
        _dragHint = hint;
        Redraw();
    }

    /// <summary>p (the surface's) is outside the TERMinator window's client area.</summary>
    private bool PointerOutsideWindow(Point p)
    {
        if (TopLevel.GetTopLevel(this) is not { } top || _surface.TranslatePoint(p, top) is not { } q) return false;
        var cs = top.ClientSize;
        return q.X < 0 || q.Y < 0 || q.X >= cs.Width || q.Y >= cs.Height;
    }

    /// <summary>A row's label until the board's comes: its longest text (the file's name).</summary>
    private static string RowLabel(SBlock row)
    {
        string best = "";
        void Walk(SBlock b)
        {
            if (best.Length == 0 && b.Kind == BkText && b.S(PText) is { Length: > 1 } t) best = t;
            foreach (var k in b.Kids) Walk(k);
        }
        Walk(row);
        return best;
    }

    /// <summary>The label beside the pointer (filesmod.c's draw_drag): the file, and the hint once the board allowed it.</summary>
    private void DrawSceneDrag(DrawingContext dc, double w, double h)
    {
        if (!_dragging || _dragLeft || _dragLabel.Length == 0) return;
        double u = _u;
        var label = Text(_dragLabel, UiBold, SSize, _dragOk ? Scol(Sc.ButtonFg) : Scol(Sc.Dim));
        label.MaxTextWidth = 360 * u;
        label.MaxLineCount = 1;
        label.Trimming = TextTrimming.CharacterEllipsis;
        FormattedText? hint = _dragHint.Length > 0 ? Text(_dragHint, Ui, SmallSize, Scol(Sc.Dim)) : null;
        double bw = Math.Max(label.Width, hint?.Width ?? 0) + 24 * u, bh = (hint != null ? 44 : 26) * u;
        double x = Math.Clamp(_mouse.X + 16 * u, 0, Math.Max(w - bw, 0)), y = Math.Clamp(_mouse.Y + 14 * u, 0, Math.Max(h - bh, 0));
        var bg = Scol(Sc.ButtonBg);
        dc.DrawRectangle(B(Color.FromArgb((byte)(_dragOk ? 235 : 150), bg.R, bg.G, bg.B)), null, R(x, y, bw, bh), 6 * u, 6 * u);
        dc.DrawText(label, new Point(x + 12 * u, y + 4 * u));
        if (hint != null) dc.DrawText(hint, new Point(x + 12 * u, y + 24 * u));
    }

    // ================================================================
    // Files dropped from the desktop (P_DROP)
    // ================================================================

    private ushort _dropLit;

    /// <summary>The P_DROP block under the pointer of an OS drag (its id), or 0: the scene showing and taking files.
    /// root and p are the window and the point in it (X11FileDropTarget's); at is p in the menu's coordinates.</summary>
    public ushort SceneDropTarget(Visual root, Point p, out Point at)
    {
        at = default;
        var s = _scene;
        if (!IsUp || s == null || !_complete || !_isScene || !IsEffectivelyVisible) return 0;
        if (root.TranslatePoint(p, _surface) is not { } sp) return 0;
        at = root.TranslatePoint(p, this) ?? sp;
        for (int i = s.Order.Count - 1; i >= 0; i--)
        {
            var b = s.Order[i];
            if (b.Pass == s.Pass && b.Flag(PDrop) && b.Id != 0 && b.Box.Contains(sp)) return b.Id;
        }
        return 0;
    }

    /// <summary>Lights drop target id while files are dragged over it (0: none).</summary>
    public void SceneDropHover(ushort id)
    {
        if (_dropLit == id) return;
        _dropLit = id;
        Redraw();
    }

    private void DrawSceneDrop(DrawingContext dc)
    {
        var s = _scene;
        if (_dropLit == 0 || s == null || !s.ById.TryGetValue(_dropLit, out var b) || b.Pass != s.Pass) return;
        var a = Scol(Sc.Accent);
        var pen = new Pen(B(Color.FromArgb(200, a.R, a.G, a.B)), 2 * _u);
        dc.DrawRectangle(null, pen, Inset(b.Box, 1.5 * _u), 6 * _u, 6 * _u);
    }

    // ================================================================
    // The board's scrolling, a grid's shared tiles
    // ================================================================

    /// <summary>A Set's P_SCROLLBY: b scrolled once by that many units (±1e6: to an end); the key isn't kept.</summary>
    private void SceneScrollBy(SceneTree s, SBlock b, byte[] v)
    {
        b.Props.Remove(PScrollBy);
        if (v.Length != 4) return;
        double by = BitConverter.ToSingle(v, 0);
        SetScroll(b, by > 1e5 ? b.ScrollMax : by < -1e5 ? 0 : b.Scroll + by * SU);
    }

    /// <summary>P_TILEMIN: as many tiles a row as fit (at least two), sharing it; each P_TILEH + its width x
    /// P_TILERATIO high. Null when b doesn't say.</summary>
    private (int Cols, double Tw, double Th)? GridShared(SBlock b, double w)
    {
        double min = Units(b, PTileMin);
        if (min <= 0 || double.IsInfinity(w) || b.U(PColumns) > 0) return null;
        double gap = Units(b, PGap);
        int cols = Math.Max(2, (int)((w + gap) / (min + gap)));
        double tw = Math.Max((w - gap * (cols - 1)) / cols, SU);
        return (cols, tw, Units(b, PTileH) + tw * Math.Max(b.F(PTileRatio), 0));
    }

    /// <summary>A viewer's whole-number scale (the module's logical pixels to a picture pixel, as its previews show
    /// them): P_SCALE, else the largest that fits (its width only with P_FITW), up to P_SCALEMAX (4); under 1 the
    /// picture is shrunk to fit, smoothly.</summary>
    private double ViewerScale(SBlock b, Rect r, double pw, double ph)
    {
        double sc = b.U(PScale);
        if (sc > 0) return sc;
        double dw = r.Width / _u, dh = r.Height / _u;
        sc = b.Flag(PFitW) ? dw / pw : Math.Min(dw / pw, dh / ph);
        int max = b.U(PScaleMax) > 0 ? b.U(PScaleMax) : 4;
        return sc >= 1 ? Math.Min(Math.Floor(sc), max) : sc;
    }
}
