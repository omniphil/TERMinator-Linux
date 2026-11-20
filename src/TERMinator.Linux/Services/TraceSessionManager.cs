using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using TERMinator.Linux.Controls;

namespace TERMinator.Linux.Services;

/// <summary>
/// Runs the TRACE (TERMinator Render And Compute Engine) modules a door asks for during a BBS session. The door sends
/// APC commands in the terminal stream, which the native bridge queues (native/trace_bridge.c):
///   Open;module=fractal;wasm=&lt;sha256&gt;;size=&lt;bytes&gt;;rect=1,1,23,79[;exclusive=1]
///                                            run the door's WebAssembly module (sandboxed) over those terminal cells
///                                            (rows/cols, 1-based). Cached: reply Ready. Not cached: reply Need, and the
///                                            door uploads it with Put;module=fractal;offset=&lt;n&gt;;data=&lt;base64&gt; chunks
///                                            then PutDone;module=fractal; TRACE checks the SHA-256, caches and runs it.
///   Data;module=fractal;&lt;payload&gt;          pass the payload to the module (the fractal viewport); with
///                                            "b64=&lt;base64&gt;" instead, the payload is binary. A module sends bytes
///                                            back the same way: TERMinator relays them as Data;module=&lt;id&gt;;b64=...
///   Asset;module=doom;sha256=&lt;hex&gt;;size=&lt;n&gt;  an asset the module will read (a WAD): reply Have when it's already
///                                            cached, else NeedAsset and the door uploads it with the same Put /
///                                            PutDone chunks, plus asset=&lt;hex&gt;
///   Put;...;enc=gz                           (Query reply gz=1) the chunks are a gzip stream of the module or asset:
///                                            offsets count the compressed bytes, every chunk of the upload carries
///                                            enc=gz, and TRACE inflates it (never past the declared size) before the
///                                            SHA-256 check. Size and sha256 in Open/Asset stay the real file's.
///   Import;module=dosbox;sha256=&lt;hex&gt;[,&lt;hex&gt;...];name=&lt;label&gt;
///                                            the player's OWN copy of a file (a game they bought, a ROM): the door
///                                            never sends it. Already imported and this board allowed: Have. Else the
///                                            player is asked (pick the file, or allow this board to use one they
///                                            imported before); TRACE checks the hash, keeps it like an asset and
///                                            replies Have;module=..;sha256=&lt;the one that matched&gt;, or
///                                            NoImport;module=..;reason=declined|mismatch|disabled|busy|bad-import
///   Bin frames (Query reply bin=1): ESC _ TERMinator:TRACE;Bin;&lt;escaped&gt; ESC \ carry "Data;module=x\n&lt;raw&gt;" or
///                                            "Put;module=x[;asset=h];offset=n\n&lt;raw chunk&gt;" with no base64 (native
///                                            trace_bridge.c lifts them out of the stream; HandleBinary). A door that
///                                            opens a module with bin=1 gets the module's messages back the same way.
///   OpenUrl;module=x;url=&lt;base64&gt;        (Query reply url=1) open an http/https address (at most 2048 bytes) in the
///                                            player's browser: a link clicked in the module. Only within 3 s of a
///                                            click or key in that module, one per click (EngineModuleHost.TakeGesture)
///   Paste;module=x                           (Query reply paste=1) paste the clipboard into the module (TE_IN_PASTE),
///                                            as Ctrl+V does, for a Paste item in its own menu: text input on, and the
///                                            same click-or-key rule, so a board can't read the clipboard unasked
///   Hide|Show;module=fractal                 hide the picture (e.g. while the door shows ANSI help) or show it again.
///                                            (Query reply keep=1, 1.1.6) a hidden module keeps running without the
///                                            keyboard, mouse or gamepads; an Open of it answers Ready;resumed=1 at
///                                            once, the module as it was. Closed;code=0;error=expired after 10 minutes
///                                            hidden, or when another module needs its place
///   Close;module=fractal                     stop the module
/// A drawing module (fractal) sits over its terminal cells and the keyboard stays with the terminal, so the door keeps
/// reading keys. An exclusive module (doom) fills the window and takes the keyboard until it quits. Any module can also
/// turn text input on (trace_text_input), which gives it the keyboard and typed characters until it turns it off, and
/// pick a mouse mode (trace_mouse_mode; EngineHostLayer.Mouse.cs). Gamepads (GamepadService) go to every running module.
/// When a module ends
/// by itself, or can't start, the door is told: ESC _ TERMinator:TRACE;Closed;module=&lt;id&gt;;code=&lt;n&gt;[;error=...] ESC \.
/// Pictures sit in MainWindow's EngineHost layer, separate from TAP+. Everything runs on the UI thread.
/// Linux port of the Windows client's Services/TraceSessionManager.cs (the master): same protocol, replies and limits.
/// </summary>
public sealed class TraceSessionManager
{
    private const int MaxSessions = 2;

    private sealed class Session
    {
        public required TraceModules.Module Module;
        public required EngineModuleHost Host;
        public required EngineHostLayer Layer;
        public int Row1, Col1, Row2, Col2;
        public bool Hidden;
        public DispatcherTimer? HiddenTimer;   // closes a hidden module (keep=1) in time
        /// <summary>The door opened it with bin=1: its messages go back as binary frames, not base64.</summary>
        public bool Binary;
    }

    private readonly Panel _layerHost;
    private readonly Func<TerminalCanvas?> _terminal;
    private readonly Action<byte[]> _sendToBoard;
    // What modules and replies send the door, in order, sent off the UI thread: conn_send can wait up to a second
    // for room in the link's buffer. Made on the first send and finished by CloseAll (disconnect).
    private BlockingCollection<byte[]>? _sendQueue;
    private readonly Action _exclusiveEnded;
    private readonly Func<bool> _enabled;
    private readonly Func<int> _cacheLimitMb;
    private readonly Func<float> _audioVolume;
    private readonly Func<string?> _bbsName;
    private readonly Func<string?> _bbsKey;

