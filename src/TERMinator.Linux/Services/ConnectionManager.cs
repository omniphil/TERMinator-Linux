using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using TERMinator.Linux.Models;

namespace TERMinator.Linux.Services;

/// <summary>
/// Manages the connection lifecycle and polling loop.
/// Port of the Kotlin ConnectionManager - uses Task.Run polling loop.
/// </summary>
public sealed class ConnectionManager : INotifyPropertyChanged, IDisposable
{
    private CancellationTokenSource? _pollCts;
    private bool _initialized;
    private bool _disposed;
    private volatile bool _transferInProgress;

    private static readonly string _logPath = Path.Combine(
        AppSettings.GetAppDataDir(), "connect_debug.log");

    internal static void Log(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
        try { File.AppendAllText(_logPath, line + "\n"); }
        catch { }
        Console.Error.WriteLine(line);
    }

    // Polling intervals
    private const int ActivePollMs = 16;   // ~60fps when data flowing
    private const int IdlePollMs = 50;     // 20fps when idle

    // Initial connection timeout: disconnect if no data arrives within the user's Connection Timeout (read in PollLoop)
    private bool _hasReceivedData;

    // Screen buffer (reused to avoid allocation)
    private int[] _screenBuffer = new int[80 * 25];
    // Per-cell 24-bit fg/bg, filled alongside _screenBuffer by the true-colour fetch.
    private int[] _screenFg = new int[80 * 25];
    private int[] _screenBg = new int[80 * 25];
    private int[] _palette = new int[16];

    // Font bitmap cached before poll loop starts (avoids deadlock)
    private byte[]? _cachedFontBitmap;
    private int _lastFontId = -1;

    // OSC 800 parsing state (for C#-side audio command extraction)
    private readonly byte[] _logBuffer = new byte[8192];
    private readonly byte[] _traceBuffer = new byte[72 * 1024];   // a binary frame: header line + up to 64 KB
    private readonly byte[] _picBuffer = new byte[2048];          // a Show with its resolved cache path
    private bool _picMissing;                                     // a .so built before pic_bridge.c
    private readonly byte[] _uiBuffer = new byte[16384];          // a Rainwire:UI message (at most ~8 KB) and its path
    private bool _uiMissing;                                      // a .so built before ui_bridge.c (1.1.6)
    private readonly StringBuilder _oscAccum = new();
    private bool _inOsc800;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? ScreenUpdated;
    public event Action<int>? ZmodemDetected; // -100 = download, -101 = upload
    public event Action? BellReceived;
    public event Action<string>? AudioCommandReceived;
    /// <summary>A TRACE command from the board (text after "TERMinator:TRACE;"), raised on the poll thread.</summary>
    public event Action<string>? TraceCommandReceived;

    /// <summary>A TRACE binary frame (header line, '\n', raw payload), in order with the text commands.</summary>
    public event Action<byte[]>? TraceBinaryReceived;

    /// <summary>A TERMinator:Pic command from native/apc/pic_bridge.c ("Show;id=..;rect=..;path=..", "Hide;id=..",
    /// "HideAll", including the HideAll a full screen clear queues), in stream order. Poll thread.</summary>
    public event Action<string>? PicCommandReceived;

    /// <summary>A Rainwire:UI message from native/apc/ui_bridge.c ("Verb[;b64=..][;path=..]": a board's menu for
    /// Controls/RainwireMenu to draw, 1.1.6), in stream order. Poll thread.</summary>
    public event Action<string>? UiMessageReceived;

    /// <summary>OSC 0/2 from the board: its window title ("" = put the normal title back). Poll thread. (12.62)</summary>
    public event Action<string>? BoardTitleChanged;
    /// <summary>OSC 52 from the board: text for the clipboard (at most 64 KB; reads are never answered). Poll thread.</summary>
    public event Action<string>? BoardClipboardWrite;
    /// <summary>OSC 777 notify from the board: (title, body), either may be empty, not both. Poll thread. (12.72)</summary>
    public event Action<string, string>? BoardNotification;

    /// <summary>Settings' Board Notifications as it was when this call connected, and notify-send there (12.72): off,
    /// the board was not told notify=1 and its notifications are drained and dropped.</summary>
    public bool BoardNotificationsOn { get; private set; }
    /// <summary>The board asked for a linked window (12.73, Rainwire:Link;Open): kind, token, title and the optional
    /// hints. Raised on the poll thread; MainWindow decides whether to open it.</summary>
    public event Action<OscExtras.LinkRequest>? LinkRequested;
    /// <summary>The board asked for the open linked window of this kind to come to the front (Rainwire:Link;Raise).</summary>
    public event Action<string>? LinkRaise;
    /// <summary>mw=1 went in this call's Caps answer (Options > Let Boards Open Extra Windows when it connected).</summary>
    public bool LinkedWindowsOn { get; private set; }
    public event Action? Disconnected;
    public event Action<byte[]>? FontChanged;

