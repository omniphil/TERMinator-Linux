using System.Text.Json.Serialization;

namespace TERMinator.Linux.Models;

public class SavedConnection
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 23;
    public ConnectionProtocol Protocol { get; set; } = ConnectionProtocol.Telnet;
    public string? Username { get; set; }
    public bool SaveCredentials { get; set; }
    public string? FontName { get; set; }
    public int ScreenMode { get; set; }

    /// <summary>
    /// Show the BBS's own status line (SyncTERM's <c>nostatus</c>, inverted). Off by default,
    /// which is what the native side has always defaulted to — this just makes it a per-board
    /// choice the way the macOS client does. Not the app's own status bar at the bottom of the
    /// window, which is always on.
    /// </summary>
    public bool ShowStatusBar { get; set; }
    public int SortOrder { get; set; }
    public DateTime LastConnected { get; set; }
    public string? SnapshotPath { get; set; }

    /// <summary>
    /// A linked window a board opened from a call (12.73, Rainwire docs/MRC_WINDOW.md): an ad-hoc entry for the same
    /// host, port and protocol as its parent, never in the phonebook. It sends link=<see cref="LinkToken"/> in its
    /// Rainwire:Caps answer, counts as no call and closes quietly when the board hangs up. Only ever set by
    /// MainWindow.OpenLinkedWindow and carried in the --session JSON; left out of the phonebook file when off.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Linked { get; set; }

    /// <summary>The board's single-use token for a <see cref="Linked"/> window (26 characters of Crockford base32).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? LinkToken { get; set; }

    /// <summary>A <see cref="Linked"/> window's board name (its parent's), for the title the board sets (OSC 0/2):
    /// "&lt;board&gt; — &lt;board's title&gt;".</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? LinkBoard { get; set; }

    /// <summary>
    /// SSH with "Save credentials" off: connect with no questions (no SSH Login dialog) and let the board log the
    /// caller in itself, the way Rainwire does. The SSH layer gets user "bbs" and a password Rainwire ignores, never
    /// the entry's username (that would count as a failed login for it). A board that wants an SSH password says so
    /// and the call ends. With "Save credentials" on, the saved username/password are used at the SSH layer as
    /// before. The client key (ssh_keys.p15) is never offered either way (ssh.c ssh_offer_key).
    /// </summary>
    [JsonIgnore]
    public bool UsesBlankSshLogin => Protocol == ConnectionProtocol.SSH && !SaveCredentials;

    [JsonIgnore]
    public string SnapshotImagePath => SnapshotPath ?? "avares://TERMinator/Assets/snapshots/no_snapshot.png";

    [JsonIgnore]
    public string DisplayAddress => Port == GetDefaultPort(Protocol)
        ? Host
        : $"{Host}:{Port}";

    public static int GetDefaultPort(ConnectionProtocol protocol) => protocol switch
    {
        ConnectionProtocol.Telnet => 23,
        ConnectionProtocol.Raw => 23,
        ConnectionProtocol.SSH => 22,
        ConnectionProtocol.TelnetS => 992,
        ConnectionProtocol.WebSocket => 80,
        ConnectionProtocol.SecureWebSocket => 443,
        _ => 23
    };

    public int GetNativeProtocol() => Protocol switch
    {
        ConnectionProtocol.Telnet => Services.NativeBridge.CONN_TYPE_TELNET,
        ConnectionProtocol.Raw => Services.NativeBridge.CONN_TYPE_RAW,
        ConnectionProtocol.SSH => Services.NativeBridge.CONN_TYPE_SSH,
        ConnectionProtocol.TelnetS => Services.NativeBridge.CONN_TYPE_TELNETS,
        ConnectionProtocol.WebSocket => Services.NativeBridge.CONN_TYPE_WEBSOCKET,
        ConnectionProtocol.SecureWebSocket => Services.NativeBridge.CONN_TYPE_WEBSOCKETS,
        _ => Services.NativeBridge.CONN_TYPE_TELNET
    };
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConnectionProtocol
{
    Telnet,
    SSH,
    TelnetS,
    WebSocket,
    SecureWebSocket,

    /// <summary>
    /// A plain 8-bit socket with NO telnet negotiation and no IAC handling at all.
    ///
    /// Not every board on port 23 is really speaking telnet. Against one that isn't, our
    /// telnet layer eats any 0xFF in the stream as an IAC command — which ZMODEM survives,
    /// because it escapes IAC itself before the bytes ever get there, but which corrupts
    /// X/YMODEM, whose blocks are raw file bytes. That shows up as a download that CRCs,
    /// retries and dies while ZMODEM on the same board is fine.
    ///
    /// Appended last on purpose: the phonebook serialises this enum BY NAME
    /// (JsonStringEnumConverter), but keeping the order stable costs nothing.
    /// </summary>
    Raw
}
