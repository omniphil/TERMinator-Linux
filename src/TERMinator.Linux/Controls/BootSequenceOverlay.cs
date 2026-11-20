using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace TERMinator.Linux.Controls;

/// <summary>
/// Startup "boot" screen. The host shows it and calls <see cref="Start"/> once the window is on screen; the BIOS /
/// modem lines type out, then <see cref="Finished"/> is raised and the host cuts straight to the app.
///
/// Left alone the text runs typing <see cref="TypingDuration"/> + hold <see cref="HoldDuration"/>. Typing is driven by
/// elapsed time rather than by counting timer ticks, because DispatcherTimer ticks are imprecise and a tick-counted
/// sequence ran well over its intended length.
///
/// A key (forwarded by the host through <see cref="HandleKey"/>) or click while it's running finishes the text at
/// once and waits on a "Press any key to load TERMinator or "C" for command line mode" prompt. At the prompt, C raises
/// <see cref="CommandLineRequested"/>; anything else raises <see cref="Finished"/>.
/// </summary>
public sealed class BootSequenceOverlay : Grid
{
    /// <summary>The boot screen is done: typed out, or dismissed at the prompt. The host removes it and shows the app.</summary>
    public event EventHandler? Finished;

    /// <summary>C was pressed at the prompt. The host removes this overlay and shows the command line.</summary>
    public event EventHandler? CommandLineRequested;

    /// <summary>How long the lines take to type out. Shared with the command line's "boot" command.</summary>
    internal static readonly TimeSpan TypingDuration = TimeSpan.FromSeconds(0.5);
    private static readonly TimeSpan HoldDuration = TimeSpan.FromMilliseconds(150);
    private const string LoadPrompt = "Press any key to load TERMinator or \"C\" for command line mode";

    /// <summary>The beat after each line, in "characters" of typing time.</summary>
    private const int LinePauseUnits = 4;

    private static readonly Color Green = Color.FromRgb(0x55, 0xDD, 0x55);
    private static readonly FontFamily BootFont = new("JetBrains Mono, DejaVu Sans Mono, monospace");

    private readonly string[] _lines;
    private readonly int _totalUnits;
    private readonly TextBlock _text;
    private readonly TextBlock _hint;
    private readonly Stopwatch _typingClock = new();
    private DispatcherTimer? _startDelayTimer;
    private DispatcherTimer? _typeTimer;
    private DispatcherTimer? _holdTimer;
    private DispatcherTimer? _blinkTimer;
    private bool _started;
    private bool _waitingForKey;
    private bool _finished;

    public BootSequenceOverlay(IEnumerable<string> lines)
    {
        _lines = lines.ToArray();
        _totalUnits = TotalUnits(_lines);
        Background = new SolidColorBrush(Color.FromRgb(0x05, 0x05, 0x08));

        _text = new TextBlock
        {
            FontFamily = BootFont,
            FontSize = 15,
            Foreground = new SolidColorBrush(Green),
            Margin = new Thickness(36, 28, 36, 28),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        Children.Add(_text);

        _hint = new TextBlock
        {
            Text = "press any key to skip",
            FontFamily = BootFont,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x66)),
            Margin = new Thickness(0, 0, 18, 12),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom
        };
        Children.Add(_hint);

