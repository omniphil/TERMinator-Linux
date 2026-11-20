using Avalonia.Input;

namespace TERMinator.Linux.Controls;

/// <summary>
/// Where a key sits on a PC keyboard, from Avalonia's layout-independent <see cref="PhysicalKey"/>. Two users:
/// <list type="bullet">
/// <item>SyncTERM physical key reports (CSI = 1 h, TerminalCanvas): the Linux evdev code.</item>
/// <item>TRACE modules (EngineHostLayer, engine contract TE_IN_KEY): the set-1 scancode, its E0-extended flag and the
/// Windows virtual key, exactly what the Windows client reads from raw WM_KEYDOWN messages (US-layout VKs; generic
/// VK_SHIFT / VK_CONTROL / VK_MENU for the modifiers, as Windows sends them, with the scancode/extended flag telling
/// left from right).</item>
/// </list>
/// Avalonia's KeyEventArgs carries no scancode, so this table stands in for one (upstream SyncTERM x_events.c does the
/// same from X keycodes). When PhysicalKey is None (some input methods), the logical Key is used as a fallback.
/// </summary>
public static class PhysicalKeyMap
{
    public readonly record struct PcKey(int Scan, bool Extended, int Vk, int Evdev);

    private static readonly Dictionary<PhysicalKey, PcKey> Table = Build();

