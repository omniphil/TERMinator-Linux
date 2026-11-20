using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace TERMinator.Linux.Services;

/// <summary>
/// Runs one TRACE module (the sandboxed WebAssembly host, gamesandbox) and trades frames, input, sound and door
/// data with it over the engine contract: a shared BGRA frame buffer with a seqlock, 8-byte input records on the
/// module's stdin, and the same kind of records coming back on its stdout (frame ready, data for the door, sound, log
/// lines). The contract lives in trace/include/engine_contract.h and the design in BBSGames/Doom/ENGINE.md. Nothing
/// here is game-specific, and nothing here touches TAP+.
/// Linux port of the Windows client's Services/EngineModuleHost.cs. POSIX differences (the host's POSIX contract): the
/// frame buffer is a regular file in $XDG_RUNTIME_DIR (else /dev/shm, else the temp dir) that both sides map, passed
/// as "-frame &lt;path&gt;", and there is no frame-ready event: the host writes a TE_OUT_FRAME record (no payload) on
/// stdout after each frame, and the record reader wakes the seqlock copy with it.
/// </summary>
public sealed class EngineModuleHost : IDisposable
{
    // engine_contract.h
    private const uint FrameMagic = 0x42464554; // "TEFB"
    private const int HeaderSize = 64;
    private const int OffMagic = 0, OffWidth = 8, OffHeight = 12, OffSeq = 16, OffFlags = 20;
    private const int OffCapacityWidth = 28, OffCapacityHeight = 32;
    private const uint FlagAspect43 = 0x01;

    public const byte InKey = 1, InText = 2, InMouseMove = 3, InMouseButton = 4, InFocus = 5, InQuit = 6,
                      InData = 7, InResize = 8, InAudioRoom = 9, InSendRoom = 10, InPadButton = 11, InPadAxis = 12,
                      InMousePos = 13, InPadDevice = 14, InPaste = 15, InSurface = 16;

    private const byte OutData = 1, OutAudio = 2, OutTick = 3, OutLog = 4, OutFrame = 5, OutTextInput = 6,
                       OutMouseMode = 7, OutPadRumble = 8, OutDirect = 9;

    /// <summary>TE_IN_SURFACE kinds (engine_contract.h te_surface_t): none, or TERMinator's X11 window.</summary>
    public const uint SurfaceNone = 0, SurfaceX11 = 2;

    /// <summary>The most a module may send the door in one go (trace_api.h TRACE_SEND_MAX).</summary>
    public const int SendMax = 4096;

    /// <summary>
    /// How fast a module may send the door, as a first cut: a window of at most <see cref="SendMax"/> bytes
    /// outstanding, refilled at this rate. A game's inputs are a few hundred bytes a second, while a BBS link is
    /// usually 200 KB/s in total, so this leaves the terminal's own traffic plenty of room.
    /// </summary>
    private const int SendBytesPerSecond = 32 * 1024;

    private readonly int _capacityWidth, _capacityHeight;

    private readonly MemoryMappedFile _mapping;
    private readonly MemoryMappedViewAccessor _view;
    private readonly IntPtr _base;
    private readonly AutoResetEvent _frameReady = new(false);   // set by the record reader on TE_OUT_FRAME
    private readonly string _framePath;
    private readonly Process _process;
    private readonly Stream _input;
    private readonly object _inputLock = new();
    private readonly Thread _reader;
    private volatile bool _stopping;

    private readonly object _frameLock = new();
    private byte[] _pixels = [];
    private byte[] _scratch;
    private int _width, _height;
    private bool _aspect43;
    private long _frameSerial;

    private readonly Queue<string> _outputTail = new();

    private readonly Func<float>? _audioGain;
    private readonly Thread _records;
    private Thread? _flow;
    private TraceAudioStream? _audio;
    private bool _audioAttached;
    private readonly object _flowLock = new();   // the fields below: written by the UI, record and flow threads
    private int _lastAudioRoomSent = -1;
    private bool _audioSinceRoomReport;   // the module wrote sound since the last TE_IN_AUDIO_ROOM
    private long _sendRoomTicks;      // when the send window was last refilled
    private double _sendRoom;         // bytes the module may still send the door
    private int _lastSendRoomSent = -1;
    private bool _sentSinceRoomReport;    // the module sent the door something since the last TE_IN_SEND_ROOM

