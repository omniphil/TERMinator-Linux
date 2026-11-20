using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TERMinator.Linux.Models;
using TERMinator.Linux.Services;

namespace TERMinator.Linux.ViewModels;

public partial class TransferViewModel : ObservableObject, IDisposable
{
    private readonly ConnectionManager? _connectionManager;
    private CancellationTokenSource? _pollCts;

    [ObservableProperty] private string _fileName = string.Empty;

    /// <summary>
    /// "Auto close" checkbox. On by default for every transfer; untick it to
    /// keep the window (and its diagnostics) up after the transfer ends.
    /// </summary>
    [ObservableProperty] private bool _autoClose = true;

    partial void OnAutoCloseChanged(bool value)
    {
        if (!value)
            CancelAutoClose();
    }

    /// <summary>Full path, shown dimmed under the name and as the tooltip.
    /// Kept separate from <see cref="FileName"/> so a long path can be trimmed
    /// without hiding which file is actually moving.</summary>
    [ObservableProperty] private string _filePath = string.Empty;

    /// <summary>The path with its middle elided, for display. Computed rather
    /// than left to TextTrimming so the FILENAME survives: plain trimming cuts
    /// the tail, which is the only part that says which file this is.</summary>
    [ObservableProperty] private string _filePathDisplay = string.Empty;

    /// <summary>"26.4 MB of 60.0 MB" — the byte readout beside the bar.</summary>
    [ObservableProperty] private string _bytesText = string.Empty;

    /// <summary>"42%" — shown over the progress bar.</summary>
    [ObservableProperty] private string _percentText = string.Empty;
    [ObservableProperty] private string _fileCounter = string.Empty;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private string _statusText = "Idle";
    [ObservableProperty] private string _speedText = string.Empty;
    [ObservableProperty] private bool _isTransferring;

    /// <summary>
    /// The banner across the top of the dialog. The window's Title is set per
    /// protocol by the MainWindow handlers; this is the same fact rendered
    /// inside the dialog, and it is a view-model property (rather than the
    /// code-behind reaching into the TextBlock) so it follows the same binding
    /// route as every other field on this dialog. Every Start* method below
    /// sets it -- default it to ZMODEM so an unset path degrades to what the
    /// header used to say rather than to an empty bar.
    /// </summary>
    [ObservableProperty] private string _protocolTitle = "ZMODEM FILE TRANSFER";

    /// <summary>"XMODEM" / "YMODEM" for a mode word from NativeBridge.XferMode.</summary>
    private static string XyName(int mode) =>
        (mode & NativeBridge.XferMode.Ymodem) != 0 ? "YMODEM" : "XMODEM";

    /// <summary>
    /// One line of the protocol status feed. <see cref="Level"/> is the
    /// severity char the native side stamped on it ("I", "W", "E") and drives
    /// the colour in the dialog. Avalonia has no DataTrigger, so the dialog
    /// colours a line through style classes bound to the Is* flags below.
    /// </summary>
    public sealed record TransferLogEntry(string Level, string Text)
    {
        public bool IsLocal   => Level == "S";
        public bool IsWarning => Level == "W";
        public bool IsError   => Level == "E";
        public bool IsDiag    => Level == "G";
    }

    /// <summary>
    /// Running protocol log, mirrored from the native ring buffer -- the same
    /// feed SyncTERM shows under its progress bar. Capped so a long transfer
    /// cannot grow it without bound.
    /// </summary>
    public ObservableCollection<TransferLogEntry> LogLines { get; } = new();

    private const int MaxLogLines = 500;

    /// <summary>A byte position the protocol prefixes to most of its messages.</summary>
    private static readonly Regex LeadingBytePosition = new(@"^\d+\s+", RegexOptions.Compiled);

