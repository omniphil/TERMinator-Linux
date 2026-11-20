using System.IO;

namespace TERMinator.Linux.Models;

public class AppSettings
{
    public string DefaultFont { get; set; } = "Codepage 437 English";
    public int DefaultScreenMode { get; set; }
    public bool FontSmoothing { get; set; } = true;
    /// <summary>A Rainwire board's windows: drawn as graphical windows (on), or in the board's own text cells (off).</summary>
    public bool RainwireGraphicalWindows { get; set; } = true;
    /// <summary>The board's OSC 777 notifications as desktop notifications (notify-send) while TERMinator is in the
    /// background (12.72). Off by default (and in settings saved before it existed); read at each connect, which also
    /// decides notify=1 in the Rainwire:Caps answer.</summary>
    public bool BoardNotifications { get; set; }
    public string DownloadDirectory { get; set; } = string.Empty;
    public bool AutoDetectZmodem { get; set; } = true;
    public bool PlayBellSound { get; set; } = true;
    public int BellSoundIndex { get; set; } = 28; // Sun SPARCstation default
    public int BellVolume { get; set; } = 5; // 0-10 scale (50% default)
    public float ModPlayerVolume { get; set; } = 0.8f;
    public bool SoundtrackEnabled { get; set; } = true;
    public int SoundtrackCacheLimitMb { get; set; } = 250;
    /// <summary>TRACE (TERMinator Render And Compute Engine): let doors run their graphics modules on this PC.</summary>
    public bool TraceEnabled { get; set; } = true;
    /// <summary>Size limit of TRACE's module cache; the least recently used modules are removed past it.</summary>
    public int TraceCacheLimitMb { get; set; } = 500;
    public bool TraceCache500Done { get; set; }   // one-time 250 -> 500 MB move done (SettingsService.Load)
    public bool EnableSessionLogging { get; set; }
    public string LogDirectory { get; set; } = string.Empty;
    public double WindowWidth { get; set; } = 800;
    public double WindowHeight { get; set; } = 600;
    public bool WindowMaximized { get; set; }
    // Last normal (restored) window bounds: left, top, right, bottom in the screen's physical pixels
    // (Avalonia's PixelPoint space), so the rect carries the monitor the window was on. Same shape as
    // the Windows client's Win32 placement rect.
    public int[]? WindowBounds { get; set; }
    // Each BBS window's last normal bounds (same coords), by connection Id: a board reopens where it was last left
    public Dictionary<string, int[]> SessionWindowBounds { get; set; } = new();
    // Linked windows (12.73) kept above other windows (View > Always on Top), by their Id (<parentId>-<kind>)
    public HashSet<string> LinkedWindowsOnTop { get; set; } = new();
    // Options > Separate Window per BBS: each connection opens in a window of its own, several at once (on by
    // default); off, connecting takes over the main window as it did before 1.1.5
    public bool MultiWindow { get; set; } = true;
    // Options > Let Boards Open Extra Windows (12.73): mw=1 in the Rainwire:Caps answer, so a board may open a linked
    // window of its own (Rainwire's Multi Relay Chat) right after the caller's key or click. On by default.
    public bool LinkedWindows { get; set; } = true;
    public int PhonebookViewMode { get; set; } = 1; // 0=List, 1=Medium, 2=Large, 3=Small
    public bool HomeSideBySide { get; set; } // View > Side-by-Side Home Screen (desktop layout); true on a first run (SettingsService)
    public bool Zmodem8k { get; set; } // Transfer > ZMODEM 8K Blocks (uploads use up to 8K subpackets)
    public bool ShowBootSequence { get; set; } = true; // Options > Boot Sequence on Startup
    public bool ScreensaverEnabled { get; set; } = true; // Options > Screensaver: the 3-minute idle saver
    public string Phosphor { get; set; } = "Color"; // View > Phosphor: Color, Green, Amber or White (every screen + the command line)
    public int TotalCalls { get; set; } // successful BBS connections, for the elite rank (Services.EliteRank)
    public int ConnectionTimeoutSeconds { get; set; } = 10; // Preferences > TERMINAL > Connection Timeout: how long to wait for a BBS to answer
    public static readonly int[] ConnectionTimeoutChoices = [5, 10, 15, 20, 30, 45, 60];
    /// <summary>The connection timeout in seconds, falling back to 10 when the stored value isn't one of the choices.</summary>
    public static int ValidConnectionTimeout(int seconds) => Array.IndexOf(ConnectionTimeoutChoices, seconds) >= 0 ? seconds : 10;
    public int EmulatedBaud { get; set; } // modem speed emulation in bps; 0 = full speed (a board's own ESC[*r still applies)
    public bool ShowModemLights { get; set; } = true; // modem front-panel lights in the terminal status bar
    public bool KeyClicksEnabled { get; set; } = false; // IBM Model M key clicks while typing into a board or the command line
    public int KeyClickVolume { get; set; } = 3; // 0-10 scale
    public CrtSettings Crt { get; set; } = new();
    public string? QuickConnect1Id { get; set; }
    public string? QuickConnect2Id { get; set; }
    public string? QuickConnect3Id { get; set; } // slots 3-4 only show in the side-by-side home layout
    public string? QuickConnect4Id { get; set; }

    public static string GetDefaultDownloadDir()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads", "TERMinator");
    }

    public static string GetAppDataDir()
    {
        // XDG Base Directory: ~/.config/TERMinator
        var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var baseDir = !string.IsNullOrEmpty(xdgConfig)
            ? xdgConfig
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        var dir = Path.Combine(baseDir, "TERMinator");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
