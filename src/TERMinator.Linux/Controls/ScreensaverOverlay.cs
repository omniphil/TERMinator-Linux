using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace TERMinator.Linux.Controls;

/// <summary>
/// Idle screensaver (§8): a slideshow of the phonebook boards' snapshots hopping around the screen, each with its name
/// decrypting underneath and a glitch burst on every change, over faint digital rain, with a fake GibsonCoin miner
/// hopping about behind it and big credits scrolling along the bottom. The pieces hop rather than slide, frames are
/// capped at 30 fps, and after a while it fades to black and stops altogether, so an idle client doesn't spin the
/// fans up. Any key (forwarded by the host through
/// <see cref="Dismiss"/>), a click or real mouse movement ends it; <see cref="Finished"/> is raised once it has faded.
///
/// Ported from the Windows client's ScreensaverOverlay with its values intact, but built differently on purpose: on
/// Windows every piece is a WPF element with a BitmapCache so the glow and the text are rasterised once and only slid
/// around. Avalonia has no bitmap cache, so the parts that cost something (the rain, the frame's glow, the credits
/// with their glow) are pre-rendered to bitmaps once, and each piece is its own small custom-drawn layer sized to what
/// it shows: the rain only moves (a transform, never redrawn), the picture is only redrawn when its slide, caption or
/// glitch changes, and the per-frame redraws are confined to the credits strip and the miner box, so Avalonia's
/// dirty-region rendering never has to repaint the whole window for them. The host also hides the home screen
/// underneath while this is up, which was the biggest win on Windows too.
/// </summary>
public sealed class ScreensaverOverlay : Panel
{
    public event EventHandler? Finished;

    private const double SlideSeconds = 6;
    private const double MouseMoveThreshold = 12;
    private const double RainScrollSpeed = 40;     // px per second
    private const double RainOpacity = 0.18;
    private const double MinerJumpInterval = 4;    // seconds; at 5, 9, 13, ... against the slides' 0, 6, 12, ...
    private static readonly TimeSpan FrameInterval = TimeSpan.FromSeconds(1.0 / 30);
    private static readonly TimeSpan SleepAfter = TimeSpan.FromMinutes(15);
    private const double CreditsBandHeight = 64;
    private const double CreditsBottomMargin = 12;
    private const double CreditsSpeed = 110;
    private const double CreditsTopInBand = 10;
    private const double FrameWidth = 448;         // 4:3, like the terminal's picture (70% of 640x480)
    private const double FrameHeight = 336;
    private const double CaptionGap = 13;
    private const double CaptionFontSize = 16;
    private const double BlockMargin = 40;
    private const double MinerMargin = 30;
    private const double GlowPad = 26;             // room around the frame for its pre-rendered glow
    private const double CreditsPad = 24;          // ditto for the credits glow
    private const string DecryptGlyphs = "!<>-_\\/[]{}=+*^?#%&$@01";

    private const string CreditsTail =
        " - Shoddily slapped together by JSONBourne, Artwork by aNACHRONiST (aNSt).   " +
        "No, we aren't really mining GibsonCoins, Sheesh, who do you think we are? :)";

