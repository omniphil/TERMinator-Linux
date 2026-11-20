using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using TERMinator.Linux.Services;

namespace TERMinator.Linux.Controls;

/// <summary>
/// TRACE's display layer (TERMinator Render And Compute Engine): shows a running module's frames over the terminal
/// area and sends it the keyboard: always for an exclusive module (a game), and for any module while it has text
/// input on (a chat line in a module drawn over part of the screen), when typed characters go to it as well as the
/// keys. While such a layer is showing it takes every key typed into the window: the
/// window's tunnelling key handlers hand each key to <see cref="HandleKey"/> before any control sees it (Avalonia has
/// no raw message hook like WPF's ThreadPreprocessMessage), and typed text to TraceSessionManager.HandleText.
/// It's its own layer (MainWindow's EngineHost panel),
/// separate from the terminal canvas and from every TAP+ overlay, which carry on untouched underneath. See
/// BBSGames/Doom/ENGINE.md. Linux port of the Windows client's Controls/EngineHostLayer.cs.
/// </summary>
public sealed partial class EngineHostLayer : Panel
{
    /// <summary>The layer whose module has text input on, if any: it gets the keyboard ahead of an exclusive game.</summary>
    private static EngineHostLayer? s_textOwner;

    private readonly EngineModuleHost _module;
    private readonly bool _exclusiveInput;
    private bool _textInput;
    private int _sentWidth, _sentHeight, _sentScale;
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private WriteableBitmap? _bitmap;
    private long _lastFrame;
    private int _frameWidth, _frameHeight;
    private bool _aspect43 = true;
    private int _framePosted;   // 1 while a render is queued on the UI thread
    private Window? _window;

    // Keys we've reported pressed, as (scancode, extended, vk), so they can be released when focus leaves
    private readonly HashSet<(int Scan, bool Extended, int Vk)> _held = [];

    /// <summary>The module has exited, with its exit code. Raised on the UI thread; the owner removes the layer.</summary>
    public event Action<EngineHostLayer, int>? Closed;

    /// <summary>The module turned text input on or off. Raised on the UI thread.</summary>
    public event Action<EngineHostLayer, bool>? TextInputChanged;

    /// <summary>The module has text input on: it has the keyboard, and typed characters go to it.</summary>
    public bool TextInputOn => _textInput;

    /// <summary>Whether key presses go to this module right now (another module's text input comes first).</summary>
    private bool CapturesKeyboard => s_textOwner == null ? _exclusiveInput : s_textOwner == this;

    public EngineModuleHost Module => _module;

    /// <summary>True when this layer takes the keyboard (a game).</summary>
    public bool ExclusiveInput => _exclusiveInput;

    /// <summary>
    /// Hidden but kept running (TRACE Hide with keep=1, 1.1.6): no keys, typed text, mouse or focus reach the module
    /// until it is shown again, and another module may have the keyboard meanwhile. The module isn't told: it keeps
    /// its state, so a board that opens it again finds it where it was.
    /// </summary>
    public void Suspend(bool on)
    {
        if (on == _suspended)
            return;
        _suspended = on;
        UpdateDirect();   // hidden: frames back through the frame buffer
        if (on)
        {
            ReleaseHeldKeys();
            MouseLost();
            if (s_textOwner == this)
                s_textOwner = null;
            if (_exclusiveInput)
                _module.SendFocus(false);
            return;
        }
        if (_textInput && s_textOwner == null)
            s_textOwner = this;
        if (_exclusiveInput)
        {
            if (_window is { IsActive: true })
                _module.SendFocus(true);
            Focus();
        }
        MouseRegained();
    }

    /// <summary>Hidden and kept running: see Suspend.</summary>
    public bool Suspended => _suspended;
    private bool _suspended;

