using System.Diagnostics;
using System.IO;
using System.Text;
using Avalonia.Threading;

namespace TERMinator.Linux.Services;

/// <summary>
/// The board's OSC 777 notifications as desktop notifications (PORT_TO_MAC_LINUX.md 12.72, Rainwire
/// docs/CLIENT_CAPS.md "Notifications"), through notify-send (libnotify-bin / libnotify), arguments as a list, never a
/// shell. Without notify-send on PATH there are none and the Caps answer leaves out notify=1 (Available). At most one is
/// shown every 5 seconds; one arriving inside that wait replaces the one waiting, and the latest is shown when the wait
/// is over. Clicking it brings the window back only with a notify-send new enough for --action (libnotify 0.7.10+,
/// probed once from --help); an older one just shows it. Post/Reset on the UI thread.
/// </summary>
public sealed class BoardNotifier : IDisposable
{
    /// <summary>The 5 s between two notifications (CLIENT_CAPS.md).</summary>
    private const long MinGapMs = 5000;

    private static readonly Lazy<string?> _notifySend = new(FindNotifySend);
    private static readonly Lazy<Features> _features = new(ProbeFeatures);

    [Flags]
    private enum Features { None = 0, Action = 1, Wait = 2, PrintId = 4, ReplaceId = 8 }

    /// <summary>notify-send is installed: board notifications can be shown (notify=1 in the Caps answer).</summary>
    public static bool Available => _notifySend.Value != null;

    private readonly Func<bool> _suppress;
    private readonly Action _bringToFront;
    private readonly DispatcherTimer _gapTimer;
    private long _lastShown = -MinGapMs;                  // Environment.TickCount64 of the last one shown
    private (string Title, string Body)? _waiting;

    private readonly object _procLock = new();
    private Process? _waitingProc;                         // the notify-send still waiting for a click (--action)
    private uint _lastId;                                  // its notification, replaced by the next (--replace-id)

    /// <param name="suppress">True while a notification must not be shown: the session is on screen in the active
    /// window (checked again when a waiting one's turn comes).</param>
    /// <param name="bringToFront">Restores and activates the window and shows the session (UI thread).</param>
    public BoardNotifier(Func<bool> suppress, Action bringToFront)
    {
        _suppress = suppress;
        _bringToFront = bringToFront;
        _gapTimer = new DispatcherTimer();
        _gapTimer.Tick += (_, _) =>
        {
            _gapTimer.Stop();
            if (_waiting is { } note)
            {
                _waiting = null;
                Post(note.Title, note.Body);
            }
        };
    }

