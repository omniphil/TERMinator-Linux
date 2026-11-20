using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using Avalonia.Threading;
using TERMinator.Linux.Models;

namespace TERMinator.Linux.Services;

/// <summary>
/// Narrates a connection attempt for the connecting overlay: which stage it has reached and how long it has sat
/// there, so a board that doesn't answer shows where it stalled. It only observes and never opens its own
/// connection to the board:
/// <list type="bullet">
/// <item>resolve: its own DNS lookup of the host, for display (the native connect resolves independently)</item>
/// <item>tcp: this process's row in the kernel TCP table (/proc/net/tcp and tcp6, matched to this process's socket
/// inodes through /proc/self/fd), SYN sent vs established</item>
/// <item>handshake: once established, TLS / SSH / WebSocket negotiation until the native connect returns</item>
/// </list>
/// Lines the native side writes to its debug log (SSH steps, error messages) are streamed in as they appear.
/// Create it, <see cref="Start"/> it and <see cref="Complete"/> it on the UI thread.
/// </summary>
public sealed class ConnectionTracer
{
    public enum LineKind { Command, Info, Success, Error }

    private enum Stage { Resolve, Tcp, Handshake, Done }

    // Socket states as /proc/net/tcp prints them (include/net/tcp_states.h)
    private const int TcpStateEstablished = 1;
    private const int TcpStateSynSent = 2;

    private readonly SavedConnection _connection;
    private readonly Action<string, LineKind> _log;
    private readonly Action<string> _setStatus;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _stageClock = new();
    private readonly HashSet<IPAddress> _addresses = [];
    private Stage _stage = Stage.Resolve;
    private string _stageLabel = string.Empty;
    private bool _synSentLogged;
    private int _nativeLogSeen;