    /// <summary>
    /// Raw protocol text rewritten for a human reader.
    ///
    /// The engine's own wording is C function names and frame mnemonics --
    /// "zmodem_recv_header frame: ZRINIT" -- which is right for a trace file
    /// and wrong for a dialog. The trace file still gets the raw text; only
    /// what reaches the screen is rewritten. Order matters: specific patterns
    /// must precede the catch-alls.
    /// </summary>
    private static readonly (Regex Pattern, string Replacement)[] FriendlyText =
    {
        (new Regex(@"^ZMODEM session started for (\w+).*$"),   "Connected over $1"),
        (new Regex(@"^Starting ZMODEM send: .*[\\/]([^\\/]+)$"), "Preparing to send $1"),
        (new Regex(@"^Starting ZMODEM send: (.+)$"),           "Preparing to send $1"),
        (new Regex(@"^Sending ZRQINIT.*$"),                    "Requesting transfer"),
        (new Regex(@"^zmodem_get_zrinit$"),                    "Waiting for the receiver"),
        // The native log joins the engine's two-line message with spaces; keep the flags
        (new Regex(@"^Receiver requested mode \(0x[0-9A-Fa-f]+\):\s*(.+)$"), "Receiver options: $1"),
        (new Regex(@"^Receiver requested mode.*$"),            "Receiver options:"),
        (new Regex(@"^Receiver specified buffer size of: (\d+)$"), "Receiver buffer: $1 bytes"),
        (new Regex(@"^Sending ZFILE frame.*$"),                "Sending file details"),
        (new Regex(@"^Purging data in receive buffer.*$"),     "Clearing the receive buffer"),
        (new Regex(@"^Received valid ZACK$"),                  "Acknowledged"),
        (new Regex(@"^Back-channel traffic detected$"),        "Receiver sent data back"),
        (new Regex(@"^Unexpected back-channel data received: (.+)$"), "Receiver sent $1"),
        (new Regex(@"^Sending End-of-File \(ZEOF\) frame$"),   "End of file sent"),
        (new Regex(@"^Sending ZNAK.*$"),                       "Requesting a resend"),
        (new Regex(@"^Sending ZSKIP.*$"),                      "Skipping this file"),
        (new Regex(@"^zmodem_get_zfin$"),                      "Closing the session"),
        (new Regex(@"^Finishing Session \(Sending ZFIN\)$"),   "Closing the session"),
        (new Regex(@"^zmodem_recv_header frame: ZRINIT$"),     "Receiver ready"),
        (new Regex(@"^zmodem_recv_header frame: ZRPOS$"),      "Receiver set the start position"),
        (new Regex(@"^zmodem_recv_header frame: ZACK$"),       "Acknowledged"),
        (new Regex(@"^zmodem_recv_header frame: ZFIN$"),       "Receiver closed the session"),
        (new Regex(@"^zmodem_recv_header frame: ZCAN$"),       "Receiver cancelled the transfer"),
        (new Regex(@"^zmodem_recv_header frame: (\w+)$"),      "Received $1"),
        (new Regex(@"^Whole file delivered \(\d+ bytes\); waiting up to (\d+) seconds.*$"),
                                                               "File delivered - waiting up to $1s for the receiver to confirm"),
        (new Regex(@"^No acknowledgement of ZEOF after (\d+) seconds.*$"),
                                                               "No confirmation after $1s - treating the file as delivered"),
        (new Regex(@"^Treating transfer as complete.*$"),      "The whole file was delivered"),
        (new Regex(@"^Treating '(.+)' as complete.*$"),        "$1 was delivered in full"),
        (new Regex(@"^zmodem_recv_raw TIMEOUT \((\d+) seconds\)$"), "Timed out after $1s waiting for the receiver"),
        (new Regex(@"^zmodem_recv_header TIMEOUT$"),           "Timed out waiting for a reply"),
        (new Regex(@"^ERROR #(\d+): TIMEOUT$"),                "Error $1: timed out"),
        (new Regex(@"^Transfer failed on receipt of: ZCAN$"),  "The receiver cancelled the transfer"),
        (new Regex(@"^Aborting receive$"),                     "Aborting"),
        (new Regex(@"^ZMODEM send cancelled$"),                "Transfer cancelled"),
        (new Regex(@"^ZMODEM send failed$"),                   "Transfer failed"),
        (new Regex(@"^ZMODEM receive cancelled$"),             "Transfer cancelled"),
        (new Regex(@"^Forcing Telnet binary mode.*$"),          "Switched the link to binary mode"),
        (new Regex(@"^Starting ZMODEM receive to: (.+)$"),      "Receiving into $1"),
        (new Regex(@"^Batch send file (\d+)/(\d+): (.+)$"),     "File $1 of $2: $3"),
        (new Regex(@"^Incoming: (.+?) \(.*$"),                  "Incoming file: $1"),
        (new Regex(@"^No ZFIN from the receiver.*$"),           "Receiver did not sign off, but the file was delivered"),
        (new Regex(@"^IDLE WATCHDOG: no transfer progress for (\d+) seconds.*$"),
                                                               "No progress for $1s - giving up"),
        (new Regex(@"^Too many errors \((\d+)\).*$"),          "Too many errors ($1) - giving up"),
        (new Regex(@"^Falling back to 8-bit Checksum mode.*$"), "Falling back to 8-bit checksum"),
        (new Regex(@"^Fetching YMODEM header block$"),          "Reading the YMODEM header"),
        (new Regex(@"^Sending YMODEM termination block$"),      "Ending the batch"),
        (new Regex(@"^Received YMODEM termination block$"),     "Sender ended the batch"),
        (new Regex(@"^No YMODEM header block after transfer.*$"), "No further files"),
        (new Regex(@"^Sender is XMODEM, not YMODEM.*$"),        "Sender is XMODEM, falling back"),
        (new Regex(@"^No ACK after the YMODEM terminating block$"), "Receiver did not acknowledge the end of the batch"),
        (new Regex(@"^Cancelled locally$"),                     "Cancelled"),
        (new Regex(@"^Failed to open file: (.+?) - .*$"),       "Could not open $1"),
        (new Regex(@"^Sending ([^\s].*\.[A-Za-z0-9]{1,8})$"),  "Transferring $1"),
    };

