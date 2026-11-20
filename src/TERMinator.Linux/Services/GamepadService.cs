using System.Runtime.InteropServices;
using System.IO;

namespace TERMinator.Linux.Services;

/// <summary>
/// Gamepads for TRACE modules: up to four Xbox-style controllers. Polls while a module is running and reports changes
/// as the engine contract's pad events (engine_contract.h TE_IN_PAD_*): buttons in SDL GameController order, sticks
/// with down/right positive, triggers 0..32767, and pads coming and going. While TERMinator isn't the active window
/// the pads read as released and centred (and stop rumbling), so a game in the background never sees input meant for
/// another app. Events are raised on the polling thread.
///
/// Linux port of the Windows client's Services/GamepadService.cs (XInput). Here the pads are read straight from
/// evdev (/dev/input/event*), which needs no library: the kernel's own driver already presents an Xbox-style pad
/// with the buttons and axes below, whether it is plugged in by USB or Bluetooth. Joystick nodes are normally
/// readable by the user logged in at the seat (logind's ACLs), so nothing has to be installed or configured; a pad
/// we may only read, not write, works except for rumble. Force feedback is the kernel's FF_RUMBLE effect.
/// </summary>
public sealed class GamepadService : IDisposable
{
    public const int MaxPads = 4;
    private const int AxisCount = 6;

    // ---- evdev ----------------------------------------------------------------------------------------------------
    private const ushort EvKey = 0x01, EvAbs = 0x03, EvFf = 0x15;
    private const int OReadOnly = 0, OReadWrite = 2, ONonBlock = 0x800;

    /// <summary>struct input_event on 64-bit Linux: struct timeval (16) + type, code (2 each) + value (4).</summary>
    private const int InputEventSize = 24;

    /// <summary>evdev key codes and the contract's button numbers (TE_PAD_BUTTON_*), in the Xbox layout.</summary>
    private static readonly (ushort Code, int Button)[] ButtonMap =
    [
        (0x130, 0), (0x131, 1), (0x134, 2), (0x133, 3),   // SOUTH = A, EAST = B, WEST = X, NORTH = Y
        (0x13a, 4), (0x13c, 5), (0x13b, 6),               // SELECT = Back, MODE = Guide, START
        (0x13d, 7), (0x13e, 8),                           // THUMBL, THUMBR
        (0x136, 9), (0x137, 10),                          // TL, TR (shoulders)
        // Pads that report the d-pad as buttons rather than a hat (BTN_DPAD_*)
        (0x220, 11), (0x221, 12), (0x222, 13), (0x223, 14),
    ];

    /// <summary>evdev absolute axes and the contract's axis numbers (TE_PAD_AXIS_*).</summary>
    private static readonly (ushort Code, int Axis)[] AxisMap =
    [
        (0x00, 0), (0x01, 1),   // ABS_X, ABS_Y      -> left stick
        (0x03, 2), (0x04, 3),   // ABS_RX, ABS_RY    -> right stick
        (0x02, 4), (0x05, 5),   // ABS_Z, ABS_RZ     -> triggers
    ];

    private const ushort AbsHat0X = 0x10, AbsHat0Y = 0x11;

