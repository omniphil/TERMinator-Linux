using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace TERMinator.Linux.RainwireWm;

/// <summary>
/// The graphical look for a board's windows (2026-10-05, sysop: "TERMinator can get the full graphical windows"): title
/// bars, buttons, borders, the taskbar and the start menu drawn with Avalonia shapes and fonts in the board's Theme
/// colours, instead of as cells of the bitmap font. Only the drawing changes. The cell views (WmCellView) stay where
/// they were, still taking every click at opacity 0 (Avalonia, like WPF, hit-tests a control at opacity 0; WmCellView's
/// ICustomHitTest decides), so the geometry, the hit testing and everything sent to the board are the same in both
/// looks. Off (Options: the board's text look), the cell views show as before.
/// Ported from Windows' RainwireWm/WmChrome.cs 2026-10-12 (FrameworkElement.OnRender is Control.Render, frozen brushes
/// are plain ones, WPF's gradient angle is a Start/EndPoint pair, PushClip/Pop is a using; Segoe UI is the app's UI
/// font, Inter, bundled through WithInterFont).
/// </summary>
public static class WmLook
{
    /// <summary>The setting: the graphical look (true) or the board's own cells (false).</summary>
    public static bool Graphical { get; set; } = true;

    public static readonly FontFamily Ui = FontFamily.Default;
    public static readonly Typeface Title = new(Ui, FontStyle.Normal, FontWeight.SemiBold, FontStretch.Normal);
    public static readonly Typeface Label = new(Ui, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal);
    public static readonly Typeface Bold = new(Ui, FontStyle.Normal, FontWeight.Bold, FontStretch.Normal);

    public static Color C(int argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    public static Color C(int argb, double alpha) => Color.FromArgb((byte)Math.Clamp(alpha * 255, 0, 255), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    public static SolidColorBrush Solid(int argb) => new(C(argb));

    /// <summary>A two-colour fade: left to right (WPF angle 0), or top to bottom (vertical; WPF angle 90).</summary>
    private static LinearGradientBrush Gradient(Color a, Color b, bool vertical = false) => new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = vertical ? new RelativePoint(0, 1, RelativeUnit.Relative) : new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops = { new GradientStop(a, 0), new GradientStop(b, 1) },
    };

    /// <summary>Left to right from a to b; flat a when there's no b.</summary>
    public static IBrush Fade(int a, int? b) =>
        b is int to && to != a ? Gradient(C(a), C(to)) : Solid(a);

    /// <summary>
    /// The board's logo colours: its start fade (Rainwire's blue to purple), else its focus fade. The graphical look
    /// uses them as see-through tints over the dark panels rather than solid fills (sysop 2026-10-05: the first colours
    /// were "a bit harsh"; "theme colored to the rainwire logo colors or just softer").
    /// </summary>
    public static (int A, int B) Accent(WmTheme th)
    {
        int a = th.StartFrom ?? th.FocusBg;
        return (a, th.StartTo ?? th.FocusBg2 ?? a);
    }

    /// <summary>The logo fade, left to right, at alpha.</summary>
    public static IBrush Tint(WmTheme th, double alpha)
    {
        var (a, b) = Accent(th);
        return Gradient(C(a, alpha), C(b, alpha));
    }

    /// <summary>A colour at alpha.</summary>
    public static SolidColorBrush Soft(int argb, double alpha) => new(C(argb, alpha));

    /// <summary>A colour taken k of the way to white: text that reads on dark without glaring.</summary>
    public static SolidColorBrush Light(int argb, double k)
    {
        var c = C(argb);
        return new SolidColorBrush(Color.FromRgb(Lift(c.R, k), Lift(c.G, k), Lift(c.B, k)));
    }

    /// <summary>Top to bottom, a little lighter at the top: depth on a flat colour.</summary>
    public static IBrush Sheen(int argb, double lift = 0.10)
    {
        var c = C(argb);
        var top = Color.FromRgb(Lift(c.R, lift), Lift(c.G, lift), Lift(c.B, lift));
        return Gradient(top, c, vertical: true);
    }

    private static byte Lift(byte v, double by) => (byte)Math.Clamp(v + (255 - v) * by, 0, 255);

    /// <summary>One line of text, cut with an ellipsis to fit maxW.</summary>
    public static FormattedText Text(string s, Typeface face, double size, IBrush fg, double maxW)
    {
        var ft = new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, Math.Max(size, 1), fg)
        {
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
            MaxTextWidth = Math.Max(1, maxW),
        };
        return ft;
    }

    /// <summary>A text size that suits a cell height.</summary>
    public static double Size(double cellH, double k = 0.62) => Math.Clamp(cellH * k, 8, 40);
}