    /// <summary>
    /// Internal bookkeeping that means nothing to someone watching a transfer.
    /// Still written to the trace file; just never shown.
    /// </summary>
    private static readonly Regex[] DroppedText =
    {
        new(@"^Calling zmodem_recv_files"),
        new(@"^Download directory (set to|not set|was not set)"),
        new(@"^.+ is an existing directory$"),
        new(@"^zmodem_recv_files returned"),
    };

    private static readonly Regex CompleteWithBytes =
        new(@"^ZMODEM (send|receive) complete: (\d+) bytes$", RegexOptions.Compiled);

    /// <summary>
    /// Turn a raw protocol message into something worth a line on screen, or
    /// null to drop it.
    ///
    /// The leading byte position goes: the progress readout above already shows
    /// it, and repeating it here just pushes the useful lines off the top. The
    /// data-phase messages collapse to one "Transferring..." which, being
    /// identical each time, is then deduplicated to a single line.
    /// </summary>
    private static string Prettify(string raw)
    {
        string text = LeadingBytePosition.Replace(raw, string.Empty);

        if (text.Contains("data subpacket", StringComparison.Ordinal)
            || text.StartsWith("Progress:", StringComparison.Ordinal)
            || text.StartsWith("zmodem_send_from", StringComparison.Ordinal)
            || text.StartsWith("Sending ZDATA", StringComparison.Ordinal))
        {
            return "Transferring...";
        }

        // Byte counts read better as KB/MB than as a bare 8-digit number.
        var done = CompleteWithBytes.Match(text);
        if (done.Success && long.TryParse(done.Groups[2].Value, out long completed))
        {
            string verb = done.Groups[1].Value == "send" ? "sent" : "received";
            return $"Transfer complete - {FormatBytes(completed)} {verb}";
        }

        foreach (var drop in DroppedText)
        {
            if (drop.IsMatch(text))
                return string.Empty;
        }

        foreach (var (pattern, replacement) in FriendlyText)
        {
            if (pattern.IsMatch(text))
                return pattern.Replace(text, replacement);
        }

        return text;
    }

    public event Action? TransferCompleted;
    public event Action<string>? TransferFailed;

    /// <summary>Raised when the post-transfer countdown expires and the
    /// dialog should close itself.</summary>
    public event Action? AutoCloseRequested;

    /// <summary>Seconds the finished dialog stays up before closing itself
    /// (matches the other TERMinator clients).</summary>
    private const int AutoCloseSeconds = 5;

    private CancellationTokenSource? _autoCloseCts;

    /// <summary>
    /// Highest byte count shown for the current file.
    ///
    /// The protocol can legitimately report a position that moves BACKWARDS --
    /// an error rewinds the sender to the last acknowledged offset -- and a
    /// byte readout that counts down looks like the transfer is undoing itself.
    /// The bar and the readout only ever move forward; the status log is where
    /// a rewind is reported, in words.
    /// </summary>
    private long _displayHighWater;

    // Milestone annotations. The protocol itself says almost nothing between
    // "sending" and "end of file" -- on a slow link that is several minutes of
    // a frozen-looking dialog -- so the view model narrates the gap from data
    // it already has. These are OUR lines, not the board's, and are marked with
    // level "S" so they are coloured differently from protocol traffic.
    private long _transferStartTicks;

    // A file the board declined (ZSKIP: it usually has one by that name already). The engine still ends the
    // session "complete", so without this the window said "the file was delivered" for 0 bytes sent.
    private bool _skipped;
    private long _completedBytes = -1;
    private long _lastRawPosition;
    private long _lastMovementTicks;
    private bool _stallAnnounced;
    private long _confirmWaitStartTicks;
    private long _lastConfirmNoticeTicks;
    private bool _announcedFileSize;

    /// <summary>How often to re-report a stalled wait for the receiver.</summary>
    private const int ConfirmNoticeIntervalMs = 15000;

    /// <summary>Silence on the wire before we call it a stall.</summary>
    private const int StallNoticeMs = 12000;

    // Throughput tracking (per file, so a batch restarts the meter on each one)
    private int _speedFileIndex = -1;
    private long _speedStartTicks;
    private long _speedStartBytes;
    private long _speedLastTicks;
    private long _speedLastBytes;
    private double _speedCps;

    public TransferViewModel(ConnectionManager? connectionManager = null)
    {
        _connectionManager = connectionManager;
    }

    [RelayCommand]
    private void CancelTransfer()
    {
        CancelAutoClose();
        NativeBridge.syncterm_transfer_cancel();
        _pollCts?.Cancel();
        StatusText = "Cancelled";
        IsTransferring = false;
    }

