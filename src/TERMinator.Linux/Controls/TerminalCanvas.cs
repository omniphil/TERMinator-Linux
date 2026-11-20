using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using SkiaSharp;
using TERMinator.Linux.Effects;
using TERMinator.Linux.Models;
using TERMinator.Linux.Services;

namespace TERMinator.Linux.Controls;

/// <summary>
/// Custom Control that renders the terminal screen using WriteableBitmap.
/// Uses pre-rendered glyph cache for fast blitting. 8x16 font, crisp pixel rendering.
/// </summary>
public class TerminalCanvas : Control
{
    private const int FontWidth = 8;
    private const int FontHeight = 16;
    private const int DefaultCols = 80;
    private const int DefaultRows = 25;

    private WriteableBitmap? _bitmap;
    private int _cols = DefaultCols;
    private int _rows = DefaultRows;

    // Font bitmap data: 256 chars * FontHeight rows, 1 byte per row (8 pixels)
    private byte[]? _fontData;
    private int _fontDataHeight = FontHeight;

    // Glyph cache: key = ch | fgRGB | bgRGB | transparent, value = pre-rendered pixels.
    // Keyed on the actual 24-bit COLOURS rather than 4-bit palette indices, because with true
    // colour a cell's colour is no longer an index -- so the old int key (ch<<16|fg<<8|bg) could
    // not represent it. That makes the key space unbounded, hence GlyphCacheLimit below.
    private readonly Dictionary<long, int[]> _glyphCache = new();

    // A gradient-heavy true-colour screen can mint a new glyph per cell, so cap the cache and
    // drop it wholesale when it gets silly. Each entry is FontWidth*height ints (~512 bytes),
    // so this bounds it at roughly 1 MB.
    private const int GlyphCacheLimit = 2048;

    // Current palette (16 ARGB colors) -- still used for the legacy path and the cursor
    private readonly int[] _paletteArgb = new int[16];

    // Previous screen state for dirty optimization
    private int[]? _prevScreen;
    // ...and the colours that went with it: a cell can keep identical packed bits while its
    // true colour changes, so the dirty check has to compare colour as well or the screen
    // silently keeps the old shade.
    private int[]? _prevFg;
    private int[]? _prevBg;

    // Last received screen buffer (kept for re-render after font change)
    private int[]? _lastScreenBuffer;
    private int[]? _lastFg;
    private int[]? _lastBg;

    // Persistent pixel buffer (reused across frames to avoid black flicker)
    private int[]? _pixels;

    // Sixel overlay
    private WriteableBitmap? _sixelBitmap;
    private int _sixelWidth;
    private int _sixelHeight;
    private bool _hasSixelData;

    // Background underlay region (TAP+ bgshow/bgset/bghide)
    // Image display handled by AXAML element in TerminalView; canvas only tracks region for transparent cells
    private bool _bgActive;
    private int _bgRow1 = 1, _bgCol1 = 1, _bgRow2 = 25, _bgCol2 = 80; // 1-based inclusive

    // Tile atlas system (TAP+ tileset/tilemap/tile)
    private Bitmap? _tileAtlasBitmap;
    private int _tileAtlasWidth, _tileAtlasHeight;
    private int _tileSpriteW, _tileSpriteH;
    private int _tileAtlasCols;
    private bool _tileActive;
    private double _tileOpacity = 1.0;
    private int _tileRow1, _tileCol1, _tileRow2, _tileCol2; // 1-based inclusive
    private int[,]? _tileGrid; // [row, col] within tile region, -1 = empty
    private RenderTargetBitmap? _tileBitmap;
    private bool _tileBitmapDirty;
    private bool _tileBatchMode;
    private int _tileScale = 1; // 1=8x16, 2=16x32, 4=32x64 pixels per cell

    // Cursor state
    private const int CursorTypeNone = 0;
    private const int CursorTypeSolid = 1;
    private const int CursorTypeNormal = 2;
    private int _cursorX, _cursorY;
    private bool _cursorVisible;
    private int _cursorType = CursorTypeNormal;
    private bool _cursorBlinks = true;  // false = steady cursor (always visible)
    private int _cursorStart = 14;      // Top scanline of cursor within cell
    private int _cursorEnd = 15;        // Bottom scanline of cursor within cell
    private int _ciolibFg = 0xAAAAAA;   // Resolved cursor fg color as 0x00RRGGBB
    private bool _cursorBlinkOn = true;
    private bool _textBlinkOn = true;   // Separate text blink phase (266ms, every 2nd cursor tick)
    private int _blinkTickCount;        // Counts 133ms ticks for text blink timing
    private DispatcherTimer? _cursorTimer;

    // Text blink state (SGR 5/6 -- ANSI blink attribute)
    private bool _hasBlinkingCells;

    // Scrollback
    private int _scrollOffset; // 0 = live view, >0 = lines scrolled back

    // Selection state
    private bool _isSelecting;
    private int _selAnchorCol = -1, _selAnchorRow = -1;
    private int _selEndCol = -1, _selEndRow = -1;
    private int _selMinCol = -1, _selMinRow = -1, _selMaxCol = -1, _selMaxRow = -1;
    private bool _isDragging; // true while mouse button is held during selection

    // Selection visual brushes
    private static readonly IBrush SelectionFillBrush = new SolidColorBrush(Color.FromArgb(0x60, 0x33, 0xAA, 0xFF));
    private static readonly IPen SelectionBorderPen = new Pen(new SolidColorBrush(Color.FromArgb(0xFF, 0x33, 0xAA, 0xFF)), 2);

