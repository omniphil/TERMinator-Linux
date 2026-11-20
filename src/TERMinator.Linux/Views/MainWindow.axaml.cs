using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using TERMinator.Linux.Models;
using TERMinator.Linux.Services;
using TERMinator.Linux.ViewModels;

namespace TERMinator.Linux.Views;

public partial class MainWindow : Window, Controls.ICommandLineHost
{
    public const string AppVersion = "1.1.6";

    // Shown in the phonebook password box when a password is already stored,
    // so the saved one is never displayed in the clear.
    private const string PasswordPlaceholder = "\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022";

    // ViewModels
    private readonly HomeViewModel _homeVm;
    private readonly PhonebookViewModel _phonebookVm;
    private readonly TerminalViewModel _terminalVm;
    private readonly SettingsViewModel _settingsVm;

    // Views (lazy-created, cached)
    private HomeView? _homeView;
    private PhonebookView? _phonebookView;
    private TerminalView? _terminalView;
    private SettingsView? _settingsView;

    /// <summary>
    /// A window's normal (restored) placement: top-left in the screen's physical pixels (carries the monitor)
    /// and the client size in DIPs. The Linux stand-in for the Windows client's Win32 placement rect.
    /// </summary>
    private readonly record struct NormalBounds(PixelPoint Position, double Width, double Height);

    // Pre-connection window position and shape (restored exactly on disconnect)
    private NormalBounds? _preConnectBounds;

    // The last bounds seen while the window was in its normal state, so a maximized / full-screen window still
    // remembers where it returns to (Avalonia exposes no "restored bounds" of its own)
    private NormalBounds? _lastNormalBounds;

    // Home-screen window fit: set when nothing was saved, applied once the window is on screen
    private bool _fitHomeOnOpen;
    private bool _fitting;
    private Rect _lastHomeBounds;

    // Active connection (for Send Login)
    private SavedConnection? _activeConnection;

    // ---- One window per BBS ----
    // Every BBS opens in a window, and a process, of its own (App.SessionConnection): the terminal engine holds one
    // session per process. The main window keeps the home screen and phonebook and never turns into a terminal; it
    // starts a session process for each board (LaunchSession). A session window connects as it opens and closes when
    // its call ends.

    /// <summary>This window is one BBS session rather than the main window.</summary>
    private static bool IsSession => App.SessionConnection != null;

    /// <summary>Main window: connections open in windows of their own (Options > Separate Window per BBS). Off, the
    /// main window becomes the terminal as it did before 1.1.5.</summary>
    private static bool LaunchesSessions => !IsSession && App.SettingsService.Settings.MultiWindow;

    /// <summary>Main window: the session processes it started, for closing them with it.</summary>
    private readonly List<Process> _sessions = [];

    private bool _sessionsConfirmed;

    /// <summary>Session: watches for the main window's close signal (the Linux stand-in for the Windows client's
    /// named EventWaitHandle, which .NET doesn't share between processes here).</summary>
    private DispatcherTimer? _closeSignalWatch;

    /// <summary>Session: closing, so the end of the call shows no Disconnected dialog and nothing reopens.</summary>
    private bool _sessionClosing;

    /// <summary>The main window's close signal to its sessions: a file named after its process, created when it
    /// closes. The sessions it started look for it twice a second.</summary>
    private static string CloseSessionsSignalPath(int mainPid) =>
        Path.Combine(Path.GetTempPath(), $"TERMinator-CloseSessions-{mainPid}");

    // Fullscreen state
    private bool _isFullScreen;
    private WindowState _savedWindowState;
    private double _savedTop, _savedLeft, _savedWidth, _savedHeight;

    // Aspect ratio locking
    private double _terminalAspectRatio;
    private double _statusBarHeight;
    private bool _aspectRatioLocked;
    private bool _adjustingSize;
    // Window size the terminal aspect lock last saw, so a resize can tell which edge moved
    private Rect _lastTerminalBounds;