    /// <summary>
    /// Reports a transfer that never got off the ground, from the background
    /// thread the native call runs on.
    ///
    /// Every Start* method funnels its failures through here, INCLUDING the
    /// exception path. Without a catch, a throw out of the P/Invoke -- a shared
    /// library that does not export the entry point, a marshalling fault -- is captured
    /// silently by the Task and never observed, so the dialog sits on
    /// "Receiving..." forever with no error and IsTransferring stuck true,
    /// which is indistinguishable from "the BBS never answered". Anything that
    /// stops a transfer starting has to be visible.
    /// </summary>
    private void ReportStartFailure(string status, string detail)
    {
        Dispatcher.UIThread.Invoke(() =>
        {
            if (!IsTransferring) return;
            StatusText = status;
            IsTransferring = false;
            _pollCts?.Cancel();
            TransferFailed?.Invoke(detail);
        });
    }

    /// <summary>Stops the auto-close countdown (any user interaction wins).</summary>
    public void CancelAutoClose()
    {
        _autoCloseCts?.Cancel();
        _autoCloseCts?.Dispose();
        _autoCloseCts = null;
    }

    /// <summary>
    /// Counts down on the status line and then asks the dialog to close.
    /// </summary>
    private void StartAutoClose(string statusMessage)
    {
        CancelAutoClose();
        if (!AutoClose)
        {
            StatusText = statusMessage;
            return;
        }
        _autoCloseCts = new CancellationTokenSource();
        var token = _autoCloseCts.Token;

        Task.Run(async () =>
        {
            try
            {
                for (int secondsLeft = AutoCloseSeconds; secondsLeft >= 1; secondsLeft--)
                {
                    int shown = secondsLeft;
                    Dispatcher.UIThread.Invoke(
                        () => StatusText = $"{statusMessage} (closing in {shown}s)");
                    await Task.Delay(1000, token);
                }

                Dispatcher.UIThread.Invoke(() =>
                {
                    StatusText = statusMessage;
                    AutoCloseRequested?.Invoke();
                });
            }
            catch (OperationCanceledException)
            {
                // User interacted — leave the status line as-is.
            }
        }, token);
    }

    /// <summary>
    /// Feeds a progress sample into the throughput meter and returns the
    /// display string ("12.3 KB/s  ETA 0:42"), or "" when not yet meaningful.
    /// </summary>
    private string UpdateSpeed(int fileIndex, long current, long total)
    {
        long nowTicks = Environment.TickCount64;

        // Restart the meter for each file of a batch
        if (fileIndex != _speedFileIndex)
        {
            _speedFileIndex = fileIndex;
            _speedStartTicks = 0;
            _speedCps = 0;
            _displayHighWater = 0;   // a new file starts its readout from zero
        }

        if (current <= 0)
            return string.Empty;

        if (_speedStartTicks == 0)
        {
            _speedStartTicks = _speedLastTicks = nowTicks;
            _speedStartBytes = _speedLastBytes = current;
            return string.Empty;
        }

        long deltaMs = nowTicks - _speedLastTicks;
        if (deltaMs >= 500)
        {
            double sampleCps = (current - _speedLastBytes) * 1000.0 / deltaMs;
            if (sampleCps < 0) sampleCps = 0;
            // Light smoothing so the readout doesn't jitter every tick
            _speedCps = _speedCps <= 0 ? sampleCps : (_speedCps * 0.6) + (sampleCps * 0.4);
            _speedLastTicks = nowTicks;
            _speedLastBytes = current;
        }
        else if (_speedCps <= 0)
        {
            long elapsedMs = nowTicks - _speedStartTicks;
            if (elapsedMs <= 0) return string.Empty;
            _speedCps = (current - _speedStartBytes) * 1000.0 / elapsedMs;
        }

        if (_speedCps <= 0)
            return string.Empty;

        string speed = $"{FormatBytes((long)_speedCps)}/s";

        if (total > current)
        {
            var eta = TimeSpan.FromSeconds((total - current) / _speedCps);
            if (eta.TotalHours < 100)
            {
                speed += eta.TotalHours >= 1
                    ? $"   ETA {(int)eta.TotalHours}:{eta.Minutes:00}:{eta.Seconds:00}"
                    : $"   ETA {eta.Minutes}:{eta.Seconds:00}";
            }
        }

        return speed;
    }

    public void StartReceive()
    {
        ProtocolTitle = "ZMODEM FILE TRANSFER";
        NativeBridge.syncterm_transfer_reset();
        // Drain any stale data from the connection buffer into ZMODEM's
        // internal buffer so the receive sees the full ZMODEM header.
        NativeBridge.syncterm_push_zmodem_buffer();

        StatusText = "Receiving...";
        IsTransferring = true;
        StartPolling();
        _zmodemReceive = true;

        // Run blocking native call on background thread so the UI stays responsive
        Task.Run(() =>
        {
            try
            {
                int result = NativeBridge.syncterm_zmodem_receive();
                if (result < 0)
                    ReportStartFailure("Failed to start receive", $"Failed to start ZMODEM receive (error {result})");
            }
            catch (Exception ex)
            {
                ReportStartFailure("Failed to start receive", $"Failed to start ZMODEM receive: {ex.Message}");
            }
            finally
            {
                _connectionManager?.SetTransferInProgress(false);
            }
        });
    }