internal static class WmBrushExt
{
    /// <summary>A 1-pixel pen in this brush.</summary>
    public static Pen ToPen(this IBrush b, double thickness = 1) => new(b, thickness);
}

/// <summary>
/// One window's frame in the graphical look: rounded corners, a soft shadow, the title bar (the board's fade when it
/// sends one), the title in the UI font, and minimize, maximize and close drawn as icons that light up under the
/// pointer. It covers exactly the cells the board's frame does (the title row, a column each side, the bottom row), so
/// the window content sits where it always did.
/// </summary>
public sealed class WmFrameChrome : Control
{
    private readonly Func<WmTheme> _theme;

    public bool Focused, Max;
    public string Title = "";
    /// <summary>The title button under the pointer (0 minimize, 1 maximize, 2 close), or -1.</summary>
    public int Hover = -1;
    public int Cols, Rows;   // the frame's outside size in cells

    public WmFrameChrome(Func<WmTheme> theme)
    {
        _theme = theme;
        IsHitTestVisible = false;
        // WPF's ShadowDepth 3 at Direction 270 is 3 pixels straight down
        Effect = new DropShadowEffect { BlurRadius = 16, OffsetX = 0, OffsetY = 3, Opacity = 0.55, Color = Colors.Black };
    }

    public override void Render(DrawingContext dc)
    {
        if (Cols < 3 || Rows < 2 || Bounds.Width < 1)
            return;
        var th = _theme();
        double w = Bounds.Width, h = Bounds.Height;
        double cw = w / Cols, ch = h / Rows;
        double r = Max ? 0 : Math.Min(8, ch * 0.45);
        var outer = new Rect(0.5, 0.5, w - 1, h - 1);

        // The body: the window's own background shows in the side and bottom margins
        dc.DrawRectangle(WmLook.Solid(th.PopBg), null, outer, r, r);

        // The title bar, clipped to the top corners
        var bar = new Rect(outer.X, outer.Y, outer.Width, ch);
        double bx = w - 9 * cw, bw = 3 * cw;
        int tfg = Focused ? th.FocusFg : th.TitleFg;
        using (dc.PushClip(new RoundedRect(outer, r)))
        {
            dc.DrawRectangle(WmLook.Sheen(th.TitleBg, 0.05), null, bar);
            if (Focused)
                dc.DrawRectangle(WmLook.Tint(th, 0.42), null, bar);   // the logo fade, softened over the bar
            // a hairline of light along its top edge, and a darker one under it
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(Focused ? (byte)60 : (byte)28, 255, 255, 255)), null, new Rect(bar.X, bar.Y, bar.Width, 1));
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), null, new Rect(bar.X, bar.Bottom - 1, bar.Width, 1));

            // The buttons: three cells each from the right, as the board's frame lays them out
            for (int i = 0; i < 3; i++)
            {
                var b = new Rect(bx + i * bw, bar.Y, bw, ch);
                if (Hover == i)
                {
                    var hl = i == 2 ? new SolidColorBrush(Color.FromRgb(0xE8, 0x11, 0x23)) : new SolidColorBrush(Color.FromArgb(48, 255, 255, 255));
                    dc.DrawRectangle(hl, null, b);
                }
                DrawButton(dc, i, b, WmLook.C(Hover == 2 && i == 2 ? unchecked((int)0xFFFFFFFF) : tfg), ch);
            }

            // The title
            var titleText = WmLook.Text(Title.Length > 0 ? Title : "Rainwire", WmLook.Title, WmLook.Size(ch),
                WmLook.Solid(tfg), Math.Max(1, bx - cw * 1.2));
            dc.DrawText(titleText, new Point(cw * 0.9, bar.Y + (ch - titleText.Height) / 2));
        }

        // The border: the board's frame colour, brighter when focused
        var pen = new Pen(Focused ? WmLook.Soft(WmLook.Accent(th).A, 0.45) : WmLook.Soft(th.Frame, 0.35), 1);
        dc.DrawRectangle(null, pen, outer, r, r);

        // The resize grip: three dots in the bottom-right corner
        if (!Max)
        {
            var dot = new SolidColorBrush(WmLook.C(th.Frame, 0.9));
            double s = Math.Max(1.5, ch / 10), gx = w - cw * 0.45, gy = h - ch * 0.4;
            for (int i = 0; i < 3; i++)
                for (int j = 0; j <= i; j++)
                    dc.DrawEllipse(dot, null, new Point(gx - (i - j) * s * 2.2, gy - j * s * 2.2), s * 0.55, s * 0.55);
        }
    }

    private void DrawButton(DrawingContext dc, int which, Rect b, Color fg, double ch)
    {
        var pen = new Pen(new SolidColorBrush(fg), Math.Max(1, Math.Round(ch / 14)));
        double s = Math.Min(b.Width, b.Height) * 0.34;
        var c = new Point(b.X + b.Width / 2, b.Y + b.Height / 2);
        switch (which)
        {
            case 0:   // minimize: a bar
                dc.DrawLine(pen, new Point(c.X - s / 2, c.Y + s / 3), new Point(c.X + s / 2, c.Y + s / 3));
                break;
            case 1 when Max:   // restore: two squares
                dc.DrawRectangle(null, pen, new Rect(c.X - s / 2, c.Y - s / 2 + s * 0.25, s * 0.75, s * 0.75));
                dc.DrawLine(pen, new Point(c.X - s / 2 + s * 0.25, c.Y - s / 2 + s * 0.25), new Point(c.X - s / 2 + s * 0.25, c.Y - s / 2));
                dc.DrawLine(pen, new Point(c.X - s / 2 + s * 0.25, c.Y - s / 2), new Point(c.X + s / 2, c.Y - s / 2));
                dc.DrawLine(pen, new Point(c.X + s / 2, c.Y - s / 2), new Point(c.X + s / 2, c.Y + s / 2 - s * 0.25));
                break;
            case 1:   // maximize: a square
                dc.DrawRectangle(null, pen, new Rect(c.X - s / 2, c.Y - s / 2, s, s));
                break;
            default:  // close: a cross
                dc.DrawLine(pen, new Point(c.X - s / 2, c.Y - s / 2), new Point(c.X + s / 2, c.Y + s / 2));
                dc.DrawLine(pen, new Point(c.X + s / 2, c.Y - s / 2), new Point(c.X - s / 2, c.Y + s / 2));
                break;
        }
    }
}