    private ConnectionState _state = ConnectionState.Disconnected;
    public ConnectionState State
    {
        get => _state;
        private set { _state = value; OnPropertyChanged(); }
    }

    private string _statusText = "Disconnected";
    public string StatusText
    {
        get => _statusText;
        private set { _statusText = value; OnPropertyChanged(); }
    }

    public bool IsInitialized => _initialized;
    public int ScreenWidth => _initialized ? NativeBridge.syncterm_get_screen_width() : 80;
    public int ScreenHeight => _initialized ? NativeBridge.syncterm_get_screen_height() : 25;
    public int[] ScreenBuffer => _screenBuffer;
    public int[] ScreenFg => _screenFg;
    public int[] ScreenBg => _screenBg;
    public int[] Palette => _palette;
    public byte[]? CachedFontBitmap => _cachedFontBitmap;

    // Cursor properties
    public int CursorX => NativeBridge.syncterm_get_cursor_x();
    public int CursorY => NativeBridge.syncterm_get_cursor_y();
    public bool CursorVisible => NativeBridge.syncterm_is_cursor_visible() != 0;
    public int CursorType
    {
        get
        {
            try { return NativeBridge.syncterm_get_cursor_type(); }
            catch (EntryPointNotFoundException) { return 2; }
        }
    }
    public int CursorStart
    {
        get
        {
            try { return NativeBridge.syncterm_get_cursor_start(); }
            catch (EntryPointNotFoundException) { return 14; }
        }
    }
    public int CursorEnd
    {
        get
        {
            try { return NativeBridge.syncterm_get_cursor_end(); }
            catch (EntryPointNotFoundException) { return 15; }
        }
    }
    public int CursorBlinks
    {
        get
        {
            try { return NativeBridge.syncterm_get_cursor_blinks(); }
            catch (EntryPointNotFoundException) { return 1; }
        }
    }
    /// <summary>
    /// Cursor foreground color as resolved 0x00RRGGBB (matching color_value(ciolib_fg) in bitmap_con.c).
    /// </summary>
    public int CiolibFg
    {
        get
        {
            try { return NativeBridge.syncterm_get_ciolib_fg(); }
            catch (EntryPointNotFoundException) { return 0xAAAAAA; /* light gray */ }
        }
    }

    public bool Initialize()
    {
        if (_initialized) return true;

        // Set files directory to app data dir
        var appData = AppSettings.GetAppDataDir();
        NativeBridge.syncterm_set_files_dir(appData);

        if (NativeBridge.syncterm_init() != 1)
            return false;

        // Fix: native code defaults to 80x24, force 80x25
        NativeBridge.syncterm_set_terminal_size(80, 25);

        // Initialize transfer system
        NativeBridge.syncterm_transfer_init();

        _initialized = true;
        return true;
    }

    /// <summary>
    /// Tracks whether a native syncterm_connect() call is currently blocked
    /// on a background thread. Used to guard against overlapping connect calls
    /// and to force-kill the process on exit when native code is hung.
    /// </summary>
    public volatile bool NativeConnectInProgress;

    /// <summary>
    /// Set to true to abort the current Connect() after syncterm_connect() returns.
    /// Prevents Connect() from setting up Connected state / polling after the
    /// UI has already moved on (timeout or cancel).
    /// </summary>
    public volatile bool ConnectAborted;

