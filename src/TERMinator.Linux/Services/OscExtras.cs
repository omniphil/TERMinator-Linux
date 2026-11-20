using System.Runtime.InteropServices;
using System.Text;

namespace TERMinator.Linux.Services;

/// <summary>
/// The board's OSC extras (PORT_TO_MAC_LINUX.md 12.62): OSC 0/2 window title, OSC 8 hyperlinks, OSC 52 clipboard
/// writes; since 12.72 also OSC 777 notifications and the app's own keys in the Caps answer; since 12.73 the board's
/// Rainwire:Link requests (a linked window, Rainwire docs/MRC_WINDOW.md). The parsing, limits and the Rainwire:Caps answer are all native (native/apc/osc_ext.c); this is the app's
/// side; same as Windows Services/OscExtras.cs. A .so built before 12.62 lacks the exports: everything here then quietly does nothing.
/// </summary>
public static class OscExtras
{
    private const string DllName = "libsyncterm-native";

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int syncterm_osc_poll_title(byte[] buffer, int bufferLen);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int syncterm_osc_poll_clipboard(byte[] buffer, int bufferLen);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int syncterm_osc_link_at(int col, int row, int ch, byte[]? buffer, int bufferLen,
                                                   out int spanStart, out int spanEnd);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int syncterm_osc_links_active();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void syncterm_osc_set_info([MarshalAs(UnmanagedType.LPUTF8Str)] string? keys);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int syncterm_osc_poll_notify(byte[] title, int titleLen, byte[] body, int bodyLen);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int syncterm_osc_poll_link(byte[] buffer, int bufferLen);

    /// <summary>Same caps as native/osc_ext.h.</summary>
    public const int UriMax = 2048;
    public const int ClipboardMax = 64 * 1024;

    private static bool _missing;
    private static bool _missing72;
    private static bool _missing73;
    private static readonly byte[] _linkBuf = new byte[LinkPollMax];
    private static readonly byte[] _titleBuf = new byte[512];
    private static readonly byte[] _clipBuf = new byte[ClipboardMax + 1];
    private static readonly byte[] _noteTitleBuf = new byte[NoteTitleMax + 1];
    private static readonly byte[] _noteBodyBuf = new byte[NoteBodyMax + 1];

    /// <summary>OSC_EXT_LINK_POLL_MAX: a Rainwire:Link request's key=value lines always fit (16 x (16 + 128 + 2)).</summary>
    public const int LinkPollMax = 16 * (16 + 128 + 2);

    /// <summary>OSC_EXT_NOTE_TITLE_MAX / OSC_EXT_NOTE_BODY_MAX: bytes of UTF-8 kept from an OSC 777 notification.</summary>
    public const int NoteTitleMax = 64;
    public const int NoteBodyMax = 256;

    private static T Guard<T>(Func<T> call, T fallback)
    {
        if (_missing) return fallback;
        try { return call(); }
        catch (EntryPointNotFoundException) { _missing = true; return fallback; }
        catch (DllNotFoundException) { _missing = true; return fallback; }
    }

    /// <summary>A title the board set since the last call ("" = it cleared it), or null when nothing changed.</summary>
    public static string? PollTitle() => Guard(() =>
    {
        int n = syncterm_osc_poll_title(_titleBuf, _titleBuf.Length);
        return n < 0 ? null : CleanTitle(Encoding.UTF8.GetString(_titleBuf, 0, n));
    }, null);

    /// <summary>Text the board put on the clipboard since the last call, or null.</summary>
    public static string? PollClipboard() => Guard(() =>
    {
        int n = syncterm_osc_poll_clipboard(_clipBuf, _clipBuf.Length);
        return n <= 0 ? null : Encoding.UTF8.GetString(_clipBuf, 0, n);
    }, null);

    /// <summary>The oldest OSC 777 notification the board sent and the app hasn't taken yet, or null (12.72). Either
    /// part may be empty, not both.</summary>
    public static (string Title, string Body)? PollNotify() => Guard72<(string, string)?>(() =>
    {
        if (syncterm_osc_poll_notify(_noteTitleBuf, _noteTitleBuf.Length, _noteBodyBuf, _noteBodyBuf.Length) != 1)
            return null;
        return (CleanTitle(Utf8Z(_noteTitleBuf)), CleanTitle(Utf8Z(_noteBodyBuf)));
    }, null);

