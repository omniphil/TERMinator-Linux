using System.Globalization;

namespace TERMinator.Linux.Services;

/// <summary>
/// The modem speeds offered for speed emulation: real line rates, each with the standard that carried it. 0 is full
/// speed. Emulation meters at bps / 10 characters a second (8 data bits plus start and stop bits), which is the raw
/// line. 57600 and 115200 are deliberately absent: those were serial-port (DTE) settings, never line rates. "56k" runs
/// at 50000, since V.90 downloads topped out at 53.3k and usually connected in the 40-50k range.
/// </summary>
public static class ModemSpeeds
{
    public static readonly int[] Rates = [0, 300, 1200, 2400, 9600, 14400, 19200, 28800, 33600, 50000];

    /// <summary>What the menu, Preferences and the command line call a speed.</summary>
    public static string Label(int bps) => bps switch
    {
        <= 0 => "Off (full speed)",
        300 => "300 bps (Bell 103)",
        1200 => "1200 bps (V.22)",
        2400 => "2400 bps (V.22bis)",
        9600 => "9600 bps (V.32)",
        14400 => "14.4k (V.32bis)",
        19200 => "19.2k (V.32terbo)",
        28800 => "28.8k (V.34)",
        33600 => "33.6k (V.34+)",
        50000 => "56k (V.90)",
        _ => $"{bps} bps"
    };

    /// <summary>Short names for Tab completion in the command line ("baud").</summary>
    public static readonly string[] ShortNames = ["off", "300", "1200", "2400", "9600", "14.4k", "19.2k", "28.8k", "33.6k", "56k"];

    /// <summary>
    /// Reads a speed typed in the command line: "off" / "0", a listed rate ("2400"), a k form ("14.4k", "56k"), or
    /// "56000" / "57600" for the 56k entry. Only listed speeds are accepted.
    /// </summary>
    public static bool TryParse(string text, out int bps)
    {
        string arg = text.Trim().ToLowerInvariant().Replace("bps", string.Empty).Trim();
        bps = 0;
        if (arg is "off" or "0")
            return true;

        double value;
        if (arg.EndsWith('k'))
        {
            if (!double.TryParse(arg[..^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value))
                return false;
            value *= 1000;
        }
        else if (!double.TryParse(arg, NumberStyles.None, CultureInfo.InvariantCulture, out value))
        {
            return false;
        }

        int requested = (int)Math.Round(value);
        if (requested is 56000 or 57600)
            requested = 50000;
        if (Array.IndexOf(Rates, requested) <= 0)
            return false;
        bps = requested;
        return true;
    }

    /// <summary>
    /// A stored speed that's no longer listed (57600 / 115200 from before the list followed the real standards)
    /// becomes the nearest listed one, so the menu and Preferences always show a checked choice.
    /// </summary>
    public static int Normalize(int bps)
    {
        if (bps <= 0 || Array.IndexOf(Rates, bps) >= 0)
            return Math.Max(0, bps);
        return Rates.Where(r => r > 0).OrderBy(r => Math.Abs(r - bps)).First();
    }
}