    // Uploads are the only direction the 8K toggle changes, so only they say so
    private static string SendTitle =>
        App.SettingsService.Settings.Zmodem8k ? "ZMODEM 8K FILE TRANSFER" : "ZMODEM FILE TRANSFER";

    public void StartSend(string filePath)
    {
        ProtocolTitle = SendTitle;
        if (!System.IO.File.Exists(filePath))
        {
            _connectionManager?.SetTransferInProgress(false);
            StatusText = "File not found";
            TransferFailed?.Invoke($"File not found: {filePath}");
            return;
        }

        FileName = System.IO.Path.GetFileName(filePath);
        NativeBridge.syncterm_transfer_reset();
        // NOTE: do NOT call syncterm_push_zmodem_buffer() here. It is a
        // receive-side helper that drains the connection buffer; on a send
        // the BBS has already started its receiver and is retransmitting
        // ZRINIT, so draining throws away the handshake we are about to
        // wait for. (The Android build only calls it from startReceive.)

        StatusText = "Sending...";
        IsTransferring = true;
        StartPolling();

        // Run blocking native call on background thread so the UI stays responsive
        Task.Run(() =>
        {
            try
            {
                int result = NativeBridge.syncterm_zmodem_send(filePath);
                if (result < 0)
                    ReportStartFailure("Failed to start send", $"Failed to start ZMODEM send (error {result})");
            }
            catch (Exception ex)
            {
                ReportStartFailure("Failed to start send", $"Failed to start ZMODEM send: {ex.Message}");
            }
            finally
            {
                _connectionManager?.SetTransferInProgress(false);
            }
        });
    }

    /// <summary>
    /// Receive via X/YMODEM.
    ///
    /// Nothing auto-detects here the way ZMODEM does — the BBS is already NAKing
    /// and the user has told us which protocol they chose on the board.
    /// <paramref name="savePath"/> must be a full destination path for XMODEM
    /// (the protocol has no filename in it); pass null for YMODEM.
    /// </summary>
    public void StartXmodemReceive(string? savePath, int mode)
    {
        ProtocolTitle = $"{XyName(mode)} FILE TRANSFER";
        NativeBridge.syncterm_transfer_reset();
        // NOTE: no syncterm_push_zmodem_buffer() here. That drains the connection
        // buffer into ZMODEM's own; X/YMODEM has no such buffer, and the receiver
        // is the one that speaks first anyway — we send the NAK/'C', the BBS
        // answers. Nothing is waiting for us to pick up.

        if (savePath != null)
            FileName = System.IO.Path.GetFileName(savePath);

        StatusText = "Receiving...";
        IsTransferring = true;
        StartPolling();

        Task.Run(() =>
        {
            try
            {
                int result = NativeBridge.syncterm_xmodem_receive(savePath, mode);
                if (result < 0)
                    ReportStartFailure("Failed to start receive", $"Failed to start the receive (error {result})");
            }
            catch (Exception ex)
            {
                ReportStartFailure("Failed to start receive", $"Failed to start the receive: {ex.Message}");
            }
            finally
            {
                _connectionManager?.SetTransferInProgress(false);
            }
        });
    }

    /// <summary>
    /// Send one file via X/YMODEM. Single file only — the boards that need
    /// X/YMODEM at all do not offer batch uploads.
    /// </summary>
    public void StartXmodemSend(string filePath, int mode)
    {
        ProtocolTitle = $"{XyName(mode)} FILE TRANSFER";
        if (!System.IO.File.Exists(filePath))
        {
            _connectionManager?.SetTransferInProgress(false);
            StatusText = "File not found";
            TransferFailed?.Invoke($"File not found: {filePath}");
            return;
        }

        FileName = System.IO.Path.GetFileName(filePath);
        NativeBridge.syncterm_transfer_reset();

        StatusText = "Sending...";
        IsTransferring = true;
        StartPolling();

        Task.Run(() =>
        {
            try
            {
                int result = NativeBridge.syncterm_xmodem_send(filePath, mode);
                if (result < 0)
                    ReportStartFailure("Failed to start send", $"Failed to start the send (error {result})");
            }
            catch (Exception ex)
            {
                ReportStartFailure("Failed to start send", $"Failed to start the send: {ex.Message}");
            }
            finally
            {
                _connectionManager?.SetTransferInProgress(false);
            }
        });
    }