    /// <summary>A new frame is ready (raised on the reader thread).</summary>
    public event Action? FrameArrived;

    /// <summary>The module process ended, with its exit code (raised on a thread-pool thread).</summary>
    public event Action<int>? Exited;

    /// <summary>The module sent bytes for the door (raised on the record reader thread).</summary>
    public event Action<byte[]>? DataForDoor;

    /// <summary>The module turned typed-text input on or off (raised on the record reader thread).</summary>
    public event Action<bool>? TextInputChanged;

    /// <summary>The module picked a mouse mode, 0-3 (engine_contract.h TE_MOUSE_*; raised on the record reader thread).</summary>
    public event Action<int>? MouseModeChanged;

    /// <summary>
    /// The module would present straight into the window (TE_OUT_DIRECT, 1.1.6): an exclusive layer answers with
    /// <see cref="SendSurface"/>. Raised on the record thread.
    /// </summary>
    public event Action<bool>? DirectRequested;

    /// <summary>The module wants a pad to rumble: pad, low motor, high motor (0-32767), milliseconds (reader thread).</summary>
    public event Action<int, int, int, int>? PadRumble;

    private EngineModuleHost(string exePath, IEnumerable<string> moduleArgs, int capacityWidth, int capacityHeight,
                             Func<float>? audioGain)
    {
        _audioGain = audioGain;
        _capacityWidth = capacityWidth;
        _capacityHeight = capacityHeight;
        long frameBytes = HeaderSize + (long)capacityWidth * capacityHeight * 4;
        _scratch = new byte[capacityWidth * capacityHeight * 4];

        string name = $"TRACE-{Environment.ProcessId}-{Guid.NewGuid():N}";
        _framePath = Path.Combine(FrameDir(), name + ".frame");
        // A regular file both sides map (owner-only): memory-backed where $XDG_RUNTIME_DIR or /dev/shm is tmpfs
        var frameFile = new FileStream(_framePath, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.ReadWrite,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
        try
        {
            frameFile.SetLength(frameBytes);
            _mapping = MemoryMappedFile.CreateFromFile(frameFile, null, frameBytes, MemoryMappedFileAccess.ReadWrite,
                                                       HandleInheritability.None, leaveOpen: false);
        }
        catch
        {
            frameFile.Dispose();
            TryDelete(_framePath);
            throw;
        }
        _view = _mapping.CreateViewAccessor(0, frameBytes);
        _base = _view.SafeMemoryMappedViewHandle.DangerousGetHandle() + (nint)_view.PointerOffset;
        // The module reads the capacity before mapping the whole buffer (engine_contract.h); on POSIX the app also
        // stamps the magic, since the host opens an existing file rather than creating it
        Marshal.WriteInt32(_base, OffMagic, unchecked((int)FrameMagic));
        Marshal.WriteInt32(_base, OffCapacityWidth, capacityWidth);
        Marshal.WriteInt32(_base, OffCapacityHeight, capacityHeight);

        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory,
        };
        psi.ArgumentList.Add("-engine");
        psi.ArgumentList.Add(name);
        psi.ArgumentList.Add("-frame");
        psi.ArgumentList.Add(_framePath);
        // libwasmtime.so ships beside the host
        string hostDir = Path.GetDirectoryName(exePath) ?? "";
        string? ldPath = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
        psi.Environment["LD_LIBRARY_PATH"] = string.IsNullOrEmpty(ldPath) ? hostDir : hostDir + ":" + ldPath;
        foreach (var arg in moduleArgs)
            psi.ArgumentList.Add(arg);

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        // stdout carries records, so only stderr is text
        _process.ErrorDataReceived += (_, e) => KeepOutput(e.Data);
        _process.Exited += (_, _) =>
        {
            _stopping = true;
            int code;
            try { code = _process.ExitCode; } catch { code = -1; }
            Exited?.Invoke(code);
        };
        try
        {
            _process.Start();
        }
        catch
        {
            _view.Dispose();
            _mapping.Dispose();
            TryDelete(_framePath);
            throw;
        }
        _process.BeginErrorReadLine();
        _input = _process.StandardInput.BaseStream;

        // The queue exists from the start, so the module is told how much room it has before it makes any sound;
        // the audio device is only opened once it actually sends some (a module that stays silent costs nothing).
        if (_audioGain != null && AudioService.AudioBridgeReady)   // no OpenAL: nothing would play it
            _audio = new TraceAudioStream(_audioGain);

        _reader = new Thread(ReadFrames) { IsBackground = true, Name = "Engine frame reader" };
        _reader.Start();
        _records = new Thread(ReadRecords) { IsBackground = true, Name = "Engine record reader" };
        _records.Start();
        StartFlowReports();
    }

