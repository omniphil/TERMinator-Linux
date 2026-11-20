using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace TERMinator.Linux.Controls;

public partial class HistoryTicker : UserControl
{
    private static readonly string[] Facts =
    [
        "1945: ENIAC, the first general-purpose electronic computer, is completed",
        "1947: The transistor is invented at Bell Labs",
        "1958: Jack Kilby demonstrates the first integrated circuit",
        "1964: BASIC programming language is created at Dartmouth",
        "1965: Gordon Moore predicts transistor doubling every two years",
        "1968: Douglas Engelbart demos the mouse, hypertext, and video conferencing",
        "1969: ARPANET sends its first message from UCLA to Stanford",
        "1970: UNIX is created at Bell Labs",
        "1971: Intel releases the 4004, the first commercial microprocessor",
        "1972: The C programming language is developed at Bell Labs",
        "1973: Xerox Alto becomes the first computer with a graphical user interface",
        "1974: Vint Cerf and Bob Kahn develop the TCP/IP protocol",
        "1975: The Altair 8800 launches the personal computer revolution",
        "1976: Steve Wozniak builds the Apple I in a garage",
        "1977: The Apple II, TRS-80, and Commodore PET debut",
        "1978: The first BBS goes online in Chicago",
        "1979: VisiCalc launches, the killer app that sold Apple IIs",
        "1981: IBM PC released with 16KB RAM and no hard drive",
        "1981: The Osborne 1 becomes the first portable computer",
        "1982: The Commodore 64 launches, selling 17 million units",
        "1982: Time magazine names the computer \"Machine of the Year\"",
        "1983: TCP/IP becomes the standard protocol for ARPANET",
        "1984: Apple Macintosh debuts with legendary Super Bowl ad",
        "1985: Commodore Amiga 1000 brings multimedia to home computing",
        "1986: Intel releases the 386 processor at 16 MHz",
        "1987: The Amiga 500 becomes the best-selling Amiga ever",
        "1987: IBM PS/2 introduces the VGA graphics standard",
        "1987: GIF image format is introduced by CompuServe",
        "1988: MS-DOS 4.0 adds support for hard drives over 32MB",
        "1990: The World Wide Web goes live on Christmas Day",
        "1991: Linus Torvalds releases the first Linux kernel",
        "1993: Mosaic browser brings the web to the masses",
        "FidoNet at its peak connects over 35,000 BBS systems",
        "A 1200 baud modem could transfer 1MB in about 2 hours",
        "The C64 had more computing power than the Apollo 11",
        "The Amiga could display 4,096 colors when PCs had 16",
        "Early hard drives cost about $100,000 per gigabyte",
        "ANSI art became the dominant art form on BBS systems",
        "The sound of a modem connecting was music to our ears",
        "Door games like Legend of the Red Dragon defined BBS gaming",
        "SysOps were the unsung heroes of the early online world",
        "CBBS, the first BBS, was created by Ward Christensen in 1978",
        "TradeWars 2002 was one of the most popular BBS door games",
        "The XMODEM protocol revolutionized file transfers on BBSes",
        "ZMODEM became the gold standard for BBS file transfers",
        "FidoNet used store-and-forward to relay mail between BBSes",
        "The term \"shareware\" was coined in the BBS era",
        "BBSes pioneered online communities before the internet",
        "The \"elite\" BBS scene used leet speak long before the internet",
        "Calling long distance to reach a BBS could cost a fortune",
        "BBS door games ran as external programs launched by the BBS",
        "File ratios required uploading to earn download credits",
        "ANSI bombs could crash terminals with malicious escape codes",
        "The demoscene grew from the BBS warez scene",
        "QWK packets let users read BBS messages offline",
        "Blueboxing and phreaking were closely tied to BBS culture",
        "Many BBSes had only one phone line - busy signals were common",
        "14.4k modems were blazing fast in the early 90s",
        "28.8k and 56k modems arrived just as BBSes began to fade",
        "Echomail let BBSes share message forums across FidoNet",
        "NetMail was FidoNet's version of email between systems",
        "Many SysOps ran BBSes from their bedrooms as teenagers",
        "Some BBSes are still running today on Telnet",
        "1979: Hayes introduces the Smartmodem with AT commands",
        "1980: Usenet launches, connecting universities via UUCP",
        "1983: CompuServe introduces CB Simulator, early chat rooms",
        "1984: FidoNet is created by Tom Jennings in San Francisco",
        "1985: Quantum Link launches, later becoming AOL",
        "1986: LISTSERV creates the first email mailing lists",
        "1988: IRC (Internet Relay Chat) is created in Finland",
        "The Hayes AT command set became the industry standard",
        "2400 baud modems made real-time chat practical on BBSes",
        "V.32bis modems brought 14.4 kbps speeds in 1991",
        "V.34 modems reached 28.8 kbps, then 33.6 kbps",
        "V.90 and V.92 achieved 56 kbps - the analog limit",
        "Prodigy, CompuServe, and GEnie were early online services",
        "The Source was one of the first consumer online services",
        "Synchronet BBS software is still actively developed today",
        "Mystic BBS keeps the BBS tradition alive on modern systems",
        "The FOSSIL driver standardized serial port access for BBSes",
        "YMODEM added batch file transfers to XMODEM",
        "Kermit protocol was popular on mainframes and minicomputers",
        "BiModem allowed simultaneous chat and file transfers",
        "HSLink could transfer files in both directions at once",
        "The Telnet protocol gave BBSes new life on the internet",
        "SSH connections now secure modern BBS communications",
        "SyncTERM keeps classic terminal emulation alive today",
        "Zone 1 was North America in FidoNet addressing",
        "A FidoNet address looked like 1:123/456.7",
        "Points were users who picked up mail from a BBS node"
    ];

