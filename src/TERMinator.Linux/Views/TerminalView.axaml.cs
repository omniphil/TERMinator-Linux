using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using SkiaSharp;
using TERMinator.Linux.Models;
using TERMinator.Linux.Services;
using TERMinator.Linux.ViewModels;

namespace TERMinator.Linux.Views;

public partial class TerminalView : UserControl
{
    private const int MaxImageChannels = 8;

    private readonly TerminalViewModel _viewModel;
    private readonly DispatcherTimer _statusTimer;
    private DateTime _connectTime;

    // Modem lights: the byte counters seen at the last tick, and when RD / SD go dark again
    private readonly DispatcherTimer _ledTimer;
    private long _ledLastRx, _ledLastTx, _rxLitUntil, _txLitUntil;
    private int _lastLoggedCursorType = -1;
#if DEBUG
    private int _screenUpdateCount;
    private int _dumpIndex;
#endif

    // Multi-channel image overlays (channels 0-7)
    private class ImageChannel
    {
        public bool Active;
        public bool PositionSet;
        public double Row = 1, Col = 1, CellW = 78, CellH = 23;
        public Grid Container = null!;
        public Image ImageControl = null!;
    }
    private readonly ImageChannel[] _imageChannels = new ImageChannel[MaxImageChannels];

    // TERMinator:Pic slots (Rainwire's pictures, native/apc/pic_bridge.c): eight of their own, separate from TAP+'s
    // channels and not affected by its commands or its hide toggle
    private const int MaxPicSlots = 8;
    private class PicSlot
    {
        public bool Active;
        public int Row1, Col1, Row2, Col2;   // 1-based cells, inclusive
        public int PixelW, PixelH;           // the picture's own size, for the aspect fit
        public int Generation;               // bumped by every Show and Hide: a decode that finishes late is dropped
        public PicFrame[]? Frames;           // one for a still picture, every frame of an animated GIF
        public int FrameIndex;
        public DispatcherTimer Timer = null!;
        public Grid Container = null!;
        public Image ImageControl = null!;
    }
    /// <summary>One decoded frame and how long it stays up.</summary>
    internal sealed record PicFrame(Bitmap Bitmap, TimeSpan Duration);
    private readonly PicSlot[] _picSlots = new PicSlot[MaxPicSlots];
    // All of a picture's decoded frames together (a long animated GIF stops there and loops what it has)
    private const long PicMaxDecodedBytes = 256L * 1024 * 1024;

    // Background underlay state
    private bool _bgActive;
    private double _bgRow = 1, _bgCol = 1, _bgCellW = 80, _bgCellH = 25;

    private bool _videoActive;
    private LibVLC? _libVLC;
    private LibVLCSharp.Shared.MediaPlayer? _mediaPlayer;
    private bool _logoActive;
    private double _logoRow = 1;
    private double _logoCol = 1;
    private double _logoCellW = 78;
    private double _logoCellH = 3;
    private bool _vizActive;
    private double _vizRow, _vizCol, _vizCellW, _vizCellH;
    private string _vizStyle = "bars";
    private double _videoRow = 1;
    private double _videoCol = 1;
    private double _videoCellW = 78;
    private double _videoCellH = 23;
    private bool _tapPlusHidden;
    private bool _scrollbackOverlayHidden;

    public event Action<int, int>? TerminalDimensionsChanged;
    public event Action? CancelConnectRequested;

    public Controls.TerminalCanvas TerminalCanvas => Terminal;

    /// <summary>
    /// Apply CRT settings to the live terminal. Thin pass-through so the call site matches the
    /// Windows client; the canvas owns the effect because in Avalonia the post-process is part
    /// of its own render pass rather than a detachable effect on the control.
    /// </summary>
    public void ApplyCrtSettings(Models.CrtSettings? settings) => Terminal.ApplyCrt(settings);

    /// <summary>
    /// Tears the effect off. Used as the recovery path when applying CRT settings fails, so the
    /// terminal stays usable.
    /// </summary>
    public void DisableCrtEffect() => Terminal.ApplyCrt(null);

    public double StatusBarActualHeight => StatusBar.Bounds.Height;

    public TerminalView() : this(new TerminalViewModel(
        App.ConnectionManager, App.SessionLogger, App.AudioService,
        App.SettingsService, App.CredentialService)) { }