    private static Dictionary<PhysicalKey, PcKey> Build()
    {
        var t = new Dictionary<PhysicalKey, PcKey>();
        // Base block: set-1 scancode == evdev code
        void B(PhysicalKey k, int scan, int vk) => t[k] = new PcKey(scan, false, vk, scan);
        // E0-extended keys: their own evdev codes
        void E(PhysicalKey k, int scan, int vk, int evdev) => t[k] = new PcKey(scan, true, vk, evdev);

        B(PhysicalKey.Escape, 0x01, 0x1B);
        B(PhysicalKey.Digit1, 0x02, '1'); B(PhysicalKey.Digit2, 0x03, '2'); B(PhysicalKey.Digit3, 0x04, '3');
        B(PhysicalKey.Digit4, 0x05, '4'); B(PhysicalKey.Digit5, 0x06, '5'); B(PhysicalKey.Digit6, 0x07, '6');
        B(PhysicalKey.Digit7, 0x08, '7'); B(PhysicalKey.Digit8, 0x09, '8'); B(PhysicalKey.Digit9, 0x0A, '9');
        B(PhysicalKey.Digit0, 0x0B, '0');
        B(PhysicalKey.Minus, 0x0C, 0xBD); B(PhysicalKey.Equal, 0x0D, 0xBB);
        B(PhysicalKey.Backspace, 0x0E, 0x08); B(PhysicalKey.Tab, 0x0F, 0x09);
        B(PhysicalKey.Q, 0x10, 'Q'); B(PhysicalKey.W, 0x11, 'W'); B(PhysicalKey.E, 0x12, 'E');
        B(PhysicalKey.R, 0x13, 'R'); B(PhysicalKey.T, 0x14, 'T'); B(PhysicalKey.Y, 0x15, 'Y');
        B(PhysicalKey.U, 0x16, 'U'); B(PhysicalKey.I, 0x17, 'I'); B(PhysicalKey.O, 0x18, 'O');
        B(PhysicalKey.P, 0x19, 'P');
        B(PhysicalKey.BracketLeft, 0x1A, 0xDB); B(PhysicalKey.BracketRight, 0x1B, 0xDD);
        B(PhysicalKey.Enter, 0x1C, 0x0D); B(PhysicalKey.ControlLeft, 0x1D, 0x11);
        B(PhysicalKey.A, 0x1E, 'A'); B(PhysicalKey.S, 0x1F, 'S'); B(PhysicalKey.D, 0x20, 'D');
        B(PhysicalKey.F, 0x21, 'F'); B(PhysicalKey.G, 0x22, 'G'); B(PhysicalKey.H, 0x23, 'H');
        B(PhysicalKey.J, 0x24, 'J'); B(PhysicalKey.K, 0x25, 'K'); B(PhysicalKey.L, 0x26, 'L');
        B(PhysicalKey.Semicolon, 0x27, 0xBA); B(PhysicalKey.Quote, 0x28, 0xDE); B(PhysicalKey.Backquote, 0x29, 0xC0);
        B(PhysicalKey.ShiftLeft, 0x2A, 0x10); B(PhysicalKey.Backslash, 0x2B, 0xDC);
        B(PhysicalKey.Z, 0x2C, 'Z'); B(PhysicalKey.X, 0x2D, 'X'); B(PhysicalKey.C, 0x2E, 'C');
        B(PhysicalKey.V, 0x2F, 'V'); B(PhysicalKey.B, 0x30, 'B'); B(PhysicalKey.N, 0x31, 'N');
        B(PhysicalKey.M, 0x32, 'M');
        B(PhysicalKey.Comma, 0x33, 0xBC); B(PhysicalKey.Period, 0x34, 0xBE); B(PhysicalKey.Slash, 0x35, 0xBF);
        B(PhysicalKey.ShiftRight, 0x36, 0x10); B(PhysicalKey.NumPadMultiply, 0x37, 0x6A);
        B(PhysicalKey.AltLeft, 0x38, 0x12); B(PhysicalKey.Space, 0x39, 0x20); B(PhysicalKey.CapsLock, 0x3A, 0x14);
        for (int i = 0; i < 10; i++)
            B(PhysicalKey.F1 + i, 0x3B + i, 0x70 + i);                       // F1-F10
        B(PhysicalKey.ScrollLock, 0x46, 0x91);
        B(PhysicalKey.NumPad7, 0x47, 0x67); B(PhysicalKey.NumPad8, 0x48, 0x68); B(PhysicalKey.NumPad9, 0x49, 0x69);
        B(PhysicalKey.NumPadSubtract, 0x4A, 0x6D);
        B(PhysicalKey.NumPad4, 0x4B, 0x64); B(PhysicalKey.NumPad5, 0x4C, 0x65); B(PhysicalKey.NumPad6, 0x4D, 0x66);
        B(PhysicalKey.NumPadAdd, 0x4E, 0x6B);
        B(PhysicalKey.NumPad1, 0x4F, 0x61); B(PhysicalKey.NumPad2, 0x50, 0x62); B(PhysicalKey.NumPad3, 0x51, 0x63);
        B(PhysicalKey.NumPad0, 0x52, 0x60); B(PhysicalKey.NumPadDecimal, 0x53, 0x6E);
        B(PhysicalKey.IntlBackslash, 0x56, 0xE2);
        B(PhysicalKey.F11, 0x57, 0x7A); B(PhysicalKey.F12, 0x58, 0x7B);
        for (int i = 0; i < 12; i++)                                         // F13-F24 (set-1 0x64..0x6F, as Windows reads them)
            t[PhysicalKey.F13 + i] = new PcKey(0x64 + i, false, 0x7C + i, 183 + i);

        // Windows reports Num Lock as E0 45 and Pause as plain 45 with VK_PAUSE
        E(PhysicalKey.NumLock, 0x45, 0x90, 69);
        t[PhysicalKey.Pause] = new PcKey(0x45, false, 0x13, 119);

        E(PhysicalKey.NumPadEnter, 0x1C, 0x0D, 96);
        E(PhysicalKey.ControlRight, 0x1D, 0x11, 97);
        E(PhysicalKey.NumPadDivide, 0x35, 0x6F, 98);
        E(PhysicalKey.PrintScreen, 0x37, 0x2C, 99);
        E(PhysicalKey.AltRight, 0x38, 0x12, 100);
        E(PhysicalKey.Home, 0x47, 0x24, 102);
        E(PhysicalKey.ArrowUp, 0x48, 0x26, 103);
        E(PhysicalKey.PageUp, 0x49, 0x21, 104);
        E(PhysicalKey.ArrowLeft, 0x4B, 0x25, 105);
        E(PhysicalKey.ArrowRight, 0x4D, 0x27, 106);
        E(PhysicalKey.End, 0x4F, 0x23, 107);
        E(PhysicalKey.ArrowDown, 0x50, 0x28, 108);
        E(PhysicalKey.PageDown, 0x51, 0x22, 109);
        E(PhysicalKey.Insert, 0x52, 0x2D, 110);
        E(PhysicalKey.Delete, 0x53, 0x2E, 111);
        E(PhysicalKey.MetaLeft, 0x5B, 0x5B, 125);
        E(PhysicalKey.MetaRight, 0x5C, 0x5C, 126);
        E(PhysicalKey.ContextMenu, 0x5D, 0x5D, 139);
        return t;
    }