    [DllImport("libc", SetLastError = true)]
    private static extern int open(string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern nint read(int fd, byte[] buffer, nint count);

    [DllImport("libc", SetLastError = true)]
    private static extern nint write(int fd, byte[] buffer, nint count);

    [DllImport("libc", SetLastError = true, EntryPoint = "ioctl")]
    private static extern int ioctl(int fd, nuint request, byte[] argument);

    private static nuint Ior(int nr, int size) => (nuint)((2u << 30) | ((uint)size << 16) | (0x45u << 8) | (uint)nr);
    private static nuint Iow(int nr, int size) => (nuint)((1u << 30) | ((uint)size << 16) | (0x45u << 8) | (uint)nr);

    /// <summary>EVIOCGABS(axis): struct input_absinfo (value, min, max, fuzz, flat, resolution).</summary>
    private static nuint EviocgAbs(ushort axis) => Ior(0x40 + axis, 24);

    /// <summary>EVIOCGBIT(EV_KEY, len): the bitmap of the keys this device can report.</summary>
    private static nuint EviocgBit(ushort type, int len) => Ior(0x20 + type, len);

    /// <summary>EVIOCSFF: uploads a force-feedback effect (struct ff_effect is 48 bytes on 64-bit).</summary>
    private static readonly nuint EviocsFf = Iow(0x80, 48);

    /// <summary>One pad we have open, and what was last reported for it.</summary>
    private sealed class Pad
    {
        public required string Path;
        public int Fd = -1;
        public bool Writable;                       // opened read/write: rumble is possible
        public short RumbleId = -1;                 // the effect the kernel gave us, -1 = none uploaded
        public long RumbleUntil;                    // Environment.TickCount64; 0 = not rumbling
        public readonly bool[] Buttons = new bool[15];
        public readonly int[] Axes = new int[AxisCount];
        /// <summary>Per-axis (min, max) from the driver, so every pad's sticks reach the same numbers.</summary>
        public readonly (int Min, int Max)[] Range = new (int, int)[AxisCount];
        public int HatX, HatY;                      // the d-pad when it comes as a hat
        public readonly byte[] Buffer = new byte[InputEventSize * 64];
    }

    private readonly Pad?[] _pads = new Pad?[MaxPads];
    private readonly object _lock = new();
    private Thread? _thread;
    private volatile bool _running;
    private volatile bool _active = true;
    private long _nextScan;

    /// <summary>A pad was plugged in (true) or removed.</summary>
    public event Action<int, bool>? DeviceChanged;

    /// <summary>pad, button (TE_PAD_BUTTON_*), pressed.</summary>
    public event Action<int, int, bool>? ButtonChanged;

    /// <summary>pad, axis (TE_PAD_AXIS_*), value.</summary>
    public event Action<int, int, int>? AxisChanged;

    /// <summary>Whether TERMinator is the active window. While it isn't, pads read as released and centred.</summary>
    public bool Active
    {
        get => _active;
        set
        {
            bool was = _active;
            _active = value;
            if (!value)
                StopAllRumble();
            if (was != value)
                ZeroOrRestore(value);
        }
    }

    /// <summary>Starts polling (a module is running).</summary>
    public void Start()
    {
        if (_running)
            return;
        _running = true;
        _nextScan = 0;
        _thread = new Thread(Poll) { IsBackground = true, Name = "TRACE gamepads" };
        _thread.Start();
    }

    /// <summary>Stops polling (no module is running): stops any rumble, closes the pads and forgets them.</summary>
    public void Stop()
    {
        if (!_running)
            return;
        _running = false;
        _thread?.Join(500);
        _thread = null;
        StopAllRumble();
        lock (_lock)
        {
            for (int i = 0; i < MaxPads; i++)
            {
                if (_pads[i] is { Fd: >= 0 } pad)
                    close(pad.Fd);
                _pads[i] = null;
            }
        }
    }

    /// <summary>
    /// Tells a module that has just started about the pads already plugged in and how they are held right now,
    /// through the given callbacks (device, button, axis).
    /// </summary>
    public void Replay(Action<int, bool> device, Action<int, int, bool> button, Action<int, int, int> axis)
    {
        // While TERMinator isn't the active window the pads read as released and centred, so a module starting now
        // must not be handed what they are really doing -- it would see a button held that it was never told about.
        // ZeroOrRestore sends the true state when we come forward again.
        bool active = _active;
        lock (_lock)
        {
            for (int index = 0; index < MaxPads; index++)
            {
                var pad = _pads[index];
                if (pad == null)
                    continue;
                device(index, true);
                if (!active)
                    continue;   // the pad is there, but everything on it reads released and centred
                for (int b = 0; b < pad.Buttons.Length; b++)
                    if (pad.Buttons[b])
                        button(index, b, true);
                for (int a = 0; a < AxisCount; a++)
                    if (pad.Axes[a] != 0)
                        axis(index, a, pad.Axes[a]);
            }
        }
    }

    /// <summary>Rumbles a pad: low (heavy) and high (light) motors 0..32767 for ms milliseconds; 0 ms stops it.</summary>
    public void Rumble(int pad, int low, int high, int ms)
    {
        if (pad < 0 || pad >= MaxPads || (!_active && ms > 0))
            return;
        lock (_lock)
        {
            if (_pads[pad] is not { Fd: >= 0, Writable: true } device)
                return;
            if (ms <= 0)
            {
                PlayEffect(device, false);
                device.RumbleUntil = 0;
                return;
            }
            if (!UploadRumble(device, low, high, ms))
                return;
            PlayEffect(device, true);
            device.RumbleUntil = Environment.TickCount64 + ms;
        }
    }

    /// <summary>
    /// Gives the kernel an FF_RUMBLE effect for this pad (re-using its effect slot, which is what the id is for)
    /// with the two motor strengths and how long it should run.
    /// </summary>
    private static bool UploadRumble(Pad pad, int low, int high, int ms)
    {
        var effect = new byte[48];
        BitConverter.TryWriteBytes(effect.AsSpan(0), (ushort)0x50);              // type = FF_RUMBLE
        BitConverter.TryWriteBytes(effect.AsSpan(2), pad.RumbleId);              // id (-1 = give me a new one)
        BitConverter.TryWriteBytes(effect.AsSpan(4), (ushort)0);                 // direction
        BitConverter.TryWriteBytes(effect.AsSpan(6), (ushort)0);                 // trigger.button
        BitConverter.TryWriteBytes(effect.AsSpan(8), (ushort)0);                 // trigger.interval
        BitConverter.TryWriteBytes(effect.AsSpan(10), (ushort)Math.Clamp(ms, 0, ushort.MaxValue));   // replay.length
        BitConverter.TryWriteBytes(effect.AsSpan(12), (ushort)0);                // replay.delay
        // The union starts at 16 (it is pointer-aligned): ff_rumble_effect { strong_magnitude, weak_magnitude }
        BitConverter.TryWriteBytes(effect.AsSpan(16), (ushort)Math.Clamp(low * 2, 0, 65535));
        BitConverter.TryWriteBytes(effect.AsSpan(18), (ushort)Math.Clamp(high * 2, 0, 65535));
        if (ioctl(pad.Fd, EviocsFf, effect) < 0)
        {
            pad.Writable = false;   // the driver won't take effects: don't ask again
            return false;
        }
        pad.RumbleId = BitConverter.ToInt16(effect, 2);
        return true;
    }

    /// <summary>Starts or stops the pad's uploaded effect (an EV_FF event written back to the device).</summary>
    private static void PlayEffect(Pad pad, bool play)
    {
        if (pad.RumbleId < 0)
            return;
        var ev = new byte[InputEventSize];
        BitConverter.TryWriteBytes(ev.AsSpan(16), EvFf);
        BitConverter.TryWriteBytes(ev.AsSpan(18), (ushort)pad.RumbleId);
        BitConverter.TryWriteBytes(ev.AsSpan(20), play ? 1 : 0);
        write(pad.Fd, ev, InputEventSize);
    }

    private void StopAllRumble()
    {
        for (int pad = 0; pad < MaxPads; pad++)
        {
            bool rumbling;
            lock (_lock)
                rumbling = _pads[pad]?.RumbleUntil != 0 && _pads[pad] != null;
            if (rumbling)
                Rumble(pad, 0, 0, 0);
        }
    }

    /// <summary>
    /// TERMinator became active or inactive: report everything as released and centred while it isn't, and what the
    /// pad is really doing again when it is. A game in the background must not act on keys meant for another app.
    /// </summary>
    private void ZeroOrRestore(bool active)
    {
        var reported = new List<Action>();
        lock (_lock)
        {
            for (int index = 0; index < MaxPads; index++)
            {
                var pad = _pads[index];
                if (pad == null)
                    continue;
                int slot = index;
                for (int b = 0; b < pad.Buttons.Length; b++)
                {
                    if (!pad.Buttons[b])
                        continue;
                    int button = b;
                    bool pressed = active;
                    reported.Add(() => ButtonChanged?.Invoke(slot, button, pressed));
                }
                for (int a = 0; a < AxisCount; a++)
                {
                    if (pad.Axes[a] == 0)
                        continue;
                    int axis = a, value = active ? pad.Axes[a] : 0;
                    reported.Add(() => AxisChanged?.Invoke(slot, axis, value));
                }
            }
        }
        foreach (var report in reported)
            report();
    }

    private void Poll()
    {
        var reported = new List<Action>();
        while (_running)
        {
            long now = Environment.TickCount64;
            reported.Clear();
            lock (_lock)
            {
                if (now >= _nextScan)
                {
                    _nextScan = now + 1000;   // look for pads coming and going about once a second
                    ScanDevices(reported);
                }
                for (int index = 0; index < MaxPads; index++)
                {
                    var pad = _pads[index];
                    if (pad == null)
                        continue;
                    if (!ReadEvents(index, pad, reported))
                    {
                        // Unplugged: let go of everything it was holding, then say it's gone
                        ReleaseAll(index, pad, reported);
                        close(pad.Fd);
                        _pads[index] = null;
                        int slot = index;
                        reported.Add(() => DeviceChanged?.Invoke(slot, false));
                        continue;
                    }
                    if (pad.RumbleUntil != 0 && now >= pad.RumbleUntil)
                    {
                        PlayEffect(pad, false);
                        pad.RumbleUntil = 0;
                    }
                }
            }
            foreach (var report in reported)
                report();
            Thread.Sleep(4);   // ~250 checks a second: well under a frame of lag
        }
    }

    /// <summary>Looks for gamepads that aren't open yet and takes the first free slot for each.</summary>
    private void ScanDevices(List<Action> reported)
    {
        string[] nodes;
        try
        {
            nodes = Directory.GetFiles("/dev/input", "event*");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
        Array.Sort(nodes, StringComparer.Ordinal);
        foreach (string node in nodes)
        {
            if (Array.Exists(_pads, p => p != null && p.Path == node))
                continue;
            int free = Array.IndexOf(_pads, null);
            if (free < 0)
                return;   // four already
            var pad = TryOpen(node);
            if (pad == null)
                continue;
            _pads[free] = pad;
            int slot = free;
            reported.Add(() => DeviceChanged?.Invoke(slot, true));
        }
    }

    /// <summary>
    /// Opens one /dev/input node if it is a gamepad: it must report the south face button (every pad does) and have
    /// the two stick axes. Read/write first, for rumble, then read-only.
    /// </summary>
    private static Pad? TryOpen(string node)
    {
        int fd = open(node, OReadWrite | ONonBlock);
        bool writable = fd >= 0;
        if (fd < 0)
            fd = open(node, OReadOnly | ONonBlock);
        if (fd < 0)
            return null;

        var keys = new byte[(0x300 + 7) / 8];
        bool isPad = ioctl(fd, EviocgBit(EvKey, keys.Length), keys) >= 0 && (keys[0x130 / 8] & (1 << (0x130 % 8))) != 0;
        if (!isPad)
        {
            close(fd);
            return null;
        }
        var pad = new Pad { Path = node, Fd = fd, Writable = writable };
        var info = new byte[24];
        bool haveSticks = true;
        foreach (var (code, axis) in AxisMap)
        {
            if (ioctl(fd, EviocgAbs(code), info) >= 0)
            {
                pad.Axes[axis] = 0;
                pad.Range[axis] = (BitConverter.ToInt32(info, 4), BitConverter.ToInt32(info, 8));
            }
            else if (axis <= 1)
            {
                haveSticks = false;   // no left stick: a keyboard or a mouse with gamepad-looking keys
            }
        }
        if (!haveSticks)
        {
            close(fd);
            return null;
        }
        return pad;
    }

    /// <summary>
    /// Reads everything the pad has queued. False when it went away (a read error that isn't "nothing there yet"),
    /// which is how an unplugged pad shows up.
    /// </summary>
    private bool ReadEvents(int index, Pad pad, List<Action> reported)
    {
        while (true)
        {
            nint got = read(pad.Fd, pad.Buffer, pad.Buffer.Length);
            if (got <= 0)
            {
                if (got == 0)
                    return false;
                int error = Marshal.GetLastWin32Error();
                return error is 11 or 4;   // EAGAIN (nothing queued) or EINTR: still there
            }
            for (int at = 0; at + InputEventSize <= got; at += InputEventSize)
            {
                ushort type = BitConverter.ToUInt16(pad.Buffer, at + 16);
                ushort code = BitConverter.ToUInt16(pad.Buffer, at + 18);
                int value = BitConverter.ToInt32(pad.Buffer, at + 20);
                if (type == EvKey)
                    HandleKey(index, pad, code, value != 0, reported);
                else if (type == EvAbs)
                    HandleAbs(index, pad, code, value, reported);
            }
            if (got < pad.Buffer.Length)
                return true;   // that was everything queued
        }
    }

    private void HandleKey(int index, Pad pad, ushort code, bool pressed, List<Action> reported)
    {
        foreach (var (mapped, button) in ButtonMap)
        {
            if (mapped != code || pad.Buttons[button] == pressed)
                continue;
            pad.Buttons[button] = pressed;
            bool report = _active;
            reported.Add(() => ButtonChanged?.Invoke(index, button, pressed && report));
            return;
        }
    }

    private void HandleAbs(int index, Pad pad, ushort code, int raw, List<Action> reported)
    {
        if (code is AbsHat0X or AbsHat0Y)
        {
            // The d-pad as a hat: -1, 0, 1 on each axis, which the contract has as four buttons
            int old = code == AbsHat0X ? pad.HatX : pad.HatY;
            if (old == raw)
                return;
            if (code == AbsHat0X)
                pad.HatX = raw;
            else
                pad.HatY = raw;
            (int negative, int positive) = code == AbsHat0X ? (13, 14) : (11, 12);   // left/right, up/down
            SetHatButton(index, pad, old < 0 ? negative : positive, old != 0, raw < 0 ? negative : positive, raw != 0, reported);
            return;
        }
        foreach (var (mapped, axis) in AxisMap)
        {
            if (mapped != code)
                continue;
            int value = Scale(axis, raw, pad.Range[axis]);
            int old = pad.Axes[axis];
            // Sticks jitter by a few counts at rest: skip changes too small to matter, but always report reaching
            // the centre or an end, so a released stick reads exactly 0
            bool edge = value == 0 || value == short.MaxValue || value == short.MinValue;
            if (value == old || (!edge && Math.Abs(value - old) < 128))
                return;
            pad.Axes[axis] = value;
            bool report = _active;
            reported.Add(() => AxisChanged?.Invoke(index, axis, report ? value : 0));
            return;
        }
    }

    /// <summary>A hat moved: whichever of its two buttons was down goes up, and the new one goes down.</summary>
    private void SetHatButton(int index, Pad pad, int oldButton, bool hadOld, int newButton, bool hasNew,
                              List<Action> reported)
    {
        bool report = _active;
        if (hadOld && pad.Buttons[oldButton])
        {
            pad.Buttons[oldButton] = false;
            int button = oldButton;
            reported.Add(() => ButtonChanged?.Invoke(index, button, false));
        }
        if (hasNew && !pad.Buttons[newButton])
        {
            pad.Buttons[newButton] = true;
            int button = newButton;
            reported.Add(() => ButtonChanged?.Invoke(index, button, report));
        }
    }

    /// <summary>
    /// The driver's own range for an axis onto the contract's: sticks -32768..32767 with down and right positive
    /// (evdev's y already points down), triggers 0..32767.
    /// </summary>
    private static int Scale(int axis, int raw, (int Min, int Max) range)
    {
        if (range.Max <= range.Min)
            return 0;
        double t = (double)(raw - range.Min) / (range.Max - range.Min);   // 0..1
        if (axis >= 4)
            return (int)Math.Round(Math.Clamp(t, 0, 1) * short.MaxValue);
        return (int)Math.Round(Math.Clamp(t * 2 - 1, -1, 1) * (t * 2 - 1 < 0 ? 32768 : short.MaxValue));
    }

    /// <summary>A pad went away: everything it was holding is released, so nothing sticks in the module.</summary>
    private void ReleaseAll(int index, Pad pad, List<Action> reported)
    {
        for (int b = 0; b < pad.Buttons.Length; b++)
        {
            if (!pad.Buttons[b])
                continue;
            pad.Buttons[b] = false;
            int button = b;
            reported.Add(() => ButtonChanged?.Invoke(index, button, false));
        }
        for (int a = 0; a < AxisCount; a++)
        {
            if (pad.Axes[a] == 0)
                continue;
            pad.Axes[a] = 0;
            int axis = a;
            reported.Add(() => AxisChanged?.Invoke(index, axis, 0));
        }
    }

    public void Dispose() => Stop();
}
