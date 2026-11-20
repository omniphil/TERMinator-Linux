using System.Runtime.InteropServices;

namespace TERMinator.Linux.Services;

public static class NativeBridge
{
    private const string DllName = "libsyncterm-native";

    // Connection type constants
    public const int CONN_TYPE_TELNET = 3;
    public const int CONN_TYPE_RAW = 4;
    public const int CONN_TYPE_SSH = 5;
    public const int CONN_TYPE_TELNETS = 12;
    public const int CONN_TYPE_WEBSOCKET = 14;
    public const int CONN_TYPE_WEBSOCKETS = 15;

    // Transfer state constants
    public const int TRANSFER_IDLE = 0;
    public const int TRANSFER_RECEIVING = 1;
    public const int TRANSFER_SENDING = 2;
    public const int TRANSFER_COMPLETE = 3;
    public const int TRANSFER_ERROR = 4;
    public const int TRANSFER_CANCELLED = 5;

    // Screen mode constants
    public const int SCREEN_MODE_80X25 = 0;
    public const int SCREEN_MODE_80X30 = 1;
    public const int SCREEN_MODE_80X40 = 2;
    public const int SCREEN_MODE_80X50 = 3;
    public const int SCREEN_MODE_132X25 = 4;
    public const int SCREEN_MODE_132X30 = 5;
    public const int SCREEN_MODE_132X40 = 6;
    public const int SCREEN_MODE_132X50 = 7;
    public const int SCREEN_MODE_132X60 = 8;
    public const int SCREEN_MODE_C64_40X25 = 9;    // Commodore 64, PETSCII
    public const int SCREEN_MODE_C128_40X25 = 10;  // Commodore 128 40-col, PETSCII
    public const int SCREEN_MODE_C128_80X25 = 11;  // Commodore 128 80-col, PETSCII