    public ConnectionTracer(SavedConnection connection, Action<string, LineKind> log, Action<string> setStatus)
    {
        _connection = connection;
        _log = log;
        _setStatus = setStatus;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) => Tick();
    }

    /// <summary>Starts narrating. Call just before the native connect is started.</summary>
    public void Start()
    {
        // Only this attempt's native lines (the native connect clears the log again itself, which is harmless)
        try
        {
            NativeBridge.syncterm_clear_ssh_debug_log();
        }
        catch (EntryPointNotFoundException)
        {
        }
        _nativeLogSeen = 0;

        _log($"dial {_connection.Host}:{_connection.Port} --proto {ProtocolName(_connection.Protocol)}", LineKind.Command);
        EnterStage(Stage.Resolve, $"resolve {_connection.Host}");
        _timer.Start();
        _ = ResolveAsync();
    }

    /// <summary>Stops narrating and writes the outcome. Safe to call more than once; only the first call counts.</summary>
    public void Complete(bool success, bool timedOut, bool cancelled)
    {
        if (_stage == Stage.Done)
            return;

        _timer.Stop();
        PumpNativeLog();
        string stuckAt = _stageLabel;
        double seconds = _stageClock.Elapsed.TotalSeconds;
        _stage = Stage.Done;

        if (success)
        {
            _log("CONNECT", LineKind.Success);
            _setStatus("[online]");
        }
        else if (cancelled)
        {
            _log("aborted by user", LineKind.Info);
            _setStatus($"[aborted during: {stuckAt}]");
        }
        else
        {
            string why = timedOut ? $"no response after {seconds:0.0}s" : $"failed after {seconds:0.0}s";
            _log($"NO CARRIER: stuck at {stuckAt} ({why})", LineKind.Error);
            _setStatus($"[stuck at: {stuckAt}]");
        }
    }

    private async Task ResolveAsync()
    {
        var clock = Stopwatch.StartNew();
        IPAddress[] found;
        string? failure = null;
        try
        {
            found = IPAddress.TryParse(_connection.Host, out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(_connection.Host).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            found = [];
            failure = ex.Message;
        }

        // Back on the UI thread: the log, the status line and the stage are all UI state
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_stage != Stage.Resolve)
                return; // the native connect already moved on or finished

            if (failure != null)
            {
                // Stay on the resolve stage: the native connect will fail on the same lookup
                _log($"  !! lookup failed: {failure}", LineKind.Error);
                return;
            }

            foreach (var address in found)
                _addresses.Add(Normalize(address));

            string shown = string.Join(", ", found.Take(3).Select(a => a.ToString())) + (found.Length > 3 ? ", ..." : string.Empty);
            _log($"  -> {shown} ({clock.ElapsedMilliseconds} ms)", LineKind.Info);

            string target = found.Length > 0 ? found[0].ToString() : _connection.Host;
            EnterStage(Stage.Tcp, $"open tcp {FormatEndpoint(target, _connection.Port)}");
        });
    }

    private void EnterStage(Stage stage, string command)
    {
        _stage = stage;
        _stageLabel = command;
        _stageClock.Restart();
        _log(command, LineKind.Command);
        UpdateStatus();
    }

    private void Tick()
    {
        PumpNativeLog();
        if (_stage == Stage.Tcp)
            CheckTcp();
        if (_stage != Stage.Done)
            UpdateStatus();
    }

    private void UpdateStatus() =>
        _setStatus($"[{_stageLabel}]  {_stageClock.Elapsed.TotalSeconds:0.0}s");

    private void CheckTcp()
    {
        int? state = FindTcpState(_connection.Port, _addresses);
        if (state == TcpStateEstablished)
        {
            _log($"  established ({_stageClock.ElapsedMilliseconds} ms)", LineKind.Success);
            EnterStage(Stage.Handshake, HandshakeCommand(_connection.Protocol));
        }
        else if (state == TcpStateSynSent && !_synSentLogged)
        {
            _synSentLogged = true;
            _log("  SYN sent, waiting for an answer", LineKind.Info);
        }
    }

    /// <summary>Streams whole new lines from the native debug log (SSH steps, error messages).</summary>
    private void PumpNativeLog()
    {
        string text = ConnectionManager.GetSshDebugLog();
        if (text.Length < _nativeLogSeen)
            _nativeLogSeen = 0; // the native connect cleared it

        int end = text.LastIndexOf('\n');
        if (end < _nativeLogSeen)
            return; // nothing new, or only a partial line so far

        foreach (var rawLine in text.Substring(_nativeLogSeen, end + 1 - _nativeLogSeen).Split('\n'))
        {
            string line = rawLine.Trim();
            // The bridge's attempt header repeats what the dial line already says
            if (line.Length == 0 || line.StartsWith("===") || line.StartsWith("Host:") || line.StartsWith("Username:"))
                continue;

            bool isError = line.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("[uifcmsg", StringComparison.Ordinal)
                           || line.StartsWith("[error]", StringComparison.Ordinal);
            _log("  " + line, isError ? LineKind.Error : LineKind.Info);
        }
        _nativeLogSeen = end + 1;
    }

    private static string ProtocolName(ConnectionProtocol protocol) => protocol switch
    {
        ConnectionProtocol.SSH => "ssh",
        ConnectionProtocol.TelnetS => "telnets",
        ConnectionProtocol.WebSocket => "ws",
        ConnectionProtocol.SecureWebSocket => "wss",
        ConnectionProtocol.Raw => "raw",
        _ => "telnet"
    };

    private static string HandshakeCommand(ConnectionProtocol protocol) => protocol switch
    {
        ConnectionProtocol.TelnetS => "tls handshake",
        ConnectionProtocol.SSH => "ssh handshake + login",
        ConnectionProtocol.SecureWebSocket => "tls handshake + websocket upgrade",
        ConnectionProtocol.WebSocket => "websocket upgrade",
        ConnectionProtocol.Raw => "raw session",
        _ => "telnet session"
    };

    private static string FormatEndpoint(string host, int port) =>
        host.Contains(':') ? $"[{host}]:{port}" : $"{host}:{port}";

    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    // ---------------------------------------------------------------- Linux TCP table (/proc)

    /// <summary>
    /// State of this process's TCP connection to the board (IPv4 or IPv6), or null if no such row exists yet.
    /// The kernel's tables list every socket on the machine; ours are the rows whose inode is one of this
    /// process's open file descriptors. Established wins if there are several candidates (e.g. the native
    /// connect trying more than one address). Any failure to read /proc just means "unknown".
    /// </summary>
    private static int? FindTcpState(int port, HashSet<IPAddress> remotes)
    {
        var inodes = OwnSocketInodes();
        if (inodes.Count == 0)
            return null;

        int? v4 = ScanProcTable("/proc/net/tcp", ipv6: false, port, inodes, remotes);
        if (v4 == TcpStateEstablished)
            return v4;
        return ScanProcTable("/proc/net/tcp6", ipv6: true, port, inodes, remotes) ?? v4;
    }

    /// <summary>Inodes of every socket this process holds open: the "socket:[N]" links under /proc/self/fd.</summary>
    private static HashSet<long> OwnSocketInodes()
    {
        var inodes = new HashSet<long>();
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries("/proc/self/fd"))
            {
                string? target;
                try { target = new FileInfo(entry).LinkTarget; }
                catch { continue; } // the descriptor closed between the listing and the readlink
                if (target != null && target.StartsWith("socket:[", StringComparison.Ordinal) && target.EndsWith(']')
                    && long.TryParse(target.AsSpan(8, target.Length - 9), NumberStyles.None, CultureInfo.InvariantCulture, out long inode))
                    inodes.Add(inode);
            }
        }
        catch
        {
            // no /proc (or no permission): the tcp stage simply never reports
        }
        return inodes;
    }

    /// <summary>
    /// One pass over /proc/net/tcp or tcp6. Columns: sl, local_address, rem_address, st, tx:rx, tr:when, retrnsmt,
    /// uid, timeout, inode, ... — addresses are hex words in host byte order, ports hex in network order.
    /// </summary>
    private static int? ScanProcTable(string path, bool ipv6, int port, HashSet<long> inodes, HashSet<IPAddress> remotes)
    {
        string[] lines;
        try
        {
            if (!File.Exists(path))
                return null;
            lines = File.ReadAllLines(path);
        }
        catch
        {
            return null;
        }

        int? found = null;
        foreach (var line in lines.Skip(1))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 10)
                continue;

            if (!long.TryParse(fields[9], NumberStyles.None, CultureInfo.InvariantCulture, out long inode) || !inodes.Contains(inode))
                continue;

            int colon = fields[2].IndexOf(':');
            if (colon < 0
                || !int.TryParse(fields[2].AsSpan(colon + 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int remotePort)
                || remotePort != port)
                continue;

            if (!int.TryParse(fields[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int state))
                continue;

            if (remotes.Count > 0)
            {
                var remote = ParseProcAddress(fields[2].AsSpan(0, colon), ipv6);
                if (remote == null || !remotes.Contains(Normalize(remote)))
                    continue;
            }

            if (state == TcpStateEstablished)
                return state;
            found ??= state;
        }
        return found;
    }

    /// <summary>
    /// Decodes a /proc/net/tcp address: 8 hex digits (IPv4) or 32 (IPv6), each 32-bit word printed in the
    /// host's byte order, so on a little-endian machine the bytes of every word are reversed.
    /// </summary>
    private static IPAddress? ParseProcAddress(ReadOnlySpan<char> hex, bool ipv6)
    {
        int words = ipv6 ? 4 : 1;
        if (hex.Length != words * 8)
            return null;

        var bytes = new byte[words * 4];
        for (int w = 0; w < words; w++)
        {
            if (!uint.TryParse(hex.Slice(w * 8, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint word))
                return null;
            for (int j = 0; j < 4; j++)
            {
                int shift = BitConverter.IsLittleEndian ? 8 * j : 8 * (3 - j);
                bytes[w * 4 + j] = (byte)((word >> shift) & 0xFF);
            }
        }
        try
        {
            return new IPAddress(bytes);
        }
        catch
        {
            return null;
        }
    }
}
