using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace TERMinator.Linux.RainwireWm;

/// <summary>
/// Takes files dropped on the TERMinator window from a file manager (XDnD, text/uri-list), for Rainwire file drops
/// (RainwireFileDrops). Avalonia 11's X11 backend has no XDnD at all (its DragDrop is in-process only), so this is a
/// small XDnD v5 target of our own:
///
///   - our own X connection and a 1x1 unmapped proxy window on it; the TERMinator window gets XdndAware = 5 and
///     XdndProxy = the proxy (the proxy points at itself), so every source sends its Xdnd messages to the proxy, on
///     our connection, and Avalonia's event loop never sees them;
///   - XdndEnter/Position/Leave/Drop are answered with XdndStatus/XdndFinished; the drop's text/uri-list is fetched
///     with XConvertSelection onto the proxy;
///   - the queue is read by a 20 ms UI-thread timer, so everything runs on the UI thread with the rest of the app.
///
/// Under Wayland desktops Avalonia runs on XWayland, which bridges Wayland drags to XDnD, so this works there too.
/// Xlib errors (a source window gone) go to Avalonia's process-wide handler, which ignores them.
/// </summary>
public sealed class X11FileDropTarget : IDisposable
{
    private readonly Window _window;
    private readonly IntPtr _toplevel;
    private readonly Func<Visual, Point, bool> _canTake;
    private readonly Func<Visual, Point, string[], bool> _drop;
    private readonly Action? _left;   // the drag left the window, was let go or was refused (a lit target goes dark)
    private readonly DispatcherTimer _pump;
    private IntPtr _dpy;
    private readonly IntPtr _root, _proxy;

    private readonly IntPtr _xdndAware, _xdndProxy, _xdndEnter, _xdndPosition, _xdndStatus, _xdndLeave, _xdndDrop,
        _xdndFinished, _xdndSelection, _xdndTypeList, _xdndActionCopy, _uriList, _dropProp;

    // The drag over us now
    private IntPtr _source;
    private int _version;
    private bool _hasUris;
    private bool _accepting;
    private Point _lastPoint;
    private bool _waitingData;
    private DateTime _dropAt;

    private const int XdndVersion = 5;
    private const int ClientMessage = 33, SelectionNotify = 31;
    private const int PropModeReplace = 0;
    private static readonly IntPtr XA_ATOM = 4, XA_WINDOW = 33;