    public MainWindow()
    {
        InitializeComponent();
        SetAppTitle();

        // Create ViewModels with service singletons from App
        _homeVm = new HomeViewModel(App.PhonebookService, App.SettingsService);
        _phonebookVm = new PhonebookViewModel(App.PhonebookService, App.CredentialService, App.SettingsService);
        _terminalVm = new TerminalViewModel(
            App.ConnectionManager,
            App.SessionLogger,
            App.AudioService,
            App.SettingsService,
            App.CredentialService);
        _settingsVm = new SettingsViewModel(App.SettingsService);

        // Wire up navigation events
        _homeVm.ConnectRequested += OnConnectRequested;
        _homeVm.PhonebookRequested += () => NavigateToPhonebook();
        _homeVm.SettingsRequested += () => NavigateToSettings();
        _homeVm.PickQuickConnectRequested += OnPickQuickConnect;
        _homeVm.CrtRequested += ShowCrtOptions;
        // Flip the View menu item so the menu checkmark, the saved setting and the window fit all stay in step
        _homeVm.ToggleLayoutRequested += () => MenuSideBySideHome.IsChecked = !MenuSideBySideHome.IsChecked;
        _homeVm.CommandLineRequested += () => _ = EnterCommandLineFromGui();

        _phonebookVm.ConnectRequested += OnConnectRequested;
        _phonebookVm.EditRequested += OnEditConnection;
        _phonebookVm.BackRequested += () => NavigateToHome();
        _phonebookVm.ConfirmDeleteRequested += OnConfirmDelete;

        // TRACE: modules a door asks for during a session (Services/TraceSessionManager.cs)
        _trace = new Services.TraceSessionManager(EngineHost,
            () => _terminalView != null && ContentArea.Content == _terminalView ? _terminalView.TerminalCanvas : null,
            bytes => Services.NativeBridge.syncterm_send_data(bytes, bytes.Length),
            () =>
            {
                if (_terminalView != null && ContentArea.Content == _terminalView)
                    _terminalView.FocusTerminal();
            },
            () => App.SettingsService.Settings.TraceEnabled,
            () => App.SettingsService.Settings.TraceCacheLimitMb,
            // Module sound rides the soundtrack volume, like the SyncTERM door mix does: it is summed into that mix
            // (OpenAL channel 6), which already plays at the soundtrack volume, so this is only on or off
            () => App.SettingsService.Settings.SoundtrackEnabled ? 1f : 0f,
            // The board a door's modules came from, recorded beside them in the cache (Settings lists it)
            () => _activeConnection is { } c ? (string.IsNullOrWhiteSpace(c.Name) ? c.Host : c.Name) : null,
            // ...and as host:port, which the player's permissions for their imported files are kept by
            () => _activeConnection is { } k ? $"{k.Host.Trim().ToLowerInvariant()}:{k.Port}" : null);
        // Settings won't delete a cached module or asset a running module is using
        _settingsVm.TraceFilesInUse = () => _trace.FilesInUse;
        _terminalVm.ConnectionManager.TraceCommandReceived += command =>
            Dispatcher.UIThread.Post(() => _trace.Handle(command));
        _terminalVm.ConnectionManager.TraceBinaryReceived += frame =>
            Dispatcher.UIThread.Post(() => _trace.HandleBinary(frame));

        // The board's OSC 52 clipboard writes (Services/OscExtras.cs, 12.62); its OSC 0/2 title is not shown
        _terminalVm.ConnectionManager.BoardClipboardWrite += text =>
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    if (Clipboard is { } clipboard)
                    {
                        await clipboard.SetTextAsync(text);
                        _terminalView?.ShowToast("Copied from the BBS");
                    }
                }
                catch { /* no clipboard owner available: skip this one */ }
            });
        // The board's OSC 777 notifications (Services/BoardNotifier.cs, 12.72): a desktop notification (notify-send)
        // only while this call isn't in front, and only with Settings' Board Notifications on when it connected (else
        // dropped); an empty title shows the board's name
        _boardNotifier = new Services.BoardNotifier(BoardNotificationSuppressed, BringSessionToFront);
        _terminalVm.ConnectionManager.BoardNotification += (title, body) =>
            Dispatcher.UIThread.Post(() =>
            {
                if (!_terminalVm.ConnectionManager.BoardNotificationsOn)
                    return;
                if (title.Length == 0)
                    title = _activeConnection is { } c ? (string.IsNullOrWhiteSpace(c.Name) ? c.Host : c.Name) : "TERMinator";
                _boardNotifier.Post(title, body);
            });
        // The board's linked windows (Rainwire:Link, 12.73): Open starts one beside this window, Raise brings it forward
        _terminalVm.ConnectionManager.LinkRequested += request =>
            Dispatcher.UIThread.Post(() => OpenLinkedWindow(request));
        // A linked window shows the board's own title (OSC 0/2) after the board's name; other windows ignore it
        _terminalVm.ConnectionManager.BoardTitleChanged += title => Dispatcher.UIThread.Post(() =>
        {
            if (!IsLinked)
                return;
            _boardTitle = title;
            SetAppTitle();
        });
        _terminalVm.ConnectionManager.LinkRaise += kind => Dispatcher.UIThread.Post(() => RaiseLinkedWindow(kind));
        // ...and only right after the caller's own key or click on the call (tunnel, handled too: the terminal
        // takes its keys and clicks)
        AddHandler(KeyDownEvent, (_, _) => StampBoardGesture(), RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerPressedEvent, (_, _) => StampBoardGesture(), RoutingStrategies.Tunnel, handledEventsToo: true);
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty && WindowState != WindowState.Minimized)
                _stateBeforeMinimize = WindowState;
        };
        Closed += (_, _) => _boardNotifier.Dispose();
        Services.NativeBridge.syncterm_trace_set_modules(
            Services.TraceModules.Capabilities(App.SettingsService.Settings.TraceEnabled));

        // Rainwire: a board's menus drawn here, over the terminal (1.1.6, Controls/RainwireMenu.cs). Answers go back as
        // Rainwire:UI APC strings; the board's messages come in order from native/apc/ui_bridge.c.
        _rainwireMenu.Send += reply =>
            _terminalVm.ConnectionManager.SendData(System.Text.Encoding.UTF8.GetBytes("\x1b_Rainwire:UI;" + reply + "\x1b\\"));
        RainwireMenuHost.Children.Add(_rainwireMenu);
        _rainwireMenu.UpChanged += FitRainwireMenu;
        RainwireMenuHost.SizeChanged += (_, _) => FitRainwireMenu();
        _rainwireMenu.PictureRect = () =>
        {
            var canvas = _terminalView != null && ContentArea.Content == _terminalView ? _terminalView.TerminalCanvas : null;
            if (canvas == null || !canvas.IsEffectivelyVisible || canvas.TransformToVisual(_rainwireMenu) is not { } m)
                return null;   // not in the same visual tree right now
            return canvas.GetBbsScreenRect().TransformToAABB(m);
        };
        _rainwireMenu.Hidden += () =>
        {
            if (_terminalView != null && ContentArea.Content == _terminalView)
                _terminalView.FocusTerminal();
        };
        _terminalVm.ConnectionManager.UiMessageReceived += msg => Dispatcher.UIThread.Post(() => _rainwireMenu.Handle(msg));

        // Rainwire: a board's windows drawn here, over the terminal (RainwireWm/RainwireDesk.cs)
        _rainwireDesk = new RainwireWm.RainwireDesk(RainwireDeskHost,
            () => _terminalView != null && ContentArea.Content == _terminalView ? _terminalView.TerminalCanvas : null,
            () =>
            {
                if (_terminalView != null && ContentArea.Content == _terminalView)
                    _terminalView.FocusTerminal();
            });
        _rainwireDesk.ApplySmoothing(App.SettingsService.Settings.FontSmoothing);
        _rainwireMenu.SmoothPixels = App.SettingsService.Settings.FontSmoothing;
        _rainwireDesk.ApplyLook(App.SettingsService.Settings.RainwireGraphicalWindows);

        // Rainwire file drops (files=1): OS files onto desk windows and the Files module. Avalonia 11's X11 backend
        // has no XDnD, so the drop target is our own (RainwireWm/X11FileDropTarget.cs); no drags out on Linux yet.
        // A Rainwire scene's P_DROP blocks take files too, lit while they're over them (DragLeft turns that off).
        _fileDrops = new RainwireWm.RainwireFileDrops(_rainwireDesk.Session, _rainwireDesk.Layer,
            id => _trace.LayerFor(id),
            text => _terminalView?.ShowToast(text, 6),
            RunDropSend)
        {
            DrainMessages = _rainwireDesk.PollNow,
        };
        _fileDrops.AttachScene(_rainwireMenu);   // Files as a scene (scenes version 5): its drop targets and dragged rows
        Opened += (_, _) => _osFileDrop ??= RainwireWm.X11FileDropTarget.TryAttach(this, _fileDrops.CanTake, _fileDrops.Drop,
            _fileDrops.DragLeft);
        Closed += (_, _) => _osFileDrop?.Dispose();

        _terminalVm.DisconnectedEvent += () =>
        {
            Console.Error.WriteLine("[MainWindow] DisconnectedEvent fired! Showing disconnect dialog.");

            // Close the call in the call log (the byte counts were saved before the native state was reset) right away,
            // as Windows does with its synchronous Dispatcher.Invoke: queued behind the dialog it never ran when the
            // disconnect came from closing the app, and the call's time online and bytes were lost
            void EndCall()
            {
                _trace.CloseAll();
                _rainwireMenu.Reset();
                _rainwireDesk.Reset();
                _fileDrops.Reset();
                _boardNotifier.Reset();
                _boardTitle = string.Empty;
                SetAppTitle();
                var (sent, received) = _terminalVm.ConnectionManager.LastSessionBytes;
                App.CallStatsService.EndCall(sent, received);
            }
            if (Dispatcher.UIThread.CheckAccess())
                EndCall();
            else
                Dispatcher.UIThread.Invoke(EndCall);

            Dispatcher.UIThread.InvokeAsync(async () =>
            {
                _terminalView?.StopAllTapPlus();
                _terminalView?.HideAllPics();   // Rainwire's TERMinator:Pic pictures go with the call too
                var bbsName = _activeConnection?.Name ?? "BBS";
                // A linked window (12.73) closes quietly: the board ends it when the main call ends. (Not by setting
                // _sessionClosing first, as Windows does: CloseSession would then skip the Close.)
                if (!_sessionClosing && !IsLinked)
                    await ShowMessageBox("Disconnected", $"Disconnected from {bbsName}.", autoCloseSeconds: 10);
                NavigateToHome();
            });
        };

        _settingsVm.SaveRequested += () =>
        {
            // TRACE on/off: what doors are told when they ask, and stop running modules when it's switched off
            bool traceEnabled = App.SettingsService.Settings.TraceEnabled;
            Services.NativeBridge.syncterm_trace_set_modules(Services.TraceModules.Capabilities(traceEnabled));
            if (!traceEnabled)
                _trace.Disable();

            _terminalView?.TerminalCanvas.SetFontSmoothing(
                App.SettingsService.Settings.FontSmoothing);
            _rainwireDesk?.ApplySmoothing(App.SettingsService.Settings.FontSmoothing);
            _rainwireMenu.SmoothPixels = App.SettingsService.Settings.FontSmoothing;
            _rainwireDesk?.ApplyLook(App.SettingsService.Settings.RainwireGraphicalWindows);
            ApplyModemSettings();
            NavigateAfterSettings();
            // After the navigation, so a side-by-side change re-fits the window against the home screen it is for
            ApplyDisplaySettings();
        };
        _settingsVm.CancelRequested += () => NavigateAfterSettings();
        _settingsVm.CrtSettingsRequested += ShowCrtOptions;
        _settingsVm.ScreensaverTestRequested += ShowScreensaver;
        _settingsVm.PhosphorPreviewRequested += mode => ApplyPhosphor(mode);

        // Wire up paste support for TerminalViewModel
        _terminalVm.PasteRequested += async () =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
                return await clipboard.GetTextAsync();
            return null;
        };

        // Wire up ZMODEM auto-detect
        App.ConnectionManager.ZmodemDetected += OnZmodemDetected;

        // Wire up menu items
        MenuPhonebook.Click += (_, _) => NavigateToPhonebook();
        MenuImportPhonebook.Click += async (_, _) => await ImportPhonebook();
        MenuExportPhonebook.Click += async (_, _) => await ExportPhonebook();
        MenuDisconnect.Click += (_, _) => _terminalVm.DisconnectCommand.Execute(null);
        MenuSendLogin.Click += async (_, _) => await SendLoginSequence();
        MenuCopy.Click += (_, _) => CopyFromTerminal();
        MenuPaste.Click += (_, _) => _terminalVm.PasteCommand.Execute(null);
        MenuSelectAll.Click += (_, _) => SelectAllInTerminal();
        MenuSendFile.Click += (_, _) => ShowSendFileDialog();
        MenuReceiveFile.Click += (_, _) => ShowReceiveTransfer();
        MenuSendXmodem.Click += (_, _) => ShowXmodemSendDialog(
            NativeBridge.XferMode.Xmodem | NativeBridge.XferMode.Crc, "XMODEM");
        MenuSendXmodem128.Click += (_, _) => ShowXmodemSendDialog(
            NativeBridge.XferMode.Xmodem | NativeBridge.XferMode.Crc | NativeBridge.XferMode.Xmodem128B, "XMODEM");
        MenuSendYmodem.Click += (_, _) => ShowXmodemSendDialog(
            NativeBridge.XferMode.Ymodem | NativeBridge.XferMode.Crc, "YMODEM");
        MenuRecvXmodem.Click += (_, _) => ShowXmodemReceive(
            NativeBridge.XferMode.Xmodem | NativeBridge.XferMode.Crc, "XMODEM");
        MenuRecvYmodem.Click += (_, _) => ShowXmodemReceive(
            NativeBridge.XferMode.Ymodem | NativeBridge.XferMode.Crc, "YMODEM");
        MenuRecvYmodemG.Click += (_, _) => ShowXmodemReceive(
            NativeBridge.XferMode.Ymodem | NativeBridge.XferMode.Crc | NativeBridge.XferMode.GMode, "YMODEM-G");
        // Call → Log Session: checked while a transcript is being written, however logging was started
        MenuLogSession.Click += (_, _) =>
        {
            _terminalVm.ToggleLoggingCommand.Execute(null);
            MenuLogSession.IsChecked = _terminalVm.IsLogging;
        };
        _terminalVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(_terminalVm.IsLogging))
                MenuLogSession.IsChecked = _terminalVm.IsLogging;
        };
        MenuSettings.Click += (_, _) => NavigateToSettings();
        MenuFullScreen.Click += (_, _) => ToggleFullScreen();
        MenuScrollback.Click += (_, _) => ToggleScrollback();
        MenuCrt.Click += (_, _) => ShowCrtOptions();
        MenuSnapshot.Click += (_, _) => CaptureBbsSnapshot();
        MenuSaveScreenshot.Click += async (_, _) => await SaveScreenshot();
        MenuHelp.Click += (_, _) => ShowHelp();
        MenuSshLog.Click += async (_, _) => await ShowSshDebugLog();
        MenuExit.Click += (_, _) => Close();
        MenuAbout.Click += (_, _) => ShowAboutDialog();
        MenuChangelog.Click += (_, _) => ShowChangelogDialog();
        MenuCheckUpdates.Click += (_, _) => ShowUpdateDialog();


        // Side-by-side home screen: the Quick Connect block sits to the right of the splash instead of below it.
        // Set IsChecked before subscribing so restoring the saved choice doesn't immediately re-save it.
        // ZMODEM 8K Blocks: pushed to the native side now and whenever it changes
        MenuZmodem8k.IsChecked = App.SettingsService.Settings.Zmodem8k;
        NativeBridge.syncterm_zmodem_set_8k(MenuZmodem8k.IsChecked == true ? 1 : 0);
        MenuZmodem8k.PropertyChanged += (_, e) =>
        {
            if (e.Property == MenuItem.IsCheckedProperty)
                SetZmodem8k(MenuZmodem8k.IsChecked == true);
        };

        MenuSideBySideHome.IsChecked = App.SettingsService.Settings.HomeSideBySide;
        MenuSideBySideHome.PropertyChanged += (_, e) =>
        {
            if (e.Property == MenuItem.IsCheckedProperty)
                SetHomeSideBySide(MenuSideBySideHome.IsChecked == true);
        };

        // Boot sequence on startup (on by default). The sequence itself is ShowBootSequence(), played at launch.
        MenuBootSequence.IsChecked = App.SettingsService.Settings.ShowBootSequence;
        MenuBootSequence.PropertyChanged += (_, e) =>
        {
            if (e.Property == MenuItem.IsCheckedProperty)
                SetShowBootSequence(MenuBootSequence.IsChecked == true);
        };

        // Command line mode is a home-screen alternative, so it only opens from the home screen. The item is never
        // disabled; away from home it just does nothing.
        MenuCommandLine.Click += (_, _) =>
        {
            if (ContentArea.Content != _homeView) return;
            _ = EnterCommandLineFromGui();
        };

        // Idle screensaver on the home screen (on by default); StartIdleWatch puts it up after three idle minutes.
        MenuScreensaver.IsChecked = App.SettingsService.Settings.ScreensaverEnabled;
        MenuScreensaver.PropertyChanged += (_, e) =>
        {
            if (e.Property == MenuItem.IsCheckedProperty)
                SetScreensaverEnabled(MenuScreensaver.IsChecked == true);
        };

        // Phosphor: monochrome tube colors for the home screen and the command line (exactly one checked)
        MenuPhosphorColor.Click += (_, _) => SetPhosphor("Color");
        MenuPhosphorGreen.Click += (_, _) => SetPhosphor("Green");
        MenuPhosphorAmber.Click += (_, _) => SetPhosphor("Amber");
        MenuPhosphorWhite.Click += (_, _) => SetPhosphor("White");
        UpdatePhosphorMenu();

        // Modem speed emulation: one checkable item per speed. The checks are refreshed whenever the submenu opens,
        // so the command line's "baud" and Preferences never leave it showing a stale speed.
        foreach (int bps in ModemSpeeds.Rates)
        {
            var item = new MenuItem
            {
                Header = ModemSpeeds.Label(bps),
                ToggleType = MenuItemToggleType.CheckBox,
                Tag = bps
            };
            item.Click += (_, _) => SetEmulatedBaud(bps);
            MenuModemSpeed.Items.Add(item);
            if (bps == 0)
                MenuModemSpeed.Items.Add(new Separator());
        }
        MenuModemSpeed.SubmenuOpened += (_, _) => UpdateModemSpeedMenu();
        UpdateModemSpeedMenu();

        // Modem lights in the terminal status bar (on by default). IsChecked before subscribing, as above.
        MenuModemLights.IsChecked = App.SettingsService.Settings.ShowModemLights;
        MenuModemLights.PropertyChanged += (_, e) =>
        {
            if (e.Property == MenuItem.IsCheckedProperty)
                SetShowModemLights(MenuModemLights.IsChecked == true);
        };

        // IBM Model M key clicks while typing into a board or the command line (off by default)
        MenuKeyClicks.IsChecked = App.SettingsService.Settings.KeyClicksEnabled;
        MenuKeyClicks.PropertyChanged += (_, e) =>
        {
            if (e.Property == MenuItem.IsCheckedProperty)
                SetKeyClicksEnabled(MenuKeyClicks.IsChecked == true);
        };

        // Separate Window per BBS (on by default). IsChecked before subscribing, as above.
        MenuMultiWindow.IsChecked = App.SettingsService.Settings.MultiWindow;
        MenuMultiWindow.PropertyChanged += (_, e) =>
        {
            if (e.Property == MenuItem.IsCheckedProperty)
                SetMultiWindow(MenuMultiWindow.IsChecked == true);
        };

        // Let Boards Open Extra Windows (12.73, on by default): mw=1 in the Caps answer. IsChecked before subscribing.
        MenuLinkedWindows.IsChecked = App.SettingsService.Settings.LinkedWindows;
        MenuLinkedWindows.PropertyChanged += (_, e) =>
        {
            if (e.Property == MenuItem.IsCheckedProperty)
                SetLinkedWindows(MenuLinkedWindows.IsChecked == true);
        };

        if (IsSession)
            SetUpSessionWindow();
        else
        {
            ClearCloseSessionsSignal();
            // What the BBS windows saved meanwhile (their last calls, snapshots, settings changed there)
            Activated += (_, _) => RefreshFromSessions();
        }

        // Boss Hide: the menu entry is mainly there to advertise the keys. The spreadsheet covers the menu too; any
        // key brings TERMinator back.
        MenuBossHide.Click += (_, _) => ToggleBossHide();

        // Alt+Enter fullscreen toggle
        KeyDown += OnWindowKeyDown;
        KeyUp += OnWindowKeyUp;

        // The flair overlays and the Konami code need the key before the focused control does
        AddHandler(KeyDownEvent, OnWindowPreviewKeyDown, RoutingStrategies.Tunnel);

        // Boss Hide also has to stop typed text: Avalonia delivers it as TextInput after the key-down, so handling the
        // key-down alone would still let the character reach the board
        AddHandler(TextInputEvent, OnWindowPreviewTextInput, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnWindowPreviewKeyUp, RoutingStrategies.Tunnel);

        // A key-up that lands in another window never reaches us: don't leave Boss Hide waiting for it
        Deactivated += (_, _) =>
        {
            _bossKeyHeld = null;
            _swallowBossText = false;
        };

        // Restore the window's last position (monitor included) and shape. The saved rect is in physical
        // screen pixels, so it lands on the same monitor; a monitor that's no longer attached falls back to
        // the default placement. With nothing saved yet (first run, or settings from before bounds were
        // stored) the window is fitted to the home layout once it is on screen instead.
        var settings = App.SettingsService.Settings;
        int[]? wb = IsSession ? SessionStartBounds() : settings.WindowBounds;
        // A linked window the board opened with focus=0 (12.73) shows without taking the focus
        if (IsSession && App.SessionNoActivate)
            ShowActivated = false;
        bool startMaximized = !IsSession && settings.WindowMaximized;
        if (!RestoreSavedBounds(wb) && !IsSession)
            _fitHomeOnOpen = true;
        if (startMaximized) WindowState = WindowState.Maximized;

        // Enforce aspect ratio on window resize (terminal: the picture's aspect; home: the layout's)
        this.GetObservable(BoundsProperty).Subscribe(new ActionObserver<Rect>(OnBoundsChanged));
        PositionChanged += (_, _) => RememberNormalBounds();

        Opened += (_, _) =>
        {
            // Let the first layout pass settle so the menu bar and content area have real sizes
            Dispatcher.UIThread.Post(() =>
            {
                RememberNormalBounds();

                // A session window goes straight to its board, once it's showing
                if (IsSession)
                {
                    OnConnectRequested(App.SessionConnection!);
                    return;
                }

                if (_fitHomeOnOpen)
                {
                    _fitHomeOnOpen = false;
                    FitWindowToHome(centerOnScreen: true);
                }
                else
                {
                    ConformWindowToHome();   // restored bounds: give them the home layout's shape
                }
                // The window is on screen now, so nothing of the effect is missed: warm the tube up and start the
                // boot text when the picture is open (or, with the boot sequence off, power the home screen on).
                if (_bootOverlay is { } boot)
                {
                    Effects.CrtPower.PowerOn(boot, CrtPowerHost);
                    boot.Start(Effects.CrtPower.PowerOnDuration);
                }
                else
                {
                    Effects.CrtPower.PowerOn(ContentHost, CrtPowerHost);
                }
            }, DispatcherPriority.Loaded);
        };

        StartIdleWatch();

        // A session window has no home screen or boot sequence: it connects from the Opened handler above
        if (IsSession)
            return;

        // Start on Home view. The saved placement wins; only a first run is fitted to the home layout (above).
        NavigateToHome();

        // With the boot sequence on, the window opens black: the overlay goes up before the first frame is drawn and
        // the app underneath is only revealed when the boot screen hands over. Typing starts from the Opened handler.
        if (App.SettingsService.Settings.ShowBootSequence)
        {
            ShowBootSequence();
        }
        else
        {
            // Collapsed until the Opened handler powers the tube on, so the picture doesn't flash into view first
            ContentHost.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
            ContentHost.RenderTransform = new ScaleTransform(0, 0);
        }
    }

    // ================================================================
    // View menu settings
    // ================================================================

    private static void SetZmodem8k(bool enable)
    {
        App.SettingsService.Settings.Zmodem8k = enable;
        App.SettingsService.Save();
        NativeBridge.syncterm_zmodem_set_8k(enable ? 1 : 0);
    }

    private void SetHomeSideBySide(bool sideBySide)
    {
        App.SettingsService.Settings.HomeSideBySide = sideBySide;
        App.SettingsService.Save();
        _homeView?.SetSideBySide(sideBySide);
        FitWindowToHome(centerOnScreen: false);
    }

    private void SetShowBootSequence(bool show)
    {
        App.SettingsService.Settings.ShowBootSequence = show;
        App.SettingsService.Save();
        // Nothing else to do: the setting is only read at launch, so the change shows next time TERMinator starts.
    }

    private void SetScreensaverEnabled(bool enabled)
    {
        App.SettingsService.Settings.ScreensaverEnabled = enabled;
        App.SettingsService.Save();
        // Turning it on shouldn't fire the saver straight away if the user has been reading the menu for a while
        _lastInputUtc = DateTime.UtcNow;
    }

    private void SetPhosphor(string mode)
    {
        App.SettingsService.Settings.Phosphor = mode;
        App.SettingsService.Save();
        UpdatePhosphorMenu();
        ApplyPhosphor();
    }

    /// <summary>Exactly one Phosphor entry checked, from the saved setting (unknown values read as Color).</summary>
    private void UpdatePhosphorMenu()
    {
        string mode = App.SettingsService.Settings.Phosphor ?? "Color";
        MenuPhosphorColor.IsChecked = mode == "Color";
        MenuPhosphorGreen.IsChecked = mode == "Green";
        MenuPhosphorAmber.IsChecked = mode == "Amber";
        MenuPhosphorWhite.IsChecked = mode == "White";
        if (!MenuPhosphorGreen.IsChecked && !MenuPhosphorAmber.IsChecked && !MenuPhosphorWhite.IsChecked)
            MenuPhosphorColor.IsChecked = true;
    }

    // ================================================================
    // Modem, key clicks and Preferences sync
    // ================================================================

    /// <summary>
    /// Brings the menu checks and the screen in step with the Display settings saved from Preferences. Setting a
    /// check to the value it already has raises nothing, so only what actually changed is re-applied.
    /// </summary>
    private void ApplyDisplaySettings()
    {
        var settings = App.SettingsService.Settings;
        MenuBootSequence.IsChecked = settings.ShowBootSequence;
        MenuScreensaver.IsChecked = settings.ScreensaverEnabled;
        MenuSideBySideHome.IsChecked = settings.HomeSideBySide;
        UpdatePhosphorMenu();
        ApplyPhosphor();
    }

    /// <summary>Pushes the modem settings (speed emulation, lights, key clicks) to where they take effect.</summary>
    private void ApplyModemSettings()
    {
        var settings = App.SettingsService.Settings;
        NativeBridge.syncterm_set_emulated_bps(settings.EmulatedBaud);
        UpdateModemSpeedMenu();
        MenuModemLights.IsChecked = settings.ShowModemLights; // no-op when already in step
        MenuKeyClicks.IsChecked = settings.KeyClicksEnabled;
        _terminalView?.ApplyModemLightsSetting();
        if (settings.KeyClicksEnabled)
            App.KeyClickService.WarmUp();
    }

    /// <summary>Stores the emulated modem speed (0 = full speed) and applies it at once, mid-session included.</summary>
    private void SetEmulatedBaud(int bps)
    {
        App.SettingsService.Settings.EmulatedBaud = bps;
        App.SettingsService.Save();
        NativeBridge.syncterm_set_emulated_bps(bps);
        UpdateModemSpeedMenu();
    }

    /// <summary>Checks exactly the Call → Modem Speed item for the stored speed.</summary>
    private void UpdateModemSpeedMenu()
    {
        int current = App.SettingsService.Settings.EmulatedBaud;
        foreach (var item in MenuModemSpeed.Items.OfType<MenuItem>())
            item.IsChecked = item.Tag is int bps && bps == current;
    }

    /// <summary>View → Modem Lights: shows or hides the lights at once, mid-session included.</summary>
    private void SetShowModemLights(bool show)
    {
        if (App.SettingsService.Settings.ShowModemLights != show)
        {
            App.SettingsService.Settings.ShowModemLights = show;
            App.SettingsService.Save();
        }
        _terminalView?.ApplyModemLightsSetting();
    }

    /// <summary>Options → Model M Key Clicks: turns the clicks on or off; turning them on loads the clips right away.</summary>
    private void SetKeyClicksEnabled(bool enabled)
    {
        if (App.SettingsService.Settings.KeyClicksEnabled != enabled)
        {
            App.SettingsService.Settings.KeyClicksEnabled = enabled;
            App.SettingsService.Save();
        }
        if (enabled)
            App.KeyClickService.WarmUp();
    }

    // ================================================================
    // Boss Hide
    // ================================================================

    private Controls.BossKeyOverlay? _bossOverlay;

    // TRACE: TERMinator Render And Compute Engine (BBSGames/Doom/ENGINE.md)
    private Services.TraceSessionManager _trace = null!;
    private RainwireWm.RainwireDesk _rainwireDesk = null!;
    private readonly Controls.RainwireMenu _rainwireMenu = new();

    /// <summary>The board's menu covers the terminal's area only, so the status bar under it (connection, modem lights,
    /// bytes) stays in sight, as with the TRACE Menu module.</summary>
    private void FitRainwireMenu()
    {
        var canvas = _terminalView != null && ContentArea.Content == _terminalView ? _terminalView.TerminalCanvas : null;
        if (canvas == null || !canvas.IsEffectivelyVisible || RainwireMenuHost.Bounds.Width <= 0
            || canvas.TransformToVisual(RainwireMenuHost) is not { } m)
        {
            _rainwireMenu.Margin = default;
            return;
        }
        var r = new Rect(canvas.Bounds.Size).TransformToAABB(m);
        _rainwireMenu.Margin = new Thickness(Math.Max(r.Left, 0), Math.Max(r.Top, 0),
            Math.Max(RainwireMenuHost.Bounds.Width - r.Right, 0), Math.Max(RainwireMenuHost.Bounds.Height - r.Bottom, 0));
    }
    private RainwireWm.RainwireFileDrops _fileDrops = null!;
    private RainwireWm.X11FileDropTarget? _osFileDrop;

    // Board notifications (OSC 777, 12.72)
    private Services.BoardNotifier _boardNotifier = null!;

    /// <summary>What a board notification's click un-minimizes the window to.</summary>
    private WindowState _stateBeforeMinimize = WindowState.Normal;

    /// <summary>The call is on screen in the active window (the board says it there too), or Boss Hide is up (a
    /// notification would give the game away): no notification.</summary>
    private bool BoardNotificationSuppressed() =>
        App.BossKeyActive
        || (IsActive && WindowState != WindowState.Minimized && _terminalView != null && ContentArea.Content == _terminalView);

    /// <summary>A board notification was clicked: this window to front (un-minimized to the state it had) and the
    /// call on screen. The window manager may still refuse the focus (focus-stealing prevention).</summary>
    private void BringSessionToFront()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = _stateBeforeMinimize;
        Activate();
        if (_bossOverlay == null && App.ConnectionManager.State == ConnectionState.Connected
            && _terminalView != null && ContentArea.Content != _terminalView)
            NavigateToTerminal();
    }

    /// <summary>Alt+Enter is held: its auto-repeats must not toggle full screen again (Windows checks e.IsRepeat).</summary>
    private bool _altEnterHeld;

    /// <summary>The window's real title, kept while the Boss Hide screen shows a fake one.</summary>
    private string _appTitle = string.Empty;

    /// <summary>A linked window's title from the board (OSC 0/2), "" = none; other windows never set it.</summary>
    private string _boardTitle = string.Empty;

    /// <summary>The key that last opened or closed the screen, until it's released: its repeats are ignored.</summary>
    private Key? _bossKeyHeld;

    /// <summary>Set when a key toggled the screen: the text that key produces must not reach the board.</summary>
    private bool _swallowBossText;

    /// <summary>Sets the app's title; while the Boss Hide screen is up it's only remembered for later.</summary>
    private void SetAppTitle()
    {
        // the board's OSC 0/2 title is no longer shown: the app's own title stays up. A linked window (12.73) is the
        // exception: "<board> — <board's title>", or its Open title until the board sets one.
        _appTitle = (App.SessionConnection is { } session
                ? session.Linked && _boardTitle.Length > 0 && !string.IsNullOrWhiteSpace(session.LinkBoard)
                    ? $"{session.LinkBoard} — {_boardTitle} - TERMinator"
                    : $"{(string.IsNullOrWhiteSpace(session.Name) ? session.Host : session.Name)} - TERMinator"
                : $"TERMinator - BBS Terminal - v{AppVersion}");
        if (_bossOverlay == null)
            Title = _appTitle;
    }

    /// <summary>
    /// Covers the whole window (menu included) with a boring spreadsheet and a matching window title, or puts
    /// everything back. The session carries on underneath; bells and key clicks stay quiet while it's up.
    /// </summary>
    private void ToggleBossHide()
    {
        if (_bossOverlay != null)
        {
            FxOverlayHost.Children.Remove(_bossOverlay);
            _bossOverlay = null;
            App.BossKeyActive = false;
            Title = _appTitle;

            if (_commandLine != null && FxOverlayHost.Children.Contains(_commandLine))
                _commandLine.FocusInput();
            else if (_terminalView != null && ContentArea.Content == _terminalView)
                _terminalView.FocusTerminal();
            return;
        }

        _bossOverlay = new Controls.BossKeyOverlay();
        FxOverlayHost.Children.Add(_bossOverlay);
        App.BossKeyActive = true;
        Title = Controls.BossKeyOverlay.FakeTitle;
        _bossOverlay.Focus();
    }

    private void OnWindowPreviewTextInput(object? sender, TextInputEventArgs e)
    {
        if (_rainwireMenu.IsUp && ContentArea.Content == _terminalView)
        {
            _rainwireMenu.HandleText(e.Text);   // a typed key picks its item
            e.Handled = true;
            return;
        }
        if (_trace.OwnsKeyboard)
        {
            // A module with text input on gets the composed characters the keyboard layout made (Shift, AltGr,
            // dead keys); either way they don't reach controls behind TRACE
            _trace.HandleText(e.Text);
            e.Handled = true;
            return;
        }
        if (_bossOverlay != null || _swallowBossText)
        {
            _swallowBossText = false;
            e.Handled = true;
        }
    }

    private void OnWindowPreviewKeyUp(object? sender, KeyEventArgs e)
    {
        // TRACE owns the keyboard (see OnWindowPreviewKeyDown); this also stops a released Alt opening the menu
        if (e.Key == Key.Enter)
            _altEnterHeld = false;
        if (_trace.OwnsKeyboard)
        {
            _trace.HandleKey(e, pressed: false);
            e.Handled = true;
            return;
        }

        if (_bossKeyHeld == e.Key)
        {
            _bossKeyHeld = null;
            _swallowBossText = false;
            e.Handled = true;
        }
        else if (_bossOverlay != null)
        {
            e.Handled = true;
        }
    }

    // ================================================================
    // Flair: boot sequence, command line hand-off and the Konami easter egg
    // ================================================================

    /// <summary>The boot screen while it is up, so the Opened handler can start it once the window is showing.</summary>
    private Controls.BootSequenceOverlay? _bootOverlay;

    /// <summary>The boot sequence text: fixed BIOS flavor plus the real version, board count, TAP+, CRT and rank.</summary>
    private static string[] BuildBootLines()
    {
        var settings = App.SettingsService.Settings;
        int boards = App.PhonebookService.Connections.Count;
        string soundtrack = settings.SoundtrackEnabled
            ? $"Enabled  (cache limit {settings.SoundtrackCacheLimitMb} MB)"
            : "Disabled";
        string crt = settings.Crt.Enabled ? $"ON  ({settings.Crt.Preset})" : "OFF";
        return
        [
            $"TERMinator BIOS v{AppVersion}    (C) 1989-2026 JSONBourne Innovations",
            "",
            "CPU    : 80486DX2-66 ........................ OK",
            "MEMORY : 640K base, 15360K extended ......... OK",
            "UART   : COM1 16550A @ 115200 bps ........... OK",
            "",
            "ATZ",
            "OK",
            "AT&F1 S0=0 E1 Q0 V1",
            "OK",
            $"PHONEBOOK : {boards} board{(boards == 1 ? "" : "s")} loaded",
            $"TAP+      : {soundtrack}",
            $"CRT MODE  : {crt}",
            $"RANK      : {EliteRank.NameFor(settings.TotalCalls)}  ({settings.TotalCalls} call{(settings.TotalCalls == 1 ? "" : "s")})",
            "",
            "CARRIER DETECT",
            "CONNECT 115200"
        ];
    }

    /// <summary>
    /// Launch: the boot screen goes up black over the app, its text types out once the window is showing, then it cuts
    /// straight to the app. Avalonia has no "hidden but laid out" visibility, so the app underneath is made fully
    /// transparent instead of collapsed — it keeps its size, which the window fit measures.
    /// </summary>
    private void ShowBootSequence()
    {
        var boot = new Controls.BootSequenceOverlay(BuildBootLines());
        _bootOverlay = boot;

        ContentArea.Opacity = 0;
        ContentArea.IsHitTestVisible = false;
        MainMenu.Opacity = 0;
        MainMenu.IsHitTestVisible = false;

        // The boot screen itself starts collapsed, so the very first frame is black and the power-on opens the
        // picture up from nothing instead of the text flashing into view first.
        boot.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        boot.RenderTransform = new ScaleTransform(0, 0);

        void RevealApp()
        {
            _bootOverlay = null;
            FxOverlayHost.Children.Remove(boot);
            ContentArea.Opacity = 1;
            ContentArea.IsHitTestVisible = true;
            MainMenu.Opacity = 1;
            MainMenu.IsHitTestVisible = true;
        }

        boot.Finished += (_, _) => RevealApp();
        boot.CommandLineRequested += (_, _) =>
        {
            RevealApp();
            ShowCommandLine();
        };

        FxOverlayHost.Children.Add(boot);
    }

    /// <summary>The console, kept for the app's lifetime so its scrollback and history survive between visits.</summary>
    private Controls.CommandLineOverlay? _commandLine;

    /// <summary>Set when a session was started from the command line, so ending it returns there.</summary>
    private bool _returnToCommandLine;

    /// <summary>The board that command line session was for (the window forgets its active connection on the way home).</summary>
    private string? _commandLineSessionBoard;

    /// <summary>Whether that session actually got connected, to tell a disconnect from a failed or cancelled call.</summary>
    private bool _commandLineSessionConnected;

    /// <summary>
    /// Command line mode (§6): a full-window console over the home screen. One instance, so the scrollback and the
    /// history survive between visits; <paramref name="notice"/> is the line a finished session leaves behind
    /// ("Disconnected from X"). <paramref name="boot"/> types the boot sequence into it first, for entering from the
    /// home screen.
    /// </summary>
    private void ShowCommandLine(string? notice = null, bool boot = false)
    {
        // The console stands in for the home screen, so a request from anywhere else goes home first
        if (ContentArea.Content != _homeView)
            NavigateToHome();

        if (_commandLine == null)
        {
            _commandLine = new Controls.CommandLineOverlay(this);
            ApplyConsoleEffect();
        }
        if (!FxOverlayHost.Children.Contains(_commandLine))
            FxOverlayHost.Children.Add(_commandLine);
        if (notice != null)
            _commandLine.WriteNotice(notice);
        if (boot)
            _commandLine.PlayBootIntro();
        else
            _commandLine.FocusInput();
    }

    private void HideCommandLine()
    {
        if (_commandLine != null)
            FxOverlayHost.Children.Remove(_commandLine);
    }

    /// <summary>The COMMAND tile and View → Command Line Mode ask first; C at the boot prompt goes straight in.</summary>
    private async Task EnterCommandLineFromGui()
    {
        if (await ConfirmCommandLineMode())
            ShowCommandLine(boot: true);
    }

    /// <summary>
    /// "Are you Elite enough?" check before entering command line mode from the home screen (the COMMAND tile or the
    /// View menu). Pressing C at the boot prompt skips it, since that's already a deliberate choice.
    /// </summary>
    private async Task<bool> ConfirmCommandLineMode()
    {
        bool elite = false;

        var dialog = new Window
        {
            Title = "Command Line Mode",
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
            Background = new SolidColorBrush(Color.Parse("#FF0D0D1A")),
            Foreground = new SolidColorBrush(Color.Parse("#FFCCCCCC"))
        };

        var panel = new StackPanel { Margin = new Thickness(28, 24, 28, 20) };
        panel.Children.Add(new TextBlock
        {
            Text = "Are you Elite enough for command line mode?",
            Foreground = new SolidColorBrush(Color.Parse("#FFFFAA00")),
            FontSize = 16,
            FontWeight = FontWeight.Bold,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Type \"help\" once you're in. \"gui\" gets you back.",
            Foreground = new SolidColorBrush(Color.Parse("#FF888899")),
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 18)
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var yes = new Button
        {
            Content = "YES, I'M ELITE",
            Classes = { "amber" },
            IsDefault = true,
            Margin = new Thickness(0, 0, 10, 0)
        };
        yes.Click += (_, _) =>
        {
            elite = true;
            dialog.Close();
        };
        var no = new Button { Content = "NOT TODAY", IsCancel = true };
        no.Click += (_, _) => dialog.Close();
        buttons.Children.Add(yes);
        buttons.Children.Add(no);
        panel.Children.Add(buttons);

        dialog.Content = panel;
        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                dialog.Close();
        };

        await dialog.ShowDialog(this);
        return elite;
    }

    /// <summary>
    /// The command line's screen effect (§7): the full CRT effect when CRT mode is on, in the phosphor color if one is
    /// set, otherwise just the phosphor tint. Re-applied whenever either setting changes. Fail-soft like the terminal.
    /// </summary>
    private void ApplyConsoleEffect()
    {
        if (_commandLine == null)
            return;
        try
        {
            var settings = App.SettingsService.Settings;
            _commandLine.ApplyScreenEffect(settings.Crt, PhosphorTint(settings.Phosphor));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Command line screen effect could not be applied: {ex}");
        }
    }

    // ================================================================
    // Retro effects: phosphor, CRT power, idle screensaver (§7, §8)
    // ================================================================

    private bool _poweredOffForClose;
    private static readonly TimeSpan ScreensaverIdleTime = TimeSpan.FromMinutes(3);
    private DateTime _lastInputUtc = DateTime.UtcNow;
    private DispatcherTimer? _idleTimer;
    private List<(string Name, Bitmap Image)>? _saverSlides;
    private string? _saverSlidesSignature;

    /// <summary>
    /// Phosphor modes turn the whole app into a monochrome tube. The tint layer sits over ContentArea, so every screen
    /// swapped in there is covered — home, phonebook, Preferences and the terminal session with its status bar. The
    /// command line lives in the overlay layer and gets the tint through its own screen effect. The menu bar and
    /// separate windows stay in color. Color mode removes the effect. <paramref name="previewMode"/>: Preferences
    /// showing a pick that isn't saved yet.
    /// </summary>
    private void ApplyPhosphor(string? previewMode = null)
    {
        var tint = PhosphorTint(previewMode ?? App.SettingsService.Settings.Phosphor);
        try
        {
            ContentPhosphorLayer.Configure(null, tint);
            _homeView?.SetPhosphor(null); // the home screen's own layer would tint it twice
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Phosphor could not be applied: {ex}");
        }
        ApplyConsoleEffect();
    }

    private static Color? PhosphorTint(string? mode) => mode switch
    {
        "Green" => Color.FromRgb(0x33, 0xFF, 0x66),
        "Amber" => Color.FromRgb(0xFF, 0xB0, 0x00),
        "White" => Color.FromRgb(0xEE, 0xF4, 0xFF),
        _ => null
    };

    /// <summary>The old-tube power-off, then the close that OnClosing cancelled.</summary>
    private async Task PowerOffAndCloseAsync()
    {
        try
        {
            await Effects.CrtPower.PowerOffAsync(ContentHost, CrtPowerHost);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"CRT power-off failed: {ex}");
        }
        Close();
    }

    /// <summary>
    /// Watches for idleness on the home screen and puts the screensaver up after three minutes. Mouse movement, clicks
    /// and keys all count as activity; the tunnelling routes see them before any control can swallow them.
    /// </summary>
    private void StartIdleWatch()
    {
        void Touched(object? sender, EventArgs e) => _lastInputUtc = DateTime.UtcNow;
        AddHandler(PointerMovedEvent, Touched, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerPressedEvent, Touched, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyDownEvent, Touched, RoutingStrategies.Tunnel, handledEventsToo: true);

        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _idleTimer.Tick += (_, _) =>
        {
            // Only on a quiet home screen: not while connected, in the command line, or under another effect
            if (!App.SettingsService.Settings.ScreensaverEnabled
                || WindowState == WindowState.Minimized
                || _homeView == null || ContentArea.Content != _homeView
                || FxOverlayHost.Children.Count > 0 || EngineHost.Children.Count > 0
                || DateTime.UtcNow - _lastInputUtc < ScreensaverIdleTime)
                return;
            ShowScreensaver();
        };
        _idleTimer.Start();
    }

    /// <summary>
    /// The idle screensaver: the phonebook boards that have a snapshot, in random order. Saved snapshot files and the
    /// bundled ones the preloaded boards use both count (never the "no snapshot" placeholder); with none at all it
    /// shows the splash art instead.
    /// </summary>
    private void ShowScreensaver()
    {
        // Decoded once and kept: the saver comes back every few idle minutes and shows the same pictures the home
        // screen already has. A changed phonebook (a board added, or a fresh snapshot taken) rebuilds the list.
        // The write time is part of the signature: a fresh snapshot of a board overwrites the same file, so
        // the path alone would keep handing back the picture the saver decoded the first time round.
        string signature = string.Join('\u0001', App.PhonebookService.Connections.Select(b =>
        {
            long stamp = 0;
            try
            {
                if (!string.IsNullOrEmpty(b.SnapshotPath) && System.IO.File.Exists(b.SnapshotPath))
                    stamp = System.IO.File.GetLastWriteTimeUtc(b.SnapshotPath).Ticks;
            }
            catch { /* an unreadable path just counts as stamp 0 */ }
            return $"{b.Name}={b.SnapshotPath}@{stamp}";
        }));
        if (_saverSlides is { Count: > 0 } cached && _saverSlidesSignature == signature)
        {
            RunScreensaver(cached);
            return;
        }
        var slides = new List<(string Name, Bitmap Image)>();
        foreach (var board in App.PhonebookService.Connections)
        {
            string? path = board.SnapshotPath;
            if (string.IsNullOrEmpty(path) || path.Contains("no_snapshot", StringComparison.OrdinalIgnoreCase))
                continue;   // no picture of its own (the placeholder is not worth a slide)
            // Bundled snapshots of the preloaded boards, or a file still on disk. The same filter the Windows
            // client uses, and it keeps the converter's missing-file placeholder out of the slideshow.
            if (!path.StartsWith("avares://", StringComparison.OrdinalIgnoreCase)
                && !System.IO.File.Exists(path))
                continue;
            if (Converters.AvaresImageConverter.Instance.Convert(
                    path, typeof(Bitmap), null, System.Globalization.CultureInfo.InvariantCulture) is Bitmap image)
                slides.Add((board.Name, image));
        }
        if (slides.Count == 0)
        {
            if (Converters.AvaresImageConverter.Instance.Convert(
                    "avares://TERMinator/Assets/splash_screen.png", typeof(Bitmap), null,
                    System.Globalization.CultureInfo.InvariantCulture) is Bitmap splash)
                slides.Add(("TERMinator", splash));
            else
                return;
        }

        _saverSlides = slides;
        _saverSlidesSignature = signature;
        RunScreensaver(slides);
    }

    /// <summary>Slide list in hand, up it goes.</summary>
    private void RunScreensaver(IReadOnlyList<(string Name, Bitmap Image)> slides)
    {
        var saver = new Controls.ScreensaverOverlay(slides);
        ShowFxOverlay(saver, handler => saver.Finished += handler);

        // The saver covers everything, so stop rendering the home screen underneath while it runs. Otherwise every
        // frame of the drifting picture re-renders the cards' glows (and the CRT thumbnails) below it, which made the
        // movement lag badly on Windows. The window's size is set explicitly, so hiding the content can't resize it.
        ContentArea.IsVisible = false;
        _homeView?.SetCovered(true);   // and stop its animations, which hiding alone doesn't
        saver.Finished += (_, _) =>
        {
            ContentArea.IsVisible = true;
            _homeView?.SetCovered(false);
            _lastInputUtc = DateTime.UtcNow;
        };
    }

    // ================================================================
    // ICommandLineHost: what the console needs from the app around it
    // ================================================================

    IReadOnlyList<SavedConnection> Controls.ICommandLineHost.Boards => App.PhonebookService.Connections.ToList();

    string Controls.ICommandLineHost.VersionText => AppVersion;

    bool Controls.ICommandLineHost.CrtEnabled => App.SettingsService.Settings.Crt.Enabled;

    void Controls.ICommandLineHost.SetCrtEnabled(bool enabled)
    {
        App.SettingsService.Settings.Crt.Enabled = enabled;
        App.SettingsService.Save();
        ApplyCrtToTerminal();
        ApplyCrtToHome();
        ApplyConsoleEffect();
    }

    SavedConnection Controls.ICommandLineHost.CreateConnection(string host, int port, ConnectionProtocol protocol,
                                                              string? username)
    {
        var settings = App.SettingsService.Settings;
        return new SavedConnection
        {
            Name = host,
            Host = host,
            Port = port,
            Protocol = protocol,
            Username = username,
            FontName = settings.DefaultFont,
            ScreenMode = settings.DefaultScreenMode
        };
    }

    void Controls.ICommandLineHost.ConnectTo(SavedConnection connection)
    {
        // The board opens in a window of its own and the command line stays where it is
        if (LaunchesSessions)
        {
            LaunchSession(connection);
            return;
        }

        HideCommandLine();
        _returnToCommandLine = true;
        _commandLineSessionBoard = connection.Name;
        _commandLineSessionConnected = false;

        // OnConnectRequested can bail out before reaching the terminal (a cancelled SSH password prompt). Nothing
        // navigates home in that case, so it brings the console straight back with "connection cancelled" itself.
        OnConnectRequested(connection);
    }

    void Controls.ICommandLineHost.AddBoard(SavedConnection board)
    {
        App.PhonebookService.Add(board);
        _phonebookVm.Refresh();
    }

    void Controls.ICommandLineHost.RemoveBoard(SavedConnection board)
    {
        App.PhonebookService.Remove(board.Id);
        _phonebookVm.Refresh();
        _homeVm.LoadQuickConnects(); // it may have been on a Quick Connect tile
    }

    SavedConnection? Controls.ICommandLineHost.GetQuickConnect(int slot) => slot switch
    {
        1 => _homeVm.QuickConnect1,
        2 => _homeVm.QuickConnect2,
        3 => _homeVm.QuickConnect3,
        4 => _homeVm.QuickConnect4,
        _ => null
    };

    void Controls.ICommandLineHost.SetQuickConnect(int slot, SavedConnection? board)
    {
        if (board == null)
            _homeVm.ClearQuickConnect(slot);
        else
            _homeVm.SetQuickConnect(slot, board);
    }

    void Controls.ICommandLineHost.ExportPhonebook() => _ = ExportFromConsole();

    void Controls.ICommandLineHost.ImportPhonebook() => _ = ImportFromConsole();

    /// <summary>The File menu's own export dialog, with the console focused again afterwards.</summary>
    private async Task ExportFromConsole()
    {
        await ExportPhonebook();
        _commandLine?.FocusInput();
    }

    /// <summary>The File menu's own import dialog, with the console focused again afterwards.</summary>
    private async Task ImportFromConsole()
    {
        await ImportPhonebook();
        _commandLine?.FocusInput();
    }

    IReadOnlyList<string> Controls.ICommandLineHost.BootLines => BuildBootLines();

    int Controls.ICommandLineHost.EmulatedBaud => App.SettingsService.Settings.EmulatedBaud;

    void Controls.ICommandLineHost.SetEmulatedBaud(int bps) => SetEmulatedBaud(bps);

    void Controls.ICommandLineHost.ShowMatrix() => ShowMatrixRain();

    int Controls.ICommandLineHost.TotalCalls => App.SettingsService.Settings.TotalCalls;

    string Controls.ICommandLineHost.Phosphor => App.SettingsService.Settings.Phosphor ?? "Color";

    void Controls.ICommandLineHost.SetPhosphor(string mode) => SetPhosphor(mode);

    void Controls.ICommandLineHost.LoadGui() => HideCommandLine();

    // OnClosing plays the CRT power-off, then closes for real
    void Controls.ICommandLineHost.QuitApp() => Close();

    /// <summary>Adds a full-window effect on top of everything and removes it again when it reports it's finished.</summary>
    private void ShowFxOverlay(Control overlay, Action<EventHandler> subscribeFinished)
    {
        FxOverlayHost.Children.Add(overlay);
        subscribeFinished((_, _) =>
        {
            FxOverlayHost.Children.Remove(overlay);
            // Back to the command line underneath, if the effect was played from there
            if (_commandLine != null && FxOverlayHost.Children.Contains(_commandLine))
                _commandLine.FocusInput();
        });
    }

    private static readonly Key[] KonamiCode =
        [Key.Up, Key.Up, Key.Down, Key.Down, Key.Left, Key.Right, Key.Left, Key.Right, Key.B, Key.A];
    private readonly Queue<Key> _recentKeys = new();

    /// <summary>Up Up Down Down Left Right Left Right B A on the home screen plays the digital rain easter egg.</summary>
    private void TrackKonamiCode(Key key)
    {
        if (_homeView == null || ContentArea.Content != _homeView)
        {
            _recentKeys.Clear();
            return;
        }

        _recentKeys.Enqueue(key);
        while (_recentKeys.Count > KonamiCode.Length)
            _recentKeys.Dequeue();

        if (_recentKeys.Count == KonamiCode.Length && _recentKeys.SequenceEqual(KonamiCode))
        {
            _recentKeys.Clear();
            ShowMatrixRain();
        }
    }

    private void ShowMatrixRain()
    {
        var rain = new Controls.MatrixRainOverlay();
        ShowFxOverlay(rain, handler => rain.Finished += handler);
    }

    /// <summary>
    /// Keys seen before anything in the window gets them (Avalonia's tunnelling KeyDown, the stand-in for WPF's
    /// PreviewKeyDown): a full-window effect on top takes the key, and otherwise the Konami code is tracked. Arrow
    /// keys would never reach a bubbling handler once a tile has focus, hence the tunnel.
    /// </summary>
    private void OnWindowPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        // A Rainwire menu drawn here (1.1.6) has the keyboard while it's up (it holds the focus, so the terminal sends
        // nothing): its keys stop here; any other key is left unhandled so it becomes typed text, which
        // OnWindowPreviewTextInput hands the menu as a hotkey (Avalonia's X11 backend makes TextInput only from a
        // key-down nobody handled). Alt+Enter stays full screen.
        if (_rainwireMenu.IsUp && ContentArea.Content == _terminalView)
        {
            if (e.Key == Key.Enter && (e.KeyModifiers & KeyModifiers.Alt) != 0)
            {
                if (!_altEnterHeld)
                {
                    _altEnterHeld = true;
                    ToggleFullScreen();
                }
                e.Handled = true;
            }
            else if (_rainwireMenu.HandleKey(e.Key, e.KeyModifiers))
            {
                e.Handled = true;
            }
            return;
        }

        // TRACE owns the keyboard while an exclusive module (a game) is up, or any module has text input on. Every key
        // goes to it from here, before any control behind it sees it (Enter would open a board card). Only Alt+Enter
        // full screen stays TERMinator's.
        if (_trace.OwnsKeyboard)
        {
            if (e.Key == Key.Enter && (e.KeyModifiers & KeyModifiers.Alt) != 0)
            {
                // Once per press, not once per auto-repeat: holding Alt+Enter in a game would otherwise flip
                // full screen over and over. Windows uses e.IsRepeat, which Avalonia has no equivalent of.
                if (!_altEnterHeld)
                {
                    _altEnterHeld = true;
                    ToggleFullScreen();
                }
            }
            else
            {
                _trace.HandleKey(e, pressed: true);
            }
            // Avalonia's X11 backend raises TextInput only for a key-down nobody handled, so a key that types a
            // character is left unhandled while a module takes text (MRC's post line, The Wall): its TextInput then
            // reaches OnWindowPreviewTextInput. Only plain/Shift keys -- the terminal canvas sends nothing for those
            // on key-down, so they can't reach the board twice.
            e.Handled = !(_trace.TakesText && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) == 0
                          && e.KeySymbol is { Length: > 0 } sym && sym[0] >= ' ' && sym[0] != '\x7F');
            return;
        }

        // Repeats of the key that just toggled Boss Hide, until it's released
        if (_bossKeyHeld == e.Key)
        {
            e.Handled = true;
            return;
        }

        // Boss Hide: while the spreadsheet is up, any key brings TERMinator back, and that key goes no further, so
        // nothing lands in the session underneath
        if (_bossOverlay != null)
        {
            ToggleBossHide();
            _bossKeyHeld = e.Key;
            _swallowBossText = true;
            e.Handled = true;
            return;
        }

        // Pause/Break or Ctrl+Shift+B puts it up. Neither reaches a board.
        bool bossChord = e.Key == Key.Pause
                         || (e.Key == Key.B && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift));
        if (bossChord)
        {
            ToggleBossHide();
            _bossKeyHeld = e.Key;
            _swallowBossText = true;
            e.Handled = true;
            return;
        }


        // A full-window effect is up. When the boot screen or the rain is on top it takes the key; when the command
        // line is on top its own keys fall through to it (Alt+Enter still works from there), and the Konami
        // code is not tracked.
        if (FxOverlayHost.Children.Count > 0)
        {
            if (FxOverlayHost.Children[^1] is not Controls.CommandLineOverlay)
            {
                foreach (var boot in FxOverlayHost.Children.OfType<Controls.BootSequenceOverlay>().ToList())
                    boot.HandleKey(e.Key);
                foreach (var rain in FxOverlayHost.Children.OfType<Controls.MatrixRainOverlay>().ToList())
                    rain.Dismiss();
                foreach (var saver in FxOverlayHost.Children.OfType<Controls.ScreensaverOverlay>().ToList())
                    saver.Dismiss();
                e.Handled = true;
                return;
            }

            // The console is on top: its own keys fall through to it, but the window's global gesture is
            // claimed here first, the way the Windows client's PreviewKeyDown does. Without this Alt+Enter would
            // be eaten by the console's Enter handler and never reach OnWindowKeyDown.
            if (e.Key == Key.Enter && (e.KeyModifiers & KeyModifiers.Alt) != 0)
            {
                ToggleFullScreen();
                e.Handled = true;
            }
            return;
        }

        // Alt+Enter is full screen everywhere else too. Claimed here on the way down: mid-session the terminal canvas
        // would send it to the board as a plain Enter and mark it handled, so OnWindowKeyDown never saw it.
        if (e.Key == Key.Enter && (e.KeyModifiers & KeyModifiers.Alt) != 0)
        {
            if (!_altEnterHeld)
            {
                _altEnterHeld = true;
                ToggleFullScreen();
            }
            e.Handled = true;
            return;
        }

        // Alt+H hangs up, as in Telix, Qmodem and Procomm. Handled here, after the full-window effects have had the key
        // (as on Windows) and ahead of the menu's access keys (Alt+H would otherwise open Help), and only while
        // connected (when Disconnect is enabled).
        if (e.Key == Key.H && (e.KeyModifiers & KeyModifiers.Alt) != 0 && MenuDisconnect.IsEnabled)
        {
            _terminalVm.DisconnectCommand.Execute(null);
            e.Handled = true;
            return;
        }

        TrackKonamiCode(e.Key);
    }

    // ================================================================
    // Window placement: remember, restore, fit to the home layout
    // ================================================================

    /// <summary>The screen the window is on (by its position), else the primary one.</summary>
    private Screen? CurrentScreen()
    {
        try
        {
            return Screens.ScreenFromPoint(Position)
                ?? Screens.ScreenFromWindow(this)
                ?? Screens.Primary
                ?? Screens.All.FirstOrDefault();
        }
        catch
        {
            return Screens.Primary ?? Screens.All.FirstOrDefault();
        }
    }

    /// <summary>
    /// Applies the saved placement before the window shows. False when nothing usable is saved (or the monitor
    /// it was on is gone), in which case the caller fits the window to the home layout instead.
    /// </summary>
    private bool RestoreSavedBounds(int[]? wb)
    {
        if (wb is not { Length: 4 } || wb[2] <= wb[0] || wb[3] <= wb[1])
            return false;

        var topLeft = new PixelPoint(wb[0], wb[1]);
        Screen? screen = null;
        try { screen = Screens.ScreenFromPoint(topLeft); } catch { /* fall through */ }
        if (screen == null)
            return false;   // that monitor is gone: fall back to the default placement + fit

        double scaling = screen.Scaling > 0 ? screen.Scaling : 1.0;
        double w = (wb[2] - wb[0]) / scaling;
        double h = (wb[3] - wb[1]) / scaling;
        double maxW = screen.WorkingArea.Width / scaling;
        double maxH = screen.WorkingArea.Height / scaling;
        if (w < MinWidth || h < MinHeight)
            return false;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = topLeft;
        Width = Math.Min(w, maxW);
        Height = Math.Min(h, maxH);
        _lastNormalBounds = new NormalBounds(topLeft, Width, Height);
        return true;
    }

    /// <summary>Track the normal bounds while the window is in its normal state.</summary>
    private void RememberNormalBounds()
    {
        if (!IsVisible || _isFullScreen || WindowState != WindowState.Normal) return;
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        _lastNormalBounds = new NormalBounds(Position, ClientSize.Width, ClientSize.Height);
    }

    /// <summary>The window's normal (restored) bounds, still valid while maximized or full screen.</summary>
    private NormalBounds? GetNormalBounds()
    {
        RememberNormalBounds();
        return _lastNormalBounds;
    }

    private void SetNormalBounds(NormalBounds b)
    {
        if (_isFullScreen) return;
        if (WindowState != WindowState.Normal)
            WindowState = WindowState.Normal;
        _adjustingSize = true;
        Position = b.Position;
        Width = b.Width;
        Height = b.Height;
        Dispatcher.UIThread.Post(() => _adjustingSize = false);
    }

    /// <summary>Client-area chrome (the menu bar) around the content area, in DIPs.</summary>
    private (double W, double H) ContentChrome()
    {
        double chromeW = ClientSize.Width - ContentArea.Bounds.Width;
        double chromeH = ClientSize.Height - ContentArea.Bounds.Height;
        if (ContentArea.Bounds.Width <= 0 || double.IsNaN(chromeW) || chromeW < 0) chromeW = 0;
        if (ContentArea.Bounds.Height <= 0 || double.IsNaN(chromeH) || chromeH < 0)
            chromeH = MainMenu.IsVisible && MainMenu.Bounds.Height > 0 ? MainMenu.Bounds.Height : 28;
        return (chromeW, chromeH);
    }

    /// <summary>
    /// Size the window to the home layout, which carries its own margins, instead of an arbitrary saved size.
    /// Runs on first launch (no saved placement yet) and when the side-by-side toggle changes the layout's
    /// footprint. Capped to the work area of the monitor the window is on (the home view scales down if it still
    /// doesn't fit), then handed straight back to normal manual resizing. A maximized window is left alone.
    ///
    /// Two things the Windows client learned the hard way, kept here: the layout is measured AFTER the toggle
    /// settles (HomeView.NaturalLayoutSize forces a measure pass), and at its natural size — the Viewbox is set
    /// shrink-only while measuring, or the "fit" would just grow the window to the whole monitor.
    /// </summary>
    private void FitWindowToHome(bool centerOnScreen)
    {
        if (WindowState != WindowState.Normal || _isFullScreen || _homeView == null || ContentArea.Content != _homeView)
            return;

        var screen = CurrentScreen();
        double scaling = screen?.Scaling > 0 ? screen!.Scaling : (DesktopScaling > 0 ? DesktopScaling : 1.0);
        var work = screen?.WorkingArea ?? new PixelRect(0, 0, (int)(1600 * scaling), (int)(900 * scaling));

        _homeView.SetFitShrinkOnly(true);
        Size natural;
        try
        {
            natural = _homeView.NaturalLayoutSize;
        }
        finally
        {
            _homeView.SetFitShrinkOnly(false);
        }
        if (natural.Width <= 0 || natural.Height <= 0)
            return;

        var (chromeW, chromeH) = ContentChrome();
        var (decorW, decorH) = WindowDecorations();

        double availW = work.Width / scaling - chromeW - decorW;
        double availH = work.Height / scaling - chromeH - decorH;
        double fit = Math.Min(1.0, Math.Min(availW / natural.Width, availH / natural.Height));
        if (fit <= 0 || double.IsNaN(fit) || double.IsInfinity(fit)) fit = 1.0;

        double newW = Math.Max(MinWidth, natural.Width * fit + chromeW);
        double newH = Math.Max(MinHeight, natural.Height * fit + chromeH);

        // Keep the window centred where it was (or centre it on the screen on a first run), inside the work area
        double oldFrameW = (FrameSize?.Width ?? ClientSize.Width) * scaling;
        double oldFrameH = (FrameSize?.Height ?? ClientSize.Height) * scaling;
        double cx = centerOnScreen || !IsVisible ? work.X + work.Width / 2.0 : Position.X + oldFrameW / 2.0;
        double cy = centerOnScreen || !IsVisible ? work.Y + work.Height / 2.0 : Position.Y + oldFrameH / 2.0;
        double newFrameW = (newW + decorW) * scaling;
        double newFrameH = (newH + decorH) * scaling;
        int x = (int)Math.Round(Math.Clamp(cx - newFrameW / 2.0, work.X, Math.Max(work.X, work.Right - newFrameW)));
        int y = (int)Math.Round(Math.Clamp(cy - newFrameH / 2.0, work.Y, Math.Max(work.Y, work.Bottom - newFrameH)));

        _fitting = true;
        _adjustingSize = true;
        Width = newW;
        Height = newH;
        if (IsVisible)
            Position = new PixelPoint(x, y);
        _lastHomeBounds = new Rect(0, 0, newW, newH);
        Dispatcher.UIThread.Post(() =>
        {
            _adjustingSize = false;
            _fitting = false;
            RememberNormalBounds();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Window-manager decorations, which sit outside the client size, in DIPs. FrameSize includes them when the
    /// platform reports it; without it, leave a little room for a title bar.
    /// </summary>
    private (double W, double H) WindowDecorations()
    {
        if (FrameSize is { } frame && frame.Width >= ClientSize.Width && frame.Height >= ClientSize.Height && ClientSize.Width > 0)
            return (frame.Width - ClientSize.Width, frame.Height - ClientSize.Height);
        return (0, 32);
    }

    /// <summary>
    /// Gives restored (saved) bounds the home layout's shape: the width and top-left stay, the height follows the
    /// layout's aspect, and both shrink when that won't fit the monitor's work area. OnBoundsChanged's correction
    /// skips the first bounds event, so without this the window opened at the saved shape and only snapped to the
    /// layout's when the user started resizing. A maximized, full-screen or session window is left alone.
    /// </summary>
    private void ConformWindowToHome()
    {
        if (IsSession || HomeResizeAspect() is not double homeAspect)
            return;

        var screen = CurrentScreen();
        double scaling = screen?.Scaling > 0 ? screen!.Scaling : (DesktopScaling > 0 ? DesktopScaling : 1.0);
        var work = screen?.WorkingArea ?? new PixelRect(0, 0, (int)(1600 * scaling), (int)(900 * scaling));
        var (chromeW, chromeH) = ContentChrome();
        var (decorW, decorH) = WindowDecorations();

        double homeW = Math.Max(1, ClientSize.Width - chromeW);
        double homeH = Math.Round(homeW / homeAspect);
        double maxHomeH = work.Height / scaling - chromeH - decorH;
        if (maxHomeH > 0 && homeH > maxHomeH)
        {
            homeH = Math.Floor(maxHomeH);
            homeW = Math.Round(homeH * homeAspect);
        }
        double newW = Math.Max(MinWidth, homeW + chromeW);
        double newH = Math.Max(MinHeight, homeH + chromeH);
        if (Math.Abs(newW - ClientSize.Width) < 1 && Math.Abs(newH - ClientSize.Height) < 1)
            return;

        // Top-left stays, pulled back inside the work area if the new size would hang off it
        double newFrameW = (newW + decorW) * scaling;
        double newFrameH = (newH + decorH) * scaling;
        int x = (int)Math.Round(Math.Clamp(Position.X, work.X, Math.Max(work.X, work.Right - newFrameW)));
        int y = (int)Math.Round(Math.Clamp(Position.Y, work.Y, Math.Max(work.Y, work.Bottom - newFrameH)));

        _adjustingSize = true;
        Width = newW;
        Height = newH;
        if (x != Position.X || y != Position.Y)
            Position = new PixelPoint(x, y);
        _lastHomeBounds = new Rect(0, 0, newW, newH);
        Dispatcher.UIThread.Post(() =>
        {
            _adjustingSize = false;
            RememberNormalBounds();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>The home layout's aspect ratio when a drag-resize should keep it; null when it shouldn't apply.</summary>
    private double? HomeResizeAspect()
    {
        if (_homeView == null || ContentArea.Content != _homeView || _isFullScreen || WindowState != WindowState.Normal)
            return null;

        double aspect = _homeView.LayoutAspectRatio;
        return aspect > 0 ? aspect : null;
    }

    // ================================================================
    // Navigation
    // ================================================================

    private void NavigateToHome()
    {
        // A session window has no home screen: where the main window would go home, it closes
        if (IsSession)
        {
            CloseSession();
            return;
        }

        _activeConnection = null;
        _aspectRatioLocked = false;
        _lastTerminalBounds = default;

        // Returning from a terminal session: put the window back exactly where it was and in the shape it had,
        // on the same monitor, instead of restoring only the size wherever the terminal left it
        if (_preConnectBounds is NormalBounds preConnect)
        {
            SetNormalBounds(preConnect);
            _preConnectBounds = null;
        }

        _homeVm.LoadQuickConnects();
        _homeView ??= new HomeView { DataContext = _homeVm };
        _homeView.SetSideBySide(App.SettingsService.Settings.HomeSideBySide);
        ApplyCrtToHome();
        ApplyPhosphor();
        ContentArea.Content = _homeView;
        _lastHomeBounds = default;
        SetAppTitle();
        UpdateMenuState(false);

        // A session started from the command line returns there when it ends (or fails, or is cancelled)
        if (_returnToCommandLine)
        {
            _returnToCommandLine = false;
            string board = _commandLineSessionBoard ?? "the board";
            ShowCommandLine(_commandLineSessionConnected ? $"Disconnected from {board}" : $"Couldn't connect to {board}");
        }
    }

    private void NavigateToPhonebook()
    {
        _phonebookVm.Refresh();
        _phonebookView ??= new PhonebookView { DataContext = _phonebookVm };
        ContentArea.Content = _phonebookView;
        ApplyPhosphor(); // drops an unsaved preview if Preferences was left without Save or Cancel
        SetAppTitle();
        UpdateMenuState(false);
    }

    private void NavigateToTerminal()
    {
        if (_terminalView == null)
        {
            _terminalView = new TerminalView(_terminalVm);
            _terminalView.TerminalDimensionsChanged += OnTerminalDimensionsChanged;
            _terminalView.RainwireTrayHost.Content = _rainwireMenu.Tray;   // a Rainwire board's tray gadgets (RainwireGadgets.cs)
            _terminalView.TerminalCanvas.OscLinkActivated += OpenBoardLink;
            // TRACE pictures belong to the live screen: hide them while scrolled back (UpdatePlacement)
            _terminalView.TerminalCanvas.ScrollbackChanged += _ => _trace.UpdatePlacement();
            _terminalView.TerminalCanvas.SizeChanged += (_, _) => FitRainwireMenu();
            AttachTerminalContextMenu();
        }
        ContentArea.Content = _terminalView;
        ApplyPhosphor(); // drops an unsaved preview if Preferences was left without Save or Cancel
        SetAppTitle();
        UpdateMenuState(true);

        // Focus the terminal canvas for keyboard input
        _terminalView.FocusTerminal();
    }

    private void NavigateToSettings()
    {
        _settingsView ??= new SettingsView { DataContext = _settingsVm };
        _settingsVm.Reload(); // the menus or the command line may have changed a setting since it last read them
        _settingsVm.RefreshCacheSize();
        ContentArea.Content = _settingsView;
        SetAppTitle();
        UpdateMenuState(false);
    }

    private void NavigateAfterSettings()
    {
        if (App.ConnectionManager.State == ConnectionState.Connected)
            NavigateToTerminal();
        else
            NavigateToHome();
    }

    private void UpdateMenuState(bool connected)
    {
        // A board's menu drawn here (1.1.6) shows only over the terminal, not over Settings or the phonebook
        RainwireMenuHost.IsVisible = connected;
        MenuDisconnect.IsEnabled = connected;
        MenuCopy.IsEnabled = connected;
        MenuPaste.IsEnabled = connected;
        MenuSelectAll.IsEnabled = connected;
        MenuSendFile.IsEnabled = connected;
        MenuReceiveFile.IsEnabled = connected;
        MenuSendXY.IsEnabled = connected;
        MenuReceiveXY.IsEnabled = connected;
        MenuLogSession.IsEnabled = connected;
        MenuSnapshot.IsEnabled = connected;
        MenuSaveScreenshot.IsEnabled = connected;
        MenuScrollback.IsEnabled = connected;

        // Send Login: only enabled when connected AND connection has saved credentials
        bool hasCredentials = connected && _activeConnection != null
            && !string.IsNullOrEmpty(_activeConnection.Username)
            && !string.IsNullOrEmpty(App.CredentialService.GetPassword(_activeConnection.Id));
        MenuSendLogin.IsEnabled = hasCredentials;
    }

    // ================================================================
    // Connection handling
    // ================================================================

    /// <summary>What ssh.c logs (BLANK_LOGIN_NEEDS_CREDENTIALS) when a blank SSH login is refused.</summary>
    private const string BlankLoginNeedsCredentials =
        "This board wants an SSH username/password: tick Save credentials and enter them";

    private async void OnConnectRequested(SavedConnection connection)
    {
        // Each board opens in a window of its own, unless that's switched off
        if (LaunchesSessions)
        {
            LaunchSession(connection);
            return;
        }

        _activeConnection = connection;
        string? password = null;

        // Use the saved password first (if the entry has one)
        if (connection.SaveCredentials)
            password = App.CredentialService.GetPassword(connection.Id);

        // SSH with saved credentials but no password saved yet: ask once (PromptForPassword saves it). With "Save
        // credentials" off there is no dialog at all: the call logs in blank and the board shows its own login
        // screen (SavedConnection.UsesBlankSshLogin).
        if (connection.Protocol == ConnectionProtocol.SSH && connection.SaveCredentials && string.IsNullOrEmpty(password))
        {
            password = await PromptForPassword(connection);
            if (password == null)
            {
                // Cancelled before the terminal was ever shown, so nothing navigates home: a call placed from the
                // command line comes straight back to it
                if (_returnToCommandLine)
                {
                    _returnToCommandLine = false;
                    ShowCommandLine("connection cancelled");
                }
                return;
            }
        }

        // Save the pre-connection position and shape so disconnecting puts the window back exactly
        _preConnectBounds = GetNormalBounds();

        NavigateToTerminal();

        // Clear stale display from any previous session
        _terminalView!.TerminalCanvas.ResetDisplay();

        // Show connecting overlay while the TCP connect runs
        _terminalView.ShowConnecting(connection.Name);
        var connectLogShown = Stopwatch.StartNew();

        // If a previous connection was aborted (timeout/cancel), the native
        // syncterm_connect() was interrupted by closing its socket. Wait for it
        // to actually return before starting a new connection.
        // The overlay is already showing so the user just sees "Connecting to X...".
        if (_abortedConnectTask != null)
        {
            Console.Error.WriteLine("[MainWindow] Waiting for previous connect to finish...");
            _terminalView.AppendConnectLog("waiting for the previous attempt to release the line...",
                                           ConnectionTracer.LineKind.Info);
            // The native TCP connect can't be interrupted — wait for it to time out.
            await _abortedConnectTask;
            Console.Error.WriteLine("[MainWindow] Previous connect finished, proceeding");
            _abortedConnectTask = null;
        }

        _connectCancelled = false;
        _connectCts?.Dispose();
        _connectCts = new CancellationTokenSource();
        _terminalView.CancelConnectRequested += OnCancelConnect;

        // Run connection on background thread, but race against the user's connection timeout
        // (Preferences > TERMINAL > Connection Timeout, default 10s, read at connect time)
        // and the cancel button. syncterm_connect() is a blocking native call
        // that can hang indefinitely — we can't cancel it, but we can stop waiting.
        // Narrate the attempt in the connecting overlay (resolve / tcp / handshake), so a stalled board shows where
        // it got stuck instead of a bare "Connecting...".
        var tracer = new ConnectionTracer(connection,
            (text, kind) => _terminalView?.AppendConnectLog(text, kind),
            status => _terminalView?.SetConnectStatus(status));
        tracer.Start();

        Console.Error.WriteLine($"[MainWindow] Starting ConnectTo for {connection.Host}:{connection.Port}");
        var connectTask = Task.Run(() => _terminalVm.ConnectTo(connection, password));
        int connectTimeoutSec = Models.AppSettings.ValidConnectionTimeout(App.SettingsService.Settings.ConnectionTimeoutSeconds);
        var timeoutTask = Task.Delay(connectTimeoutSec * 1000, _connectCts.Token);
        var winner = await Task.WhenAny(connectTask, timeoutTask);

        _terminalView.CancelConnectRequested -= OnCancelConnect;

        // If the user cancelled or we timed out, interrupt the hung native
        // connect so it returns quickly, then stash the task for cleanup later.
        if (_connectCancelled || (winner == timeoutTask && !connectTask.IsCompleted))
        {
            Console.Error.WriteLine($"[MainWindow] Connection {(_connectCancelled ? "cancelled" : "timed out")}, interrupting...");

            // Tell Connect() to bail out when syncterm_connect() returns
            _terminalVm.ConnectionManager.ConnectAborted = true;

            // Interrupt the blocking conn_connect() by calling syncterm_disconnect()
            // which closes the socket. Now safe — g_lock is not held during conn_connect().
            Task.Run(() =>
            {
                Console.Error.WriteLine("[MainWindow] Calling syncterm_disconnect...");
                try { NativeBridge.syncterm_disconnect(); }
                catch (Exception ex) { Console.Error.WriteLine($"[MainWindow] disconnect failed: {ex.Message}"); }
                Console.Error.WriteLine("[MainWindow] syncterm_disconnect done");
            });

            // Stash the task — it will be awaited before the next connect
            _abortedConnectTask = connectTask;

            if (_connectCancelled)
            {
                tracer.Complete(success: false, timedOut: false, cancelled: true);
                return;
            }

            // Timed out — show failure dialog below
        }

        bool success;
        if (winner == timeoutTask)
        {
            success = false;
        }
        else
        {
            success = await connectTask;
        }
        tracer.Complete(success, timedOut: !success && winner == timeoutTask, cancelled: _connectCancelled);

        // Retrieve SSH debug log for encrypted protocols
        string? sshLog = null;
        if (connection.Protocol == ConnectionProtocol.SSH ||
            connection.Protocol == ConnectionProtocol.TelnetS)
        {
            sshLog = ConnectionManager.GetSshDebugLog();
        }

        if (!success)
        {
            // If user already cancelled, we've already navigated home -- don't do it again
            if (_connectCancelled) return;

            Console.Error.WriteLine("[MainWindow] Showing connection failed message");

            // The connection log stays up behind the failure dialog (it shows where the attempt stalled) and is
            // hidden once the dialog closes

            // A blank SSH login the board turned down (it wants a real SSH username/password): say what to do
            // rather than show the SSH log. The native side logs BlankLoginNeedsCredentials (ssh.c) in that case.
            if (connection.UsesBlankSshLogin && sshLog != null
                && sshLog.Contains(BlankLoginNeedsCredentials, StringComparison.Ordinal))
            {
                await ShowMessageBox("TERMinator", $"{connection.Name}: {BlankLoginNeedsCredentials} in the phonebook entry.");
            }
            // Show SSH debug window for encrypted protocol failures
            else if (connection.Protocol == ConnectionProtocol.SSH ||
                     connection.Protocol == ConnectionProtocol.TelnetS)
            {
                var logText = !string.IsNullOrEmpty(sshLog)
                    ? sshLog
                    : $"Connection to {connection.Host}:{connection.Port} failed.\n" +
                      $"Protocol: {connection.Protocol}\n" +
                      $"Username: {connection.Username}\n\n" +
                      "(No SSH debug log was captured — the connection may have failed before SSH negotiation.)";
                var debugWindow = new SshDebugWindow(logText);
                await debugWindow.ShowDialog(this);
            }
            else
            {
                bool timedOut = winner == timeoutTask;
                await ShowConnectionFailedDialog(connection.Name, timedOut, connectTimeoutSec);
            }
            _terminalView.HideConnecting();
            NavigateToHome();
        }
        else
        {
            Console.Error.WriteLine("[MainWindow] Connection succeeded, showing terminal");
            _commandLineSessionConnected = true;

            // A linked window (12.73) is part of its parent's call, not a call of its own: no call count, last call,
            // recent list or call log
            if (!connection.Linked)
            {
                // Every successful call counts toward the elite rank. Added to the count on disk, since other BBS
                // windows count their calls too.
                App.SettingsService.Update(s => s.TotalCalls++);
                // The phonebook's "last call" (TerminalViewModel set it on this window's copy of the entry)
                App.PhonebookService.UpdateEntry(connection.Id, c => c.LastConnected = connection.LastConnected);

                // Per-board call log; time online and data are added when the call ends
                App.CallStatsService.BeginCall(connection);
            }

            // Fast connections would flash the connection log past unread: hold it until it has been up for a
            // moment and has finished typing out (ending on CONNECT), capped so a long log can't stall the session.
            // The session is already live, so the board's output keeps drawing behind the overlay.
            var holds = new List<Task> { _terminalView.WhenConnectLogTypedAsync(MaxConnectLogTypingWait) };
            var holdLog = MinConnectLogDisplay - connectLogShown.Elapsed;
            if (holdLog > TimeSpan.Zero)
                holds.Add(Task.Delay(holdLog));
            await Task.WhenAll(holds);

            // The board may have hung up, or the user left, during the hold
            if (ContentArea.Content != _terminalView || !_terminalVm.IsConnected)
                return;

            _terminalView.ShowConnected(connection);

            // PETSCII keyboard translation for the Commodore modes, and the display aspect the
            // mode's picture had on real hardware — 4:3 for the ANSI modes, 6:5 for C64/C128 40-col.
            _terminalView.TerminalCanvas.PetsciiMode =
                ConnectionManager.IsCommodoreMode(connection.ScreenMode);
            _terminalView.TerminalCanvas.DisplayAspect =
                ConnectionManager.GetDisplayAspect(connection.ScreenMode);

            // The CRT filter is a display preference, not a per-connection one, so it is
            // re-applied to every new session rather than being remembered by the canvas.
            ApplyCrtToTerminal();

            // Load font data (cached during Connect, safe to call here)
            var fontData = App.ConnectionManager.CachedFontBitmap;
            if (fontData != null)
                _terminalView.SetFontData(fontData);

            _terminalView.TerminalCanvas.SetFontSmoothing(
                App.SettingsService.Settings.FontSmoothing);

            // Adjust window for terminal dimensions
            var (connectCols, connectRows) = ConnectionManager.GetScreenModeDimensions(connection.ScreenMode);
            _ = Dispatcher.UIThread.InvokeAsync(() =>
            {
                AdjustWindowForTerminal(connectCols, connectRows);
            });

            _terminalView.FocusTerminal();
        }
    }

    /// <summary>Minimum time the connection log stays up on a successful connect, so it can be read.</summary>
    private static readonly TimeSpan MinConnectLogDisplay = TimeSpan.FromSeconds(1.2);

    /// <summary>Longest a successful connect waits for the connection log to finish typing out.</summary>
    private static readonly TimeSpan MaxConnectLogTypingWait = TimeSpan.FromSeconds(2.5);

    private bool _connectCancelled;
    private CancellationTokenSource? _connectCts;
    private Task? _abortedConnectTask;

    private void OnCancelConnect()
    {
        _connectCancelled = true;
        _connectCts?.Cancel();
        _terminalView?.HideConnecting();
        NavigateToHome();
    }

    private async Task<string?> PromptForPassword(SavedConnection connection)
    {
        bool needUsername = string.IsNullOrEmpty(connection.Username);

        var dialog = new Window
        {
            Title = $"Password for {connection.Host}",
            Width = 400,
            Height = needUsername ? 250 : 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = new SolidColorBrush(Color.Parse("#FF0D0D1A")),
            Foreground = new SolidColorBrush(Color.Parse("#FFCCCCCC"))
        };

        var panel = new StackPanel
        {
            Margin = new Thickness(20)
        };

        // Username field (only shown when the entry has no username saved)
        TextBox? usernameBox = null;
        if (needUsername)
        {
            var usernameLabel = new TextBlock
            {
                Text = "Username:",
                Margin = new Thickness(0, 0, 0, 8)
            };
            usernameBox = new TextBox { Margin = new Thickness(0, 0, 0, 12) };
            panel.Children.Add(usernameLabel);
            panel.Children.Add(usernameBox);
        }

        var label = new TextBlock
        {
            Text = needUsername
                ? "Password:"
                : $"Password for {connection.Username}@{connection.Host}:",
            Margin = new Thickness(0, 0, 0, 8)
        };

        var passwordBox = new TextBox
        {
            PasswordChar = '\u2022',
            Margin = new Thickness(0, 0, 0, 16)
        };

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        string? result = null;

        var okButton = new Button
        {
            Content = "Connect",
            Width = 90,
            Margin = new Thickness(0, 0, 8, 0)
        };
        okButton.Click += (_, _) =>
        {
            if (usernameBox != null && !string.IsNullOrWhiteSpace(usernameBox.Text))
                connection.Username = usernameBox.Text.Trim();
            result = passwordBox.Text;
            dialog.Close(true);
        };

        var cancelButton = new Button
        {
            Content = "Cancel",
            Width = 90
        };
        cancelButton.Click += (_, _) => dialog.Close(false);

        buttonPanel.Children.Add(okButton);
        buttonPanel.Children.Add(cancelButton);

        panel.Children.Add(label);
        panel.Children.Add(passwordBox);
        panel.Children.Add(buttonPanel);

        dialog.Content = panel;

        // Handle Enter key to confirm
        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                if (usernameBox != null && !string.IsNullOrWhiteSpace(usernameBox.Text))
                    connection.Username = usernameBox.Text.Trim();
                result = passwordBox.Text;
                dialog.Close(true);
            }
            else if (e.Key == Key.Escape)
            {
                dialog.Close(false);
            }
        };

        var dialogResult = await dialog.ShowDialog<object?>(this);

        if (dialogResult is true)
        {
            // Save credentials if requested
            if (connection.SaveCredentials && result != null)
                App.CredentialService.SavePassword(connection.Id, result);
            // Persist a username typed at the prompt so it sticks for next time
            if (usernameBox != null && !string.IsNullOrEmpty(connection.Username))
                App.PhonebookService.Update(connection);
            return result;
        }

        return null;
    }

    /// <summary>Opens a board's OSC 8 link straight away (sysop's call; the hover tooltip shows the real address) (12.62).</summary>
    private void OpenBoardLink(string url)
    {
        if (!Services.OscExtras.IsAllowedUri(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { /* no handler for the scheme (telnet/ssh) or the launch failed */ }
        _terminalView?.FocusTerminal();
    }

    private async Task<bool> OnConfirmDelete(string name)
    {
        var dialog = new Window
        {
            Title = "Confirm Delete",
            Width = 360,
            Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = new SolidColorBrush(Color.Parse("#FF0D0D1A")),
            Foreground = new SolidColorBrush(Color.Parse("#FFCCCCCC"))
        };

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = $"Delete connection \"{name}\"?",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        });

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        bool confirmed = false;
        var yesBtn = new Button { Content = "DELETE", Width = 90, Margin = new Thickness(0, 0, 8, 0) };
        var noBtn = new Button { Content = "Cancel", Width = 90 };
        yesBtn.Click += (_, _) => { confirmed = true; dialog.Close(); };
        noBtn.Click += (_, _) => { dialog.Close(); };
        buttonPanel.Children.Add(yesBtn);
        buttonPanel.Children.Add(noBtn);
        panel.Children.Add(buttonPanel);
        dialog.Content = panel;

        await dialog.ShowDialog(this);
        return confirmed;
    }

    private void OnTerminalDimensionsChanged(int cols, int rows)
    {
        // Defer to after layout so canvas has been arranged
        Dispatcher.UIThread.InvokeAsync(() => AdjustWindowForTerminal(cols, rows));
    }

    private void AdjustWindowForTerminal(int cols, int rows)
    {
        if (_terminalView == null)
            return;

        int fontH = _terminalView.TerminalCanvas.FontDataHeight;
        double bitmapW = cols * 8.0;
        double bitmapH = rows * fontH;
        // The forced display aspect (4:3 for the ANSI modes, 6:5 for the 40-column Commodore
        // ones) sizes the window by that aspect rather than by the raw bitmap, so the picture
        // keeps the shape it had on the hardware instead of the shape of its pixel grid.
        if (_terminalView.TerminalCanvas.DisplayAspect is double forcedAspect && forcedAspect > 0)
            bitmapH = bitmapW / forcedAspect;

        // Calculate chrome (non-content area) by measuring the difference
        // between window size and content area
        double chromeW = Width - ContentArea.Bounds.Width;
        double chromeH = Height - ContentArea.Bounds.Height;
        // Use sensible defaults if layout hasn't happened yet
        if (chromeW <= 0 || double.IsNaN(chromeW)) chromeW = 16;
        if (chromeH <= 0 || double.IsNaN(chromeH)) chromeH = 60;

        // Account for status bar height inside the content area
        double statusBarH = _terminalView.StatusBarActualHeight;
        if (statusBarH <= 0) statusBarH = 28;

        // Store the status bar height and aspect ratio for resize enforcement
        _statusBarHeight = statusBarH;
        _terminalAspectRatio = bitmapW / bitmapH;
        _aspectRatioLocked = true;

        // Size window to fit the terminal bitmap at the current width
        double contentW = Width - chromeW;
        double scale = contentW / bitmapW;
        double contentH = Math.Ceiling(bitmapH * scale) + statusBarH;

        // Clamp to screen work area
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        double maxH = screen?.WorkingArea.Height / (screen?.Scaling ?? 1.0) ?? 900;
        double maxW = screen?.WorkingArea.Width / (screen?.Scaling ?? 1.0) ?? 1600;

        double newWindowW = Width;
        double newWindowH = contentH + chromeH;

        // If the computed height exceeds the work area, shrink width to fit
        if (newWindowH > maxH)
        {
            newWindowH = maxH;
            contentH = newWindowH - chromeH;
            double termContentH = contentH - statusBarH;
            scale = termContentH / bitmapH;
            contentW = bitmapW * scale;
            newWindowW = contentW + chromeW;
        }

        newWindowH = Math.Max(newWindowH, MinHeight);
        newWindowW = Math.Max(newWindowW, MinWidth);

        _adjustingSize = true;
        Width = newWindowW;
        Height = newWindowH;
        // The fit's own resize is swallowed by _adjustingSize, so seed the baseline the first drag compares against
        _lastTerminalBounds = new Rect(0, 0, newWindowW, newWindowH);

        // Keep window on screen
        if (Position.Y + newWindowH > maxH)
            Position = new PixelPoint(Position.X, Math.Max(0, (int)(maxH - newWindowH)));
        if (Position.X + newWindowW > maxW)
            Position = new PixelPoint(Math.Max(0, (int)(maxW - newWindowW)), Position.Y);

        Dispatcher.UIThread.Post(() => _adjustingSize = false);
    }

    private void OnBoundsChanged(Rect bounds)
    {
        RememberNormalBounds();

        if (_adjustingSize || _fitting)
            return;

        // Home screen: keep the content area at the home layout's shape while the window is resized, so the
        // scaling Viewbox fills it with no empty bands at the sides. Avalonia has no WM_SIZING-style hook on
        // X11, so this is the closest thing: the size the window manager granted is corrected straight after —
        // width drives height when the width changed (sides, corners), height drives width when only the
        // height changed (top / bottom edges). Maximized and full-screen windows simply centre the layout.
        if (!_aspectRatioLocked && HomeResizeAspect() is double homeAspect && IsVisible
            && WindowState == WindowState.Normal && !_isFullScreen)
        {
            var prev = _lastHomeBounds;
            _lastHomeBounds = bounds;
            if (prev.Width <= 0 || prev.Height <= 0)
                return;

            var (chromeW, chromeH) = ContentChrome();
            double homeW = Math.Max(1, bounds.Width - chromeW);
            double homeH = Math.Max(1, bounds.Height - chromeH);
            if (Math.Abs(homeW / homeH - homeAspect) < 0.01)
                return;

            bool widthChanged = Math.Abs(bounds.Width - prev.Width) > 0.5;
            bool heightChanged = Math.Abs(bounds.Height - prev.Height) > 0.5;
            if (!widthChanged && !heightChanged)
                return;

            _adjustingSize = true;
            if (heightChanged && !widthChanged)
                Width = Math.Max(MinWidth, Math.Round(homeH * homeAspect + chromeW));
            else
                Height = Math.Max(MinHeight, Math.Round(homeW / homeAspect + chromeH));
            _lastHomeBounds = new Rect(0, 0, Width, Height);
            Dispatcher.UIThread.Post(() => _adjustingSize = false);
            return;
        }

        if (!_aspectRatioLocked || _terminalAspectRatio <= 0)
            return;

        // Don't fight the window manager when maximized or fullscreen. Forget the baseline too, so coming back
        // from maximized doesn't read as a drag of the whole window.
        if (WindowState == WindowState.Maximized || WindowState == WindowState.FullScreen
            || _isFullScreen)
        {
            _lastTerminalBounds = default;
            return;
        }

        // Terminal: keep the picture at its display aspect while the window is resized -- the Windows client does
        // this per edge in WM_SIZING. Same after-the-fact correction as the home screen above: the edge that moved
        // drives. Width changed (sides, corners) -> derive the height; only the height changed (top / bottom edges)
        // -> derive the width. Width-only used to be the whole rule, so a top/bottom drag recomputed the height from
        // the unchanged width and snapped straight back, which read as a window that won't resize.
        var prevTerm = _lastTerminalBounds;
        _lastTerminalBounds = bounds;
        if (prevTerm.Width <= 0 || prevTerm.Height <= 0)
            return;

        // Chrome measured live from the client size: a value stored at connect time goes stale (it was taken from
        // the Width property, which isn't always the size the window manager actually granted)
        var (termChromeW, termChromeH) = ContentChrome();
        double contentW = Math.Max(1, bounds.Width - termChromeW);
        double termH = bounds.Height - termChromeH - _statusBarHeight;
        if (termH <= 0) return;
        if (Math.Abs(contentW / termH - _terminalAspectRatio) < 0.01) return;

        bool termWidthChanged = Math.Abs(bounds.Width - prevTerm.Width) > 0.5;
        bool termHeightChanged = Math.Abs(bounds.Height - prevTerm.Height) > 0.5;
        if (!termWidthChanged && !termHeightChanged)
            return;

        // _adjustingSize stays true until the deferred callback clears it,
        // so we don't re-enter when Avalonia processes the size change.
        _adjustingSize = true;
        if (termHeightChanged && !termWidthChanged)
            Width = Math.Max(MinWidth, Math.Round(termH * _terminalAspectRatio + termChromeW));
        else
            Height = Math.Max(MinHeight, Math.Round(contentW / _terminalAspectRatio + _statusBarHeight + termChromeH));
        _lastTerminalBounds = new Rect(0, 0, Width, Height);
        Dispatcher.UIThread.Post(() => _adjustingSize = false);
    }

    // ================================================================
    // ZMODEM Transfer handling
    // ================================================================

    private void OnZmodemDetected(int direction)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            // A Rainwire file drop's transfer runs whatever the auto-detect setting, and with no picker
            if (_fileDrops.HandleZmodem(direction))
                return;
            if (!App.SettingsService.Settings.AutoDetectZmodem)
            {
                NativeBridge.syncterm_clear_zmodem_detected();
                return;
            }

            if (direction == -100)
            {
                // Download detected
                ShowReceiveTransfer();
            }
            else if (direction == -101)
            {
                // Upload detected
                ShowSendFileDialog();
            }
        });
    }

    private async void ShowReceiveTransfer()
    {
        App.ConnectionManager.SetTransferInProgress(true);
        var dialog = new TransferDialog(App.ConnectionManager);
        dialog.StartReceive();
        _trace.TransferStarted();   // a module Rainwire closed for this stays on screen, dimmed (Close;hold=1)
        try
        {
            await dialog.ShowDialog(this);
        }
        finally
        {
            _trace.TransferEnded();
        }
        _rainwireDesk.RefocusAfterDialog();
        NativeBridge.syncterm_clear_zmodem_detected();
    }

    private async void ShowSendFileDialog()
    {
        _trace.TransferStarted();
        try
        {
            await ShowSendFileDialogCore();
        }
        finally
        {
            _trace.TransferEnded();
        }
    }

    private async Task ShowSendFileDialogCore()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select file(s) to send",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("All Files") { Patterns = new[] { "*.*" } }
            }
        });

        if (files.Count > 0)
        {
            var filePaths = files.Select(f => f.Path.LocalPath).ToArray();
            App.ConnectionManager.SetTransferInProgress(true);
            var dialog = new TransferDialog(App.ConnectionManager);
            dialog.StartSend(filePaths);
            await dialog.ShowDialog(this);
            _rainwireDesk.RefocusAfterDialog();
            NativeBridge.syncterm_clear_zmodem_detected();
        }
        else
        {
            // User cancelled file picker -- clear the ZMODEM detected flag
            // so the auto-detect can fire again for the next transfer
            NativeBridge.syncterm_clear_zmodem_detected();
        }
    }

    // ================================================================
    // Rainwire file drops (RainwireWm/RainwireFileDrops.cs)
    // ================================================================

    /// <summary>FileGo: the board's ZRINIT came; send exactly these files, no picker, the dialog for progress.</summary>
    private async void RunDropSend(string[] paths)
    {
        App.ConnectionManager.SetTransferInProgress(true);
        var dialog = new TransferDialog(App.ConnectionManager);
        dialog.StartSend(paths);
        _trace.TransferStarted();
        try
        {
            await dialog.ShowDialog(this);
        }
        finally
        {
            _trace.TransferEnded();
        }
        _rainwireDesk.RefocusAfterDialog();
        NativeBridge.syncterm_clear_zmodem_detected();
    }

    // ================================================================
    // X/YMODEM Transfer handling
    //
    // Nothing here is auto-detected. ZMODEM announces itself with "**\x18B00"
    // and we pop the dialog off the back of that; an X/YMODEM sender just starts
    // NAKing into the void, so the user tells the BBS which protocol to use and
    // then picks the same one from this menu. Order matters: start it on the
    // BBS first, then here — the protocol retries the handshake for a while, so
    // a few seconds of slack either way is fine.
    // ================================================================

    private async void ShowXmodemSendDialog(int mode, string protocolName)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Select a file to send via {protocolName}",
            AllowMultiple = false,   // X/YMODEM batch is not supported — see StartXmodemSend
            FileTypeFilter = new[]
            {
                new FilePickerFileType("All Files") { Patterns = new[] { "*" } }
            }
        });

        if (files.Count == 0)
            return;

        App.ConnectionManager.SetTransferInProgress(true);
        var dialog = new TransferDialog(App.ConnectionManager)
        {
            Title = $"{protocolName} Upload"
        };

        dialog.StartXmodemSend(files[0].Path.LocalPath, mode);
        await dialog.ShowDialog(this);
        _rainwireDesk.RefocusAfterDialog();
    }

    private async void ShowXmodemReceive(int mode, string protocolName)
    {
        string? savePath = null;

        // XMODEM carries no filename, so the only way to know where this lands
        // is to ask. YMODEM sends the name in its header block and goes straight
        // to the download directory.
        if ((mode & NativeBridge.XferMode.Xmodem) != 0)
        {
            var downloadDir = App.SettingsService.Settings.DownloadDirectory;
            if (string.IsNullOrWhiteSpace(downloadDir))
                downloadDir = AppSettings.GetDefaultDownloadDir();

            try { System.IO.Directory.CreateDirectory(downloadDir); } catch { /* the save dialog will complain */ }

            IStorageFolder? startIn = null;
            try { startIn = await StorageProvider.TryGetFolderFromPathAsync(downloadDir); } catch { /* fall back to the picker default */ }

            var saveFile = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save the incoming XMODEM file as",
                SuggestedFileName = "download.bin",
                SuggestedStartLocation = startIn,
                ShowOverwritePrompt = true,
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("All Files") { Patterns = new[] { "*" } }
                }
            });

            if (saveFile == null)
                return;

            savePath = saveFile.Path.LocalPath;

            // The Save dialog has already asked about an existing file and the
            // user said yes, so tell the native side to honour that. Without
            // OVERWRITE, xmodem_recv_files() side-steps the collision and writes
            // "download (1).bin" instead — which looks exactly like a transfer
            // that silently did nothing, because the file the user was watching
            // never changes.
            mode |= NativeBridge.XferMode.Overwrite;
        }

        App.ConnectionManager.SetTransferInProgress(true);
        var dialog = new TransferDialog(App.ConnectionManager)
        {
            Title = $"{protocolName} Download"
        };

        dialog.StartXmodemReceive(savePath, mode);
        await dialog.ShowDialog(this);
        _rainwireDesk.RefocusAfterDialog();
    }

    // ================================================================
    // Edit Connection Dialog
    // ================================================================

    private async void OnEditConnection(SavedConnection connection)
    {
        var dialog = new Window
        {
            Title = "Edit Connection",
            Width = 440,
            Height = 545,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = new SolidColorBrush(Color.Parse("#FF0D0D1A")),
            Foreground = new SolidColorBrush(Color.Parse("#FFCCCCCC"))
        };

        var grid = new Grid { Margin = new Thickness(20) };
        grid.ColumnDefinitions.Add(new ColumnDefinition(110, GridUnitType.Pixel));
        grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));

        int row = 0;
        void AddRow() { grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); }

        // Name
        AddRow();
        var nameLabel = new TextBlock { Text = "Name:", VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(nameLabel, row); Grid.SetColumn(nameLabel, 0);
        var nameBox = new TextBox { Text = connection.Name, Margin = new Thickness(0, 2, 0, 6) };
        Grid.SetRow(nameBox, row); Grid.SetColumn(nameBox, 1);
        grid.Children.Add(nameLabel); grid.Children.Add(nameBox); row++;

        // Host
        AddRow();
        var hostLabel = new TextBlock { Text = "Host:", VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(hostLabel, row); Grid.SetColumn(hostLabel, 0);
        var hostBox = new TextBox { Text = connection.Host, Margin = new Thickness(0, 2, 0, 6) };
        Grid.SetRow(hostBox, row); Grid.SetColumn(hostBox, 1);
        grid.Children.Add(hostLabel); grid.Children.Add(hostBox); row++;

        // Port
        AddRow();
        var portLabel = new TextBlock { Text = "Port:", VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(portLabel, row); Grid.SetColumn(portLabel, 0);
        var portBox = new TextBox { Text = connection.Port.ToString(), Margin = new Thickness(0, 2, 0, 6) };
        Grid.SetRow(portBox, row); Grid.SetColumn(portBox, 1);
        grid.Children.Add(portLabel); grid.Children.Add(portBox); row++;

        // Protocol
        AddRow();
        var protoLabel = new TextBlock { Text = "Protocol:", VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(protoLabel, row); Grid.SetColumn(protoLabel, 0);
        var protoCombo = new ComboBox
        {
            ItemsSource = Enum.GetValues<ConnectionProtocol>(),
            SelectedItem = connection.Protocol,
            Margin = new Thickness(0, 2, 0, 6)
        };
        Grid.SetRow(protoCombo, row); Grid.SetColumn(protoCombo, 1);
        grid.Children.Add(protoLabel); grid.Children.Add(protoCombo); row++;

        // Changing protocol swaps in that protocol's default port, but only if the port
        // box still holds the previous protocol's default (a custom port is left alone).
        var previousProtocol = connection.Protocol;
        protoCombo.SelectionChanged += (_, _) =>
        {
            if (protoCombo.SelectedItem is not ConnectionProtocol newProto) return;
            if (int.TryParse(portBox.Text?.Trim(), out int cur) && cur == SavedConnection.GetDefaultPort(previousProtocol))
                portBox.Text = SavedConnection.GetDefaultPort(newProto).ToString();
            previousProtocol = newProto;
        };

        // Screen Mode
        AddRow();
        var modeLabel = new TextBlock { Text = "Screen Mode:", VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(modeLabel, row); Grid.SetColumn(modeLabel, 0);
        var screenModes = new[] { "80x25", "80x30", "80x40", "80x50", "132x25", "132x30", "132x40", "132x50", "132x60",
                                  "C64 40x25 (PETSCII)", "C128 40x25 (PETSCII)", "C128 80x25 (PETSCII)" };
        var modeCombo = new ComboBox
        {
            ItemsSource = screenModes,
            SelectedIndex = connection.ScreenMode >= 0 && connection.ScreenMode < screenModes.Length
                ? connection.ScreenMode : 0,
            Margin = new Thickness(0, 2, 0, 6)
        };
        Grid.SetRow(modeCombo, row); Grid.SetColumn(modeCombo, 1);
        grid.Children.Add(modeLabel); grid.Children.Add(modeCombo); row++;

        // Font
        AddRow();
        var fontLabel = new TextBlock { Text = "Font:", VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(fontLabel, row); Grid.SetColumn(fontLabel, 0);
        // No Commodore entries on purpose: in a C64/C128 screen mode the board drives the
        // character set with PETSCII 0x0E / 0x8E, so a manual upper/lower choice is redundant and
        // would only fight the board. The font picker applies to the ANSI modes.
        var fonts = new[] { "Codepage 437 English", "P0T NOoDLE", "mO'sOul", "MicroKnight Plus", "Topaz Plus", "MicroKnight", "Topaz" };
        var fontCombo = new ComboBox
        {
            ItemsSource = fonts,
            Margin = new Thickness(0, 2, 0, 6)
        };
        var fontForLookup = connection.FontName switch
        {
            "Topaz Plus (Amiga)" => "Topaz Plus",
            "P0T NOoDLE (Amiga)" => "P0T NOoDLE",
            "mO'sOul (Amiga)" => "mO'sOul",
            "MicroKnight Plus (Amiga)" => "MicroKnight Plus",
            "MicroKnight (Amiga)" => "MicroKnight",
            "Topaz (Amiga)" => "Topaz",
            _ => connection.FontName
        };
        fontCombo.SelectedIndex = string.IsNullOrEmpty(fontForLookup) ? 0
            : Array.IndexOf(fonts, fontForLookup) is int idx && idx >= 0 ? idx : 0;
        Grid.SetRow(fontCombo, row); Grid.SetColumn(fontCombo, 1);
        grid.Children.Add(fontLabel); grid.Children.Add(fontCombo); row++;

        // A Commodore screen mode supplies its own character ROM, and the BBS switches between its
        // upper and lower sets itself, so there is no font to choose. Show that IN the picker rather
        // than as a suffix on the label, which had nowhere to fit and got clipped.
        var petsciiFont = new[] { "PETSCII" };
        int lastAnsiFont = fontCombo.SelectedIndex;
        void SyncFontToMode()
        {
            bool commodore = ConnectionManager.IsCommodoreMode(modeCombo.SelectedIndex);
            if (commodore)
            {
                if (!ReferenceEquals(fontCombo.ItemsSource, petsciiFont))
                {
                    lastAnsiFont = fontCombo.SelectedIndex;
                    fontCombo.ItemsSource = petsciiFont;
                }
                fontCombo.SelectedIndex = 0;
                fontCombo.IsEnabled = false;
            }
            else
            {
                if (!ReferenceEquals(fontCombo.ItemsSource, fonts))
                {
                    fontCombo.ItemsSource = fonts;
                    fontCombo.SelectedIndex = lastAnsiFont >= 0 && lastAnsiFont < fonts.Length ? lastAnsiFont : 0;
                }
                fontCombo.IsEnabled = true;
            }
        }
        modeCombo.SelectionChanged += (_, _) => SyncFontToMode();
        SyncFontToMode();

        // Username
        AddRow();
        var userLabel = new TextBlock { Text = "Username:", VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(userLabel, row); Grid.SetColumn(userLabel, 0);
        var userBox = new TextBox { Text = connection.Username ?? "", Margin = new Thickness(0, 2, 0, 6) };
        Grid.SetRow(userBox, row); Grid.SetColumn(userBox, 1);
        grid.Children.Add(userLabel); grid.Children.Add(userBox); row++;

        // Password (shown as placeholder dots when one is already stored)
        AddRow();
        var passLabel = new TextBlock { Text = "Password:", VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(passLabel, row); Grid.SetColumn(passLabel, 0);
        var storedPassword = App.CredentialService.GetPassword(connection.Id);
        var passBox = new TextBox
        {
            PasswordChar = '\u2022',
            Text = string.IsNullOrEmpty(storedPassword) ? "" : PasswordPlaceholder,
            Margin = new Thickness(0, 2, 0, 6)
        };
        Grid.SetRow(passBox, row); Grid.SetColumn(passBox, 1);
        grid.Children.Add(passLabel); grid.Children.Add(passBox); row++;

        // Save Credentials
        AddRow();
        var credCheck = new CheckBox
        {
            Content = "Save Credentials",
            IsChecked = connection.SaveCredentials,
            Margin = new Thickness(0, 0, 0, 16)
        };
        Grid.SetRow(credCheck, row); Grid.SetColumn(credCheck, 1);
        grid.Children.Add(credCheck); row++;

        // Show the BBS's OWN status line (SyncTERM's nostatus, inverted). Not the app's status
        // bar along the bottom of the window — that one is always on.
        AddRow();
        var statusCheck = new CheckBox
        {
            Content = "Show BBS Status Line",
            IsChecked = connection.ShowStatusBar,
            Margin = new Thickness(0, 0, 0, 16)
        };
        Grid.SetRow(statusCheck, row); Grid.SetColumn(statusCheck, 1);
        grid.Children.Add(statusCheck); row++;

        // Typing a password implies the user wants it remembered
        passBox.TextChanged += (_, _) =>
        {
            if (!string.IsNullOrEmpty(passBox.Text))
                credCheck.IsChecked = true;
        };

        // Buttons
        AddRow();
        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        bool saved = false;
        var saveBtn = new Button
        {
            Content = "SAVE",
            Width = 90,
            Margin = new Thickness(0, 0, 8, 0)
        };
        var cancelBtn = new Button
        {
            Content = "CANCEL",
            Width = 90
        };
        saveBtn.Click += (_, _) => { saved = true; dialog.Close(); };
        cancelBtn.Click += (_, _) => { dialog.Close(); };

        // Handle Enter/Escape keys
        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                dialog.Close();
        };

        buttonPanel.Children.Add(saveBtn);
        buttonPanel.Children.Add(cancelBtn);
        Grid.SetRow(buttonPanel, row);
        Grid.SetColumn(buttonPanel, 0);
        Grid.SetColumnSpan(buttonPanel, 2);
        grid.Children.Add(buttonPanel);

        dialog.Content = grid;

        await dialog.ShowDialog(this);

        if (saved)
        {
            connection.Name = nameBox.Text?.Trim() ?? "";
            connection.Host = hostBox.Text?.Trim() ?? "";
            if (int.TryParse(portBox.Text?.Trim(), out int port) && port > 0 && port <= 65535)
                connection.Port = port;
            connection.Protocol = (ConnectionProtocol)(protoCombo.SelectedItem ?? ConnectionProtocol.Telnet);
            connection.ScreenMode = modeCombo.SelectedIndex;
            connection.ShowStatusBar = statusCheck.IsChecked == true;
            // Read the SelectedItem, not fonts[index] — in a Commodore mode the picker is showing
            // its own one-item list, so indexing the ANSI array would be wrong. "PETSCII" and the
            // default CP437 both persist as null, i.e. "no font override".
            var pickedFont = fontCombo.SelectedItem as string;
            connection.FontName =
                (pickedFont == null || pickedFont == "PETSCII" || pickedFont == fonts[0]) ? null : pickedFont;
            connection.Username = string.IsNullOrWhiteSpace(userBox.Text) ? null : userBox.Text.Trim();
            connection.SaveCredentials = credCheck.IsChecked == true;

            // Store / update / clear the saved password for this entry
            var typedPassword = passBox.Text ?? "";
            if (!connection.SaveCredentials || typedPassword.Length == 0)
                App.CredentialService.DeletePassword(connection.Id);
            else if (typedPassword != PasswordPlaceholder)
                App.CredentialService.SavePassword(connection.Id, typedPassword);

            _phonebookVm.SaveEditedConnection(connection);
        }
    }

    // ================================================================
    // Quick Connect Picker Dialog
    // ================================================================

    private async void OnPickQuickConnect(int slot)
    {
        var entries = App.PhonebookService.Connections;
        if (entries.Count == 0)
        {
            await ShowMessageBox("No Connections",
                "No phonebook entries. Add connections in the Phonebook first.");
            return;
        }

        var dialog = new Window
        {
            Title = "Select Connection",
            Width = 520,
            Height = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = new SolidColorBrush(Color.Parse("#FF0D0D1A")),
            Foreground = new SolidColorBrush(Color.Parse("#FFCCCCCC"))
        };

        var panel = new StackPanel { Margin = new Thickness(16) };

        var listBox = new ListBox
        {
            Height = 440
        };

        foreach (var entry in entries)
        {
            var item = new ListBoxItem
            {
                Content = $"{entry.Name}  \u2014  {entry.DisplayAddress}",
                Tag = entry,
                Padding = new Thickness(8, 6, 8, 6)
            };
            listBox.Items.Add(item);
        }

        bool cleared = false;
        bool selected = false;

        // Double-click to select
        listBox.DoubleTapped += (_, _) =>
        {
            if (listBox.SelectedItem != null)
            {
                selected = true;
                dialog.Close();
            }
        };

        var buttonPanel = new DockPanel
        {
            Margin = new Thickness(0, 12, 0, 0)
        };

        var clearBtn = new Button
        {
            Content = "CLEAR",
            Width = 90
        };
        clearBtn.Click += (_, _) => { cleared = true; dialog.Close(); };
        DockPanel.SetDock(clearBtn, Dock.Left);

        var rightButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var selectBtn = new Button
        {
            Content = "SELECT",
            Width = 90,
            Margin = new Thickness(0, 0, 8, 0),
            IsEnabled = false
        };
        selectBtn.Click += (_, _) => { selected = true; dialog.Close(); };

        // Enable SELECT only when an item is selected
        listBox.SelectionChanged += (_, _) =>
        {
            selectBtn.IsEnabled = listBox.SelectedItem != null;
        };

        var cancelBtn = new Button
        {
            Content = "CANCEL",
            Width = 90
        };
        cancelBtn.Click += (_, _) => { dialog.Close(); };

        // Escape key closes dialog
        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                dialog.Close();
            else if (e.Key == Key.Enter && listBox.SelectedItem != null)
            {
                selected = true;
                dialog.Close();
            }
        };

        rightButtons.Children.Add(selectBtn);
        rightButtons.Children.Add(cancelBtn);

        buttonPanel.Children.Add(clearBtn);
        buttonPanel.Children.Add(rightButtons);

        panel.Children.Add(listBox);
        panel.Children.Add(buttonPanel);

        dialog.Content = panel;

        await dialog.ShowDialog(this);

        if (cleared)
        {
            _homeVm.ClearQuickConnect(slot);
        }
        else if (selected && listBox.SelectedItem is ListBoxItem selectedItem)
        {
            var conn = (SavedConnection)selectedItem.Tag!;
            _homeVm.SetQuickConnect(slot, conn);
        }
    }

    // ================================================================
    // Menu Handlers
    // ================================================================

    private async void CopyFromTerminal()
    {
        if (_terminalView != null)
        {
            bool copied = await _terminalView.TerminalCanvas.CopySelectionToClipboardAsync();
            if (!copied)
            {
                // Nothing selected -- select all then copy
                _terminalView.TerminalCanvas.SelectAll();
                await _terminalView.TerminalCanvas.CopySelectionToClipboardAsync();
            }
        }
    }

    private void SelectAllInTerminal()
    {
        _terminalView?.TerminalCanvas.SelectAll();
    }

    private async void CaptureBbsSnapshot()
    {
        if (_terminalView == null || _activeConnection == null) return;

        try
        {
            var path = App.SnapshotService.CaptureAndSave(
                _terminalView.TerminalCanvas, _activeConnection);

            if (path == null)
            {
                await ShowMessageBox("TERMinator - Snapshot",
                    "Could not capture the terminal — the screen has not been rendered yet.");
                return;
            }

            // Refresh views that show the snapshot so the new image appears.
            _phonebookVm.Refresh();
            _homeVm.LoadQuickConnects();
        }
        catch (Exception ex)
        {
            await ShowMessageBox("TERMinator - Snapshot",
                $"Failed to save snapshot:\n\n{ex.Message}");
        }
    }

    // ================================================================
    // Changelog / About / Licenses
    // ================================================================

    /// <summary>A dialog with the app's dark chrome; height sized to its content unless a Height is set.</summary>
    private static Window MakeDialog(string title, double width)
    {
        return new Window
        {
            Title = title,
            Width = width,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
            Background = new SolidColorBrush(Color.Parse("#FF0D0D1A")),
            Foreground = new SolidColorBrush(Color.Parse("#FFCCCCCC"))
        };
    }

    /// <summary>
    /// Help → Changelog: the old About box's first half, no buttons. Title "Changelog", width 540, height sized to
    /// content, centred on the main window, not resizable; a 420 px scrolling text area with a 20 px margin. Esc
    /// closes it. Text comes from AppInfoText so the command line's "changelog" shows the same thing.
    /// </summary>
    private async void ShowChangelogDialog()
    {
        var dialog = MakeDialog("Changelog", 540);

        // Colorized like the console's "changelog" command: the logo colors on the name, amber headers, green bullets
        var textBlock = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var amber = new SolidColorBrush(Color.FromRgb(0xFF, 0xAA, 0x00));
        var secondary = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x99));
        var primary = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
        var green = new SolidColorBrush(Color.FromRgb(0x55, 0xDD, 0x55));
        static Avalonia.Controls.Documents.Run Run(string text, IBrush brush, bool bold = false) =>
            new(text) { Foreground = brush, FontWeight = bold ? FontWeight.Bold : FontWeight.Normal };
        var termBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x55, 0xFF));
        var inatorBrush = new SolidColorBrush(Color.FromRgb(0x55, 0xFF, 0xFF));
        textBlock.Inlines!.Add(Run("TERM", termBrush, bold: true));
        textBlock.Inlines.Add(Run("inator", inatorBrush, bold: true));
        textBlock.Inlines.Add(Run(" - BBS Terminal\n", primary));
        textBlock.Inlines.Add(Run($"Version {AppVersion}\n\n", secondary));
        textBlock.Inlines.Add(Run("─── What's New ───\n\n", amber, bold: true));
        foreach (var line in AppInfoText.Changelog)
        {
            if (line.StartsWith('v'))
                textBlock.Inlines.Add(Run(line + "\n", amber, bold: true));
            else if (line.StartsWith("• "))
            {
                textBlock.Inlines.Add(Run("• ", amber));
                textBlock.Inlines.Add(Run(line[2..] + "\n", green));
            }
            else
                textBlock.Inlines.Add(Run(line + "\n", green));
        }

        dialog.Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Height = 420,
            Margin = new Thickness(20),
            Content = textBlock
        };

        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                dialog.Close();
        };

        await dialog.ShowDialog(this);
    }

    /// <summary>Help > Check for Updates (manual only). Main window only: installing closes every TERMinator window.</summary>
    private async void ShowUpdateDialog()
    {
        string? HangUpWarning()
        {
            _sessions.RemoveAll(p => p.HasExited);
            int windows = _sessions.Count;
            if (_terminalVm.IsConnected)
                return windows > 0 ? "Installing hangs up this call and closes your BBS windows." : "Installing hangs up this call.";
            if (windows > 0)
                return windows == 1 ? "Installing closes your open BBS window and hangs up its call."
                                    : $"Installing closes your {windows} open BBS windows and hangs up their calls.";
            return null;
        }

        await new UpdateWindow(HangUpWarning, () =>
        {
            // Close everything now, without the "BBS windows are still open" question; the new AppImage is starting
            _sessionsConfirmed = true;
            SignalSessionsToClose();
            Close();
        }, OpenInBrowser).ShowDialog(this);
    }

    /// <summary>A plain run of text in a TextBlock's inlines.</summary>
    private static void AddRun(TextBlock tb, string text, IBrush? brush = null, bool bold = false)
    {
        var run = new Run(text) { FontWeight = bold ? FontWeight.Bold : FontWeight.Normal };
        if (brush != null)
            run.Foreground = brush;
        tb.Inlines!.Add(run);
    }

    /// <summary>
    /// A clickable link in a TextBlock's inlines. Avalonia has no Hyperlink inline, so the link is an inline
    /// TextBlock with an underline and a hand cursor; a browser that can't be opened is ignored.
    /// </summary>
    private void AddLink(TextBlock tb, string text, string uri)
    {
        var link = new TextBlock
        {
            Text = text,
            TextDecorations = TextDecorations.Underline,
            Cursor = new Cursor(StandardCursorType.Hand),
            Foreground = new SolidColorBrush(Color.Parse("#FFFFAA00")), // amber, like every other link in the app
            FontFamily = tb.FontFamily,
            FontSize = tb.FontSize
        };
        link.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            OpenInBrowser(uri);
            e.Handled = true;
        };
        tb.Inlines!.Add(new InlineUIContainer(link) { BaselineAlignment = BaselineAlignment.Baseline });
    }

    /// <summary>Opens a URL in the default browser; never throws.</summary>
    private void OpenInBrowser(string uri)
    {
        try
        {
            var launcher = Launcher;
            _ = launcher.LaunchUriAsync(new Uri(uri)).ContinueWith(t =>
            {
                if (t.IsFaulted || !t.Result)
                {
                    try { Process.Start(new ProcessStartInfo("xdg-open", uri) { UseShellExecute = false }); }
                    catch (Exception ex) { Console.Error.WriteLine($"Could not open {uri}: {ex.Message}"); }
                }
            }, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not open {uri}: {ex.Message}");
        }
    }

    /// <summary>
    /// Help → About TERMinator, top to bottom: the cracktro strip (220 px, edge to edge), then a panel with margin
    /// 20,16,20,12 holding the title / version / credits (links clickable; scrolls only past 360 px), then the two
    /// buttons. Width 540, height sized to content.
    /// </summary>
    private async void ShowAboutDialog()
    {
        var aboutDialog = MakeDialog("About TERMinator", 540);

        // Cracktro across the top, edge to edge; the text and buttons below keep their margins
        var root = new StackPanel();
        root.Children.Add(new Controls.CracktroScroller(HomeView.OriginalMarqueeText)
        {
            HorizontalAlignment = HorizontalAlignment.Stretch
        });

        var panel = new StackPanel { Margin = new Thickness(20, 16, 20, 12) };
        root.Children.Add(panel);

        // Colorized like the console's "about" command: the logo colors on the name, amber header and links,
        // green credits
        var textBlock = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var green = new SolidColorBrush(Color.FromRgb(0x55, 0xDD, 0x55));
        AddRun(textBlock, "TERM", new SolidColorBrush(Color.FromRgb(0xFF, 0x55, 0xFF)), bold: true);
        AddRun(textBlock, "inator", new SolidColorBrush(Color.FromRgb(0x55, 0xFF, 0xFF)), bold: true);
        AddRun(textBlock, " - BBS Terminal\n", new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)));
        AddRun(textBlock, $"Version {AppVersion}\n\n", new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x99)));
        AddRun(textBlock, "─── Credits ───\n\n", new SolidColorBrush(Color.FromRgb(0xFF, 0xAA, 0x00)), bold: true);
        AddRun(textBlock, "MOD Tracker Music by Jester of Sanity\n\n", green); // credit for the About music (AudioService.PlayAboutMusic)
        foreach (var (text, uri) in AppInfoText.Credits)
        {
            if (uri == null)
                AddRun(textBlock, text, green);
            else
                AddLink(textBlock, text, uri);
        }

        panel.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 360, // room for the music credit line without a scrollbar
            Content = textBlock
        });

        var buttonBar = new DockPanel
        {
            Margin = new Thickness(0, 12, 0, 0),
            LastChildFill = false
        };

        var licensesButton = new Button
        {
            Content = "Open Source Licenses...",
            Width = 170,
            Padding = new Thickness(4, 2, 4, 2)
        };
        licensesButton.Click += async (_, _) => await ShowLicensesDialog(aboutDialog);
        DockPanel.SetDock(licensesButton, Dock.Left);
        buttonBar.Children.Add(licensesButton);

        var okButton = new Button
        {
            Content = "OK",
            Width = 90,
            IsDefault = true,
            IsCancel = true
        };
        okButton.Click += (_, _) => aboutDialog.Close();
        DockPanel.SetDock(okButton, Dock.Right);
        buttonBar.Children.Add(okButton);

        panel.Children.Add(buttonBar);

        aboutDialog.Content = root;

        aboutDialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape || e.Key == Key.Enter)
                aboutDialog.Close();
        };

        // Elysium (Jester of Sanity), played once; Closed fires however the box is left (OK, Esc, X, Alt+F4)
        App.AudioService.PlayAboutMusic();
        aboutDialog.Closed += (_, _) => App.AudioService.StopAboutMusic();
        await aboutDialog.ShowDialog(this);
    }

    private async Task ShowLicensesDialog(Window owner)
    {
        var dialog = MakeDialog("Open Source Licenses", 640);   // height sized to content, like the About box

        var panel = new StackPanel { Margin = new Thickness(20) };

        var textBlock = new TextBlock { TextWrapping = TextWrapping.Wrap };
        const string rule = "\n\n────────────────────────────────────────\n\n";

        AddRun(textBlock, "TERMinator includes the following open source software:\n\n");

        AddRun(textBlock, "SyncTERM 1.8b\nCopyright 2007 Stephen Hurd\nLicense: GNU GPL v2\n");
        AddLink(textBlock, "syncterm.net", "https://syncterm.net");
        AddRun(textBlock, rule);

        AddRun(textBlock, "cryptlib\nCopyright 1992-2023 Peter Gutmann\nLicense: Sleepycat License\n");
        AddLink(textBlock, "cryptlib.com", "https://www.cryptlib.com");
        AddRun(textBlock, rule);

        AddRun(textBlock, "libxmp 4.6.0\nCopyright 1996-2024 Claudio Matsuoka & Hipolito Carraro Jr\nLicense: MIT\n");
        AddLink(textBlock, "github.com/libxmp/libxmp", "https://github.com/libxmp/libxmp");
        AddRun(textBlock, rule);

        AddRun(textBlock, "LibVLC / LibVLCSharp / VideoLAN.LibVLC\nCopyright VideoLAN\nLicense: LGPL 2.1\nSource: ");
        AddLink(textBlock, "code.videolan.org/videolan/LibVLCSharp", "https://code.videolan.org/videolan/LibVLCSharp");
        AddRun(textBlock, rule);

        // SyncTERM door audio / JPEG XL (native/apc), bundled in the AppImage from the build host's packages
        AddRun(textBlock, "libsndfile\nCopyright 1999-2023 Erik de Castro Lopo and contributors\nLicense: GNU LGPL 2.1 (with libogg, libvorbis, FLAC, Opus under BSD; mpg123, LAME under LGPL)\n");
        AddLink(textBlock, "github.com/libsndfile/libsndfile", "https://github.com/libsndfile/libsndfile");
        AddRun(textBlock, rule);

        AddRun(textBlock, "libjxl (JPEG XL)\nCopyright the JPEG XL Project Authors\nLicense: BSD 3-Clause (with Brotli, Highway under MIT / Apache 2.0; Little CMS under MIT)\n");
        AddLink(textBlock, "github.com/libjxl/libjxl", "https://github.com/libjxl/libjxl");
        AddRun(textBlock, rule);

        AddRun(textBlock, "CommunityToolkit.Mvvm\nCopyright Microsoft\nLicense: MIT\n");
        AddLink(textBlock, "github.com/CommunityToolkit/dotnet", "https://github.com/CommunityToolkit/dotnet");
        AddRun(textBlock, rule);

        AddRun(textBlock, "Avalonia UI\nCopyright AvaloniaUI OC\nLicense: MIT\n");
        AddLink(textBlock, "github.com/AvaloniaUI/Avalonia", "https://github.com/AvaloniaUI/Avalonia");

        panel.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Height = 360,
            Content = textBlock
        });

        var okButton = new Button
        {
            Content = "OK",
            Width = 90,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
            IsDefault = true,
            IsCancel = true
        };
        okButton.Click += (_, _) => dialog.Close();
        panel.Children.Add(okButton);

        dialog.Content = panel;

        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape || e.Key == Key.Enter)
                dialog.Close();
        };

        await dialog.ShowDialog(owner);
    }

    // ================================================================
    // Send Login
    // ================================================================

    private async Task SendLoginSequence()
    {
        if (_activeConnection == null) return;

        var username = _activeConnection.Username;
        var password = App.CredentialService.GetPassword(_activeConnection.Id);
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password)) return;

        // Disable the menu item while sending to prevent double-clicks
        MenuSendLogin.IsEnabled = false;
        try
        {
            await App.ConnectionManager.SendLoginSequenceAsync(username, password);
        }
        finally
        {
            // Re-enable if still connected
            if (App.ConnectionManager.State == ConnectionState.Connected)
                MenuSendLogin.IsEnabled = true;
        }
    }

    // ================================================================
    // Fullscreen
    // ================================================================

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        // F1 is not TERMinator's: it goes to the board (C64 doors want it). Help is on the menu.

        // Alt+Enter toggles fullscreen
        if (e.Key == Key.Enter && (e.KeyModifiers & KeyModifiers.Alt) != 0)
        {
            ToggleFullScreen();
            e.Handled = true;
        }
        // Alt+I sends login sequence
        else if (e.Key == Key.I && (e.KeyModifiers & KeyModifiers.Alt) != 0 && MenuSendLogin.IsEnabled)
        {
            _ = SendLoginSequence();
            e.Handled = true;
        }
        // Ctrl+Shift+S captures a BBS snapshot for the active connection
        else if (e.Key == Key.S
                 && (e.KeyModifiers & KeyModifiers.Control) != 0
                 && (e.KeyModifiers & KeyModifiers.Shift) != 0
                 && MenuSnapshot.IsEnabled)
        {
            CaptureBbsSnapshot();
            e.Handled = true;
        }
        // While in terminal view, suppress bare Alt from activating the menu bar.
        // Avalonia routes bare Alt to the AccessKey handler — block it so Alt+key
        // combos don't steal focus from the terminal.
        else if (_terminalView != null && ContentArea.Content == _terminalView
                 && (e.Key == Key.LeftAlt || e.Key == Key.RightAlt)
                 && _terminalVm.IsConnected)
        {
            e.Handled = true;
        }
    }

    private void OnWindowKeyUp(object? sender, KeyEventArgs e)
    {
        // Avalonia activates the menu bar on Alt key release. Suppress this while
        // the terminal is active so Alt+key shortcuts don't shift focus to the menu.
        if (_terminalView != null && ContentArea.Content == _terminalView
            && (e.Key == Key.LeftAlt || e.Key == Key.RightAlt)
            && _terminalVm.IsConnected)
        {
            e.Handled = true;
        }
    }

    private void ToggleFullScreen()
    {
        if (_isFullScreen)
        {
            // Exit fullscreen -- restore saved state
            WindowState = _savedWindowState;
            Position = new PixelPoint((int)_savedLeft, (int)_savedTop);
            Width = _savedWidth;
            Height = _savedHeight;
            MainMenu.IsVisible = true;
            _isFullScreen = false;
        }
        else
        {
            // Enter fullscreen -- save current state
            _savedWindowState = WindowState;
            _savedTop = Position.Y;
            _savedLeft = Position.X;
            _savedWidth = Width;
            _savedHeight = Height;

            MainMenu.IsVisible = false;
            WindowState = WindowState.FullScreen;
            _isFullScreen = true;
        }
    }

    // ================================================================
    // Helper: Simple message box
    // ================================================================

    /// <summary>
    /// Windows' ShowConnectionFailedDialog: 380x220, amber 16 bold title, 14 px message, OK (100 wide) on the right.
    /// The timed-out text names the user's Connection Timeout ("within N seconds").
    /// </summary>
    private async Task ShowConnectionFailedDialog(string bbsName, bool timedOut, int timeoutSec)
    {
        var title = timedOut ? "CONNECTION TIMED OUT" : "CONNECTION FAILED";
        var message = timedOut
            ? $"Could not reach {bbsName} within {timeoutSec} seconds."
            : $"Failed to connect to {bbsName}.";

        var dlg = new Window
        {
            Title = title,
            Width = 380,
            Height = 220,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = new SolidColorBrush(Color.Parse("#FF0D0D1A")),
            Foreground = new SolidColorBrush(Color.Parse("#FFCCCCCC"))
        };

        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 16,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#FFFFAA00")),
            Margin = new Thickness(0, 0, 0, 12)
        });
        panel.Children.Add(new TextBlock
        {
            Text = message,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#FFCCCCCC")),
            Margin = new Thickness(0, 0, 0, 20)
        });

        var btn = new Button
        {
            Content = "OK",
            Width = 100,
            HorizontalAlignment = HorizontalAlignment.Right,
            IsDefault = true
        };
        btn.Click += (_, _) => dlg.Close();
        dlg.Opened += (_, _) => { dlg.Activate(); btn.Focus(); };
        panel.Children.Add(btn);

        dlg.Content = panel;
        dlg.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter || e.Key == Key.Escape)
                dlg.Close();
        };
        await dlg.ShowDialog(this);
    }

    /// <param name="autoCloseSeconds">If above 0, the box shows "Closing in Ns" and closes itself when it
    /// runs out, like the transfer window (the Disconnected notice, user 2026-10-08).</param>
    private async Task ShowMessageBox(string title, string message, int autoCloseSeconds = 0)
    {
        var msgBox = new Window
        {
            Title = title,
            Width = 400,
            Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = new SolidColorBrush(Color.Parse("#FF0D0D1A")),
            Foreground = new SolidColorBrush(Color.Parse("#FFCCCCCC"))
        };

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        });

        var okButton = new Button
        {
            Content = "OK",
            Width = 80,
            HorizontalAlignment = HorizontalAlignment.Right,
            IsDefault = true
        };
        okButton.Click += (_, _) => msgBox.Close();
        // Nothing had focus, so Enter went nowhere (the Disconnected notice, user 2026-11-06)
        msgBox.Opened += (_, _) => { msgBox.Activate(); okButton.Focus(); };

        DispatcherTimer? timer = null;
        if (autoCloseSeconds > 0)
        {
            int secondsLeft = autoCloseSeconds;
            var countdown = new TextBlock
            {
                Text = $"Closing in {secondsLeft}s",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse("#FF888899")),
                Margin = new Thickness(0, -8, 0, 12)
            };
            panel.Children.Add(countdown);
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (_, _) =>
            {
                if (--secondsLeft <= 0)
                {
                    timer.Stop();
                    msgBox.Close();
                    return;
                }
                countdown.Text = $"Closing in {secondsLeft}s";
            };
            msgBox.Closed += (_, _) => timer.Stop();
            msgBox.Height += 20;
        }
        panel.Children.Add(okButton);

        msgBox.Content = panel;
        timer?.Start();

        msgBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter || e.Key == Key.Escape)
                msgBox.Close();
        };

        await msgBox.ShowDialog(this);
    }

    // ================================================================
    // One window per BBS
    // ================================================================

    /// <summary>Main window: opens <paramref name="connection"/> in a window and process of its own. The whole entry
    /// goes along (a board typed in the command line isn't in the phonebook), and so does where to open when the
    /// board has no remembered window: cascaded down from this one.</summary>
    /// <remarks>A session window calls it too, for a board's linked window (12.73): that passes where to open
    /// (<paramref name="startBounds"/>) and keeps the process itself, so it isn't one of <see cref="_sessions"/>.</remarks>
    private Process? LaunchSession(SavedConnection connection, int[]? startBounds = null, bool activate = true)
    {
        _sessions.RemoveAll(p => p.HasExited);
        string entry = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
            System.Text.Json.JsonSerializer.Serialize(connection)));

        try
        {
            var start = SessionStartInfo();
            start.ArgumentList.Add("--session");
            start.ArgumentList.Add(entry);
            start.ArgumentList.Add("--parent");
            start.ArgumentList.Add(Environment.ProcessId.ToString());
            if (startBounds is { Length: 4 } at)
            {
                start.ArgumentList.Add("--cascade");
                start.ArgumentList.Add($"{at[0]},{at[1]},{at[2]},{at[3]}");
            }
            else if (startBounds == null && GetNormalBounds() is NormalBounds normal)
            {
                var here = ToScreenRect(normal);
                int step = 32 * (_sessions.Count + 1);
                start.ArgumentList.Add("--cascade");
                start.ArgumentList.Add($"{here[0] + step},{here[1] + step},{here[2] + step},{here[3] + step}");
            }
            if (!activate)
            {
                start.ArgumentList.Add("--focus");
                start.ArgumentList.Add("0");
            }

            var process = Process.Start(start);
            if (process != null && startBounds == null)
                _sessions.Add(process);
            return process;
        }
        catch (Exception ex)
        {
            _ = ShowMessageBox("TERMinator", $"Couldn't open a window for {connection.Name}:\n\n{ex.Message}");
            return null;
        }
    }

    // ================================================================
    // Linked windows (Rainwire:Link, 12.73; Rainwire docs/MRC_WINDOW.md)
    // ================================================================
    // A board can open more windows onto the same call, one per kind: Rainwire's Multi Relay Chat (mrc) or Dead
    // Modem TV (tv), after the caller says yes to "Open ... in a new window?". Any kind the native side passed
    // ([a-z0-9_], 16 at most) is fine: the window only ever connects back to this board, so the kind only names it.
    // It's one more session process with an ad-hoc entry for this board (never a host the board names) that sends
    // the board's single-use token in its Caps answer. The board ends it when this call ends, so nothing here tracks
    // it beyond the process, kept for Raise. Same as Windows; Raise is a signal file here (no named events between
    // processes in .NET on Linux, and no AllowSetForegroundWindow: some window managers only flash the window).

    /// <summary>How long after the caller's key or click on the call a board may open a linked window.</summary>
    private const long LinkGestureWindowMs = 10_000;

    /// <summary>This window is a board's linked window (opened by another window's call).</summary>
    private static bool IsLinked => App.SessionConnection?.Linked == true;

    /// <summary>The linked windows this window's call opened, by kind.</summary>
    private readonly Dictionary<string, Process> _linkedWindows = [];

    private long _boardGestureAt;   // Environment.TickCount64 of the caller's last key or click on the call (0: none)

    /// <summary>Linked window: watches for its Raise signal file.</summary>
    private DispatcherTimer? _raiseSignalWatch;

    /// <summary>The Raise signal to a linked window: a file named after its process, created by the window that
    /// opened it and removed by the linked window when it comes forward.</summary>
    private static string RaiseSignalPath(int pid) =>
        Path.Combine(Path.GetTempPath(), $"TERMinator-Raise-{pid}");

    /// <summary>A key or click while the call is on screen: the board may open a linked window for the next 10 s.</summary>
    private void StampBoardGesture()
    {
        if (_terminalView != null && ContentArea.Content == _terminalView && _terminalVm.IsConnected)
            _boardGestureAt = Environment.TickCount64;
    }

    /// <summary>Options > Let Boards Open Extra Windows. Applies from the next connection (it decides mw=1).</summary>
    private static void SetLinkedWindows(bool on)
    {
        if (App.SettingsService.Settings.LinkedWindows == on)
            return;
        App.SettingsService.Settings.LinkedWindows = on;
        App.SettingsService.Save();
    }

    /// <summary>
    /// Rainwire:Link;Open. Opened only when all hold, else ignored without a word (the board falls back after 15 s):
    /// the setting was on when this call connected and still is; the caller pressed a key or clicked on the call in
    /// the last 10 s (used up); none of that kind is open from this call; this isn't a
    /// linked window itself. The optional hints (Rainwire docs/MRC_WINDOW.md): cols/rows pick the screen mode (else
    /// this window's), place=right|left|below|above where it first opens (else right), focus=0 opens it without
    /// taking the focus. Any other key is ignored.
    /// </summary>
    private void OpenLinkedWindow(Services.OscExtras.LinkRequest request)
    {
        string kind = request.Kind, token = request.Token, title = request.Title;
        if (IsLinked || kind.Length == 0 || !Services.OscExtras.IsLinkToken(token))
            return;
        if (!_terminalVm.IsConnected || _activeConnection is not { } parent)
            return;
        App.SettingsService.ReloadIfChanged();
        if (!_terminalVm.ConnectionManager.LinkedWindowsOn || !App.SettingsService.Settings.LinkedWindows)
            return;
        long gesture = _boardGestureAt;
        _boardGestureAt = 0;
        if (gesture == 0 || Environment.TickCount64 - gesture > LinkGestureWindowMs)
            return;
        if (_linkedWindows.TryGetValue(kind, out var open))
        {
            if (!open.HasExited)
                return;
            _linkedWindows.Remove(kind);
        }

        // The same board as this window, and nothing else from the board but the token and the title text. Its own
        // Id, so its window remembers its own place; no credentials, so SSH logs in blank and the token does the rest.
        string board = string.IsNullOrWhiteSpace(parent.Name) ? parent.Host : parent.Name;
        var linked = new SavedConnection
        {
            Id = $"{parent.Id}-{kind}",
            Name = $"{board} — {(title.Length > 0 ? title : kind.ToUpperInvariant())}",
            Host = parent.Host,
            Port = parent.Port,
            Protocol = parent.Protocol,
            SaveCredentials = false,
            FontName = parent.FontName,
            ScreenMode = LinkedScreenMode(parent.ScreenMode, request.Int("cols"), request.Int("rows")),
            ShowStatusBar = parent.ShowStatusBar,
            Linked = true,
            LinkToken = token,
            LinkBoard = board
        };
        // First time: beside this window (place=, right by default), on its screen, as wide as its columns need.
        // Afterwards the window's own remembered place wins. (An empty rect, when this window has no bounds yet, lets
        // it open wherever the window manager puts it.)
        int parentCols = Services.ConnectionManager.GetScreenModeDimensions(parent.ScreenMode).cols;
        int linkedCols = Services.ConnectionManager.GetScreenModeDimensions(linked.ScreenMode).cols;
        request.Args.TryGetValue("place", out var place);
        bool activate = !(request.Args.TryGetValue("focus", out var focus) && focus == "0");
        if (LaunchSession(linked, LinkedStartBounds(OpenLinkedCount(), place, (double)linkedCols / parentCols) ?? [],
                          activate) is { } process)
            _linkedWindows[kind] = process;
    }

    /// <summary>The linked window's screen mode: with no cols/rows hint (or one that isn't a number), the parent's;
    /// else the smallest text mode (80/132 by 25-60) that holds cols x rows, clamped to 20-255 by 5-100 first, or
    /// 132x60 when nothing does. The Commodore modes are never picked for a hint.</summary>
    private static int LinkedScreenMode(int parentMode, int? cols, int? rows)
    {
        if (cols == null && rows == null)
            return parentMode;
        var (pc, pr) = Services.ConnectionManager.GetScreenModeDimensions(parentMode);
        int wantCols = Math.Clamp(cols ?? pc, 20, 255), wantRows = Math.Clamp(rows ?? pr, 5, 100);
        int best = Services.NativeBridge.SCREEN_MODE_132X60, bestArea = int.MaxValue;
        foreach (int mode in new[]
                 {
                     Services.NativeBridge.SCREEN_MODE_80X25, Services.NativeBridge.SCREEN_MODE_80X30,
                     Services.NativeBridge.SCREEN_MODE_80X40, Services.NativeBridge.SCREEN_MODE_80X50,
                     Services.NativeBridge.SCREEN_MODE_132X25, Services.NativeBridge.SCREEN_MODE_132X30,
                     Services.NativeBridge.SCREEN_MODE_132X40, Services.NativeBridge.SCREEN_MODE_132X50,
                     Services.NativeBridge.SCREEN_MODE_132X60
                 })
        {
            var (c, r) = Services.ConnectionManager.GetScreenModeDimensions(mode);
            if (c >= wantCols && r >= wantRows && c * r < bestArea)
                (best, bestArea) = (mode, c * r);
        }
        return best;
    }

    /// <summary>Rainwire:Link;Raise: the linked window of this kind comes to the front (it restores and activates
    /// itself when it sees its signal file; the window manager may only flash it).</summary>
    private void RaiseLinkedWindow(string kind)
    {
        if (!_linkedWindows.TryGetValue(kind, out var process))
            return;
        try
        {
            if (process.HasExited)
            {
                _linkedWindows.Remove(kind);
                return;
            }
            File.WriteAllText(RaiseSignalPath(process.Id), string.Empty);
        }
        catch
        {
            // gone meanwhile
        }
    }

    /// <summary>How many of this call's linked windows are still open.</summary>
    private int OpenLinkedCount()
    {
        int n = 0;
        foreach (var p in _linkedWindows.Values)
        {
            try { if (!p.HasExited) n++; }
            catch { /* gone */ }
        }
        return n;
    }

    /// <summary>Where a linked window first opens (left, top, right, bottom in physical screen pixels, the --cascade
    /// shape): this window's normal bounds moved past its right edge (place "left", "below", "above": that edge
    /// instead; anything else is right), 32 px further along for each linked window already open, kept inside the
    /// work area of this window's monitor. widthScale: its columns over this window's (the terminal then sets its
    /// height to fit).</summary>
    private int[]? LinkedStartBounds(int open, string? place = null, double widthScale = 1.0)
    {
        if (GetNormalBounds() is not NormalBounds normal)
            return null;
        var here = ToScreenRect(normal);
        int width = (int)Math.Round((here[2] - here[0]) * Math.Clamp(widthScale, 0.25, 4.0));
        int height = here[3] - here[1], step = 32 * open;
        var (x, y) = place switch
        {
            "left" => (here[0] - width - step, here[1] + step),
            "below" => (here[0] + step, here[3] + step),
            "above" => (here[0] + step, here[1] - height - step),
            _ => (here[2] + step, here[1] + step)
        };

        Screen? screen = null;
        try { screen = Screens.ScreenFromPoint(normal.Position) ?? Screens.Primary; } catch { /* unclamped */ }
        if (screen == null)
            return [x, y, x + width, y + height];
        var work = screen.WorkingArea;
        int left = Math.Max(work.X, Math.Min(x, work.Right - width));
        int top = Math.Max(work.Y, Math.Min(y, work.Bottom - height));
        return [left, top, left + width, top + height];
    }

    /// <summary>
    /// What a session is started from: TERMinator itself. Run from an AppImage, that's the AppImage file ($APPIMAGE),
    /// not the binary inside it: the binary lives in the AppImage's temporary mount, which goes away when this process
    /// exits, while the AppImage gives the session a mount (and AppRun's library paths) of its own. Run as
    /// <c>dotnet TERMinator.dll</c> (a development build), the host needs the app's assembly first.
    /// </summary>
    private static ProcessStartInfo SessionStartInfo()
    {
        string? appImage = Environment.GetEnvironmentVariable("APPIMAGE");
        if (!string.IsNullOrEmpty(appImage) && File.Exists(appImage))
            return new ProcessStartInfo(appImage) { UseShellExecute = false };

        string exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "TERMinator");
        var start = new ProcessStartInfo(exe) { UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(exe) == "dotnet")
            start.ArgumentList.Add(typeof(App).Assembly.Location);
        return start;
    }

    /// <summary>Main window closing with BBS windows open: close them too? Themed like the app's other questions
    /// (ConfirmCommandLineMode).</summary>
    private async Task<bool> ConfirmCloseSessions(int count)
    {
        bool closeAll = false;

        var dialog = new Window
        {
            Title = "Close TERMinator",
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
            Background = new SolidColorBrush(Color.Parse("#FF0D0D1A")),
            Foreground = new SolidColorBrush(Color.Parse("#FFCCCCCC"))
        };

        var panel = new StackPanel { Margin = new Thickness(28, 24, 28, 20) };
        panel.Children.Add(new TextBlock
        {
            Text = count == 1 ? "1 BBS window is still open" : $"{count} BBS windows are still open",
            Foreground = new SolidColorBrush(Color.Parse("#FFFFAA00")),
            FontSize = 16,
            FontWeight = FontWeight.Bold,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        panel.Children.Add(new TextBlock
        {
            Text = count == 1 ? "Closing TERMinator hangs up that call too." : "Closing TERMinator hangs up every one of those calls.",
            Foreground = new SolidColorBrush(Color.Parse("#FF888899")),
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 18)
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var close = new Button
        {
            Content = "CLOSE ALL",
            Classes = { "amber" },
            IsDefault = true,
            Margin = new Thickness(0, 0, 10, 0)
        };
        close.Click += (_, _) =>
        {
            closeAll = true;
            dialog.Close();
        };
        var cancel = new Button { Content = "CANCEL", IsCancel = true };
        cancel.Click += (_, _) => dialog.Close();
        buttons.Children.Add(close);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        dialog.Content = panel;
        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                dialog.Close();
        };

        await dialog.ShowDialog(this);
        return closeAll;
    }

    /// <summary>Main window: asks about the open BBS windows (ConfirmCloseSessions); on CLOSE ALL signals them to close
    /// and closes for real. Avalonia's dialogs don't block, hence the close OnClosing cancelled and this second one.</summary>
    private async Task ConfirmThenCloseAsync(int count)
    {
        if (!await ConfirmCloseSessions(count))
            return;
        _sessionsConfirmed = true;
        SignalSessionsToClose();
        Close();
    }

    /// <summary>Main window: tells the sessions it started to close (they look for this file).</summary>
    private static void SignalSessionsToClose()
    {
        try
        {
            File.WriteAllText(CloseSessionsSignalPath(Environment.ProcessId), string.Empty);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MainWindow] Couldn't signal the BBS windows to close: {ex.Message}");
        }
    }

    /// <summary>Main window, at start: removes a close signal left by an earlier TERMinator that had this process id,
    /// and old ones from closed main windows (their sessions saw them long ago).</summary>
    private static void ClearCloseSessionsSignal()
    {
        try
        {
            File.Delete(CloseSessionsSignalPath(Environment.ProcessId));
            foreach (var old in Directory.EnumerateFiles(Path.GetTempPath(), "TERMinator-CloseSessions-*"))
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(old) > TimeSpan.FromMinutes(1))
                    File.Delete(old);
        }
        catch
        {
            // a stale signal only matters if its process id comes round again, which the first delete covers
        }
    }

    /// <summary>A window's normal bounds as a screen rect: left, top, right, bottom in the screen's physical pixels
    /// (the shape AppSettings.WindowBounds and SessionWindowBounds keep).</summary>
    private int[] ToScreenRect(NormalBounds bounds)
    {
        double scaling = 1.0;
        try { scaling = Screens.ScreenFromPoint(bounds.Position)?.Scaling ?? DesktopScaling; } catch { /* keep 1.0 */ }
        if (scaling <= 0) scaling = 1.0;
        return
        [
            bounds.Position.X,
            bounds.Position.Y,
            bounds.Position.X + (int)Math.Round(bounds.Width * scaling),
            bounds.Position.Y + (int)Math.Round(bounds.Height * scaling)
        ];
    }

    /// <summary>Session: where to open. The board's own remembered window, else cascaded from the main window at
    /// the main window's size (the terminal then sets its height to fit).</summary>
    private int[]? SessionStartBounds()
    {
        var board = App.SessionConnection!;
        if (App.SettingsService.Settings.SessionWindowBounds.TryGetValue(board.Id, out var remembered) &&
            remembered is { Length: 4 })
            return remembered;
        return App.SessionCascade;
    }

    /// <summary>Session: the menu has no home-screen items, and the window closes when the main window does.</summary>
    private void SetUpSessionWindow()
    {
        foreach (var item in new[]
                 { MenuPhonebook, MenuImportPhonebook, MenuExportPhonebook, MenuCommandLine, MenuSideBySideHome,
                   MenuBootSequence, MenuScreensaver, MenuMultiWindow, MenuCheckUpdates })
            item.IsVisible = false;

        // A linked window (12.73): no login to send and no phonebook entry to snapshot for; it comes to the front when
        // the window that opened it says so (RaiseLinkedWindow writes its signal file)
        if (IsLinked)
        {
            MenuSendLogin.IsVisible = false;
            MenuSnapshot.IsVisible = false;
            // View > Always on Top, remembered per linked window (its Id, <parentId>-<kind>)
            string id = App.SessionConnection!.Id;
            MenuAlwaysOnTop.IsVisible = true;
            MenuAlwaysOnTop.IsChecked = Topmost = App.SettingsService.Settings.LinkedWindowsOnTop.Contains(id);
            MenuAlwaysOnTop.PropertyChanged += (_, e) =>
            {
                if (e.Property == MenuItem.IsCheckedProperty)
                    SetLinkedOnTop(id, MenuAlwaysOnTop.IsChecked == true);
            };
            string raise = RaiseSignalPath(Environment.ProcessId);
            try { File.Delete(raise); } catch { /* a stale one only raises the window once */ }
            _raiseSignalWatch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _raiseSignalWatch.Tick += (_, _) =>
            {
                if (!File.Exists(raise))
                    return;
                try { File.Delete(raise); } catch { /* seen anyway */ }
                BringSessionToFront();
            };
            _raiseSignalWatch.Start();
            Closed += (_, _) =>
            {
                _raiseSignalWatch.Stop();
                try { File.Delete(raise); } catch { /* temp dir */ }
            };
        }

        if (App.ParentProcessId > 0)
        {
            string signal = CloseSessionsSignalPath(App.ParentProcessId);
            _closeSignalWatch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _closeSignalWatch.Tick += (_, _) =>
            {
                if (!File.Exists(signal))
                    return;
                _closeSignalWatch.Stop();
                _sessionClosing = true;
                Close();
            };
            _closeSignalWatch.Start();
        }
    }

    /// <summary>A linked window's View > Always on Top: applied now and remembered for that window's Id.</summary>
    private void SetLinkedOnTop(string id, bool on)
    {
        Topmost = on;
        App.SettingsService.Update(s =>
        {
            if (on)
                s.LinkedWindowsOnTop.Add(id);
            else
                s.LinkedWindowsOnTop.Remove(id);
        });
    }

    /// <summary>Options > Separate Window per BBS. Applies from the next connection; a call already running stays
    /// where it is.</summary>
    private static void SetMultiWindow(bool on)
    {
        if (App.SettingsService.Settings.MultiWindow == on)
            return;
        App.SettingsService.Settings.MultiWindow = on;
        App.SettingsService.Save();
    }

    /// <summary>Session: the call is over (or never started), so the window goes.</summary>
    private void CloseSession()
    {
        if (_sessionClosing)
            return;
        _sessionClosing = true;
        Dispatcher.UIThread.Post(Close);
    }

    /// <summary>Main window, on being brought back to the front: the phonebook, call log and settings as the BBS
    /// windows left them.</summary>
    private void RefreshFromSessions()
    {
        App.SettingsService.ReloadIfChanged();
        if (App.PhonebookService.ReloadIfChanged())
        {
            _phonebookVm.Refresh();
            _homeVm.LoadQuickConnects();
        }
    }

    // ================================================================
    // Window lifecycle
    // ================================================================

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Main window with BBS windows still open: ask once, then close them all with it. The question is a window of
        // its own, so this close is cancelled and made again once it's answered.
        if (!IsSession && !_sessionsConfirmed)
        {
            _sessions.RemoveAll(p => p.HasExited);
            if (_sessions.Count > 0)
            {
                e.Cancel = true;
                _ = ConfirmThenCloseAsync(_sessions.Count);
                return;
            }
        }

        // Old-tube power-off first: cancel this close, play the effect, then close for real
        if (!_poweredOffForClose && IsVisible && WindowState != WindowState.Minimized)
        {
            e.Cancel = true;
            _poweredOffForClose = true;
            _ = PowerOffAndCloseAsync();
            return;
        }

        _sessionClosing = true;

        // A BBS window remembers its own place, for the next time that board is opened, and leaves the main window's
        // alone
        if (IsSession)
        {
            _closeSignalWatch?.Stop();
            if (GetNormalBounds() is NormalBounds sessionBounds && App.SessionConnection is { } board)
            {
                var rect = ToScreenRect(sessionBounds);
                App.SettingsService.Update(s => s.SessionWindowBounds[board.Id] = rect);
            }
            if (_terminalVm.IsConnected)
                _terminalVm.DisconnectCommand.Execute(null);
            base.OnClosing(e);
            if (App.ConnectionManager.NativeConnectInProgress)
                Environment.Exit(0);
            return;
        }

        // Save window size -- use pre-fullscreen/pre-connect size as appropriate
        // so special modes don't become the startup size
        var settings = App.SettingsService.Settings;

        if (_isFullScreen)
        {
            // In fullscreen: save the pre-fullscreen state
            settings.WindowMaximized = _savedWindowState == WindowState.Maximized;
            settings.WindowWidth = _savedWidth;
            settings.WindowHeight = _savedHeight;
        }
        else
        {
            settings.WindowMaximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal)
            {
                settings.WindowWidth = Width;
                settings.WindowHeight = Height;
            }
        }

        // Position and shape for next launch: the normal (restored) bounds, so a maximized or full-screen window
        // still remembers where it returns to. Mid-session that's the home-screen placement from before
        // connecting, since launch always opens on the home screen.
        if ((_preConnectBounds ?? GetNormalBounds()) is NormalBounds closingBounds)
        {
            settings.WindowBounds = ToScreenRect(closingBounds);
            settings.WindowWidth = closingBounds.Width;
            settings.WindowHeight = closingBounds.Height;
        }
        App.SettingsService.Save();

        // Disconnect if still connected
        if (_terminalVm.IsConnected)
        {
            _terminalVm.DisconnectCommand.Execute(null);
        }

        base.OnClosing(e);
    }

    // ================================================================
    // CRT / Scanline effect
    // ================================================================

    private CrtSettingsWindow? _crtOptionsWindow;

    /// <summary>
    /// Opens the CRT tuning window. Everything — the on/off switch, the presets and the
    /// individual sliders — lives in that one window, so the View menu stays a single entry and
    /// there is no second copy of the state to keep in step.
    /// </summary>
    private void ShowCrtOptions()
    {
        if (_crtOptionsWindow != null)
        {
            _crtOptionsWindow.Activate();
            return;
        }

        var crt = App.SettingsService.Settings.Crt;

        // Modeless, so the terminal keeps running behind it and the look can be dialled in
        // against real ANSI art.
        var dialog = new CrtSettingsWindow(crt, live =>
        {
            crt.CopyFrom(live);
            ApplyCrtToTerminal();
            ApplyCrtToHome();
            ApplyConsoleEffect();
        });

        dialog.Closed += (_, _) =>
        {
            _crtOptionsWindow = null;
            App.SettingsService.Save();
        };
        _crtOptionsWindow = dialog;
        dialog.Show(this);
    }

    /// <summary>
    /// Pushes the current CRT settings at the live terminal.
    ///
    /// Deliberately fail-soft: a cosmetic filter must never be able to drop a connected BBS
    /// session, so a broken effect degrades to no effect.
    /// </summary>
    private void ApplyCrtToTerminal()
    {
        try
        {
            _terminalView?.ApplyCrtSettings(App.SettingsService.Settings.Crt);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"CRT effect could not be applied: {ex}");
            _terminalView?.DisableCrtEffect();
        }
    }

    /// <summary>
    /// Pushes the current CRT settings at the home screen's Quick Connect thumbnails, so toggling CRT mode changes
    /// how they look too. Fail-soft for the same reason as the terminal: a cosmetic filter must never take the app down.
    /// </summary>
    private void ApplyCrtToHome()
    {
        try
        {
            _homeView?.ApplyCrtSettings(App.SettingsService.Settings.Crt);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"CRT effect could not be applied to the home screen: {ex}");
            _homeView?.DisableCrtEffect();
        }
    }


    // ================================================================
    // Phonebook import / export
    // ================================================================

    /// <summary>
    /// Writes the whole phonebook out as a .zip (connections plus their snapshots) or a single
    /// .xml. The format matches the Windows and macOS clients, so a phonebook moves between all
    /// three.
    /// </summary>
    private async Task ExportPhonebook()
    {
        if (App.PhonebookService.Connections.Count == 0)
        {
            await ShowMessageBox("Export Phonebook", "There are no saved connections to export.");
            return;
        }

        var saveFile = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Phonebook",
            SuggestedFileName = $"TERMinator_Export_{DateTime.Now:yyyyMMdd_HHmmss}.zip",
            DefaultExtension = "zip",
            ShowOverwritePrompt = true,
            FileTypeChoices = new[]
            {
                new FilePickerFileType("TERMinator phonebook") { Patterns = new[] { "*.zip" } },
                new FilePickerFileType("Connections XML") { Patterns = new[] { "*.xml" } }
            }
        });
        if (saveFile == null)
            return;

        try
        {
            int count = PhonebookImportExportService.Export(
                App.PhonebookService, saveFile.Path.LocalPath);
            await ShowMessageBox("Export Phonebook",
                $"Exported {count} connection{(count == 1 ? "" : "s")}.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Phonebook export failed: {ex}");
            await ShowMessageBox("Export Phonebook", $"Export failed:\n\n{ex.Message}");
        }
    }

    /// <summary>
    /// Reads a .zip or .xml phonebook back in. The user chooses between merging into the current
    /// list and replacing it outright; merging updates any connection whose id already exists,
    /// so importing the same file twice does not duplicate the phonebook.
    /// </summary>
    private async Task ImportPhonebook()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Phonebook",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("TERMinator phonebook") { Patterns = new[] { "*.zip", "*.xml" } },
                new FilePickerFileType("All Files") { Patterns = new[] { "*" } }
            }
        });
        if (files == null || files.Count == 0)
            return;

        bool? replace = await ShowImportChoice();
        if (replace == null)
            return;

        try
        {
            int count = PhonebookImportExportService.Import(
                App.PhonebookService, files[0].Path.LocalPath, replace.Value);

            if (count == 0)
            {
                await ShowMessageBox("Import Phonebook", "No connections were found in that file.");
                return;
            }

            // The phonebook list and the home screen's quick-connect tiles both read the
            // service, so both need a nudge after the list changes underneath them.
            _phonebookVm.Refresh();
            _homeVm.LoadQuickConnects();

            await ShowMessageBox("Import Phonebook",
                $"Imported {count} connection{(count == 1 ? "" : "s")}.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Phonebook import failed: {ex}");
            await ShowMessageBox("Import Phonebook", $"Import failed:\n\n{ex.Message}");
        }
    }

    /// <summary>
    /// Replace / Merge / Cancel. True replaces the phonebook, false merges, null backs out.
    /// Avalonia ships no message box, so this is the three-button sibling of ShowMessageBox;
    /// Merge is the default and Escape cancels, because Replace is the destructive one.
    /// </summary>
    private async Task<bool?> ShowImportChoice()
    {
        bool? result = null;

        var dialog = new Window
        {
            Title = "Import Phonebook",
            Width = 460,
            Height = 190,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = new SolidColorBrush(Color.Parse("#FF0D0D1A")),
            Foreground = new SolidColorBrush(Color.Parse("#FFCCCCCC"))
        };

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = "Replace the current phonebook with the imported one, "
                 + "or merge the imported connections into it?",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        });

        var buttons = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10
        };

        var replaceButton = new Button { Content = "REPLACE", Width = 110 };
        replaceButton.Click += (_, _) => { result = true; dialog.Close(); };
        var mergeButton = new Button { Content = "MERGE", Width = 110 };
        mergeButton.Click += (_, _) => { result = false; dialog.Close(); };
        var cancelButton = new Button { Content = "CANCEL", Width = 110 };
        cancelButton.Click += (_, _) => { result = null; dialog.Close(); };

        buttons.Children.Add(replaceButton);
        buttons.Children.Add(mergeButton);
        buttons.Children.Add(cancelButton);
        panel.Children.Add(buttons);
        dialog.Content = panel;

        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { result = null; dialog.Close(); }
            else if (e.Key == Key.Enter) { result = false; dialog.Close(); }
        };

        await dialog.ShowDialog(this);
        return result;
    }


    // ================================================================
    // Help, SSH log and screenshots
    // ================================================================

    private HelpWindow? _helpWindow;

    /// <summary>The in-app manual. Modeless and single-instance, like the CRT window.</summary>
    private void ShowHelp()
    {
        if (_helpWindow != null)
        {
            _helpWindow.Activate();
            return;
        }

        var win = new HelpWindow();
        win.Closed += (_, _) => _helpWindow = null;
        _helpWindow = win;
        win.Show(this);
    }

    /// <summary>
    /// The SSH negotiation log, on demand rather than only after a failed connection.
    /// Cryptlib keeps the log from the last attempt, so this is the only way to read it after
    /// a connection that actually succeeded.
    /// </summary>
    private async Task ShowSshDebugLog()
    {
        string log = ConnectionManager.GetSshDebugLog();
        if (string.IsNullOrWhiteSpace(log))
            log = "No SSH debug log has been captured yet.\n\n" +
                  "The log is written while an SSH or Telnet/SSL connection negotiates, so " +
                  "connect to a board using one of those protocols and look again.";

        var win = new SshDebugWindow(log);
        await win.ShowDialog(this);
    }

    /// <summary>
    /// Save the terminal as a PNG the user chooses. Distinct from Take BBS Snapshot, which
    /// writes into the config directory and becomes the phonebook tile — this one is just a
    /// picture.
    /// </summary>
    private async Task SaveScreenshot()
    {
        if (_terminalView == null) return;

        var bitmap = _terminalView.TerminalCanvas.CaptureSnapshot();
        if (bitmap == null)
        {
            await ShowMessageBox("TERMinator - Screenshot",
                "Could not capture the terminal - the screen has not been rendered yet.");
            return;
        }

        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string name = _activeConnection?.Name ?? "TERMinator";
        foreach (char bad in System.IO.Path.GetInvalidFileNameChars())
            name = name.Replace(bad, '_');

        var saveFile = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Screenshot",
            SuggestedFileName = $"{name}_{stamp}.png",
            DefaultExtension = "png",
            ShowOverwritePrompt = true,
            FileTypeChoices = new[]
            {
                new FilePickerFileType("PNG image") { Patterns = new[] { "*.png" } }
            }
        });
        if (saveFile == null)
            return;

        try
        {
            using var stream = System.IO.File.Create(saveFile.Path.LocalPath);
            bitmap.Save(stream);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Screenshot save failed: {ex}");
            await ShowMessageBox("TERMinator - Screenshot",
                $"Could not save the screenshot:\n\n{ex.Message}");
        }
    }


    // ================================================================
    // Terminal right-click menu
    // ================================================================

    /// <summary>View > Scrollback and the terminal's right-click menu (Ctrl+B does the same in the canvas).</summary>
    private void ToggleScrollback()
    {
        if (_terminalView == null) return;
        var canvas = _terminalView.TerminalCanvas;
        if (!canvas.ToggleScrollback())
            _terminalView.ShowToast("Nothing to scroll back to yet");
        else if (canvas.IsScrolledBack)
            _terminalView.ShowToast("Scrollback: mouse wheel scrolls, any key returns");
        _terminalView.FocusTerminal();
    }

    private bool _contextMenuHooked;

    /// <summary>Hook the terminal up to the context menu once the view exists.</summary>
    private void AttachTerminalContextMenu()
    {
        if (_terminalView == null || _contextMenuHooked) return;
        _contextMenuHooked = true;

        // ⚠️ Avalonia opens a ContextMenu assigned to the control on its own, and it would then
        // be built ONCE with stale state. Handling the press ourselves and opening a freshly
        // built menu is what keeps the live entries honest.
        _terminalView.TerminalCanvas.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(_terminalView.TerminalCanvas).Properties.IsRightButtonPressed)
                return;
            ShowTerminalContextMenu();
            e.Handled = true;
        };
    }

    /// <summary>
    /// Build and open the terminal's context menu.
    ///
    /// Rebuilt on every open because three of the entries read live state — whether there is a
    /// selection, whether the clipboard has text, and whether logging is running — and a cached
    /// menu would show yesterday's answer. Mirrors the macOS client's menu.
    /// </summary>
    private async void ShowTerminalContextMenu()
    {
        if (_terminalView == null) return;

        var canvas = _terminalView.TerminalCanvas;
        bool connected = _terminalVm.IsConnected;

        bool hasClipboard = false;
        try
        {
            var cb = Clipboard;   // TopLevel.Clipboard; null on a headless/embedded surface
            if (cb != null)
                hasClipboard = !string.IsNullOrEmpty(await cb.GetTextAsync());
        }
        catch (Exception)
        {
            // A clipboard the compositor will not hand over just means Paste is greyed out.
        }

        var menu = new ContextMenu();
        var items = new List<Control>();

        void Add(string header, bool enabled, Action action)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => action();
            items.Add(item);
        }

        Add("Copy", canvas.IsSelecting, () => CopyFromTerminal());
        Add("Paste", hasClipboard && connected, () => _terminalVm.PasteCommand.Execute(null));

        Add(canvas.IsScrolledBack ? "Back to Live Screen" : "Scrollback", connected, () => ToggleScrollback());

        items.Add(new Separator());
        Add(canvas.UserHideCursor ? "Show Cursor" : "Hide Cursor", connected,
            () => canvas.UserHideCursor = !canvas.UserHideCursor);

        items.Add(new Separator());
        Add("Send File (ZMODEM)...", connected, () => ShowSendFileDialog());

        items.Add(new Separator());
        Add("Save Screenshot...", connected, () => _ = SaveScreenshot());
        if (!IsLinked)
            Add("Take BBS Snapshot", connected, () => CaptureBbsSnapshot());

        items.Add(new Separator());
        Add(_terminalVm.IsLogging ? "Stop Logging" : "Start Logging", connected,
            () => _terminalVm.ToggleLoggingCommand.Execute(null));

        items.Add(new Separator());
        Add("Disconnect", connected, () => _terminalVm.DisconnectCommand.Execute(null));

        menu.ItemsSource = items;
        menu.Open(canvas);
    }

}