    private static readonly FontFamily MonoFont = new("JetBrains Mono, DejaVu Sans Mono, monospace");
    private static readonly Color Cyan = Color.FromRgb(0x55, 0xFF, 0xFF);
    private static readonly IBrush CyanBrush = new SolidColorBrush(Cyan);
    private static readonly IPen CyanPen = new Pen(CyanBrush, 1);
    private static readonly IBrush MagentaBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x55, 0xFF));
    private static readonly IBrush CreditsGreenBrush = new SolidColorBrush(Color.FromRgb(0x55, 0xDD, 0x55));

    private readonly (string Name, Bitmap Image)[] _slides;
    // The layers, back to front. None of them is hit-testable: clicks and movement land on the panel's background.
    private readonly DrawLayer _rainLayer;
    private readonly DrawLayer _minerLayer;
    private readonly DrawLayer _blockLayer;
    private readonly DrawLayer _creditsLayer;
    private readonly DrawLayer _sleepLayer;
    private readonly TranslateTransform _rainMove = new();
    private readonly TranslateTransform _minerMove = new();
    private readonly TranslateTransform _blockMove = new();
    private (int Index, string Caption, double JitterX, double ImageOpacity, double Scale) _blockDrawn;
    private readonly GibsonMinerPanel _miner = new();
    private readonly Random _rng = new();
    private readonly Stopwatch _clock = new();
    private DispatcherTimer? _timer;
    private FxTween? _fade;
    private bool _started, _closing, _asleep;

    // Pre-rendered pieces (rebuilt when the window size changes)
    private Bitmap? _rain;
    private Bitmap? _frameGlow;
    private Bitmap? _credits;
    private double _creditsWidth;
    private double _rainForWidth, _rainForHeight;

    // Motion
    private double _lastTime;
    private double _rainShift;
    private double _driftX, _driftY;
    private bool _artworkJumpPending = true; // the picture hops to a new spot with each slide (under its glitch burst)
    private double _minerX, _minerY;
    private double _nextMinerJump = 1 + MinerJumpInterval;
    private bool _minerPlaced;
    private double _creditsX;
    private bool _creditsStarted;

    // Slideshow
    private int _index = -1;
    private double _slideStart;
    private string _captionShown = string.Empty;
    private double _nextCaptionChurn;
    private double _glitchStart = double.NegativeInfinity;
    private int _glitchFrame = -1;
    private double _jitterX;
    private double _imageOpacity = 1;
    private FormattedText? _caption;
    private string? _captionFor;
    private double _captionHeight;

    private Point? _pointerOrigin;

    public ScreensaverOverlay(IEnumerable<(string Name, Bitmap Image)> slides)
    {
        _slides = slides.ToArray();
        Background = new SolidColorBrush(Color.FromRgb(0x02, 0x02, 0x04));
        Cursor = new Cursor(StandardCursorType.None);
        ClipToBounds = true;
        Opacity = 0;

        // Panel.Render is sealed, so the drawing lives in child layers; the panel itself keeps the background (which
        // is also what makes the whole saver hit-testable for the click / movement dismiss).
        _rainLayer = new DrawLayer(PaintRain) { Opacity = RainOpacity, RenderTransform = _rainMove };
        // The miner sits one plane behind the artwork, which passes over it
        _minerLayer = new DrawLayer(dc => _miner.Draw(dc, 0, 0))
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            RenderTransform = _minerMove,
            IsVisible = false
        };
        _blockLayer = new DrawLayer(PaintBlock)
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            RenderTransform = _blockMove,
            ClipToBounds = true
        };
        _creditsLayer = new DrawLayer(PaintCredits)
        {
            Height = CreditsBandHeight,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, CreditsBottomMargin),
            ClipToBounds = true
        };
        _sleepLayer = new DrawLayer(dc => dc.FillRectangle(Brushes.Black, new Rect(Bounds.Size)))
        {
            Opacity = 0,
            IsVisible = false
        };
        Children.AddRange([_rainLayer, _minerLayer, _blockLayer, _creditsLayer, _sleepLayer]);

        AttachedToVisualTree += (_, _) => Start();
        DetachedFromVisualTree += (_, _) => StopTimer();
        PointerPressed += (_, e) =>
        {
            Dismiss();
            e.Handled = true;
        };
        PointerMoved += OnPointerMoved;
    }

    // Never influence the window's size-to-content fit: the overlay just fills whatever space the window has.
    protected override Size MeasureOverride(Size availableSize)
    {
        base.MeasureOverride(availableSize);
        return default;
    }

    /// <summary>Fades out, then raises <see cref="Finished"/>.</summary>
    public void Dismiss()
    {
        if (_closing)
            return;
        _closing = true;

        double from = Opacity;
        _fade = FxTween.Run(TimeSpan.FromMilliseconds(250), t => Opacity = from * (1 - t), completed: () =>
        {
            StopTimer();
            Finished?.Invoke(this, EventArgs.Empty);
        });
    }

    private void Start()
    {
        if (_started)
            return;
        _started = true;

        _clock.Restart();
        NextSlide(0);
        FxTween.Run(TimeSpan.FromMilliseconds(600), t => { if (!_closing) Opacity = t; });

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = FrameInterval };
        _timer.Tick += (_, _) => Frame();
        _timer.Start();
    }

    private void StopTimer()
    {
        _timer?.Stop();
        _timer = null;
        _clock.Stop();
    }

    /// <summary>One frame of motion: everything is time-driven, with dt capped so a stall never teleports anything.</summary>
    private void Frame()
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 0 || h <= 0)
            return;

        // Built here rather than in the paint pass: these are render-target bitmaps of their own, and the timer tick
        // is a plain UI-thread callback
        EnsureAssets(w, h);

        double now = _clock.Elapsed.TotalSeconds;
        double dt = Math.Min(0.1, now - _lastTime);
        _lastTime = now;

        if (now >= SleepAfter.TotalSeconds)
        {
            GoToSleep();
            return;
        }
        // Nothing to see while the window is minimized
        if (TopLevel.GetTopLevel(this) is Window { WindowState: WindowState.Minimized })
            return;

        // The rain is one still frame scrolling down, wrapping every screen height: just a transform, never redrawn
        _rainShift = (_rainShift + RainScrollSpeed * dt) % Math.Max(1, h);
        _rainMove.Y = _rainShift;

        // New slide every six seconds, in random order
        if (now - _slideStart >= SlideSeconds)
            NextSlide(now);

        // Caption: the board name decrypting over 0.7 s
        double age = now - _slideStart;
        string name = _slides.Length > 0 ? _slides[Math.Max(0, _index)].Name : string.Empty;
        if (age >= 0.7)
        {
            _captionShown = name;
        }
        else if (now >= _nextCaptionChurn)
        {
            _nextCaptionChurn = now + 0.03;
            _captionShown = Churn(name, age / 0.7);
        }

        // Glitch burst: six frames of horizontal jitter with the picture flickering
        int glitch = (int)((now - _glitchStart) / 0.045);
        if (glitch is >= 0 and < 6)
        {
            if (glitch != _glitchFrame)
            {
                _glitchFrame = glitch;
                _jitterX = _rng.Next(-13, 14);
                _imageOpacity = (glitch + 1) % 2 == 0 ? 0.4 : 1.0;
            }
        }
        else
        {
            _jitterX = 0;
            _imageOpacity = 1;
        }

        // The credits: start just off the right edge, scroll left, and come round again once fully off the left
        if (_creditsWidth > 0)
        {
            if (!_creditsStarted)
            {
                _creditsX = w;
                _creditsStarted = true;
            }
            _creditsX -= CreditsSpeed * dt;
            if (_creditsX < -_creditsWidth)
                _creditsX = w;
            _creditsLayer.InvalidateVisual(); // just the strip along the bottom
        }

        // The picture hops with each new slide, the miner on its own clock (offset so the two never move together);
        // each picks the spot that overlaps the other the least, so the miner isn't left hidden behind the artwork.
        // Both are centered in the same space above the credits band, so their offsets compare directly.
        var block = BlockSize();
        var minerSize = _miner.Size;
        if (_artworkJumpPending)
        {
            _artworkJumpPending = false;
            (_driftX, _driftY) = Jump(w, h, BlockMargin, block.Width, block.Height,
                                      _minerX, _minerY, minerSize.Width, minerSize.Height);
        }
        if (!_minerPlaced || now >= _nextMinerJump)
        {
            if (_minerPlaced)
                _nextMinerJump += MinerJumpInterval;
            _minerPlaced = true;
            (_minerX, _minerY) = Jump(w, h, MinerMargin, minerSize.Width, minerSize.Height,
                                      _driftX, _driftY, block.Width, block.Height);
        }
        _miner.Tick(now, dt);

        PlaceLayers(w, h, block.Width, block.Height, minerSize);
    }

    /// <summary>
    /// Moves the miner and picture layers into place, and redraws the picture only when something in it changed (the
    /// slide, the decrypting caption, a glitch frame, a resize): between those it is left exactly as it was drawn.
    /// </summary>
    private void PlaceLayers(double w, double h, double blockWidth, double blockHeight, Size minerSize)
    {
        double centerY = h / 2 - (CreditsBandHeight + CreditsBottomMargin) / 2; // same for both: equal top/bottom margins

        if (_minerPlaced)
        {
            if (_minerLayer.Width != minerSize.Width || _minerLayer.Height != minerSize.Height)
            {
                _minerLayer.Width = minerSize.Width;
                _minerLayer.Height = minerSize.Height;
            }
            _minerMove.X = w / 2 - minerSize.Width / 2 + _minerX;
            _minerMove.Y = centerY - minerSize.Height / 2 + _minerY;
            _minerLayer.IsVisible = true;
            _minerLayer.InvalidateVisual(); // the share bar moves every frame; it's a small box
        }

        double scale = blockWidth / FrameWidth;
        double pad = GlowPad * scale;
        double layerWidth = blockWidth + 2 * pad, layerHeight = blockHeight + 2 * pad;
        if (_blockLayer.Width != layerWidth || _blockLayer.Height != layerHeight)
        {
            _blockLayer.Width = layerWidth;
            _blockLayer.Height = layerHeight;
        }
        _blockMove.X = w / 2 - blockWidth / 2 + _driftX - pad;
        _blockMove.Y = centerY - blockHeight / 2 + _driftY - pad;

        var drawn = (_index, _captionShown, _jitterX, _imageOpacity, scale);
        if (drawn != _blockDrawn)
        {
            _blockDrawn = drawn;
            _blockLayer.InvalidateVisual();
        }
    }

    /// <summary>
    /// A random offset (from the centre of the free space above the credits) for a box of the given size, the best of
    /// a few tries for staying clear of the other box.
    /// </summary>
    private (double X, double Y) Jump(double w, double h, double margin, double width, double height,
                                      double otherX, double otherY, double otherWidth, double otherHeight)
    {
        double rangeX = Math.Max(0, (w - 2 * margin - width) / 2);
        double rangeY = Math.Max(0, (h - 2 * margin - CreditsBandHeight - CreditsBottomMargin - height) / 2);
        double bestX = 0, bestY = 0, bestOverlap = double.MaxValue;
        for (int attempt = 0; attempt < 8 && bestOverlap > 0; attempt++)
        {
            double x = (_rng.NextDouble() * 2 - 1) * rangeX;
            double y = (_rng.NextDouble() * 2 - 1) * rangeY;
            double overlapX = (width + otherWidth) / 2 - Math.Abs(x - otherX);
            double overlapY = (height + otherHeight) / 2 - Math.Abs(y - otherY);
            double overlap = Math.Max(0, overlapX) * Math.Max(0, overlapY);
            if (overlap < bestOverlap)
            {
                bestOverlap = overlap;
                bestX = x;
                bestY = y;
            }
        }
        return (Math.Round(bestX), Math.Round(bestY));
    }

    /// <summary>Fades to black and stops the frame timer: nothing left to draw until the user comes back.</summary>
    private void GoToSleep()
    {
        if (_asleep || _closing)
            return;
        _asleep = true;
        StopTimer();
        _sleepLayer.IsVisible = true;
        FxTween.Run(TimeSpan.FromSeconds(1.5), t => _sleepLayer.Opacity = t, completed: () =>
        {
            // Hide what's under the black so it isn't composed any more
            foreach (var layer in new Control[] { _rainLayer, _minerLayer, _blockLayer, _creditsLayer })
                layer.IsVisible = false;
        });
    }

    private void NextSlide(double now)
    {
        _slideStart = now;
        _artworkJumpPending = true;
        _glitchStart = now;
        _glitchFrame = -1;
        _nextCaptionChurn = 0;
        if (_slides.Length == 0)
            return;
        // Random order, never the same board twice in a row
        _index = _slides.Length == 1 ? 0 : (_index + 1 + _rng.Next(_slides.Length - 1)) % _slides.Length;
    }

    /// <summary>The decrypt effect's churned text: locked in left to right, whitespace never scrambled.</summary>
    private string Churn(string text, double progress)
    {
        if (text.Length == 0)
            return text;
        int locked = (int)(Math.Clamp(progress, 0, 1) * text.Length);
        var buffer = new char[text.Length];
        for (int i = 0; i < buffer.Length; i++)
            buffer[i] = i < locked || char.IsWhiteSpace(text[i])
                ? text[i]
                : DecryptGlyphs[_rng.Next(DecryptGlyphs.Length)];
        return new string(buffer);
    }

    // ================================================================ drawing

    /// <summary>Both copies of the rain frame, stacked; the layer's transform does the scrolling.</summary>
    private void PaintRain(DrawingContext dc)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (_rain == null || w <= 0 || h <= 0)
            return;
        dc.DrawImage(_rain, new Rect(0, 0, w, h));
        dc.DrawImage(_rain, new Rect(0, -h, w, h));
    }

    /// <summary>
    /// The BBS picture and its caption, scaled down to fit a small window, in the block layer's own coordinates (the
    /// glow padding around it, then the frame).
    /// </summary>
    private void PaintBlock(DrawingContext dc)
    {
        double scale = _blockDrawn.Scale;
        if (scale <= 0)
            return;

        using (dc.PushTransform(Matrix.CreateTranslation(GlowPad, GlowPad) * Matrix.CreateScale(scale, scale)))
        {
            if (_frameGlow != null)
                dc.DrawImage(_frameGlow, new Rect(-GlowPad, -GlowPad, FrameWidth + 2 * GlowPad, FrameHeight + 2 * GlowPad));

            dc.DrawRectangle(Brushes.Black, CyanPen, new Rect(0.5, 0.5, FrameWidth - 1, FrameHeight - 1));

            if (_index >= 0 && _index < _slides.Length)
            {
                var picture = new Rect(1, 1, FrameWidth - 2, FrameHeight - 2);
                using (dc.PushClip(picture))
                using (dc.PushOpacity(_imageOpacity))
                using (dc.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None }))
                    dc.DrawImage(_slides[_index].Image, picture.Translate(new Vector(_jitterX, 0)));
            }

            var caption = Caption();
            if (caption != null)
                dc.DrawText(caption, new Point((FrameWidth - caption.Width) / 2, FrameHeight + CaptionGap));
        }
    }

    /// <summary>The credits line in the strip's own coordinates (the strip clips it).</summary>
    private void PaintCredits(DrawingContext dc)
    {
        if (_credits == null)
            return;
        dc.DrawImage(_credits, new Rect(_creditsX - CreditsPad, CreditsTopInBand - CreditsPad,
                                        _credits.Size.Width, _credits.Size.Height));
    }

    private FormattedText? Caption()
    {
        if (_captionShown.Length == 0)
            return null;
        if (_caption == null || _captionFor != _captionShown)
        {
            _captionFor = _captionShown;
            _caption = new FormattedText(_captionShown, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                         new Typeface(MonoFont, FontStyle.Normal, FontWeight.Bold),
                                         CaptionFontSize, CyanBrush);
        }
        return _caption;
    }

    /// <summary>The picture + caption block at its drawn size (shrunk to fit a small window, never enlarged).</summary>
    private (double Width, double Height) BlockSize()
    {
        if (_captionHeight <= 0)
        {
            _captionHeight = new FormattedText("Mg", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                               new Typeface(MonoFont, FontStyle.Normal, FontWeight.Bold),
                                               CaptionFontSize, CyanBrush).Height;
        }
        double naturalHeight = FrameHeight + CaptionGap + _captionHeight;
        double availableWidth = Math.Max(1, Bounds.Width - 2 * BlockMargin);
        double availableHeight = Math.Max(1, Bounds.Height - 2 * BlockMargin - CreditsBandHeight - CreditsBottomMargin);
        double scale = Math.Min(1, Math.Min(availableWidth / FrameWidth, availableHeight / naturalHeight));
        return (FrameWidth * scale, naturalHeight * scale);
    }

    /// <summary>
    /// Builds (or rebuilds after a resize) the pieces that are too expensive to redraw every frame: the tiling rain
    /// frame, the frame's cyan glow and the credits line with its glow.
    /// </summary>
    private void EnsureAssets(double w, double h)
    {
        double scaling = VisualRoot?.RenderScaling ?? 1.0;
        if (scaling <= 0 || double.IsNaN(scaling)) scaling = 1.0;

        if (_rain == null || Math.Abs(_rainForWidth - w) > 1 || Math.Abs(_rainForHeight - h) > 1)
        {
            try
            {
                // The old frame is only dropped, never disposed: a queued render may still be reading it, and the
                // bitmap's own finaliser releases the native surface once nothing points at it.
                _rain = DigitalRain.RenderStill(w, h, scaling);
                _rainForWidth = w;
                _rainForHeight = h;
                _rainLayer.InvalidateVisual();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Screensaver rain could not be rendered: {ex.Message}");
                _rain = null;
            }
        }

        _frameGlow ??= RenderFrameGlow(scaling);
        _credits ??= RenderCredits(scaling);
    }

    /// <summary>
    /// The cyan halo around the picture frame, once. Windows gets it from a DropShadowEffect (blur 21, 45%); Avalonia's
    /// DrawingContext has no effects, so it is faked with nested translucent rings — the same soft glow, and free per
    /// frame because it is a bitmap.
    /// </summary>
    private static Bitmap? RenderFrameGlow(double scaling)
    {
        try
        {
            double w = FrameWidth + 2 * GlowPad, h = FrameHeight + 2 * GlowPad;
            var bitmap = new RenderTargetBitmap(
                new PixelSize((int)Math.Ceiling(w * scaling), (int)Math.Ceiling(h * scaling)),
                new Vector(96 * scaling, 96 * scaling));
            using (var dc = bitmap.CreateDrawingContext())
            {
                const int rings = 13;
                for (int i = rings; i >= 1; i--)
                {
                    double spread = i * (GlowPad / rings);
                    var brush = new SolidColorBrush(Cyan, 0.05);
                    dc.DrawRectangle(brush, null,
                        new Rect(GlowPad - spread, GlowPad - spread, FrameWidth + 2 * spread, FrameHeight + 2 * spread),
                        spread * 0.5, spread * 0.5);
                }
            }
            return bitmap;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Screensaver frame glow could not be rendered: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The scrolling credits, once: "TERM" in the command line prompt's magenta, "inator" in its cyan and the rest
    /// green, bold 34, with a soft cyan glow around the whole line.
    /// </summary>
    private Bitmap? RenderCredits(double scaling)
    {
        try
        {
            var bold = new Typeface(MonoFont, FontStyle.Normal, FontWeight.Bold);
            FormattedText Run(string text, IBrush brush) =>
                new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, bold, 34, brush);

            var term = Run("TERM", MagentaBrush);
            var inator = Run("inator", CyanBrush);
            var tail = Run(CreditsTail, CreditsGreenBrush);
            var glow = Run("TERMinator" + CreditsTail, CyanBrush);

            _creditsWidth = term.Width + inator.Width + tail.Width;
            double w = _creditsWidth + 2 * CreditsPad;
            double h = Math.Max(term.Height, tail.Height) + 2 * CreditsPad;

            var bitmap = new RenderTargetBitmap(
                new PixelSize((int)Math.Ceiling(w * scaling), (int)Math.Ceiling(h * scaling)),
                new Vector(96 * scaling, 96 * scaling));
            using (var dc = bitmap.CreateDrawingContext())
            {
                var origin = new Point(CreditsPad, CreditsPad);

                // Fake the DropShadowEffect (blur 14, 50%) with a ring of faint copies
                using (dc.PushOpacity(0.5))
                {
                    foreach (double radius in new[] { 4.0, 8.0, 12.0 })
                    {
                        for (int step = 0; step < 8; step++)
                        {
                            double angle = step * Math.PI / 4;
                            using (dc.PushOpacity(0.07))
                                dc.DrawText(glow, origin + new Vector(Math.Cos(angle) * radius, Math.Sin(angle) * radius));
                        }
                    }
                }

                dc.DrawText(term, origin);
                dc.DrawText(inator, origin + new Vector(term.Width, 0));
                dc.DrawText(tail, origin + new Vector(term.Width + inator.Width, 0));
            }
            return bitmap;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Screensaver credits could not be rendered: {ex.Message}");
            return null;
        }
    }

    /// <summary>The saver's picture. A child control, because <see cref="Panel"/> seals its own Render.</summary>
    private sealed class DrawLayer(Action<DrawingContext> draw) : Control
    {
        public override void Render(DrawingContext dc) => draw(dc);
    }

    /// <summary>Only real movement counts: the first event just records where the pointer was.</summary>
    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var position = e.GetPosition(this);
        if (_pointerOrigin is not Point origin)
        {
            _pointerOrigin = position;
            return;
        }
        if (Math.Abs(position.X - origin.X) + Math.Abs(position.Y - origin.Y) > MouseMoveThreshold)
            Dismiss();
    }
}