    private X11FileDropTarget(Window window, IntPtr toplevel, IntPtr dpy,
        Func<Visual, Point, bool> canTake, Func<Visual, Point, string[], bool> drop, Action? left)
    {
        _window = window;
        _toplevel = toplevel;
        _dpy = dpy;
        _canTake = canTake;
        _drop = drop;
        _left = left;

        _xdndAware = Atom("XdndAware");
        _xdndProxy = Atom("XdndProxy");
        _xdndEnter = Atom("XdndEnter");
        _xdndPosition = Atom("XdndPosition");
        _xdndStatus = Atom("XdndStatus");
        _xdndLeave = Atom("XdndLeave");
        _xdndDrop = Atom("XdndDrop");
        _xdndFinished = Atom("XdndFinished");
        _xdndSelection = Atom("XdndSelection");
        _xdndTypeList = Atom("XdndTypeList");
        _xdndActionCopy = Atom("XdndActionCopy");
        _uriList = Atom("text/uri-list");
        _dropProp = Atom("TERMINATOR_XDND_DROP");

        _root = XDefaultRootWindow(_dpy);
        _proxy = XCreateSimpleWindow(_dpy, _root, -10, -10, 1, 1, 0, IntPtr.Zero, IntPtr.Zero);
        var version = new[] { (IntPtr)XdndVersion };
        var proxy = new[] { _proxy };
        foreach (var w in new[] { _proxy, _toplevel })
        {
            XChangeProperty(_dpy, w, _xdndAware, XA_ATOM, 32, PropModeReplace, version, 1);
            XChangeProperty(_dpy, w, _xdndProxy, XA_WINDOW, 32, PropModeReplace, proxy, 1);
        }
        XFlush(_dpy);

        _pump = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(20) };
        _pump.Tick += (_, _) => Pump();
        _pump.Start();
    }

    /// <summary>
    /// Makes window a target for file drops from the OS. Null when it isn't an X11 window or Xlib can't be had
    /// (then drops simply aren't taken, as before).
    /// </summary>
    /// <param name="left">Optional: the drag left the window or was refused at the drop (Windows' DragLeave).</param>
    public static X11FileDropTarget? TryAttach(Window window, Func<Visual, Point, bool> canTake,
        Func<Visual, Point, string[], bool> drop, Action? left = null)
    {
        try
        {
            if (window.TryGetPlatformHandle() is not { HandleDescriptor: "XID" } handle || handle.Handle == IntPtr.Zero)
                return null;
            IntPtr dpy = XOpenDisplay(IntPtr.Zero);
            if (dpy == IntPtr.Zero)
                return null;
            return new X11FileDropTarget(window, handle.Handle, dpy, canTake, drop, left);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Debug.WriteLine("X11FileDropTarget: no Xlib: " + ex.Message);
            return null;
        }
    }

    public void Dispose()
    {
        _pump.Stop();
        if (_dpy == IntPtr.Zero)
            return;
        XDeleteProperty(_dpy, _toplevel, _xdndAware);
        XDeleteProperty(_dpy, _toplevel, _xdndProxy);
        XDestroyWindow(_dpy, _proxy);
        XCloseDisplay(_dpy);
        _dpy = IntPtr.Zero;
    }

    private IntPtr Atom(string name) => XInternAtom(_dpy, name, false);

    private void Pump()
    {
        if (_dpy == IntPtr.Zero)
            return;
        try
        {
            while (XPending(_dpy) > 0)
            {
                XNextEvent(_dpy, out var ev);
                if (ev.type == ClientMessage)   // sent to the proxy, window = the TERMinator window (or the proxy)
                    OnClientMessage(ref ev);
                else if (ev.type == SelectionNotify && ev.window == _proxy)
                    OnSelectionNotify(ref ev);
            }
            // A source that never answered our request for the files: let it go
            if (_waitingData && DateTime.UtcNow - _dropAt > TimeSpan.FromSeconds(10))
            {
                _waitingData = false;
                Finish(false);
                _left?.Invoke();
            }
        }
        catch (Exception ex)
        {
            // A bad message from another program must never take the terminal down
            Debug.WriteLine("X11FileDropTarget: " + ex);
        }
    }

    private void OnClientMessage(ref XEvent ev)
    {
        IntPtr type = ev.messageType;
        if (type == _xdndEnter)
        {
            _source = ev.l0;
            _version = (int)(((long)ev.l1 >> 24) & 0xFF);
            _waitingData = false;
            _accepting = false;
            _hasUris = false;
            if (((long)ev.l1 & 1) != 0)
                _hasUris = TypeListHasUris(_source);
            else
                _hasUris = ev.l2 == _uriList || ev.l3 == _uriList || ev.l4 == _uriList;
        }
        else if (type == _xdndPosition)
        {
            if (ev.l0 != _source)
            {
                // Position without an Enter we saw (shouldn't happen): take the source from it, no types known
                _source = ev.l0;
                _hasUris = false;
            }
            long packed = (long)ev.l2;
            int rx = (int)((packed >> 16) & 0xFFFF), ry = (int)(packed & 0xFFFF);
            _accepting = false;
            if (_hasUris && XTranslateCoordinates(_dpy, _root, _toplevel, rx, ry, out int x, out int y, out _))
            {
                double scale = _window.RenderScaling > 0 ? _window.RenderScaling : 1;
                _lastPoint = new Point(x / scale, y / scale);
                _accepting = _canTake(_window, _lastPoint);
            }
            SendStatus(_accepting);
        }
        else if (type == _xdndLeave)
        {
            if (ev.l0 == _source && !_waitingData)
                _source = IntPtr.Zero;
            _left?.Invoke();
        }
        else if (type == _xdndDrop)
        {
            if (ev.l0 != _source || !_accepting)
            {
                _source = ev.l0;
                _left?.Invoke();
                Finish(false);
                return;
            }
            _waitingData = true;
            _dropAt = DateTime.UtcNow;
            XConvertSelection(_dpy, _xdndSelection, _uriList, _dropProp, _proxy, _version >= 1 ? ev.l2 : IntPtr.Zero);
            XFlush(_dpy);
        }
    }

    private void OnSelectionNotify(ref XEvent ev)
    {
        if (!_waitingData)
            return;
        _waitingData = false;
        string[] paths = Array.Empty<string>();
        if (ev.selProperty != IntPtr.Zero)
            paths = ParseUriList(ReadProperty(_proxy, _dropProp));
        // Answer the source first: taking the files may put up a toast, and the source is waiting
        Finish(paths.Length > 0);
        if (paths.Length == 0)
            _left?.Invoke();
        if (paths.Length > 0)
        {
            Point at = _lastPoint;
            Dispatcher.UIThread.Post(() => _drop(_window, at, paths));
        }
    }

    private void SendStatus(bool accept)
    {
        var e = new XEvent
        {
            type = ClientMessage,
            window = _source,
            messageType = _xdndStatus,
            format = 32,
            l0 = _toplevel,
            l1 = (IntPtr)((accept ? 1 : 0) | 2),   // 2: send a Position for every move (no "quiet" rectangle)
            l2 = IntPtr.Zero,
            l3 = IntPtr.Zero,
            l4 = accept ? _xdndActionCopy : IntPtr.Zero,
        };
        XSendEvent(_dpy, _source, false, IntPtr.Zero, ref e);
        XFlush(_dpy);
    }

    private void Finish(bool ok)
    {
        if (_source == IntPtr.Zero)
            return;
        var e = new XEvent
        {
            type = ClientMessage,
            window = _source,
            messageType = _xdndFinished,
            format = 32,
            l0 = _toplevel,
            l1 = (IntPtr)(ok ? 1 : 0),
            l2 = ok ? _xdndActionCopy : IntPtr.Zero,
        };
        XSendEvent(_dpy, _source, false, IntPtr.Zero, ref e);
        XFlush(_dpy);
        _source = IntPtr.Zero;
        _accepting = false;
    }

    private bool TypeListHasUris(IntPtr source)
    {
        if (XGetWindowProperty(_dpy, source, _xdndTypeList, IntPtr.Zero, (IntPtr)1024, false, XA_ATOM,
                out _, out int format, out IntPtr count, out _, out IntPtr data) != 0 || data == IntPtr.Zero)
            return false;
        try
        {
            if (format != 32)
                return false;
            for (long i = 0; i < (long)count; i++)
                if (Marshal.ReadIntPtr(data, (int)(i * IntPtr.Size)) == _uriList)
                    return true;
            return false;
        }
        finally
        {
            XFree(data);
        }
    }

    private byte[] ReadProperty(IntPtr window, IntPtr property)
    {
        // Up to 4 MB of URIs (thousands of files); format 8, so nitems is the byte count
        if (XGetWindowProperty(_dpy, window, property, IntPtr.Zero, (IntPtr)(1024 * 1024), true, IntPtr.Zero,
                out IntPtr actualType, out int format, out IntPtr count, out _, out IntPtr data) != 0 || data == IntPtr.Zero)
            return Array.Empty<byte>();
        try
        {
            if (format != 8 || actualType == IntPtr.Zero || (long)count <= 0)
                return Array.Empty<byte>();   // INCR (a list too big to send at once) isn't taken
            var bytes = new byte[(int)(long)count];
            Marshal.Copy(data, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            XFree(data);
        }
    }

    /// <summary>The local paths in a text/uri-list (RFC 2483): file: URIs only, comments skipped.</summary>
    internal static string[] ParseUriList(byte[] bytes)
    {
        var paths = new List<string>();
        foreach (var raw in Encoding.UTF8.GetString(bytes).Split('\n'))
        {
            string line = raw.Trim('\r', '\0', ' ', '\t');
            if (line.Length == 0 || line[0] == '#')
                continue;
            if (!Uri.TryCreate(line, UriKind.Absolute, out var uri) || !uri.IsFile)
                continue;
            string host = uri.Host;
            if (host.Length > 0 && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                && !host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                continue;   // a file on another machine
            string path = Uri.UnescapeDataString(uri.AbsolutePath);
            if (path.Length > 0 && !paths.Contains(path))
                paths.Add(path);
        }
        return paths.ToArray();
    }

    // ---- Xlib ----

    /// <summary>XEvent (192 bytes on 64-bit), with the XClientMessageEvent and XSelectionEvent fields we use.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 192)]
    private struct XEvent
    {
        [FieldOffset(0)] public int type;
        [FieldOffset(32)] public IntPtr window;        // ClientMessage: window; SelectionNotify: requestor
        [FieldOffset(40)] public IntPtr messageType;   // ClientMessage: message_type; SelectionNotify: selection
        [FieldOffset(48)] public int format;           // ClientMessage (SelectionNotify: target)
        [FieldOffset(56)] public IntPtr l0;            // ClientMessage data.l[0]; SelectionNotify: property
        [FieldOffset(64)] public IntPtr l1;
        [FieldOffset(72)] public IntPtr l2;
        [FieldOffset(80)] public IntPtr l3;
        [FieldOffset(88)] public IntPtr l4;
        public readonly IntPtr selProperty => l0;
    }

    private const string LibX11 = "libX11.so.6";

    [DllImport(LibX11)] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport(LibX11)] private static extern int XCloseDisplay(IntPtr dpy);
    [DllImport(LibX11)] private static extern IntPtr XDefaultRootWindow(IntPtr dpy);
    [DllImport(LibX11)] private static extern IntPtr XInternAtom(IntPtr dpy, string name, bool onlyIfExists);
    [DllImport(LibX11)] private static extern IntPtr XCreateSimpleWindow(IntPtr dpy, IntPtr parent, int x, int y,
        uint width, uint height, uint borderWidth, IntPtr border, IntPtr background);
    [DllImport(LibX11)] private static extern int XDestroyWindow(IntPtr dpy, IntPtr window);
    [DllImport(LibX11)] private static extern int XChangeProperty(IntPtr dpy, IntPtr window, IntPtr property, IntPtr type,
        int format, int mode, IntPtr[] data, int count);
    [DllImport(LibX11)] private static extern int XDeleteProperty(IntPtr dpy, IntPtr window, IntPtr property);
    [DllImport(LibX11)] private static extern int XPending(IntPtr dpy);
    [DllImport(LibX11)] private static extern int XNextEvent(IntPtr dpy, out XEvent ev);
    [DllImport(LibX11)] private static extern int XSendEvent(IntPtr dpy, IntPtr window, bool propagate, IntPtr mask, ref XEvent ev);
    [DllImport(LibX11)] private static extern int XFlush(IntPtr dpy);
    [DllImport(LibX11)] private static extern bool XTranslateCoordinates(IntPtr dpy, IntPtr src, IntPtr dst, int x, int y,
        out int dx, out int dy, out IntPtr child);
    [DllImport(LibX11)] private static extern int XConvertSelection(IntPtr dpy, IntPtr selection, IntPtr target,
        IntPtr property, IntPtr requestor, IntPtr time);
    [DllImport(LibX11)] private static extern int XGetWindowProperty(IntPtr dpy, IntPtr window, IntPtr property,
        IntPtr offset, IntPtr length, bool delete, IntPtr reqType, out IntPtr actualType, out int actualFormat,
        out IntPtr nItems, out IntPtr bytesAfter, out IntPtr prop);
    [DllImport(LibX11)] private static extern int XFree(IntPtr data);
}