/// <summary>While a window is resized: a see-through box in the board's outline colour, with the new size in it.</summary>
public sealed class WmOutlineChrome : Control
{
    private readonly Func<WmTheme> _theme;
    public int Cols, Rows;   // the content's new size, shown in the middle

    public WmOutlineChrome(Func<WmTheme> theme)
    {
        _theme = theme;
        IsHitTestVisible = false;
    }

    public override void Render(DrawingContext dc)
    {
        var th = _theme();
        var rect = new Rect(1, 1, Math.Max(1, Bounds.Width - 2), Math.Max(1, Bounds.Height - 2));
        dc.DrawRectangle(new SolidColorBrush(WmLook.C(th.Outline, 0.12)), new Pen(new SolidColorBrush(WmLook.C(th.Outline, 0.95)), 2), rect, 6, 6);
        var t = WmLook.Text($"{Cols} × {Rows}", WmLook.Bold, 15, WmLook.Solid(th.Outline), rect.Width);
        var at = new Point(rect.X + (rect.Width - t.Width) / 2, rect.Y + (rect.Height - t.Height) / 2);
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)), null,
            new Rect(at.X - 10, at.Y - 4, t.Width + 20, t.Height + 8), 6, 6);
        dc.DrawText(t, at);
    }
}

/// <summary>
/// The taskbar in the graphical look: the board's bar colour with some depth, the start button as a pill in the board's
/// start fade, and a rounded button per window (the focused one in the board's fade, a minimized one dimmed). The spans
/// are the cell ranges the desk layer hit-tests, so a click lands where it's drawn.
/// </summary>
public sealed class WmBarChrome : Control
{
    private readonly Func<WmTheme> _theme;

    public record Task(int From, int To, string Label, bool Focused, bool Minimized);

    public int Cols;
    public (int From, int To) Start;
    public bool MenuOpen, StartHover;
    public List<Task> Tasks = new();

    public WmBarChrome(Func<WmTheme> theme)
    {
        _theme = theme;
        IsHitTestVisible = false;
    }

