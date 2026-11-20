using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using TERMinator.Linux.Models;

namespace TERMinator.Linux.Controls;

/// <summary>What the command line needs from the app around it.</summary>
public interface ICommandLineHost
{
    /// <summary>Phonebook boards, in phonebook order (the numbers "list" shows are 1-based positions in this).</summary>
    IReadOnlyList<SavedConnection> Boards { get; }

    string VersionText { get; }

    bool CrtEnabled { get; }

    void SetCrtEnabled(bool enabled);

    /// <summary>An unsaved connection for an ad-hoc "dial", filled in with the user's default font and screen mode.</summary>
    SavedConnection CreateConnection(string host, int port, ConnectionProtocol protocol, string? username);

    /// <summary>Leaves the command line and connects. The host brings the command line back when the session ends.</summary>
    void ConnectTo(SavedConnection connection);

    void AddBoard(SavedConnection board);

    void RemoveBoard(SavedConnection board);

    /// <summary>The board in Quick Connect slot 1-4, or null if the slot is empty.</summary>
    SavedConnection? GetQuickConnect(int slot);

    /// <summary>Sets Quick Connect slot 1-4; null clears it.</summary>
    void SetQuickConnect(int slot, SavedConnection? board);

    void ExportPhonebook();

    void ImportPhonebook();

    /// <summary>The boot sequence lines (version, phonebook, TAP+, CRT status...), for the "boot" command.</summary>
    IReadOnlyList<string> BootLines { get; }

    /// <summary>The emulated modem speed in bps; 0 = full speed.</summary>
    int EmulatedBaud { get; }

    /// <summary>Stores the emulated modem speed (0 = full speed) and applies it straight away.</summary>
    void SetEmulatedBaud(int bps);

    /// <summary>Plays the digital rain easter egg over the command line.</summary>
    void ShowMatrix();

    /// <summary>Successful BBS calls so far, for the elite rank.</summary>
    int TotalCalls { get; }

    /// <summary>Phosphor mode: Color, Green, Amber or White.</summary>
    string Phosphor { get; }

    void SetPhosphor(string mode);

    /// <summary>Leaves the command line for the graphical home screen.</summary>
    void LoadGui();

    void QuitApp();
}

/// <summary>
/// Full-window command line: a <c>TERMinator&gt;</c> prompt ("TERM" and "inator" in the home logo's CGA magenta and
/// cyan) with a small command interpreter for the phonebook, connecting, a few network tools and some fun. Tab
/// completes commands and their arguments, Up/Down walks the history, Esc clears the line, Ctrl+C clears the line or
/// cancels a running command, and Ctrl+L clears the screen.
///
/// One instance lives for the app's lifetime, so the scrollback and the history survive between visits.
/// </summary>
public sealed class CommandLineOverlay : Grid
{
    private sealed record Command(
        string Name,
        string Group,
        string[] Aliases,
        string Usage,
        string Summary,
        Action<string> Run,
        Func<string, IEnumerable<string>>? CompleteArgs = null,
        bool Hidden = false);

    private static readonly string[] Groups = ["Boards", "Network", "System"];

    private const double ConsoleFontSize = 15;
    private const int MaxOutputLines = 1000;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan WardialGap = TimeSpan.FromMilliseconds(400);