        // Take keyboard focus as soon as we're up. At launch nothing in the window has focus yet, and without a
        // focused element key presses never arrive, so the skip key and "C" would never reach the window's key handler.
        Focusable = true;
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => Focus(), DispatcherPriority.Input);

        PointerPressed += (_, e) =>
        {
            Advance(commandLineKey: false);
            e.Handled = true;
        };
    }

    /// <summary>Starts typing after <paramref name="delay"/> (the host waits out its CRT power-on effect).</summary>
    public void Start(TimeSpan delay)
    {
        if (_started || _waitingForKey || _finished)
            return;
        _started = true;

        if (delay <= TimeSpan.Zero)
        {
            BeginTyping();
            return;
        }

        _startDelayTimer = new DispatcherTimer { Interval = delay };
        _startDelayTimer.Tick += (_, _) =>
        {
            _startDelayTimer.Stop();
            BeginTyping();
        };
        _startDelayTimer.Start();
    }

    /// <summary>
    /// A key while the boot screen is up. While the text is still typing (or holding), finish it at once and wait on
    /// the prompt; at the prompt, C opens the command line and any other key loads the app.
    /// </summary>
    public void HandleKey(Key key) => Advance(commandLineKey: key == Key.C);

    // Never influence the window's size-to-content fit: the overlay just fills whatever space the window has.
    protected override Size MeasureOverride(Size availableSize)
    {
        base.MeasureOverride(availableSize);
        return new Size(0, 0);
    }

    private void Advance(bool commandLineKey)
    {
        if (_finished)
            return;

        if (_waitingForKey)
        {
            if (commandLineKey)
            {
                StopTimers();
                _finished = true; // done: ignore anything else that arrives before the host removes us
                CommandLineRequested?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                Finish();
            }
            return;
        }

        StopTimers();
        _waitingForKey = true;
        _hint.IsVisible = false;

        // Every line, a blank line, then the prompt with a blinking cursor
        string screen = BuildScreen(_lines, _totalUnits) + "\n" + LoadPrompt;
        bool cursorOn = true;
        _text.Text = screen + "█";
        _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _blinkTimer.Tick += (_, _) =>
        {
            cursorOn = !cursorOn;
            _text.Text = screen + (cursorOn ? "█" : " ");
        };
        _blinkTimer.Start();
    }

    private void BeginTyping()
    {
        if (_typeTimer != null || _waitingForKey || _finished)
            return;

        _typingClock.Start();
        _typeTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
        _typeTimer.Tick += (_, _) => TypeTick();
        _typeTimer.Start();
    }

    private void TypeTick()
    {
        double progress = Math.Min(1.0, _typingClock.Elapsed.TotalSeconds / TypingDuration.TotalSeconds);
        string shown = BuildScreen(_lines, (int)Math.Round(progress * _totalUnits));

        if (progress < 1.0)
        {
            _text.Text = shown + "█";
            return;
        }

        _text.Text = shown;
        _typeTimer?.Stop();

        // Hold the finished screen for a beat before handing over to the app
        _holdTimer = new DispatcherTimer { Interval = HoldDuration };
        _holdTimer.Tick += (_, _) =>
        {
            _holdTimer?.Stop();
            Finish();
        };
        _holdTimer.Start();
    }

    private void Finish()
    {
        if (_finished)
            return;
        _finished = true;
        StopTimers();
        Finished?.Invoke(this, EventArgs.Empty);
    }

    private void StopTimers()
    {
        _startDelayTimer?.Stop();
        _typeTimer?.Stop();
        _holdTimer?.Stop();
        _blinkTimer?.Stop();
    }

    /// <summary>Total typing "units" for <paramref name="lines"/>: every character plus a beat after each line.</summary>
    internal static int TotalUnits(IReadOnlyList<string> lines) =>
        Math.Max(1, lines.Sum(line => line.Length + LinePauseUnits));

    /// <summary>
    /// The text visible after <paramref name="units"/> of typing: whole lines (each with its trailing beat) followed
    /// by the part of the current line typed so far. Shared with the command line's "boot" command.
    /// </summary>
    internal static string BuildScreen(IReadOnlyList<string> lines, int units)
    {
        var shown = new StringBuilder();
        foreach (var line in lines)
        {
            int lineUnits = line.Length + LinePauseUnits;
            if (units >= lineUnits)
            {
                shown.Append(line).Append('\n');
                units -= lineUnits;
                continue;
            }
            shown.Append(line, 0, Math.Min(units, line.Length));
            break;
        }
        return shown.ToString();
    }
}