    public bool Connect(SavedConnection connection, string? password = null)
    {
        Log($"Connect: starting to {connection.Host}:{connection.Port}, _initialized={_initialized}, State={State}");

        if (!_initialized || State == ConnectionState.Connected)
        {
            Log($"Connect: early return, initialized={_initialized} state={State}");
            return false;
        }

        ConnectAborted = false;
        State = ConnectionState.Connecting;
        StatusText = $"Connecting to {connection.Host}:{connection.Port}...";

        // Clear stale screen buffer from previous session
        Array.Clear(_screenBuffer);
        Array.Clear(_screenFg);
        Array.Clear(_screenBg);
        Array.Clear(_palette);

        // Always hide native SyncTERM status line (we render our own)
        NativeBridge.syncterm_set_hide_status_line(1);
        NativeBridge.syncterm_set_screen_mode(connection.ScreenMode);
        if (IsCommodoreMode(connection.ScreenMode))
        {
            // Commodore modes use the machine's own character ROM and ALWAYS start in the
            // lower/text set, which is where a real C64 terminal program sits.
            //
            // There is deliberately no user-facing upper/lower choice. A PETSCII board drives the
            // character set itself with 0x0E and 0x8E — for a typical board that is lower case for
            // everything except the odd module — so exposing it as a setting is redundant at best
            // and fights the board at worst. The phonebook font is ignored in these modes.
            NativeBridge.syncterm_set_font(CommodoreFontFor(connection.ScreenMode)!);
        }
        else if (!string.IsNullOrEmpty(connection.FontName))
        {
            // Map display names to native conio_fontdata names
            var nativeFontName = connection.FontName switch
            {
                "Topaz 1200 Plus" => "Topaz Plus (Amiga)",
                "Topaz Plus" => "Topaz Plus (Amiga)",
                "P0T NOoDLE" => "P0T NOoDLE (Amiga)",
                "mO'sOul" => "mO'sOul (Amiga)",
                "MicroKnight Plus" => "MicroKnight Plus (Amiga)",
                "MicroKnight" => "MicroKnight (Amiga)",
                "Topaz" => "Topaz (Amiga)",
                _ => connection.FontName
            };
            NativeBridge.syncterm_set_font(nativeFontName);
        }

        // Set terminal dimensions based on screen mode
        // (syncterm_set_screen_mode only sets bbs_config, doesn't update g_term_width/height)
        var (cols, rows) = GetScreenModeDimensions(connection.ScreenMode);
        NativeBridge.syncterm_set_terminal_size(cols, rows);

        // The BBS's own status line, per board. The native default has always been to hide it;
        // this is the first thing that ever calls the setter, so it must be set EVERY connect —
        // the native flag is global and would otherwise carry over from the previous session.
        NativeBridge.syncterm_set_hide_status_line(connection.ShowStatusBar ? 0 : 1);

        // SyncTERM APC: the board's cache directory (C;S stores, audio loads, JXL frames), per board like
        // SyncTERM's own, and the soundtrack setting for its audio
        NativeBridge.syncterm_set_apc_cache_dir(GetApcCacheDir(connection));
        App.AudioService.PrepareSyncTermAudio();

        // SSH with "Save credentials" off logs in blank: no password, no prompts, the board's own login screen.
        // The native flag is global, so it is set EVERY connect.
        string? username = connection.Username;
        bool blankLogin = connection.UsesBlankSshLogin;
        if (blankLogin)
            password = null;
        try
        {
            NativeBridge.syncterm_set_ssh_blank_login(blankLogin ? 1 : 0);
        }
        catch (EntryPointNotFoundException)
        {
            // An older .so (it still offers the client key): the neutral name at least keeps the entry's username
            // out of it
            if (blankLogin)
                username = "bbs";
        }

        // The app's own keys in the Rainwire:Caps answer (12.72): version, UI language, time zone, and notify=1 only
        // with Settings' Board Notifications on (off by default) and notify-send there to show them
        // (Services/BoardNotifier.cs). Global native state, so set EVERY connect.
        // 12.73: mw=1 with Options > Let Boards Open Extra Windows on, and a linked window's own connection (Rainwire's
        // MRC window) adds the board's token, link=..., which is what lets it in
        BoardNotificationsOn = App.SettingsService.Settings.BoardNotifications && BoardNotifier.Available;
        LinkedWindowsOn = App.SettingsService.Settings.LinkedWindows || connection.Linked;
        OscExtras.SetInfo(OscExtras.AppInfo(notify: BoardNotificationsOn, multiWindow: LinkedWindowsOn,
                                            linkToken: connection.Linked ? connection.LinkToken : null));

        Log($"Connect: calling syncterm_connect({connection.Host}, {connection.Port}, proto={connection.GetNativeProtocol()})...");
        NativeConnectInProgress = true;
        int result;
        try
        {
            result = NativeBridge.syncterm_connect(
                connection.Host,
                connection.Port,
                connection.GetNativeProtocol(),
                username,
                password);
        }
        finally
        {
            NativeConnectInProgress = false;
        }
        Log($"Connect: syncterm_connect returned {result}, ConnectAborted={ConnectAborted}");

        // If the connection was aborted (timeout/cancel) while we were blocked
        // in syncterm_connect(), clean up and bail out.
        if (ConnectAborted)
        {
            Log($"Connect: aborted path, result={result}, doing full cleanup");
            CleanupNativeState();
            State = ConnectionState.Disconnected;
            StatusText = "Connection aborted";
            Log("Connect: abort cleanup done");
            return false;
        }

        if (result != 1)
        {
            Log("Connect: failed normally");
            State = ConnectionState.Disconnected;
            StatusText = "Connection failed";
            return false;
        }

        State = ConnectionState.Connected;
        StatusText = $"Connected to {connection.Host}:{connection.Port}";
        LastSessionBytes = (0, 0);
        Log("Connect: connection established, finalizing setup...");

        // Ensure screen buffer is sized correctly
        ResizeScreenBuffer();

        // Refresh palette
        NativeBridge.syncterm_get_palette(_palette);

        // Enable raw data capture for session logging
        // (OSC 800 TAP+ commands are handled by the native queue via cterm.c)
        NativeBridge.syncterm_set_logging_enabled(1);

        // Get font bitmap BEFORE starting the poll loop to avoid deadlock.
        // The poll loop acquires g_lock for process_data and then event handlers
        // may need g_lock (e.g. for GetFontBitmap), causing deadlock.
        _cachedFontBitmap = GetFontBitmap();
        _lastFontId = NativeBridge.syncterm_get_current_font_id();

        _hasReceivedData = false;

        Log("Connect: starting poll loop");
        // Start polling loop
        StartPolling();
        return true;
    }