    // Lifecycle
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void syncterm_set_files_dir([MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_init();
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void syncterm_destroy();

    // Connection
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_connect([MarshalAs(UnmanagedType.LPUTF8Str)] string host, int port, int protocol, [MarshalAs(UnmanagedType.LPUTF8Str)] string? username, [MarshalAs(UnmanagedType.LPUTF8Str)] string? password);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void syncterm_disconnect();
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_is_connected();

    // Data I/O
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_send_data(byte[] data, int len);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_send_key(int keyCode);

    // Physical key reports (CSI = 1 h / CSI = 2 h), native/syncterm_bridge.c: SyncDoom and the other termgfx doors
    /// <summary>Bit 0: physical key reports on (CSI = 1 h). Bit 1: typed key bytes suppressed (CSI = 2 h). 0 when offline.</summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_get_key_modes();
    /// <summary>One physical key edge as a Linux evdev code (Controls/PhysicalKeyMap). Repeats and unknown releases are dropped.</summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_key_event(int evdev, int pressed);
    /// <summary>Reports every key the board still thinks is held as released (focus left the terminal).</summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void syncterm_key_release_all();
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_send_string([MarshalAs(UnmanagedType.LPUTF8Str)] string str);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_process_data();
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_data_waiting();

    // Screen Buffer
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_get_screen_buffer(int[] buffer, int bufferLen);

    /// <summary>
    /// True-colour screen fetch: same packed cells as syncterm_get_screen_buffer, plus the
    /// per-cell 24-bit foreground/background colours (0x00RRGGBB) already resolved from the
    /// palette or from a literal ESC[38;2;R;G;Bm colour. One call, so the native dirty flag is
    /// cleared exactly once per frame. All three arrays must be at least bufferLen long.
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_get_screen_buffer_rgb(int[] buffer, int[] fg, int[] bg, int bufferLen);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_get_palette(int[] palette);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_get_screen_width();
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_get_screen_height();
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_get_cursor_x();
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_get_cursor_y();
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_is_cursor_visible();
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_get_cursor_type();
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_get_cursor_start();
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_get_cursor_end();
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_get_cursor_blinks();
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_get_ciolib_fg();
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_is_screen_dirty();
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_get_dirty_region(out int minX, out int minY, out int maxX, out int maxY);

    // Terminal Control
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void syncterm_set_terminal_size(int width, int height);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_set_font([MarshalAs(UnmanagedType.LPUTF8Str)] string fontName);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_set_font_by_id(int fontId);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void syncterm_clear_screen();
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void syncterm_push_input(byte[] data, int len);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void syncterm_set_hide_status_line(int hide);
    /// <summary>SSH blank login, set before every connect: 1 = no credentials and never a prompt (ssh.c
    /// ssh_blank_login). Not in .so files built before 2026-10-01 (PORT_TO_MAC_LINUX.md §12.61).</summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void syncterm_set_ssh_blank_login(int on);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void syncterm_set_screen_mode(int mode);

    // Status & Statistics
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_get_status_info(byte[] buffer, int bufferLen);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_get_connection_stats(long[] stats);

    /// <summary>Modem speed emulation in bps; 0 = full speed (a board's own ESC[*r still applies).</summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void syncterm_set_emulated_bps(int bps);

    /// <summary>The speed incoming data is metered at: the user's setting, else the board's; 0 = full speed.</summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_get_effective_bps();
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_get_font_bitmap(byte[] buffer, int bufferLen);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_get_current_font_id();

    // Debug
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_debug_dump_screen([MarshalAs(UnmanagedType.LPUTF8Str)] string filepath, int numRows);

    // SSH Debug Log
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_get_ssh_debug_log(byte[] buffer, int bufferLen);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void syncterm_clear_ssh_debug_log();

    // ZMODEM
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_transfer_init();
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void syncterm_set_download_dir([MarshalAs(UnmanagedType.LPUTF8Str)] string dir);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_zmodem_receive();
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_zmodem_send([MarshalAs(UnmanagedType.LPUTF8Str)] string filePath);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void syncterm_upload_queue_clear();
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_upload_queue_add(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string filePath);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_upload_queue_count();
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_zmodem_send_batch();

    // Transfer > ZMODEM 8K Blocks: let uploads grow to 8K subpackets
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void syncterm_zmodem_set_8k(int enable);

    // Largest subpacket received in this transfer (downloads: the board's block size)
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint syncterm_zmodem_largest_rx_block();

    // ---------------- X/YMODEM ----------------
    //
    // Unlike ZMODEM these never auto-start: the BBS just begins NAKing, so the
    // user picks the protocol on both ends. Both calls block for the whole
    // transfer — run them off the UI thread, exactly like the ZMODEM ones.

    /// <summary>Mode bits for the X/YMODEM calls. Values mirror sbbs3/sexyz.h
    /// (and syncterm/term.h for <see cref="Xmodem128B"/>) — do not renumber.</summary>
    public static class XferMode
    {
        public const int Xmodem     = 1 << 2;
        public const int Ymodem     = 1 << 3;
        public const int Crc        = 1 << 5;   // CRC-16 instead of 8-bit checksum
        public const int GMode      = 1 << 6;   // streaming -g variant (no per-block ACK)
        public const int Overwrite  = 1 << 9;   // clobber an existing file instead of renaming
        public const int Xmodem128B = 1 << 10;  // pin 128-byte blocks (what 8-bit boards want)
    }

    /// <summary>
    /// Receive via X/YMODEM. <paramref name="savePath"/> is REQUIRED for XMODEM —
    /// the protocol carries no filename — and ignored for YMODEM, which takes the
    /// name from its header block and writes to the download directory.
    /// Returns the number of files received, or negative on error.
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_xmodem_receive(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? savePath, int mode);

    /// <summary>Send one file via X/YMODEM. Returns 0 on success, negative on error.</summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_xmodem_send(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string filePath, int mode);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_get_transfer_file_index(out int current, out int total);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void syncterm_transfer_cancel();
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_get_transfer_state();
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_get_transfer_progress(long[] progress);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_get_transfer_filename(byte[] buffer, int bufferLen);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_get_transfer_error(byte[] buffer, int bufferLen);

    /// <summary>
    /// Drain protocol status lines produced since the last call. Each line is
    /// a severity char ('I', 'W', 'E') followed by the message, separated by
    /// '\n'. Returns bytes written. Lines are handed out once, so polling
    /// repeatedly never repeats a line.
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_get_transfer_log(byte[] buffer, int bufferLen);

    /// <summary>Discard pending status lines, so a new transfer starts clean.</summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void syncterm_clear_transfer_log();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void syncterm_transfer_reset();
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void syncterm_transfer_cleanup();
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_is_zmodem_detected();
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_get_zmodem_buffer(byte[] buffer, int bufferLen);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void syncterm_clear_zmodem_detected();
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_push_zmodem_buffer();
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void syncterm_queue_upload([MarshalAs(UnmanagedType.LPUTF8Str)] string filePath);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_is_upload_queued();
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_is_upload_ready();
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_get_queued_upload(byte[] buffer, int bufferLen);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void syncterm_clear_upload_queue();

    // Bell & Audio
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_check_bell();
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_check_audio_command();
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_get_audio_command(byte[] buffer, int bufferLen);

    // Session Logging
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void syncterm_set_logging_enabled(int enabled);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_get_logged_data(byte[] buffer, int bufferLen);

    // Scrollback
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_get_scrollback_info(int[] info);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_get_scrollback_buffer(int offset, int count, int[] buffer, int bufferLen);

    /// <summary>True-colour scrollback fetch -- see syncterm_get_screen_buffer_rgb.</summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_get_scrollback_buffer_rgb(int offset, int count, int[] buffer,
                                                                int[] fg, int[] bg, int bufferLen);

    // Sixel Graphics
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_has_sixel_data();
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_is_sixel_dirty();
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_get_sixel_dimensions(out int width, out int height);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_get_sixel_buffer(int[] buffer, int bufferLen);

    /// <summary>
    /// Copies only the pixel layer's changed rectangle into a bitmap back buffer (destW x destH, destStride pixels
    /// per row); full copies everything. Returns pixels copied, 0 for nothing, -1 when the layer isn't that size
    /// (bw x bh is its size).
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_copy_sixel_pixels(IntPtr dest, int destW, int destH, int destStride, int full,
        out int x, out int y, out int w, out int h, out int bw, out int bh);

    /// <summary>Blocks up to timeoutMs until received data is waiting; returns as soon as it arrives.</summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_wait_for_data(int timeoutMs);

    /// <summary>The board's SyncTERM APC cache directory (C;S stores, audio loads, JXL frames).</summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void syncterm_set_apc_cache_dir([MarshalAs(UnmanagedType.LPUTF8Str)] string dir);

    /// <summary>The soundtrack setting for SyncTERM:A audio; while off, doors are told there is no audio.</summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void syncterm_set_apc_audio_enabled(int enabled);

    /// <summary>Pulls frames of the native SyncTERM audio mix (S16 stereo 44.1 kHz); returns the audible leading frames.</summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_apc_audio_pull(short[] buffer, int frames);

    // MOD/Tracker Audio
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_mod_load([MarshalAs(UnmanagedType.LPUTF8Str)] string filePath);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_mod_get_pcm(short[] buffer, int frames);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void syncterm_mod_stop();
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void syncterm_mod_set_volume(float volume);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_mod_is_playing();
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void syncterm_mod_restart();
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void syncterm_mod_seek(int positionMs);

    // Audio Bridge (OpenAL Soft)
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int audio_bridge_init();
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void audio_bridge_shutdown();
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int audio_bridge_play_pcm(int channel, short[] samples, int frames, int sampleRate, int numChannels);
    // Gapless push onto the channel's buffer queue; 0 = queue full, try again once some has played
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int audio_bridge_queue_pcm(int channel, short[] samples, int frames, int sampleRate, int numChannels);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int audio_bridge_play_file(int channel, [MarshalAs(UnmanagedType.LPUTF8Str)] string filepath, int loop);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void audio_bridge_stop(int channel);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void audio_bridge_stop_all();
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void audio_bridge_pause(int channel);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void audio_bridge_resume(int channel);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void audio_bridge_set_volume(int channel, float volume);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void audio_bridge_seek(int channel, int positionMs);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int audio_bridge_is_playing(int channel);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int audio_bridge_get_pcm_snapshot(int channel, short[] buffer, int maxFrames);

    // Short overlapping clips (key clicks): uploaded once, played on a small pool of sources
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int audio_bridge_load_clip(int clipId, short[] samples, int frames, int sampleRate, int numChannels);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int audio_bridge_play_clip(int clipId, float gain);

    // TRACE (TERMinator Render And Compute Engine): native/apc/trace_bridge.c
    /// <summary>What TRACE can run ("wasm=1"), for the reply to a door's TRACE;Query.</summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void syncterm_trace_set_modules([MarshalAs(UnmanagedType.LPUTF8Str)] string modules);
    /// <summary>Takes the oldest queued TRACE command into buffer: its length, 0 when none, -1 if buffer is too small.</summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_trace_poll(byte[] buffer, int bufferLen);

    // TERMinator:Pic (Rainwire's pictures): native/apc/pic_bridge.c

    /// <summary>Takes the oldest queued Pic command ("Show;id=..;rect=..;path=..", "Hide;id=..", "HideAll") into
    /// buffer: its length, 0 when none, -1 if buffer is too small.</summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_pic_poll(byte[] buffer, int bufferLen);

    // Rainwire:UI (a Rainwire board's menus drawn by the app, 1.1.6): native/apc/ui_bridge.c

    /// <summary>Takes the oldest queued Rainwire:UI message ("Verb[;b64=..][;path=..]") into buffer: its length, 0 when
    /// none, -1 if buffer is too small.</summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int syncterm_ui_poll(byte[] buffer, int bufferLen);

    // Helper methods
    public static char UnpackChar(int cell) => (char)(cell & 0xFF);
    public static int UnpackAttr(int cell) => (cell >> 8) & 0xFF;
    public static int UnpackFg(int cell) => (cell >> 16) & 0x7F;
    public static int UnpackBg(int cell) => (cell >> 24) & 0xFF;
    public static bool IsUnderline(int cell) => ((cell >> 23) & 1) == 1;
    public static int AttrToFg(int attr) => attr & 0x0F;
    public static int AttrToBg(int attr) => (attr >> 4) & 0x0F;
    public static string GetNativeString(byte[] buffer, int length)
    {
        if (length <= 0) return string.Empty;
        return System.Text.Encoding.UTF8.GetString(buffer, 0, length);
    }
}