    /// <summary>Where frame files go: $XDG_RUNTIME_DIR (tmpfs, per user), else /dev/shm, else the temp dir.</summary>
    private static string FrameDir()
    {
        string? runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrEmpty(runtime) && Directory.Exists(runtime))
            return runtime;
        if (Directory.Exists("/dev/shm"))
            return "/dev/shm";
        return Path.GetTempPath();
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Starts a module executable with a frame buffer that holds up to capacityWidth x capacityHeight. Throws if it
    /// can't be started. audioGain, when given, lets the module make sound: it returns the volume (0-1) to mix it at.
    /// </summary>
    public static EngineModuleHost Start(string exePath, IEnumerable<string> moduleArgs,
                                         int capacityWidth = 1280, int capacityHeight = 1024,
                                         Func<float>? audioGain = null) =>
        new(exePath, moduleArgs, capacityWidth, capacityHeight, audioGain);

    /// <summary>True once the module process has ended.</summary>
    public bool HasExited
    {
        get { try { return _process.HasExited; } catch (InvalidOperationException) { return true; } }
    }

    /// <summary>The module's exit code, or -1 while it's running or when unknown.</summary>
    public int ExitCode
    {
        get { try { return _process.HasExited ? _process.ExitCode : -1; } catch (InvalidOperationException) { return -1; } }
    }

    /// <summary>The last lines the module printed, for error reports.</summary>
    public string OutputTail
    {
        get { lock (_outputTail) return string.Join(Environment.NewLine, _outputTail); }
    }

    /// <summary>
    /// Why the module stopped, in a few words, for the Closed report the door gets: the sandbox host's own line when
    /// it caught a trap ("gamesandbox: running the module: wasm trap: ..."), else the last thing the module itself
    /// logged; for a trap, its cause and where it happened too. Empty when it said nothing, which is what a clean exit
    /// looks like. Cut down to something that can travel in a TRACE field: no ';' (the field separator), no ESC, no
    /// control characters, 300 characters at most.
    /// </summary>
    public string CrashReason
    {
        get
        {
            string[] lines;
            lock (_outputTail)
                lines = [.. _outputTail];
            // The host's line says what killed it; the module's last line says what it was doing, which is usually
            // the more useful half. Both when we have both.
            string? host = Array.FindLast(lines, l => l.StartsWith("gamesandbox:", StringComparison.Ordinal));
            string? module = Array.FindLast(lines, l => l.StartsWith("module:", StringComparison.Ordinal));
            string? line = host ?? module ?? (lines.Length > 0 ? lines[^1] : null);
            if (line == null)
                return "";
            if (host != null)   // a trap: its cause and where, from the lines under it (the board logs the whole report)
            {
                int c = Array.FindLastIndex(lines, l => l.TrimStart().StartsWith("Caused by:", StringComparison.Ordinal));
                string? cause = c >= 0 ? Array.Find(lines[(c + 1)..], l => l.Trim().Length > 0) : null;
                string? at = Array.Find(lines, l => l.TrimStart().StartsWith("0:", StringComparison.Ordinal));
                if (cause != null) line += " | cause: " + cause.Trim();
                if (at != null) line += " | at " + at.Trim()[2..].Trim();
            }
            if (host != null && module != null)
                line += " | last: " + module;
            var clean = new string([.. line.Select(c => c is ';' or '\x1b' || char.IsControl(c) ? ' ' : c)]).Trim();
            while (clean.Contains("  ", StringComparison.Ordinal))
                clean = clean.Replace("  ", " ", StringComparison.Ordinal);
            return clean.Length > 300 ? clean[..300] : clean;
        }
    }