    // URL detection for click-to-open and underline rendering
    private static readonly Regex UrlPattern = new(
        @"(https?://|ftp://|www\.)[^\s<>\[\](){}'""`,;]+[^\s<>\[\](){}'""`,;.!?]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private record struct UrlRegion(int StartRow, int StartCol, int EndRow, int EndCol);
    private List<UrlRegion> _detectedUrls = new();
    /// <summary>Cells drawn inside a board's OSC 8 link (12.62), live screen only; null when there are none.</summary>
    private bool[]? _oscLinkMask;

    /// <summary>A click on a board's OSC 8 link: the window opens it (12.62).</summary>
    public event Action<string>? OscLinkActivated;

    // Bitmap scaling mode
    private bool _fontSmoothing;

    // CP437 to Unicode lookup table
    private static readonly char[] Cp437ToUnicode =
    {
        // 0x00-0x0F
        ' ',      '\u263A', '\u263B', '\u2665', '\u2666', '\u2663', '\u2660', '\u2022',
        '\u25D8', '\u25CB', '\u25D9', '\u2642', '\u2640', '\u266A', '\u266B', '\u263C',
        // 0x10-0x1F
        '\u25BA', '\u25C4', '\u2195', '\u203C', '\u00B6', '\u00A7', '\u25AC', '\u21A8',
        '\u2191', '\u2193', '\u2192', '\u2190', '\u221F', '\u2194', '\u25B2', '\u25BC',
        // 0x20-0x7E: Standard ASCII
        ' ', '!', '"', '#', '$', '%', '&', '\'', '(', ')', '*', '+', ',', '-', '.', '/',
        '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', ':', ';', '<', '=', '>', '?',
        '@', 'A', 'B', 'C', 'D', 'E', 'F', 'G', 'H', 'I', 'J', 'K', 'L', 'M', 'N', 'O',
        'P', 'Q', 'R', 'S', 'T', 'U', 'V', 'W', 'X', 'Y', 'Z', '[', '\\', ']', '^', '_',
        '`', 'a', 'b', 'c', 'd', 'e', 'f', 'g', 'h', 'i', 'j', 'k', 'l', 'm', 'n', 'o',
        'p', 'q', 'r', 's', 't', 'u', 'v', 'w', 'x', 'y', 'z', '{', '|', '}', '~', '\u2302',
        // 0x80-0x8F
        '\u00C7', '\u00FC', '\u00E9', '\u00E2', '\u00E4', '\u00E0', '\u00E5', '\u00E7',
        '\u00EA', '\u00EB', '\u00E8', '\u00EF', '\u00EE', '\u00EC', '\u00C4', '\u00C5',
        // 0x90-0x9F
        '\u00C9', '\u00E6', '\u00C6', '\u00F4', '\u00F6', '\u00F2', '\u00FB', '\u00F9',
        '\u00FF', '\u00D6', '\u00DC', '\u00A2', '\u00A3', '\u00A5', '\u20A7', '\u0192',
        // 0xA0-0xAF
        '\u00E1', '\u00ED', '\u00F3', '\u00FA', '\u00F1', '\u00D1', '\u00AA', '\u00BA',
        '\u00BF', '\u2310', '\u00AC', '\u00BD', '\u00BC', '\u00A1', '\u00AB', '\u00BB',
        // 0xB0-0xBF
        '\u2591', '\u2592', '\u2593', '\u2502', '\u2524', '\u2561', '\u2562', '\u2556',
        '\u2555', '\u2563', '\u2551', '\u2557', '\u255D', '\u255C', '\u255B', '\u2510',
        // 0xC0-0xCF
        '\u2514', '\u2534', '\u252C', '\u251C', '\u2500', '\u253C', '\u255E', '\u255F',
        '\u255A', '\u2554', '\u2569', '\u2566', '\u2560', '\u2550', '\u256C', '\u2567',
        // 0xD0-0xDF
        '\u2568', '\u2564', '\u2565', '\u2559', '\u2558', '\u2552', '\u2553', '\u256B',
        '\u256A', '\u2518', '\u250C', '\u2588', '\u2584', '\u258C', '\u2590', '\u2580',
        // 0xE0-0xEF
        '\u03B1', '\u00DF', '\u0393', '\u03C0', '\u03A3', '\u03C3', '\u00B5', '\u03C4',
        '\u03A6', '\u0398', '\u03A9', '\u03B4', '\u221E', '\u03C6', '\u03B5', '\u2229',
        // 0xF0-0xFF
        '\u2261', '\u00B1', '\u2265', '\u2264', '\u2320', '\u2321', '\u00F7', '\u2248',
        '\u00B0', '\u2219', '\u00B7', '\u221A', '\u207F', '\u00B2', '\u25A0', '\u00A0'
    };

    /// <summary>
    /// Get the screen-space rectangle where the BBS terminal content is rendered.
    /// Used for positioning overlays (images, video) relative to the terminal area.
    /// </summary>
    public Rect GetBbsScreenRect()
    {
        if (_bitmap == null) return new Rect(0, 0, Bounds.Width, Bounds.Height);
        var (offsetX, renderW, renderH, _, _) = ComputeRenderRect();
        return new Rect(offsetX, 0, renderW, renderH);
    }

    /// <summary>
    /// Forced display aspect (width/height) for the terminal picture, or null to show the
    /// bitmap at its natural pixel aspect. Every mode the app offers sets one, because no text
    /// mode had square pixels: 80x25 is a 720x400 grid that filled a 4:3 monitor, and the
    /// 40-column Commodore modes render 320x200 and are shown at 6:5 like the real machine.
    /// Leaving it null renders the pixel grid's own shape, which is wider and squatter than
    /// the art was drawn for.
    /// </summary>
    public double? DisplayAspect { get; set; }

    /// <summary>
    /// When set, keyboard input is translated to PETSCII (Commodore) codes instead of
    /// ANSI escape sequences: swapped letter case, C64 cursor/colour/function keys.
    /// </summary>
    public bool PetsciiMode { get; set; }

    /// <summary>Effective aspect of the rendered picture (width / height).</summary>
    private double EffectiveAspect(double bitmapW, double bitmapH)
    {
        if (DisplayAspect is double forced && forced > 0) return forced;
        return bitmapH > 0 ? bitmapW / bitmapH : 1.6;
    }

    /// <summary>
    /// Where the terminal bitmap lands inside the control: horizontally centred, scaled to
    /// fit while keeping the effective aspect. Returns per-axis scales because a forced
    /// aspect makes them differ.
    /// </summary>
    private (double offsetX, double renderW, double renderH, double scaleX, double scaleY) ComputeRenderRect()
    {
        if (_bitmap == null) return (0, Bounds.Width, Bounds.Height, 1, 1);
        double aspect = EffectiveAspect(_bitmap.PixelSize.Width, _bitmap.PixelSize.Height);
        double renderW = Bounds.Width;
        double renderH = renderW / aspect;
        if (renderH > Bounds.Height)
        {
            renderH = Bounds.Height;
            renderW = renderH * aspect;
        }
        double offsetX = (Bounds.Width - renderW) / 2;
        return (offsetX, renderW, renderH,
                renderW / _bitmap.PixelSize.Width, renderH / _bitmap.PixelSize.Height);
    }

    /// <summary>
    /// Capture the BBS terminal content as a bitmap (excludes black padding bars).
    /// Includes the bitmap, sixel/tile overlays, and cursor — anything Render draws.
    /// Returns null if the canvas has not been laid out or has no content yet.
    /// </summary>
    /// <summary>
    /// The user's own "Hide Cursor" toggle from the terminal right-click menu. Separate from the
    /// BBS-driven cursor state, which still wins: this only ever hides, never forces the cursor
    /// on. Not persisted — it resets with each session, like the macOS client.
    /// </summary>
    public bool UserHideCursor { get; set; }

    public Bitmap? CaptureSnapshot()
    {
        if (_bitmap == null || Bounds.Width <= 0 || Bounds.Height <= 0) return null;

        int fullW = (int)Math.Ceiling(Bounds.Width);
        int fullH = (int)Math.Ceiling(Bounds.Height);
        if (fullW <= 0 || fullH <= 0) return null;

        var rtb = new RenderTargetBitmap(new PixelSize(fullW, fullH), new Vector(96, 96));
        rtb.Render(this);

        var rect = GetBbsScreenRect();
        if (rect.Width <= 0 || rect.Height <= 0)
            return rtb;

        int cropX = Math.Max(0, (int)Math.Round(rect.X));
        int cropY = Math.Max(0, (int)Math.Round(rect.Y));
        int cropW = Math.Min(rtb.PixelSize.Width - cropX, (int)Math.Round(rect.Width));
        int cropH = Math.Min(rtb.PixelSize.Height - cropY, (int)Math.Round(rect.Height));
        if (cropW <= 0 || cropH <= 0)
            return rtb;

        var cropped = new RenderTargetBitmap(new PixelSize(cropW, cropH), new Vector(96, 96));
        using (var ctx = cropped.CreateDrawingContext())
        {
            var srcRect = new Rect(cropX, cropY, cropW, cropH);
            var dstRect = new Rect(0, 0, cropW, cropH);
            ctx.DrawImage(rtb, srcRect, dstRect);
        }
        return cropped;
    }

    public event Action<byte[]>? InputGenerated;
    public event Action<int>? ScrollbackChanged;
    public event Action<int, int>? TerminalDimensionsChanged;
    public event Action<bool>? SelectionChanged;

    public int Columns => _cols;
    public int Rows => _rows;
    public int FontDataHeight => _fontDataHeight;
    public int BitmapWidth => _bitmap?.PixelSize.Width ?? 0;
    public int BitmapHeight => _bitmap?.PixelSize.Height ?? 0;
    public bool IsSelecting => _isSelecting;

    /// <summary>The cell under a point, 0-based, as selection maps it (TerminalMouse.cs uses it for mouse reports).</summary>
    internal (int col, int row) CellAt(Point p) => ScreenToCell(p.X, p.Y);

    /// <summary>True while the view shows scrollback; a click then returns to the live screen.</summary>
    internal bool IsScrolledBack => _scrollOffset > 0;

    /// <summary>
    /// Get raw font bitmap rows for a character (for diagnostics).
    /// </summary>
    public byte[]? GetFontRows(int ch)
    {
        if (_fontData == null || ch < 0 || ch >= 256) return null;
        int offset = ch * _fontDataHeight;
        if (offset + _fontDataHeight > _fontData.Length) return null;
        var rows = new byte[_fontDataHeight];
        Array.Copy(_fontData, offset, rows, 0, _fontDataHeight);
        return rows;
    }

    /// <summary>
    /// Aspect ratio of the terminal bitmap (width / height).
    /// </summary>
    public double BitmapAspectRatio
    {
        get
        {
            return EffectiveAspect(_cols * FontWidth, _rows * _fontDataHeight);
        }
    }

    public void SetFontSmoothing(bool enabled)
    {
        _fontSmoothing = enabled;
        InvalidateVisual();
    }

    public TerminalCanvas()
    {
        Focusable = true;
        TerminalMouse.Attach(this);  // xterm mouse reports when a board asks (TerminalMouse.cs)

        // Initialize default palette
        var defaultPalette = new uint[]
        {
            0x000000, 0x0000AA, 0x00AA00, 0x00AAAA,
            0xAA0000, 0xAA00AA, 0xAA5500, 0xAAAAAA,
            0x555555, 0x5555FF, 0x55FF55, 0x55FFFF,
            0xFF5555, 0xFF55FF, 0xFFFF55, 0xFFFFFF
        };
        for (int i = 0; i < 16; i++)
            _paletteArgb[i] = unchecked((int)(0xFF000000 | defaultPalette[i]));

        // Cursor blink timer -- 133ms matches SyncTERM bitmap_con.c blinker_thread default
        _cursorTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(133)
        };
        _cursorTimer.Tick += (_, _) =>
        {
            // Cursor blinks every 133ms tick (matching SyncTERM blinker_thread)
            _cursorBlinkOn = !_cursorBlinkOn;

            // Text blink (SGR 5/6) toggles every 2nd tick = 266ms (matching SyncTERM)
            _blinkTickCount++;
            bool textChanged = false;
            if (_blinkTickCount >= 2)
            {
                _blinkTickCount = 0;
                _textBlinkOn = !_textBlinkOn;
                textChanged = true;
            }

            // Re-render blinking cells only when text blink phase changes
            if (textChanged && _hasBlinkingCells && _lastScreenBuffer != null && _scrollOffset == 0)
            {
                _prevScreen = null; // Force full redraw so blink state applies
                RenderScreen(_lastScreenBuffer, _lastFg, _lastBg);
            }
            InvalidateVisual();
        };

        AttachedToVisualTree += (_, _) =>
        {
            _cursorTimer.Start();
            AttachPhysicalKeys();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _cursorTimer.Stop();
            DetachPhysicalKeys();
        };
    }

    /// <summary>
    /// Clear all display state so the next connection starts with a blank screen.
    /// </summary>
    public void ResetDisplay()
    {
        _prevScreen = null;
        _pixels = null;
        _lastScreenBuffer = null;
        _fontData = null;
        _fontDataHeight = FontHeight;
        _glyphCache.Clear();
        _scrollOffset = 0;
        _cursorX = 0;
        _cursorY = 0;
        _cursorVisible = false;
        CancelSelection();
        _detectedUrls.Clear();

        // Clear tile/bg underlay state
        _bgActive = false;
        _tileActive = false;
        _tileAtlasBitmap = null;
        _tileGrid = null;
        _tileBitmap = null;
        _tileBitmapDirty = false;

        // Reset palette to default CGA colors
        var defaultPalette = new uint[]
        {
            0x000000, 0x0000AA, 0x00AA00, 0x00AAAA,
            0xAA0000, 0xAA00AA, 0xAA5500, 0xAAAAAA,
            0x555555, 0x5555FF, 0x55FF55, 0x55FFFF,
            0xFF5555, 0xFF55FF, 0xFFFF55, 0xFFFFFF
        };
        for (int i = 0; i < 16; i++)
            _paletteArgb[i] = unchecked((int)(0xFF000000 | defaultPalette[i]));
        if (_bitmap != null)
        {
            int bmpW = _bitmap.PixelSize.Width;
            int bmpH = _bitmap.PixelSize.Height;
            var black = new int[bmpW * bmpH];
            using (var fb = _bitmap.Lock())
            {
                Marshal.Copy(black, 0, fb.Address, black.Length);
            }
        }
        InvalidateVisual();
    }

    public void SetFontData(byte[] fontBitmapData)
    {
        if (fontBitmapData.Length < 3) return;

        int newHeight = fontBitmapData[1];
        bool heightChanged = newHeight != _fontDataHeight;

        // First 2 bytes: width (always 8), height
        _fontDataHeight = newHeight;
        _fontData = new byte[fontBitmapData.Length - 2];
        Array.Copy(fontBitmapData, 2, _fontData, 0, _fontData.Length);

        // Force full redraw: clear glyph cache and previous screen state
        // so the dirty optimization doesn't skip cells rendered with the old font
        _glyphCache.Clear();
        _prevScreen = null;
        _pixels = null;

        // Recreate bitmap if font height changed
        if (heightChanged && _bitmap != null)
        {
            int bmpW = _cols * FontWidth;
            int bmpH = _rows * _fontDataHeight;
            _bitmap = new WriteableBitmap(
                new PixelSize(bmpW, bmpH),
                new Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888,
                AlphaFormat.Premul);

            // Rebuild tile bitmap with new cell height
            if (_tileActive && _tileGrid != null)
                RebuildTileBitmap();
        }

        // Re-render immediately with the last known screen buffer
        // so the screen isn't stuck black if the BBS is idle
        if (_lastScreenBuffer != null && _bitmap != null)
        {
            RenderScreen(_lastScreenBuffer, _lastFg, _lastBg);
            InvalidateVisual();
        }
    }

    public void UpdatePalette(int[] palette)
    {
        bool changed = false;
        for (int i = 0; i < 16 && i < palette.Length; i++)
        {
            int argb = unchecked((int)(0xFF000000 | (uint)palette[i]));
            if (_paletteArgb[i] != argb)
            {
                _paletteArgb[i] = argb;
                changed = true;
            }
        }
        if (changed)
        {
            _glyphCache.Clear(); // Palette change invalidates all cached glyphs
            _prevScreen = null;  // Force full redraw so all cells pick up new palette
        }
    }