    /// <summary>A notification from the board (title already filled in with the board's name when it was empty).</summary>
    public void Post(string title, string body)
    {
        if (!Available || _suppress())
            return;
        long sinceLast = Environment.TickCount64 - _lastShown;
        if (sinceLast < MinGapMs || _gapTimer.IsEnabled)
        {
            // Inside the 5 s: this one replaces any waiting, and is shown when the gap is over
            _waiting = (title, body);
            if (!_gapTimer.IsEnabled)
            {
                _gapTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, MinGapMs - sinceLast));
                _gapTimer.Start();
            }
            return;
        }
        _lastShown = Environment.TickCount64;
        Task.Run(() => Show(title, body));
    }

    /// <summary>The call ended: forget the one waiting (the native side drops its queue too).</summary>
    public void Reset()
    {
        _gapTimer.Stop();
        _waiting = null;
    }

    /// <summary>The window closes: a notify-send still waiting for a click has no window to bring back.</summary>
    public void Dispose()
    {
        Reset();
        lock (_procLock)
        {
            StopWaiting();
        }
    }

    /// <summary>Runs notify-send (thread pool).</summary>
    private void Show(string title, string body)
    {
        if (_notifySend.Value is not { } exe)
            return;
        Features features = _features.Value;
        bool clickable = features.HasFlag(Features.Action);
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = clickable,
            RedirectStandardError = true,
        };
        UseSystemLibraries(psi);
        psi.ArgumentList.Add("--app-name=TERMinator");
        lock (_procLock)
        {
            if (features.HasFlag(Features.ReplaceId) && _lastId != 0)
                psi.ArgumentList.Add("--replace-id=" + _lastId);
            // The previous one's wait ends: the new one is the one a click answers
            StopWaiting();
        }
        if (clickable)
        {
            psi.ArgumentList.Add("--action=default=Open");
            if (features.HasFlag(Features.Wait))
                psi.ArgumentList.Add("--wait");
            if (features.HasFlag(Features.PrintId))
                psi.ArgumentList.Add("--print-id");
        }
        psi.ArgumentList.Add("--");                       // a title or body starting with "-" is text, not an option
        psi.ArgumentList.Add(title);
        psi.ArgumentList.Add(EscapeMarkup(body));        // most servers read the body as markup

        try
        {
            var proc = Process.Start(psi);
            if (proc == null)
                return;
            proc.ErrorDataReceived += (_, _) => { };
            proc.BeginErrorReadLine();
            if (!clickable)
            {
                proc.WaitForExit(10_000);
                proc.Dispose();
                return;
            }
            lock (_procLock)
                _waitingProc = proc;
            // --print-id prints the notification's id first; a click then prints the action's key ("default")
            string? line;
            while ((line = proc.StandardOutput.ReadLine()) != null)
            {
                line = line.Trim();
                if (uint.TryParse(line, out uint id))
                {
                    lock (_procLock)
                        _lastId = id;
                }
                else if (line == "default")
                    Dispatcher.UIThread.Post(_bringToFront);
            }
            lock (_procLock)
            {
                if (_waitingProc == proc)
                    _waitingProc = null;
            }
            proc.Dispose();
        }
        catch
        {
            // notify-send vanished or failed: this one is lost, the next one tries again
        }
    }

    /// <summary>Ends the notify-send still waiting for a click (its notification stays up). Under _procLock.</summary>
    private void StopWaiting()
    {
        if (_waitingProc is not { } old)
            return;
        _waitingProc = null;
        try
        {
            if (!old.HasExited)
                old.Kill();
        }
        catch { /* exited meanwhile */ }
    }

    /// <summary>The Desktop Notifications body markup's three special characters as entities: a board's text shows as
    /// written, never as links or images.</summary>
    private static string EscapeMarkup(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
            sb.Append(c switch { '&' => "&amp;", '<' => "&lt;", '>' => "&gt;", _ => c.ToString() });
        return sb.ToString();
    }

    /// <summary>Running as an AppImage: the system's notify-send gets the system's libraries, not the ones AppRun put
    /// on LD_LIBRARY_PATH for TERMinator (as UpdateService.ReplaceAndRelaunch).</summary>
    private static void UseSystemLibraries(ProcessStartInfo psi)
    {
        var appDir = Environment.GetEnvironmentVariable("APPDIR");
        if (string.IsNullOrEmpty(appDir))
            return;
        var kept = (Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") ?? "")
            .Split(':', StringSplitOptions.RemoveEmptyEntries).Where(p => !p.StartsWith(appDir, StringComparison.Ordinal));
        psi.Environment["LD_LIBRARY_PATH"] = string.Join(':', kept);
    }

    private static string? FindNotifySend()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string path = Path.Combine(dir, "notify-send");
                if (File.Exists(path))
                    return path;
            }
            catch { /* a malformed PATH entry */ }
        }
        return null;
    }

    /// <summary>What this notify-send takes, from its --help (old ones have no --action, --wait, -p or -r).</summary>
    private static Features ProbeFeatures()
    {
        if (_notifySend.Value is not { } exe)
            return Features.None;
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            UseSystemLibraries(psi);
            psi.ArgumentList.Add("--help");
            using var proc = Process.Start(psi);
            if (proc == null)
                return Features.None;
            var errTask = proc.StandardError.ReadToEndAsync();
            string help = proc.StandardOutput.ReadToEnd() + errTask.GetAwaiter().GetResult();
            proc.WaitForExit(5000);
            var features = Features.None;
            if (help.Contains("--action")) features |= Features.Action;
            if (help.Contains("--wait")) features |= Features.Wait;
            if (help.Contains("--print-id")) features |= Features.PrintId;
            if (help.Contains("--replace-id")) features |= Features.ReplaceId;
            return features;
        }
        catch
        {
            return Features.None;
        }
    }
}
