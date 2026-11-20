namespace TERMinator.Linux.Services;

/// <summary>
/// Changelog and credits text, shared by the Help → Changelog / About boxes and the command
/// line's "changelog" / "about" commands so the two never drift apart. Same shape as the Windows
/// client's Services/AppInfoText.cs.
/// </summary>
public static class AppInfoText
{
    /// <summary>Every version's list, newest first. Version headers start with "v"; "" separates versions.</summary>
    ///
    /// ⚠️ 1.0.1 to 1.0.5 do not exist on Linux. The client shipped once as 1.0.0 and then quietly
    /// caught up with Windows over several of its releases without ever being renumbered, so the
    /// 1.1.0 entry folds that whole catch-up (listed as 1.0.6 until 1.1.0 absorbed it) into 1.1.0,
    /// the version that brings all three desktop clients onto the same number. Do not invent the
    /// missing releases after the fact.
    public static readonly string[] Changelog =
    [
        "v1.1.6",
        "• Rainwire screen engine support",
        "• Rainwire gadget engine support",
        "• CTRL-A fix",
        "• Removed F1 key for help",
        "• TRACE performance upgrades",
        "",
        "v1.1.5",
        "• Rainwire BBS support",
        "• ZMODEM 8K optimizations",
        "• Added Check for Updates in Help menu",
        "• Rainwire multi-window support",
        "",
        "v1.1.4",
        "• Multi-window/multi-BBS support",
        "• Super ZMODEM 8K support",
        "• Ed25519 SSH key support",
        "",
        "v1.1.3",
        "• TRACE - GPU support",
        "• TRACE - gamepad support",
        "• TRACE - mouse support",
        "",
        "v1.1.2",
        "• TRACE support (TERMinator Render and Compute Engine)",
        "• Variable connection timeout",
        "",
        // Same lines as Windows 1.1.1 (all three desktop clients share the changelog); the SyncTERM door work they
        // describe was ported to Linux 2026-09-16 (TERMinator-Windows/PORT_SYNCDOOM_TO_MAC_LINUX.md §10).
        "v1.1.1",
        "• JPEG XL graphics for BBS doors",
        "• BBS door sound and music",
        "• Sixel performance improvements",
        "",
        "v1.1.0",
        "• Layout overhaul",
        "• Command line mode (Elite)",
        "• Side-by-side layout option",
        "• TelnetS fix for BBSs with misconfigured certs",
        "• Proper 4:3 aspect ratio",
        "• CRT and scanline effects",
        "• Phonebook import / export",
        "• In-app help (F1)",
        "• Save Screenshot, and the SSH debug log on demand",
        "• Terminal right-click menu",
        "• Per-connection BBS status line",
        "• Commodore PETSCII support (C64 / C128 screen modes)",
        "• XMODEM / YMODEM file transfers",
        "• Windows secure sockets support (WebSocket / Secure WebSocket)",
        "• Raw connection type",
        "• 132x60 screen modes added",
        "• True color support",
        "• Better Sixel support",
        "• ZMODEM batch uploads",
        "",
        "v1.0.0",
        "• ZMODEM fixes/improvements",
        "• Dynamic font switching",
        "• Added 5 Amiga fonts",
        "• Phonebook alphabetical sort"
    ];

    /// <summary>The credits, as runs of text; a run with a Uri is a clickable link in the About box.</summary>
    public static readonly (string Text, string? Uri)[] Credits =
    [
        ("Based on SyncTERM, we would not exist without the legendary work of\n" +
         "SyncTERM, so hats off to the SyncTERM folks!\n\n" +
         "Linux version by JSONBourne 2026\n" +
         "JSONBourne Innovations\n", null),
        ("jsonbourneinnovations@gmail.com", "mailto:jsonbourneinnovations@gmail.com"),
        ("\n", null),
        ("deadmodemsociety.com", "https://deadmodemsociety.com"),
        ("\n\n" +
         "Special thx to aNACHRONiST (aNSt) for the artwork and testing!\n" +
         "He may be reached at: ", null),
        ("aNSt@absinthebbs.net", "mailto:aNSt@absinthebbs.net"),
        (" for collaborations and commissions\n\n" +
         "This application is open source and licensed under the GNU GPL v2.\n" +
         "GitHub: ", null),
        ("github.com/omniphil/TERMinator-Linux", "https://github.com/omniphil/TERMinator-Linux")
    ];

    /// <summary>The credits as plain text lines (links shown as their text).</summary>
    public static string[] CreditsLines() => string.Concat(Credits.Select(run => run.Text)).Split('\n');
}