    /// <param name="cellFg">Per-cell 24-bit foreground colours from the native true-colour
    /// fetch, or null to fall back to the legacy 16-colour palette path.</param>
    /// <param name="cellBg">Per-cell 24-bit background colours, same contract as cellFg.</param>
    public void UpdateScreen(int[] screenBuffer, int[]? cellFg, int[]? cellBg, int width, int height,
                              int cursorX, int cursorY, bool cursorVisible,
                              int cursorType = CursorTypeNormal,
                              int cursorStart = 14, int cursorEnd = 15,
                              int cursorBlinks = 1, int ciolibFg = 0xAAAAAA)
    {
        if (width != _cols || height != _rows || _bitmap == null)
        {
            bool dimensionsChanged = width != _cols || height != _rows;
            _cols = width;
            _rows = height;
            int bmpW = _cols * FontWidth;
            int bmpH = _rows * _fontDataHeight;
            _bitmap = new WriteableBitmap(
                new PixelSize(bmpW, bmpH),
                new Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888,
                AlphaFormat.Premul);
            _prevScreen = null;
            _pixels = null;
            _glyphCache.Clear();

            if (dimensionsChanged)
            {
                InvalidateMeasure();
                TerminalDimensionsChanged?.Invoke(_cols, _rows);
            }
        }

        _cursorX = cursorX;
        _cursorY = cursorY;
        _cursorVisible = cursorVisible;
        _cursorType = cursorType;
        _cursorStart = cursorStart;
        _cursorEnd = cursorEnd;
        _cursorBlinks = cursorBlinks != 0;
        _ciolibFg = ciolibFg;

        // Keep a copy for re-render after font change -- colours included, or a re-render
        // (font change, returning from scrollback) would repaint the screen in the wrong shades.
        int cellCount2 = width * height;
        if (_lastScreenBuffer == null || _lastScreenBuffer.Length != cellCount2)
            _lastScreenBuffer = new int[cellCount2];
        Array.Copy(screenBuffer, _lastScreenBuffer, Math.Min(screenBuffer.Length, cellCount2));
        if (cellFg != null && cellBg != null)
        {
            if (_lastFg == null || _lastFg.Length != cellCount2) _lastFg = new int[cellCount2];
            if (_lastBg == null || _lastBg.Length != cellCount2) _lastBg = new int[cellCount2];
            Array.Copy(cellFg, _lastFg, Math.Min(cellFg.Length, cellCount2));
            Array.Copy(cellBg, _lastBg, Math.Min(cellBg.Length, cellCount2));
        }
        else
        {
            _lastFg = null;
            _lastBg = null;
        }

        // If scrolled back, save live data but don't render it
        if (_scrollOffset > 0)
            return;

        RenderScreen(screenBuffer, cellFg, cellBg);
        InvalidateVisual();
    }

    private void RenderScreen(int[] screenBuffer, int[]? cellFg, int[]? cellBg)
    {
        if (_bitmap == null) return;

        // If no font data, try to load it from the native side on demand.
        // This handles the case where ResetDisplay() cleared _fontData
        // and CachedFontBitmap was not yet available when SetFontData was called.
        if (_fontData == null)
        {
            TryLoadFontFromNative();
            if (_fontData == null) return; // Still no font available
        }

        int bmpW = _bitmap.PixelSize.Width;
        int bmpH = _bitmap.PixelSize.Height;
        int stride = bmpW;
        int pixelCount = bmpW * bmpH;

        // Reuse pixel buffer across frames so unchanged cells retain their pixels
        if (_pixels == null || _pixels.Length != pixelCount)
            _pixels = new int[pixelCount];

        int cellCount = _cols * _rows;
        bool trueColour = cellFg != null && cellBg != null;
        bool fullRedraw = _prevScreen == null || _prevScreen.Length != cellCount
                          || (trueColour && (_prevFg == null || _prevFg.Length != cellCount));

        // No cell changed (a sixel or JXL frame, a cursor move): the bitmap already shows this screen, so skip
        // the URL scan and the upload (Windows 2026-09-15)
        if (!fullRedraw && screenBuffer.Length >= cellCount
            && screenBuffer.AsSpan(0, cellCount).SequenceEqual(_prevScreen)
            && (!trueColour || (cellFg!.Length >= cellCount && cellBg!.Length >= cellCount
                                && cellFg.AsSpan(0, cellCount).SequenceEqual(_prevFg)
                                && cellBg.AsSpan(0, cellCount).SequenceEqual(_prevBg))))
            return;

        // Detect URLs for underline rendering and click-to-open
        DetectUrls(screenBuffer);
        _hasBlinkingCells = false;
        int minRow = int.MaxValue;
        int maxRow = -1;

        for (int i = 0; i < cellCount && i < screenBuffer.Length; i++)
        {
            int cell = screenBuffer[i];

            // Track blink attribute regardless of dirty state
            if ((cell & 0x8000) != 0)
                _hasBlinkingCells = true;

            // True colour of this cell. Native already resolved the palette lookup, the literal
            // ESC[38;2;R;G;Bm case, and the DOS blink/background-intensity rule, so there is no
            // index arithmetic to do here -- only the alpha to add.
            int rawFg = 0, rawBg = 0;
            if (trueColour && i < cellFg!.Length && i < cellBg!.Length)
            {
                rawFg = cellFg[i];
                rawBg = cellBg[i];
            }

            // Skip unchanged cells -- colour is part of "unchanged" now.
            if (!fullRedraw && _prevScreen![i] == cell
                && (!trueColour || (_prevFg![i] == rawFg && _prevBg![i] == rawBg)))
                continue;

            int ch = cell & 0xFF;
            int fgColor, bgColor;

            if (trueColour)
            {
                fgColor = unchecked((int)(0xFF000000u | (uint)(rawFg & 0x00FFFFFF)));
                bgColor = unchecked((int)(0xFF000000u | (uint)(rawBg & 0x00FFFFFF)));
                // Blink-off hides the text by painting it in its own background.
                if ((cell & 0x8000) != 0 && !_textBlinkOn)
                    fgColor = bgColor;
            }
            else
            {
                // Legacy 16-colour path, kept for any caller that has no colour arrays.
                int fg = (cell >> 16) & 0x7F; // 7 bits for fg color index
                int bg = (cell >> 24) & 0xFF;

                // Blink attribute (bit 7 of legacy attr = bit 15 of packed cell)
                // Uses _textBlinkOn (266ms cycle) separate from cursor blink (133ms)
                if ((cell & 0x8000) != 0)
                {
                    bg &= 0x07; // Strip leaked blink bit from background
                    if (!_textBlinkOn)
                        fg = bg; // Hide text during blink-off phase
                }

                // Clamp to palette range
                if (fg >= 16) fg &= 0x0F;
                if (bg >= 16) bg &= 0x0F;
                fgColor = _paletteArgb[fg];
                bgColor = _paletteArgb[bg];
            }

            int cellCol = i % _cols;
            int cellRow = i / _cols;
            int cellX = cellCol * FontWidth;
            int cellY = cellRow * _fontDataHeight;
            if (cellRow < minRow) minRow = cellRow;
            if (cellRow > maxRow) maxRow = cellRow;

            bool isUnderlay = IsCellInUnderlayRegion(cellCol, cellRow);
            int[] glyph = GetOrCreateGlyph(ch, fgColor, bgColor, isUnderlay);
            BlitGlyph(_pixels, stride, cellX, cellY, glyph);

            // Draw underline for detected URLs only (matches Windows and Android).
            // ESC[4m underline data is tracked in cells but NOT rendered: most BBSes send underline codes that
            // were historically ignored, so honouring them draws grey rules across artwork and input fields.
            if (IsCellInUrl(cellRow, cellCol))
            {
                // Position at ~90% down the cell (matches Android), row 14 of 16
                int underlineY = cellY + _fontDataHeight - 2;
                int dstOffset = underlineY * stride + cellX;
                if (dstOffset >= 0 && dstOffset + FontWidth <= _pixels.Length)
                {
                    for (int col = 0; col < FontWidth; col++)
                        _pixels[dstOffset + col] = fgColor;
                }
            }
        }

        // Write only the rows that changed to the bitmap
        if (maxRow >= 0)
        {
            int y0 = minRow * _fontDataHeight;
            int rowsHeight = Math.Min((maxRow - minRow + 1) * _fontDataHeight, bmpH - y0);
            if (rowsHeight > 0)
            {
                using var fb = _bitmap.Lock();
                int rowBytes = fb.RowBytes;   // the bitmap's own row size, not assumed to be bmpW * 4
                for (int y = y0; y < y0 + rowsHeight; y++)
                {
                    int start = y * stride;
                    if (start + stride > _pixels.Length)
                        break;
                    Marshal.Copy(_pixels, start, fb.Address + y * rowBytes, stride);
                }
            }
        }

        // Save current screen for next dirty check
        if (_prevScreen == null || _prevScreen.Length != cellCount)
            _prevScreen = new int[cellCount];
        Array.Copy(screenBuffer, _prevScreen, Math.Min(cellCount, screenBuffer.Length));
        if (trueColour)
        {
            if (_prevFg == null || _prevFg.Length != cellCount) _prevFg = new int[cellCount];
            if (_prevBg == null || _prevBg.Length != cellCount) _prevBg = new int[cellCount];
            Array.Copy(cellFg!, _prevFg, Math.Min(cellCount, cellFg!.Length));
            Array.Copy(cellBg!, _prevBg, Math.Min(cellCount, cellBg!.Length));
        }
        else
        {
            _prevFg = null;
            _prevBg = null;
        }
    }

