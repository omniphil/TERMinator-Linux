using System.Text;

namespace TERMinator.Linux.RainwireWm;

/// <summary>
/// One message of Rainwire's window protocol (Rainwire docs/WINDOWS.md, protocol v1). Every message is one APC string:
///   ESC _ Rainwire:WM;&lt;Verb&gt;[;key=value]... ESC \
/// Verbs are 1-16 ASCII letters, keys 1-16 of a-z, 0-9 and _. In values, '%', ';', '=', the C0 controls, DEL and the
/// C1 controls (U+0080-U+009F) are written as %XX; everything else is UTF-8 as it is. Window data travels as b64=.
/// The native bridge (native/apc/wm_bridge.c) strips "Rainwire:WM;" before queueing a message for us.
/// No Avalonia in here, so the protocol test harness (tools/RainwireWmHarness) builds it on its own.
/// </summary>
public sealed class WmMessage
{
    public const string Prefix = "Rainwire:WM;";
    public const int Version = 1;
    /// <summary>The most window data one Data or Input message carries before base64.</summary>
    public const int MaxData = 2048;
    /// <summary>A message from the board longer than this is refused (a full Data message is about 2.8 KB).</summary>
    public const int MaxLen = 8000;
    public const int MaxArgs = 32;

    public string Verb { get; }
    public List<KeyValuePair<string, string>> Args { get; } = new();

    /// <summary>A message; kv is key, value, key, value...</summary>
    public WmMessage(string verb, params string[] kv)
    {
        Verb = verb;
        for (int i = 0; i + 1 < kv.Length; i += 2)
            Args.Add(new(kv[i], kv[i + 1]));
    }

    /// <summary>An argument's value, or "" when it isn't there.</summary>
    public string Get(string key)
    {
        foreach (var a in Args)
            if (a.Key == key)
                return a.Value;
        return "";
    }

    /// <summary>An argument as a number from -99999 to 99999, as the protocol allows.</summary>
    public bool TryInt(string key, out int value)
    {
        value = 0;
        string s = Get(key);
        if (s.Length == 0 || s.Length > 6)
            return false;
        int i = 0, sign = 1, n = 0;
        if (s[0] == '-' || s[0] == '+')
        {
            sign = s[0] == '-' ? -1 : 1;
            i = 1;
        }
        if (i == s.Length)
            return false;
        for (; i < s.Length; i++)
        {
            if (s[i] < '0' || s[i] > '9')
                return false;
            n = n * 10 + (s[i] - '0');
        }
        n *= sign;
        if (n < -99999 || n > 99999)
            return false;
        value = n;
        return true;
    }