    /// <summary>The PC key for a key event, or null when it isn't one we know.</summary>
    public static PcKey? Lookup(KeyEventArgs e)
    {
        var physical = e.PhysicalKey != PhysicalKey.None ? e.PhysicalKey : FromLogicalKey(e.Key);
        return Table.TryGetValue(physical, out var key) ? key : null;
    }

    /// <summary>The Linux evdev code for a key event (0 when unknown).</summary>
    public static int Evdev(KeyEventArgs e) => Lookup(e)?.Evdev ?? 0;

    /// <summary>Fallback for input paths that leave PhysicalKey empty: the logical key, read on a US layout.</summary>
    private static PhysicalKey FromLogicalKey(Key key)
    {
        if (key >= Key.A && key <= Key.Z)
            return PhysicalKey.A + (key - Key.A);
        if (key >= Key.D0 && key <= Key.D9)
            return key == Key.D0 ? PhysicalKey.Digit0 : PhysicalKey.Digit1 + (key - Key.D1);
        if (key >= Key.NumPad0 && key <= Key.NumPad9)
            return PhysicalKey.NumPad0 + (key - Key.NumPad0);
        if (key >= Key.F1 && key <= Key.F24)
            return PhysicalKey.F1 + (key - Key.F1);
        return key switch
        {
            Key.Escape => PhysicalKey.Escape,
            Key.Enter => PhysicalKey.Enter,
            Key.Space => PhysicalKey.Space,
            Key.Tab => PhysicalKey.Tab,
            Key.Back => PhysicalKey.Backspace,
            Key.LeftShift => PhysicalKey.ShiftLeft,
            Key.RightShift => PhysicalKey.ShiftRight,
            Key.LeftCtrl => PhysicalKey.ControlLeft,
            Key.RightCtrl => PhysicalKey.ControlRight,
            Key.LeftAlt => PhysicalKey.AltLeft,
            Key.RightAlt => PhysicalKey.AltRight,
            Key.LWin => PhysicalKey.MetaLeft,
            Key.RWin => PhysicalKey.MetaRight,
            Key.Up => PhysicalKey.ArrowUp,
            Key.Down => PhysicalKey.ArrowDown,
            Key.Left => PhysicalKey.ArrowLeft,
            Key.Right => PhysicalKey.ArrowRight,
            Key.Home => PhysicalKey.Home,
            Key.End => PhysicalKey.End,
            Key.PageUp => PhysicalKey.PageUp,
            Key.PageDown => PhysicalKey.PageDown,
            Key.Insert => PhysicalKey.Insert,
            Key.Delete => PhysicalKey.Delete,
            Key.CapsLock => PhysicalKey.CapsLock,
            Key.NumLock => PhysicalKey.NumLock,
            Key.Scroll => PhysicalKey.ScrollLock,
            Key.Pause => PhysicalKey.Pause,
            Key.OemMinus => PhysicalKey.Minus,
            Key.OemPlus => PhysicalKey.Equal,
            Key.OemOpenBrackets => PhysicalKey.BracketLeft,
            Key.OemCloseBrackets => PhysicalKey.BracketRight,
            Key.OemSemicolon => PhysicalKey.Semicolon,
            Key.OemQuotes => PhysicalKey.Quote,
            Key.OemTilde => PhysicalKey.Backquote,
            Key.OemPipe => PhysicalKey.Backslash,
            Key.OemComma => PhysicalKey.Comma,
            Key.OemPeriod => PhysicalKey.Period,
            Key.OemQuestion => PhysicalKey.Slash,
            Key.Multiply => PhysicalKey.NumPadMultiply,
            Key.Add => PhysicalKey.NumPadAdd,
            Key.Subtract => PhysicalKey.NumPadSubtract,
            Key.Divide => PhysicalKey.NumPadDivide,
            Key.Decimal => PhysicalKey.NumPadDecimal,
            _ => PhysicalKey.None,
        };
    }
}
