using System.Diagnostics;
using System.Runtime.InteropServices;
using TERMinator.Linux.Services;

namespace TERMinator.Linux.RainwireWm;

/// <summary>
/// The native side of Rainwire's window protocol (native/apc/wm_bridge.c): it answers the board's Query and queues the
/// Rainwire:WM messages for us. A libsyncterm-native.so built before wm_bridge.c has neither export; then Available is
/// false, the board's Query goes unanswered, and Rainwire draws its windows itself as it would for any terminal.
/// </summary>
internal static class WmNative
{
    private const string DllName = "libsyncterm-native";

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void syncterm_wm_set_enabled(int enabled);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int syncterm_wm_poll(byte[] buffer, int bufferLen);

    public static bool Available { get; private set; } = true;

    /// <summary>Switches the protocol on in the native bridge. False when the library doesn't have it.</summary>
    public static bool Enable(bool on)
    {
        if (!Available)
            return false;
        try
        {
            syncterm_wm_set_enabled(on ? 1 : 0);
            return true;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            Available = false;
            Debug.WriteLine("RainwireWm: this libsyncterm-native.so has no window protocol (rebuild native): " + ex.Message);
            return false;
        }
    }

    /// <summary>The oldest queued message into buffer: its length, 0 when none, -1 when buffer is too small.</summary>
    public static int Poll(byte[] buffer)
    {
        if (!Available)
            return 0;
        try
        {
            return syncterm_wm_poll(buffer, buffer.Length);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            Available = false;
            return 0;
        }
    }

    /// <summary>Bytes to the board, on the one connection.</summary>
    public static void Send(byte[] bytes) => NativeBridge.syncterm_send_data(bytes, bytes.Length);
}
