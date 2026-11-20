using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TERMinator.Linux.Controls;
using TERMinator.Linux.Models;

namespace TERMinator.Linux.Views;

public partial class HomeView : UserControl
{
    private DispatcherTimer? _marqueeTimer;

    // True while the home screen's animations run (marquee, glitches, static, scanline, CRT thumbnails, facts ticker).
    // They only run while the view is actually seen: attached, not covered by the screensaver (MainWindow calls
    // SetCovered, since hiding ContentArea doesn't change this view's own IsVisible) and the window not minimized.
    // Every glitch/burst re-schedule checks this, so a one-shot timer already in flight can't restart them.
    private bool _animating;
    private bool _covered;
    private Window? _hostWindow;
    private readonly Stopwatch _marqueeClock = new();
    private double _textWidth;
    private double _canvasWidth;

    private readonly Random _rng = new();

    // Original marquee text (immutable reference for case-flip). Also the About box's cracktro scroller.
    internal const string OriginalMarqueeText =
        ">>> You're looking at a fresh zero day drop from JSONBourne <<< " +
        "TERMinator, a fresh new BBS terminal app for people who live their life 64K at a time. " +
        "Greetings to all the crews still pushing pixels in 16 colors, all the SysOps who never sleep, " +
        "and anyone still rocking a 1084S monitor like it's 1989. " +
        "Special thx to aNACHRONiST (aNSt) for providing his excellent skillz and for making textmode look like pixel CG! " +
        "If this thing breaks, you get to keep both pieces. Send fixes, not complaints. " +
        ">>> end of line <<<";

    // --- Marquee glitch state ---
    private DispatcherTimer? _marqueeJitterTimer;
    private DispatcherTimer? _marqueeCaseFlipTimer;
    private DispatcherTimer? _marqueeGlitchScheduleTimer;
    private readonly Dictionary<int, DateTime> _activeCaseFlips = new();

    // --- Splash glitch state ---
    private DispatcherTimer? _splashGlitchScheduleTimer;
    private Bitmap? _splashBitmap;

    // --- Static burst state ---
    private DispatcherTimer? _staticBurstScheduleTimer;
    private DispatcherTimer? _staticBurstAnimTimer;
    private WriteableBitmap? _staticBurstBitmap;
    private int _staticBurstFramesLeft;
    private byte[]? _staticNoiseBuffer;  // reused across frames

    // --- Scanline sweep state ---
    private DispatcherTimer? _scanlineTimer;
    private WriteableBitmap? _scanlineBitmap;
    private int _scanlineRow;
    private int _prevScanlineRow = -1;
    private byte[]? _scanlineClearBuffer;  // reused each tick
    private byte[]? _scanlineDrawBuffer;   // reused each tick

    // Named transform (Avalonia doesn't auto-generate fields for transforms)
    private TranslateTransform? _marqueeJitter;

    private record GlitchSlice(
        int StartY,
        int Height,
        double XOffset,
        double RgbShift
    );

    public HomeView()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;

        FactsTicker.FactsRequested += (_, _) => ShowFactsOverlay();
        FactsOverlay.KeyDown += FactsOverlay_KeyDown;
        FactsBackdrop.PointerPressed += FactsBackdrop_PointerPressed;
        FactsClose.Click += (_, _) => HideFactsOverlay();

        HeaderLogo.PointerReleased += HeaderLogo_PointerReleased;
        SplashContainer.PointerReleased += SplashContainer_PointerReleased;