    /// <summary>
    /// A Rainwire:Link request (12.73). Native has already checked kind ([a-z0-9_], 16 at most) and, on Open, token
    /// (26 characters of Crockford base32); every other key the board sent is in <see cref="Args"/> as it came
    /// (unescaped, controls removed): title, the optional Open hints cols, rows, place and focus, and anything later.
    /// Unknown keys are the app's to ignore.
    /// </summary>
    public sealed record LinkRequest(bool Open, string Kind, string Token, IReadOnlyDictionary<string, string> Args)
    {
        public string Title => Args.TryGetValue("title", out var t) ? CleanTitle(t) : string.Empty;

        /// <summary>A whole-number argument, or null when it's missing or not a number.</summary>
        public int? Int(string key) =>
            Args.TryGetValue(key, out var v) && int.TryParse(v, System.Globalization.NumberStyles.None,
                                                             System.Globalization.CultureInfo.InvariantCulture, out int n)
                ? n : null;
    }

    /// <summary>
    /// The oldest Rainwire:Link request the board sent and the app hasn't taken yet (12.73), or null. Native passes
    /// "key=value" lines; kind is always there and token is on every Open. A .so without the export (before 12.73)
    /// never has one; one from before the pass-through (three bare lines) is ignored.
    /// </summary>
    public static LinkRequest? PollLink()
    {
        if (_missing || _missing72 || _missing73) return null;
        try
        {
            int verb = syncterm_osc_poll_link(_linkBuf, _linkBuf.Length);
            if (verb != 1 && verb != 2)
                return null;
            var args = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in Utf8Z(_linkBuf).Split('\n'))
            {
                int eq = line.IndexOf('=');
                if (eq > 0)
                    args[line[..eq]] = line[(eq + 1)..];
            }
            if (!args.TryGetValue("kind", out var kind) || kind.Length == 0)
                return null;
            args.TryGetValue("token", out var token);
            return new LinkRequest(verb == 1, kind, token ?? string.Empty, args);
        }
        catch (EntryPointNotFoundException) { _missing73 = true; return null; }
        catch (DllNotFoundException) { _missing = true; return null; }
    }

    /// <summary>The app's own keys for the Rainwire:Caps answer (12.72); call before each connect.</summary>
    public static void SetInfo(string keys) => Guard72(() => { syncterm_osc_set_info(keys); return true; }, false);

    /// <summary>Guard for the 12.72 exports: a 12.62 .so without them keeps its titles, links and clipboard.</summary>
    private static T Guard72<T>(Func<T> call, T fallback)
    {
        if (_missing || _missing72) return fallback;
        try { return call(); }
        catch (EntryPointNotFoundException) { _missing72 = true; return fallback; }
        catch (DllNotFoundException) { _missing = true; return fallback; }
    }

    /// <summary>
    /// "app=1.1.5;lang=en-US;tz=America/Chicago;notify=1" (Rainwire docs/CLIENT_CAPS.md, 12.72): the version the
    /// About box shows, the UI language, the IANA time zone (left out when the system doesn't name one) and whether
    /// board notifications are shown (Settings' Board Notifications, and notify-send installed: BoardNotifier.cs). Never
    /// touch=1 on a desktop. 12.73: mw=1 when the board may open linked windows (Options > Let Boards Open Extra
    /// Windows), and link=&lt;token&gt; on a linked window's own connection (a token that isn't plain Crockford base32
    /// is left out, so the board hangs up on it). 1.1.6: ui=3, this app draws a Rainwire board's menus itself
    /// (Controls/RainwireMenu.cs, Rainwire docs/MENU_GFX.md), and uiscreens= which of a board's screens it draws
    /// ("menu+dialog"; later ones join with '+'). 1.1.6: scene=1 and scenekinds= the scene block kinds it draws
    /// (Controls/RainwireScene.cs, Rainwire docs/SCENES.md). The native side keeps 256 bytes of these keys
    /// (OSC_EXT_INFO_MAX) and drops a key that doesn't fit, so scenekinds goes last.
    /// </summary>
    public static string AppInfo(bool notify, bool multiWindow = false, string? linkToken = null)
    {
        var keys = new List<string> { "app=" + Views.MainWindow.AppVersion };
        string lang = System.Globalization.CultureInfo.CurrentUICulture.Name;
        if (IsInfoValue(lang))
            keys.Add("lang=" + lang);
        if (IanaTimeZone() is { } tz)
            keys.Add("tz=" + tz);
        if (notify)
            keys.Add("notify=1");
        if (multiWindow)
            keys.Add("mw=1");
        if (linkToken != null && IsLinkToken(linkToken))
            keys.Add("link=" + linkToken);
        keys.Add("ui=" + Controls.RainwireMenu.Version);
        keys.Add("uiscreens=" + Controls.RainwireMenu.Screens);
        keys.Add("scene=" + Controls.RainwireMenu.SceneVersion);
        keys.Add("scenekinds=" + Controls.RainwireMenu.SceneKinds);
        return string.Join(";", keys);
    }

    /// <summary>The local time zone's IANA name ("America/Chicago"; on Linux normally TimeZoneInfo.Local's own id, from
    /// TZ or /etc/localtime), or null when there's none: .NET's "Local" for an unnamed zone file is not a name.</summary>
    private static string? IanaTimeZone()
    {
        try
        {
            var local = TimeZoneInfo.Local;
            string? iana = local.HasIanaId ? local.Id
                : TimeZoneInfo.TryConvertWindowsIdToIanaId(local.Id, out var converted) ? converted : null;
            return iana != null && iana != "Local" && IsInfoValue(iana) ? iana : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A Rainwire:Link token: exactly 26 characters of upper-case Crockford base32 (no I, L, O or U).</summary>
    public static bool IsLinkToken(string token) =>
        token.Length == 26 && token.All(c => c is (>= '0' and <= '9') or (>= 'A' and <= 'Z') && c is not ('I' or 'L' or 'O' or 'U'));

    /// <summary>Only [A-Za-z0-9._+/-] may go in a Caps value (native drops anything else; this keeps the rest).</summary>
    private static bool IsInfoValue(string value)
    {
        if (value.Length == 0)
            return false;
        foreach (char c in value)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '+' or '/' or '-'))
                return false;
        return true;
    }

    /// <summary>The NUL-terminated UTF-8 in buf.</summary>
    private static string Utf8Z(byte[] buf)
    {
        int n = Array.IndexOf(buf, (byte)0);
        return Encoding.UTF8.GetString(buf, 0, n < 0 ? buf.Length : n);
    }

    /// <summary>True once the board has drawn an OSC 8 link this session.</summary>
    public static bool LinksActive => Guard(() => syncterm_osc_links_active() != 0, false);

    /// <summary>Whether screen cell (col, row) — 0-based, showing CP437 character ch — is part of an OSC 8 link.</summary>
    public static bool IsLinkCell(int col, int row, int ch) =>
        Guard(() => syncterm_osc_link_at(col, row, ch, null, 0, out _, out _) != 0, false);

    /// <summary>The OSC 8 link under screen cell (col, row), or null.</summary>
    public static string? LinkAt(int col, int row, int ch)
    {
        return Guard(() =>
        {
            var buf = new byte[UriMax + 1];
            int n = syncterm_osc_link_at(col, row, ch, buf, buf.Length, out _, out _);
            if (n <= 0) return null;
            string uri = Encoding.ASCII.GetString(buf, 0, n);
            return IsAllowedUri(uri) ? uri : null;
        }, null);
    }

    /// <summary>The schemes a board may link to (checked natively too; this is the last word before the browser).</summary>
    public static bool IsAllowedUri(string uri)
    {
        if (uri.Length == 0 || uri.Length > UriMax) return false;
        foreach (char c in uri)
            if (c <= ' ' || c >= '\x7f') return false;
        foreach (var scheme in new[] { "http://", "https://", "mailto:", "telnet://", "ssh://" })
            if (uri.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) && uri.Length > scheme.Length)
                return true;
        return false;
    }

    /// <summary>"title - TERMinator", or null for the app's normal title.</summary>
    public static string? WindowTitle(string? boardTitle) =>
        string.IsNullOrWhiteSpace(boardTitle) ? null : $"{boardTitle} - TERMinator";

    /// <summary>Native already dropped control characters; this also drops C1 and other non-printing ones.</summary>
    private static string CleanTitle(string title)
    {
        var sb = new StringBuilder(title.Length);
        foreach (char c in title)
            if (!char.IsControl(c) && c != '\uFFFD' && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format)
                sb.Append(c);
        return sb.ToString().Trim();
    }
}