    private void KeepOutput(string? line)
    {
        if (line == null)
            return;
        Debug.WriteLine($"[engine] {line}");
        lock (_outputTail)
        {
            _outputTail.Enqueue(line);
            while (_outputTail.Count > 20)
                _outputTail.Dequeue();
        }
    }

    private void ReadFrames()
    {
        while (!_stopping)
        {
            if (!_frameReady.WaitOne(100) || _stopping)
                continue;
            if ((uint)Marshal.ReadInt32(_base, OffMagic) != FrameMagic)
                continue;

            // Seqlock: copy, then keep the copy only if no write started or finished meanwhile
            for (int attempt = 0; attempt < 20; attempt++)
            {
                uint seq = (uint)Marshal.ReadInt32(_base, OffSeq);
                if ((seq & 1) != 0)
                {
                    Thread.Yield();
                    continue;
                }
                int w = Marshal.ReadInt32(_base, OffWidth);
                int h = Marshal.ReadInt32(_base, OffHeight);
                uint flags = (uint)Marshal.ReadInt32(_base, OffFlags);
                if (w <= 0 || h <= 0 || w > _capacityWidth || h > _capacityHeight)
                    break;
                int bytes = w * h * 4;
                Marshal.Copy(_base + HeaderSize, _scratch, 0, bytes);
                if ((uint)Marshal.ReadInt32(_base, OffSeq) != seq)
                    continue;

                lock (_frameLock)
                {
                    (_pixels, _scratch) = (_scratch, _pixels.Length == _scratch.Length ? _pixels : new byte[_scratch.Length]);
                    _width = w;
                    _height = h;
                    _aspect43 = (flags & FlagAspect43) != 0;
                    _frameSerial++;
                }
                FrameArrived?.Invoke();
                break;
            }
        }
    }