    private static readonly FontFamily ConsoleFont = new("JetBrains Mono, DejaVu Sans Mono, monospace");
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.FromRgb(0x55, 0xDD, 0x55));
    private static readonly IBrush SuccessBrush = new SolidColorBrush(Color.FromRgb(0x33, 0xFF, 0x33));
    private static readonly IBrush DimBrush = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x99));
    private static readonly IBrush AccentBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xAA, 0x00));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x55, 0x55));
    // The home screen logo's own colors (CGA bright magenta and bright cyan), one solid color per half of the name
    private static readonly IBrush PromptTermBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x55, 0xFF));
    private static readonly IBrush PromptInatorBrush = new SolidColorBrush(Color.FromRgb(0x55, 0xFF, 0xFF));
    private static readonly IBrush PromptArrowBrush = PromptInatorBrush;

    private static readonly (string Name, ConnectionProtocol Protocol)[] Protocols =
    [
        ("telnet", ConnectionProtocol.Telnet),
        ("ssh", ConnectionProtocol.SSH),
        ("telnets", ConnectionProtocol.TelnetS),
        ("ws", ConnectionProtocol.WebSocket),
        ("wss", ConnectionProtocol.SecureWebSocket),
        ("raw", ConnectionProtocol.Raw)
    ];

    private readonly ICommandLineHost _host;
    private readonly List<Command> _commands = [];
    private readonly StackPanel _output = new();
    private readonly ScrollViewer _scroll;
    private readonly TextBox _input;
    private readonly DockPanel _inputRow;
    private readonly List<string> _history = [];
    private int _historyPosition;

    /// <summary>A running async command (ping, wardial, ...); Ctrl+C cancels it.</summary>
    private CancellationTokenSource? _busy;

    /// <summary>Set while a y/n question is waiting; the next line answers it.</summary>
    private Action<bool>? _pendingConfirm;

    /// <summary>The last board called from here (connect / dial / redial), for "redial".</summary>
    private SavedConnection? _lastCalled;

    /// <summary>The last unsaved "dial" target, for "save".</summary>
    private SavedConnection? _lastDialed;

    /// <summary>Set while the boot sequence is typing into the console (the input line is hidden meanwhile).</summary>
    private bool _booting;

    /// <summary>The console's CRT / phosphor layer, drawn over everything else in here (§7).</summary>
    private readonly Effects.ScreenEffectOverlay _screenEffect = new();

    public CommandLineOverlay(ICommandLineHost host)
    {
        _host = host;
        Background = new SolidColorBrush(Color.FromRgb(0x05, 0x05, 0x08));
        Cursor = new Cursor(StandardCursorType.Ibeam);

        _input = new TextBox
        {
            FontFamily = ConsoleFont,
            FontSize = ConsoleFontSize,
            Foreground = TextBrush,
            CaretBrush = TextBrush,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            MinHeight = 0,
            VerticalContentAlignment = VerticalAlignment.Center,
            AcceptsReturn = false,
            AcceptsTab = false
        };

        // The console's keys before the TextBox's own (Avalonia's TextBox handles Up / Down / Tab itself in the
        // bubbling phase, so a plain KeyDown handler would never see them): the tunnelling route runs first.
        _input.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);

        // The input line has no chrome at all, as on Windows. Transparent / zero on the TextBox itself isn't enough
        // here: Fluent's TextBox theme repaints its own PART_BorderElement when the box is hovered or focused (and
        // the console's input is always focused), which would put a grey box and an accent underline under the
        // prompt. Pinning the part flat in every state keeps it invisible; a no-op if the theme stops doing it.
        Styles.Add(FlatInputChrome(null));
        Styles.Add(FlatInputChrome(":pointerover"));
        Styles.Add(FlatInputChrome(":focus"));

        var prompt = CreatePromptBlock(echo: null);
        DockPanel.SetDock(prompt, Dock.Left);
        _inputRow = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
        _inputRow.Children.Add(prompt);
        _inputRow.Children.Add(_input);

        var content = new StackPanel { Margin = new Thickness(36, 28, 36, 28) };
        content.Children.Add(_output);
        content.Children.Add(_inputRow);

        _scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            Content = content
        };
        Children.Add(_scroll);
        Children.Add(_screenEffect);

        Loaded += (_, _) => FocusInput();
        // A click anywhere in the console puts the caret back in the input line (the left button only, as on Windows)
        PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left)
                FocusInput();
        };

        RegisterCommands();
        WriteWelcome();
    }

    private void WriteWelcome()
    {
        WriteLine($"TERMinator command line v{_host.VersionText}", AccentBrush);
        WriteLine("Type \"help\" for commands. Tab completes commands and board names.", DimBrush);
        WriteLine(" ", TextBrush);
    }

    /// <summary>
    /// Entering command line mode from the home screen: clears the screen, types the boot sequence, then the welcome
    /// and the prompt. History survives; a second call while it's still typing is ignored.
    /// </summary>
    public async void PlayBootIntro()
    {
        if (_booting)
            return;
        await TypeBootSequence();
        WriteLine(" ", TextBrush);
        WriteWelcome();
        ScrollToEnd();
        FocusInput();
    }

    /// <summary>Puts keyboard focus in the input line (after layout, so it sticks when the overlay is re-shown).</summary>
    public void FocusInput() =>
        Dispatcher.UIThread.Post(() =>
        {
            _input.Focus();
            _input.CaretIndex = (_input.Text ?? string.Empty).Length;
        }, DispatcherPriority.Input);

    /// <summary>Writes a highlighted status line, e.g. when returning from a session.</summary>
    public void WriteNotice(string text) => WriteLine(text, AccentBrush);

    // ================================================================ screen effect

    /// <summary>
    /// The console's screen effect (§7): with CRT mode on the full CRT effect in the phosphor color, otherwise just
    /// the phosphor tint, or nothing in Color mode.
    /// </summary>
    public void ApplyScreenEffect(CrtSettings crt, Color? phosphorTint)
    {
        // One layer carries both: the CRT post-process is tinted with the phosphor colour when one is set, exactly as
        // the Windows client folds the tint into its single element effect.
        _screenEffect.Configure(crt, phosphorTint);
    }

    // Never influence the window's size-to-content fit: the overlay just fills whatever space the window has.
    protected override Size MeasureOverride(Size availableSize)
    {
        base.MeasureOverride(availableSize);
        return new Size(0, 0);
    }

    // ================================================================ command table

    private void RegisterCommands()
    {
        IEnumerable<string> BoardNames(string _) => _host.Boards.Select(b => b.Name);
        IEnumerable<string> BoardNamesAndHosts(string _) =>
            _host.Boards.Select(b => b.Name).Concat(_host.Boards.Select(b => $"{b.Host}:{b.Port}"));

        // Boards
        Add("list", "Boards", ["ls", "dir", "phonebook"], "list [filter]", "Show the boards in your phonebook", List);
        Add("recent", "Boards", [], "recent", "Boards you called most recently", _ => Recent());
        Add("info", "Boards", [], "info <#|name>", "Show a board's connection details", Info, BoardNames);
        Add("stats", "Boards", [], "stats [#|name]", "Your call log: calls, time online and data, for every board or one",
            Stats, BoardNames);
        Add("connect", "Boards", ["call"], "connect <#|name>", "Connect to a phonebook board by number or name", Connect, BoardNames);
        Add("redial", "Boards", [], "redial", "Call the last board again", _ => Redial());
        Add("dial", "Boards", [], "dial <host[:port]> [telnet|ssh|telnets|ws|wss|raw]",
            "Connect to any address without adding it to the phonebook (ssh: user@host)", Dial, DialCompletions);
        Add("add", "Boards", [], "add <name> <host[:port]> [telnet|ssh|telnets|ws|wss|raw]",
            "Add a board to the phonebook (names may contain spaces)", AddBoard);
        Add("save", "Boards", [], "save [name]", "Save the last board you dialed to the phonebook", Save);
        Add("remove", "Boards", ["rm", "del"], "remove <#|name>", "Remove a board from the phonebook (asks first)", Remove, BoardNames);
        Add("qc", "Boards", [], "qc [<1-4> <#|name>|clear]", "Show the Quick Connect slots, or set / clear one",
            QuickConnect, QuickConnectCompletions);
        Add("export", "Boards", [], "export", "Export the phonebook to a file", _ => _host.ExportPhonebook());
        Add("import", "Boards", [], "import", "Import a phonebook from a file", _ => _host.ImportPhonebook());

        // Network
        Add("resolve", "Network", ["nslookup"], "resolve <host|#|name>", "Look up a host's addresses (doesn't contact the board)",
            Resolve, BoardNamesAndHosts);
        Add("ping", "Network", [], "ping <#|name|host[:port]>", "See whether a board answers, and how fast (makes one real call)",
            Ping, BoardNamesAndHosts);
        Add("wardial", "Network", [], "wardial", "Ping every board in your phonebook to see if the board is up",
            _ => Wardial());

        // System
        Add("crt", "System", [], "crt [on|off]", "Show or switch CRT mode", Crt, _ => ["on", "off"]);
        Add("baud", "System", ["speed"], "baud [off|300|1200|2400|9600|14.4k|19.2k|28.8k|33.6k|56k]",
            "Show or set the emulated modem speed", Baud, _ => Services.ModemSpeeds.ShortNames);
        Add("phosphor", "System", [], "phosphor [color|green|amber|white]", "Show or switch the monochrome tube color",
            Phosphor, _ => ["color", "green", "amber", "white"]);
        Add("whoami", "System", [], "whoami", "Your operator profile", _ => WhoAmI());
        Add("rank", "System", ["elite"], "rank", "Your elite status and how far to the next rank", _ => Rank());
        Add("date", "System", [], "date", "The current date and time", _ =>
            WriteLine(DateTime.Now.ToString("ddd MMM dd HH:mm:ss yyyy", CultureInfo.InvariantCulture), TextBrush));
        Add("uptime", "System", [], "uptime", "How long TERMinator has been running", _ => Uptime());
        Add("motd", "System", [], "motd", "Message of the day", _ => Motd());
        Add("ver", "System", ["version"], "ver", "Show the TERMinator version", _ =>
            WriteLine($"TERMinator v{_host.VersionText}", AccentBrush));
        Add("about", "System", [], "about", "About TERMinator and the people behind it", _ => About());
        Add("changelog", "System", [], "changelog", "What's new in each version", _ => Changelog());
        Add("clear", "System", ["cls"], "clear", "Clear the screen", _ => _output.Children.Clear());
        Add("help", "System", ["?"], "help [command]", "List the commands, or show how to use one", Help, _ => CommandNames());
        Add("gui", "System", ["exit"], "gui", "Load the graphical TERMinator home screen", _ => _host.LoadGui());

        Add("fact", "System", [], "fact", "A random piece of computing and BBS history", _ => Fact());
        Add("hack", "System", [], "hack <#|name>", "Totally real hacking. Definitely.", Hack, BoardNames);
        Add("boot", "System", [], "boot", "Clear the screen and replay the boot sequence", args => _ = TypeBootSequence());
        Add("quit", "System", [], "quit", "Close TERMinator", _ => _host.QuitApp()); // keep last in the list

        // Secret: they work, but aren't listed by "help" or offered by Tab
        Add("matrix", "System", [], "matrix", "Enter the digital rain", _ => _host.ShowMatrix(), hidden: true);
        Add("sudo", "System", [], "sudo <anything>", "Try it", _ => WriteLine("nice try.", ErrorBrush), hidden: true);
    }

    private void Add(string name, string group, string[] aliases, string usage, string summary, Action<string> run,
                     Func<string, IEnumerable<string>>? completeArgs = null, bool hidden = false) =>
        _commands.Add(new Command(name, group, aliases, usage, summary, run, completeArgs, hidden));

    // ================================================================ boards

    private void Help(string args)
    {
        if (args.Length > 0)
        {
            var command = FindCommand(args);
            if (command == null)
            {
                WriteLine($"No such command: {args}", ErrorBrush);
                return;
            }
            WriteLine($"  {command.Usage}", AccentBrush);
            WriteLine($"  {command.Summary}", TextBrush);
            if (command.Aliases.Length > 0)
                WriteLine($"  also: {string.Join(", ", command.Aliases)}", DimBrush);
            return;
        }

        foreach (var group in Groups)
        {
            WriteLine(group, AccentBrush);
            foreach (var command in _commands.Where(c => c.Group == group && !c.Hidden))
                WriteLine($"  {command.Name,-9} {command.Summary}", TextBrush);
        }
        WriteLine(" ", TextBrush);
        WriteLine("\"help <command>\" shows how to use one.", DimBrush);
        WriteLine("Tab: complete   Up/Down: history   Esc: clear the line   Ctrl+C: clear / cancel   Ctrl+L: clear the screen",
                  DimBrush);
    }

    private void List(string filter)
    {
        var boards = _host.Boards;
        if (boards.Count == 0)
        {
            WriteLine("The phonebook is empty. Try \"add\" or \"dial\".", DimBrush);
            return;
        }

        WriteLine($"  {"#",3}  {"NAME",-28} {"PROTO",-8} ADDRESS", AccentBrush);
        int shown = 0;
        for (int i = 0; i < boards.Count; i++)
        {
            var board = boards[i];
            if (filter.Length > 0
                && !board.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !board.Host.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;

            WriteLine($"  {i + 1,3}  {Truncate(board.Name, 28),-28} {ProtocolName(board.Protocol),-8} {board.Host}:{board.Port}",
                      TextBrush);
            shown++;
        }
        if (shown == 0)
            WriteLine($"No boards match \"{filter}\".", DimBrush);
    }

    private void Recent()
    {
        var called = RecentlyCalled().Take(10).ToList();
        if (called.Count == 0)
        {
            WriteLine("No calls yet.", DimBrush);
            return;
        }
        foreach (var board in called)
            WriteLine($"  {board.LastConnected:yyyy-MM-dd HH:mm}  {Truncate(board.Name, 28),-28} {board.Host}:{board.Port}",
                      TextBrush);
    }

    private void Info(string args)
    {
        var board = ResolveBoard(args);
        if (board == null)
            return;

        WriteLine($"  {board.Name}", AccentBrush);
        WriteLine($"  address   : {board.Host}:{board.Port}", TextBrush);
        WriteLine($"  protocol  : {ProtocolName(board.Protocol)}", TextBrush);
        if (!string.IsNullOrEmpty(board.Username))
            WriteLine($"  username  : {board.Username}", TextBrush);
        WriteLine($"  last call : {(board.LastConnected == default ? "never" : board.LastConnected.ToString("yyyy-MM-dd HH:mm"))}",
                  TextBrush);
    }

    /// <summary>"stats": the call log for every board (busiest first), or one board in detail.</summary>
    private void Stats(string args)
    {
        static string Duration(double seconds) => Services.CallStatsService.FormatDuration(seconds);
        static string Size(long bytes) => Services.CallStatsService.FormatBytes(bytes);
        var log = App.CallStatsService;

        if (args.Length > 0)
        {
            var board = ResolveBoard(args);
            if (board == null)
                return;
            var stats = log.Get(board.Id);
            WriteLine($"  {board.Name}", AccentBrush);
            if (stats == null || stats.Calls == 0)
            {
                WriteLine("  No calls logged yet.", DimBrush);
                return;
            }
            WriteLine($"  calls      : {stats.Calls}", TextBrush);
            WriteLine($"  first call : {stats.FirstCall:yyyy-MM-dd HH:mm}", TextBrush);
            WriteLine($"  last call  : {stats.LastCall:yyyy-MM-dd HH:mm}", TextBrush);
            WriteLine($"  online     : {Duration(stats.SecondsOnline)}  (about {Duration(stats.SecondsOnline / stats.Calls)} a call)",
                      TextBrush);
            WriteLine($"  received   : {Size(stats.BytesReceived)}", TextBrush);
            WriteLine($"  sent       : {Size(stats.BytesSent)}", TextBrush);
            return;
        }

        var all = log.All.Where(s => s.Calls > 0).ToList();
        if (all.Count == 0)
        {
            WriteLine("No calls logged yet.", DimBrush);
            return;
        }

        WriteLine("CALL LOG", AccentBrush);
        WriteLine($"  {all.Sum(s => s.Calls)} calls to {all.Count} board{(all.Count == 1 ? "" : "s")}, " +
                  $"{Duration(all.Sum(s => s.SecondsOnline))} online, " +
                  $"{Size(all.Sum(s => s.BytesReceived))} received, {Size(all.Sum(s => s.BytesSent))} sent", TextBrush);
        WriteLine(" ", TextBrush);
        WriteLine($"  {"BOARD",-28} {"CALLS",5}  {"ONLINE",-9}  LAST CALL", AccentBrush);
        foreach (var stats in all.OrderByDescending(s => s.SecondsOnline).Take(10))
            WriteLine($"  {Truncate(stats.Name, 28),-28} {stats.Calls,5}  {Duration(stats.SecondsOnline),-9}  {stats.LastCall:yyyy-MM-dd HH:mm}",
                      TextBrush);
        if (all.Count > 10)
            WriteLine($"  ... and {all.Count - 10} more", DimBrush);
        WriteLine("\"stats <#|name>\" shows one board.", DimBrush);
    }

    /// <summary>"baud": shows or sets the emulated modem speed.</summary>
    private void Baud(string args)
    {
        if (args.Trim().Length == 0)
        {
            int current = _host.EmulatedBaud;
            WriteLine(current > 0 ? $"Emulated speed is {Services.ModemSpeeds.Label(current)}." : "Speed emulation is OFF (full speed).",
                      TextBrush);
            return;
        }

        if (!Services.ModemSpeeds.TryParse(args, out int bps))
        {
            WriteLine($"usage: baud [{string.Join("|", Services.ModemSpeeds.ShortNames)}]", ErrorBrush);
            return;
        }

        _host.SetEmulatedBaud(bps);
        WriteLine(bps > 0 ? $"Emulated speed set to {Services.ModemSpeeds.Label(bps)}." : "Speed emulation OFF.", SuccessBrush);
    }

    private void Connect(string args)
    {
        var board = ResolveBoard(args);
        if (board != null)
            Call(board, board.Name);
    }

    private void Redial()
    {
        var target = _lastCalled ?? RecentlyCalled().FirstOrDefault();
        if (target == null)
        {
            WriteLine("Nothing to redial yet.", ErrorBrush);
            return;
        }
        Call(target, target.Name);
    }

    private void Dial(string args)
    {
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is 0 or > 2)
        {
            WriteLine("usage: dial <host[:port]> [telnet|ssh|telnets|ws|wss|raw]", ErrorBrush);
            return;
        }

        var protocol = ConnectionProtocol.Telnet;
        if (parts.Length == 2 && !TryParseProtocol(parts[1], out protocol))
        {
            WriteLine($"Unknown protocol \"{parts[1]}\". Use telnet, ssh, telnets, ws, wss or raw.", ErrorBrush);
            return;
        }

        if (!TryParseTarget(parts[0], protocol, out var host, out int port, out var username))
        {
            WriteLine($"\"{parts[0]}\" isn't a valid host[:port].", ErrorBrush);
            return;
        }

        var connection = _host.CreateConnection(host, port, protocol, username);
        _lastDialed = connection;
        Call(connection, "dial");
    }

    private void Call(SavedConnection connection, string label)
    {
        _lastCalled = connection;
        WriteLine($"ATDT {connection.Host}:{connection.Port}  ({label}, {ProtocolName(connection.Protocol)})", AccentBrush);
        _host.ConnectTo(connection);
    }

    private void AddBoard(string args)
    {
        var tokens = args.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var protocol = ConnectionProtocol.Telnet;
        if (tokens.Count >= 3 && TryParseProtocol(tokens[^1], out var parsedProtocol))
        {
            protocol = parsedProtocol;
            tokens.RemoveAt(tokens.Count - 1);
        }
        if (tokens.Count < 2)
        {
            WriteLine("usage: add <name> <host[:port]> [telnet|ssh|telnets|ws|wss|raw]", ErrorBrush);
            return;
        }

        string address = tokens[^1];
        tokens.RemoveAt(tokens.Count - 1);
        string name = string.Join(' ', tokens);

        if (!TryParseTarget(address, protocol, out var host, out int port, out var username))
        {
            WriteLine($"\"{address}\" isn't a valid host[:port].", ErrorBrush);
            return;
        }
        if (FindByAddress(host, port) is { } existing)
        {
            WriteLine($"{host}:{port} is already in the phonebook as \"{existing.Name}\".", ErrorBrush);
            return;
        }

        var board = _host.CreateConnection(host, port, protocol, username);
        board.Name = name;
        _host.AddBoard(board);
        WriteLine($"Added \"{name}\" as #{_host.Boards.Count}.", SuccessBrush);
    }

    private void Save(string args)
    {
        if (_lastDialed == null)
        {
            WriteLine("Nothing to save. \"dial\" a board first.", ErrorBrush);
            return;
        }
        if (FindByAddress(_lastDialed.Host, _lastDialed.Port) is { } existing)
        {
            WriteLine($"{_lastDialed.Host}:{_lastDialed.Port} is already in the phonebook as \"{existing.Name}\".", ErrorBrush);
            return;
        }

        if (args.Length > 0)
            _lastDialed.Name = args;
        _host.AddBoard(_lastDialed);
        WriteLine($"Saved \"{_lastDialed.Name}\" as #{_host.Boards.Count}.", SuccessBrush);
        _lastDialed = null;
    }

    private void Remove(string args)
    {
        var board = ResolveBoard(args);
        if (board == null)
            return;

        WriteLine($"Remove \"{board.Name}\" ({board.Host}:{board.Port}) from the phonebook? (y/n)", AccentBrush);
        _pendingConfirm = yes =>
        {
            if (yes)
            {
                _host.RemoveBoard(board);
                WriteLine($"Removed \"{board.Name}\".", SuccessBrush);
            }
            else
            {
                WriteLine("Kept it.", DimBrush);
            }
        };
    }

    private void QuickConnect(string args)
    {
        var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            for (int slot = 1; slot <= 4; slot++)
            {
                var board = _host.GetQuickConnect(slot);
                WriteLine(board == null ? $"  {slot}: (empty)" : $"  {slot}: {board.Name}  {board.Host}:{board.Port}",
                          board == null ? DimBrush : TextBrush);
            }
            return;
        }

        if (parts.Length == 1 || !int.TryParse(parts[0], out int number) || number is < 1 or > 4)
        {
            WriteLine("usage: qc [<1-4> <#|name>|clear]", ErrorBrush);
            return;
        }

        if (parts[1].Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            _host.SetQuickConnect(number, null);
            WriteLine($"Quick Connect {number} cleared.", SuccessBrush);
            return;
        }

        var target = ResolveBoard(parts[1]);
        if (target == null)
            return;
        _host.SetQuickConnect(number, target);
        WriteLine($"Quick Connect {number} is now \"{target.Name}\".", SuccessBrush);
    }

    // ================================================================ network

    private void Resolve(string args)
    {
        string? host = args.Length == 0 ? null : ResolveBoard(args, report: false)?.Host
                       ?? (TrySplitHostPort(args, out var parsed, out _) ? parsed : null);
        if (host == null)
        {
            WriteLine("usage: resolve <host|#|name>", ErrorBrush);
            return;
        }

        RunAsync(async token =>
        {
            var clock = Stopwatch.StartNew();
            var addresses = await Dns.GetHostAddressesAsync(host, token);
            WriteLine($"{host}  ({clock.ElapsedMilliseconds} ms)", AccentBrush);
            foreach (var address in addresses)
                WriteLine($"  {(address.AddressFamily == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4")}  {address}", TextBrush);
            if (addresses.Length == 0)
                WriteLine("  no addresses", DimBrush);
        });
    }

    private void Ping(string args)
    {
        string host;
        int port;
        string label;
        if (ResolveBoard(args, report: false) is { } board)
        {
            (host, port, label) = (board.Host, board.Port, board.Name);
        }
        else if (args.Length > 0 && TrySplitHostPort(args, out var parsedHost, out var parsedPort))
        {
            (host, port, label) = (parsedHost, parsedPort ?? 23, parsedHost);
        }
        else
        {
            WriteLine("usage: ping <#|name|host[:port]>", ErrorBrush);
            return;
        }

        RunAsync(async token =>
        {
            WriteLine($"ATDT {host}:{port} ...", DimBrush);
            var (answered, ms, error) = await ProbeAsync(host, port, token);
            WriteLine(answered
                          ? $"CARRIER {host}:{port}  time={ms}ms  ({label})"
                          : $"NO CARRIER {host}:{port}  ({error})",
                      answered ? SuccessBrush : ErrorBrush);
        });
    }

    private void Wardial()
    {
        var boards = _host.Boards.ToList();
        if (boards.Count == 0)
        {
            WriteLine("The phonebook is empty. Nothing to dial.", DimBrush);
            return;
        }

        RunAsync(async token =>
        {
            WriteLine($"war dialing {boards.Count} boards (Ctrl+C to stop)...", AccentBrush);
            int answered = 0;
            for (int i = 0; i < boards.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var board = boards[i];
                var (ok, ms, error) = await ProbeAsync(board.Host, board.Port, token);
                if (ok)
                    answered++;

                string result = ok ? $"CARRIER     {ms,5}ms" : "NO CARRIER         ";
                string detail = ok ? string.Empty : $"  ({error})";
                WriteLine($"  {i + 1,3}  {result}  {Truncate(board.Name, 28),-28} {board.Host}:{board.Port}{detail}",
                          ok ? SuccessBrush : ErrorBrush);

                // Go easy on the boards: a short gap between calls
                if (i < boards.Count - 1)
                    await Task.Delay(WardialGap, token);
            }
            WriteLine($"{answered}/{boards.Count} boards answered.", AccentBrush);
        });
    }

    /// <summary>One TCP call to host:port: whether it answered within <see cref="ProbeTimeout"/>, and how long it took.</summary>
    private static async Task<(bool Answered, long Milliseconds, string? Error)> ProbeAsync(
        string host, int port, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(ProbeTimeout);
        var clock = Stopwatch.StartNew();
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, timeout.Token);
            return (true, clock.ElapsedMilliseconds, null);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return (false, clock.ElapsedMilliseconds, "no answer");
        }
        catch (SocketException ex)
        {
            string why = ex.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => "refused",
                SocketError.HostNotFound or SocketError.NoData => "host not found",
                _ => ex.Message
            };
            return (false, clock.ElapsedMilliseconds, why);
        }
    }

    // ================================================================ system

    private void Crt(string args)
    {
        switch (args.ToLowerInvariant())
        {
            case "":
                WriteLine($"CRT mode is {(_host.CrtEnabled ? "ON" : "OFF")}.", TextBrush);
                break;
            case "on":
                _host.SetCrtEnabled(true);
                WriteLine("CRT mode ON.", SuccessBrush);
                break;
            case "off":
                _host.SetCrtEnabled(false);
                WriteLine("CRT mode OFF.", SuccessBrush);
                break;
            default:
                WriteLine("usage: crt [on|off]", ErrorBrush);
                break;
        }
    }

    private void Phosphor(string args)
    {
        string? mode = args.ToLowerInvariant() switch
        {
            "" => null,
            "color" => "Color",
            "green" => "Green",
            "amber" => "Amber",
            "white" => "White",
            _ => "?"
        };
        if (mode == null)
        {
            WriteLine($"Phosphor is {_host.Phosphor}.", TextBrush);
            return;
        }
        if (mode == "?")
        {
            WriteLine("usage: phosphor [color|green|amber|white]", ErrorBrush);
            return;
        }
        _host.SetPhosphor(mode);
        WriteLine($"Phosphor {mode}.", SuccessBrush);
    }

    private void Rank()
    {
        const int barWidth = 20;
        int calls = _host.TotalCalls;
        string current = Services.EliteRank.NameFor(calls);
        int currentMin = Services.EliteRank.All.Last(rank => calls >= rank.MinCalls).MinCalls;

        WriteLine("ELITE STATUS", AccentBrush);
        WriteLine($"  rank  : {current}  ({calls} call{(calls == 1 ? "" : "s")})", TextBrush);
        if (Services.EliteRank.NextFor(calls) is { } next)
        {
            double fraction = (double)(calls - currentMin) / (next.Calls - currentMin);
            int filled = (int)Math.Round(fraction * barWidth);
            WriteLine($"  next  : {next.Name} at {next.Calls} calls  [{new string('#', filled)}{new string('-', barWidth - filled)}]  " +
                      $"{next.Calls - calls} to go", TextBrush);
        }
        else
        {
            WriteLine($"  next  : none. You're a {current}. Respect.", SuccessBrush);
        }
        WriteLine("", TextBrush);

        foreach (var (minCalls, name) in Services.EliteRank.All)
        {
            string row = $"  {name,-8}{minCalls,4}+ calls";
            if (name == current)
                WriteLine($"{row}   <- you", AccentBrush);
            else
                WriteLine(row, minCalls < currentMin ? SuccessBrush : DimBrush);
        }
        WriteLine("", TextBrush);
        WriteLine("Every successful BBS connection counts.", DimBrush);
    }

    private void WhoAmI()
    {
        var last = RecentlyCalled().FirstOrDefault();
        WriteLine($"  handle    : {Environment.UserName}", TextBrush);
        int calls = _host.TotalCalls;
        string progress = Services.EliteRank.NextFor(calls) is { } next
            ? $", {next.Calls - calls} more to {next.Name}"
            : string.Empty;
        WriteLine($"  rank      : {Services.EliteRank.NameFor(calls)}  ({calls} call{(calls == 1 ? "" : "s")}{progress})", TextBrush);
        WriteLine($"  boards    : {_host.Boards.Count} in phonebook", TextBrush);
        WriteLine($"  last call : {(last == null ? "never" : $"{last.Name} ({last.LastConnected:yyyy-MM-dd HH:mm})")}", TextBrush);
        WriteLine($"  client    : TERMinator v{_host.VersionText}", TextBrush);
    }

    private void Uptime()
    {
        var up = DateTime.Now - Process.GetCurrentProcess().StartTime;
        WriteLine($"{DateTime.Now:HH:mm:ss} up {(int)up.TotalHours}:{up.Minutes:00}:{up.Seconds:00}, " +
                  $"{_host.Boards.Count} boards, CRT {(_host.CrtEnabled ? "ON" : "OFF")}", TextBrush);
    }

    private void Motd()
    {
        string greeting = DateTime.Now.Hour switch
        {
            < 5 => "Burning the midnight oil",
            < 12 => "Good morning",
            < 18 => "Good afternoon",
            _ => "Good evening"
        };
        WriteLine($"{greeting}, {Environment.UserName}. Welcome to TERMinator v{_host.VersionText}.", AccentBrush);

        var facts = HistoryTicker.AllFacts;
        if (facts.Count > 0)
            WriteLine("Fact of the day: " + facts[DateTime.Today.DayOfYear % facts.Count], TextBrush);
    }

    // ================================================================ fun

    private void Fact()
    {
        var facts = HistoryTicker.AllFacts;
        if (facts.Count > 0)
            WriteLine(facts[Random.Shared.Next(facts.Count)], TextBrush);
    }

    /// <summary>
    /// "boot": clears the screen and types the boot sequence into the console at the boot screen's own pace, then the
    /// prompt returns right under it. No glitch-out, nothing is cleared afterwards, and keys are ignored while it
    /// types (the input line is hidden until it's done).
    /// </summary>
    private async Task TypeBootSequence()
    {
        if (_booting)
            return;
        _booting = true;
        var lines = _host.BootLines;
        int totalUnits = BootSequenceOverlay.TotalUnits(lines);

        _output.Children.Clear();
        var screen = new TextBlock { FontFamily = ConsoleFont, FontSize = ConsoleFontSize, Foreground = TextBrush };
        _output.Children.Add(screen);
        _inputRow.IsVisible = false;

        var clock = Stopwatch.StartNew();
        while (true)
        {
            double progress = Math.Min(1.0, clock.Elapsed.TotalSeconds / BootSequenceOverlay.TypingDuration.TotalSeconds);
            string shown = BootSequenceOverlay.BuildScreen(lines, (int)Math.Round(progress * totalUnits));
            if (progress >= 1.0)
            {
                screen.Text = shown.TrimEnd('\n');
                break;
            }
            screen.Text = shown + "█";
            ScrollToEnd();
            await Task.Delay(15);
        }

        _booting = false;
        _inputRow.IsVisible = true;
        ScrollToEnd();
        FocusInput();
    }

    private void Hack(string args)
    {
        // Needs a real phonebook board: a number or a name
        if (args.Length == 0)
        {
            WriteLine("usage: hack <#|name>", ErrorBrush);
            return;
        }
        var board = ResolveBoard(args);
        if (board == null)
            return;
        string target = board.Name;
        string[] steps =
        [
            $"[*] target acquired: {target}",
            "[*] spoofing caller ID ... 555-0199",
            "[*] bypassing firewall  [#####-----]  50%",
            "[*] bypassing firewall  [##########] 100%",
            "[*] injecting ANSI payload ...",
            "[*] cracking sysop password: ********",
            "[!] The SysOp broke into chat: \"I can see you. Want to play a door game instead?\"",
            "[!] ACCESS DENIED. Maybe just call in and say hi."
        ];

        RunAsync(async token =>
        {
            for (int i = 0; i < steps.Length; i++)
            {
                await Task.Delay(i == 0 ? 150 : 420, token);
                WriteLine(steps[i], steps[i].StartsWith("[!]", StringComparison.Ordinal) ? ErrorBrush : TextBrush);
            }
        });
    }

    // ================================================================ input

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        bool alt = (e.KeyModifiers & KeyModifiers.Alt) != 0;

        // Model M click for every real keypress (not modifiers on their own), Alt combos included as on Windows.
        // Avalonia's key events don't flag auto-repeat, so a held key clicks on each repeat here.
        if (e.Key is not (Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt
                          or Key.RightAlt or Key.LWin or Key.RWin or Key.CapsLock))
            App.KeyClickService.Play(space: e.Key == Key.Space);

        // WPF hands Alt+key to the window as Key.System, so the Windows console never acts on one: Alt combos belong to
        // the window (Alt+Enter fullscreen, Alt+I login). Avalonia reports Alt+Enter as a plain Key.Enter, which would
        // otherwise run the typed line and swallow the shortcut, so leave every Alt combo alone.
        if (alt)
            return;

        bool ctrl = e.KeyModifiers == KeyModifiers.Control;
        switch (e.Key)
        {
            case Key.Enter:
                string line = _input.Text ?? string.Empty;
                _input.Clear();
                Execute(line);
                e.Handled = true;
                break;
            case Key.Tab:
                Complete();
                e.Handled = true;
                break;
            case Key.Up:
                RecallHistory(-1);
                e.Handled = true;
                break;
            case Key.Down:
                RecallHistory(+1);
                e.Handled = true;
                break;
            case Key.Escape:
                _input.Clear();
                e.Handled = true;
                break;
            case Key.L when ctrl:
                _output.Children.Clear();
                e.Handled = true;
                break;
            case Key.C when ctrl && _input.SelectionStart == _input.SelectionEnd:
                if (_busy != null)
                {
                    _busy.Cancel();
                }
                else
                {
                    Echo((_input.Text ?? string.Empty) + "^C");
                    _input.Clear();
                    _pendingConfirm = null;
                }
                e.Handled = true;
                break;
        }
    }

    private void Execute(string line)
    {
        Echo(line);
        string trimmed = line.Trim();

        // A y/n question is waiting: this line is the answer
        if (_pendingConfirm != null)
        {
            var confirm = _pendingConfirm;
            _pendingConfirm = null;
            confirm(trimmed.Equals("y", StringComparison.OrdinalIgnoreCase)
                    || trimmed.Equals("yes", StringComparison.OrdinalIgnoreCase));
            return;
        }

        if (trimmed.Length == 0)
            return;

        if (_history.Count == 0 || _history[^1] != trimmed)
            _history.Add(trimmed);
        _historyPosition = _history.Count;

        if (_busy != null)
        {
            WriteLine("busy. Press Ctrl+C to cancel.", DimBrush);
            return;
        }

        int space = trimmed.IndexOf(' ');
        string name = space < 0 ? trimmed : trimmed[..space];
        string args = space < 0 ? string.Empty : trimmed[(space + 1)..].Trim();

        var command = FindCommand(name);
        if (command == null)
        {
            WriteLine($"'{name}' is not a command. Type \"help\" for the list.", ErrorBrush);
            return;
        }

        try
        {
            command.Run(args);
        }
        catch (Exception ex)
        {
            WriteLine($"error: {ex.Message}", ErrorBrush);
        }
    }

    /// <summary>Runs a long command (network, animations) without blocking the console; Ctrl+C cancels it.</summary>
    private async void RunAsync(Func<CancellationToken, Task> work)
    {
        _busy = new CancellationTokenSource();
        try
        {
            await work(_busy.Token);
        }
        catch (OperationCanceledException)
        {
            WriteLine("^C  cancelled", DimBrush);
        }
        catch (Exception ex)
        {
            WriteLine($"error: {ex.Message}", ErrorBrush);
        }
        finally
        {
            _busy.Dispose();
            _busy = null;
        }
    }

    private void RecallHistory(int step)
    {
        if (_history.Count == 0)
            return;
        _historyPosition = Math.Clamp(_historyPosition + step, 0, _history.Count);
        SetInput(_historyPosition < _history.Count ? _history[_historyPosition] : string.Empty);
    }

    /// <summary>
    /// Tab: completes the command name, or the argument for commands that know their arguments. One match completes
    /// outright; several extend to their shared prefix, or are listed when there's nothing more to add.
    /// </summary>
    private void Complete()
    {
        string text = _input.Text ?? string.Empty;
        int space = text.IndexOf(' ');

        string head;
        string partial;
        IEnumerable<string> pool;
        if (space < 0)
        {
            head = string.Empty;
            partial = text;
            pool = CommandNames();
        }
        else
        {
            var command = FindCommand(text[..space]);
            if (command?.CompleteArgs == null)
                return;
            head = text[..(space + 1)];
            partial = text[(space + 1)..].TrimStart();
            pool = command.CompleteArgs(partial);
        }

        var candidates = pool
            .Where(c => c.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (candidates.Count == 0)
            return;

        if (candidates.Count == 1)
        {
            SetInput(head + candidates[0] + (space < 0 ? " " : string.Empty));
            return;
        }

        string common = CommonPrefix(candidates);
        if (common.Length > partial.Length)
        {
            SetInput(head + common);
            return;
        }

        Echo(text);
        WriteLine("  " + string.Join("   ", candidates), DimBrush);
    }

    private IEnumerable<string> DialCompletions(string args)
    {
        int space = args.IndexOf(' ');
        if (space < 0)
            return _host.Boards.Select(b => $"{b.Host}:{b.Port}");
        string target = args[..space];
        return Protocols.Select(p => $"{target} {p.Name}");
    }

    private IEnumerable<string> QuickConnectCompletions(string args)
    {
        int space = args.IndexOf(' ');
        if (space < 0)
            return ["1", "2", "3", "4"];
        string slot = args[..space];
        return _host.Boards.Select(b => $"{slot} {b.Name}").Append($"{slot} clear");
    }

    private IEnumerable<string> CommandNames() =>
        _commands.Where(c => !c.Hidden).SelectMany(c => c.Aliases.Prepend(c.Name)).Where(n => n != "?");

    private Command? FindCommand(string name) =>
        _commands.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                                      || c.Aliases.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase)));

    private void SetInput(string text)
    {
        _input.Text = text;
        _input.CaretIndex = text.Length;
    }

    // ================================================================ lookups

    /// <summary>
    /// A board by 1-based number ("3" or "#3"), exact name, or unique partial name. With <paramref name="report"/> it
    /// explains why nothing matched.
    /// </summary>
    private SavedConnection? ResolveBoard(string query, bool report = true)
    {
        var boards = _host.Boards;
        if (query.Length == 0)
        {
            if (report)
                WriteLine("Which board? Give a number or a name (see \"list\").", ErrorBrush);
            return null;
        }

        if (int.TryParse(query.TrimStart('#'), out int number))
        {
            if (number >= 1 && number <= boards.Count)
                return boards[number - 1];
            if (report)
                WriteLine($"There is no board #{number}. The phonebook has {boards.Count}.", ErrorBrush);
            return null;
        }

        var exact = boards.Where(b => b.Name.Equals(query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1)
            return exact[0];

        var matches = boards.Where(b => b.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
            matches = boards.Where(b => b.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 1)
            return matches[0];

        if (!report)
            return null;
        if (matches.Count == 0)
        {
            WriteLine($"No board matches \"{query}\".", ErrorBrush);
        }
        else
        {
            WriteLine($"\"{query}\" matches {matches.Count} boards:", ErrorBrush);
            foreach (var board in matches.Take(10))
                WriteLine($"  {board.Name}", DimBrush);
        }
        return null;
    }

    private SavedConnection? FindByAddress(string host, int port) =>
        _host.Boards.FirstOrDefault(b => b.Port == port && b.Host.Equals(host, StringComparison.OrdinalIgnoreCase));

    private IEnumerable<SavedConnection> RecentlyCalled() =>
        _host.Boards.Where(b => b.LastConnected != default).OrderByDescending(b => b.LastConnected);

    private void About()
    {
        WriteLine("TERMinator - BBS Terminal", AccentBrush);
        WriteLine($"Version {_host.VersionText}", TextBrush);
        WriteLine("", TextBrush);
        WriteLine("─── Credits ───", AccentBrush);
        WriteLine("", TextBrush);
        foreach (var line in Services.AppInfoText.CreditsLines())
            WriteLine(line, TextBrush);
    }

    private void Changelog()
    {
        WriteLine("TERMinator - BBS Terminal", AccentBrush);
        WriteLine($"Version {_host.VersionText}", TextBrush);
        WriteLine("", TextBrush);
        WriteLine("─── What's New ───", AccentBrush);
        WriteLine("", TextBrush);
        foreach (var line in Services.AppInfoText.Changelog)
            WriteLine(line, line.StartsWith('v') ? AccentBrush : TextBrush);
    }

    // ================================================================ output

    private void Echo(string text)
    {
        _output.Children.Add(CreatePromptBlock(text));
        TrimAndScroll();
    }

    private void WriteLine(string text, IBrush brush)
    {
        _output.Children.Add(new TextBlock
        {
            // An empty TextBlock collapses to nothing in Avalonia, so a blank line is written as a space
            Text = text.Length == 0 ? " " : text,
            FontFamily = ConsoleFont,
            FontSize = ConsoleFontSize,
            Foreground = brush,
            TextWrapping = TextWrapping.Wrap
        });
        TrimAndScroll();
    }

    private void TrimAndScroll()
    {
        while (_output.Children.Count > MaxOutputLines)
            _output.Children.RemoveAt(0);

        // The screen effect draws over the console, so it has to repaint when the console does
        if (!_screenEffect.IsIdle)
            _screenEffect.InvalidateVisual();
        ScrollToEnd();
    }

    /// <summary>Auto-scroll. Posted as well as called, because the new line has no size until layout has run.</summary>
    private void ScrollToEnd()
    {
        _scroll.ScrollToEnd();
        Dispatcher.UIThread.Post(() => _scroll.ScrollToEnd(), DispatcherPriority.Loaded);
    }

    /// <summary>The prompt, optionally followed by an echoed command line.</summary>
    private static TextBlock CreatePromptBlock(string? echo)
    {
        var block = new TextBlock
        {
            FontFamily = ConsoleFont,
            FontSize = ConsoleFontSize,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        block.Inlines!.Add(new Run("TERM") { Foreground = PromptTermBrush, FontWeight = FontWeight.Bold });
        block.Inlines!.Add(new Run("inator") { Foreground = PromptInatorBrush, FontWeight = FontWeight.Bold });
        block.Inlines!.Add(new Run("> ") { Foreground = PromptArrowBrush, FontWeight = FontWeight.Bold });
        if (echo != null)
            block.Inlines!.Add(new Run(echo) { Foreground = TextBrush });
        return block;
    }

    // ================================================================ helpers

    /// <summary>
    /// A style that flattens the input TextBox's themed border element, optionally only in one state
    /// (<c>:pointerover</c>, <c>:focus</c>), so the console's prompt line never grows any chrome.
    /// </summary>
    private static Style FlatInputChrome(string? pseudoClass)
    {
        var style = new Style(x =>
        {
            Selector selector = x.OfType<TextBox>();
            if (pseudoClass != null)
                selector = selector.Class(pseudoClass);
            return selector.Template().OfType<Border>().Name("PART_BorderElement");
        });
        style.Setters.Add(new Setter(Border.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(0)));
        return style;
    }

    private static string ProtocolName(ConnectionProtocol protocol) =>
        Protocols.FirstOrDefault(p => p.Protocol == protocol).Name ?? protocol.ToString().ToLowerInvariant();

    /// <summary>Only the names in <see cref="Protocols"/>: Enum.TryParse would also accept numbers such as a port.</summary>
    private static bool TryParseProtocol(string text, out ConnectionProtocol protocol)
    {
        foreach (var (name, value) in Protocols)
        {
            if (name.Equals(text, StringComparison.OrdinalIgnoreCase))
            {
                protocol = value;
                return true;
            }
        }
        protocol = ConnectionProtocol.Telnet;
        return false;
    }

    /// <summary>[user@]host[:port], with the protocol's default port when none is given.</summary>
    private static bool TryParseTarget(string text, ConnectionProtocol protocol, out string host, out int port,
                                       out string? username)
    {
        username = null;
        int at = text.LastIndexOf('@');
        if (at > 0)
        {
            username = text[..at];
            text = text[(at + 1)..];
        }

        port = 0;
        if (!TrySplitHostPort(text, out host, out var parsedPort))
            return false;
        port = parsedPort ?? SavedConnection.GetDefaultPort(protocol);
        return true;
    }

    /// <summary>host, host:port, or [ipv6]:port.</summary>
    private static bool TrySplitHostPort(string text, out string host, out int? port)
    {
        host = text;
        port = null;

        if (text.StartsWith('['))
        {
            int close = text.IndexOf(']');
            if (close < 0)
                return false;
            host = text[1..close];
            string rest = text[(close + 1)..];
            if (rest.Length == 0)
                return host.Length > 0;
            if (!rest.StartsWith(':') || !int.TryParse(rest[1..], out int bracketPort) || bracketPort is < 1 or > 65535)
                return false;
            port = bracketPort;
            return host.Length > 0;
        }

        int colon = text.LastIndexOf(':');
        if (colon > 0 && text.IndexOf(':') == colon)
        {
            if (!int.TryParse(text[(colon + 1)..], out int parsed) || parsed is < 1 or > 65535)
                return false;
            host = text[..colon];
            port = parsed;
        }
        return host.Length > 0;
    }

    private static string CommonPrefix(IReadOnlyList<string> values)
    {
        string first = values[0];
        int length = first.Length;
        foreach (var value in values.Skip(1))
        {
            length = Math.Min(length, value.Length);
            for (int i = 0; i < length; i++)
            {
                if (char.ToLowerInvariant(first[i]) != char.ToLowerInvariant(value[i]))
                {
                    length = i;
                    break;
                }
            }
        }
        return first[..length];
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)] + "…";
}