    /// <summary>The b64= argument decoded, or null if it is missing or not valid base64.</summary>
    public byte[]? Data()
    {
        try
        {
            return Convert.FromBase64String(Get("b64"));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>The message as it goes on the wire, APC and all.</summary>
    public byte[] Encode()
    {
        var b = new List<byte>(64);
        b.Add(0x1b);
        b.Add((byte)'_');
        b.AddRange(Encoding.ASCII.GetBytes(Prefix + Verb));
        foreach (var a in Args)
        {
            b.Add((byte)';');
            b.AddRange(Encoding.ASCII.GetBytes(a.Key));
            b.Add((byte)'=');
            Escape(b, a.Value);
        }
        b.Add(0x1b);
        b.Add((byte)'\\');
        return b.ToArray();
    }

    /// <summary>Splits input (or output) into Input (or Data) messages of at most MaxData bytes each.</summary>
    public static List<WmMessage> DataMessages(string verb, int id, ReadOnlySpan<byte> data)
    {
        var list = new List<WmMessage>();
        while (data.Length > 0)
        {
            int n = Math.Min(data.Length, MaxData);
            list.Add(new WmMessage(verb, "id", id.ToString(), "b64", Convert.ToBase64String(data[..n])));
            data = data[n..];
        }
        return list;
    }

    private static void Escape(List<byte> b, string value)
    {
        byte[] s = Encoding.UTF8.GetBytes(value);
        for (int i = 0; i < s.Length; i++)
        {
            byte c = s[i];
            // A C1 control is C2 80..C2 9F in UTF-8: both bytes are escaped, never sent raw inside an APC
            bool c1 = c == 0xC2 && i + 1 < s.Length && s[i + 1] >= 0x80 && s[i + 1] <= 0x9F;
            if (c == '%' || c == ';' || c == '=' || c < 0x20 || c == 0x7F || c1)
            {
                Hex(b, c);
                if (c1)
                    Hex(b, s[++i]);
            }
            else
                b.Add(c);
        }
    }

    private static void Hex(List<byte> b, byte c)
    {
        const string digits = "0123456789abcdef";
        b.Add((byte)'%');
        b.Add((byte)digits[c >> 4]);
        b.Add((byte)digits[c & 15]);
    }

    /// <summary>
    /// Reads a message: its APC body with or without the "Rainwire:WM;" prefix (the native bridge strips it).
    /// Returns null for anything malformed, which is then ignored.
    /// </summary>
    public static WmMessage? Parse(ReadOnlySpan<byte> body)
    {
        var prefix = "Rainwire:WM;"u8;
        if (body.StartsWith(prefix))
            body = body[prefix.Length..];
        if (body.Length == 0 || body.Length > MaxLen)
            return null;

        int semi = body.IndexOf((byte)';');
        var verb = semi < 0 ? body : body[..semi];
        if (!ValidVerb(verb))
            return null;
        var msg = new WmMessage(Encoding.ASCII.GetString(verb));
        var rest = semi < 0 ? ReadOnlySpan<byte>.Empty : body[(semi + 1)..];
        if (semi >= 0 && rest.Length == 0)
            return null;   // "Verb;" with nothing after it

        while (semi >= 0)
        {
            semi = rest.IndexOf((byte)';');
            var part = semi < 0 ? rest : rest[..semi];
            rest = semi < 0 ? ReadOnlySpan<byte>.Empty : rest[(semi + 1)..];
            int eq = part.IndexOf((byte)'=');
            if (eq < 0 || !ValidKey(part[..eq]) || msg.Args.Count >= MaxArgs)
                return null;
            string? value = Unescape(part[(eq + 1)..]);
            if (value == null)
                return null;
            msg.Args.Add(new(Encoding.ASCII.GetString(part[..eq]), value));
        }
        return msg;
    }

    public static WmMessage? Parse(string body) => Parse(Encoding.UTF8.GetBytes(body));

    private static string? Unescape(ReadOnlySpan<byte> v)
    {
        if (v.IndexOf((byte)'%') < 0)
            return Encoding.UTF8.GetString(v);
        var b = new List<byte>(v.Length);
        for (int i = 0; i < v.Length; i++)
        {
            if (v[i] != '%')
            {
                b.Add(v[i]);
                continue;
            }
            if (i + 2 >= v.Length)
                return null;   // a '%' without two hex digits after it
            int hi = HexValue(v[i + 1]), lo = HexValue(v[i + 2]);
            if (hi < 0 || lo < 0)
                return null;
            b.Add((byte)(hi << 4 | lo));
            i += 2;
        }
        return Encoding.UTF8.GetString(b.ToArray());
    }

    private static int HexValue(byte c) => c switch
    {
        >= (byte)'0' and <= (byte)'9' => c - '0',
        >= (byte)'a' and <= (byte)'f' => c - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => c - 'A' + 10,
        _ => -1,
    };

    private static bool ValidVerb(ReadOnlySpan<byte> s)
    {
        if (s.Length == 0 || s.Length > 16)
            return false;
        foreach (byte c in s)
            if (!(c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z'))
                return false;
        return true;
    }

    private static bool ValidKey(ReadOnlySpan<byte> s)
    {
        if (s.Length == 0 || s.Length > 16)
            return false;
        foreach (byte c in s)
            if (!(c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '_'))
                return false;
        return true;
    }

    public override string ToString()
    {
        var sb = new StringBuilder(Verb);
        foreach (var a in Args)
            sb.Append(';').Append(a.Key).Append('=').Append(a.Key == "b64" ? $"<{a.Value.Length} b64>" : a.Value);
        return sb.ToString();
    }
}