    public override void Render(DrawingContext dc)
    {
        if (Cols < 1 || Bounds.Width < 1)
            return;
        var th = _theme();
        double cw = Bounds.Width / Cols, h = Bounds.Height;
        dc.DrawRectangle(WmLook.Sheen(th.BarBg, 0.08), null, new Rect(0, 0, Bounds.Width, h));
        dc.DrawRectangle(WmLook.Tint(th, 0.25), null, new Rect(0, 0, Bounds.Width, 1));

        double pad = Math.Max(1.5, h * 0.12), r = (h - 2 * pad) / 2;
        // The start button
        var sb = new Rect(Start.From * cw + pad, pad, Math.Max(0, (Start.To - Start.From) * cw - 2 * pad), Math.Max(0, h - 2 * pad));
        if (sb.Width > 2)
        {
            // Just the menu mark and an R (sysop 2026-10-05), in the logo fade
            dc.DrawRectangle(WmLook.Tint(th, MenuOpen ? 0.75 : StartHover ? 0.62 : 0.5), WmLook.Tint(th, 0.6).ToPen(), sb, r, r);
            var t = WmLook.Text("≡ R", WmLook.Bold, WmLook.Size(h, 0.6), WmLook.Light(WmLook.Accent(th).A, 0.8), sb.Width);
            dc.DrawText(t, new Point(sb.X + (sb.Width - t.Width) / 2, sb.Y + (sb.Height - t.Height) / 2));
        }
        // A button per window
        foreach (var task in Tasks)
        {
            double bwid = (task.To - task.From) * cw - 2 * pad;
            if (bwid < 4)
                continue;
            var b = new Rect(task.From * cw + pad, pad, bwid, Math.Max(0, h - 2 * pad));
            if (task.Focused)
                dc.DrawRectangle(WmLook.Tint(th, 0.36), WmLook.Tint(th, 0.5).ToPen(), b, 4, 4);
            else
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(task.Minimized ? (byte)8 : (byte)18, 255, 255, 255)), null, b, 4, 4);
            int fg = task.Focused ? th.TaskFg : task.Minimized ? th.DimFg : th.BarFg;
            var t = WmLook.Text(task.Label, task.Focused ? WmLook.Title : WmLook.Label, WmLook.Size(h, 0.54), WmLook.Solid(fg), b.Width - 12);
            dc.DrawText(t, new Point(b.X + 6, b.Y + (b.Height - t.Height) / 2));
        }
    }
}

/// <summary>
/// The start menu in the graphical look: a rounded panel in the board's popup colours, its title, and one row per item
/// with the hotkey in a little badge; the selected row in the board's fade. Rows are the cell rows the desk layer
/// hit-tests.
/// </summary>
public sealed class WmMenuChrome : Control
{
    private readonly Func<WmTheme> _theme;

    public int Cols, Rows, Selected = -1;
    public List<(string Key, string Label)> Items = new();

    public WmMenuChrome(Func<WmTheme> theme)
    {
        _theme = theme;
        IsHitTestVisible = false;
        Effect = new DropShadowEffect { BlurRadius = 18, OffsetX = 0, OffsetY = 3, Opacity = 0.6, Color = Colors.Black };
    }

    public override void Render(DrawingContext dc)
    {
        if (Cols < 3 || Rows < 3 || Bounds.Width < 1)
            return;
        var th = _theme();
        double cw = Bounds.Width / Cols, ch = Bounds.Height / Rows;
        var outer = new Rect(0.5, 0.5, Bounds.Width - 1, Bounds.Height - 1);
        var (accent, _) = WmLook.Accent(th);
        dc.DrawRectangle(WmLook.Sheen(th.PopBg, 0.05), new Pen(WmLook.Soft(accent, 0.35), 1), outer, 8, 8);
        var title = WmLook.Text(th.MenuLabel, WmLook.Bold, WmLook.Size(ch, 0.6), WmLook.Light(accent, 0.35), Bounds.Width - 2 * cw);
        dc.DrawText(title, new Point(cw * 1.2, (ch - title.Height) / 2));
        dc.DrawRectangle(WmLook.Tint(th, 0.18), null, new Rect(cw, ch - 1, Math.Max(0, Bounds.Width - 2 * cw), 1));

        for (int i = 0; i < Items.Count && i < Rows - 2; i++)
        {
            var row = new Rect(cw * 0.5, (i + 1) * ch, Bounds.Width - cw, ch);
            bool sel = i == Selected;
            if (sel)
                dc.DrawRectangle(WmLook.Tint(th, 0.32), WmLook.Tint(th, 0.5).ToPen(), row, 5, 5);
            var key = WmLook.Text(Items[i].Key, WmLook.Bold, WmLook.Size(ch, 0.55), WmLook.Light(accent, 0.5), cw * 2);
            var badge = new Rect(row.X + cw * 0.5, row.Y + ch * 0.14, Math.Max(cw * 1.6, key.Width + 8), ch * 0.72);
            dc.DrawRectangle(WmLook.Soft(accent, sel ? 0.28 : 0.14), null, badge, 4, 4);
            dc.DrawText(key, new Point(badge.X + (badge.Width - key.Width) / 2, badge.Y + (badge.Height - key.Height) / 2));
            var label = WmLook.Text(Items[i].Label, sel ? WmLook.Title : WmLook.Label, WmLook.Size(ch, 0.58),
                sel ? WmLook.Solid(th.FocusFg) : WmLook.Soft(th.PopFg, 0.88), row.Right - badge.Right - cw);
            dc.DrawText(label, new Point(badge.Right + cw * 0.8, row.Y + (ch - label.Height) / 2));
        }
    }
}