    /// <summary>An Import is waiting on the player (only one question at a time).</summary>
    private bool _importAsking;
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gamepads, polled while any module runs; their events go to every running module.</summary>
    private readonly GamepadService _pads = new();

    /// <summary>The running modules, for the gamepad thread (replaced, never changed, on the UI thread).</summary>
    private volatile EngineModuleHost[] _padTargets = [];

    private Window? _window;

    /// <summary>A module or an asset being uploaded, and (for a module) the Open that asked for it.</summary>
    private sealed class Upload
    {
        public required string Sha256;
        public required int Size;
        public required string OpenArgs;
        public required MemoryStream Data;
        /// <summary>An asset (a WAD or similar) rather than the module itself.</summary>
        public bool IsAsset;
        /// <summary>
        /// Its Puts carry enc=gz (TraceGzip): Data is the gzip stream, offsets count it, and FinishUpload inflates
        /// it to Size bytes before the hash check. Set by the first chunk; every later one must agree.
        /// </summary>
        public bool Gzip;
    }

    private readonly Dictionary<string, Upload> _uploads = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Assets each module has been given (cached paths), so the cache limit never drops one in use.</summary>
    private readonly Dictionary<string, List<string>> _assets = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>An upload is keyed by the module, or by module + hash for an asset (several can be in flight).</summary>
    private static string UploadKey(string id, string? assetHash) => assetHash == null ? id : id + "#" + assetHash;

    /// <param name="layerHost">MainWindow's EngineHost panel, over the content area.</param>
    /// <param name="terminal">The terminal canvas while a session is on screen, else null (TRACE then closes).</param>
    /// <param name="sendToBoard">Sends bytes to the BBS (replies to the door).</param>
    /// <param name="exclusiveEnded">
    /// A module gave the keyboard back (an exclusive one ended, or text input went off): return it to the terminal.
    /// </param>
    /// <param name="enabled">Whether TRACE is switched on in Preferences.</param>
    /// <param name="cacheLimitMb">The module cache limit from Preferences.</param>
    /// <param name="audioVolume">The volume module sound is mixed at (0 = doors may not make sound).</param>
    /// <param name="bbsName">The board this session is with, recorded beside what its doors cache (Settings lists it).</param>
    /// <param name="bbsKey">The board as host:port, which the player's permissions for imported files are kept by.</param>
    public TraceSessionManager(Panel layerHost, Func<TerminalCanvas?> terminal, Action<byte[]> sendToBoard,
                               Action exclusiveEnded, Func<bool> enabled, Func<int> cacheLimitMb,
                               Func<float> audioVolume, Func<string?> bbsName, Func<string?> bbsKey)
    {
        _bbsName = bbsName;
        _bbsKey = bbsKey;
        _audioVolume = audioVolume;
        _enabled = enabled;
        _cacheLimitMb = cacheLimitMb;
        _layerHost = layerHost;
        _terminal = terminal;
        _sendToBoard = sendToBoard;
        _exclusiveEnded = exclusiveEnded;
        _layerHost.LayoutUpdated += (_, _) => UpdatePlacement();
        _pads.DeviceChanged += (pad, connected) =>
        {
            foreach (var host in _padTargets)
                host.SendPadDevice(pad, connected);
        };
        _pads.ButtonChanged += (pad, button, pressed) =>
        {
            foreach (var host in _padTargets)
                host.SendPadButton(pad, button, pressed);
        };
        _pads.AxisChanged += (pad, axis, value) =>
        {
            foreach (var host in _padTargets)
                host.SendPadAxis(pad, axis, value);
        };
    }

    /// <summary>
    /// Keeps the gamepad thread's list of modules current, and polls the pads only while a module runs. Pads read
    /// as released while TERMinator isn't the active window.
    /// </summary>
    private void UpdatePadTargets()
    {
        _padTargets = [.. _sessions.Values.Where(s => !s.Hidden).Select(s => s.Host)];
        if (_window == null && TopLevel.GetTopLevel(_layerHost) is Window window)
        {
            _window = window;
            window.Activated += (_, _) => _pads.Active = true;
            window.Deactivated += (_, _) => _pads.Active = false;
            _pads.Active = window.IsActive;
        }
        if (_sessions.Count > 0)
            _pads.Start();
        else
            _pads.Stop();
    }

    /// <summary>True while a module that owns the keyboard (a game) is running and showing (a hidden one, kept
    /// running for the board, has given the keyboard back).</summary>
    public bool HasExclusive => _sessions.Values.Any(s => s.Module.Exclusive && !s.Hidden);

    /// <summary>A showing module has text input on: typed characters go to it (HandleText).</summary>
    public bool TakesText => _sessions.Values.Any(s => !s.Hidden && s.Layer.TextInputOn);

    /// <summary>
    /// True while a module has the keyboard: an exclusive one, or any with text input on. MainWindow then keeps
    /// every key away from the controls behind it.
    /// </summary>
    public bool OwnsKeyboard => _sessions.Values.Any(s => !s.Hidden && (s.Module.Exclusive || s.Layer.TextInputOn));

    private void TellDoorClosed(string id, int code, string? error = null) =>
        TellDoor($"Closed;module={id};code={code}{(error != null ? $";error={error}" : "")}");

    private void TellDoor(string message) =>
        SendToBoard(Encoding.ASCII.GetBytes($"\x1b_TERMinator:TRACE;{message}\x1b\\"));