    public void Disconnect()
    {
        StopPolling();
        SnapshotSessionBytes();
        CleanupNativeState();
        State = ConnectionState.Disconnected;
        StatusText = "Disconnected";
    }

    /// <summary>
    /// Force-reset the native layer after a timed-out or cancelled connection.
    /// This tears down and reinitializes the native library so subsequent
    /// connections start from a clean slate.
    /// </summary>
    public void ForceReset()
    {
        StopPolling();
        State = ConnectionState.Disconnected;
        StatusText = "Disconnected";

        if (_initialized)
        {
            try
            {
                NativeBridge.syncterm_set_logging_enabled(0);
                NativeBridge.syncterm_disconnect();
                NativeBridge.syncterm_transfer_cleanup();
                NativeBridge.syncterm_destroy();
            }
            catch { /* native state may be corrupt — best effort */ }
            _initialized = false;
        }

        _inOsc800 = false;
        _oscAccum.Clear();

        // Reinitialize so the next Connect() has a fresh native context
        Initialize();
    }

    /// <summary>
    /// Reset all native state so the next connection starts clean.
    /// Safe to call multiple times (guarded by _initialized).
    /// </summary>
    private void CleanupNativeState()
    {
        try { NativeBridge.syncterm_set_logging_enabled(0); }
        catch (DllNotFoundException) { }
        _inOsc800 = false;
        _oscAccum.Clear();

        if (!_initialized) return;

        NativeBridge.syncterm_disconnect();
        NativeBridge.syncterm_transfer_cleanup();
        NativeBridge.syncterm_destroy();
        _initialized = false;
        Initialize();

        // Clear the native screen so no stale content remains
        NativeBridge.syncterm_clear_screen();
    }

    public void SendData(byte[] data) =>
        NativeBridge.syncterm_send_data(data, data.Length);

    public void SendKey(int keyCode) =>
        NativeBridge.syncterm_send_key(keyCode);

    public void SendString(string str) =>
        NativeBridge.syncterm_send_string(str);

    public void PushInput(byte[] data) =>
        NativeBridge.syncterm_push_input(data, data.Length);

    /// <summary>
    /// Sends a login sequence (username + password) character-by-character,
    /// mimicking human typing speed. Matches Android TerminalActivity.sendLoginSequence().
    /// </summary>
    public async Task SendLoginSequenceAsync(string username, string password)
    {
        if (State != ConnectionState.Connected) return;

        // Send username character by character
        foreach (char ch in username)
        {
            SendData([(byte)(ch & 0x7F)]);
            await Task.Delay(50);
        }
        // Send Enter (CR)
        SendData([13]);

        // Wait for BBS to show password prompt
        await Task.Delay(500);

        // Send password character by character
        foreach (char ch in password)
        {
            SendData([(byte)(ch & 0x7F)]);
            await Task.Delay(50);
        }
        // Send Enter (CR)
        SendData([13]);
    }

    public void SetTerminalSize(int width, int height)
    {
        NativeBridge.syncterm_set_terminal_size(width, height);
        ResizeScreenBuffer();
    }

    public void ClearScreen() =>
        NativeBridge.syncterm_clear_screen();

    /// <summary>
    /// Prevents the poll loop from processing connection data while a
    /// native ZMODEM transfer thread is active. Must be cleared in a
    /// finally block when the native call returns.
    /// </summary>
    public void SetTransferInProgress(bool active) => _transferInProgress = active;

    public (long bytesSent, long bytesReceived, long connectTimeMs) GetConnectionStats()
    {
        var stats = new long[4];
        NativeBridge.syncterm_get_connection_stats(stats);
        return (stats[0], stats[1], stats[2]);
    }

    /// <summary>
    /// Bytes sent and received in the session that just ended. Read before the native state is torn down, because
    /// destroying it zeroes the counters before the Disconnected event reaches anyone.
    /// </summary>
    public (long sent, long received) LastSessionBytes { get; private set; }