        // CRT thumbnail geometry follows the on-screen size: window resizes rescale the Viewbox, and a thumbnail
        // only gets a size once its slot is filled and visible
        SizeChanged += (_, _) => QueueCrtGeometryUpdate();
        foreach (var img in QuickConnectImages)
        {
            img.SizeChanged += (_, _) => QueueCrtGeometryUpdate();
            img.PropertyChanged += (_, e) =>
            {
                if (e.Property == IsVisibleProperty) QueueCrtGeometryUpdate();
            };
        }
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _marqueeJitter = MarqueeText.RenderTransform as TranslateTransform;
        _hostWindow = TopLevel.GetTopLevel(this) as Window;
        if (_hostWindow != null)
            _hostWindow.PropertyChanged += OnHostWindowPropertyChanged;
        UpdateAnimationState();
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_hostWindow != null)
            _hostWindow.PropertyChanged -= OnHostWindowPropertyChanged;
        _hostWindow = null;
        StopAnimations();
    }

    private void OnHostWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty)
            UpdateAnimationState();
    }

    /// <summary>
    /// The idle screensaver hides the home screen (ContentArea.IsVisible = false) without detaching it, so MainWindow
    /// reports it here; otherwise every timer below kept running behind the saver.
    /// </summary>
    public void SetCovered(bool covered)
    {
        _covered = covered;
        UpdateAnimationState();
    }

    /// <summary>
    /// Runs the animations only while someone can see them. Leaving the home screen detaches the view, but the
    /// screensaver and minimizing don't, and without this gate a dozen 16-100ms timers kept burning CPU.
    /// </summary>
    private void UpdateAnimationState()
    {
        bool shouldRun = this.GetVisualRoot() != null && !_covered
                         && _hostWindow?.WindowState != WindowState.Minimized;
        if (shouldRun && !_animating)
            StartAnimations();
        else if (!shouldRun && _animating)
            StopAnimations();
    }

    private void StartAnimations()
    {
        _animating = true;
        StartMarquee();
        StartMarqueeGlitch();
        StartSplashGlitch();
        StartStaticBurst();
        StartScanlineSweep();
        FactsTicker.Resume();

        // Resume the thumbnail CRT effect (its animation stops while the home screen is off screen)
        if (_crtSettings != null)
            ApplyCrtSettings(_crtSettings);
    }

    private void StopAnimations()
    {
        _animating = false;
        MarqueeCanvas.PropertyChanged -= OnCanvasBoundsChanged;
        _marqueeTimer?.Stop();
        _marqueeTimer = null;
        _marqueeJitterTimer?.Stop();
        _marqueeCaseFlipTimer?.Stop();
        _marqueeGlitchScheduleTimer?.Stop();
        _splashGlitchScheduleTimer?.Stop();
        _staticBurstScheduleTimer?.Stop();
        _staticBurstAnimTimer?.Stop();
        _scanlineTimer?.Stop();
        _scanlineTimer = null;
        StopCrtAnimation();
        FactsTicker.Pause();

        // Don't leave a half-finished glitch or static frame frozen on the logo
        GlitchCanvas.Children.Clear();
        if (_staticBurstBitmap != null && _staticNoiseBuffer != null)
        {
            Array.Clear(_staticNoiseBuffer);
            WriteWholeBitmap(_staticBurstBitmap, _staticNoiseBuffer);
            StaticBurstImage.InvalidateVisual();
        }
        if (_marqueeJitter != null)
        {
            _marqueeJitter.X = 0;
            _marqueeJitter.Y = 0;
        }
        MarqueeText.Opacity = 1.0;
        MarqueeText.Text = OriginalMarqueeText;
        _activeCaseFlips.Clear();
    }

    // ================================================================
    //  Layout: stacked / side-by-side
    // ================================================================

    /// <summary>
    /// Desktop layout toggle (View > Side-by-Side Home Screen). Moves the side column (Quick Connect and the
    /// shortcuts) beside the splash card instead of below it. Only that column moves, so the splash, marquee and
    /// their glitch overlays keep the same 600px column in both layouts.
    /// </summary>
    public void SetSideBySide(bool sideBySide)
    {
        // The card it covers changes size with the layout
        HideFactsOverlay();

        // The Quick Connect and shortcuts cards travel together as one column
        Grid.SetRow(HomeSideColumn, sideBySide ? 0 : 1);
        Grid.SetColumn(HomeSideColumn, sideBySide ? 1 : 0);
        HomeSideColumn.Margin = sideBySide ? new Thickness(24, 0, 0, 0) : new Thickness(0, 16, 0, 0);
        HomeBottomPanel.Width = sideBySide ? 420 : 600;
        HomeShortcutsPanel.Width = sideBySide ? 420 : 600;

        // Side-by-side: the column stretches to the row height and the Quick Connect card takes up any slack, so
        // both sides line up top and bottom. Content centres in whichever side ends up with room to spare.
        HomeSideColumn.VerticalAlignment = sideBySide ? VerticalAlignment.Stretch : VerticalAlignment.Top;
        HomeBottomPanel.VerticalAlignment = sideBySide ? VerticalAlignment.Center : VerticalAlignment.Top;
        HomeTopPanel.VerticalAlignment = sideBySide ? VerticalAlignment.Center : VerticalAlignment.Top;

        // Side-by-side shows Quick Connects 1-4 (2x2); stacked shows just 1-2
        QuickConnectRow2.IsVisible = sideBySide;

        // Stacked shows a single row of Quick Connects, so its card is tightened. Side-by-side keeps the roomier
        // spacing that suits the taller 2x2 column.
        HomeBottomCard.Padding = sideBySide ? new Thickness(20) : new Thickness(20, 14, 20, 14);

        // Side-by-side is top-aligned (no vertical centring) with a small top margin; stacked keeps its original margin
        HomeLayoutGrid.VerticalAlignment = VerticalAlignment.Top;
        HomeLayoutGrid.Margin = sideBySide ? new Thickness(20, 12, 20, 20) : new Thickness(20);

        // The layout's footprint changes, so the Viewbox scale (and the thumbnails' on-screen size) does too
        QueueCrtGeometryUpdate();
    }

    /// <summary>
    /// The home layout's natural size, margins included — what the window's content area should be for the
    /// layout to show at 1:1. Forces a measure pass so a just-toggled layout isn't reported stale (the Windows
    /// client hit exactly that: the stacked Quick Connect card kept its side-by-side height).
    /// </summary>
    public Size NaturalLayoutSize
    {
        get
        {
            HomeLayoutGrid.InvalidateMeasure();
            HomeLayoutGrid.Measure(Size.Infinity);
            return HomeLayoutGrid.DesiredSize;
        }
    }

    /// <summary>
    /// Width / height of the home layout at its natural size, margins included: the shape the window's content
    /// area should keep so the scaling Viewbox fills it edge to edge. Changes with the side-by-side toggle. 0
    /// before the first layout pass.
    /// </summary>
    public double LayoutAspectRatio =>
        HomeLayoutGrid.DesiredSize is { Width: > 0, Height: > 0 } d ? d.Width / d.Height : 0;

    /// <summary>
    /// Shrink-only while the window is being fitted to the layout (otherwise the Viewbox would scale the layout up
    /// to whatever size the window is measured at), then back to scaling both ways.
    /// </summary>
    public void SetFitShrinkOnly(bool shrinkOnly) =>
        HomeViewbox.StretchDirection = shrinkOnly ? StretchDirection.DownOnly : StretchDirection.Both;

    // ================================================================
    //  Facts overlay (opened by clicking the history ticker)
    // ================================================================

    private void ShowFactsOverlay()
    {
        FactsList.ItemsSource ??= HistoryTicker.AllFacts.Select(f => "> " + f).ToList();

        // Cover the card exactly, so the overlay never makes it grow, with the facts card at 90% of its size
        double w = HomeTopCard.Bounds.Width;
        double h = HomeTopCard.Bounds.Height;
        if (w <= 0 || h <= 0) return;
        FactsOverlay.Width = w;
        FactsOverlay.Height = h;
        FactsCard.Width = w * 0.9;
        FactsCard.Height = h * 0.9;

        FactsOverlay.IsVisible = true;
        FactsOverlay.Focus();
    }

    private void HideFactsOverlay() => FactsOverlay.IsVisible = false;

    private void FactsOverlay_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;
        HideFactsOverlay();
        e.Handled = true;
    }

    private void FactsBackdrop_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        HideFactsOverlay();
        e.Handled = true;
    }

    // ================================================================
    //  Header logo and splash art links
    // ================================================================

    private const string HeaderLogoUrl = "https://deadmodemsociety.com/terminator/";
    private const string SplashArtUrl = "https://absinthebbs.net/";

    private void HeaderLogo_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        OpenInBrowser(HeaderLogoUrl);
        e.Handled = true;
    }

    private void SplashContainer_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        OpenInBrowser(SplashArtUrl);
        e.Handled = true;
    }

    /// <summary>
    /// Opens a URL in the default browser. A browser that can't be opened is ignored — a link must never take the
    /// home screen down. Avalonia's launcher (the XDG portal / xdg-open) first, plain xdg-open as the fallback.
    /// </summary>
    private void OpenInBrowser(string url)
    {
        try
        {
            var launcher = TopLevel.GetTopLevel(this)?.Launcher;
            if (launcher != null)
            {
                _ = launcher.LaunchUriAsync(new Uri(url)).ContinueWith(t =>
                {
                    if (t.IsFaulted || !t.Result)
                        TryXdgOpen(url);
                }, TaskScheduler.Default);
                return;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not open {url}: {ex.Message}");
        }
        TryXdgOpen(url);
    }

    private static void TryXdgOpen(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo("xdg-open", url) { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not open {url}: {ex.Message}");
        }
    }

    // ================================================================
    //  CRT effect on the Quick Connect thumbnails
    // ================================================================

    private CrtSettings? _crtSettings;
    private bool _crtApplied;
    private DispatcherTimer? _crtAnimTimer;
    private readonly Stopwatch _crtClock = new();
    private bool _crtGeometryQueued;

    private CrtImage[] QuickConnectImages =>
        new[] { QuickConnectImage1, QuickConnectImage2, QuickConnectImage3, QuickConnectImage4 };

    /// <summary>
    /// Mirrors the terminal's CRT / scanline setting onto the Quick Connect snapshots, so they look the way the boards
    /// will once connected. Disabled settings (or a Skia without the runtime shader) remove the effect entirely.
    /// </summary>
    public void ApplyCrtSettings(CrtSettings? settings)
    {
        _crtSettings = settings;

        if (settings == null || !settings.Enabled || !Effects.CrtShader.IsSupported)
        {
            DisableCrtEffect();
            return;
        }

        _crtApplied = true;
        foreach (var img in QuickConnectImages)
            img.ApplyCrt(settings);
        UpdateCrtGeometry();

        if (Effects.CrtParams.IsAnimated(settings) && _animating)
            StartCrtAnimation();
        else
            StopCrtAnimation();
    }

    /// <summary>
    /// Phosphor mode (§7): the home screen as a monochrome tube (luminance x the phosphor colour), or null for the
    /// colour picture. The terminal is never tinted, so only this view and the console carry it.
    /// </summary>
    public void SetPhosphor(Avalonia.Media.Color? tint) => PhosphorLayer.Configure(null, tint);

    /// <summary>Removes the effect from the thumbnails. Also the recovery path if applying it fails.</summary>
    public void DisableCrtEffect()
    {
        StopCrtAnimation();
        foreach (var img in QuickConnectImages)
            img.ApplyCrt(null);
        _crtApplied = false;
    }

    private void QueueCrtGeometryUpdate()
    {
        if (!_crtApplied || _crtGeometryQueued) return;
        _crtGeometryQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _crtGeometryQueued = false;
            UpdateCrtGeometry();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Scanline and grille pitch are in real device pixels, as on the terminal. The thumbnails sit inside the scaling
    /// Viewbox, so this measures the scale each one is actually drawn at on screen rather than its unscaled layout
    /// size, and hands it to the control (which folds in the display's render scaling itself).
    /// </summary>
    private void UpdateCrtGeometry()
    {
        if (!_crtApplied || _crtSettings == null)
            return;

        var root = this.GetVisualRoot() as Visual;
        if (root == null)
            return;

        foreach (var img in QuickConnectImages)
        {
            if (!img.IsVisible || img.Bounds.Width <= 0 || img.Bounds.Height <= 0)
                continue;

            var m = img.TransformToVisual(root);
            double scale = m.HasValue ? Math.Sqrt(Math.Abs(m.Value.M11 * m.Value.M22 - m.Value.M12 * m.Value.M21)) : 1.0;
            if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale)) scale = 1.0;
            img.LayoutScale = scale;
            img.InvalidateVisual();
        }
    }

    private void StartCrtAnimation()
    {
        if (_crtAnimTimer != null) return;
        _crtClock.Restart();
        _crtAnimTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _crtAnimTimer.Tick += (_, _) =>
        {
            if (!_crtApplied) return;
            double t = _crtClock.Elapsed.TotalSeconds;
            foreach (var img in QuickConnectImages)
            {
                if (!img.IsVisible) continue;
                img.Time = t;
                img.InvalidateVisual();
            }
        };
        _crtAnimTimer.Start();
    }

    private void StopCrtAnimation()
    {
        _crtAnimTimer?.Stop();
        _crtAnimTimer = null;
        _crtClock.Stop();
    }

    // ================================================================
    //  Marquee scroll animation
    // ================================================================

    private void StartMarquee()
    {
        MarqueeText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _textWidth = MarqueeText.DesiredSize.Width;
        _canvasWidth = MarqueeCanvas.Bounds.Width;

        if (_canvasWidth <= 0)
        {
            MarqueeCanvas.PropertyChanged += OnCanvasBoundsChanged;
            return;
        }

        SetupAnimation(_textWidth, _canvasWidth);
    }

    private void OnCanvasBoundsChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == BoundsProperty)
        {
            MarqueeCanvas.PropertyChanged -= OnCanvasBoundsChanged;
            _canvasWidth = MarqueeCanvas.Bounds.Width;
            if (_canvasWidth > 0)
            {
                MarqueeText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                _textWidth = MarqueeText.DesiredSize.Width;
                SetupAnimation(_textWidth, _canvasWidth);
            }
        }
    }

    private void SetupAnimation(double textWidth, double canvasWidth)
    {
        _marqueeTimer?.Stop();

        Canvas.SetLeft(MarqueeText, canvasWidth);

        // As Windows' DoubleAnimation: Canvas.Left runs linearly from canvasWidth to -textWidth in textWidth/45 seconds,
        // then repeats forever. Driven off a clock, not per-tick steps, so a late tick never slows the scroll.
        double duration = textWidth / 45.0;
        if (duration <= 0) return;
        double distance = canvasWidth + textWidth;
        _marqueeClock.Restart();
        _marqueeTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _marqueeTimer.Tick += (_, _) =>
        {
            double phase = (_marqueeClock.Elapsed.TotalSeconds % duration) / duration;
            Canvas.SetLeft(MarqueeText, canvasWidth - distance * phase);
        };
        _marqueeTimer.Start();
    }

    // ================================================================
    //  Marquee CRT glitch
    // ================================================================

    private void StartMarqueeGlitch()
    {
        _marqueeJitterTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(80)
        };
        _marqueeJitterTimer.Tick += OnMarqueeJitterTick;
        _marqueeJitterTimer.Start();

        _marqueeCaseFlipTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _marqueeCaseFlipTimer.Tick += OnMarqueeCaseFlipTick;
        _marqueeCaseFlipTimer.Start();

        ScheduleNextMarqueeGlitch();
    }

    private void OnMarqueeJitterTick(object? sender, EventArgs e)
    {
        _marqueeJitter!.X = _rng.NextDouble() * 4.0 - 2.0;
    }

    private void OnMarqueeCaseFlipTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var chars = OriginalMarqueeText.ToCharArray();

        // Expire old flips
        var expired = new List<int>();
        foreach (var kvp in _activeCaseFlips)
        {
            if (kvp.Value <= now) expired.Add(kvp.Key);
        }
        foreach (var key in expired) _activeCaseFlips.Remove(key);

        // Add 1-2 new flips, each lasting ~350ms
        int flipCount = _rng.Next(1, 3);
        for (int i = 0; i < flipCount; i++)
        {
            int idx = _rng.Next(chars.Length);
            if (char.IsLetter(chars[idx]))
            {
                _activeCaseFlips[idx] = now.AddMilliseconds(350);
            }
        }

        // Apply all active flips
        foreach (var idx in _activeCaseFlips.Keys)
        {
            if (idx < chars.Length && char.IsLetter(chars[idx]))
            {
                chars[idx] = char.IsUpper(chars[idx])
                    ? char.ToLower(chars[idx])
                    : char.ToUpper(chars[idx]);
            }
        }

        MarqueeText.Text = new string(chars);
    }

    private void ScheduleNextMarqueeGlitch()
    {
        if (!_animating) return;
        double delay = 1.5 + _rng.NextDouble() * 2.5;
        _marqueeGlitchScheduleTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(delay)
        };
        _marqueeGlitchScheduleTimer.Tick += OnMarqueeGlitchTrigger;
        _marqueeGlitchScheduleTimer.Start();
    }

    private void OnMarqueeGlitchTrigger(object? sender, EventArgs e)
    {
        _marqueeGlitchScheduleTimer?.Stop();

        _marqueeJitter!.X = _rng.NextDouble() * 4.0 - 2.0;
        _marqueeJitter!.Y = _rng.NextDouble() * 2.0 - 1.0;
        MarqueeText.Opacity = 0.7 + _rng.NextDouble() * 0.15;

        double duration = 0.03 + _rng.NextDouble() * 0.05;
        var clearTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(duration)
        };
        clearTimer.Tick += (s, _) =>
        {
            ((DispatcherTimer)s!).Stop();
            _marqueeJitter!.Y = 0;
            MarqueeText.Opacity = 1.0;
            ScheduleNextMarqueeGlitch();
        };
        clearTimer.Start();
    }

    // ================================================================
    //  Splash image glitch
    // ================================================================

    private void StartSplashGlitch()
    {
        LoadSplashBitmap();
        ScheduleNextSplashGlitch();
    }

    private void LoadSplashBitmap()
    {
        // Grab the bitmap from the already-loaded Image control (set via AXAML)
        _splashBitmap = SplashImage.Source as Bitmap;
    }

    private void ScheduleNextSplashGlitch()
    {
        if (!_animating) return;
        double delay = 2.0 + _rng.NextDouble() * 3.0;
        _splashGlitchScheduleTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(delay)
        };
        _splashGlitchScheduleTimer.Tick += OnSplashGlitchTrigger;
        _splashGlitchScheduleTimer.Start();
    }

    private void OnSplashGlitchTrigger(object? sender, EventArgs e)
    {
        _splashGlitchScheduleTimer?.Stop();

        if (_splashBitmap == null || SplashImage.Bounds.Width <= 0 || SplashImage.Bounds.Height <= 0)
        {
            ScheduleNextSplashGlitch();
            return;
        }

        int srcHeight = _splashBitmap.PixelSize.Height;

        int sliceCount = _rng.Next(3, 7);
        var slices = new List<GlitchSlice>(sliceCount);

        for (int i = 0; i < sliceCount; i++)
        {
            int startY = _rng.Next(srcHeight);
            int maxH = Math.Min(srcHeight - startY, srcHeight / 5);
            int height = _rng.Next(5, Math.Max(10, maxH));
            double xOffset = _rng.NextDouble() * 50.0 - 25.0;
            double rgbShift = _rng.Next(2) == 0 ? _rng.NextDouble() * 4.0 + 2.0 : 0;

            slices.Add(new GlitchSlice(startY, height, xOffset, rgbShift));
        }

        RenderSplashGlitch(slices);

        double duration = 0.03 + _rng.NextDouble() * 0.05;
        var clearTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(duration)
        };
        clearTimer.Tick += (s, _) =>
        {
            ((DispatcherTimer)s!).Stop();
            GlitchCanvas.Children.Clear();
            ScheduleNextSplashGlitch();
        };
        clearTimer.Start();
    }

    private void RenderSplashGlitch(List<GlitchSlice> slices)
    {
        GlitchCanvas.Children.Clear();

        if (_splashBitmap == null) return;

        double renderWidth = SplashImage.Bounds.Width;
        double renderHeight = SplashImage.Bounds.Height;
        if (renderWidth <= 0 || renderHeight <= 0) return;

        int srcWidth = _splashBitmap.PixelSize.Width;
        int srcHeight = _splashBitmap.PixelSize.Height;
        double scaleY = renderHeight / srcHeight;

        double offsetLeft = (SplashContainer.Bounds.Width - renderWidth) / 2.0;
        double offsetTop = (SplashContainer.Bounds.Height - renderHeight) / 2.0;

        foreach (var slice in slices)
        {
            int srcY = Math.Min(slice.StartY, srcHeight - 1);
            int srcH = Math.Min(slice.Height, srcHeight - srcY);
            if (srcH <= 0) continue;

            // Create a cropped slice using SkiaSharp-compatible pixel copy
            var croppedBitmap = CreateCroppedBitmap(_splashBitmap, 0, srcY, srcWidth, srcH);
            if (croppedBitmap == null) continue;

            double displayY = offsetTop + srcY * scaleY;
            double displayH = srcH * scaleY;

            if (slice.RgbShift > 0)
            {
                var redImg = new Image
                {
                    Source = croppedBitmap,
                    Width = renderWidth,
                    Height = displayH,
                    Stretch = Stretch.Fill,
                    Opacity = 0.35,
                };
                RenderOptions.SetBitmapInterpolationMode(redImg, BitmapInterpolationMode.None);
                Canvas.SetLeft(redImg, offsetLeft + slice.XOffset + slice.RgbShift);
                Canvas.SetTop(redImg, displayY);
                GlitchCanvas.Children.Add(redImg);

                var cyanImg = new Image
                {
                    Source = croppedBitmap,
                    Width = renderWidth,
                    Height = displayH,
                    Stretch = Stretch.Fill,
                    Opacity = 0.35,
                };
                RenderOptions.SetBitmapInterpolationMode(cyanImg, BitmapInterpolationMode.None);
                Canvas.SetLeft(cyanImg, offsetLeft + slice.XOffset - slice.RgbShift);
                Canvas.SetTop(cyanImg, displayY);
                GlitchCanvas.Children.Add(cyanImg);
            }

            var img = new Image
            {
                Source = croppedBitmap,
                Width = renderWidth,
                Height = displayH,
                Stretch = Stretch.Fill,
            };
            RenderOptions.SetBitmapInterpolationMode(img, BitmapInterpolationMode.None);
            Canvas.SetLeft(img, offsetLeft + slice.XOffset);
            Canvas.SetTop(img, displayY);
            GlitchCanvas.Children.Add(img);
        }
    }

    // ================================================================
    //  Static burst — brief flash of TV static over the image (Windows HomeView.StartStaticBurst)
    //  - Every 5-8s, 2-13 frames of fresh noise 60ms apart (120-780ms)
    //  - Quarter-resolution bitmap, same stretch as the splash, so it stays inside the image
    // ================================================================

    private void StartStaticBurst()
    {
        if (_splashBitmap == null) return;

        // Lower-res bitmap for a chunky static look (1/4 resolution)
        if (_staticBurstBitmap == null)
        {
            int w = _splashBitmap.PixelSize.Width / 4;
            int h = _splashBitmap.PixelSize.Height / 4;
            if (w <= 0 || h <= 0) return;
            _staticBurstBitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96),
                                                     PixelFormat.Bgra8888, AlphaFormat.Premul);
            _staticNoiseBuffer = new byte[w * h * 4];
            WriteWholeBitmap(_staticBurstBitmap, _staticNoiseBuffer);   // start transparent
            StaticBurstImage.Source = _staticBurstBitmap;
        }

        ScheduleNextStaticBurst();
    }

    private void ScheduleNextStaticBurst()
    {
        if (!_animating) return;
        double delay = 5.0 + _rng.NextDouble() * 3.0; // 5-8s
        if (_staticBurstScheduleTimer == null)
        {
            _staticBurstScheduleTimer = new DispatcherTimer();
            _staticBurstScheduleTimer.Tick += OnStaticBurstTrigger;
        }
        _staticBurstScheduleTimer.Interval = TimeSpan.FromSeconds(delay);
        _staticBurstScheduleTimer.Start();
    }

    private void OnStaticBurstTrigger(object? sender, EventArgs e)
    {
        _staticBurstScheduleTimer?.Stop();

        if (_staticBurstBitmap == null)
        {
            ScheduleNextStaticBurst();
            return;
        }

        // Animate for 120-780ms, regenerating noise every 60ms like a TV
        _staticBurstFramesLeft = _rng.Next(2, 14);

        if (_staticBurstAnimTimer == null)
        {
            _staticBurstAnimTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            _staticBurstAnimTimer.Tick += OnStaticBurstFrame;
        }
        _staticBurstAnimTimer.Start();

        // Draw the first frame immediately
        DrawStaticNoise();
    }

    private void OnStaticBurstFrame(object? sender, EventArgs e)
    {
        _staticBurstFramesLeft--;

        if (_staticBurstFramesLeft <= 0)
        {
            _staticBurstAnimTimer?.Stop();

            // Clear to transparent
            if (_staticBurstBitmap != null && _staticNoiseBuffer != null)
            {
                Array.Clear(_staticNoiseBuffer);
                WriteWholeBitmap(_staticBurstBitmap, _staticNoiseBuffer);
                StaticBurstImage.InvalidateVisual();
            }

            ScheduleNextStaticBurst();
            return;
        }

        DrawStaticNoise();
    }

    private void DrawStaticNoise()
    {
        if (_staticBurstBitmap == null || _staticNoiseBuffer == null) return;

        var buf = _staticNoiseBuffer;
        Array.Clear(buf);

        // 18% of the cells lit: grey 120-254 at alpha 25-69 (straight alpha on Windows; premultiplied here)
        for (int i = 0; i < buf.Length; i += 4)
        {
            if (_rng.NextDouble() < 0.18)
            {
                int brightness = _rng.Next(120, 255);
                int alpha = _rng.Next(25, 70);
                byte c = (byte)((brightness * alpha + 127) / 255);
                buf[i] = c;                 // B
                buf[i + 1] = c;             // G
                buf[i + 2] = c;             // R
                buf[i + 3] = (byte)alpha;   // A
            }
        }

        WriteWholeBitmap(_staticBurstBitmap, buf);
        StaticBurstImage.InvalidateVisual();
    }

    // ================================================================
    //  Single CRT scanline sweep (Windows HomeView.StartScanlineSweep): a 2-source-row #66EE66 line at alpha 0x30
    //  drawn into a bitmap matching the splash, stepping h/240 rows (min 1) every 33ms, so ~8s top to bottom
    // ================================================================

    private void StartScanlineSweep()
    {
        if (_splashBitmap == null) return;

        if (_scanlineBitmap == null)
        {
            int w = _splashBitmap.PixelSize.Width;
            int h = _splashBitmap.PixelSize.Height;
            if (w <= 0 || h <= 0) return;
            _scanlineBitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96),
                                                  PixelFormat.Bgra8888, AlphaFormat.Premul);
            WriteWholeBitmap(_scanlineBitmap, new byte[w * h * 4]);   // start transparent
            ScanlineImage.Source = _scanlineBitmap;
            _scanlineRow = 0;
            _prevScanlineRow = -1;
        }

        _scanlineTimer?.Stop();
        _scanlineTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _scanlineTimer.Tick += OnScanlineTick;
        _scanlineTimer.Start();
    }

    private void OnScanlineTick(object? sender, EventArgs e)
    {
        if (_scanlineBitmap == null) return;

        int w = _scanlineBitmap.PixelSize.Width;
        int h = _scanlineBitmap.PixelSize.Height;
        int stride = w * 4;
        const int lineH = 2;
        int bufSize = stride * lineH;

        if (_scanlineClearBuffer == null || _scanlineClearBuffer.Length != bufSize)
            _scanlineClearBuffer = new byte[bufSize];

        if (_scanlineDrawBuffer == null || _scanlineDrawBuffer.Length != bufSize)
        {
            // #66EE66 at alpha 0x30 (~19%), premultiplied
            const int a = 0x30;
            byte r = (byte)((0x66 * a + 127) / 255), g = (byte)((0xEE * a + 127) / 255), b = r;
            _scanlineDrawBuffer = new byte[bufSize];
            for (int i = 0; i < bufSize; i += 4)
            {
                _scanlineDrawBuffer[i] = b;
                _scanlineDrawBuffer[i + 1] = g;
                _scanlineDrawBuffer[i + 2] = r;
                _scanlineDrawBuffer[i + 3] = a;
            }
        }

        using (var fb = _scanlineBitmap.Lock())
        {
            // Clear the previous line
            if (_prevScanlineRow >= 0 && _prevScanlineRow < h)
                WriteRows(fb, _scanlineClearBuffer, _prevScanlineRow, Math.Min(lineH, h - _prevScanlineRow), stride);

            // Advance (~8 seconds to traverse the full image)
            double speed = h / 240.0; // source rows per tick at ~30fps
            _scanlineRow = (_scanlineRow + Math.Max(1, (int)speed)) % h;

            int drawH = Math.Min(lineH, h - _scanlineRow);
            if (drawH > 0)
                WriteRows(fb, _scanlineDrawBuffer, _scanlineRow, drawH, stride);
        }

        _prevScanlineRow = _scanlineRow;
        ScanlineImage.InvalidateVisual();
    }

    private static void WriteRows(ILockedFramebuffer fb, byte[] src, int row, int rows, int srcStride)
    {
        for (int y = 0; y < rows; y++)
            System.Runtime.InteropServices.Marshal.Copy(src, y * srcStride, fb.Address + (row + y) * fb.RowBytes,
                                                        srcStride);
    }

    private static void WriteWholeBitmap(WriteableBitmap bmp, byte[] src)
    {
        using var fb = bmp.Lock();
        WriteRows(fb, src, 0, bmp.PixelSize.Height, bmp.PixelSize.Width * 4);
    }

    /// <summary>
    /// Extract a horizontal strip from a bitmap via DrawImage with source/dest rects.
    /// </summary>
    private static RenderTargetBitmap? CreateCroppedBitmap(Bitmap source, int x, int y, int w, int h)
    {
        try
        {
            var cropped = new RenderTargetBitmap(new PixelSize(w, h));
            using (var ctx = cropped.CreateDrawingContext())
            {
                var srcRect = new Rect(x, y, w, h);
                var dstRect = new Rect(0, 0, w, h);
                ctx.DrawImage(source, srcRect, dstRect);
            }
            return cropped;
        }
        catch
        {
            return null;
        }
    }

}