    /// <param name="exclusiveInput">
    /// True: the layer takes the keyboard (a game). False: display only; the keyboard and mouse stay with the terminal
    /// underneath, as when a door drives the module (TraceSessionManager).
    /// </param>
    public EngineHostLayer(EngineModuleHost module, bool exclusiveInput = true)
    {
        _module = module;
        _exclusiveInput = exclusiveInput;
        Background = Brushes.Black;
        Focusable = exclusiveInput;
        IsHitTestVisible = exclusiveInput;
        ClipToBounds = true;

        RenderOptions.SetBitmapInterpolationMode(_image, BitmapInterpolationMode.None);   // nearest-neighbour
        _image.HorizontalAlignment = HorizontalAlignment.Center;
        _image.VerticalAlignment = VerticalAlignment.Center;
        Children.Add(_image);

        _module.FrameArrived += OnFrameArrived;
        _module.DirectRequested += on => Dispatcher.UIThread.Post(() =>
        {
            _directWanted = on;
            UpdateDirect();
        });
        LayoutUpdated += (_, _) => UpdateDirect();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty)
                UpdateDirect();
        };
        _module.TextInputChanged += on => Dispatcher.UIThread.Post(() => SetTextInput(on));
        _module.Exited += code => Dispatcher.UIThread.Post(() => RaiseClosed(code));
        if (_module.HasExited)  // died before we subscribed
            Dispatcher.UIThread.Post(() => RaiseClosed(_module.ExitCode));

        SizeChanged += (_, _) =>
        {
            LayoutImage();
            ReportSize();
        };
        AttachedToVisualTree += OnAttached;
        DetachedFromVisualTree += OnDetached;
        InitMouse();   // EngineHostLayer.Mouse.cs
    }

    /// <summary>
    /// The module turned text input on or off. On: it takes the keyboard (ahead of an exclusive game) and gets typed
    /// characters as well as keys. Off: the keyboard goes back where it was (the terminal, or the game).
    /// </summary>
    private void SetTextInput(bool on)
    {
        if (on == _textInput || _closed)
            return;
        _textInput = on;
        if (on)
            s_textOwner = this;
        else if (s_textOwner == this)
            s_textOwner = null;
        TextInputChanged?.Invoke(this, on);
    }

    private bool _closed;
    private bool _shownFrame;

    /// <summary>The module's first frame is on screen (a held picture of its last run can go).</summary>
    public event Action<EngineHostLayer>? FirstFrame;

    /// <summary>
    /// The module's last frame as a still picture in the layer's place, dimmed, taking no input to the module but
    /// covering what's under it: shown while Rainwire runs a transfer with the module closed (Close;hold=1).
    /// Null before the first frame. The pixels are copied: the layer's own bitmap is freed when it leaves the screen.
    /// </summary>
    public Control? HeldSnapshot()
    {
        if (_bitmap == null || _frameWidth <= 0 || _frameHeight <= 0)
            return null;
        WriteableBitmap still;
        try
        {
            var size = _bitmap.PixelSize;
            still = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            using var src = _bitmap.Lock();
            using var dst = still.Lock();
            int rowBytes = size.Width * 4;
            var row = new byte[rowBytes];
            for (int y = 0; y < size.Height; y++)
            {
                Marshal.Copy(src.Address + y * src.RowBytes, row, 0, rowBytes);
                Marshal.Copy(row, 0, dst.Address + y * dst.RowBytes, rowBytes);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("EngineHostLayer: held picture: " + ex.Message);
            return null;
        }
        var image = new Image
        {
            Source = still,
            Stretch = Stretch.Fill,
            Width = _image.Width,
            Height = _image.Height,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.6,   // on black: about 60% brightness
        };
        RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.None);
        var held = new Panel
        {
            Background = Brushes.Black,
            ClipToBounds = true,
            IsHitTestVisible = true,   // clicks stop here, not on the terminal text underneath
            Focusable = false,
            HorizontalAlignment = HorizontalAlignment,
            VerticalAlignment = VerticalAlignment,
            Margin = Margin,
            Width = Width,
            Height = Height,
        };
        held.Children.Add(image);
        // Fit the picture as LayoutImage does, should the area change size while it's held
        bool aspect43 = _aspect43;
        int fw = _frameWidth, fh = _frameHeight;
        held.SizeChanged += (_, _) =>
        {
            var heldTop = TopLevel.GetTopLevel(held);
            if (held.Bounds.Width <= 0 || held.Bounds.Height <= 0 || heldTop == null)
                return;
            double scale = heldTop.RenderScaling;
            var (w, h) = FitDevice((int)Math.Round(held.Bounds.Width * scale), (int)Math.Round(held.Bounds.Height * scale),
                                   fw, fh, aspect43);
            image.Width = w / scale;
            image.Height = h / scale;
        };
        // Swallow the pointer so nothing underneath reacts while it's held
        held.PointerPressed += (_, e) => e.Handled = true;
        held.PointerReleased += (_, e) => e.Handled = true;
        held.PointerWheelChanged += (_, e) => e.Handled = true;
        held.DetachedFromVisualTree += (_, _) =>
        {
            image.Source = null;
            still.Dispose();
        };
        return held;
    }

    private void RaiseClosed(int exitCode)
    {
        if (_closed)
            return;
        _closed = true;
        Closed?.Invoke(this, exitCode);
    }

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        Dispatcher.UIThread.Post(ReportSize, DispatcherPriority.Background);
        // Every layer watches the window, since any module can turn text input on or take the mouse; only an
        // exclusive one reports focus to its module
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window != null)
        {
            _window.Deactivated += OnWindowDeactivated;
            _window.Activated += OnWindowActivated;
            _window.ScalingChanged += OnScalingChanged;   // moved to a screen with another scale
            if (_exclusiveInput && _window.IsActive)
                _module.SendFocus(true);
        }
        if (_exclusiveInput)
            Dispatcher.UIThread.Post(() => Focus(), DispatcherPriority.Input);
    }

    /// <summary>The window's RenderScaling changed: the device-pixel size, the scale and the picture's place with it.</summary>
    private void OnScalingChanged(object? sender, EventArgs e)
    {
        LayoutImage();
        ReportSize();
        UpdateDirect();
    }

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _directSent = default;
        if (_directWanted && !_closed)
            _module.SendSurface(EngineModuleHost.SurfaceNone, 0, 0, 0, 0, 0);
        FreeMouse();
        // The frame bitmap is a Skia surface, not managed memory the GC hurries to reclaim: at the 1280x1024
        // capacity that is about 5 MB left behind per session. WPF needs no equivalent.
        _image.Source = null;
        _bitmap?.Dispose();
        _bitmap = null;
        if (_window != null)
        {
            _window.Deactivated -= OnWindowDeactivated;
            _window.Activated -= OnWindowActivated;
            _window.ScalingChanged -= OnScalingChanged;
        }
        _window = null;
        if (s_textOwner == this)
            s_textOwner = null;   // closed while typing: the keyboard goes back
    }

    // Frames arrive on the host's reader thread; one render at a time is queued on the UI thread
    private void OnFrameArrived()
    {
        if (Interlocked.Exchange(ref _framePosted, 1) == 0)
            Dispatcher.UIThread.Post(RenderFrame, DispatcherPriority.Render);
    }

    private void RenderFrame()
    {
        Interlocked.Exchange(ref _framePosted, 0);
        bool drawn = _module.TryConsumeFrame(ref _lastFrame, (pixels, width, height, aspect43) =>
        {
            if (_bitmap == null || _bitmap.PixelSize.Width != width || _bitmap.PixelSize.Height != height)
            {
                var old = _bitmap;
                // Opaque: the module's BGRX pixels leave alpha undefined (WPF's Bgr32 ignores it the same way)
                _bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96),
                                              PixelFormat.Bgra8888, AlphaFormat.Opaque);
                _image.Source = _bitmap;
                old?.Dispose();
            }
            using (var fb = _bitmap.Lock())
            {
                int rowBytes = width * 4;
                if (fb.RowBytes == rowBytes)
                {
                    Marshal.Copy(pixels, 0, fb.Address, rowBytes * height);
                }
                else
                {
                    for (int y = 0; y < height; y++)
                        Marshal.Copy(pixels, y * rowBytes, fb.Address + y * fb.RowBytes, rowBytes);
                }
            }
            if (width != _frameWidth || height != _frameHeight || aspect43 != _aspect43)
            {
                _frameWidth = width;
                _frameHeight = height;
                _aspect43 = aspect43;
                LayoutImage();
            }
        });
        if (drawn)
        {
            _image.InvalidateVisual();
            if (!_shownFrame)
            {
                _shownFrame = true;
                Dispatcher.UIThread.Post(() => FirstFrame?.Invoke(this), DispatcherPriority.Render);
            }
        }
    }

    /// <summary>Where an OS file drop at p (in relativeTo's coordinates) falls in the module's frame pixels; false off the picture.</summary>
    public bool FramePoint(Visual relativeTo, Point p, out int x, out int y)
    {
        x = y = 0;
        if (_frameWidth <= 0 || _frameHeight <= 0 || _image.Bounds.Width <= 0 || _image.Bounds.Height <= 0)
            return false;
        if (relativeTo.TranslatePoint(p, _image) is not { } q)
            return false;
        double fx = q.X * _frameWidth / _image.Bounds.Width, fy = q.Y * _frameHeight / _image.Bounds.Height;
        if (fx < 0 || fy < 0 || fx >= _frameWidth || fy >= _frameHeight)
            return false;
        x = (int)fx;
        y = (int)fy;
        return true;
    }

    /// <summary>Tells the module the device-pixel size it's shown at, so it can render 1:1 (the fractal module does).</summary>
    private void ReportSize()
    {
        var top = TopLevel.GetTopLevel(this);
        if (Bounds.Width <= 0 || Bounds.Height <= 0 || top == null)
            return;
        double scale = top.RenderScaling;
        int w = (int)Math.Round(Bounds.Width * scale), h = (int)Math.Round(Bounds.Height * scale);
        int percent = (int)Math.Round(scale * 100);   // the display scale in percent (TE_IN_RESIZE's a, 1.1.6)
        if (w == _sentWidth && h == _sentHeight && percent == _sentScale)
            return;
        _sentWidth = w;
        _sentHeight = h;
        _sentScale = percent;
        _module.SendResize(w, h, percent);
    }

    /// <summary>
    /// Fits the picture in the layer: 4:3 when the module asks for it (Doom's tall pixels), else square pixels. Worked
    /// in whole device pixels (§12.70): nearest-neighbour scaling drops a row or column whenever the picture is even one
    /// pixel smaller than the frame, and the old floating-point fit often came out a pixel short. A UI module renders
    /// exactly the size ReportSize sends, so that is now shown 1:1 (layout rounding keeps it on whole pixels).
    /// </summary>
    // ---- Direct present (TE_OUT_DIRECT / TE_IN_SURFACE, 1.1.6; Windows: Controls/EngineHostLayer.cs) ----
    // An exclusive layer offers the module its X11 window: the module makes a child window over the picture (it
    // selects no events, so the pointer's go on to ours, and it never takes the focus) and presents into it. Nothing
    // Avalonia draws can cover it, hence exclusive layers only. Hidden, detached or not on X11 (no "XID" handle), the
    // offer is withdrawn or never made, and frames come through the frame buffer.
    private bool _directWanted;
    private (ulong Win, int X, int Y, int W, int H) _directSent;

    private void UpdateDirect()
    {
        if (!_directWanted || _closed)
            return;
        (ulong Win, int X, int Y, int W, int H) want = default;
        var top = TopLevel.GetTopLevel(this);
        if (_exclusiveInput && !_suspended && IsVisible && top != null && _image.Bounds.Width > 0
            && _image.Bounds.Height > 0 && top.TryGetPlatformHandle() is { HandleDescriptor: "XID" } handle
            && handle.Handle != IntPtr.Zero && _image.TranslatePoint(new Point(0, 0), top) is { } at)
        {
            double scale = top.RenderScaling;
            want = ((ulong)handle.Handle, (int)Math.Round(at.X * scale), (int)Math.Round(at.Y * scale),
                    (int)Math.Round(_image.Bounds.Width * scale), (int)Math.Round(_image.Bounds.Height * scale));
        }
        if (want == _directSent)
            return;
        _directSent = want;
        if (want.Win == 0 || want.W <= 0 || want.H <= 0)
            _module.SendSurface(EngineModuleHost.SurfaceNone, 0, 0, 0, 0, 0);
        else
            _module.SendSurface(EngineModuleHost.SurfaceX11, want.Win, want.X, want.Y, want.W, want.H);
    }

    private void LayoutImage()
    {
        var top = TopLevel.GetTopLevel(this);
        if (_frameWidth == 0 || Bounds.Width <= 0 || Bounds.Height <= 0 || top == null)
            return;
        double scale = top.RenderScaling;
        var (w, h) = FitDevice((int)Math.Round(Bounds.Width * scale), (int)Math.Round(Bounds.Height * scale),
                               _frameWidth, _frameHeight, _aspect43);
        if (w <= 0 || h <= 0)
            return;
        _image.Width = w / scale;
        _image.Height = h / scale;
    }

    /// <summary>The picture's size in device pixels: the most of availW x availH that keeps its shape. Integer maths,
    /// so a frame of exactly the available size is exactly that size (floating point made it a pixel short).</summary>
    private static (int W, int H) FitDevice(int availW, int availH, int frameW, int frameH, bool aspect43)
    {
        long num = aspect43 ? 4 : frameW, den = aspect43 ? 3 : frameH;
        if (availW <= 0 || availH <= 0 || num <= 0 || den <= 0)
            return (0, 0);
        if (availW * den <= availH * num)
            return (availW, (int)(availW * den / num));   // width-limited
        return ((int)(availH * num / den), availH);
    }

    /// <summary>
    /// A key event from the window's tunnelling handlers while this exclusive layer is up. Presses count while the
    /// layer is showing in the active window; releases of keys we reported always go through so nothing sticks.
    /// The caller has already kept the fullscreen toggle (Alt+Enter) for itself and marks the event handled.
    /// </summary>
    public void HandleKey(KeyEventArgs e, bool pressed)
    {
        if (!CapturesKeyboard || _suspended && pressed || PhysicalKeyMap.Lookup(e) is not { } pc)
            return;
        var key = (pc.Scan, pc.Extended, pc.Vk);
        if (pressed)
        {
            if (!(IsVisible && _window is { IsActive: true }))
                return;
            // Ctrl+V and Shift+Insert paste into a module that's taking text (TE_IN_PASTE); the key itself isn't
            // sent, and its release goes by untouched since it never went in _held. Ctrl+Alt is AltGr: not a paste.
            var mods = e.KeyModifiers;
            if (_textInput && s_textOwner == this && (mods & KeyModifiers.Alt) == 0
                && ((e.Key == Key.V && mods == KeyModifiers.Control) || (e.Key == Key.Insert && mods == KeyModifiers.Shift)))
            {
                PasteFromClipboard();
                return;
            }
            _held.Add(key);   // auto-repeat presses go through, as WM_KEYDOWN repeats do on Windows
        }
        else if (!_held.Remove(key))
        {
            return;
        }
        _module.SendKey(pc.Scan, pc.Extended, pc.Vk, pressed);
    }

    /// <summary>
    /// Pastes the clipboard's text into the module (TE_IN_PASTE) when it has text input on: Ctrl+V / Shift+Insert, or
    /// the door's Paste request (TraceSessionManager, which checks the player just clicked or pressed a key here).
    /// </summary>
    public async void PasteFromClipboard()
    {
        if (!_textInput || _closed)
            return;
        string? text = null;
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                text = await clipboard.GetTextAsync();
        }
        catch { /* no clipboard owner, or it went away: nothing to paste */ }
        if (!string.IsNullOrEmpty(text) && _textInput && !_closed)
            _module.SendPaste(text);
    }

    private void ReleaseHeldKeys()
    {
        foreach (var (scan, extended, vk) in _held)
            _module.SendKey(scan, extended, vk, false);
        _held.Clear();
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        ReleaseHeldKeys();
        MouseLost();
        if (_exclusiveInput)
            _module.SendFocus(false);
    }

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        if (_exclusiveInput)
            _module.SendFocus(true);
        MouseRegained();
    }
}