    private void SnapshotSessionBytes()
    {
        try
        {
            var stats = new long[4];
            NativeBridge.syncterm_get_connection_stats(stats);
            if (stats[0] > 0 || stats[1] > 0)
                LastSessionBytes = (stats[0], stats[1]);
        }
        catch
        {
            // Native state already gone; keep whatever was captured
        }
    }

    /// <summary>The speed incoming data is metered at in bps (the user's setting, else the board's); 0 = full speed.</summary>
    public int EffectiveBaud => NativeBridge.syncterm_get_effective_bps();

    public byte[]? GetFontBitmap()
    {
        var buffer = new byte[256 * 32 + 2]; // max: 256 chars * up to 32 rows + 2 byte header
        int len = NativeBridge.syncterm_get_font_bitmap(buffer, buffer.Length);
        if (len <= 0) return null;
        var result = new byte[len];
        Array.Copy(buffer, result, len);
        return result;
    }

    public static (int cols, int rows) GetScreenModeDimensions(int screenMode) => screenMode switch
    {
        NativeBridge.SCREEN_MODE_80X25 => (80, 25),
        NativeBridge.SCREEN_MODE_80X30 => (80, 30),
        NativeBridge.SCREEN_MODE_80X40 => (80, 40),
        NativeBridge.SCREEN_MODE_80X50 => (80, 50),
        NativeBridge.SCREEN_MODE_132X25 => (132, 25),
        NativeBridge.SCREEN_MODE_132X30 => (132, 30),
        NativeBridge.SCREEN_MODE_132X40 => (132, 40),
        NativeBridge.SCREEN_MODE_132X50 => (132, 50),
        NativeBridge.SCREEN_MODE_132X60 => (132, 60),
        NativeBridge.SCREEN_MODE_C64_40X25 => (40, 25),
        NativeBridge.SCREEN_MODE_C128_40X25 => (40, 25),
        NativeBridge.SCREEN_MODE_C128_80X25 => (80, 25),
        _ => (80, 25)
    };

    /// <summary>True for the Commodore (PETSCII) screen modes.</summary>
    public static bool IsCommodoreMode(int screenMode) => screenMode is
        NativeBridge.SCREEN_MODE_C64_40X25 or
        NativeBridge.SCREEN_MODE_C128_40X25 or
        NativeBridge.SCREEN_MODE_C128_80X25;

    /// <summary>
    /// Display aspect (width/height) to draw the mode at, never null for anything the app offers.
    ///
    /// Every mode keeps the CHARACTER CELL shape of an 80x25 screen on a 4:3 monitor (no text mode
    /// had square pixels: 80x25 VGA is a 720x400 grid that filled a 4:3 monitor), and the picture
    /// grows with the grid: 80x25 is 4:3, 80x50 is twice as tall (2:3), 132x25 is 132/80 as wide,
    /// 132x50 is about 1.1:1. The 40-column Commodore modes keep their own 6:5 (320x200).
    /// User's call 2026-10-01 (replaces the 2026-09-12 rows-only scaling, which squeezed 132-column
    /// modes into an 80-column width): "open at their proper aspect ratio based on their pixel sizes".
    /// </summary>
    public static double? GetDisplayAspect(int screenMode)
    {
        if (screenMode is NativeBridge.SCREEN_MODE_C64_40X25 or NativeBridge.SCREEN_MODE_C128_40X25)
            return 6.0 / 5.0;
        var (cols, rows) = GetScreenModeDimensions(screenMode);
        return cols > 0 && rows > 0 ? 4.0 / 3.0 * (cols / 80.0) * (25.0 / rows) : 4.0 / 3.0;
    }

    /// <summary>Native conio font a Commodore mode starts in by default (the lowercase text set).</summary>
    public static string? CommodoreFontFor(int screenMode) => screenMode switch
    {
        NativeBridge.SCREEN_MODE_C64_40X25 => "Commodore 64 (Lower)",
        NativeBridge.SCREEN_MODE_C128_40X25 => "Commodore 128 (Lower)",
        NativeBridge.SCREEN_MODE_C128_80X25 => "Commodore 128 (Lower)",
        _ => null
    };

    /// <summary>The four Commodore character-ROM fonts, by their native conio names.</summary>
    public static readonly string[] CommodoreFonts =
    [
        "Commodore 64 (Lower)",
        "Commodore 64 (UPPER)",
        "Commodore 128 (Lower)",
        "Commodore 128 (UPPER)"
    ];

    public static bool IsCommodoreFont(string? fontName) =>
        fontName != null && Array.IndexOf(CommodoreFonts, fontName) >= 0;