    /// <summary>
    /// Send multiple files in a single ZMODEM batch session. Falls back to the
    /// single-file path when only one valid file is supplied.
    /// </summary>
    public void StartSend(string[] filePaths)
    {
        ProtocolTitle = SendTitle;
        var files = new List<string>();
        if (filePaths != null)
        {
            foreach (var p in filePaths)
            {
                if (System.IO.File.Exists(p)) files.Add(p);
            }
        }

        if (files.Count == 0)
        {
            _connectionManager?.SetTransferInProgress(false);
            StatusText = "File not found";
            TransferFailed?.Invoke("No valid files to send");
            return;
        }

        if (files.Count == 1)
        {
            StartSend(files[0]);
            return;
        }

        FileName = System.IO.Path.GetFileName(files[0]);
        FileCounter = $"(1/{files.Count})";
        NativeBridge.syncterm_transfer_reset();
        // No push_zmodem_buffer() on the send path — see StartSend(string).

        // Build the native batch queue
        NativeBridge.syncterm_upload_queue_clear();
        foreach (var f in files)
        {
            NativeBridge.syncterm_upload_queue_add(f);
        }

        StatusText = "Sending...";
        IsTransferring = true;
        StartPolling();

        // Run blocking native call on background thread so the UI stays responsive
        Task.Run(() =>
        {
            try
            {
                int result = NativeBridge.syncterm_zmodem_send_batch();
                if (result < 0)
                    ReportStartFailure("Failed to start send", $"Failed to start ZMODEM batch send (error {result})");
            }
            catch (Exception ex)
            {
                ReportStartFailure("Failed to start send", $"Failed to start ZMODEM batch send: {ex.Message}");
            }
            finally
            {
                _connectionManager?.SetTransferInProgress(false);
            }
        });
    }

    // A ZMODEM download is running: the title says 8K once the board sends an 8K block
    private bool _zmodemReceive;

    private void StartPolling()
    {
        // Start each transfer with a clean feed on both sides of the boundary.
        NativeBridge.syncterm_clear_transfer_log();
        Dispatcher.UIThread.Invoke(() => LogLines.Clear());

        _transferStartTicks = Environment.TickCount64;
        _skipped = false;
        _completedBytes = -1;
        _lastRawPosition = 0;
        _lastMovementTicks = 0;
        _stallAnnounced = false;
        _confirmWaitStartTicks = 0;
        _announcedFileSize = false;
        _zmodemReceive = false;

        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = new CancellationTokenSource();
        var token = _pollCts.Token;
        Task.Run(() => PollTransferProgress(token), token);
    }

    /// <summary>
    /// Move any new native status lines into <see cref="LogLines"/>. Each
    /// native line is "&lt;level char&gt;&lt;message&gt;"; lines are handed out
    /// once, so this never duplicates.
    /// </summary>
    private void DrainTransferLog(byte[] buf)
    {
        int len = NativeBridge.syncterm_get_transfer_log(buf, buf.Length);
        if (len <= 0)
            return;

        var entries = new List<TransferLogEntry>();
        foreach (string raw in NativeBridge.GetNativeString(buf, len).Split('\n'))
        {
            if (raw.Length < 2)
                continue;
            if (raw.Contains("File skipped by receiver", StringComparison.Ordinal))
                _skipped = true;
            var done = CompleteWithBytes.Match(LeadingBytePosition.Replace(raw[1..], string.Empty));
            if (done.Success && long.TryParse(done.Groups[2].Value, out long completed))
                _completedBytes = completed;
            // 'G' = the native diagnostics, already written for people: shown as-is
            string pretty = raw[0] == 'G' ? raw[1..] : Prettify(raw[1..]);
            if (_skipped && pretty == "Receiver did not sign off, but the file was delivered")
                pretty = "Receiver did not sign off";
            if (_skipped && _completedBytes == 0 && pretty.StartsWith("Transfer complete", StringComparison.Ordinal))
                pretty = "Nothing sent: the board skipped the file (it may already have one by that name)";
            if (pretty.Length == 0)
                continue;   // dropped as internal bookkeeping
            entries.Add(new TransferLogEntry(raw[..1], pretty));
        }

        if (entries.Count == 0)
            return;

        Dispatcher.UIThread.Invoke(() =>
        {
            foreach (var entry in entries)
                AppendLogEntry(entry);
        });
    }

    /// <summary>
    /// Append one line, dropping it if it merely repeats the line above.
    /// MUST be called on the UI thread.
    /// </summary>
    private void AppendLogEntry(TransferLogEntry entry)
    {
        var last = LogLines.Count > 0 ? LogLines[^1] : null;
        if (last is not null && last.Level == entry.Level && last.Text == entry.Text)
            return;

        LogLines.Add(entry);
        while (LogLines.Count > MaxLogLines)
            LogLines.RemoveAt(0);
    }

    /// <summary>Add one of our own narration lines (level "S").</summary>
    private void AddLocalLine(string text) =>
        Dispatcher.UIThread.Invoke(() => AppendLogEntry(new TransferLogEntry("S", text)));

