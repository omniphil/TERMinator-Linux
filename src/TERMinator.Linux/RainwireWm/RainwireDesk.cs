using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;
using TERMinator.Linux.Services;

namespace TERMinator.Linux.RainwireWm;

/// <summary>
/// Rainwire's client-drawn windows (Rainwire docs/WINDOWS.md, protocol v1): the compositor. A Rainwire board asks at
/// connect whether this terminal draws windows itself; native/apc/wm_bridge.c says yes (framed mode, no extra SSH
/// channels) and queues the board's messages, which this polls on the UI thread and hands to WmSession. The desk
/// layer (RainwireDeskLayer) draws the windows over the main screen.
///
/// With a libsyncterm-native.so built before wm_bridge.c, nothing answers the board, so it draws its windows itself
/// as for any terminal, and this stays idle. Ported from Windows' RainwireWm/RainwireDesk.cs 2026-09-30.
/// </summary>
public sealed class RainwireDesk
{
    private readonly WmSession _session;
    private readonly RainwireDeskLayer _layer;
    private readonly DispatcherTimer _poll;
    private byte[] _buffer = new byte[16384];
    private WmFont? _font;

    public RainwireDesk(Panel host, Func<Control?> mainScreen, Action focusMain)
    {
        _session = new WmSession(WmNative.Send);
        _layer = new RainwireDeskLayer(_session, mainScreen, focusMain, () => _font)
        {
            PictureRect = s => s is Controls.TerminalCanvas tc ? tc.GetBbsScreenRect() : null,
        };
        host.Children.Add(_layer);
        _session.Started += () => _font = LoadFont();

        Available = WmNative.Enable(true);
        _poll = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(15) };
        _poll.Tick += (_, _) => Poll();
        if (Available)
            _poll.Start();
    }

    /// <summary>The native bridge has the window protocol (a library with wm_bridge.c).</summary>
    public bool Available { get; }

    /// <summary>A board's windows are on screen.</summary>
    public bool Active => _session.Active;

    /// <summary>The window protocol, for file drops (RainwireFileDrops), which also run outside windowed mode.</summary>
    public WmSession Session => _session;

    /// <summary>The desk layer: drop targets and drags out of windows.</summary>
    public RainwireDeskLayer Layer => _layer;

    /// <summary>The Rainwire windows setting: the graphical look, or the board's text cells (RainwireWm/WmChrome.cs).</summary>
    public void ApplyLook(bool graphical) => _layer.ApplyLook(graphical);

    /// <summary>After a modal dialog: the keyboard back to the top window (see RainwireDeskLayer).</summary>
    public void RefocusAfterDialog() => _layer.RefocusAfterDialog();

    /// <summary>
    /// Takes what's queued now, rather than at the next tick: the ZMODEM auto-detect runs ahead of the poll timer, and
    /// a file drop's FileGo/FileGet (sent just before the transfer) has to be seen first.
    /// </summary>
    public void PollNow()
    {
        if (Available)
            Poll();
    }

    private void Poll()
    {
        // Everything queued since the last tick, in order; a burst of window output can be many messages
        for (int i = 0; i < 4096; i++)
        {
            int n = WmNative.Poll(_buffer);
            if (n == -1)
            {
                _buffer = new byte[_buffer.Length * 2];
                continue;
            }
            if (n <= 0)
                return;
            try
            {
                _session.Handle(_buffer.AsSpan(0, n));
            }
            catch (Exception ex)
            {
                // A bad message from a board must never take the terminal down
                Debug.WriteLine("RainwireWm: " + ex);
            }
        }
    }

    /// <summary>The Font smoothing setting, which the windows follow as the main screen does.</summary>
    public void ApplySmoothing(bool smooth) => _layer.ApplySmoothing(smooth);

    /// <summary>The connection went: drop the desk and anything queued.</summary>
    public void Reset()
    {
        _session.Reset();
        _font = null;
    }

    private static WmFont? LoadFont()
    {
        try
        {
            var buffer = new byte[256 * 32 + 2];
            int len = NativeBridge.syncterm_get_font_bitmap(buffer, buffer.Length);
            int height = len > 2 ? buffer[1] : 0;
            if (height <= 0 || height > 32 || len - 2 < 256 * height)
                return null;
            return new WmFont { Data = buffer[2..len], Height = height };
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }
}