    /// <summary>
    /// Pulls the text screen and palette from native when they changed. Called on the UI thread just before
    /// a redraw, so the buffers are only written by the thread that reads them.
    /// </summary>
    public void FetchScreen()
    {
        if (State != ConnectionState.Connected || NativeBridge.syncterm_is_screen_dirty() == 0)
            return;
        ResizeScreenBuffer();
        NativeBridge.syncterm_get_screen_buffer_rgb(_screenBuffer, _screenFg, _screenBg, _screenBuffer.Length);
        NativeBridge.syncterm_get_palette(_palette);
    }

    /// <summary>
    /// Lets a redraw this loop already queued run before native state is torn down, so the board's last
    /// screen (a goodbye, NO CARRIER) still shows. Same priority as the redraw, so it runs after it.
    /// </summary>
    private static void FlushQueuedRedraw()
    {
        try
        {
            // Bounded: a dispatcher that has stopped (the app closing) must not park the poll thread forever
            Avalonia.Threading.Dispatcher.UIThread.Invoke(() => { }, Avalonia.Threading.DispatcherPriority.Input,
                CancellationToken.None, TimeSpan.FromSeconds(1));
        }
        catch
        {
            // Shutting down
        }
    }

    /// <summary>
    /// <c>~/.config/TERMinator/cache/&lt;board&gt;/</c>, named as Windows' <c>GetApcCacheDir</c>: the board's name (host_port
    /// without one), the characters Windows can't put in a file name replaced with <c>_</c>, so a cache folder means the
    /// same thing on every client (.NET on Linux only rejects '/' and NUL, so Windows' set is spelled out).
    /// </summary>
    private static string GetApcCacheDir(SavedConnection connection)
    {
        string name = string.IsNullOrWhiteSpace(connection.Name) ? $"{connection.Host}_{connection.Port}" : connection.Name;
        const string invalid = "\"<>|:*?\\/";
        string safe = new string(name.Trim().Select(c => c < 32 || invalid.IndexOf(c) >= 0 ? '_' : c).ToArray()).TrimEnd('.', ' ');
        return Path.Combine(AppSettings.GetAppDataDir(), "cache", safe.Length > 0 ? safe : "default");
    }

    private void ResizeScreenBuffer()
    {
        int w = NativeBridge.syncterm_get_screen_width();
        int h = NativeBridge.syncterm_get_screen_height();
        int needed = w * h;
        if (_screenBuffer.Length < needed)
            _screenBuffer = new int[needed];
        // The colour arrays are indexed in lockstep with _screenBuffer, so they must be at
        // least as long -- native writes all three using the same cell count.
        if (_screenFg.Length < _screenBuffer.Length)
            _screenFg = new int[_screenBuffer.Length];
        if (_screenBg.Length < _screenBuffer.Length)
            _screenBg = new int[_screenBuffer.Length];
    }

    private void StartPolling()
    {
        StopPolling();
        _pollCts = new CancellationTokenSource();
        var token = _pollCts.Token;
        Task.Run(() => PollLoop(token), token);
    }