    /// <summary>
    /// Narrate the parts of a transfer the protocol is silent through: the
    /// size up front, a mark every 10%, and -- the one that matters most -- a
    /// heartbeat while we sit waiting for a receiver that has taken delivery
    /// but not yet confirmed. That wait can legitimately run into minutes, and
    /// without a line here the dialog is indistinguishable from a hang.
    /// </summary>
    private void AnnotateProgress(long raw, long shown, long total, string speed)
    {
        long now = Environment.TickCount64;

        // A position that moves BACKWARDS means the receiver rejected something
        // and the sender has rewound to the last acknowledged offset. The bar
        // deliberately hides that (it would look like the transfer undoing
        // itself), so this is the only place it is visible -- and it is worth
        // seeing, because a rewind on a nearly-finished file is the shape of a
        // transfer about to go badly wrong.
        if (_lastRawPosition > 0 && raw > 0 && raw < _lastRawPosition - 1024)
        {
            Dispatcher.UIThread.Invoke(() => AppendLogEntry(
                new TransferLogEntry("W",
                    $"Receiver rejected data - resending from {FormatBytes(raw)}")));
        }

        // Stall detection. Not redundant with the bar: a bar that has stopped
        // moving looks identical to a slow link, and this says which it is.
        if (raw != _lastRawPosition)
        {
            _lastRawPosition = raw;
            _lastMovementTicks = now;
            _stallAnnounced = false;
        }
        else if (!_stallAnnounced && _lastMovementTicks > 0
                 && now - _lastMovementTicks >= StallNoticeMs
                 && (total <= 0 || shown < total))
        {
            _stallAnnounced = true;
            Dispatcher.UIThread.Invoke(() => AppendLogEntry(
                new TransferLogEntry("W",
                    $"No data moved for {(now - _lastMovementTicks) / 1000}s")));
        }

        if (total <= 0)
            return;

        if (!_announcedFileSize)
        {
            _announcedFileSize = true;
            AddLocalLine($"File size: {FormatBytes(total)}");
        }

        if (shown < total)
        {
            _confirmWaitStartTicks = 0;
            return;
        }

        // Every byte is out but the transfer has not finished: we are waiting
        // on the receiver's acknowledgement.
        if (_confirmWaitStartTicks == 0)
        {
            _confirmWaitStartTicks = _lastConfirmNoticeTicks = now;
            AddLocalLine("All bytes sent - waiting for the receiver to confirm");
            return;
        }

        if (now - _lastConfirmNoticeTicks >= ConfirmNoticeIntervalMs)
        {
            _lastConfirmNoticeTicks = now;
            AddLocalLine($"Still waiting for the receiver... ({(now - _confirmWaitStartTicks) / 1000}s)");
        }
    }