    /// <summary>
    /// Reads the records the module sends back (engine contract): bytes for the door, sound, the tick rate it
    /// wants, and log lines. Unknown record types are skipped along with their payload, so a newer module still runs.
    /// </summary>
    private void ReadRecords()
    {
        var stdout = _process.StandardOutput.BaseStream;
        var header = new byte[8];
        try
        {
            while (!_stopping)
            {
                if (!ReadExactly(stdout, header, 8))
                    break;
                byte type = header[0];
                byte flags = header[1];
                int a = BitConverter.ToUInt16(header, 2);
                short b = BitConverter.ToInt16(header, 4);
                short c = BitConverter.ToInt16(header, 6);
                int payloadBytes = type switch
                {
                    OutData or OutLog => a,
                    OutAudio => a * 4,        // S16 stereo frames
                    _ => 0,                   // OutFrame and unknown types carry nothing
                };
                byte[] payload = payloadBytes > 0 ? new byte[payloadBytes] : [];
                if (payloadBytes > 0 && !ReadExactly(stdout, payload, payloadBytes))
                    break;

                switch (type)
                {
                    case OutFrame:
                        _frameReady.Set();    // POSIX: the frame-ready signal comes on stdout
                        break;
                    case OutData:
                        DataForDoor?.Invoke(payload);
                        break;
                    case OutAudio:
                        PlayAudio(payload, a);
                        break;
                    case OutTick:
                        Debug.WriteLine($"[engine] module asked for {b} updates/s");
                        break;
                    case OutLog:
                        KeepOutput("module: " + System.Text.Encoding.UTF8.GetString(payload));
                        break;
                    case OutTextInput:
                        TextInputChanged?.Invoke(b != 0);
                        break;
                    case OutMouseMode:
                        MouseModeChanged?.Invoke(b is >= 0 and <= 3 ? b : 0);
                        break;
                    case OutPadRumble:
                        PadRumble?.Invoke(flags & 3, Math.Max((int)b, 0), Math.Max((int)c, 0), a);
                        break;
                    case OutDirect:
                        DirectRequested?.Invoke((flags & 1) != 0);
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // the module is going away; Exited follows
        }
    }

    private static bool ReadExactly(Stream stream, byte[] buffer, int count)
    {
        int have = 0;
        while (have < count)
        {
            int got = stream.Read(buffer, have, count - have);
            if (got <= 0)
                return false;
            have += got;
        }
        return true;
    }

    /// <summary>Queues module sound into TERMinator's own output.</summary>
    private void PlayAudio(byte[] payload, int frames)
    {
        var audio = _audio;
        if (audio == null || frames <= 0)
            return;
        if (!_audioAttached)
        {
            _audioAttached = true;
            AudioService.AddTraceAudio(audio);   // into the door mix at the soundtrack volume (Windows: AudioOutput.Effects)
        }
        var samples = new short[frames * 2];
        Buffer.BlockCopy(payload, 0, samples, 0, frames * 4);
        audio.Write(samples);
        lock (_flowLock)
            _audioSinceRoomReport = true;   // the sandbox counts its own writes down from our last report
        ReportAudioRoom();
    }

    /// <summary>
    /// Keeps the module told how much room is left: audio frames the queue can take, and bytes it may send the door.
    /// Without this a module would either run ahead of the sound card or flood a BBS link.
    /// </summary>
    private void StartFlowReports()
    {
        if (_flow != null)
            return;
        lock (_flowLock)
            _sendRoomTicks = Environment.TickCount64;
        _flow = new Thread(() =>
        {
            while (!_stopping)
            {
                ReportAudioRoom();
                ReportSendRoom();
                Thread.Sleep(10);
            }
        })
        { IsBackground = true, Name = "Engine flow control" };
        _flow.Start();
    }

    private void ReportAudioRoom()
    {
        var audio = _audio;
        if (audio == null)
            return;
        // Read and sent under the lock, so the record and flow threads can't land an older figure after a newer one
        lock (_flowLock)
        {
            int room = Math.Min(audio.RoomFrames, ushort.MaxValue);
            // The sandbox treats the report as absolute and counts its own sound down from it, so after it wrote
            // any it must hear again even if the room barely moved; otherwise only when it moved enough to matter
            if (Math.Abs(room - _lastAudioRoomSent) < 256 && !_audioSinceRoomReport)
                return;
            _lastAudioRoomSent = room;
            _audioSinceRoomReport = false;
            Send(InAudioRoom, 0, (ushort)room);
        }
    }

    private void ReportSendRoom()
    {
        lock (_flowLock)
        {
            long now = Environment.TickCount64;
            _sendRoom = Math.Min(SendMax, _sendRoom + (now - _sendRoomTicks) * SendBytesPerSecond / 1000.0);
            _sendRoomTicks = now;
            int room = (int)_sendRoom / 64;   // the record carries 64-byte units
            // The sandbox treats the report as absolute and counts its own sends down from it, so after a send it
            // must hear again even if the room is unchanged, or it stays at zero and stops sending
            if (room == _lastSendRoomSent && !_sentSinceRoomReport)
                return;
            _lastSendRoomSent = room;
            _sentSinceRoomReport = false;
            Send(InSendRoom, 0, (ushort)Math.Min(room, ushort.MaxValue));   // takes _inputLock, never the reverse
        }
    }

    /// <summary>Counts what the module sent the door against its send window (called on the UI thread).</summary>
    public void CountSent(int bytes)
    {
        lock (_flowLock)
        {
            _sendRoom = Math.Max(0, _sendRoom - bytes);
            _sentSinceRoomReport = true;
        }
    }

    /// <summary>
    /// Hands the newest frame to <paramref name="consume"/> (pixels, width, height, show-at-4:3) if it's newer than
    /// <paramref name="lastSerial"/>. The pixel array is only valid during the call.
    /// </summary>
    public bool TryConsumeFrame(ref long lastSerial, Action<byte[], int, int, bool> consume)
    {
        lock (_frameLock)
        {
            if (_frameSerial == lastSerial || _width == 0)
                return false;
            lastSerial = _frameSerial;
            consume(_pixels, _width, _height, _aspect43);
            return true;
        }
    }

    private void Send(byte type, byte flags, ushort a, short b = 0, short c = 0, ReadOnlySpan<byte> payload = default)
    {
        Span<byte> rec = stackalloc byte[8];
        rec[0] = type;
        rec[1] = flags;
        BitConverter.TryWriteBytes(rec[2..], a);
        BitConverter.TryWriteBytes(rec[4..], b);
        BitConverter.TryWriteBytes(rec[6..], c);
        lock (_inputLock)
        {
            if (_stopping)
                return;
            try
            {
                _input.Write(rec);
                if (!payload.IsEmpty)
                    _input.Write(payload);
                _input.Flush();
            }
            catch (IOException)
            {
                // the module is going away; Exited follows
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>The most one paste carries (engine_contract.h TE_PASTE_MAX), in UTF-16 units.</summary>
    public const int PasteMax = 4096;

    /// <summary>How long after a click or key in the module a door may open a link or ask for a paste.</summary>
    public const int GestureWindowMs = 3000;

    private long _gestureAt;   // Environment.TickCount64 of the player's last click or key press here (0: none)

    /// <summary>
    /// True when the player clicked or pressed a key in this module in the last <see cref="GestureWindowMs"/>, and
    /// uses that up: a door's OpenUrl or Paste needs one each, so a board can't open pages or read the clipboard by
    /// itself.
    /// </summary>
    public bool TakeGesture()
    {
        long at = Interlocked.Exchange(ref _gestureAt, 0);
        return at != 0 && Environment.TickCount64 - at <= GestureWindowMs;
    }

    /// <summary>A physical key: set-1 scancode, E0-extended flag and Windows virtual key (Controls/PhysicalKeyMap).</summary>
    public void SendKey(int scanCode, bool extended, int virtualKey, bool pressed)
    {
        if (pressed)
            Interlocked.Exchange(ref _gestureAt, Environment.TickCount64);
        Send(InKey, (byte)((pressed ? 1 : 0) | (extended ? 2 : 0)), (ushort)scanCode, (short)virtualKey);
    }

    /// <summary>
    /// The clipboard's text as a paste (engine_contract.h TE_IN_PASTE): line breaks as '\n', tabs kept, every other
    /// control character dropped, at most <see cref="PasteMax"/> units (never half a surrogate pair). Only while the
    /// module has text input on.
    /// </summary>
    public void SendPaste(string text)
    {
        string clean = CleanPaste(text);
        if (clean.Length == 0)
            return;
        lock (_inputLock)   // one paste's records stay together
        {
            Send(InPaste, 1, (ushort)clean.Length);
            foreach (char c in clean)
                Send(InText, 0, c);
            Send(InPaste, 0, 0);
        }
    }

    /// <summary>Text for a paste: CR LF and CR become LF, tabs and LF stay, other control characters go, capped.</summary>
    public static string CleanPaste(string text)
    {
        var sb = new System.Text.StringBuilder(Math.Min(text.Length, PasteMax));
        for (int i = 0; i < text.Length && sb.Length < PasteMax; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                c = '\n';
            }
            else if (char.IsControl(c) && c != '\n' && c != '\t')
                continue;
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]) || sb.Length + 2 > PasteMax)
                    break;
                sb.Append(c).Append(text[++i]);
                continue;
            }
            if (char.IsLowSurrogate(c))
                continue;   // half a pair on its own
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>A typed character, one UTF-16 code unit (a surrogate pair is two calls). Only sent in text input mode.</summary>
    public void SendText(char c) => Send(InText, 0, c);

    public void SendMouseMove(int dx, int dy) =>
        Send(InMouseMove, 0, 0, (short)Math.Clamp(dx, short.MinValue, short.MaxValue),
             (short)Math.Clamp(dy, short.MinValue, short.MaxValue));

    /// <summary>button: 1 left, 2 middle, 3 right, 4 wheel up, 5 wheel down, 6 back, 7 forward.</summary>
    public void SendMouseButton(int button, bool pressed)
    {
        if (pressed && button is >= 1 and <= 3)
            Interlocked.Exchange(ref _gestureAt, Environment.TickCount64);
        Send(InMouseButton, (byte)(pressed ? 1 : 0), (ushort)button);
    }

    /// <summary>Pointer modes: where the pointer is in the module's frame pixels, whether it's over the picture, and
    /// the buttons held (bit0 left, bit1 middle, bit2 right).</summary>
    public void SendMousePos(int x, int y, bool over, int buttons) =>
        Send(InMousePos, (byte)(over ? 1 : 0), (ushort)buttons, (short)Math.Clamp(x, short.MinValue, short.MaxValue),
             (short)Math.Clamp(y, short.MinValue, short.MaxValue));

    /// <summary>A gamepad button (engine_contract.h TE_PAD_BUTTON_*) on pad 0-3.</summary>
    public void SendPadButton(int pad, int button, bool pressed) =>
        Send(InPadButton, (byte)(pressed ? 1 : 0), (ushort)button, (short)pad);

    /// <summary>A stick or trigger (TE_PAD_AXIS_*) on pad 0-3: sticks -32768..32767 (down/right positive), triggers 0..32767.</summary>
    public void SendPadAxis(int pad, int axis, int value) =>
        Send(InPadAxis, 0, (ushort)axis, (short)Math.Clamp(value, short.MinValue, short.MaxValue), (short)pad);

    /// <summary>A pad was plugged in or removed.</summary>
    public void SendPadDevice(int pad, bool connected) => Send(InPadDevice, (byte)(connected ? 1 : 0), (ushort)pad);

    public void SendFocus(bool focused) => Send(InFocus, (byte)(focused ? 1 : 0), 0);

    /// <summary>Data from the door for this module (up to 65535 bytes), e.g. the fractal viewport.</summary>
    public void SendData(ReadOnlySpan<byte> payload)
    {
        if (payload.Length is > 0 and <= ushort.MaxValue)
            Send(InData, 0, (ushort)payload.Length, payload: payload);
    }

    /// <summary>The device-pixel size the module is shown at.</summary>
    public void SendSurface(uint kind, ulong handle, int x, int y, int width, int height)
    {
        // TE_IN_SURFACE: kind, reserved, the X11 window, the picture's rectangle in device pixels of its client area
        Span<byte> p = stackalloc byte[32];
        BitConverter.TryWriteBytes(p[0..4], kind);
        BitConverter.TryWriteBytes(p[4..8], 0u);
        BitConverter.TryWriteBytes(p[8..16], handle);
        BitConverter.TryWriteBytes(p[16..20], x);
        BitConverter.TryWriteBytes(p[20..24], y);
        BitConverter.TryWriteBytes(p[24..28], Math.Max(width, 0));
        BitConverter.TryWriteBytes(p[28..32], Math.Max(height, 0));
        Send(InSurface, 0, 32, payload: p);
    }

    public void SendResize(int width, int height, int scalePercent = 0) =>
        Send(InResize, 0, (ushort)Math.Clamp(scalePercent, 0, ushort.MaxValue),
             (short)Math.Clamp(width, 0, short.MaxValue), (short)Math.Clamp(height, 0, short.MaxValue));

    /// <summary>Asks the module to quit the way it normally would (Doom shows its quit prompt).</summary>
    public void RequestQuit() => Send(InQuit, 0, 0);

    public void Dispose()
    {
        _stopping = true;
        if (_audio != null)
        {
            if (_audioAttached)
                AudioService.RemoveTraceAudio(_audio);
            _audio.Clear();
            _audio = null;
        }
        lock (_inputLock)
        {
            try { _input.Dispose(); } catch { }  // EOF: the module quits by itself
        }
        try
        {
            if (!_process.WaitForExit(2000))
                _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        _reader.Join(500);
        _records.Join(500);
        _flow?.Join(500);
        _process.Dispose();
        _frameReady.Dispose();
        _view.Dispose();
        _mapping.Dispose();
        TryDelete(_framePath);
    }
}
