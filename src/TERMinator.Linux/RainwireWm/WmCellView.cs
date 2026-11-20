using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering;

namespace TERMinator.Linux.RainwireWm;

/// <summary>Drawing cells with a bitmap font, shared by window contents and everything around them.</summary>
public static class WmPaint
{
    /// <summary>The Font smoothing setting: smoothed scaling when on, crisp aliased pixels when off, as the main screen.</summary>
    public static bool Smooth { get; set; }

    /// <summary>How a cell bitmap is scaled (Avalonia's equivalent of WPF's bitmap scaling + edge mode).</summary>
    public static BitmapInterpolationMode Interpolation =>
        Smooth ? BitmapInterpolationMode.HighQuality : BitmapInterpolationMode.None;

    public static void Apply(Visual e) => RenderOptions.SetBitmapInterpolationMode(e, Interpolation);

    /// <summary>One glyph into an ARGB pixel buffer pw pixels wide, at cell (cx, cy). A clear background stays clear.</summary>
    public static void Glyph(int[] px, int pw, int cx, int cy, byte ch, int fg, int bg, WmFont? font, int fh, bool underline = false)
    {
        int baseAt = cy * fh * pw + cx * 8;
        int glyph = ch * fh;
        for (int row = 0; row < fh; row++)
        {
            byte bits = font != null && glyph + row < font.Data.Length ? font.Data[glyph + row] : (byte)0;
            if (underline && row == fh - 1)
                bits = 0xFF;
            int at = baseAt + row * pw;
            for (int col = 0; col < 8; col++)
                px[at + col] = (bits & (0x80 >> col)) != 0 ? fg : bg;
        }
    }

    /// <summary>An ARGB pixel buffer into a bitmap (Avalonia's WritePixels), row by row at the bitmap's own stride.</summary>
    public static void Copy(int[] px, int pw, int ph, WriteableBitmap bitmap)
    {
        using var fb = bitmap.Lock();
        for (int y = 0; y < ph; y++)
            Marshal.Copy(px, y * pw, fb.Address + y * fb.RowBytes, pw);
    }

    /// <summary>A bitmap for ARGB cells: straight (not premultiplied) alpha, as WPF's Bgra32.</summary>
    public static WriteableBitmap NewBitmap(int pw, int ph) =>
        new(new PixelSize(pw, ph), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
}

/// <summary>
/// A grid of character cells drawn with the bitmap font and scaled to the main screen's cell size: the frames, the
/// taskbar, the start menu and the drag label are all drawn with it, in the board's colours and glyphs, so they look
/// exactly like the text inside the windows. A cell with a clear background (alpha 0) shows what's behind it and lets
/// the mouse through, unless its glyph has pixels (ICustomHitTest here, WPF's HitTestCore on Windows).
/// Ported from Windows' RainwireWm/WmCellView.cs 2026-09-30.
/// </summary>
public sealed class WmCellView : Control, ICustomHitTest
{
    public struct Cell
    {
        public byte Ch;
        public int Fg, Bg;
    }

    private readonly Func<WmFont?> _font;
    private Cell[] _cells = Array.Empty<Cell>();
    private WriteableBitmap? _bitmap;
    private int[] _pixels = Array.Empty<int>();
    private bool _dirty = true;

    public WmCellView(Func<WmFont?> font)
    {
        _font = font;
        WmPaint.Apply(this);
    }

    public int Cols { get; private set; }
    public int Rows { get; private set; }

    /// <summary>A new size in cells, all clear.</summary>
    public void Resize(int cols, int rows)
    {
        Cols = Math.Max(0, cols);
        Rows = Math.Max(0, rows);
        _cells = new Cell[Cols * Rows];
        Changed();
    }

    /// <summary>Everything clear (0) or filled with spaces in bg.</summary>
    public void Clear(int bg = 0)
    {
        for (int i = 0; i < _cells.Length; i++)
            _cells[i] = new Cell { Ch = (byte)' ', Bg = bg };
        Changed();
    }

    public void Put(int x, int y, byte ch, int fg, int bg)
    {
        if (x < 0 || y < 0 || x >= Cols || y >= Rows)
            return;
        _cells[y * Cols + x] = new Cell { Ch = ch, Fg = fg, Bg = bg };
        Changed();
    }

    /// <summary>Text from (x, y), clipped to the grid; returns the column after it.</summary>
    public int Text(int x, int y, ReadOnlySpan<byte> s, int fg, int bg)
    {
        foreach (byte b in s)
            Put(x++, y, b, fg, bg);
        return x;
    }

    public int Text(int x, int y, string s, int fg, int bg) => Text(x, y, WmTheme.Cp(s), fg, bg);

    public void Fill(int x, int y, int w, int h, int fg, int bg)
    {
        for (int yy = y; yy < y + h; yy++)
            for (int xx = x; xx < x + w; xx++)
                Put(xx, yy, (byte)' ', fg, bg);
    }

    /// <summary>The font or smoothing changed: draw again.</summary>
    public void Refresh()
    {
        WmPaint.Apply(this);
        Changed();
    }

    private void Changed()
    {
        _dirty = true;
        InvalidateVisual();
    }

    /// <summary>The cell under a point in this view.</summary>
    public (int X, int Y) CellAt(Point p)
    {
        double cw = Bounds.Width / Math.Max(1, Cols), ch = Bounds.Height / Math.Max(1, Rows);
        return (Math.Clamp((int)Math.Floor(p.X / Math.Max(cw, 1e-6)), 0, Math.Max(0, Cols - 1)),
                Math.Clamp((int)Math.Floor(p.Y / Math.Max(ch, 1e-6)), 0, Math.Max(0, Rows - 1)));
    }

    public override void Render(DrawingContext dc)
    {
        if (Cols == 0 || Rows == 0)
            return;
        var font = _font();
        int fh = font?.Height ?? 16;
        int pw = Cols * 8, ph = Rows * fh;
        if (_bitmap == null || _bitmap.PixelSize.Width != pw || _bitmap.PixelSize.Height != ph)
        {
            _bitmap?.Dispose();
            _bitmap = WmPaint.NewBitmap(pw, ph);
            _pixels = new int[pw * ph];
            _dirty = true;
        }
        if (_dirty)
        {
            for (int y = 0; y < Rows; y++)
                for (int x = 0; x < Cols; x++)
                {
                    var c = _cells[y * Cols + x];
                    WmPaint.Glyph(_pixels, pw, x, y, c.Ch, c.Fg, c.Bg, font, fh);
                }
            WmPaint.Copy(_pixels, pw, ph, _bitmap);
            _dirty = false;
        }
        using (dc.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = WmPaint.Interpolation }))
            dc.DrawImage(_bitmap, new Rect(0, 0, pw, ph), new Rect(Bounds.Size));
    }

    public bool HitTest(Point p)
    {
        if (Cols == 0 || Rows == 0 || p.X < 0 || p.Y < 0 || p.X >= Bounds.Width || p.Y >= Bounds.Height)
            return false;
        var (x, y) = CellAt(p);
        var c = _cells[y * Cols + x];
        return (c.Bg & unchecked((int)0xFF000000)) != 0 || c.Ch != ' ' && c.Ch != 0;
    }
}