    private static string FormatDuration(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}h {t.Minutes}m {t.Seconds}s"
            : t.TotalMinutes >= 1 ? $"{t.Minutes}m {t.Seconds}s" : $"{t.Seconds}s";
    }

    private async Task PollTransferProgress(CancellationToken ct)
    {
        var progressBuf = new long[2];
        var fileNameBuf = new byte[512];
        var logBuf = new byte[16384];

        while (!ct.IsCancellationRequested)
        {
            try
            {
                int state = NativeBridge.syncterm_get_transfer_state();

                DrainTransferLog(logBuf);

                // Downloads: the board picks the block size, so say 8K only once one arrives
                if (_zmodemReceive && NativeBridge.syncterm_zmodem_largest_rx_block() >= 8192)
                {
                    _zmodemReceive = false;
                    Dispatcher.UIThread.Invoke(() => ProtocolTitle = "ZMODEM 8K FILE TRANSFER");
                }

                // Get file name
                int nameLen = NativeBridge.syncterm_get_transfer_filename(fileNameBuf, fileNameBuf.Length);
                string currentFileName = nameLen > 0
                    ? NativeBridge.GetNativeString(fileNameBuf, nameLen)
                    : FileName;

                // Get batch file index (0/0 for single-file transfers)
                NativeBridge.syncterm_get_transfer_file_index(out int fileIdx, out int fileTotal);
                string counterText = fileTotal > 1 ? $"({fileIdx}/{fileTotal})" : string.Empty;

                // Get progress
                NativeBridge.syncterm_get_transfer_progress(progressBuf);
                long current = progressBuf[0];
                long total = progressBuf[1];

                string speedDisplay = UpdateSpeed(fileIdx, current, total);

                // Never let the readout run backwards (see _displayHighWater).
                if (current > _displayHighWater)
                    _displayHighWater = current;
                long shown = _displayHighWater;

                double pct = total > 0 ? (double)shown / total * 100.0 : 0;
                string pctText = total > 0
                    ? $"{FormatBytes(shown)} of {FormatBytes(total)}"
                    : FormatBytes(shown);
                string percentDisplay = total > 0 ? $"{pct:F0}%" : string.Empty;
                AnnotateProgress(current, shown, total, speedDisplay);
                string shortName = FileNameOnly(currentFileName);
                // Only show a path line when there is actually a path to show.
                string pathDisplay = shortName == currentFileName
                    ? string.Empty
                    : ElidePath(currentFileName);

                Dispatcher.UIThread.Invoke(() =>
                {
                    FileName = shortName;
                    FilePath = currentFileName;
                    FilePathDisplay = pathDisplay;
                    FileCounter = counterText;
                    Progress = pct;
                    ProgressText = pctText;
                    BytesText = pctText;
                    PercentText = percentDisplay;
                    SpeedText = speedDisplay;
                });

                var transferStatus = (TransferStatus)state;

                // The native side sets the terminal state and THEN logs its
                // closing lines, so one more drain after a short beat is what
                // puts "send complete" / the failure reason on screen.
                if (transferStatus is TransferStatus.Complete
                                   or TransferStatus.Error
                                   or TransferStatus.Cancelled)
                {
                    await Task.Delay(60, ct);
                    DrainTransferLog(logBuf);
                }

                switch (transferStatus)
                {
                    case TransferStatus.Complete:
                        bool skippedAll = _skipped && _completedBytes == 0;
                        Dispatcher.UIThread.Invoke(() =>
                        {
                            StatusText = skippedAll ? "Skipped by the board" : "Transfer complete";
                            SpeedText = FormatAverageSpeed();
                            Progress = 100;
                            PercentText = "100%";
                            if (total > 0)
                                BytesText = $"{FormatBytes(total)} of {FormatBytes(total)}";
                            AppendLogEntry(new TransferLogEntry("S",
                                $"Finished in {FormatDuration(Environment.TickCount64 - _transferStartTicks)}"
                                + $" - average {FormatAverageSpeed()}"));
                            IsTransferring = false;
                            TransferCompleted?.Invoke();
                            StartAutoClose(skippedAll ? "Skipped by the board" : "Transfer complete");
                        });
                        return;

                    case TransferStatus.Error:
                        var errorBuf = new byte[1024];
                        int errLen = NativeBridge.syncterm_get_transfer_error(errorBuf, errorBuf.Length);
                        string errorMsg = errLen > 0
                            ? NativeBridge.GetNativeString(errorBuf, errLen)
                            : "Transfer error";
                        Dispatcher.UIThread.Invoke(() =>
                        {
                            StatusText = errorMsg;
                            IsTransferring = false;
                            TransferFailed?.Invoke(errorMsg);
                            StartAutoClose(errorMsg);
                        });
                        return;

                    case TransferStatus.Cancelled:
                        Dispatcher.UIThread.Invoke(() =>
                        {
                            StatusText = "Cancelled";
                            IsTransferring = false;
                            StartAutoClose("Cancelled");
                        });
                        return;

                    default:
                        // Still in progress
                        Dispatcher.UIThread.Invoke(() =>
                        {
                            StatusText = transferStatus == TransferStatus.Receiving
                                ? "Receiving..."
                                : "Sending...";
                        });
                        break;
                }

                await Task.Delay(200, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Don't crash on transient poll errors
            }
        }
    }

    /// <summary>Average throughput over the whole (last) file.</summary>
    private string FormatAverageSpeed()
    {
        long elapsedMs = Environment.TickCount64 - _speedStartTicks;
        if (_speedStartTicks == 0 || elapsedMs <= 0)
            return SpeedText;

        long bytes = _speedLastBytes - _speedStartBytes;
        if (bytes <= 0)
            return SpeedText;

        return $"{FormatBytes(bytes * 1000 / elapsedMs)}/s avg";
    }

    public void Dispose()
    {
        CancelAutoClose();
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = null;
    }

    /// <summary>Last path segment, for either separator. The native side hands
    /// back a full path early in a send and a bare name once the protocol takes
    /// over, so this has to cope with both.</summary>
    /// <summary>
    /// Elide the middle of a path so both ends stay readable:
    /// /home/me/.../Android/delete-me5.zip
    /// Cutting on separators keeps whole path segments rather than slicing
    /// through the middle of a folder name.
    /// </summary>
    private static string ElidePath(string path, int max = 62)
    {
        if (string.IsNullOrEmpty(path) || path.Length <= max)
            return path ?? string.Empty;

        string name = FileNameOnly(path);
        int keepHead = Math.Max(0, max - name.Length - 5);
        if (keepHead <= 0)
            return name;   // the filename alone already fills the line

        string head = path[..keepHead];
        int lastSep = head.LastIndexOfAny(new[] { '\\', '/' });
        if (lastSep > 0)
            head = head[..lastSep];

        return $"{head}/.../{name}";
    }

    private static string FileNameOnly(string path)
    {
        if (string.IsNullOrEmpty(path))
            return string.Empty;
        int cut = path.LastIndexOfAny(new[] { '\\', '/' });
        return cut >= 0 && cut < path.Length - 1 ? path[(cut + 1)..] : path;
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1048576 => $"{bytes / 1024.0:F1} KB",
        < 1073741824 => $"{bytes / 1048576.0:F1} MB",
        _ => $"{bytes / 1073741824.0:F2} GB"
    };
}