    private readonly List<int> _shuffledIndices = [];
    private int _currentPosition;
    private bool _isAnimating;
    private DispatcherTimer? _timer;

    // TranslateTransform references from AXAML (Avalonia doesn't auto-generate fields for non-Control types)
    private TranslateTransform CurrentTransform => (TranslateTransform)CurrentText.RenderTransform!;
    private TranslateTransform NextTransform => (TranslateTransform)NextText.RenderTransform!;

    // Animation state
    private DispatcherTimer? _animTimer;
    private readonly System.Diagnostics.Stopwatch _animClock = new();
    private double _animTargetHeight;
    private const double AnimDurationMs = 1000.0;
    private const double AnimFrameMs = 16.0;

    public HistoryTicker()
    {
        InitializeComponent();
        // No attach handler: the home screen starts the ticker (Resume) only while it's actually on screen, and
        // pauses it under the screensaver or while the window is minimized. Detach is kept as a safety stop.
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    /// <summary>Starts cycling facts (from a fresh shuffle). No-op if it's already running.</summary>
    public void Resume() => StartScrolling();

    /// <summary>Stops the 8s flip timer (and any flip in progress).</summary>
    public void Pause() => StopScrolling();

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        StopScrolling();
    }

    private void ShuffleIndices()
    {
        _shuffledIndices.Clear();
        _shuffledIndices.AddRange(Enumerable.Range(0, Facts.Length));
        var rng = new Random();
        for (int i = _shuffledIndices.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (_shuffledIndices[i], _shuffledIndices[j]) = (_shuffledIndices[j], _shuffledIndices[i]);
        }
    }

    private void StartScrolling()
    {
        if (Facts.Length == 0 || _timer != null) return;

        ShuffleIndices();
        _currentPosition = 0;

        // A pause can land mid-flip (StopScrolling kills the frame timer), so reset the flip state too, or
        // _isAnimating would stay set and ScrollToNext would never run again
        _isAnimating = false;
        CurrentText.Text = Facts[_shuffledIndices[_currentPosition]];
        CurrentTransform.Y = 0;
        CurrentText.Opacity = 1;
        NextText.Opacity = 0;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _timer.Tick += (_, _) => ScrollToNext();
        _timer.Start();
    }

    private void StopScrolling()
    {
        _timer?.Stop();
        _timer = null;
        _animTimer?.Stop();
        _animTimer = null;
    }

    private void ScrollToNext()
    {
        if (_isAnimating) return;
        _isAnimating = true;

        _currentPosition++;
        if (_currentPosition >= _shuffledIndices.Count)
        {
            ShuffleIndices();
            _currentPosition = 0;
        }

        NextText.Text = Facts[_shuffledIndices[_currentPosition]];

        double height = Bounds.Height;
        if (height <= 0) height = 48;
        _animTargetHeight = height;

        // Set start positions
        NextTransform.Y = height;
        NextText.Opacity = 1;
        CurrentTransform.Y = 0;
        CurrentText.Opacity = 1;

        // Start frame-based animation
        _animClock.Restart();
        _animTimer?.Stop();
        _animTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(AnimFrameMs) };
        _animTimer.Tick += OnAnimationTick;
        _animTimer.Start();
    }

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        double t = Math.Min(_animClock.Elapsed.TotalMilliseconds / AnimDurationMs, 1.0);

        // Linear, as Windows' plain DoubleAnimations (no easing function)
        double ease = t;

        double height = _animTargetHeight;

        // Current text slides up and fades out
        CurrentTransform.Y = -height * ease;
        CurrentText.Opacity = 1.0 - ease;

        // Next text slides up into view
        NextTransform.Y = height * (1.0 - ease);

        if (t >= 1.0)
        {
            // Animation complete
            _animTimer?.Stop();
            _animTimer = null;

            // Reset to steady state
            CurrentText.Text = NextText.Text;
            CurrentTransform.Y = 0;
            CurrentText.Opacity = 1;
            NextText.Opacity = 0;
            NextTransform.Y = height;

            _isAnimating = false;
        }
    }

    /// <summary>Raised by a left click anywhere on the ticker. The home screen opens the full facts list.</summary>
    public event EventHandler? FactsRequested;

    /// <summary>Every fact, in its original order, for the facts overlay (and the command line's "fact").</summary>
    public static IReadOnlyList<string> AllFacts => Facts;

    /// <summary>
    /// Clicking opens the full list instead of advancing the ticker (it used to skip to the next fact).
    /// </summary>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsLeftButtonPressed)
        {
            FactsRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }
}