    /// <summary>Queues bytes for the board; one worker sends them in order (UI thread only).</summary>
    private void SendToBoard(byte[] bytes)
    {
        if (_sendQueue == null)
        {
            var queue = new BlockingCollection<byte[]>();
            var send = _sendToBoard;
            new Thread(() =>
            {
                // Ends once CloseAll finishes the queue and what was already in it has gone (or failed: with the
                // link down the bridge refuses straight away)
                foreach (var pending in queue.GetConsumingEnumerable())
                {
                    try
                    {
                        send(pending);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"TRACE: send to the door failed: {ex.Message}");
                    }
                }
            })
            { IsBackground = true, Name = "TRACE door sends" }.Start();
            _sendQueue = queue;
        }
        _sendQueue.Add(bytes);
    }

    /// <summary>Lets the send worker finish what's queued and stop; the next send starts a new one.</summary>
    private void StopSending()
    {
        _sendQueue?.CompleteAdding();
        _sendQueue = null;
    }

    private static Dictionary<string, string> ParseFields(string args) => args.Split(';')
        .Select(f => f.Split('=', 2))
        .Where(kv => kv.Length == 2)
        .GroupBy(kv => kv[0], StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.First()[1], StringComparer.OrdinalIgnoreCase);

    /// <summary>One queued command, without its "TERMinator:TRACE;" prefix. Unknown or malformed commands are ignored.</summary>
    public void Handle(string command)
    {
        // Verb;module=<id>[;rest] -- for Data the rest is the module's payload, passed on untouched
        var parts = command.Split(';', 3);
        if (parts.Length < 2 || !parts[1].StartsWith("module=", StringComparison.OrdinalIgnoreCase))
            return;
        string verb = parts[0];
        string id = parts[1]["module=".Length..];
        string rest = parts.Length > 2 ? parts[2] : "";
        _sessions.TryGetValue(id, out var session);

        switch (verb)
        {
            case "Open":
                Open(id, session, rest);
                break;
            case "Data" when session != null:
                // Text as the door wrote it, or "b64=<base64>" when it's binary (a game's state updates)
                byte[] payload = rest.StartsWith("b64=", StringComparison.OrdinalIgnoreCase)
                    ? FromBase64(rest[4..])
                    : Encoding.UTF8.GetBytes(rest);
                if (payload.Length is > 0 and <= ushort.MaxValue)
                    session.Host.SendData(payload);
                break;
            case "OpenUrl" when session != null:
                OpenUrl(session, ParseFields(rest).GetValueOrDefault("url"));
                break;
            case "Paste" when session != null:
                if (!session.Hidden && session.Layer.TextInputOn && session.Host.TakeGesture())
                    session.Layer.PasteFromClipboard();
                break;
            case "Hide" when session != null:
                Hide(session);
                break;
            case "Show" when session != null:
                Unhide(session);
                UpdatePlacement();
                break;
            case "Close" when session != null:
                // hold=1: Rainwire closed it only to run a transfer (it reopens after): keep its last frame up,
                // dimmed, until it does. Unknown keys are ignored.
                if (ParseFields(rest).GetValueOrDefault("hold") == "1")
                    Hold(session);
                Close(session);
                break;
            case "Close":
                foreach (var key in _uploads.Keys.Where(k => k == id || k.StartsWith(id + "#", StringComparison.OrdinalIgnoreCase)).ToList())
                    _uploads.Remove(key);   // closed before its module or assets finished uploading
                break;
            case "Asset":
                OfferAsset(id, rest);
                break;
            case "Import":
                _ = ImportAsync(id, rest);
                break;
            case "Put":
                ReceiveChunk(id, rest);
                break;
            case "PutDone":
                FinishUpload(id, ParseFields(rest).GetValueOrDefault("asset"));
                break;
        }
    }

    /// <summary>
    /// A door's OpenUrl: the address (base64 UTF-8) opens in the player's browser when it's http or https, at most
    /// <see cref="OscExtras.UriMax"/> bytes with no spaces or control characters, and the player clicked or pressed
    /// a key in that module in the last few seconds (used up, so one click opens one page). Anything else is
    /// dropped without a word.
    /// </summary>
    private static void OpenUrl(Session session, string? b64)
    {
        if (string.IsNullOrEmpty(b64) || b64.Length > OscExtras.UriMax * 2 || session.Hidden)
            return;
        string url;
        try
        {
            url = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        }
        catch (FormatException)
        {
            return;
        }
        if (!IsWebUrl(url) || !session.Host.TakeGesture())
            return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // no browser registered: nothing to do
        }
    }

    /// <summary>An http or https address with a host, no user name, no spaces or control characters, not too long.</summary>
    public static bool IsWebUrl(string url)
    {
        if (url.Length == 0 || Encoding.UTF8.GetByteCount(url) > OscExtras.UriMax || url.Any(c => c <= ' ' || char.IsControl(c)))
            return false;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
               && uri.Host.Length > 0 && uri.UserInfo.Length == 0;
    }

    /// <summary>
    /// A binary frame from the door: a header line (the command, as in a text command) then the raw payload. Data
    /// goes to the module and Put is an upload chunk, both without base64; anything else is handled as its header.
    /// </summary>
    public void HandleBinary(byte[] frame)
    {
        int newline = Array.IndexOf(frame, (byte)'\n');
        string header = Encoding.ASCII.GetString(frame, 0, newline < 0 ? frame.Length : newline);
        var payload = newline < 0 ? ReadOnlySpan<byte>.Empty : frame.AsSpan(newline + 1);
        var parts = header.Split(';', 3);
        if (parts.Length < 2 || !parts[1].StartsWith("module=", StringComparison.OrdinalIgnoreCase))
            return;
        string id = parts[1]["module=".Length..];
        string rest = parts.Length > 2 ? parts[2] : "";
        switch (parts[0])
        {
            case "Data":
                if (_sessions.TryGetValue(id, out var session) && payload.Length is > 0 and <= ushort.MaxValue)
                    session.Host.SendData(payload);
                break;
            case "Put":
                ReceiveChunk(id, ParseFields(rest), payload.ToArray());
                break;
            default:
                Handle(header);
                break;
        }
    }

    /// <summary>
    /// Wraps bytes as a binary frame for the door: '=' and the byte + 64 stands for a byte that mustn't travel raw,
    /// the escaping the door uses towards us (trace_bridge.c). The door escapes only the nine bytes a link could touch
    /// on the way here; this way every control byte is escaped too, since a door often reads through a pseudo-terminal
    /// that would act on Ctrl-C, Ctrl-Z or XON/XOFF or turn CR into LF. It's the small direction (input, saves), so
    /// the few percent extra don't matter. The decoding is the same either way.
    /// </summary>
    private static byte[] BinaryFrame(string header, ReadOnlySpan<byte> payload)
    {
        var frame = new List<byte>(header.Length + payload.Length + payload.Length / 16 + 32);
        frame.AddRange(Encoding.ASCII.GetBytes("\x1b_TERMinator:TRACE;Bin;"));
        void Put(byte b)
        {
            if (b < 0x20 || b is (byte)'=' or 0x7F or 0xFF)
            {
                frame.Add((byte)'=');
                frame.Add((byte)(b + 64));
            }
            else
            {
                frame.Add(b);
            }
        }
        foreach (byte b in Encoding.ASCII.GetBytes(header + "\n"))
            Put(b);
        foreach (byte b in payload)
            Put(b);
        frame.Add(0x1B);
        frame.Add((byte)'\\');
        return [.. frame];
    }

    private static byte[] FromBase64(string text)
    {
        try { return Convert.FromBase64String(text); } catch (FormatException) { return []; }
    }

    /// <summary>Every cached file a running module might still be reading, so the cache limit leaves them alone.</summary>
    private string?[] InUse(params string?[] extra) => [.. extra, .. _assets.Values.SelectMany(a => a)];

    /// <summary>
    /// The cached files running modules are using (their .wasm and the assets they were given), which Settings
    /// won't delete: a module reads its assets while it runs.
    /// </summary>
    public IReadOnlyCollection<string> FilesInUse =>
        [.. _sessions.Values.Select(s => s.Module.CachePath).OfType<string>(), .. _assets.Values.SelectMany(a => a)];

    /// <summary>
    /// The door offers an asset its module will read (a WAD, a tileset). Cached already: say so and the door moves
    /// on. Otherwise it uploads it in the same chunks as a module, and the hash is checked the same way.
    /// </summary>
    private void OfferAsset(string id, string args)
    {
        var fields = ParseFields(args);
        string hash = fields.GetValueOrDefault("sha256", "").ToLowerInvariant();
        bool validHash = hash.Length == 64 && hash.All(Uri.IsHexDigit);
        bool validSize = int.TryParse(fields.GetValueOrDefault("size"), out int size)
                         && size > 0 && size <= TraceModules.MaxAssetBytes;
        if (!_enabled() || !validHash || !validSize)
        {
            TellDoorClosed(id, -1, !_enabled() ? "disabled" : "bad-asset");
            return;
        }
        string path = TraceModules.CachedAssetPath(hash);
        var file = new FileInfo(path);
        // A file the player imported is only this board's to use if they said so (Import asks); otherwise it's as
        // if it weren't here, so a door can't find out what the player owns by offering its hash
        bool hidden = file.Exists && TraceModules.IsImported(path) && !TraceModules.IsGranted(path, _bbsKey() ?? "");
        if (file.Exists && file.Length == size && !hidden)
        {
            KeepAsset(id, path);
            TellDoor($"Have;module={id};sha256={hash}");
            return;
        }
        _uploads[UploadKey(id, hash)] = new Upload
        {
            Sha256 = hash, Size = size, OpenArgs = "", Data = new MemoryStream(), IsAsset = true
        };
        TellDoor($"NeedAsset;module={id};sha256={hash}");
    }

    /// <summary>
    /// Remembers that this module uses an asset, marks the file as recently used for the cache limit, and records
    /// what it is beside it (Settings lists it).
    /// </summary>
    private void KeepAsset(string id, string path)
    {
        if (!_assets.TryGetValue(id, out var list))
            _assets[id] = list = [];
        if (!list.Contains(path, StringComparer.OrdinalIgnoreCase))
            list.Add(path);
        try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        TraceModules.RecordUse(path, id, isAsset: true, _bbsName());
    }

    /// <summary>
    /// The door asks for the player's own copy of a file (Import). Nothing crosses the link: the player picks the
    /// file, TRACE checks its hash against what the door expects and keeps it like an asset, so the module reads it
    /// by hash. Every board is asked about once per file, and the answer is kept (Settings lists and deletes them).
    /// </summary>
    private async Task ImportAsync(string id, string args)
    {
        var fields = ParseFields(args);
        var hashes = fields.GetValueOrDefault("sha256", "").ToLowerInvariant().Split(',')
            .Where(h => h.Length == 64 && h.All(Uri.IsHexDigit)).Distinct().Take(16).ToList();
        string name = new string(fields.GetValueOrDefault("name", "").Where(c => !char.IsControl(c)).Take(60).ToArray()).Trim();
        if (name.Length == 0)
            name = "a file";
        if (!_enabled())
        {
            TellDoor($"NoImport;module={id};reason=disabled");
            return;
        }
        if (hashes.Count == 0)
        {
            TellDoor($"NoImport;module={id};reason=bad-import");
            return;
        }
        if (_importAsking)
        {
            TellDoor($"NoImport;module={id};reason=busy");
            return;
        }
        string bbsKey = _bbsKey() ?? "unknown";
        string board = _bbsName() ?? bbsKey;

        _importAsking = true;
        try
        {
            // Imported before: this board may use it if the player allowed it, or says so now
            foreach (var hash in hashes)
            {
                string path = TraceModules.CachedAssetPath(hash);
                if (!File.Exists(path) || !TraceModules.IsImported(path))
                    continue;
                if (!TraceModules.IsGranted(path, bbsKey)
                    && !await Ask("USE YOUR FILE?",
                                  $"{board} would like its door to use your copy of {TraceModules.ImportedName(path) ?? name}, " +
                                  "which you imported earlier.\n\nThe file stays on your PC: the board never receives it.",
                                  "ALLOW", "DON'T ALLOW"))
                {
                    TellDoor($"NoImport;module={id};reason=declined");
                    return;
                }
                TraceModules.RecordImport(path, name, bbsKey, board);
                KeepAsset(id, path);
                TellDoor($"Have;module={id};sha256={hash}");
                return;
            }

            // Not here yet: ask the player for their copy
            string question = $"{board} would like to use your own copy of {name}.\n\n" +
                              "Choose the file and TERMinator checks it is the right one. It stays on your PC: " +
                              "the board never receives it.";
            while (true)
            {
                if (!await Ask("USE YOUR OWN FILE", question, "CHOOSE FILE...", "NOT NOW"))
                {
                    TellDoor($"NoImport;module={id};reason=declined");
                    return;
                }
                var storage = TopLevel.GetTopLevel(_layerHost)?.StorageProvider;
                if (storage == null)
                {
                    TellDoor($"NoImport;module={id};reason=declined");
                    return;
                }
                var picked = await storage.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
                {
                    Title = $"Choose your copy of {name}",
                    AllowMultiple = false,
                });
                // The file has to be one we can read by path: hashing a 1 GB stream through the picker's
                // abstraction would be slower, and a module reads it from the cache anyway
                string? chosen = picked.Count > 0 && picked[0].Path.IsFile ? picked[0].Path.LocalPath : null;
                if (string.IsNullOrEmpty(chosen))
                    continue;   // back to the question
                var (hash, kept) = await TraceModules.ImportFileAsync(chosen, hashes);
                if (kept && hash != null)
                {
                    string path = TraceModules.CachedAssetPath(hash);
                    TraceModules.RecordImport(path, name, bbsKey, board);
                    KeepAsset(id, path);
                    TellDoor($"Have;module={id};sha256={hash}");
                    return;
                }
                question = hash == null
                    ? $"That file couldn't be read, or is over {TraceModules.MaxImportBytes >> 20} MB.\n\n" +
                      $"{board} is asking for your own copy of {name}."
                    : $"That file isn't the copy of {name} this door knows (it may be a different version or " +
                      $"release).\n\nTry another file?";
            }
        }
        finally
        {
            _importAsking = false;
        }
    }

    /// <summary>A question for the player in TERMinator's own dialog style: true for yes, false for no.</summary>
    private async Task<bool> Ask(string title, string message, string yes, string no)
    {
        var owner = TopLevel.GetTopLevel(_layerHost) as Window;
        var dlg = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#FF0D0D1A")),
            Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#FFCCCCCC")),
        };
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 16,
            FontWeight = Avalonia.Media.FontWeight.Bold,
            Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#FFFFAA00")),
            Margin = new Thickness(0, 0, 0, 12),
        });
        panel.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 20),
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var yesButton = new Button { Content = yes, MinWidth = 100, Padding = new Thickness(10, 4), IsDefault = true, Margin = new Thickness(0, 0, 10, 0) };
        var noButton = new Button { Content = no, MinWidth = 100, Padding = new Thickness(10, 4), IsCancel = true };
        yesButton.Click += (_, _) => dlg.Close(true);
        noButton.Click += (_, _) => dlg.Close(false);
        buttons.Children.Add(yesButton);
        buttons.Children.Add(noButton);
        panel.Children.Add(buttons);
        dlg.Content = panel;
        dlg.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape)
                dlg.Close(false);
        };
        object? answer = owner != null ? await dlg.ShowDialog<object?>(owner) : null;
        return answer is true;
    }

    private void ReceiveChunk(string id, string args)
    {
        var fields = ParseFields(args);
        ReceiveChunk(id, fields, FromBase64(fields.GetValueOrDefault("data", "")));
    }

    /// <summary>One upload chunk, from a text Put (base64) or a binary one (raw).</summary>
    private void ReceiveChunk(string id, Dictionary<string, string> fields, byte[] chunk)
    {
        string key = UploadKey(id, fields.GetValueOrDefault("asset")?.ToLowerInvariant());
        if (!_uploads.TryGetValue(key, out var upload))
            return;
        // enc=gz (offered as gz=1): the chunks are a gzip stream of the file; any other encoding is one we don't know
        string? enc = fields.GetValueOrDefault("enc");
        bool gzip = enc == "gz";
        long limit = gzip ? TraceGzip.MaxCompressed(upload.Size) : upload.Size;
        // Chunks must arrive in order, all in the same encoding, and never run past the size the door declared
        if (!long.TryParse(fields.GetValueOrDefault("offset"), out long offset) || offset != upload.Data.Length
            || chunk.Length == 0 || upload.Data.Length + chunk.Length > limit
            || (enc != null && !gzip) || (offset > 0 && gzip != upload.Gzip))
        {
            _uploads.Remove(key);
            TellDoorClosed(id, -1, "bad-upload");
            return;
        }
        upload.Gzip = gzip;
        upload.Data.Write(chunk);
    }

    private void FinishUpload(string id, string? assetHash)
    {
        string key = UploadKey(id, assetHash?.ToLowerInvariant());
        if (!_uploads.Remove(key, out var upload))
            return;
        // A gzip upload is inflated first (never past the declared size), then checked like any other
        byte[]? data = upload.Gzip ? TraceGzip.Gunzip(upload.Data.ToArray(), upload.Size) : upload.Data.ToArray();
        string hash = data == null ? "" : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();
        if (data == null || data.Length != upload.Size || hash != upload.Sha256)
        {
            TellDoorClosed(id, -1, upload.IsAsset ? "bad-asset" : "bad-module");
            return;
        }
        string path = upload.IsAsset ? TraceModules.CachedAssetPath(hash) : TraceModules.CachedModulePath(hash);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, data);
            if (!upload.IsAsset)
                TraceModules.RecordUse(path, id, isAsset: false, _bbsName());   // an asset's is recorded by KeepAsset
            TraceModules.EnforceCacheLimit(_cacheLimitMb(), InUse(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TellDoorClosed(id, -1, "cache");
            return;
        }
        if (upload.IsAsset)
        {
            KeepAsset(id, path);
            TellDoor($"Have;module={id};sha256={hash}");
            return;
        }
        _sessions.TryGetValue(id, out var session);
        Open(id, session, upload.OpenArgs);
    }

    private void Open(string id, Session? session, string args)
    {
        var fields = ParseFields(args);

        // rect=row1,col1,row2,col2 (inclusive); the whole screen when it's missing or malformed
        int row1 = 1, col1 = 1, row2 = int.MaxValue, col2 = int.MaxValue;
        if (fields.TryGetValue("rect", out var rect))
        {
            var n = rect.Split(',');
            if (n.Length == 4 && int.TryParse(n[0], out int r1) && int.TryParse(n[1], out int c1)
                && int.TryParse(n[2], out int r2) && int.TryParse(n[3], out int c2)
                && r1 >= 1 && c1 >= 1 && r2 >= r1 && c2 >= c1)
                (row1, col1, row2, col2) = (r1, c1, r2, c2);
        }

        if (session == null)
        {
            // A door that waits for an answer must always get one, including when nothing starts
            if (!_enabled())
            {
                TellDoorClosed(id, -1, "disabled");
                return;
            }
            TraceModules.Module? module;
            if (fields.TryGetValue("wasm", out var sha256))
            {
                // The door's own WebAssembly module: run it from the cache, or ask the door to send it
                sha256 = sha256.ToLowerInvariant();
                bool validHash = sha256.Length == 64 && sha256.All(Uri.IsHexDigit);
                bool validSize = int.TryParse(fields.GetValueOrDefault("size"), out int size)
                                 && size > 0 && size <= TraceModules.MaxModuleBytes;
                if (!validHash || !validSize)
                {
                    TellDoorClosed(id, -1, "bad-module");
                    return;
                }
                if (TraceModules.HostPath == null || _terminal() == null)
                {
                    TellDoorClosed(id, -1, "not-installed");
                    return;
                }
                bool exclusive = fields.GetValueOrDefault("exclusive") == "1";
                module = TraceModules.WasmModule(id, sha256, exclusive);
                if (module == null)
                {
                    _uploads[id] = new Upload { Sha256 = sha256, Size = size, OpenArgs = args, Data = new MemoryStream() };
                    TellDoor($"Need;module={id};wasm={sha256}");
                    return;
                }
                TraceModules.RecordUse(module.CachePath!, id, isAsset: false, _bbsName());
            }
            else
            {
                module = null;  // every module comes from the door (wasm=...); there are no built-in ones
            }
            if (module == null || _terminal() == null)
            {
                TellDoorClosed(id, -1, "not-installed");
                return;
            }
            MakeRoom();
            if (_sessions.Count >= MaxSessions || (module.Exclusive && HasExclusive))
            {
                TellDoorClosed(id, -1, "busy");
                return;
            }
            if (fields.TryGetValue("v", out var v) && int.TryParse(v, out int version) && version > module.Version)
            {
                TellDoorClosed(id, -1, "version");  // the door needs a newer module than we have
                return;
            }

            EngineModuleHost host;
            try
            {
                host = EngineModuleHost.Start(module.ExePath, module.PrepareArgs?.Invoke() ?? [],
                                              module.CapacityWidth, module.CapacityHeight, _audioVolume);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"TRACE: couldn't start {module.ExePath}: {ex.Message}");
                TellDoorClosed(id, -1, "start-failed");
                return;
            }
            var layer = module.Exclusive
                ? new EngineHostLayer(host, exclusiveInput: true)
                : new EngineHostLayer(host, exclusiveInput: false)
                {
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                };
            var started = new Session { Module = module, Host = host, Layer = layer };
            // The module answering the door: a game's key presses, a score, whatever it sends. Base64 keeps it
            // binary-safe, and the send window (EngineModuleHost) keeps it from flooding the link.
            host.DataForDoor += bytes => Dispatcher.UIThread.Post(() =>
            {
                if (bytes.Length == 0 || !_sessions.ContainsKey(started.Module.Id))
                    return;
                host.CountSent(bytes.Length);
                if (started.Binary)
                    SendToBoard(BinaryFrame($"Data;module={started.Module.Id}", bytes));
                else
                    TellDoor($"Data;module={started.Module.Id};b64={Convert.ToBase64String(bytes)}");
            });
            // Text input went on or off: unless a game has the keyboard, the terminal takes focus. Off, the keyboard
            // goes back to it; on, Avalonia raises TextInput only on the focused element, so with nothing focused
            // (MRC's linked window) MainWindow's tunnelling handler never saw the typing, only the keys
            layer.TextInputChanged += (_, _) =>
            {
                if (!HasExclusive)
                    _exclusiveEnded();
            };
            // The module ended by itself (the player quit Doom, or it crashed): tell the door
            layer.Closed += (_, exitCode) =>
            {
                if (_sessions.TryGetValue(started.Module.Id, out var current) && current == started)
                {
                    // A module that died rather than quitting takes its reason with it, and the door is the only
                    // place anyone can see it: a sysop reads the door's log, a player never sees the debug output.
                    string reason = exitCode != 0 ? host.CrashReason : "";
                    Close(started);
                    TellDoorClosed(started.Module.Id, exitCode, reason.Length > 0 ? reason : null);
                }
            };
            host.PadRumble += (pad, low, high, ms) => _pads.Rumble(pad, low, high, ms);
            session = started;
            _sessions[id] = session;
            _layerHost.Children.Add(layer);
            if (_held.ContainsKey(id))
            {
                // Reopened after a transfer: the held picture stays under the new layer until its first frame
                layer.FirstFrame += _ => ReleaseHeld(id);
                var fallback = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
                fallback.Tick += (_, _) =>
                {
                    fallback.Stop();
                    if (_held.TryGetValue(id, out var still) && _layerHost.Children.Contains(still)
                        && (!_sessions.TryGetValue(id, out var cur) || cur == started))
                        ReleaseHeld(id);
                };
                fallback.Start();
            }
            LayerOpened?.Invoke(id, layer);
            UpdatePadTargets();
            // Pads already plugged in (and held) are news to a module that has just started
            _pads.Replay(host.SendPadDevice, host.SendPadButton, host.SendPadAxis);
        }

        (session.Row1, session.Col1, session.Row2, session.Col2) = (row1, col1, row2, col2);
        bool resumed = session.Hidden;
        Unhide(session);
        session.Binary = fields.GetValueOrDefault("bin") == "1";
        UpdatePlacement();
        // also when re-opening one that's already running; resumed=1: it was hidden and kept running (keep=1, 1.1.6)
        if (fields.ContainsKey("wasm"))
            TellDoor($"Ready;module={id}{(resumed ? ";resumed=1" : "")}");
    }

    /// <summary>How long a hidden module is kept running for the board before it is closed (keep=1, 1.1.6).</summary>
    private static readonly TimeSpan HiddenKeep = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Hide: the picture goes and the module keeps running, without the keyboard, mouse or gamepads (keep=1 in the
    /// Query answer, 1.1.6). A board can open it again at once (Ready;resumed=1) and find it as it was, instead of
    /// starting it over. It is closed after HiddenKeep hidden, or when another module needs its place.
    /// </summary>
    private void Hide(Session session)
    {
        if (session.Hidden)
            return;
        bool hadKeyboard = session.Module.Exclusive || session.Layer.TextInputOn;
        session.Hidden = true;
        session.Layer.IsVisible = false;
        session.Layer.Suspend(true);
        UpdatePadTargets();
        session.HiddenTimer?.Stop();
        session.HiddenTimer = new DispatcherTimer { Interval = HiddenKeep };
        session.HiddenTimer.Tick += (_, _) =>
        {
            session.HiddenTimer?.Stop();
            if (session.Hidden && _sessions.TryGetValue(session.Module.Id, out var cur) && cur == session)
            {
                Close(session);
                TellDoorClosed(session.Module.Id, 0, "expired");
            }
        };
        session.HiddenTimer.Start();
        if (hadKeyboard && !HasExclusive)
            _exclusiveEnded();
    }

    /// <summary>Show again after Hide (a Show, or an Open of the same module): the keyboard and input come back.</summary>
    private void Unhide(Session session)
    {
        session.HiddenTimer?.Stop();
        session.HiddenTimer = null;
        if (!session.Hidden)
            return;
        session.Hidden = false;
        session.Layer.Suspend(false);
        UpdatePadTargets();
    }

    /// <summary>Room for one more module when all MaxSessions are taken: hidden ones (kept for the board, keep=1) go.
    /// A hidden game never blocks another (HasExclusive leaves it out).</summary>
    private void MakeRoom()
    {
        foreach (var s in _sessions.Values.Where(s => s.Hidden).ToList())
        {
            if (_sessions.Count < MaxSessions)
                break;
            Close(s);
            TellDoorClosed(s.Module.Id, 0, "expired");
        }
    }

    private void Close(Session session)
    {
        session.HiddenTimer?.Stop();
        if (!_sessions.Remove(session.Module.Id))
            return;
        _assets.Remove(session.Module.Id);   // its assets stay cached, but may now be trimmed like anything else
        bool hadKeyboard = session.Module.Exclusive || session.Layer.TextInputOn;
        _layerHost.Children.Remove(session.Layer);
        UpdatePadTargets();
        session.Host.Dispose();
        if (hadKeyboard && !HasExclusive)
            _exclusiveEnded();
    }

    /// <summary>
    /// A key from MainWindow's tunnelling handlers while a module owns the keyboard: goes to that module's layer
    /// (Windows reads raw key messages in EngineHostLayer instead). A module with text input on takes the keys
    /// ahead of a running game, so two layers never both act on one key.
    /// </summary>
    public void HandleKey(Avalonia.Input.KeyEventArgs e, bool pressed) => KeyboardOwner?.HandleKey(e, pressed);

    /// <summary>
    /// A typed character from MainWindow's tunnelling TextInput handler, while a module has text input on: the
    /// composed text the player's keyboard layout produced (Shift, AltGr, dead keys), one UTF-16 unit per record.
    /// </summary>
    public void HandleText(string? text)
    {
        var owner = _sessions.Values.FirstOrDefault(s => !s.Hidden && s.Layer.TextInputOn);
        if (owner == null || string.IsNullOrEmpty(text))
            return;
        // Control characters are left out: Enter, Backspace, Tab and Esc already went as keys, and Avalonia's
        // TextInput delivers them as text too on several backends -- a chat line would get a stray character.
        foreach (char c in text)
            if (c >= ' ' && c != '\x7F')
                owner.Host.SendText(c);
    }

    /// <summary>The layer keys go to right now: a module with text input on, else a running game, else none.</summary>
    private EngineHostLayer? KeyboardOwner =>
        (_sessions.Values.FirstOrDefault(s => !s.Hidden && s.Layer.TextInputOn)
         ?? _sessions.Values.FirstOrDefault(s => !s.Hidden && s.Module.Exclusive))?.Layer;

    // ---- held pictures: a module closed for a transfer (Close;hold=1) stays on screen, dimmed, till it reopens ----

    private readonly Dictionary<string, Control> _held = new(StringComparer.OrdinalIgnoreCase);
    private DispatcherTimer? _heldTimer;
    private int _transfers;

    /// <summary>A module layer started (RainwireFileDrops watches the Files module's).</summary>
    public event Action<string, EngineHostLayer>? LayerOpened;

    /// <summary>The running module's layer, or null.</summary>
    public EngineHostLayer? LayerFor(string id) => _sessions.TryGetValue(id, out var s) ? s.Layer : null;

    private void Hold(Session session)
    {
        if (session.Hidden || !session.Layer.IsVisible || session.Layer.HeldSnapshot() is not { } still)
            return;
        ReleaseHeld(session.Module.Id);
        int at = _layerHost.Children.IndexOf(session.Layer);
        _layerHost.Children.Insert(at < 0 ? _layerHost.Children.Count : at, still);
        _held[session.Module.Id] = still;
        // A transfer normally starts within moments; if none does, the picture goes after a minute
        ArmHeldTimer(_transfers > 0 ? null : TimeSpan.FromSeconds(60));
    }

    private void ReleaseHeld(string id)
    {
        if (_held.Remove(id, out var still))
            _layerHost.Children.Remove(still);
    }

    private void ReleaseAllHeld()
    {
        foreach (var still in _held.Values)
            _layerHost.Children.Remove(still);
        _held.Clear();
        _heldTimer?.Stop();
    }

    private void ArmHeldTimer(TimeSpan? after)
    {
        _heldTimer?.Stop();
        if (after == null || _held.Count == 0)
            return;
        _heldTimer ??= new DispatcherTimer();
        _heldTimer.Tick -= HeldTimerTick;
        _heldTimer.Tick += HeldTimerTick;
        _heldTimer.Interval = after.Value;
        _heldTimer.Start();
    }

    private void HeldTimerTick(object? sender, EventArgs e) => ReleaseAllHeld();

    /// <summary>A file picker or TransferDialog opened: held pictures stay while it's up.</summary>
    public void TransferStarted()
    {
        _transfers++;
        _heldTimer?.Stop();
    }

    /// <summary>It closed: held pictures go 15 s later unless their module reopens first.</summary>
    public void TransferEnded()
    {
        _transfers = Math.Max(0, _transfers - 1);
        if (_transfers == 0)
            ArmHeldTimer(TimeSpan.FromSeconds(15));
    }

    /// <summary>TRACE was switched off: stop every module and tell the doors, so they fall back to their own graphics.</summary>
    public void Disable()
    {
        ReleaseAllHeld();
        _uploads.Clear();
        _assets.Clear();
        foreach (var session in _sessions.Values.ToList())
        {
            Close(session);
            TellDoorClosed(session.Module.Id, -1, "disabled");
        }
    }

    /// <summary>Stops every module (disconnect, or the terminal left the screen).</summary>
    public void CloseAll()
    {
        ReleaseAllHeld();
        _uploads.Clear();
        _assets.Clear();
        foreach (var session in _sessions.Values.ToList())
            Close(session);
        StopSending();
    }

    /// <summary>Keeps each picture over its terminal cells as the window, font or screen mode changes.</summary>
    public void UpdatePlacement()
    {
        if (_sessions.Count == 0)
            return;
        var canvas = _terminal();
        if (canvas == null || !canvas.IsEffectivelyVisible || canvas.Columns <= 0 || canvas.Rows <= 0)
        {
            if (canvas == null)
                CloseAll();
            return;
        }

        Rect screen = canvas.GetBbsScreenRect();
        double cellW = screen.Width / canvas.Columns, cellH = screen.Height / canvas.Rows;
        if (canvas.TransformToVisual(_layerHost) is not { } toHost)
            return;  // not in the same visual tree right now

        // Full-window modules (games, a board's Messages, Files, Wall and menus) fill the terminal's area: the whole
        // canvas, letterbox bands included, but not the status bar under it (connection, modem lights, bytes)
        Rect area = new Rect(canvas.Bounds.Size).TransformToAABB(toHost);
        var fill = new Thickness(Math.Max(Math.Round(area.Left), 0), Math.Max(Math.Round(area.Top), 0),
                                 Math.Max(Math.Round(_layerHost.Bounds.Width - area.Right), 0),
                                 Math.Max(Math.Round(_layerHost.Bounds.Height - area.Bottom), 0));
        foreach (var s in _sessions.Values)
        {
            if (s.Module.Exclusive)
            {
                if (s.Layer.Margin != fill)
                    s.Layer.Margin = fill;
                s.Layer.IsVisible = !s.Hidden;
                continue;
            }
            // A picture sits over live-screen cells; in scrollback those cells show history, so the
            // picture would stay put while the text scrolls under it. Hide it until back on the live screen.
            if (s.Hidden || canvas.IsScrolledBack)
            {
                s.Layer.IsVisible = false;
                continue;
            }
            int row2 = Math.Min(s.Row2, canvas.Rows), col2 = Math.Min(s.Col2, canvas.Columns);
            var cells = new Rect(screen.X + (s.Col1 - 1) * cellW, screen.Y + (s.Row1 - 1) * cellH,
                                 Math.Max(0, col2 - s.Col1 + 1) * cellW, Math.Max(0, row2 - s.Row1 + 1) * cellH);
            Rect r = cells.TransformToAABB(toHost);

            var margin = new Thickness(Math.Round(r.X), Math.Round(r.Y), 0, 0);
            double w = Math.Round(r.Width), h = Math.Round(r.Height);
            if (s.Layer.Margin != margin)
                s.Layer.Margin = margin;
            if (s.Layer.Width != w)
                s.Layer.Width = w;
            if (s.Layer.Height != h)
                s.Layer.Height = h;
            if (!s.Layer.IsVisible)
                s.Layer.IsVisible = true;
        }
    }
}
