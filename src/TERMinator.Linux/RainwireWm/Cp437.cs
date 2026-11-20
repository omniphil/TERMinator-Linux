namespace TERMinator.Linux.RainwireWm;

/// <summary>
/// Code page 437, both ways. A Rainwire window's screen comes in the character set the board detected for this
/// terminal, which for TERMinator's SyncTERM core is CP437, so window cells hold CP437 codes and draw with the same
/// bitmap font as the main screen. This is for turning them into text (tests, drag labels) and typed text into bytes.
/// </summary>
public static class Cp437
{
    private static readonly string Table =
        "\u0000☺☻♥♦♣♠•◘○◙♂♀♪♫☼►◄↕‼¶§▬↨↑↓→←∟↔▲▼" +
        " !\"#$%&'()*+,-./0123456789:;<=>?" +
        "@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_" +
        "`abcdefghijklmnopqrstuvwxyz{|}~⌂" +
        "ÇüéâäàåçêëèïîìÄÅÉæÆôöòûùÿÖÜ¢£¥₧ƒ" +
        "áíóúñÑªº¿⌐¬½¼¡«»░▒▓│┤╡╢╖╕╣║╗╝╜╛┐" +
        "└┴┬├─┼╞╟╚╔╩╦╠═╬╧╨╤╥╙╘╒╓╫╪┘┌█▄▌▐▀" +
        "αßΓπΣσµτΦΘΩδ∞φε∩≡±≥≤⌠⌡÷≈°∙·√ⁿ²■ ";

    private static readonly Dictionary<char, byte> Reverse = BuildReverse();

    private static Dictionary<char, byte> BuildReverse()
    {
        var d = new Dictionary<char, byte>(256);
        for (int i = 0; i < 256; i++)
            d.TryAdd(Table[i], (byte)i);
        return d;
    }

    /// <summary>The character a CP437 code shows.</summary>
    public static char ToChar(byte code) => Table[code];

    /// <summary>A typed character as the byte to send, or null if CP437 has no such character.</summary>
    public static byte? FromChar(char c)
    {
        if (c >= 0x20 && c < 0x7F)
            return (byte)c;
        return c >= 0x80 && Reverse.TryGetValue(c, out byte b) && b >= 0x80 ? b : null;
    }
}