    private void StopPolling()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = null;
    }

    private async Task PollLoop(CancellationToken ct)
    {
        var connectionTimer = Stopwatch.StartNew();
        // Give up if the board sends nothing within the user's connection timeout
        // (Preferences > TERMINAL > Connection Timeout, default 10s), read at connect time.
        long connectionTimeoutMs = AppSettings.ValidConnectionTimeout(App.SettingsService.Settings.ConnectionTimeoutSeconds) * 1000L;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (NativeBridge.syncterm_is_connected() != 1)
                {
                    FlushQueuedRedraw();
                    if (ct.IsCancellationRequested)
                        break; // the user's Disconnect ran meanwhile and has already cleaned up
                    SnapshotSessionBytes();
                    CleanupNativeState();
                    State = ConnectionState.Disconnected;
                    StatusText = "Connection lost";
                    Disconnected?.Invoke();
                    break;
                }

                // While a native ZMODEM transfer thread is active, skip all
                // data processing -- the transfer owns the connection I/O.
                if (_transferInProgress)
                {
                    await Task.Delay(IdlePollMs, ct);
                    continue;
                }

                // Process all available data in a tight loop before rendering.
                // This prevents the render pipeline from starving data processing
                // (e.g. a countdown timer skipping numbers).
                int lastResult = 0;
                bool metered = false;
                bool held = false; // data is waiting that process_data isn't taking yet (modem meter, ZMODEM)
                long redrawAt = Environment.TickCount64 + ActivePollMs;
                for (int batch = 0; batch < 64; batch++)
                {
                    int result = NativeBridge.syncterm_process_data();

                    // A long batch (a door streaming full-screen frames never lets the socket run dry) still gets
                    // the screen drawn every ActivePollMs, not only once the whole batch is through
                    if (Environment.TickCount64 >= redrawAt)
                    {
                        redrawAt = Environment.TickCount64 + ActivePollMs;
                        if (NativeBridge.syncterm_is_screen_dirty() != 0 || NativeBridge.syncterm_is_sixel_dirty() != 0)
                            ScreenUpdated?.Invoke();
                    }

                    if (result == -100)
                        ZmodemDetected?.Invoke(-100);
                    else if (result == -101)
                        ZmodemDetected?.Invoke(-101);

                    // Check for bell each iteration
                    if (NativeBridge.syncterm_check_bell() != 0)
                        BellReceived?.Invoke();

                    // Drain all pending audio commands from the native queue
                    while (NativeBridge.syncterm_check_audio_command() != 0)
                    {
                        var buf = new byte[4096];
                        int len = NativeBridge.syncterm_get_audio_command(buf, buf.Length);
                        if (len > 0)
                        {
                            string cmd = NativeBridge.GetNativeString(buf, len);
                            Debug.WriteLine($"ConnectionManager: NATIVE audio cmd received: {cmd}");
                            AudioCommandReceived?.Invoke(cmd);
                        }
                        else break;
                    }

                    // TRACE commands, in the order the board sent them (trace_bridge.c caps each below 8192 bytes)
                    int traceLen;
                    while ((traceLen = NativeBridge.syncterm_trace_poll(_traceBuffer, _traceBuffer.Length)) > 0)
                    {
                        if (_traceBuffer[0] == 0x01)   // a binary frame (trace_bridge.c BIN_MARK)
                            TraceBinaryReceived?.Invoke(_traceBuffer.AsSpan(1, traceLen - 1).ToArray());
                        else
                            TraceCommandReceived?.Invoke(Encoding.UTF8.GetString(_traceBuffer, 0, traceLen));
                    }

                    // Rainwire's pictures (native/apc/pic_bridge.c), in the order the board sent them
                    if (!_picMissing)
                    {
                        try
                        {
                            int picLen;
                            while ((picLen = NativeBridge.syncterm_pic_poll(_picBuffer, _picBuffer.Length)) > 0)
                                PicCommandReceived?.Invoke(Encoding.UTF8.GetString(_picBuffer, 0, picLen));
                        }
                        catch (EntryPointNotFoundException)
                        {
                            _picMissing = true;
                        }
                    }

                    // Rainwire's menus drawn here (native/apc/ui_bridge.c, 1.1.6), in the order the board sent them
                    if (!_uiMissing)
                    {
                        try
                        {
                            int uiLen;
                            while ((uiLen = NativeBridge.syncterm_ui_poll(_uiBuffer, _uiBuffer.Length)) > 0)
                                UiMessageReceived?.Invoke(Encoding.UTF8.GetString(_uiBuffer, 0, uiLen));
                        }
                        catch (EntryPointNotFoundException)
                        {
                            _uiMissing = true;
                        }
                    }

                    // OSC 0/2 window title and OSC 52 clipboard writes (native/apc/osc_ext.c, 12.62)
                    if (OscExtras.PollTitle() is { } boardTitle)
                        BoardTitleChanged?.Invoke(boardTitle);
                    if (OscExtras.PollClipboard() is { } clipText)
                        BoardClipboardWrite?.Invoke(clipText);
                    // OSC 777 notifications (12.72): native keeps the last 4, in order
                    while (OscExtras.PollNotify() is { } note)
                        BoardNotification?.Invoke(note.Title, note.Body);
                    // Rainwire:Link requests (12.73): open or raise a linked window
                    while (OscExtras.PollLink() is { } link)
                    {
                        if (link.Open)
                            LinkRequested?.Invoke(link);
                        else
                            LinkRaise?.Invoke(link.Kind);
                    }

                    // Consume logged data to prevent buffer overflow.
                    // OSC 800 parsing no longer needed here -- native queue handles all TAP+ commands.
                    NativeBridge.syncterm_get_logged_data(_logBuffer, _logBuffer.Length);

                    if (result > 0)
                    {
                        lastResult = result;
                        _hasReceivedData = true;
                    }
                    else
                    {
                        // -102: data is waiting on the modem speed meter, so keep polling at the active rate
                        metered = result == -102;
                        held = result < 0;
                        break; // No more data available (yet)
                    }
                }

                // Check for initial connection timeout (no data received within the window)
                if (!_hasReceivedData && connectionTimer.ElapsedMilliseconds > connectionTimeoutMs)
                {
                    Log("PollLoop: connection timed out — no data received within the timeout window");
                    FlushQueuedRedraw();
                    if (ct.IsCancellationRequested)
                        break; // the user's Disconnect ran meanwhile and has already cleaned up
                    SnapshotSessionBytes();
                    CleanupNativeState();
                    State = ConnectionState.Disconnected;
                    StatusText = "Connection timed out";
                    Disconnected?.Invoke();
                    break;
                }

                // After processing all available data, render once.
                // Fire on either text-screen or sixel-pixel dirty so pure-sixel
                // updates (e.g. AI-WX weather) aren't dropped when the text grid is unchanged.
                // The UI thread pulls the screen itself (FetchScreen) when it gets to the redraw, so this loop
                // never waits on rendering.
                if (NativeBridge.syncterm_is_screen_dirty() != 0 || NativeBridge.syncterm_is_sixel_dirty() != 0)
                    ScreenUpdated?.Invoke();

                // Check for BBS-driven font changes
                int currentFontId = NativeBridge.syncterm_get_current_font_id();
                if (currentFontId != _lastFontId)
                {
                    _lastFontId = currentFontId;
                    Debug.WriteLine($"ConnectionManager: font changed to ID {currentFontId}");
                    var newFont = GetFontBitmap();
                    if (newFont != null)
                    {
                        _cachedFontBitmap = newFont;
                        FontChanged?.Invoke(newFont);
                    }
                }

                int delay = lastResult > 0 || metered ? ActivePollMs : IdlePollMs;
                if (held)
                {
                    // Those bytes aren't ours to take yet, so a data wait would return at once and spin
                    await Task.Delay(delay, ct);
                }
                else
                {
                    // Returns the moment data arrives: a door pacing frames on our cursor-position replies
                    // (SyncDoom) gets each reply as soon as its frame lands, not after a fixed sleep
                    NativeBridge.syncterm_wait_for_data(delay);
                    ct.ThrowIfCancellationRequested();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // Don't crash the poll loop on transient errors
            }
        }
    }

    /// <summary>
    /// Scan raw terminal data for OSC 800 audio command sequences.
    /// Format: ESC ] 800 ; command ; params ... ST
    /// Where ST = ESC \ (0x1B 0x5C) or BEL (0x07)
    /// </summary>
    private void ScanForOsc800(byte[] data, int length)
    {
        for (int i = 0; i < length; i++)
        {
            byte b = data[i];

            if (_inOsc800)
            {
                // Check for String Terminator: BEL (0x07)
                if (b == 0x07)
                {
                    DispatchOsc800(_oscAccum.ToString());
                    _inOsc800 = false;
                    _oscAccum.Clear();
                }
                // Check for String Terminator: ESC \ (0x1B 0x5C)
                else if (b == 0x1B)
                {
                    // Peek ahead for backslash
                    if (i + 1 < length && data[i + 1] == 0x5C)
                    {
                        DispatchOsc800(_oscAccum.ToString());
                        _inOsc800 = false;
                        _oscAccum.Clear();
                        i++; // Skip the backslash
                    }
                    else
                    {
                        // ESC without \: abort this sequence, could be new escape
                        _inOsc800 = false;
                        _oscAccum.Clear();
                    }
                }
                else if (_oscAccum.Length < 4096)
                {
                    _oscAccum.Append((char)b);
                }
                else
                {
                    // Sequence too long, abort
                    _inOsc800 = false;
                    _oscAccum.Clear();
                }
            }
            else
            {
                // Look for ESC ] 8 0 0 ; sequence start
                if (b == 0x1B && i + 5 < length &&
                    data[i + 1] == ']' &&
                    data[i + 2] == '8' &&
                    data[i + 3] == '0' &&
                    data[i + 4] == '0' &&
                    data[i + 5] == ';')
                {
                    Debug.WriteLine($"ConnectionManager: ScanForOsc800 FOUND ESC]800; at offset {i} in {length}-byte chunk");
                    _inOsc800 = true;
                    _oscAccum.Clear();
                    i += 5; // Skip past "ESC]800;" -- loop will advance past ';'
                }
            }
        }
    }

    private void DispatchOsc800(string command)
    {
        if (string.IsNullOrEmpty(command)) return;
        Debug.WriteLine($"ConnectionManager: OSC 800 parsed: {command}");
        AudioCommandReceived?.Invoke(command);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public static string GetSshDebugLog()
    {
        try
        {
            var buffer = new byte[16384];
            int len = NativeBridge.syncterm_get_ssh_debug_log(buffer, buffer.Length);
            if (len <= 0) return string.Empty;
            return Encoding.UTF8.GetString(buffer, 0, len);
        }
        catch (EntryPointNotFoundException)
        {
            return "(SSH debug log not available — native library needs to be rebuilt)";
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopPolling();
        if (_initialized)
        {
            NativeBridge.syncterm_transfer_cleanup();
            NativeBridge.syncterm_destroy();
        }
    }
}

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected
}