    public TerminalView(TerminalViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;

        // Create 8 image channels dynamically and insert into OverlayGrid
        // They go after BgContainer (index 0) and Terminal (index 1), before VideoContainer
        for (int i = 0; i < MaxImageChannels; i++)
        {
            var img = new Image { Stretch = Stretch.Fill };
            var container = new Grid
            {
                Background = Brushes.Transparent,
                IsVisible = false,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top
            };
            container.Children.Add(img);
            _imageChannels[i] = new ImageChannel { Container = container, ImageControl = img };
            // Insert after Terminal (index 1), before VideoContainer
            OverlayGrid.Children.Insert(2 + i, container);
        }

        // ...and 8 Pic slots right after them (above the text and TAP+'s images, below video and logo)
        for (int i = 0; i < MaxPicSlots; i++)
        {
            var img = new Image { Stretch = Stretch.Fill };
            var container = new Grid
            {
                Background = Brushes.Transparent,
                IsVisible = false,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top
            };
            container.Children.Add(img);
            var slot = new PicSlot { Container = container, ImageControl = img, Timer = new DispatcherTimer() };
            slot.Timer.Tick += (_, _) => AdvancePicFrame(slot);
            _picSlots[i] = slot;
            OverlayGrid.Children.Insert(2 + MaxImageChannels + i, container);
        }

        // Forward terminal dimension changes
        Terminal.TerminalDimensionsChanged += (cols, rows) =>
            TerminalDimensionsChanged?.Invoke(cols, rows);

        // Update logging indicator
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TerminalViewModel.IsLogging))
            {
                LoggingIndicator.Text = _viewModel.IsLogging ? "LOGGING" : string.Empty;
            }
        };

        // Cancel connect button
        CancelConnectButton.Click += (_, _) => CancelConnectRequested?.Invoke();

        // Hide/show TAP+ overlays during scrollback
        Terminal.ScrollbackChanged += OnScrollbackChanged;

        // Reposition overlays when the terminal resizes
        Terminal.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty)
                RepositionOverlays();
        };
        // Pic slots also follow a mode change that doesn't resize the control (the canvas has its new picture by now)
        Terminal.TerminalDimensionsChanged += (_, _) => Dispatcher.UIThread.Post(RepositionPics);

        // Status bar update timer (1 second interval, like Android)
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) => UpdateStatusBar();

        // Modem lights flicker far faster than the status line updates
        _ledTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _ledTimer.Tick += (_, _) => UpdateModemLights();
        ApplyModemLightsSetting();

        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    public void FocusTerminal()
    {
        Terminal.Focus();
    }

    private DispatcherTimer? _toastTimer;

    /// <summary>Shows a short notice over the bottom of the terminal for two seconds (12.62).</summary>
    public void ShowToast(string message, double seconds = 2)
    {
        ToastText.Text = message;
        ToastBorder.IsVisible = true;
        _toastTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(seconds);
        _toastTimer.Tick -= HideToast;
        _toastTimer.Tick += HideToast;
        _toastTimer.Start();
    }

    private void HideToast(object? sender, EventArgs e)
    {
        _toastTimer?.Stop();
        ToastBorder.IsVisible = false;
    }

    /// <summary>
    /// Toggle TAP+ overlay visibility without destroying state.
    /// When hidden, overlays are collapsed but state is preserved.
    /// When shown again, active overlays are restored.
    /// </summary>
    public void SetTapPlusHidden(bool hidden)
    {
        _tapPlusHidden = hidden;
        if (hidden)
        {
            BgContainer.IsVisible = false;
            for (int i = 0; i < MaxImageChannels; i++)
                _imageChannels[i].Container.IsVisible = false;
            LogoContainer.IsVisible = false;
            VideoContainer.IsVisible = false;
            Visualizer.Hide();
        }
        else if (!_scrollbackOverlayHidden)
        {
            // Only restore if not also hidden by scrollback
            if (_bgActive) BgContainer.IsVisible = true;
            for (int i = 0; i < MaxImageChannels; i++)
                if (_imageChannels[i].Active)
                    _imageChannels[i].Container.IsVisible = true;
            if (_logoActive) LogoContainer.IsVisible = true;
            if (_videoActive) VideoContainer.IsVisible = true;
            if (_vizActive)
                ShowVisualizer(_vizRow, _vizCol, _vizCellW, _vizCellH, _vizStyle);
        }
    }

    private void OnScrollbackChanged(int scrollOffset)
    {
        bool inScrollback = scrollOffset > 0;
        if (inScrollback == _scrollbackOverlayHidden) return;
        _scrollbackOverlayHidden = inScrollback;

        // Pic slots go with the live screen too (whatever TAP+'s own toggle says)
        for (int i = 0; i < MaxPicSlots; i++)
            if (_picSlots[i].Active)
                _picSlots[i].Container.IsVisible = !inScrollback;

        if (inScrollback)
        {
            // Hide all TAP+ XAML overlays during scrollback
            BgContainer.IsVisible = false;
            for (int i = 0; i < MaxImageChannels; i++)
                _imageChannels[i].Container.IsVisible = false;
            LogoContainer.IsVisible = false;
            VideoContainer.IsVisible = false;
            Visualizer.Hide();
        }
        else if (!_tapPlusHidden)
        {
            // Restore overlays (only if not also hidden by menu toggle)
            if (_bgActive) BgContainer.IsVisible = true;
            for (int i = 0; i < MaxImageChannels; i++)
                if (_imageChannels[i].Active)
                    _imageChannels[i].Container.IsVisible = true;
            if (_logoActive) LogoContainer.IsVisible = true;
            if (_videoActive) VideoContainer.IsVisible = true;
            if (_vizActive)
                ShowVisualizer(_vizRow, _vizCol, _vizCellW, _vizCellH, _vizStyle);
        }
    }

    public void SetFontData(byte[] fontData)
    {
        Terminal.SetFontData(fontData);
    }

    /// <summary>
    /// Show the connecting overlay with BBS name.
    /// </summary>
    public void ShowConnecting(string bbsName)
    {
        ConnectingText.Text = $"Connecting to {bbsName}...";
        ResetConnectLog();
        ConnectLogLines.Children.Clear();
        ConnectStatusText.Text = string.Empty;
        ConnectingOverlay.IsVisible = true;
    }

    // ================================================================
    // Connection log (the connecting overlay's typewriter)
    // ================================================================

    private const int MaxConnectLogLines = 300;
    private static readonly FontFamily ConnectLogFont = new("JetBrains Mono, DejaVu Sans Mono, monospace");
    private static readonly IBrush ConnectLogCommandBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xAA, 0x00));
    private static readonly IBrush ConnectLogInfoBrush = new SolidColorBrush(Color.FromRgb(0x55, 0xDD, 0x55));
    private static readonly IBrush ConnectLogSuccessBrush = new SolidColorBrush(Color.FromRgb(0x33, 0xFF, 0x33));
    private static readonly IBrush ConnectLogErrorBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x55, 0x55));

    // Lines type out one at a time, like terminal output, instead of appearing all at once
    private readonly Queue<(string Text, IBrush Brush)> _connectLogQueue = new();
    private DispatcherTimer? _connectLogTimer;
    private TextBlock? _connectLogTypingLine;
    private string _connectLogTypingText = string.Empty;
    private int _connectLogTypingPos;
    private TaskCompletionSource? _connectLogTyped;

    /// <summary>Queues a line for the connecting overlay's log; it types out after any lines ahead of it.</summary>
    public void AppendConnectLog(string text, ConnectionTracer.LineKind kind)
    {
        var brush = kind switch
        {
            ConnectionTracer.LineKind.Command => ConnectLogCommandBrush,
            ConnectionTracer.LineKind.Success => ConnectLogSuccessBrush,
            ConnectionTracer.LineKind.Error => ConnectLogErrorBrush,
            _ => ConnectLogInfoBrush
        };
        _connectLogQueue.Enqueue((kind == ConnectionTracer.LineKind.Command ? "> " + text : text, brush));

        if (_connectLogTimer == null)
        {
            _connectLogTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            _connectLogTimer.Tick += (_, _) => TypeConnectLog();
            _connectLogTimer.Start();
        }
    }

    /// <summary>
    /// Completes once every queued log line has finished typing, or after <paramref name="maxWait"/>, whichever comes
    /// first. Lets a successful connect finish showing the log (ending on CONNECT) before the overlay closes.
    /// </summary>
    public Task WhenConnectLogTypedAsync(TimeSpan maxWait)
    {
        if (_connectLogTimer == null)
            return Task.CompletedTask;
        _connectLogTyped ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return Task.WhenAny(_connectLogTyped.Task, Task.Delay(maxWait));
    }

    private void TypeConnectLog()
    {
        // A few characters per tick, faster when lines are backing up (SSH can report several at once)
        int budget = 3 + _connectLogQueue.Count * 3;
        while (budget > 0)
        {
            if (_connectLogTypingLine == null)
            {
                if (!_connectLogQueue.TryDequeue(out var next))
                {
                    StopConnectLogTyping();
                    break;
                }
                _connectLogTypingText = next.Text;
                _connectLogTypingPos = 0;
                _connectLogTypingLine = new TextBlock
                {
                    FontFamily = ConnectLogFont,
                    FontSize = 15,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = next.Brush,
                    Margin = new Thickness(0, 0, 0, 2)
                };
                ConnectLogLines.Children.Add(_connectLogTypingLine);
                while (ConnectLogLines.Children.Count > MaxConnectLogLines)
                    ConnectLogLines.Children.RemoveAt(0);
            }

            int take = Math.Min(budget, _connectLogTypingText.Length - _connectLogTypingPos);
            _connectLogTypingPos += take;
            budget -= take;

            bool lineDone = _connectLogTypingPos >= _connectLogTypingText.Length;
            _connectLogTypingLine.Text = _connectLogTypingText[.._connectLogTypingPos] + (lineDone ? string.Empty : "█");
            if (lineDone)
                _connectLogTypingLine = null;
        }
        // Keep the newest line in view (the new text is measured on the next layout pass, so scroll after it)
        Dispatcher.UIThread.Post(() => ConnectLogScroll.ScrollToEnd(), DispatcherPriority.Loaded);
    }

    private void StopConnectLogTyping()
    {
        _connectLogTimer?.Stop();
        _connectLogTimer = null;
        _connectLogTyped?.TrySetResult();
        _connectLogTyped = null;
    }

    /// <summary>Drops any untyped lines and stops the typing (the overlay is being shown fresh or hidden).</summary>
    private void ResetConnectLog()
    {
        _connectLogQueue.Clear();
        _connectLogTypingLine = null;
        StopConnectLogTyping();
    }

    /// <summary>The live status line under the log: current stage and how long it has been there.</summary>
    public void SetConnectStatus(string text) => ConnectStatusText.Text = text;

    /// <summary>
    /// Hide the connecting overlay and start the status bar.
    /// </summary>
    public void ShowConnected(SavedConnection connection)
    {
        ConnectingOverlay.IsVisible = false;
        ResetConnectLog();

        // Set status bar fields
        string protocol = connection.Protocol.ToString().ToUpper();
        StatusName.Text = $"{connection.Name} ({protocol})";

        var (cols, rows) = ConnectionManager.GetScreenModeDimensions(connection.ScreenMode);
        StatusMode.Text = $"{cols}x{rows}";

        _connectTime = DateTime.UtcNow;
        StatusTime.Text = "00:00:00";
        StatusBytes.Text = "\u2191 0  \u2193 0";

        _statusTimer.Start();

        _ledLastRx = _ledLastTx = 0;
        _rxLitUntil = _txLitUntil = 0;
        ApplyModemLightsSetting();
        _ledTimer.Start();
    }

    /// <summary>
    /// Hide the connecting overlay (e.g. on cancel or failure).
    /// </summary>
    public void HideConnecting()
    {
        ConnectingOverlay.IsVisible = false;
        ResetConnectLog();
        _statusTimer.Stop();
        _ledTimer.Stop();
        ModemPanel.AllOff();
        StatusName.Text = string.Empty;
        StatusTime.Text = string.Empty;
        StatusMode.Text = string.Empty;
        StatusBytes.Text = string.Empty;
    }

    private void UpdateStatusBar()
    {
        if (!_viewModel.IsConnected) return;

        // Connection duration
        var elapsed = DateTime.UtcNow - _connectTime;
        StatusTime.Text = elapsed.ToString(@"hh\:mm\:ss");

        // Bytes sent/received
        var (sent, received, _) = _viewModel.ConnectionManager.GetConnectionStats();
        StatusBytes.Text = $"\u2191 {FormatBytes(sent)}  \u2193 {FormatBytes(received)}";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024 * 1024)
            return $"{bytes / (1024.0 * 1024.0):F1}M";
        if (bytes >= 1024)
            return $"{bytes / 1024.0:F1}K";
        return $"{bytes}B";
    }

    /// <summary>Shows or hides the modem lights per the setting.</summary>
    public void ApplyModemLightsSetting() => ModemPanel.IsVisible = App.SettingsService.Settings.ShowModemLights;

    /// <summary>
    /// One tick of the modem lights: RD / SD light when their byte counter moved (flickering while data flows, held
    /// briefly after), CD and OH while connected, HS at 9600 bps and up (or full speed), TR and MR while a session is up.
    /// </summary>
    private void UpdateModemLights()
    {
        if (!_viewModel.IsConnected)
        {
            _ledTimer.Stop();
            ModemPanel.AllOff();
            return;
        }
        if (!ModemPanel.IsVisible)
            return;

        var cm = _viewModel.ConnectionManager;
        var (sent, received, _) = cm.GetConnectionStats();
        long now = Environment.TickCount64;

        bool rxMoved = received != _ledLastRx;
        bool txMoved = sent != _ledLastTx;
        _ledLastRx = received;
        _ledLastTx = sent;
        if (rxMoved)
            _rxLitUntil = now + 90;
        if (txMoved)
            _txLitUntil = now + 90;

        // A real RD light flickers with each burst, so skip the odd tick while data keeps coming
        bool rd = rxMoved ? Random.Shared.Next(4) != 0 : now < _rxLitUntil;
        bool sd = txMoved ? Random.Shared.Next(4) != 0 : now < _txLitUntil;

        int bps = cm.EffectiveBaud;
        bool highSpeed = bps == 0 || bps >= 9600;
        ModemPanel.Set(hs: highSpeed, aa: false, cd: true, oh: true, rd: rd, sd: sd, tr: true, mr: true);
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        // Re-hook events that were unhooked in OnDetachedFromVisualTree
        // (remove first to avoid double-subscription on first load)
        Terminal.InputGenerated -= OnTerminalInput;
        _viewModel.ScreenUpdatedEvent -= OnScreenUpdated;
        _viewModel.ConnectionManager.FontChanged -= OnFontChanged;
        _viewModel.TapPlusCommandReceived -= OnTapPlusCommand;
        _viewModel.ConnectionManager.PicCommandReceived -= OnPicCommand;
        Terminal.InputGenerated += OnTerminalInput;
        _viewModel.ScreenUpdatedEvent += OnScreenUpdated;
        _viewModel.ConnectionManager.FontChanged += OnFontChanged;
        _viewModel.TapPlusCommandReceived += OnTapPlusCommand;
        _viewModel.ConnectionManager.PicCommandReceived += OnPicCommand;

        FocusTerminal();
    }

    private void OnTerminalInput(byte[] data)
    {
        if (_viewModel.IsConnected)
        {
            _viewModel.ConnectionManager.SendData(data);

            // A keystroke or a key's escape sequence clicks; a paste arrives as one long run and doesn't
            if (data.Length <= 8)
                App.KeyClickService.Play(space: data.Length == 1 && data[0] == 0x20);
        }
    }

    private void OnScreenUpdated()
    {
        var cm = _viewModel.ConnectionManager;

        // Update palette
        Terminal.UpdatePalette(cm.Palette);

        // Update screen
        int width = cm.ScreenWidth;
        int height = cm.ScreenHeight;
        int cursorX = cm.CursorX;
        int cursorY = cm.CursorY;
        // UserHideCursor is the right-click "Hide Cursor" toggle. ANDed in here rather than
        // in the canvas so the BBS's own cursor state still wins when it hides it.
        bool cursorVisible = cm.CursorVisible && !Terminal.UserHideCursor;
        int cursorType = cm.CursorType;
        int cursorStart = cm.CursorStart;
        int cursorEnd = cm.CursorEnd;
        int cursorBlinks = cm.CursorBlinks;
        int ciolibFg = cm.CiolibFg;

        // Log cursor type changes for diagnostics
        if (cursorType != _lastLoggedCursorType)
        {
            string[] names = { "NOCURSOR", "SOLIDCURSOR", "NORMALCURSOR" };
            string name = cursorType >= 0 && cursorType <= 2 ? names[cursorType] : "?";
            Debug.WriteLine($"TerminalView: cursor type changed to {name}({cursorType}), visible={cursorVisible} start={cursorStart} end={cursorEnd} blinks={cursorBlinks}");
            _lastLoggedCursorType = cursorType;
        }

        Terminal.UpdateScreen(cm.ScreenBuffer, cm.ScreenFg, cm.ScreenBg, width, height, cursorX, cursorY, cursorVisible, cursorType,
                              cursorStart, cursorEnd, cursorBlinks, ciolibFg);

#if DEBUG
        // Dump raw screen data every 50 updates, keep last 10 rolling files (Debug builds only: two file
        // writes per 50 redraws stall a graphics door)
        _screenUpdateCount++;
        if (_screenUpdateCount % 50 == 1)
        {
            try
            {
                var debugDir = Path.Combine(AppSettings.GetAppDataDir(), "debug");
                Directory.CreateDirectory(debugDir);

                // Native vmem_cell dump
                var path = Path.Combine(debugDir, $"screen_dump_{_dumpIndex}.txt");
                NativeBridge.syncterm_debug_dump_screen(path, 10);

                // C# side dump: packed cells + font info + palette
                var csPath = Path.Combine(debugDir, $"cs_dump_{_dumpIndex}.txt");
                DumpCSharpState(csPath, cm.ScreenBuffer, width, height);

                _dumpIndex = (_dumpIndex + 1) % 10;
            }
            catch { }
        }
#endif

        Terminal.RefreshSixelOverlay();
    }

    private void DumpCSharpState(string path, int[] screenBuffer, int width, int height)
    {
        using var f = new StreamWriter(path);
        f.WriteLine($"Screen: {width}x{height}");
        f.WriteLine($"ScreenBuffer.Length: {screenBuffer.Length}");
        f.WriteLine($"FontDataHeight: {Terminal.FontDataHeight}");
        f.WriteLine($"HasSixelData: {NativeBridge.syncterm_has_sixel_data()}");
        f.WriteLine($"CurrentFontId: {NativeBridge.syncterm_get_current_font_id()}");
        f.WriteLine($"BitmapSize: {Terminal.BitmapWidth}x{Terminal.BitmapHeight}");

        // Dump font bitmap data for chars 0x2F(/), 0x5F(_), 0xAF(>>), 0xDB(block)
        int[] testChars = { 0x2F, 0x5F, 0xAF, 0xDB };
        f.WriteLine("\nFont bitmap data for test characters:");
        foreach (int ch in testChars)
        {
            var rows = Terminal.GetFontRows(ch);
            if (rows == null)
                f.WriteLine($"  ch=0x{ch:X2}: NO FONT DATA");
            else
                f.WriteLine($"  ch=0x{ch:X2}: {string.Join(" ", rows.Select(b => $"{b:X2}"))}");
        }

        // Dump first 3 rows of packed cell data
        int rowsToDump = Math.Min(3, height);
        for (int row = 0; row < rowsToDump; row++)
        {
            f.WriteLine($"\nRow {row} (packed cells):");
            for (int col = 0; col < width; col++)
            {
                int idx = row * width + col;
                if (idx >= screenBuffer.Length) break;
                int cell = screenBuffer[idx];
                int ch = cell & 0xFF;
                int attr = (cell >> 8) & 0xFF;
                int fg = (cell >> 16) & 0x7F;
                int bg = (cell >> 24) & 0xFF;
                // Only dump non-space cells to keep output manageable
                if (ch != 0x20 || fg != 0 || bg != 0)
                    f.WriteLine($"  [{col:D2}] packed=0x{cell:X8} ch=0x{ch:X2} attr=0x{attr:X2} fg={fg} bg={bg}");
            }
        }
    }

    private void OnFontChanged(byte[] fontData)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            Debug.WriteLine($"TerminalView: reloading font ({fontData.Length} bytes)");
            Terminal.SetFontData(fontData);
        });
    }

    // ================================================================
    // TAP+ Media Commands (image/video overlays)
    // ================================================================

    private void OnTapPlusCommand(string command)
    {
        if (!_viewModel.IsTapPlusEnabled) return;

        var parts = command.Split(';');
        if (parts.Length == 0) return;

        switch (parts[0].ToLowerInvariant())
        {
            // --- Multi-channel image overlays ---
            case "imgshow":
                if (parts.Length >= 2)
                {
                    int ch = parts.Length >= 3 && int.TryParse(parts[2], out int showCh) ? showCh : 0;
                    if (ch >= 0 && ch < MaxImageChannels) ShowImage(parts[1], ch);
                }
                break;
            case "imghide":
                {
                    int ch = parts.Length >= 2 && int.TryParse(parts[1], out int hideCh) ? hideCh : 0;
                    if (ch >= 0 && ch < MaxImageChannels) HideImage(ch);
                }
                break;
            case "imgmove":
                if (parts.Length >= 3 &&
                    double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double imgRow) &&
                    double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double imgCol))
                {
                    int ch = parts.Length >= 4 && int.TryParse(parts[3], out int moveCh) ? moveCh : 0;
                    if (ch >= 0 && ch < MaxImageChannels)
                    {
                        _imageChannels[ch].Row = imgRow;
                        _imageChannels[ch].Col = imgCol;
                        _imageChannels[ch].PositionSet = true;
                        UpdateImagePosition(ch);
                    }
                }
                break;
            case "imgresize":
                if (parts.Length >= 3 &&
                    double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double imgW) &&
                    double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double imgH))
                {
                    int ch = parts.Length >= 4 && int.TryParse(parts[3], out int resizeCh) ? resizeCh : 0;
                    if (ch >= 0 && ch < MaxImageChannels)
                    {
                        _imageChannels[ch].CellW = imgW;
                        _imageChannels[ch].CellH = imgH;
                        _imageChannels[ch].PositionSet = true;
                        UpdateImagePosition(ch);
                    }
                }
                break;
            case "imgset":
                // Two-corner format: imgset;row1;col1;row2;col2[;channel]
                if (parts.Length >= 5 &&
                    double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double sRow1) &&
                    double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double sCol1) &&
                    double.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double sRow2) &&
                    double.TryParse(parts[4], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double sCol2))
                {
                    int ch = parts.Length >= 6 && int.TryParse(parts[5], out int setCh) ? setCh : 0;
                    if (ch >= 0 && ch < MaxImageChannels)
                    {
                        _imageChannels[ch].Row = sRow1;
                        _imageChannels[ch].Col = sCol1;
                        _imageChannels[ch].CellW = sCol2 - sCol1 + 1;
                        _imageChannels[ch].CellH = sRow2 - sRow1 + 1;
                        _imageChannels[ch].PositionSet = true;
                        UpdateImagePosition(ch);
                    }
                }
                break;

            // --- Logo overlay (unchanged, separate from channels) ---
            case "logoshow":
                if (parts.Length >= 2) ShowLogo(parts[1]);
                break;
            case "logohide":
                HideLogo();
                break;
            case "logoset":
                if (parts.Length >= 5 &&
                    double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double lRow1) &&
                    double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double lCol1) &&
                    double.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double lRow2) &&
                    double.TryParse(parts[4], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double lCol2))
                {
                    _logoRow = lRow1;
                    _logoCol = lCol1;
                    _logoCellW = lCol2 - lCol1 + 1;
                    _logoCellH = lRow2 - lRow1 + 1;
                    UpdateLogoPosition();
                }
                break;

            // --- Video overlay ---
            case "seek":
                if (parts.Length >= 2 && int.TryParse(parts[1], out int seekMs) && _videoActive && _mediaPlayer != null)
                {
                    try { _mediaPlayer.Time = Math.Max(0, seekMs); }
                    catch { /* ignore */ }
                }
                break;
            case "play":
                if (parts.Length >= 2) TryPlayVideo(parts[1]);
                break;
            case "stop":
                if (parts.Length >= 2 && int.TryParse(parts[1], out int stopCh) && stopCh >= 0 && stopCh < MaxImageChannels)
                {
                    HideImage(stopCh);
                }
                else
                {
                    for (int i = 0; i < MaxImageChannels; i++) HideImage(i);
                    HideVideo();
                    HideVisualizer();
                }
                break;
            case "stopall":
                StopAllTapPlus();
                break;
            case "video":
                if (parts.Length >= 2 && parts[1] == "on")
                {
                    if (parts.Length >= 6 &&
                        double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double vRow1) &&
                        double.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double vCol1) &&
                        double.TryParse(parts[4], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double vRow2) &&
                        double.TryParse(parts[5], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double vCol2))
                    {
                        _videoRow = vRow1;
                        _videoCol = vCol1;
                        _videoCellW = vCol2 - vCol1 + 1;
                        _videoCellH = vRow2 - vRow1 + 1;
                        UpdateVideoPosition();
                    }
                    if (_videoActive && !_tapPlusHidden)
                        VideoContainer.IsVisible = true;
                }
                else if (parts.Length >= 2 && parts[1] == "off")
                    VideoContainer.IsVisible = false;
                break;
            case "videomove":
                if (parts.Length >= 3 &&
                    double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double vmRow) &&
                    double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double vmCol))
                {
                    _videoRow = vmRow;
                    _videoCol = vmCol;
                    UpdateVideoPosition();
                }
                break;
            case "videoset":
                if (parts.Length >= 5 &&
                    double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double vsRow1) &&
                    double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double vsCol1) &&
                    double.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double vsRow2) &&
                    double.TryParse(parts[4], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double vsCol2))
                {
                    _videoRow = vsRow1;
                    _videoCol = vsCol1;
                    _videoCellW = vsCol2 - vsCol1 + 1;
                    _videoCellH = vsRow2 - vsRow1 + 1;
                    UpdateVideoPosition();
                }
                break;

            // --- Visualizer ---
            case "visualizer":
                if (parts.Length >= 2 && parts[1] == "on")
                {
                    double vizRow = 1, vizCol = 1;
                    double vizW = Terminal.Columns, vizH = Terminal.Rows;
                    string vizStyle = "bars";
                    if (parts.Length >= 6 &&
                        double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double vzRow1) &&
                        double.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double vzCol1) &&
                        double.TryParse(parts[4], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double vzRow2) &&
                        double.TryParse(parts[5], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double vzCol2))
                    {
                        vizRow = vzRow1;
                        vizCol = vzCol1;
                        vizW = vzCol2 - vzCol1 + 1;
                        vizH = vzRow2 - vzRow1 + 1;
                        if (parts.Length >= 7) vizStyle = parts[6];
                    }
                    ShowVisualizer(vizRow, vizCol, vizW, vizH, vizStyle);
                }
                else if (parts.Length >= 2 && parts[1] == "off")
                {
                    HideVisualizer();
                }
                break;

            // --- Background image underlay ---
            case "bgshow":
                if (parts.Length >= 2) ShowBackground(parts[1]);
                break;
            case "bgset":
                if (parts.Length >= 5 &&
                    double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double bgR1) &&
                    double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double bgC1) &&
                    double.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double bgR2) &&
                    double.TryParse(parts[4], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double bgC2))
                {
                    _bgRow = bgR1;
                    _bgCol = bgC1;
                    _bgCellW = bgC2 - bgC1 + 1;
                    _bgCellH = bgR2 - bgR1 + 1;
                    Terminal.SetBackgroundRegion((int)bgR1, (int)bgC1, (int)bgR2, (int)bgC2);
                    UpdateBackgroundPosition();
                }
                break;
            case "bghide":
                HideBackground();
                break;
            case "bgopacity":
                if (parts.Length >= 2 &&
                    double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double bgOp))
                {
                    BgContainer.Opacity = Math.Clamp(bgOp, 0.0, 1.0);
                }
                break;

            // --- Tile atlas system ---
            case "tileset":
                // tileset;filename;tileW;tileH
                if (parts.Length >= 4 &&
                    int.TryParse(parts[2], out int tileW) &&
                    int.TryParse(parts[3], out int tileH))
                {
                    LoadTileset(parts[1], tileW, tileH);
                }
                break;
            case "tilemap":
                // tilemap;r1;c1;r2;c2
                if (parts.Length >= 5 &&
                    int.TryParse(parts[1], out int tmR1) &&
                    int.TryParse(parts[2], out int tmC1) &&
                    int.TryParse(parts[3], out int tmR2) &&
                    int.TryParse(parts[4], out int tmC2))
                {
                    Terminal.SetTileRegion(tmR1, tmC1, tmR2, tmC2);
                }
                break;
            case "tile":
                // tile;screenRow;screenCol;spriteId
                if (parts.Length >= 4 &&
                    int.TryParse(parts[1], out int tRow) &&
                    int.TryParse(parts[2], out int tCol) &&
                    int.TryParse(parts[3], out int tId))
                {
                    Terminal.SetTile(tRow, tCol, tId);
                }
                break;
            case "tiles":
                // tiles;row;startCol;id1,id2,id3,...
                if (parts.Length >= 4 &&
                    int.TryParse(parts[1], out int tsRow) &&
                    int.TryParse(parts[2], out int tsStartCol))
                {
                    var ids = parts[3].Split(',')
                        .Select(s => int.TryParse(s.Trim(), out int v) ? v : -1)
                        .ToArray();
                    Terminal.SetTileRow(tsRow, tsStartCol, ids);
                }
                break;
            case "tilefill":
                // tilefill;r1;c1;r2;c2;spriteId
                if (parts.Length >= 6 &&
                    int.TryParse(parts[1], out int tfR1) &&
                    int.TryParse(parts[2], out int tfC1) &&
                    int.TryParse(parts[3], out int tfR2) &&
                    int.TryParse(parts[4], out int tfC2) &&
                    int.TryParse(parts[5], out int tfId))
                {
                    Terminal.FillTiles(tfR1, tfC1, tfR2, tfC2, tfId);
                }
                break;
            case "tileopacity":
                if (parts.Length >= 2 &&
                    double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double tileOp))
                {
                    Terminal.SetTileOpacity(tileOp);
                }
                break;
            case "tilemapclear":
                Terminal.ClearTiles();
                break;
            case "tilescale":
                if (parts.Length >= 2 && int.TryParse(parts[1], out int ts))
                {
                    Terminal.SetTileScale(ts);
                }
                break;
            case "tilebegin":
                Terminal.TileBeginBatch();
                break;
            case "tileend":
                Terminal.TileEndBatch();
                break;
            case "tilerle":
                if (parts.Length >= 4 &&
                    int.TryParse(parts[1], out int rleRow) &&
                    int.TryParse(parts[2], out int rleStartCol))
                {
                    var tokens = parts[3].Split(',')
                        .Select(s => int.TryParse(s.Trim(), out int v) ? v : -1)
                        .ToArray();
                    if (tokens.Length >= 2 && tokens.Length % 2 == 0)
                        Terminal.TileRle(rleRow, rleStartCol, tokens);
                }
                break;
            case "tilescroll":
                if (parts.Length >= 3 &&
                    int.TryParse(parts[1], out int scrollDx) &&
                    int.TryParse(parts[2], out int scrollDy))
                {
                    Terminal.TileScroll(scrollDx, scrollDy);
                }
                break;
            case "tilecopy":
                if (parts.Length >= 7 &&
                    int.TryParse(parts[1], out int cpSrcRow) &&
                    int.TryParse(parts[2], out int cpSrcCol) &&
                    int.TryParse(parts[3], out int cpDstRow) &&
                    int.TryParse(parts[4], out int cpDstCol) &&
                    int.TryParse(parts[5], out int cpW) &&
                    int.TryParse(parts[6], out int cpH))
                {
                    Terminal.TileCopy(cpSrcRow, cpSrcCol, cpDstRow, cpDstCol, cpW, cpH);
                }
                break;
            case "tileswap":
                if (parts.Length >= 3 &&
                    int.TryParse(parts[1], out int swapId1) &&
                    int.TryParse(parts[2], out int swapId2))
                {
                    Terminal.TileSwap(swapId1, swapId2);
                }
                break;
            case "tilesetswap":
                if (parts.Length >= 4 &&
                    int.TryParse(parts[2], out int swTileW) &&
                    int.TryParse(parts[3], out int swTileH))
                {
                    LoadTileset(parts[1], swTileW, swTileH);
                }
                break;

            // --- TAP+ capability query (BBS → Client → BBS response) ---
            case "tapquery":
                SendTapReply();
                break;
        }
    }

    private void SendTapReply()
    {
        var data = System.Text.Encoding.ASCII.GetBytes("\x1b]800;tapreply\x1b\\");
        _viewModel.ConnectionManager.SendData(data);
    }

    // ================================================================
    // Multi-Channel Image Overlays
    // ================================================================

    private async void ShowImage(string displayName, int channel)
    {
        if (channel == 0) HideVideo(); // Legacy behavior: imgshow on ch0 hides video

        var filePath = _viewModel.ResolveMediaFile(displayName);

        // Wait for download if not yet cached
        if (filePath == null)
        {
            for (int i = 0; i < 40; i++)
            {
                await Task.Delay(250);
                filePath = _viewModel.ResolveMediaFile(displayName);
                if (filePath != null) break;
            }
            if (filePath == null) return;
        }

        try
        {
            var ch = _imageChannels[channel];
            // Avalonia supports GIF natively in Image control, no special handling needed
            var bitmap = new Bitmap(filePath);
            ch.ImageControl.Source = bitmap;

            // Set default 80% centered position unless imgset/imgmove/imgresize already set it
            if (!ch.PositionSet)
            {
                int cols = Terminal.Columns;
                int rows = Terminal.Rows;
                if (cols > 0 && rows > 0)
                {
                    ch.CellW = cols * 0.8;
                    ch.CellH = rows * 0.8;
                    ch.Col = (cols - ch.CellW) / 2.0 + 1;
                    ch.Row = (rows - ch.CellH) / 2.0 + 1;
                }
            }
            if (!_tapPlusHidden)
                ch.Container.IsVisible = true;
            ch.Active = true;
            UpdateImagePosition(channel);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"TerminalView: Error loading image on channel {channel}: {ex.Message}");
        }
    }

    private void HideImage(int channel)
    {
        var ch = _imageChannels[channel];
        if (!ch.Active) return;
        ch.Container.IsVisible = false;
        ch.ImageControl.Source = null;
        ch.Active = false;
        ch.PositionSet = false;
    }

    // ================================================================
    // TERMinator:Pic slots (Rainwire's pictures, native/apc/pic_bridge.c)
    // ================================================================

    /// <summary>From the poll thread; handled on the UI thread in the order the board sent them.</summary>
    private void OnPicCommand(string command) => Dispatcher.UIThread.Post(() => HandlePicCommand(command));

    /// <summary>
    /// "Show;id=n;rect=r1,c1,r2,c2;path=..." (the native side checked the name and found the file in the board's
    /// cache), "Hide;id=n", or "HideAll" (also queued by a full screen clear, in stream order).
    /// </summary>
    private void HandlePicCommand(string command)
    {
        if (command == "HideAll")
        {
            HideAllPics();
            return;
        }
        if (command.StartsWith("Hide;id=", StringComparison.Ordinal))
        {
            if (int.TryParse(command.AsSpan(8), out int hideId) && hideId >= 0 && hideId < MaxPicSlots)
                HidePic(hideId);
            return;
        }
        if (!command.StartsWith("Show;", StringComparison.Ordinal))
            return;
        // Queued just before the call ended: the disconnect has cleared the slots already
        if (_viewModel.ConnectionManager.State == ConnectionState.Disconnected)
            return;

        // path= is last and runs to the end (a folder name may hold ';')
        int pathAt = command.IndexOf(";path=", StringComparison.Ordinal);
        if (pathAt < 0) return;
        string path = command[(pathAt + 6)..];
        int id = -1;
        int[]? rect = null;
        foreach (var part in command[5..pathAt].Split(';'))
        {
            if (part.StartsWith("id=", StringComparison.Ordinal) && int.TryParse(part.AsSpan(3), out int n))
                id = n;
            else if (part.StartsWith("rect=", StringComparison.Ordinal))
            {
                var nums = part[5..].Split(',');
                if (nums.Length == 4 && nums.All(x => int.TryParse(x, out _)))
                    rect = nums.Select(int.Parse).ToArray();
            }
        }
        if (id < 0 || id >= MaxPicSlots || rect == null || path.Length == 0)
            return;
        ShowPic(id, path, rect[0], rect[1], rect[2], rect[3]);
    }

    private async void ShowPic(int id, string path, int row1, int col1, int row2, int col2)
    {
        var slot = _picSlots[id];
        int generation = ++slot.Generation;
        PicFrame[]? frames = null;
        try
        {
            string full = Path.GetFullPath(path);
            if (!File.Exists(full)) return;
            // Decoded off the UI thread (a long GIF is many frames); a busy slot keeps its old picture until then
            frames = await Task.Run(() => DecodePicFrames(full));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"TerminalView: Pic slot {id} couldn't load {path}: {ex.Message}");
        }

        // A later Show or Hide on this slot (a HideAll, a screen clear, the disconnect) came in meanwhile
        if (generation != slot.Generation)
        {
            DisposePicFrames(frames);
            return;
        }
        if (frames == null || frames.Length == 0)
        {
            HidePic(id);
            return;
        }

        // A Show on a busy slot replaces it
        StopPicFrames(slot);
        slot.Frames = frames;
        slot.FrameIndex = 0;
        slot.ImageControl.Source = frames[0].Bitmap;
        slot.PixelW = frames[0].Bitmap.PixelSize.Width;
        slot.PixelH = frames[0].Bitmap.PixelSize.Height;
        slot.Row1 = row1;
        slot.Col1 = col1;
        slot.Row2 = row2;
        slot.Col2 = col2;
        slot.Active = true;
        slot.Container.IsVisible = !_scrollbackOverlayHidden;
        UpdatePicPosition(id);
        // An animated GIF loops forever whatever its own loop count says; one frame just shows still
        if (frames.Length > 1)
        {
            slot.Timer.Interval = frames[0].Duration;
            slot.Timer.Start();
        }
    }

    /// <summary>The next frame of an animated picture, held for that frame's own duration.</summary>
    private void AdvancePicFrame(PicSlot slot)
    {
        if (!slot.Active || slot.Frames == null || slot.Frames.Length < 2)
        {
            slot.Timer.Stop();
            return;
        }
        slot.FrameIndex = (slot.FrameIndex + 1) % slot.Frames.Length;
        var frame = slot.Frames[slot.FrameIndex];
        slot.ImageControl.Source = frame.Bitmap;
        slot.Timer.Stop();
        slot.Timer.Interval = frame.Duration;
        slot.Timer.Start();
    }

    /// <summary>
    /// Every frame of the file, composited (TAP+'s plain Bitmap shows only a GIF's first frame). Skia's codecs read
    /// GIF, PNG, JPEG, WebP and BMP; the whole file is read first, so the cache file isn't held open (the board may
    /// store it again). Runs on a worker thread. Also the header of a Rainwire menu (Controls/RainwireMenu.cs).
    /// </summary>
    internal static PicFrame[]? DecodePicFrames(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec == null)
        {
            // Not one Skia's codecs know: let Avalonia try it, still
            using var stream = new MemoryStream(bytes);
            return new[] { new PicFrame(new Bitmap(stream), TimeSpan.Zero) };
        }

        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        if (info.Width <= 0 || info.Height <= 0)
            return null;
        var frameInfos = codec.FrameInfo;
        long frameBytes = (long)info.RowBytes * info.Height;
        int count = (int)Math.Clamp(PicMaxDecodedBytes / frameBytes, 1, Math.Max(1, codec.FrameCount));

        var decoded = new SKBitmap?[count];
        var frames = new List<PicFrame>(count);
        try
        {
            for (int i = 0; i < count; i++)
            {
                var bmp = new SKBitmap(info);
                decoded[i] = bmp;
                // A frame drawn over an earlier one starts from that one's finished picture (Skia then applies its
                // disposal); one that isn't starts from transparent
                int prior = i < frameInfos.Length ? frameInfos[i].RequiredFrame : -1;
                if (prior >= i || (prior >= 0 && decoded[prior] == null))
                    prior = -1;
                using (var canvas = new SKCanvas(bmp))
                {
                    canvas.Clear(SKColors.Transparent);
                    if (prior >= 0)
                        canvas.DrawBitmap(decoded[prior]!, 0, 0);
                }
                var options = prior >= 0 ? new SKCodecOptions(i, prior) : new SKCodecOptions(i);
                var result = codec.GetPixels(info, bmp.GetPixels(), options);
                if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
                {
                    if (frames.Count == 0) return null;
                    break;   // a damaged frame: loop the ones before it
                }

                // As browsers do: 0 and 10 ms (and anything faster) play at 100 ms, nothing under 20 ms
                int ms = i < frameInfos.Length ? frameInfos[i].Duration : 0;
                if (ms <= 10) ms = 100;
                ms = Math.Max(ms, 20);
                frames.Add(new PicFrame(new Bitmap(Avalonia.Platform.PixelFormat.Bgra8888,
                    Avalonia.Platform.AlphaFormat.Premul, bmp.GetPixels(), new PixelSize(info.Width, info.Height),
                    new Vector(96, 96), bmp.RowBytes), TimeSpan.FromMilliseconds(ms)));
            }
        }
        catch
        {
            DisposePicFrames(frames.ToArray());
            throw;
        }
        finally
        {
            foreach (var bmp in decoded) bmp?.Dispose();
        }
        return frames.Count > 0 ? frames.ToArray() : null;
    }

    internal static void DisposePicFrames(PicFrame[]? frames)
    {
        if (frames == null) return;
        foreach (var frame in frames) frame.Bitmap.Dispose();
    }

    /// <summary>Stops a slot's animation and frees its frames (the slot shows nothing afterwards).</summary>
    private static void StopPicFrames(PicSlot slot)
    {
        slot.Timer.Stop();
        slot.ImageControl.Source = null;
        DisposePicFrames(slot.Frames);
        slot.Frames = null;
        slot.FrameIndex = 0;
    }

    private void HidePic(int id)
    {
        var slot = _picSlots[id];
        slot.Generation++;   // a Show still decoding for this slot is dropped too
        if (!slot.Active) return;
        slot.Container.IsVisible = false;
        StopPicFrames(slot);
        slot.Active = false;
    }

    /// <summary>Hides every Pic slot: HideAll, a full screen clear, and disconnect.</summary>
    public void HideAllPics()
    {
        for (int i = 0; i < MaxPicSlots; i++) HidePic(i);
    }

    private void RepositionPics()
    {
        for (int i = 0; i < MaxPicSlots; i++)
            UpdatePicPosition(i);
    }

    /// <summary>Fits the picture inside its cell rect keeping its aspect, centred (as TAP+'s UpdateImagePosition).</summary>
    private void UpdatePicPosition(int id)
    {
        var slot = _picSlots[id];
        if (!slot.Active) return;

        var bbsRect = Terminal.GetBbsScreenRect();
        int cols = Terminal.Columns;
        int rows = Terminal.Rows;
        if (cols <= 0 || rows <= 0) return;

        double cellPixelW = bbsRect.Width / cols;
        double cellPixelH = bbsRect.Height / rows;
        var cellRect = TapCoordinateMapper.CellsToPixels(slot.Row1, slot.Col1, slot.Col2 - slot.Col1 + 1,
            slot.Row2 - slot.Row1 + 1, cellPixelW, cellPixelH);

        double fitW = cellRect.Width, fitH = cellRect.Height;
        if (slot.PixelW > 0 && slot.PixelH > 0)
        {
            // Fit as if cells were 8x16 (how the board draws the still under it), then stretch with the
            // screen: on cells taller than 1:2 an aspect fit sat lower and left the art's edge rows showing.
            double picW = slot.PixelW * cellPixelW / 8.0, picH = slot.PixelH * cellPixelH / 16.0;
            double scale = Math.Min(cellRect.Width / picW, cellRect.Height / picH);
            fitW = picW * scale;
            fitH = picH * scale;
        }
        slot.Container.Width = fitW;
        slot.Container.Height = fitH;
        slot.Container.Margin = new Thickness(
            bbsRect.X + cellRect.Left + (cellRect.Width - fitW) / 2.0,
            bbsRect.Y + cellRect.Top + (cellRect.Height - fitH) / 2.0,
            0, 0);
    }

    private async void ShowLogo(string displayName)
    {
        var filePath = _viewModel.ResolveMediaFile(displayName);

        if (filePath == null)
        {
            for (int i = 0; i < 40; i++)
            {
                await Task.Delay(250);
                filePath = _viewModel.ResolveMediaFile(displayName);
                if (filePath != null) break;
            }
            if (filePath == null) return;
        }

        try
        {
            var bitmap = new Bitmap(filePath);
            LogoOverlay.Source = bitmap;

            if (!_tapPlusHidden)
                LogoContainer.IsVisible = true;
            _logoActive = true;
            UpdateLogoPosition();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"TerminalView: Error loading logo: {ex.Message}");
        }
    }

    private void HideLogo()
    {
        if (!_logoActive) return;
        LogoContainer.IsVisible = false;
        LogoOverlay.Source = null;
        _logoActive = false;
    }

    private void UpdateLogoPosition()
    {
        if (!_logoActive) return;

        var bbsRect = Terminal.GetBbsScreenRect();
        int cols = Terminal.Columns;
        int rows = Terminal.Rows;
        if (cols <= 0 || rows <= 0) return;

        double cellPixelW = bbsRect.Width / cols;
        double cellPixelH = bbsRect.Height / rows;
        var rect = TapCoordinateMapper.CellsToPixels(_logoRow, _logoCol, _logoCellW, _logoCellH, cellPixelW, cellPixelH);

        LogoContainer.Width = rect.Width;
        LogoContainer.Height = rect.Height;
        LogoContainer.Margin = new Thickness(bbsRect.X + rect.Left, bbsRect.Y + rect.Top, 0, 0);
    }

    private void UpdateImagePosition(int channel)
    {
        var ch = _imageChannels[channel];
        if (!ch.Active) return;

        var bbsRect = Terminal.GetBbsScreenRect();
        int cols = Terminal.Columns;
        int rows = Terminal.Rows;
        if (cols <= 0 || rows <= 0) return;

        double cellPixelW = bbsRect.Width / cols;
        double cellPixelH = bbsRect.Height / rows;
        var cellRect = TapCoordinateMapper.CellsToPixels(ch.Row, ch.Col, ch.CellW, ch.CellH, cellPixelW, cellPixelH);

        // Compute aspect-ratio-preserved fit
        double contentW = 0, contentH = 0;
        if (ch.ImageControl.Source is Bitmap bmp)
        {
            contentW = bmp.PixelSize.Width;
            contentH = bmp.PixelSize.Height;
        }

        if (contentW > 0 && contentH > 0)
        {
            double scaleW = cellRect.Width / contentW;
            double scaleH = cellRect.Height / contentH;
            double scale = Math.Min(scaleW, scaleH);
            double fitW = contentW * scale;
            double fitH = contentH * scale;
            double offsetX = (cellRect.Width - fitW) / 2.0;
            double offsetY = (cellRect.Height - fitH) / 2.0;

            ch.Container.Width = fitW;
            ch.Container.Height = fitH;
            ch.Container.Margin = new Thickness(
                bbsRect.X + cellRect.Left + offsetX,
                bbsRect.Y + cellRect.Top + offsetY,
                0, 0);
        }
        else
        {
            ch.Container.Width = cellRect.Width;
            ch.Container.Height = cellRect.Height;
            ch.Container.Margin = new Thickness(bbsRect.X + cellRect.Left, bbsRect.Y + cellRect.Top, 0, 0);
        }
    }

    private async void TryPlayVideo(string displayName)
    {
        var filePath = _viewModel.ResolveMediaFile(displayName);

        if (filePath == null)
        {
            for (int i = 0; i < 40; i++)
            {
                await Task.Delay(250);
                filePath = _viewModel.ResolveMediaFile(displayName);
                if (filePath != null) break;
            }
            if (filePath == null) return;
        }

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext is not ".mp4" and not ".m4v" and not ".webm") return;

        HideImage(0);
        HideVideo();

        try
        {
            // Default video position: centered 80% of terminal
            int cols = Terminal.Columns;
            int rows = Terminal.Rows;
            if (cols > 0 && rows > 0)
            {
                _videoCellW = cols * 0.8;
                _videoCellH = rows * 0.8;
                _videoCol = (cols - _videoCellW) / 2.0 + 1;
                _videoRow = (rows - _videoCellH) / 2.0 + 1;
            }

            _videoActive = true;
            UpdateVideoPosition();
            if (!_tapPlusHidden)
                VideoContainer.IsVisible = true;

            _libVLC ??= new LibVLC();
            var media = new Media(_libVLC, new Uri(filePath));
            _mediaPlayer = new LibVLCSharp.Shared.MediaPlayer(media);
            media.Dispose();
            _mediaPlayer.Volume = (int)(_viewModel.MediaVolume * 100);

            // Once playing, update position and stretch to fill (no black bars)
            _mediaPlayer.Playing += (s, e) =>
            {
                Dispatcher.UIThread.InvokeAsync(() => UpdateVideoPosition());
            };

            VideoOverlay.MediaPlayer = _mediaPlayer;
            _mediaPlayer.Play();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"TerminalView: Error playing video: {ex.Message}");
        }
    }

    private void HideVideo()
    {
        if (!_videoActive) return;
        try
        {
            VideoOverlay.MediaPlayer = null;
            _mediaPlayer?.Stop();
            _mediaPlayer?.Dispose();
            _mediaPlayer = null;
        }
        catch { /* ignore */ }
        VideoContainer.IsVisible = false;
        _videoActive = false;
    }

    private void ShowVisualizer(double row, double col, double width, double height, string style)
    {
        _vizRow = row;
        _vizCol = col;
        _vizCellW = width;
        _vizCellH = height;
        _vizStyle = style;
        _vizActive = true;

        var bbsRect = Terminal.GetBbsScreenRect();
        int cols = Terminal.Columns;
        int rows = Terminal.Rows;
        if (cols <= 0 || rows <= 0) return;

        double cellPixelW = bbsRect.Width / cols;
        double cellPixelH = bbsRect.Height / rows;
        var rect = TapCoordinateMapper.CellsToPixels(row, col, width, height, cellPixelW, cellPixelH);

        Visualizer.SetAnalyzer(_viewModel.AudioAnalyzer);
        if (!_tapPlusHidden)
            Visualizer.Show(bbsRect.X + rect.Left, bbsRect.Y + rect.Top, rect.Width, rect.Height, style);
    }

    private void HideVisualizer()
    {
        _vizActive = false;
        Visualizer.Hide();
    }

    private void RepositionOverlays()
    {
        UpdateBackgroundPosition();
        for (int i = 0; i < MaxImageChannels; i++)
            UpdateImagePosition(i);
        RepositionPics();
        UpdateLogoPosition();
        UpdateVideoPosition();
        if (_vizActive)
            ShowVisualizer(_vizRow, _vizCol, _vizCellW, _vizCellH, _vizStyle);
    }

    private void UpdateVideoPosition()
    {
        if (!_videoActive) return;

        var bbsRect = Terminal.GetBbsScreenRect();
        int cols = Terminal.Columns;
        int rows = Terminal.Rows;
        if (cols <= 0 || rows <= 0) return;

        double cellPixelW = bbsRect.Width / cols;
        double cellPixelH = bbsRect.Height / rows;
        var cellRect = TapCoordinateMapper.CellsToPixels(_videoRow, _videoCol, _videoCellW, _videoCellH, cellPixelW, cellPixelH);

        VideoContainer.Width = cellRect.Width;
        VideoContainer.Height = cellRect.Height;
        VideoContainer.Margin = new Thickness(bbsRect.X + cellRect.Left, bbsRect.Y + cellRect.Top, 0, 0);

        // Force VLC to stretch video to fill container (no black bars)
        if (_mediaPlayer != null)
        {
            int w = (int)cellRect.Width;
            int h = (int)cellRect.Height;
            if (w > 0 && h > 0)
                _mediaPlayer.AspectRatio = $"{w}:{h}";
        }
    }

    // ================================================================
    // Background Image Underlay
    // ================================================================

    private async void ShowBackground(string displayName)
    {
        var filePath = _viewModel.ResolveMediaFile(displayName);

        if (filePath == null)
        {
            for (int i = 0; i < 40; i++)
            {
                await Task.Delay(250);
                filePath = _viewModel.ResolveMediaFile(displayName);
                if (filePath != null) break;
            }
            if (filePath == null) return;
        }

        try
        {
            var bitmap = new Bitmap(filePath);
            BgOverlay.Source = bitmap;

            _bgActive = true;
            Terminal.SetBackgroundActive(true);
            if (!_tapPlusHidden)
                BgContainer.IsVisible = true;
            UpdateBackgroundPosition();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"TerminalView: Error loading background: {ex.Message}");
        }
    }

    private void HideBackground()
    {
        if (!_bgActive) return;
        BgContainer.IsVisible = false;
        BgOverlay.Source = null;
        BgContainer.Opacity = 1.0;
        _bgActive = false;
        Terminal.SetBackgroundActive(false);
    }

    private void UpdateBackgroundPosition()
    {
        if (!_bgActive) return;

        var bbsRect = Terminal.GetBbsScreenRect();
        int cols = Terminal.Columns;
        int rows = Terminal.Rows;
        if (cols <= 0 || rows <= 0) return;

        double cellPixelW = bbsRect.Width / cols;
        double cellPixelH = bbsRect.Height / rows;
        var rect = TapCoordinateMapper.CellsToPixels(_bgRow, _bgCol, _bgCellW, _bgCellH, cellPixelW, cellPixelH);

        BgContainer.Width = rect.Width;
        BgContainer.Height = rect.Height;
        BgContainer.Margin = new Thickness(bbsRect.X + rect.Left, bbsRect.Y + rect.Top, 0, 0);
    }

    // ================================================================
    // Tile Atlas System
    // ================================================================

    private async void LoadTileset(string displayName, int tileW, int tileH)
    {
        var filePath = _viewModel.ResolveMediaFile(displayName);

        if (filePath == null)
        {
            for (int i = 0; i < 40; i++)
            {
                await Task.Delay(250);
                filePath = _viewModel.ResolveMediaFile(displayName);
                if (filePath != null) break;
            }
            if (filePath == null) return;
        }

        try
        {
            var bitmap = new Bitmap(filePath);
            Terminal.SetTileAtlas(bitmap, tileW, tileH);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"TerminalView: Error loading tileset: {ex.Message}");
        }
    }

    /// <summary>
    /// Stops ALL TAP+ subsystems: images, video, visualizer, audio, tiles, background, logo.
    /// Called by the "stopall" TAP+ command and on disconnect.
    /// </summary>
    public void StopAllTapPlus()
    {
        for (int i = 0; i < MaxImageChannels; i++) HideImage(i);
        HideVideo();
        HideVisualizer();
        HideBackground();
        HideLogo();
        Terminal.ClearTiles();
        _viewModel.StopAllAudio();
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _statusTimer.Stop();
        // Unhook events to prevent leaks
        Terminal.InputGenerated -= OnTerminalInput;
        _viewModel.ScreenUpdatedEvent -= OnScreenUpdated;
        _viewModel.ConnectionManager.FontChanged -= OnFontChanged;
        _viewModel.TapPlusCommandReceived -= OnTapPlusCommand;
        Terminal.ScrollbackChanged -= OnScrollbackChanged;
        _viewModel.ConnectionManager.PicCommandReceived -= OnPicCommand;
        HideAllPics();
        HideBackground();
        for (int i = 0; i < MaxImageChannels; i++) HideImage(i);
        HideLogo();
        HideVideo();
        HideVisualizer();
        Terminal.ClearTiles();
        // Dispose LibVLC resources
        _mediaPlayer?.Dispose();
        _mediaPlayer = null;
        _libVLC?.Dispose();
        _libVLC = null;
    }
}