    /// <summary>
    /// Refresh the sixel pixel overlay bitmap from native data.
    /// Call after RenderScreen in the update cycle.
    /// </summary>
    public void RefreshSixelOverlay()
    {
        bool hasSixel = NativeBridge.syncterm_has_sixel_data() != 0;

        if (!hasSixel)
        {
            if (_hasSixelData)
            {
                // Had data before, now gone -- clear
                _sixelBitmap = null;
                _hasSixelData = false;
            }
            // Clears native's changed flag for the now-empty layer, so the poll loop stops queueing redraws
            NativeBridge.syncterm_copy_sixel_pixels(IntPtr.Zero, 0, 0, 0, 0, out _, out _, out _, out _, out _, out _);
            return;
        }

        bool isDirty = NativeBridge.syncterm_is_sixel_dirty() != 0;
        if (!isDirty && _sixelBitmap != null)
            return; // No new data, keep existing bitmap

        NativeBridge.syncterm_get_sixel_dimensions(out int pw, out int ph);
        if (pw <= 0 || ph <= 0) return;

        // Native copies straight into the bitmap's frame buffer, and only the rectangle that changed since the last
        // copy (Windows 2026-09-15). A new or re-sized bitmap takes the whole layer. Two tries: the layer can grow
        // between the size check and the copy, which reports the new size instead of copying.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            bool full = false;
            if (_sixelBitmap == null || _sixelWidth != pw || _sixelHeight != ph)
            {
                _sixelBitmap = new WriteableBitmap(
                    new PixelSize(pw, ph),
                    new Vector(96, 96),
                    Avalonia.Platform.PixelFormat.Bgra8888,
                    AlphaFormat.Premul);
                _sixelWidth = pw;
                _sixelHeight = ph;
                full = true;
            }

            int copied;
            // ARGB packed ints match Bgra8888 on little-endian (0xAARRGGBB -> bytes B,G,R,A)
            using (var fb = _sixelBitmap.Lock())
            {
                copied = NativeBridge.syncterm_copy_sixel_pixels(fb.Address, pw, ph, fb.RowBytes / 4, full ? 1 : 0,
                    out _, out _, out _, out _, out int bw, out int bh);
                pw = bw;
                ph = bh;
            }

            if (copied >= 0)
            {
                if (copied > 0)
                    _hasSixelData = true;
                return;
            }
            if (pw <= 0 || ph <= 0)
                return;
        }
    }

    /// <summary>
    /// Attempt to load font bitmap data directly from the native library.
    /// Used as a fallback when _fontData is null (e.g. after ResetDisplay).
    /// </summary>
    private void TryLoadFontFromNative()
    {
        var buffer = new byte[256 * 32 + 2]; // Support font heights up to 32px
        int len = NativeBridge.syncterm_get_font_bitmap(buffer, buffer.Length);
        if (len <= 2) return;

        int newHeight = buffer[1];
        if (newHeight <= 0 || newHeight > 32) return;

        _fontDataHeight = newHeight;
        _fontData = new byte[len - 2];
        Array.Copy(buffer, 2, _fontData, 0, len - 2);
        _glyphCache.Clear();
    }

    /// <param name="fgColor">Fully-opaque ARGB foreground, already resolved (true colour or
    /// palette). Was a 0-15 palette index before true-colour support.</param>
    /// <param name="bgColor">Fully-opaque ARGB background; ignored when transparentBg.</param>
    private int[] GetOrCreateGlyph(int ch, int fgColor, int bgColor, bool transparentBg = false)
    {
        if (transparentBg) bgColor = 0;
        // 8 bits of char + 24 bits each of fg and bg + the transparency flag = 57 bits, so the
        // key has to be a long now; the old int key could only hold 4-bit colour indices.
        long key = ((long)(ch & 0xFF) << 49)
                 | ((long)(fgColor & 0x00FFFFFF) << 25)
                 | ((long)(bgColor & 0x00FFFFFF) << 1)
                 | (transparentBg ? 1L : 0L);
        if (_glyphCache.TryGetValue(key, out var cached))
            return cached;

        // With true colour the key space is effectively unbounded (a gradient can mint one entry
        // per cell), so bound the cache. Dropping it wholesale is fine -- it refills from the
        // font data on the next frame and costs one redraw.
        if (_glyphCache.Count >= GlyphCacheLimit)
            _glyphCache.Clear();

        var glyph = new int[FontWidth * _fontDataHeight];

        if (_fontData != null && ch < 256)
        {
            int fontOffset = ch * _fontDataHeight;
            for (int row = 0; row < _fontDataHeight; row++)
            {
                if (fontOffset + row >= _fontData.Length) break;
                byte rowBits = _fontData[fontOffset + row];
                for (int col = 0; col < FontWidth; col++)
                {
                    bool lit = (rowBits & (0x80 >> col)) != 0;
                    glyph[row * FontWidth + col] = lit ? fgColor : bgColor;
                }
            }
        }
        else
        {
            // Unknown char: fill with background
            Array.Fill(glyph, bgColor);
        }

        _glyphCache[key] = glyph;
        return glyph;
    }

    private void BlitGlyph(int[] pixels, int stride, int x, int y, int[] glyph)
    {
        for (int row = 0; row < _fontDataHeight; row++)
        {
            int dstOffset = (y + row) * stride + x;
            int srcOffset = row * FontWidth;
            if (dstOffset + FontWidth > pixels.Length) break;
            Array.Copy(glyph, srcOffset, pixels, dstOffset, FontWidth);
        }
    }

    /// <summary>
    /// The "SCROLLBACK (n lines)" banner. A UI annotation rather than terminal content, so the
    /// CRT path draws it AFTER the shader and leaves it crisp instead of scanlining it.
    /// </summary>
    private void DrawScrollbackIndicator(DrawingContext dc)
    {
        if (_scrollOffset <= 0) return;

        var indicatorText = new FormattedText(
            $"SCROLLBACK ({_scrollOffset} lines)",
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("JetBrains Mono, DejaVu Sans Mono, monospace"),
            12,
            Brushes.White);
        double textX = (Bounds.Width - indicatorText.Width) / 2;
        var bgRect = new Rect(textX - 6, 0, indicatorText.Width + 12, indicatorText.Height + 4);
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0xCC, 0x00, 0x00, 0x00)), null, bgRect);
        dc.DrawText(indicatorText, new Point(textX, 2));
    }

    // ================================================================
    // CRT / scanline post-process
    // ================================================================

    /// <summary>
    /// Live CRT settings, or null / disabled for an unfiltered terminal. Set from
    /// TerminalView.ApplyCrtSettings so the options window can dial the look in against a
    /// running session, exactly as on Windows.
    /// </summary>
    public CrtSettings? Crt { get; set; }

    private readonly Stopwatch _crtClock = Stopwatch.StartNew();
    private DispatcherTimer? _crtAnimTimer;

    /// <summary>
    /// Apply CRT settings to the live terminal. Safe to call at any time — while connected the
    /// change shows on the very next frame, which is what lets the options window be dialled in
    /// against real ANSI art.
    ///
    /// ⚠️ Unlike WPF, nothing here repaints on its own. Flicker, interlace, the rolling bar, the
    /// static and the scan sweep all read the Time uniform, so if any of them is in play this
    /// has to drive InvalidateVisual itself or the animation freezes on a still screen.
    /// </summary>
    public void ApplyCrt(CrtSettings? settings)
    {
        Crt = settings;

        if (settings is { Enabled: true } && CrtParams.IsAnimated(settings))
            StartCrtAnimation();
        else
            StopCrtAnimation();

        InvalidateVisual();
    }

    private void StartCrtAnimation()
    {
        if (_crtAnimTimer != null) return;
        _crtAnimTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _crtAnimTimer.Tick += (_, _) => InvalidateVisual();
        _crtAnimTimer.Start();
    }

    private void StopCrtAnimation()
    {
        _crtAnimTimer?.Stop();
        _crtAnimTimer = null;
    }

    /// <summary>True when the effect should run this frame.</summary>
    private bool CrtActive => Crt is { Enabled: true } && CrtShader.IsSupported && _bitmap != null;

    /// <summary>
    /// Copy an Avalonia bitmap into an SKImage.
    ///
    /// Goes through CopyPixels rather than WriteableBitmap.Lock so the ONE method covers the
    /// tile underlay too — that layer is a RenderTargetBitmap, which cannot be locked. The
    /// terminal bitmaps are created Bgra8888 / Premul, which is what Skia wants, so no
    /// conversion happens beyond the copy itself.
    /// </summary>
    internal static SKImage? ToSKImage(Bitmap? bmp)
    {
        if (bmp == null) return null;
        var size = bmp.PixelSize;
        if (size.Width <= 0 || size.Height <= 0) return null;

        var info = new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        IntPtr buffer = IntPtr.Zero;
        try
        {
            buffer = Marshal.AllocHGlobal(info.BytesSize);
            bmp.CopyPixels(new PixelRect(0, 0, size.Width, size.Height),
                           buffer, info.BytesSize, info.RowBytes);
            return SKImage.FromPixelCopy(info, buffer, info.RowBytes);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"CRT: bitmap copy failed: {ex.Message}");
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
                Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Compose everything the shader should see into one image, at the terminal's NATIVE pixel
    /// size rather than at output size — it is what the canvas already has, and it keeps the
    /// per-frame copy small (a 640x400 screen is 1MB). The draw operation scales it up.
    ///
    /// The layer order matches the unfiltered path: tile underlay, terminal text over it (cells
    /// in an underlay region carry a transparent background), sixel on top, then the cursor and
    /// selection so they get scanlined along with the text the way they do on Windows.
    /// </summary>
    private SKImage? BuildCrtContent()
    {
        if (_bitmap == null) return null;
        int w = _bitmap.PixelSize.Width;
        int h = _bitmap.PixelSize.Height;
        if (w <= 0 || h <= 0) return null;

        SKSurface? surface = null;
        try
        {
            surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
            if (surface == null) return null;
            var c = surface.Canvas;

            // A TAP+ background image lives OUTSIDE this canvas, so painting black over it
            // would hide it. Same reason the unfiltered path skips its background fill.
            c.Clear(_bgActive ? SKColors.Transparent : SKColors.Black);

            if (_tileActive && _tileBitmap != null && _scrollOffset == 0)
            {
                if (_tileBitmapDirty)
                    RebuildTileBitmap();
                using var tile = ToSKImage(_tileBitmap);
                if (tile != null)
                {
                    var dst = SKRect.Create(
                        (_tileCol1 - 1) * FontWidth,
                        (_tileRow1 - 1) * _fontDataHeight,
                        (_tileCol2 - _tileCol1 + 1) * FontWidth,
                        (_tileRow2 - _tileRow1 + 1) * _fontDataHeight);
                    using var tp = new SKPaint
                    {
                        Color = SKColors.White.WithAlpha((byte)Math.Clamp(_tileOpacity * 255.0, 0, 255))
                    };
                    c.DrawImage(tile, dst, tp);
                }
            }

            using (var term = ToSKImage(_bitmap))
            {
                if (term == null) return null;
                c.DrawImage(term, 0, 0);
            }

            if (_hasSixelData && _sixelBitmap != null && _scrollOffset == 0)
            {
                using var sixel = ToSKImage(_sixelBitmap);
                if (sixel != null)
                    c.DrawImage(sixel, SKRect.Create(0, 0, _sixelWidth, _sixelHeight));
            }

            DrawCrtCursor(c);
            DrawCrtSelection(c);

            return surface.Snapshot();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"CRT: compose failed: {ex.Message}");
            return null;
        }
        finally
        {
            surface?.Dispose();
        }
    }

    /// <summary>
    /// The cursor in NATIVE pixels. Same rules as the unfiltered path, including the clamp that
    /// stops a scanline range carried over from a taller cell painting a row too low.
    /// </summary>
    private void DrawCrtCursor(SKCanvas c)
    {
        bool showCursor = _cursorVisible && _scrollOffset == 0
            && _cursorStart <= _cursorEnd
            && (_cursorBlinkOn || !_cursorBlinks)
            && _cursorX >= 1 && _cursorX <= _cols
            && _cursorY >= 1 && _cursorY <= _rows;
        if (!showCursor) return;

        int lastScan = Math.Max(_fontDataHeight, 1) - 1;
        int cursStart = Math.Clamp(_cursorStart, 0, lastScan);
        int cursEnd = Math.Clamp(_cursorEnd, cursStart, lastScan);
        float top = (_cursorY - 1) * _fontDataHeight + cursStart;
        float bot = (_cursorY - 1) * _fontDataHeight + cursEnd + 1;

        using var paint = new SKPaint
        {
            Color = new SKColor((byte)((_ciolibFg >> 16) & 0xFF),
                                (byte)((_ciolibFg >> 8) & 0xFF),
                                (byte)(_ciolibFg & 0xFF))
        };
        c.DrawRect(SKRect.Create((_cursorX - 1) * FontWidth, top,
                                 FontWidth, Math.Max(bot - top, 1)), paint);
    }

    /// <summary>The selection highlight in NATIVE pixels.</summary>
    private void DrawCrtSelection(SKCanvas c)
    {
        if (!_isSelecting || _selMinCol < 0) return;

        float x1 = _selMinCol * FontWidth;
        float y1 = _selMinRow * _fontDataHeight;
        float x2 = (_selMaxCol + 1) * FontWidth;
        float y2 = (_selMaxRow + 1) * _fontDataHeight;
        var rect = SKRect.Create(x1, y1, x2 - x1, y2 - y1);

        using var fill = new SKPaint { Color = new SKColor(0x33, 0xAA, 0xFF, 0x60) };
        c.DrawRect(rect, fill);
        using var border = new SKPaint
        {
            Color = new SKColor(0x33, 0xAA, 0xFF), Style = SKPaintStyle.Stroke, StrokeWidth = 1
        };
        c.DrawRect(rect, border);
    }

    public override void Render(DrawingContext dc)
    {
        // CRT path: everything the shader should filter is composed into one image and pushed
        // through the runtime effect. Falls through to the normal path whenever the effect is
        // off, unsupported, or the compose failed, so a shader problem can never blank a live
        // session.
        if (CrtActive)
        {
            var content = BuildCrtContent();
            if (content != null)
            {
                var (crtOffsetX, crtW, crtH, _, _) = ComputeRenderRect();
                if (crtW > 0 && crtH > 0)
                {
                    double scaling = VisualRoot?.RenderScaling ?? 1.0;
                    var p = CrtParams.From(Crt!, crtH, scaling, _crtClock.Elapsed.TotalSeconds);
                    dc.Custom(new CrtDrawOperation(new Rect(crtOffsetX, 0, crtW, crtH), content, p));
                    DrawScrollbackIndicator(dc);
                    return;
                }
                content.Dispose();
            }
        }

        // Fill background black (skip when bg underlay active so AXAML bg image shows through)
        if (!_bgActive)
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, Bounds.Width, Bounds.Height));

        if (_bitmap != null)
        {
            // Fit the picture at its effective aspect (natural, or forced for C64 modes)
            var (offsetX, renderW, renderH, scaleX, scaleY) = ComputeRenderRect();

            // Set bitmap interpolation mode based on font smoothing
            var bitmapInterpolation = _fontSmoothing
                ? BitmapInterpolationMode.HighQuality
                : BitmapInterpolationMode.None;

            using (dc.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = bitmapInterpolation }))
            {
                // Tile underlay layer -- drawn BEHIND terminal text, ON TOP of bg image
                // Hidden during scrollback so tiles don't overlay scrollback text
                if (_tileActive && _tileBitmap != null && _scrollOffset == 0)
                {
                    if (_tileBitmapDirty)
                        RebuildTileBitmap();
                    double cellW = FontWidth * scaleX;
                    double cellH = _fontDataHeight * scaleY;
                    double tileLeft = offsetX + (_tileCol1 - 1) * cellW;
                    double tileTop = (_tileRow1 - 1) * cellH;
                    double tileRenderW = (_tileCol2 - _tileCol1 + 1) * cellW;
                    double tileRenderH = (_tileRow2 - _tileRow1 + 1) * cellH;
                    if (_tileOpacity < 1.0)
                    {
                        using (dc.PushOpacity(_tileOpacity))
                        {
                            dc.DrawImage(_tileBitmap, new Rect(tileLeft, tileTop, tileRenderW, tileRenderH));
                        }
                    }
                    else
                    {
                        dc.DrawImage(_tileBitmap, new Rect(tileLeft, tileTop, tileRenderW, tileRenderH));
                    }
                }

                // Terminal text bitmap (cells in underlay regions have transparent bg)
                dc.DrawImage(_bitmap, new Rect(offsetX, 0, renderW, renderH));

                // Sixel overlay -- scale proportionally to match terminal bitmap
                if (_hasSixelData && _sixelBitmap != null && _scrollOffset == 0)
                {
                    double sx = renderW / _bitmap.PixelSize.Width;
                    double sy = renderH / _bitmap.PixelSize.Height;
                    double sixelRenderW = _sixelWidth * sx;
                    double sixelRenderH = _sixelHeight * sy;
                    dc.DrawImage(_sixelBitmap, new Rect(offsetX, 0, sixelRenderW, sixelRenderH));
                }
            }

            DrawScrollbackIndicator(dc);

            // Draw cursor overlay -- scanline-based matching SyncTERM bitmap_con.c.
            // No idle threshold: cursor drawn every frame when visible.
            // Steady cursor (_cursorBlinks=false) skips blink phase -- always visible.
            // curs_start > curs_end means hidden in the native ciolib.
            bool showCursor = _cursorVisible && _scrollOffset == 0
                && _cursorStart <= _cursorEnd
                && (_cursorBlinkOn || !_cursorBlinks)
                && _cursorX >= 1 && _cursorX <= _cols
                && _cursorY >= 1 && _cursorY <= _rows;
            if (showCursor)
            {
                double cellW = (double)FontWidth * scaleX;
                double cellH = (double)_fontDataHeight * scaleY;
                double cx = offsetX + (_cursorX - 1) * cellW;

                // Scanline-based cursor: spans from curs_start to curs_end+1 within cell.
                // Clamp to the cell: curs_start/curs_end are scanlines relative to the
                // CURRENT cell height, and a value carried over from a taller cell (the
                // 14/15 VGA underline landing in an 8-pixel PETSCII cell) would otherwise
                // paint the cursor a whole row below the character it belongs to. Native
                // now re-derives these per mode; this keeps the renderer honest regardless.
                int lastScan = Math.Max(_fontDataHeight, 1) - 1;
                int cursStart = Math.Clamp(_cursorStart, 0, lastScan);
                int cursEnd = Math.Clamp(_cursorEnd, cursStart, lastScan);
                double top = (_cursorY - 1) * cellH + (cursStart * cellH / _fontDataHeight);
                double bot = (_cursorY - 1) * cellH + ((cursEnd + 1) * cellH / _fontDataHeight);
                double cursorH = Math.Max(bot - top, 1);

                // Use resolved ciolib_fg RGB color (matches color_value(ciolib_fg) in bitmap_con.c).
                var cursorBrush = new SolidColorBrush(Color.FromRgb(
                    (byte)((_ciolibFg >> 16) & 0xFF),
                    (byte)((_ciolibFg >> 8) & 0xFF),
                    (byte)(_ciolibFg & 0xFF)));
                dc.DrawRectangle(cursorBrush, null, new Rect(cx, top, cellW, cursorH));
            }

            // Draw selection overlay
            if (_isSelecting && _selMinCol >= 0)
            {
                double cellW = (double)FontWidth * scaleX;
                double cellH = (double)_fontDataHeight * scaleY;
                double x1 = offsetX + _selMinCol * cellW;
                double y1 = _selMinRow * cellH;
                double x2 = offsetX + (_selMaxCol + 1) * cellW;
                double y2 = (_selMaxRow + 1) * cellH;
                var selRect = new Rect(x1, y1, x2 - x1, y2 - y1);
                dc.DrawRectangle(SelectionFillBrush, SelectionBorderPen, selRect);
            }
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double bitmapW = _cols * FontWidth;
        double bitmapH = _rows * (_fontData != null ? _fontDataHeight : FontHeight);
        // Measure at the effective aspect so a forced (C64) aspect reserves the right height
        double aspect = EffectiveAspect(bitmapW, bitmapH);
        bitmapH = bitmapW / aspect;

        // Scale uniformly to fit within available space (both width and height)
        double scaleX = !double.IsInfinity(availableSize.Width) && availableSize.Width > 0
            ? availableSize.Width / bitmapW : 1.0;
        double scaleY = !double.IsInfinity(availableSize.Height) && availableSize.Height > 0
            ? availableSize.Height / bitmapH : double.MaxValue;
        double scale = Math.Min(scaleX, scaleY);

        double w = Math.Ceiling(bitmapW * scale);
        double h = Math.Ceiling(bitmapH * scale);
        return new Size(w, h);
    }

    // ================================================================
    // Physical key reports (CSI = 1 h / CSI = 2 h)
    // ================================================================
    // SyncTERM's physical key protocol: a door (SyncDoom and the other termgfx games) turns it on and then gets
    // a press and a release report per physical key, so held keys and several keys at once work. Avalonia gives no
    // scancode, so the layout-independent PhysicalKey is mapped to the evdev code (PhysicalKeyMap). The handlers sit
    // on the window, tunnelling and with handledEventsToo, because MainWindow's own handlers mark some keys handled
    // (Alt releases among them) and a release must still arrive after focus has moved. Presses count only while this
    // canvas has focus in the active window; releases always go through (native drops releases the board never saw
    // pressed) so a key can't stick. Windows' TerminalCanvas region "Physical key reports" is the model.

    private Window? _keyWindow;

    private void AttachPhysicalKeys()
    {
        if (_keyWindow != null)
            return;
        _keyWindow = TopLevel.GetTopLevel(this) as Window;
        if (_keyWindow == null)
            return;
        _keyWindow.AddHandler(KeyDownEvent, OnPhysicalKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        _keyWindow.AddHandler(KeyUpEvent, OnPhysicalKeyUp, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        _keyWindow.Deactivated += OnKeyWindowDeactivated;
    }

    private void DetachPhysicalKeys()
    {
        if (_keyWindow == null)
            return;
        _keyWindow.RemoveHandler(KeyDownEvent, OnPhysicalKeyDown);
        _keyWindow.RemoveHandler(KeyUpEvent, OnPhysicalKeyUp);
        _keyWindow.Deactivated -= OnKeyWindowDeactivated;
        _keyWindow = null;
        NativeBridge.syncterm_key_release_all();
    }

    private void OnPhysicalKeyDown(object? sender, KeyEventArgs e) => ReportPhysicalKey(e, pressed: true);

    private void OnPhysicalKeyUp(object? sender, KeyEventArgs e) => ReportPhysicalKey(e, pressed: false);

    private void ReportPhysicalKey(KeyEventArgs e, bool pressed)
    {
        if ((NativeBridge.syncterm_get_key_modes() & 1) == 0)
            return;
        if (pressed && !(IsFocused && _keyWindow is { IsActive: true }))
            return;
        int evdev = PhysicalKeyMap.Evdev(e);
        if (evdev != 0)
            NativeBridge.syncterm_key_event(evdev, pressed ? 1 : 0);
    }

    private void OnKeyWindowDeactivated(object? sender, EventArgs e) => NativeBridge.syncterm_key_release_all();

    protected override void OnLostFocus(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        NativeBridge.syncterm_key_release_all();
    }

    /// <summary>True while the board has asked for typed key bytes to stop (CSI = 2 h).</summary>
    private static bool TranslatedKeysSuppressed => (NativeBridge.syncterm_get_key_modes() & 2) != 0;

    // ================================================================
    // Keyboard Input
    // ================================================================

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // The board is reading physical key reports instead; the canvas's own shortcuts (paste, scrollback)
        // would fire mid-game on Ctrl combinations, so they go quiet too.
        if (TranslatedKeysSuppressed)
        {
            e.Handled = true;
            base.OnKeyDown(e);
            return;
        }

        // Ctrl+C: copy selection if active, otherwise send ^C to terminal
        if (e.Key == Key.C && e.KeyModifiers == KeyModifiers.Control)
        {
            if (_isSelecting)
            {
                _ = CopySelectionToClipboardAsync();
                e.Handled = true;
                base.OnKeyDown(e);
                return;
            }
        }

        // Ctrl+V: paste clipboard as terminal input
        if (e.Key == Key.V && e.KeyModifiers == KeyModifiers.Control)
        {
            _ = PasteFromClipboardAsync();
            e.Handled = true;
            base.OnKeyDown(e);
            return;
        }

        // Ctrl+Shift+A: select all terminal text. Plain Ctrl+A falls through and goes to the BBS as 0x01 --
        // full-screen editors (Mystic's ANSI editor among them) use it, so the terminal must not eat it.
        if (e.Key == Key.A && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            SelectAll();
            e.Handled = true;
            base.OnKeyDown(e);
            return;
        }

        // Escape cancels selection
        if (e.Key == Key.Escape && _isSelecting)
        {
            CancelSelection();
            e.Handled = true;
            base.OnKeyDown(e);
            return;
        }

        // Ctrl+B: toggle scrollback buffer mode
        if (e.Key == Key.B && e.KeyModifiers == KeyModifiers.Control)
        {
            ToggleScrollback();
            e.Handled = true;
            base.OnKeyDown(e);
            return;
        }

        byte[]? sequence = PetsciiMode
            ? MapKeyToPetscii(e.Key, e.KeyModifiers)
            : MapKeyToSequence(e.Key, e.KeyModifiers);
        if (sequence != null)
        {
            // Any terminal input returns to live view and cancels selection
            if (_scrollOffset > 0)
                ReturnToLive();
            if (_isSelecting)
                CancelSelection();
            InputGenerated?.Invoke(sequence);
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        if (TranslatedKeysSuppressed)
        {
            e.Handled = true;
            base.OnTextInput(e);
            return;
        }
        if (!string.IsNullOrEmpty(e.Text))
        {
            foreach (char c in e.Text)
            {
                if (PetsciiMode)
                {
                    if (PetsciiFromChar(c) is byte p)
                        InputGenerated?.Invoke([p]);
                }
                else if (c >= 0x20 && c <= 0x7E)
                {
                    InputGenerated?.Invoke([(byte)c]);
                }
            }
            e.Handled = true;
        }
        base.OnTextInput(e);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (_isSelecting)
            CancelSelection();

        int delta = e.Delta.Y > 0 ? 3 : -3; // Scroll 3 lines at a time
        int prevOffset = _scrollOffset;
        _scrollOffset = Math.Max(0, _scrollOffset + delta);

        // Clamp to available scrollback lines
        var info = new int[4];
        NativeBridge.syncterm_get_scrollback_info(info);
        int filled = info[0];
        if (filled > 0 && _scrollOffset > filled - _rows)
            _scrollOffset = Math.Max(0, filled - _rows);

        if (_scrollOffset != prevOffset)
        {
            if (_scrollOffset > 0)
            {
                RenderScrollback();
            }
            else
            {
                // Return to live view -- re-render with last screen buffer
                _prevScreen = null;
                if (_lastScreenBuffer != null)
                {
                    RenderScreen(_lastScreenBuffer, _lastFg, _lastBg);
                    InvalidateVisual();
                }
            }
            ScrollbackChanged?.Invoke(_scrollOffset);
        }

        e.Handled = true;
        base.OnPointerWheelChanged(e);
    }

    /// <summary>
    /// Ctrl+B and View > Scrollback: back to the live screen if scrolled back, else half a screen up into history.
    /// Once scrolled back the mouse wheel scrolls the history even when the board has mouse reporting on
    /// (TerminalMouseRules.WheelToBoard). False when there is no history yet.
    /// </summary>
    public bool ToggleScrollback()
    {
        if (_scrollOffset > 0)
        {
            ReturnToLive();
            return true;
        }
        var info = new int[4];
        NativeBridge.syncterm_get_scrollback_info(info);
        int max = info[0] - _rows;     // same limit as the mouse wheel
        if (max <= 0)
            return false;
        _scrollOffset = Math.Min(Math.Max(1, _rows / 2), max);
        RenderScrollback();
        ScrollbackChanged?.Invoke(_scrollOffset);
        return true;
    }

    /// <summary>
    /// Return to live view (scroll offset 0) and re-render.
    /// </summary>
    public void ReturnToLive()
    {
        if (_scrollOffset == 0) return;
        _scrollOffset = 0;
        _prevScreen = null;
        if (_lastScreenBuffer != null)
        {
            RenderScreen(_lastScreenBuffer, _lastFg, _lastBg);
            InvalidateVisual();
        }
        ScrollbackChanged?.Invoke(0);
    }

    public void ResetScrollback()
    {
        _scrollOffset = 0;
    }

    /// <summary>
    /// Render scrollback buffer content at the current scroll offset.
    /// Reads lines from the native scrollback ring buffer and displays them.
    /// </summary>
    private void RenderScrollback()
    {
        if (_bitmap == null) return;
        if (_fontData == null)
        {
            TryLoadFontFromNative();
            if (_fontData == null) return;
        }

        var info = new int[4];
        NativeBridge.syncterm_get_scrollback_info(info);
        int filled = info[0];
        int sbCols = info[2];

        if (filled <= 0 || sbCols <= 0) return;

        // Read _rows lines from scrollback. The native API returns lines with
        // offset 0 = most recent scrollback line. We want to display _rows lines
        // ending at _scrollOffset lines back from the most recent.
        int linesToRead = Math.Min(_rows, filled);
        int offset = _scrollOffset;

        // Clamp so we don't read past the end
        if (offset + linesToRead > filled)
            linesToRead = filled - offset;
        if (linesToRead <= 0) return;

        // Read from native -- buffer contains lines from offset (most recent of range)
        // going further back. For display, we need to reverse so oldest is at top.
        int bufSize = linesToRead * sbCols;
        var rawBuffer = new int[bufSize];
        // Scrollback is stored natively as whole vmem_cells, so history keeps its true colours.
        var rawFg = new int[bufSize];
        var rawBg = new int[bufSize];
        int cellsRead = NativeBridge.syncterm_get_scrollback_buffer_rgb(
            offset, linesToRead, rawBuffer, rawFg, rawBg, bufSize);
        if (cellsRead <= 0) return;

        // Build a screen-sized buffer with the scrollback lines reversed
        // (native returns newest-first, but we want oldest-first for top-to-bottom display)
        int screenSize = _cols * _rows;
        var screenBuffer = new int[screenSize];
        var screenFg = new int[screenSize];
        var screenBg = new int[screenSize];

        for (int line = 0; line < linesToRead; line++)
        {
            // Reverse: native line 0 (newest) goes to display bottom, last line to top
            int srcLine = linesToRead - 1 - line;
            int displayLine = (_rows - linesToRead) + line; // Top-align if fewer lines than rows
            if (displayLine < 0) continue;

            for (int col = 0; col < _cols && col < sbCols; col++)
            {
                int srcIdx = srcLine * sbCols + col;
                int dstIdx = displayLine * _cols + col;
                if (srcIdx < rawBuffer.Length && dstIdx < screenBuffer.Length)
                {
                    screenBuffer[dstIdx] = rawBuffer[srcIdx];
                    screenFg[dstIdx] = rawFg[srcIdx];
                    screenBg[dstIdx] = rawBg[srcIdx];
                }
            }
        }

        // Force full redraw for scrollback
        _prevScreen = null;
        _prevFg = null;
        _prevBg = null;
        RenderScreen(screenBuffer, screenFg, screenBg);
        InvalidateVisual();
    }

    // ================================================================
    // PETSCII (Commodore) keyboard translation
    // ================================================================
    //
    // Codes follow SyncTERM's PETSCII key table (conio/petscii.c + term.c):
    // cursor keys 17/29/145/157, HOME 19, CLR 147, DEL 20, INST 148,
    // F1..F8 = 133..140 in the C64's odd/even layout, Shift+Return 141.
    // Ctrl+1..8 / Ctrl+9 / Ctrl+0 and Alt(C=)+1..8 send the C64 colour
    // and reverse-video control codes the real keyboard produces.

    private static byte[]? MapKeyToPetscii(Key key, KeyModifiers modifiers)
    {
        if (modifiers == KeyModifiers.Control)
        {
            switch (key)
            {
                case Key.D1: return [144]; // Black
                case Key.D2: return [5];   // White
                case Key.D3: return [28];  // Red
                case Key.D4: return [159]; // Cyan
                case Key.D5: return [156]; // Purple
                case Key.D6: return [30];  // Green
                case Key.D7: return [31];  // Blue
                case Key.D8: return [158]; // Yellow
                case Key.D9: return [18];  // RVS on
                case Key.D0: return [146]; // RVS off
            }
            if (key >= Key.A && key <= Key.Z)
                return [(byte)(key - Key.A + 1)];
            return null;
        }
        if (modifiers == KeyModifiers.Alt)
        {
            switch (key)
            {
                case Key.D1: return [129]; // Orange
                case Key.D2: return [149]; // Brown
                case Key.D3: return [150]; // Light Red
                case Key.D4: return [151]; // Dark Grey
                case Key.D5: return [152]; // Grey
                case Key.D6: return [153]; // Light Green
                case Key.D7: return [154]; // Light Blue
                case Key.D8: return [155]; // Light Grey
            }
            return null;
        }

        bool shift = modifiers == KeyModifiers.Shift;
        return key switch
        {
            Key.Up => [145],
            Key.Down => [17],
            Key.Left => [157],
            Key.Right => [29],
            Key.Home => shift ? [147] : [19],   // Shift+HOME = CLR on the C64
            Key.End => [147],
            Key.Insert => [148],
            Key.Delete => [20],
            Key.Back => [20],
            Key.PageUp => [131],
            Key.PageDown => [3],
            Key.F1 => [133],
            Key.F2 => [137],
            Key.F3 => [134],
            Key.F4 => [138],
            Key.F5 => [135],
            Key.F6 => [139],
            Key.F7 => [136],
            Key.F8 => [140],
            Key.Enter => shift ? [141] : [13],
            Key.Tab => [9],
            Key.Escape => [27],
            _ => null
        };
    }

    /// <summary>
    /// ASCII → PETSCII for typed text. PETSCII inverts letter case relative to ASCII
    /// (0x41-0x5A shows lowercase in the lower/upper font, 0xC1-0xDA uppercase), digits
    /// and most punctuation are shared, and a few ASCII symbols have no equivalent.
    /// </summary>
    private static byte? PetsciiFromChar(char c)
    {
        if (c >= 'a' && c <= 'z') return (byte)(c - 'a' + 0x41);
        if (c >= 'A' && c <= 'Z') return (byte)(c - 'A' + 0xC1);
        if (c >= 0x20 && c <= 0x5D) return (byte)c;   // space, digits, punctuation, [ \ ]
        return c switch
        {
            '^' => 0x5E,        // up arrow
            '_' => 0xA4,        // underline graphic
            '£' => 0x5C,        // pound
            'π' => 0xFF,        // pi
            '\r' => 13,
            '\n' => 13,
            _ => null
        };
    }

    private static byte[] PetsciiFromText(string text)
    {
        var list = new List<byte>(text.Length);
        bool lastWasCr = false;
        foreach (char c in text)
        {
            if (c == '\n' && lastWasCr) { lastWasCr = false; continue; }  // CRLF → one RETURN
            lastWasCr = c == '\r';
            if (PetsciiFromChar(c) is byte p) list.Add(p);
        }
        return list.ToArray();
    }

    private static byte[]? MapKeyToSequence(Key key, KeyModifiers modifiers)
    {
        // Ctrl+A through Ctrl+Z -> 0x01-0x1A
        if (modifiers == KeyModifiers.Control && key >= Key.A && key <= Key.Z)
        {
            return [(byte)(key - Key.A + 1)];
        }

        // Special keys -> ANSI escape sequences
        return key switch
        {
            Key.Up => "\x1b[A"u8.ToArray(),
            Key.Down => "\x1b[B"u8.ToArray(),
            Key.Right => "\x1b[C"u8.ToArray(),
            Key.Left => "\x1b[D"u8.ToArray(),
            Key.Home => "\x1b[H"u8.ToArray(),
            Key.End => "\x1b[F"u8.ToArray(),
            Key.Insert => "\x1b[2~"u8.ToArray(),
            Key.Delete => "\x1b[3~"u8.ToArray(),
            Key.PageUp => "\x1b[5~"u8.ToArray(),
            Key.PageDown => "\x1b[6~"u8.ToArray(),
            Key.F1 => "\x1bOP"u8.ToArray(),
            Key.F2 => "\x1bOQ"u8.ToArray(),
            Key.F3 => "\x1bOR"u8.ToArray(),
            Key.F4 => "\x1bOS"u8.ToArray(),
            Key.F5 => "\x1b[15~"u8.ToArray(),
            Key.F6 => "\x1b[17~"u8.ToArray(),
            Key.F7 => "\x1b[18~"u8.ToArray(),
            Key.F8 => "\x1b[19~"u8.ToArray(),
            Key.F9 => "\x1b[20~"u8.ToArray(),
            Key.F10 => "\x1b[21~"u8.ToArray(),
            Key.F11 => "\x1b[23~"u8.ToArray(),
            Key.F12 => "\x1b[24~"u8.ToArray(),
            Key.Enter => [(byte)'\r'],
            Key.Tab => [(byte)'\t'],
            Key.Back => [(byte)0x08],
            Key.Escape => [(byte)0x1B],
            _ => null
        };
    }

    // ================================================================
    // Mouse Selection
    // ================================================================

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;

        if (props.IsRightButtonPressed)
        {
            if (_isSelecting)
            {
                CancelSelection();
                e.Handled = true;
            }
            base.OnPointerPressed(e);
            return;
        }

        if (!props.IsLeftButtonPressed)
        {
            base.OnPointerPressed(e);
            return;
        }

        // Click returns to live view when scrolled back
        if (_scrollOffset > 0)
        {
            ReturnToLive();
            e.Handled = true;
            base.OnPointerPressed(e);
            return;
        }

        e.Pointer.Capture(this);
        var pos = e.GetPosition(this);
        var (col, row) = ScreenToCell(pos.X, pos.Y);
        if (col < 0) { base.OnPointerPressed(e); return; }

        _isSelecting = true;
        _isDragging = true;
        _selAnchorCol = col;
        _selAnchorRow = row;
        _selEndCol = col;
        _selEndRow = row;
        NormalizeSelection();
        SelectionChanged?.Invoke(true);
        InvalidateVisual();
        e.Handled = true;
        base.OnPointerPressed(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_isDragging && _isSelecting)
        {
            var pos = e.GetPosition(this);
            var (col, row) = ScreenToCell(pos.X, pos.Y);
            col = Math.Clamp(col, 0, _cols - 1);
            row = Math.Clamp(row, 0, _rows - 1);
            _selEndCol = col;
            _selEndRow = row;
            NormalizeSelection();
            InvalidateVisual();
            e.Handled = true;
        }
        else
        {
            // Show hand cursor when hovering over a URL
            var pos = e.GetPosition(this);
            var (col, row) = ScreenToCell(pos.X, pos.Y);
            Cursor = (col >= 0 && IsCellInUrl(row, col)) ? new Cursor(StandardCursorType.Hand) : null;
            // A board's OSC 8 link shows where it really goes, whatever its text says (12.62)
            string? linkTip = col >= 0 ? OscLinkAtCell(row, col) : null;
            if (!Equals(ToolTip.GetTip(this), linkTip))
            {
                ToolTip.SetTip(this, linkTip);
                ToolTip.SetIsOpen(this, linkTip != null);
            }
        }
        base.OnPointerMoved(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_isDragging)
        {
            _isDragging = false;
            e.Pointer.Capture(null);

            // If anchor == end (single click, no drag), check for URL click
            if (_selAnchorCol == _selEndCol && _selAnchorRow == _selEndRow)
            {
                int clickRow = _selAnchorRow;
                int clickCol = _selAnchorCol;
                CancelSelection();

                // A board's OSC 8 link, else a URL in the text
                var oscLink = OscLinkAtCell(clickRow, clickCol);
                var url = oscLink == null ? GetUrlAtCell(clickRow, clickCol) : null;
                if (oscLink != null)
                    OscLinkActivated?.Invoke(oscLink);
                else if (url != null)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                    }
                    catch { /* ignore launch failures */ }
                }

                // Re-focus for keyboard input
                Focus();
            }
            e.Handled = true;
        }
        base.OnPointerReleased(e);
    }

    private (int col, int row) ScreenToCell(double screenX, double screenY)
    {
        if (_bitmap == null) return (-1, -1);

        var (offsetX, _, _, scaleX, scaleY) = ComputeRenderRect();

        double cellW = FontWidth * scaleX;
        double cellH = _fontDataHeight * scaleY;

        int col = (int)((screenX - offsetX) / cellW);
        int row = (int)(screenY / cellH);

        col = Math.Clamp(col, 0, _cols - 1);
        row = Math.Clamp(row, 0, _rows - 1);
        return (col, row);
    }

    private void NormalizeSelection()
    {
        _selMinCol = Math.Min(_selAnchorCol, _selEndCol);
        _selMinRow = Math.Min(_selAnchorRow, _selEndRow);
        _selMaxCol = Math.Max(_selAnchorCol, _selEndCol);
        _selMaxRow = Math.Max(_selAnchorRow, _selEndRow);
    }

    // ================================================================
    // Public Selection API
    // ================================================================

    public void CancelSelection()
    {
        if (!_isSelecting) return;
        _isSelecting = false;
        _isDragging = false;
        _selAnchorCol = _selAnchorRow = -1;
        _selEndCol = _selEndRow = -1;
        _selMinCol = _selMinRow = _selMaxCol = _selMaxRow = -1;
        SelectionChanged?.Invoke(false);
        InvalidateVisual();
    }

    public void SelectAll()
    {
        _isSelecting = true;
        _selAnchorCol = 0;
        _selAnchorRow = 0;
        _selEndCol = _cols - 1;
        _selEndRow = _rows - 1;
        NormalizeSelection();
        SelectionChanged?.Invoke(true);
        InvalidateVisual();
    }

    public string? GetSelectedText()
    {
        if (!_isSelecting || _selMinCol < 0) return null;

        int[] buffer;
        int bufCols = _cols;

        if (_scrollOffset == 0)
        {
            // Live view: use the cached screen buffer
            buffer = _prevScreen ?? Array.Empty<int>();
            if (buffer.Length == 0) return null;
        }
        else
        {
            // Scrollback view: read from native
            int lineCount = _selMaxRow - _selMinRow + 1;
            buffer = new int[lineCount * _cols];
            int offset = _scrollOffset - _selMinRow;
            NativeBridge.syncterm_get_scrollback_buffer(offset, lineCount, buffer, buffer.Length);
        }

        var sb = new StringBuilder();
        int startRow = _scrollOffset == 0 ? _selMinRow : 0;
        int endRow = _scrollOffset == 0 ? _selMaxRow : (_selMaxRow - _selMinRow);

        for (int row = startRow; row <= endRow; row++)
        {
            if (row > startRow) sb.Append('\n');
            var line = new StringBuilder();
            for (int col = _selMinCol; col <= _selMaxCol; col++)
            {
                int idx = row * bufCols + col;
                if (idx >= buffer.Length) break;
                int ch = buffer[idx] & 0xFF;
                line.Append(ch < Cp437ToUnicode.Length ? Cp437ToUnicode[ch] : ' ');
            }
            // Trim trailing spaces
            string trimmed = line.ToString().TrimEnd();
            sb.Append(trimmed);
        }

        return sb.ToString();
    }

    private async Task PasteFromClipboardAsync()
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null) return;
            var text = await clipboard.GetTextAsync();
            if (!string.IsNullOrEmpty(text))
            {
                var bytes = PetsciiMode ? PetsciiFromText(text) : System.Text.Encoding.UTF8.GetBytes(text);
                if (_scrollOffset > 0) ReturnToLive();
                if (_isSelecting) CancelSelection();
                InputGenerated?.Invoke(bytes);
            }
        }
        catch { /* clipboard may be locked */ }
    }

    public async Task<bool> CopySelectionToClipboardAsync()
    {
        string? text = GetSelectedText();
        if (string.IsNullOrEmpty(text)) return false;
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(text);
                CancelSelection();
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    // ================================================================
    // Background Image Underlay (TAP+ bgshow/bgset/bghide)
    // ================================================================

    public void SetBackgroundActive(bool active)
    {
        if (_bgActive == active) return;
        _bgActive = active;
        _prevScreen = null;
        _glyphCache.Clear();
        InvalidateVisual();
    }

    public void SetBackgroundRegion(int r1, int c1, int r2, int c2)
    {
        _bgRow1 = r1;
        _bgCol1 = c1;
        _bgRow2 = r2;
        _bgCol2 = c2;
        if (_bgActive)
        {
            _prevScreen = null;
            _glyphCache.Clear();
            InvalidateVisual();
        }
    }

    // ================================================================
    // Tile Atlas System (TAP+ tileset/tilemap/tile)
    // ================================================================

    public void SetTileAtlas(Bitmap atlas, int spriteW, int spriteH)
    {
        // Store the atlas bitmap for DrawingContext-based sprite rendering
        _tileAtlasBitmap = atlas;
        _tileAtlasWidth = atlas.PixelSize.Width;
        _tileAtlasHeight = atlas.PixelSize.Height;
        _tileSpriteW = spriteW;
        _tileSpriteH = spriteH;
        _tileAtlasCols = _tileAtlasWidth / Math.Max(spriteW, 1);

        // Auto-detect tile scale from sprite dimensions:
        // 8x16 = 1x, 16x32 = 2x, 32x64 = 4x
        if (spriteW >= 32 && spriteH >= 64)
            _tileScale = 4;
        else if (spriteW >= 16 && spriteH >= 32)
            _tileScale = 2;
        else
            _tileScale = 1;
    }

    public void SetTileRegion(int r1, int c1, int r2, int c2)
    {
        _tileRow1 = r1;
        _tileCol1 = c1;
        _tileRow2 = r2;
        _tileCol2 = c2;

        int regionRows = r2 - r1 + 1;
        int regionCols = c2 - c1 + 1;
        _tileGrid = new int[regionRows, regionCols];
        for (int r = 0; r < regionRows; r++)
            for (int c = 0; c < regionCols; c++)
                _tileGrid[r, c] = -1;

        // Create tile RenderTargetBitmap covering the tile region at scaled resolution
        int bmpW = regionCols * FontWidth * _tileScale;
        int bmpH = regionRows * _fontDataHeight * _tileScale;
        _tileBitmap = new RenderTargetBitmap(new PixelSize(bmpW, bmpH), new Vector(96, 96));
        _tileBitmapDirty = false;
        _tileActive = true;

        _prevScreen = null;
        _glyphCache.Clear();
        InvalidateVisual();
    }

    public void SetTile(int row, int col, int spriteId)
    {
        if (_tileGrid == null || _tileAtlasBitmap == null) return;

        // Convert 1-based screen coords to 0-based tile grid coords
        int gr = row - _tileRow1;
        int gc = col - _tileCol1;
        if (gr < 0 || gr >= _tileGrid.GetLength(0) || gc < 0 || gc >= _tileGrid.GetLength(1))
            return;

        _tileGrid[gr, gc] = spriteId;
        if (!_tileBatchMode) { _tileBitmapDirty = true; InvalidateVisual(); }
    }

    public void SetTileRow(int row, int startCol, int[] spriteIds)
    {
        if (_tileGrid == null || _tileAtlasBitmap == null) return;

        int gr = row - _tileRow1;
        if (gr < 0 || gr >= _tileGrid.GetLength(0)) return;

        for (int i = 0; i < spriteIds.Length; i++)
        {
            int gc = startCol - _tileCol1 + i;
            if (gc < 0 || gc >= _tileGrid.GetLength(1)) continue;
            _tileGrid[gr, gc] = spriteIds[i];
        }
        if (!_tileBatchMode) { _tileBitmapDirty = true; InvalidateVisual(); }
    }

    public void FillTiles(int r1, int c1, int r2, int c2, int spriteId)
    {
        if (_tileGrid == null || _tileAtlasBitmap == null) return;

        for (int r = r1; r <= r2; r++)
        {
            int gr = r - _tileRow1;
            if (gr < 0 || gr >= _tileGrid.GetLength(0)) continue;
            for (int c = c1; c <= c2; c++)
            {
                int gc = c - _tileCol1;
                if (gc < 0 || gc >= _tileGrid.GetLength(1)) continue;
                _tileGrid[gr, gc] = spriteId;
            }
        }
        if (!_tileBatchMode) { _tileBitmapDirty = true; InvalidateVisual(); }
    }

    public void SetTileOpacity(double opacity)
    {
        _tileOpacity = Math.Clamp(opacity, 0.0, 1.0);
        if (_tileActive) InvalidateVisual();
    }

    public void TileBeginBatch()
    {
        _tileBatchMode = true;
    }

    public void TileEndBatch()
    {
        _tileBatchMode = false;
        if (_tileActive && _tileGrid != null)
        {
            _tileBitmapDirty = true;
            InvalidateVisual();
        }
    }

    public void TileRle(int row, int startCol, int[] tokens)
    {
        if (_tileGrid == null || _tileAtlasBitmap == null || tokens.Length < 2) return;

        int gr = row - _tileRow1;
        if (gr < 0 || gr >= _tileGrid.GetLength(0)) return;

        int gc = startCol - _tileCol1;
        int i = 0;
        while (i < tokens.Length - 1)
        {
            int id = tokens[i];
            int count = tokens[i + 1];
            for (int n = 0; n < count; n++)
            {
                if (gc >= 0 && gc < _tileGrid.GetLength(1))
                    _tileGrid[gr, gc] = id;
                gc++;
            }
            i += 2;
        }
        if (!_tileBatchMode) { _tileBitmapDirty = true; InvalidateVisual(); }
    }

    public void TileScroll(int dx, int dy)
    {
        if (_tileGrid == null) return;

        int rows = _tileGrid.GetLength(0);
        int cols = _tileGrid.GetLength(1);
        var newGrid = new int[rows, cols];
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                newGrid[r, c] = -1;

        for (int r = 0; r < rows; r++)
        {
            int srcR = r - dy;
            if (srcR < 0 || srcR >= rows) continue;
            for (int c = 0; c < cols; c++)
            {
                int srcC = c - dx;
                if (srcC < 0 || srcC >= cols) continue;
                newGrid[r, c] = _tileGrid[srcR, srcC];
            }
        }
        Array.Copy(newGrid, _tileGrid, rows * cols);

        if (!_tileBatchMode) { _tileBitmapDirty = true; InvalidateVisual(); }
    }

    public void TileCopy(int srcRow, int srcCol, int dstRow, int dstCol, int width, int height)
    {
        if (_tileGrid == null) return;

        int rows = _tileGrid.GetLength(0);
        int cols = _tileGrid.GetLength(1);
        int sr1 = srcRow - _tileRow1;
        int sc1 = srcCol - _tileCol1;
        int dr1 = dstRow - _tileRow1;
        int dc1 = dstCol - _tileCol1;

        var temp = new int[width * height];
        for (int r = 0; r < height; r++)
        {
            for (int c = 0; c < width; c++)
            {
                int sr = sr1 + r, sc = sc1 + c;
                temp[r * width + c] = (sr >= 0 && sr < rows && sc >= 0 && sc < cols)
                    ? _tileGrid[sr, sc] : -1;
            }
        }
        for (int r = 0; r < height; r++)
        {
            for (int c = 0; c < width; c++)
            {
                int dr = dr1 + r, dc = dc1 + c;
                if (dr >= 0 && dr < rows && dc >= 0 && dc < cols)
                    _tileGrid[dr, dc] = temp[r * width + c];
            }
        }
        if (!_tileBatchMode) { _tileBitmapDirty = true; InvalidateVisual(); }
    }

    public void TileSwap(int id1, int id2)
    {
        if (_tileGrid == null) return;

        int rows = _tileGrid.GetLength(0);
        int cols = _tileGrid.GetLength(1);
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                if (_tileGrid[r, c] == id1) _tileGrid[r, c] = id2;
                else if (_tileGrid[r, c] == id2) _tileGrid[r, c] = id1;
            }
        }
        if (!_tileBatchMode) { _tileBitmapDirty = true; InvalidateVisual(); }
    }

    public void SetTileScale(int scale)
    {
        // Clamp to valid values: 1, 2, or 4
        if (scale <= 1) _tileScale = 1;
        else if (scale <= 2) _tileScale = 2;
        else _tileScale = 4;

        // Rebuild tile bitmap at new scale if active
        if (_tileActive && _tileGrid != null)
        {
            RebuildTileBitmap();
            InvalidateVisual();
        }
    }

    public void ClearTiles()
    {
        _tileActive = false;
        _tileOpacity = 1.0;
        _tileScale = 1;
        _tileGrid = null;
        _tileBitmap = null;
        _tileBitmapDirty = false;
        _tileBatchMode = false;
        _prevScreen = null;
        _glyphCache.Clear();
        InvalidateVisual();
    }

    /// <summary>
    /// Rebuild the tile composite bitmap using DrawingContext to blit sprites
    /// from the atlas. This avoids needing raw pixel access to the atlas Bitmap.
    /// </summary>
    private void RebuildTileBitmap()
    {
        if (_tileGrid == null || _tileAtlasBitmap == null) return;

        int regionRows = _tileGrid.GetLength(0);
        int regionCols = _tileGrid.GetLength(1);
        int cellW = FontWidth * _tileScale;
        int cellH = _fontDataHeight * _tileScale;
        int bmpW = regionCols * cellW;
        int bmpH = regionRows * cellH;

        _tileBitmap = new RenderTargetBitmap(new PixelSize(bmpW, bmpH), new Vector(96, 96));

        using (var ctx = _tileBitmap.CreateDrawingContext())
        {
            for (int r = 0; r < regionRows; r++)
            {
                for (int c = 0; c < regionCols; c++)
                {
                    int spriteId = _tileGrid[r, c];
                    if (spriteId < 0) continue;

                    int spriteCol = spriteId % _tileAtlasCols;
                    int spriteRow = spriteId / _tileAtlasCols;
                    var srcRect = new Rect(
                        spriteCol * _tileSpriteW,
                        spriteRow * _tileSpriteH,
                        _tileSpriteW,
                        _tileSpriteH);
                    var dstRect = new Rect(
                        c * cellW,
                        r * cellH,
                        cellW,
                        cellH);

                    ctx.DrawImage(_tileAtlasBitmap, srcRect, dstRect);
                }
            }
        }
        _tileBitmapDirty = false;
    }

    // ================================================================
    // URL Detection and Click-to-Open
    // ================================================================

    /// <summary>
    /// Scan screen buffer for URLs and cache their cell regions.
    /// Called each frame before rendering.
    /// </summary>
    private void DetectUrls(int[] screenBuffer)
    {
        _detectedUrls.Clear();

        if (_cols <= 0 || _rows <= 0) return;

        // Build text representation of screen, one row at a time (no newlines)
        var sb = new StringBuilder(_cols * _rows);
        int cellCount = Math.Min(screenBuffer.Length, _cols * _rows);
        for (int i = 0; i < cellCount; i++)
        {
            int ch = screenBuffer[i] & 0xFF;
            char mapped = ch < Cp437ToUnicode.Length ? Cp437ToUnicode[ch] : ' ';
            sb.Append(mapped >= ' ' && mapped <= '~' ? mapped : ' ');
        }

        string screenText = sb.ToString();
        foreach (Match match in UrlPattern.Matches(screenText))
        {
            int startIdx = match.Index;
            int endIdx = startIdx + match.Length - 1;

            int startRow = startIdx / _cols;
            int startCol = startIdx % _cols;
            int endRow = endIdx / _cols;
            int endCol = endIdx % _cols;

            _detectedUrls.Add(new UrlRegion(startRow, startCol, endRow, endCol));
        }

        // The board's OSC 8 links (native/apc/osc_ext.c): underlined like a detected URL; hover shows the real address
        _oscLinkMask = null;
        if (_scrollOffset == 0 && OscExtras.LinksActive)
        {
            bool[]? mask = null;
            for (int i = 0; i < cellCount; i++)
            {
                if (!OscExtras.IsLinkCell(i % _cols, i / _cols, screenBuffer[i] & 0xFF)) continue;
                mask ??= new bool[cellCount];
                mask[i] = true;
            }
            _oscLinkMask = mask;
        }
    }

    /// <summary>Whether a 0-based cell of the live screen is part of a board's OSC 8 link.</summary>
    internal bool IsOscLinkCell(int col, int row)
    {
        if (_scrollOffset > 0 || _oscLinkMask == null || col < 0 || row < 0 || col >= _cols) return false;
        int i = row * _cols + col;
        return i < _oscLinkMask.Length && _oscLinkMask[i];
    }

    /// <summary>The OSC 8 link under a 0-based cell of the live screen, or null.</summary>
    private string? OscLinkAtCell(int row, int col)
    {
        if (!IsOscLinkCell(col, row) || _lastScreenBuffer == null) return null;
        int i = row * _cols + col;
        return i < _lastScreenBuffer.Length ? OscExtras.LinkAt(col, row, _lastScreenBuffer[i] & 0xFF) : null;
    }

    /// <summary>
    /// Check if a cell is part of a detected URL (0-based row/col).
    /// </summary>
    private bool IsCellInUrl(int row, int col)
    {
        if (IsOscLinkCell(col, row)) return true;
        for (int i = 0; i < _detectedUrls.Count; i++)
        {
            var region = _detectedUrls[i];
            if (region.StartRow == region.EndRow)
            {
                // Single row URL
                if (row == region.StartRow && col >= region.StartCol && col <= region.EndCol)
                    return true;
            }
            else
            {
                // Multi-row (wrapped) URL
                if (row == region.StartRow && col >= region.StartCol) return true;
                if (row == region.EndRow && col <= region.EndCol) return true;
                if (row > region.StartRow && row < region.EndRow) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Get the URL string at a given cell position, or null if no URL there.
    /// </summary>
    private string? GetUrlAtCell(int row, int col)
    {
        if (_lastScreenBuffer == null || _cols <= 0) return null;

        for (int i = 0; i < _detectedUrls.Count; i++)
        {
            var region = _detectedUrls[i];
            bool inUrl;
            if (region.StartRow == region.EndRow)
                inUrl = row == region.StartRow && col >= region.StartCol && col <= region.EndCol;
            else
            {
                inUrl = (row == region.StartRow && col >= region.StartCol)
                     || (row == region.EndRow && col <= region.EndCol)
                     || (row > region.StartRow && row < region.EndRow);
            }

            if (!inUrl) continue;

            // Extract the URL text from the screen buffer
            int startIdx = region.StartRow * _cols + region.StartCol;
            int endIdx = region.EndRow * _cols + region.EndCol;
            int len = endIdx - startIdx + 1;
            var urlSb = new StringBuilder(len);
            for (int j = startIdx; j <= endIdx && j < _lastScreenBuffer.Length; j++)
            {
                int ch = _lastScreenBuffer[j] & 0xFF;
                char mapped = ch < Cp437ToUnicode.Length ? Cp437ToUnicode[ch] : ' ';
                urlSb.Append(mapped);
            }

            string url = urlSb.ToString().Trim();
            // Ensure it has a scheme for Process.Start
            if (url.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                url = "https://" + url;
            return url;
        }

        return null;
    }

    private bool IsCellInUnderlayRegion(int col, int row)
    {
        // During scrollback, no cells are in underlay regions -- render all with opaque backgrounds
        if (_scrollOffset > 0)
            return false;

        // col, row are 0-based
        if (_tileActive && col >= _tileCol1 - 1 && col <= _tileCol2 - 1 &&
            row >= _tileRow1 - 1 && row <= _tileRow2 - 1)
            return true;
        if (_bgActive && col >= _bgCol1 - 1 && col <= _bgCol2 - 1 &&
            row >= _bgRow1 - 1 && row <= _bgRow2 - 1)
            return true;
        return false;
    }
}
