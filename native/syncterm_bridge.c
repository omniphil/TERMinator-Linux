/**
 * SyncTERM Linux Bridge
 *
 * Provides plain C DLL exports to connect Avalonia/.NET with the native
 * SyncTERM terminal emulator and Telnet/SSH connection code.
 *
 * Ported from syncterm_jni.c (Android JNI bridge) with the following changes:
 * - JNI infrastructure removed (no JavaVM, JNIEnv, callbacks)
 * - JNI types replaced with plain C types
 * - pthread replaced with Windows pthread_mutex_t
 * - Android logging replaced with OutputDebugStringA
 * - gettimeofday replaced with GetSystemTimeAsFileTime
 * - android_ciolib replaced with wpf_ciolib
 */

#include <pthread.h>
#include <unistd.h>
#include <sys/time.h>
#include <time.h>
#include <string.h>
#include <stdlib.h>
#include <stdint.h>
#include <stdatomic.h>
#include <stdio.h>
#include <stdarg.h>

#include "safe_math.h"
#include "linux_ciolib.h"
#include "conn.h"
#include "conn_telnet.h"
#include "cterm.h"
#include "bbslist.h"
#include "ciolib.h"
#include "genwrap.h"
#include "sockwrap.h"
#include "rlogin.h"       /* rlogin_sock: the telnet/raw TCP socket, for TCP_NODELAY */
#include "trace_bridge.h"  /* TRACE: TERMinator Render And Compute Engine escapes (apc/trace_bridge.c) */
#include "apc_bridge.h"   /* SyncTERM APC: cache, JXL/PPM images, audio (apc/, ported from Windows 2026-09-16) */
#include "mouse_bridge.h"  /* xterm mouse reports (CSI ? 1000 h and friends), as SyncTERM sends them */

#ifndef WITHOUT_CRYPTLIB
#include "ssh.h"
#ifdef WITH_WEBSOCKET
#include "conn_websocket.h"

/* Route WebSocket diagnostics into the SSH debug log window */
extern void ssh_debug_log(const char *fmt, ...);
static void websocket_bridge_log(const char *msg) {
    ssh_debug_log("[ws] %s\n", msg);
}
#endif
#endif

/* ------------------------------------------------------------------ */
/* DLL export macro                                                    */
/* ------------------------------------------------------------------ */
#ifdef SYNCTERM_BRIDGE_EXPORTS
#define SYNCTERM_API __attribute__((visibility("default")))
#else
#define SYNCTERM_API
#endif

/* ------------------------------------------------------------------ */
/* Logging macros (OutputDebugStringA)                                 */
/* ------------------------------------------------------------------ */
#define LOGI(fmt, ...) do { char _buf[512]; snprintf(_buf, sizeof(_buf), "SyncTERM-Bridge: " fmt "\n", ##__VA_ARGS__); fprintf(stderr, "%s", _buf); } while(0)
#define LOGD(fmt, ...) LOGI(fmt, ##__VA_ARGS__)
#define LOGW(fmt, ...) do { char _buf[512]; snprintf(_buf, sizeof(_buf), "SyncTERM-Bridge WARN: " fmt "\n", ##__VA_ARGS__); fprintf(stderr, "%s", _buf); } while(0)
#define LOGE(fmt, ...) do { char _buf[512]; snprintf(_buf, sizeof(_buf), "SyncTERM-Bridge ERROR: " fmt "\n", ##__VA_ARGS__); fprintf(stderr, "%s", _buf); } while(0)

/* ------------------------------------------------------------------ */
/* TAP debug logging (forward declaration)                             */
/* ------------------------------------------------------------------ */
static char g_tap_logpath[512] = {0};
static int tap_debug_enabled(void);
static void tap_debug_log(const char *fmt, ...);

/* ------------------------------------------------------------------ */
/* Constants                                                           */
/* ------------------------------------------------------------------ */

/* Maximum scrollback buffer size to prevent excessive memory usage (100 MB) */
#define MAX_SCROLLBACK_BYTES (100 * 1024 * 1024)

/* Maximum bytes to drain from connection buffer when clearing stale data */
#define MAX_DRAIN_BYTES 4096

/* ------------------------------------------------------------------ */
/* Global state                                                        */
/* ------------------------------------------------------------------ */
static struct cterminal *g_cterm = NULL;
static struct vmem_cell *g_scrollback = NULL;
static int g_scrollback_lines = 1000;
static int g_connected = 0;
static int g_initialized = 0;
static pthread_mutex_t g_lock;

/*
 * Screen copies waiting for g_lock. The poll thread takes g_lock for every 4 KB it parses and gives it back for only
 * a moment between reads, and a mutex isn't fair: under a steady stream (a door sending full-screen colour frames)
 * the UI thread's screen copy could wait through most of a batch, so frames were parsed and answered for but few were
 * drawn. process_data steps aside while one is waiting.
 */
static atomic_int g_snapshot_waiting = 0;

/* Terminal dimensions */
int g_term_width = 80;
int g_term_height = 24;

/* Current font ID for bitmap rendering (non-static so wpf_ciolib.c can update it) */
int g_current_font_id = 0;

/* Connection settings (set before connect, applied during connect) */
static int g_hide_status_line = 1;  /* Default to hiding status line */
static int g_ssh_blank_login = 0;   /* SSH with no credentials: the board logs the caller in (ssh.c ssh_blank_login) */
static int g_screen_mode = SCREEN_MODE_80X25;  /* Default screen mode */
static int g_emulation = CTERM_EMULATION_ANSI_BBS;  /* cterm emulation, derived from screen mode */
static char g_font_name[256] = "Codepage 437 English";  /* Default font */

/* Connection config (not static — wpf_stubs.c reads credentials for uifcinput) */
struct bbslist g_bbs_config;

/* ZMODEM auto-detection */
static volatile int g_zmodem_detected = 0;      /* Download init (ZRQINIT) detected */
static volatile int g_zmodem_upload_ready = 0;   /* Upload init (ZRINIT) detected */
static unsigned char g_zmodem_buffer[4096];
static int g_zmodem_buffer_len = 0;
static pthread_mutex_t g_zmodem_lock;

/* Upload queue - file waiting to be sent when BBS is ready */
static char g_upload_queued_file[512] = {0};
static volatile int g_upload_file_queued = 0;

/* Bell detection - set when BEL character (0x07) is received */
static volatile int g_bell_detected = 0;

/* Audio command queue - receives OSC 800 commands from cterm */
#define AUDIO_CMD_BUFFER_SIZE 4096
#define AUDIO_CMD_QUEUE_SIZE 64
static char g_audio_cmd_queue[AUDIO_CMD_QUEUE_SIZE][AUDIO_CMD_BUFFER_SIZE];
static int  g_audio_cmd_lengths[AUDIO_CMD_QUEUE_SIZE];
static volatile int g_audio_queue_head = 0;   /* Next slot to write */
static volatile int g_audio_queue_tail = 0;   /* Next slot to read */
static volatile int g_audio_queue_count = 0;  /* Number of commands pending */
static pthread_mutex_t g_audio_lock;

/* Connection statistics */
static _Atomic uint64_t g_bytes_sent = 0;

/* Physical key reports (CSI = 1 h), ported from Windows native/syncterm_bridge.c (section above syncterm_send_key) */
#define PK_MAX_EVDEV 1024
static pthread_mutex_t g_pk_lock = PTHREAD_MUTEX_INITIALIZER;
static atomic_int g_key_modes = 0;                 /* bit 0: CSI = 1 h, bit 1: CSI = 2 h; mirrored after cterm_write */
static uint8_t g_pk_reported[PK_MAX_EVDEV / 8];    /* keys whose press the board was sent; guarded by g_pk_lock */
static void keyev_reset(void);
static _Atomic uint64_t g_bytes_received = 0;
static _Atomic int64_t g_connect_time_ms = 0;  /* Timestamp when connected */

/* Modem speed emulation, in bps (0 = full speed). The user's setting wins; without one, a board can set its own
   speed with CSI Ps1 ; Ps2 * r. Incoming data is metered with a token bucket in syncterm_process_data. */
static _Atomic int g_emulated_bps = 0;
static _Atomic int g_host_bps = 0;
static double g_bps_credit = 0;    /* characters the emulated line may still deliver; guarded by g_lock */
static int64_t g_bps_last_ms = 0;  /* when the credit was last topped up; guarded by g_lock */

/* Look-ahead for terminal queries in data still queued behind the speed meter (see find_pending_query_end) */
static unsigned char g_query_peek[32768];              /* guarded by g_lock */
static size_t g_query_scan_waiting = (size_t)-1;       /* queue length at the last scan that found nothing */

/* Session logging */
#define LOG_BUFFER_SIZE (64 * 1024)  /* 64KB circular buffer for logging */
static unsigned char g_log_buffer[LOG_BUFFER_SIZE];
static volatile int g_log_buffer_len = 0;
static volatile int g_logging_enabled = 0;
static pthread_mutex_t g_log_lock;

/* Track whether critical sections have been initialized */
static int g_locks_initialized = 0;

/* ------------------------------------------------------------------ */
/* Time helper                                                         */
/* ------------------------------------------------------------------ */
static int64_t get_current_time_ms(void) {
    struct timespec ts;
    clock_gettime(CLOCK_REALTIME, &ts);
    return (int64_t)ts.tv_sec * 1000 + ts.tv_nsec / 1000000;
}

/* ------------------------------------------------------------------ */
/* ZMODEM frame types                                                  */
/* ------------------------------------------------------------------ */
#define ZMODEM_FRAME_ZRQINIT  0x00  /* Request receive init (sender -> receiver, download) */
#define ZMODEM_FRAME_ZRINIT   0x01  /* Receive init (receiver -> sender, upload ready) */

/* Structure to hold ZMODEM detection result */
typedef struct {
    int position;    /* Start position of header (-1 if not found) */
    int frame_type;  /* Frame type (ZRQINIT, ZRINIT, etc.) or -1 */
} zmodem_detect_result_t;

/* ------------------------------------------------------------------ */
/* Check if buffer contains ZMODEM init sequence and identify frame    */
/* type.                                                               */
/* ZMODEM headers start with: ZPAD ZPAD ZDLE <type>                    */
/* where ZPAD = '*' (0x2a), ZDLE = CAN (0x18)                         */
/* and <type> is 'A' (ZBIN), 'B' (ZHEX), or 'C' (ZBIN32)             */
/* We detect ZDLE + type, then back up to include preceding ZPAD bytes */
/* ------------------------------------------------------------------ */
static zmodem_detect_result_t detect_zmodem_ex(const unsigned char *buf, int len) {
    zmodem_detect_result_t result = { -1, -1 };

    /* Need at least 2 bytes for ZDLE + type detection (prevents len-1 underflow) */
    if (len < 2) {
        return result;
    }

    for (int i = 0; i < len - 1; i++) {
        /* Look for ZDLE (CAN/0x18) byte followed by header type */
        if (buf[i] == 0x18 && (buf[i+1] == 'A' || buf[i+1] == 'B' || buf[i+1] == 'C')) {
            LOGI("ZMODEM header detected: ZDLE(0x18) + '%c'(0x%02x) at position %d, buflen=%d",
                 buf[i+1], buf[i+1], i, len);

            /* Back up to include preceding ZPAD ('*' = 0x2a) bytes */
            /* ZMODEM headers start with one or two ZPAD bytes before ZDLE */
            /* This is CRITICAL - zmodem_recv_header_raw() expects ZPAD first! */
            int start = i;
            while (start > 0 && buf[start - 1] == 0x2a) {  /* 0x2a = '*' = ZPAD */
                start--;
            }

            if (start < i) {
                LOGI("  Backed up to include %d ZPAD byte(s), new start position: %d", i - start, start);
            }

            /* Extract frame type based on header encoding */
            int frame_type = -1;
            if (buf[i+1] == 'B' && i + 3 < len) {
                /* ZHEX: frame type is two hex digits after 'B' */
                char hex[3] = { (char)buf[i+2], (char)buf[i+3], 0 };
                frame_type = (int)strtol(hex, NULL, 16);
                LOGI("  ZHEX frame type: 0x%02x (from '%s')", frame_type, hex);
            } else if ((buf[i+1] == 'A' || buf[i+1] == 'C') && i + 2 < len) {
                /* ZBIN/ZBIN32: frame type is raw byte after 'A' or 'C' */
                frame_type = buf[i+2];
                LOGI("  ZBIN frame type: 0x%02x", frame_type);
            }

            /* Log a few more bytes for debugging */
            if (i + 7 < len) {
                LOGI("  Following bytes: %02x %02x %02x %02x %02x %02x",
                     buf[i+2], buf[i+3], buf[i+4], buf[i+5], buf[i+6], buf[i+7]);
            }

            result.position = start;
            result.frame_type = frame_type;
            return result;
        }
    }
    return result;  /* Not found */
}

/* Legacy function for backward compatibility */
static int detect_zmodem(const unsigned char *buf, int len) {
    return detect_zmodem_ex(buf, len).position;
}

/* ------------------------------------------------------------------ */
/* Initialize all critical sections                                    */
/* ------------------------------------------------------------------ */
static void syncterm_bridge_init_locks(void) {
    if (!g_locks_initialized) {
        pthread_mutex_init(&g_lock, NULL);
        pthread_mutex_init(&g_zmodem_lock, NULL);
        pthread_mutex_init(&g_audio_lock, NULL);
        pthread_mutex_init(&g_log_lock, NULL);
        g_locks_initialized = 1;
    }
}

/* ------------------------------------------------------------------ */
/* External function from wpf_stubs.c                                  */
/* ------------------------------------------------------------------ */
extern void linux_set_files_dir(const char *path);

/* ------------------------------------------------------------------ */
/* Lifecycle functions                                                  */
/* ------------------------------------------------------------------ */

/* Set the files directory for SSH keys and config files */
SYNCTERM_API void syncterm_set_files_dir(const char *path) {
    if (!path) {
        LOGE("Failed to get files directory path string");
        return;
    }
    linux_set_files_dir(path);
}

/* Initialize the terminal system */
SYNCTERM_API int syncterm_init(void) {
    LOGI("syncterm_init called");
    tap_debug_log("=== syncterm_init: TAP debug logging active (logpath=%s) ===", g_tap_logpath);

    /* Initialize critical sections first */
    syncterm_bridge_init_locks();

    pthread_mutex_lock(&g_lock);

    if (g_initialized) {
        LOGI("Already initialized");
        pthread_mutex_unlock(&g_lock);
        return 1;
    }

#ifndef WITHOUT_CRYPTLIB
    /* Initialize cryptlib for SSH support - MUST be done before ciolib init */
    LOGI("Initializing cryptlib for SSH support");
    init_crypt();
#endif

    /* Initialize ciolib */
    if (initciolib(CIOLIB_MODE_AUTO) != 0) {
        LOGE("Failed to initialize ciolib");
        pthread_mutex_unlock(&g_lock);
        return 0;
    }

    /* Allocate scrollback buffer with overflow check */
    int scrollback_cols = g_term_width > 132 ? g_term_width : 132;
    size_t scrollback_alloc_size;
    if (!validate_alloc_size(g_scrollback_lines, scrollback_cols,
                             sizeof(struct vmem_cell), &scrollback_alloc_size)) {
        LOGE("Scrollback buffer size overflow: %d x %d", g_scrollback_lines, scrollback_cols);
        pthread_mutex_unlock(&g_lock);
        return 0;
    }
    /* Ensure allocation doesn't exceed maximum allowed size */
    if (scrollback_alloc_size > MAX_SCROLLBACK_BYTES) {
        LOGE("Scrollback buffer too large: %zu bytes (max: %d)", scrollback_alloc_size, MAX_SCROLLBACK_BYTES);
        pthread_mutex_unlock(&g_lock);
        return 0;
    }
    g_scrollback = calloc(1, scrollback_alloc_size);
    if (!g_scrollback) {
        LOGE("Failed to allocate scrollback buffer");
        pthread_mutex_unlock(&g_lock);
        return 0;
    }

    /* Initialize terminal emulator */
    g_cterm = cterm_init(g_term_height, g_term_width, 1, 1,
                         g_scrollback_lines, scrollback_cols,
                         g_scrollback, g_emulation);
    if (!g_cterm) {
        LOGE("Failed to initialize cterm");
        free(g_scrollback);
        g_scrollback = NULL;
        pthread_mutex_unlock(&g_lock);
        return 0;
    }

    /* Start terminal */
    cterm_start(g_cterm);

    g_initialized = 1;
    LOGI("Terminal initialized: %dx%d", g_term_width, g_term_height);

    pthread_mutex_unlock(&g_lock);
    return 1;
}

/* Cleanup and destroy */
SYNCTERM_API void syncterm_destroy(void) {
    LOGI("syncterm_destroy called");

    pthread_mutex_lock(&g_lock);

    /* Disconnect if connected */
    if (g_connected) {
        conn_close();
        g_connected = 0;
    }

    /* Cleanup cterm */
    if (g_cterm) {
        cterm_end(g_cterm, 0);
        g_cterm = NULL;
    }
    linux_apc_session_reset();
    trace_session_reset();
    keyev_reset();

    /* Free scrollback */
    if (g_scrollback) {
        free(g_scrollback);
        g_scrollback = NULL;
    }

    /* Cleanup ciolib */
    linux_ciolib_cleanup();

    /* Reset font state to defaults so next connection starts clean */
    g_current_font_id = 0;
    strcpy(g_font_name, "Codepage 437 English");

    /* Reset connection statistics */
    atomic_store(&g_bytes_sent, 0);
    atomic_store(&g_bytes_received, 0);
    atomic_store(&g_connect_time_ms, 0);

    /* Reset bell detection */
    g_bell_detected = 0;

    /* Reset ZMODEM state */
    pthread_mutex_lock(&g_zmodem_lock);
    g_zmodem_detected = 0;
    g_zmodem_upload_ready = 0;
    g_zmodem_buffer_len = 0;
    pthread_mutex_unlock(&g_zmodem_lock);

    /* Reset upload queue */
    g_upload_file_queued = 0;
    g_upload_queued_file[0] = '\0';

    /* Reset audio command queue */
    pthread_mutex_lock(&g_audio_lock);
    g_audio_queue_head = 0;
    g_audio_queue_tail = 0;
    g_audio_queue_count = 0;
    pthread_mutex_unlock(&g_audio_lock);

    /* Reset log buffer */
    pthread_mutex_lock(&g_log_lock);
    g_log_buffer_len = 0;
    g_logging_enabled = 0;
    pthread_mutex_unlock(&g_log_lock);

    /* Reset terminal dimensions to defaults */
    g_term_width = 80;
    g_term_height = 24;

    g_initialized = 0;

    pthread_mutex_unlock(&g_lock);

    LOGI("Cleanup complete");
}

/* ------------------------------------------------------------------ */
/* Connection functions                                                */
/* ------------------------------------------------------------------ */

/* Connect to a server (Telnet or SSH) */
/* protocol: CONN_TYPE_TELNET (3) or CONN_TYPE_SSH (5) */
SYNCTERM_API int syncterm_connect(const char *host, int port, int protocol,
                                  const char *username, const char *password) {
    LOGI("syncterm_connect called (protocol=%d)", protocol);

    if (!g_initialized) {
        LOGE("Not initialized");
        return 0;
    }

    if (g_connected) {
        LOGE("Already connected");
        return 0;
    }

    if (!host) {
        LOGE("Host string is NULL");
        return 0;
    }

    pthread_mutex_lock(&g_lock);

    /* Set up connection config */
    memset(&g_bbs_config, 0, sizeof(g_bbs_config));
    strncpy(g_bbs_config.name, "Linux Connection", sizeof(g_bbs_config.name) - 1);
    g_bbs_config.name[sizeof(g_bbs_config.name) - 1] = '\0';
    strncpy(g_bbs_config.addr, host, sizeof(g_bbs_config.addr) - 1);
    g_bbs_config.addr[sizeof(g_bbs_config.addr) - 1] = '\0';

    /* Validate port number to prevent truncation */
    if (!validate_port(port, &g_bbs_config.port)) {
        LOGE("Invalid port number: %d (must be 1-65535)", port);
        pthread_mutex_unlock(&g_lock);
        return 0;
    }

    /* Set connection type based on protocol parameter */
    /* CONN_TYPE_TELNET = 3, CONN_TYPE_RAW = 4, CONN_TYPE_SSH = 5, CONN_TYPE_TELNETS = 12,
       CONN_TYPE_WEBSOCKET = 14, CONN_TYPE_WEBSOCKETS = 15 (from conn.h) */
    if (protocol == 5) {  /* SSH */
#ifndef WITHOUT_CRYPTLIB
        g_bbs_config.type = CONN_TYPE_SSH;
        g_bbs_config.conn_type = CONN_TYPE_SSH;
        LOGI("SSH connection requested");

        /* Copy username for SSH */
        if (username && username[0] != '\0') {
            strncpy(g_bbs_config.user, username, sizeof(g_bbs_config.user) - 1);
            g_bbs_config.user[sizeof(g_bbs_config.user) - 1] = '\0';
            LOGI("SSH username set: %s", g_bbs_config.user);
        }

        /* Copy password for SSH */
        if (password && password[0] != '\0') {
            strncpy(g_bbs_config.password, password, sizeof(g_bbs_config.password) - 1);
            g_bbs_config.password[sizeof(g_bbs_config.password) - 1] = '\0';
            LOGI("SSH password set (length=%zu)", strlen(password));
        }

        /* Blank login: no prompts; ssh.c logs in as "bbs" with a password Rainwire ignores */
        ssh_blank_login = g_ssh_blank_login;
        ssh_offer_key = 0;  /* never the client key: the entry's own account logs in (ssh.c ssh_offer_key) */
        if (ssh_blank_login)
            LOGI("SSH blank login");
#else
        LOGE("SSH requested but cryptlib not available - falling back to Telnet");
        g_bbs_config.type = CONN_TYPE_TELNET;
        g_bbs_config.conn_type = CONN_TYPE_TELNET;
#endif
    } else if (protocol == 12) {  /* TelnetS (Telnet over SSL) */
#ifndef WITHOUT_CRYPTLIB
        g_bbs_config.type = CONN_TYPE_TELNETS;
        g_bbs_config.conn_type = CONN_TYPE_TELNETS;
        LOGI("TelnetS connection requested");
#else
        LOGE("TelnetS requested but cryptlib not available - falling back to Telnet");
        g_bbs_config.type = CONN_TYPE_TELNET;
        g_bbs_config.conn_type = CONN_TYPE_TELNET;
#endif
    } else if (protocol == CONN_TYPE_RAW) {
        /* Raw: a plain 8-bit socket, NO telnet negotiation and no IAC handling.
         *
         * Not every board on port 23 actually speaks telnet. Against one that does not,
         * our telnet layer eats any 0xFF in the stream as an IAC command. ZMODEM shrugs
         * that off because it escapes IAC itself before the bytes reach the wire, but
         * X/YMODEM blocks ARE raw file bytes, so a download CRCs, retries and dies while
         * ZMODEM on the same board is perfectly happy. This is the escape hatch for that,
         * and it is why IcyTerm exposes a connection-type picker too. */
        g_bbs_config.type = CONN_TYPE_RAW;
        g_bbs_config.conn_type = CONN_TYPE_RAW;
        LOGI("Raw (no telnet negotiation) connection requested");
    } else if (protocol == CONN_TYPE_WEBSOCKET || protocol == CONN_TYPE_WEBSOCKETS) {
        /* WebSocket / Secure WebSocket (RFC 6455 carrying a telnet stream) */
#ifdef WITH_WEBSOCKET
        g_bbs_config.type = protocol;
        g_bbs_config.conn_type = protocol;
        websocket_log_cb = websocket_bridge_log;
        LOGI("%s connection requested", protocol == CONN_TYPE_WEBSOCKETS ? "Secure WebSocket" : "WebSocket");
#else
        LOGE("WebSocket requested but not built in - falling back to Telnet");
        g_bbs_config.type = CONN_TYPE_TELNET;
        g_bbs_config.conn_type = CONN_TYPE_TELNET;
#endif
    } else {
        g_bbs_config.type = CONN_TYPE_TELNET;
        g_bbs_config.conn_type = CONN_TYPE_TELNET;
    }
    g_bbs_config.screen_mode = g_screen_mode;  /* Apply screen mode setting */
    g_bbs_config.hidepopups = 1;  /* No UI popups in bridge mode */
    g_bbs_config.address_family = ADDRESS_FAMILY_UNSPEC;
    g_bbs_config.music = 0;
    g_bbs_config.nostatus = g_hide_status_line;  /* Apply hide status line setting */
    strncpy(g_bbs_config.font, g_font_name, sizeof(g_bbs_config.font) - 1);  /* Apply font setting */
    g_bbs_config.font[sizeof(g_bbs_config.font) - 1] = '\0';

    /* Initialize palette with DOS colors */
    g_bbs_config.palette[0]  = 0x000000;  /* Black */
    g_bbs_config.palette[1]  = 0x0000AA;  /* Blue */
    g_bbs_config.palette[2]  = 0x00AA00;  /* Green */
    g_bbs_config.palette[3]  = 0x00AAAA;  /* Cyan */
    g_bbs_config.palette[4]  = 0xAA0000;  /* Red */
    g_bbs_config.palette[5]  = 0xAA00AA;  /* Magenta */
    g_bbs_config.palette[6]  = 0xAA5500;  /* Brown */
    g_bbs_config.palette[7]  = 0xAAAAAA;  /* Light Gray */
    g_bbs_config.palette[8]  = 0x555555;  /* Dark Gray */
    g_bbs_config.palette[9]  = 0x5555FF;  /* Light Blue */
    g_bbs_config.palette[10] = 0x55FF55;  /* Light Green */
    g_bbs_config.palette[11] = 0x55FFFF;  /* Light Cyan */
    g_bbs_config.palette[12] = 0xFF5555;  /* Light Red */
    g_bbs_config.palette[13] = 0xFF55FF;  /* Light Magenta */
    g_bbs_config.palette[14] = 0xFFFF55;  /* Yellow */
    g_bbs_config.palette[15] = 0xFFFFFF;  /* White */

    LOGI("Connecting to %s:%d (type=%d)", host, port, g_bbs_config.conn_type);

    /* Clear and start SSH debug log */
    extern void ssh_debug_clear_log(void);
    extern void ssh_debug_log(const char *fmt, ...);
    ssh_debug_clear_log();
    ssh_debug_log("=== SSH Connection Attempt ===\n");
    ssh_debug_log("Host: %s  Port: %d  Protocol: %d (%s)\n",
        host, port, g_bbs_config.conn_type,
        g_bbs_config.conn_type == CONN_TYPE_SSH ? "SSH" :
        g_bbs_config.conn_type == CONN_TYPE_TELNETS ? "TelnetS" :
        g_bbs_config.conn_type == CONN_TYPE_WEBSOCKET ? "WebSocket" :
        g_bbs_config.conn_type == CONN_TYPE_WEBSOCKETS ? "Secure WebSocket" :
        g_bbs_config.conn_type == CONN_TYPE_RAW ? "Raw" : "Telnet");
    ssh_debug_log("Username: '%s'  Password: %s  Screen: %dx%d\n",
        username ? username : "(none)",
        (password && password[0]) ? "provided" : "none",
        g_term_width, g_term_height);

    /* Attempt connection */
    /* NOTE: conn_connect() returns conn_api.terminate which is: */
    /*   - true (non-zero) on FAILURE (connection was terminated) */
    /*   - false (0) on SUCCESS (connection is active) */

    /* Release g_lock before the blocking conn_connect() call so that
       syncterm_disconnect() can run from another thread to cancel it. */
    pthread_mutex_unlock(&g_lock);

    bool terminated = conn_connect(&g_bbs_config);

    pthread_mutex_lock(&g_lock);

    if (!terminated) {
        g_connected = 1;
        /* Reset connection stats */
        g_bytes_sent = 0;
        g_bytes_received = 0;
        g_connect_time_ms = get_current_time_ms();
        g_host_bps = 0;
        g_bps_credit = 0;
        g_bps_last_ms = 0;
        g_query_scan_waiting = (size_t)-1;

        /* Plain telnet/raw sockets come out of conn_socket_connect() with Nagle on, so the OS holds back each small
         * write until the previous one is ACKed: key reports and the cursor-position replies SyncDOOM paces its frames
         * on. SSH, TelnetS and WebSocket already turn it off. Same as Windows (1.1.1); it lives in the bridge, not the
         * shared conn.c. Telnet and raw (raw_connect is rlogin_connect) both keep their socket in rlogin_sock. */
        if (g_bbs_config.conn_type == CONN_TYPE_TELNET || g_bbs_config.conn_type == CONN_TYPE_RAW) {
            int on = 1;
            if (setsockopt(rlogin_sock, IPPROTO_TCP, TCP_NODELAY, (char *)&on, sizeof(on)) != 0)
                LOGW("setsockopt(TCP_NODELAY) failed: %s", strerror(errno));
        }

        LOGI("Connected successfully");
        ssh_debug_log("\n=== Connection SUCCEEDED ===\n");
    } else {
        LOGE("Connection failed (terminated=%d)", terminated);
        ssh_debug_log("\n=== Connection FAILED ===\n");
    }

    pthread_mutex_unlock(&g_lock);
    return !terminated ? 1 : 0;
}

/* Disconnect from server */
SYNCTERM_API void syncterm_disconnect(void) {
    LOGI("syncterm_disconnect called");

    extern struct conn_api conn_api;
    conn_api.terminate = true;

    pthread_mutex_lock(&g_lock);

    if (g_connected) {
        conn_close();
        g_connected = 0;
        LOGI("Disconnected");
        linux_apc_session_reset();
        trace_session_reset();
        keyev_reset();
        mouse_bridge_reset();
    }

    pthread_mutex_unlock(&g_lock);
}

/* Blocks up to timeout_ms until received data is waiting and returns as soon as it is, so the poll loop answers a
 * door that paces frames on our cursor-position replies (SyncDoom) the moment the frame lands instead of after a
 * fixed sleep. Checks every 1 ms under g_lock: conn_close() tears conn_inbuf down under the same lock, so a
 * disconnect can't free it mid-wait. Windows syncterm_wait_for_data, 2026-09-15 (no timer-resolution call needed
 * here). Returns the bytes waiting, or 0 on timeout or when not connected. */
static int64_t monotonic_ms(void) {
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (int64_t)ts.tv_sec * 1000 + ts.tv_nsec / 1000000;
}

SYNCTERM_API int syncterm_wait_for_data(int timeout_ms) {
    int64_t start = monotonic_ms();
    int64_t span = timeout_ms > 0 ? timeout_ms : 0;

    for (;;) {
        size_t waiting = 0;
        int connected;

        pthread_mutex_lock(&g_lock);
        connected = g_connected;
        if (connected)
            waiting = conn_data_waiting();
        pthread_mutex_unlock(&g_lock);

        if (!connected)
            return 0;
        if (waiting > 0)
            return waiting > INT32_MAX ? INT32_MAX : (int)waiting;
        if (monotonic_ms() - start >= span)
            return 0;
        usleep(1000);
    }
}

/* Check if connected */
SYNCTERM_API int syncterm_is_connected(void) {
    pthread_mutex_lock(&g_lock);
    int result = (g_connected && conn_connected()) ? 1 : 0;
    pthread_mutex_unlock(&g_lock);
    return result;
}

/* ------------------------------------------------------------------ */
/* Data I/O functions                                                  */
/* ------------------------------------------------------------------ */

/* Send data to the remote server */
SYNCTERM_API int syncterm_send_data(const uint8_t *data, int len) {
    if (!g_connected) {
        return -1;
    }

    if (!data || len <= 0) {
        return 0;
    }

    /* Validate len is within reasonable bounds for conn_send */
    if (len > INT_MAX) {
        LOGE("Data length too large: %d", len);
        return -1;
    }

    int sent = conn_send(data, (size_t)len, 1000);

    /* Track bytes sent */
    if (sent > 0) {
        g_bytes_sent += sent;
    }

    return sent;
}

/* ------------------------------------------------------------------ */
/* Physical key reports (CSI = 1 h / CSI = 2 h)                        */
/* ------------------------------------------------------------------ */
/* Ported from Windows native/syncterm_bridge.c (the master), itself from upstream SyncTERM (sbbs ee025717 "Add
 * physical key event reporting": cterm.c cterm_pk_*, key.c).  A door sends CSI = 1 h to get CSI = Pk K on every
 * physical key press and CSI = Pk k on every release (Pk = Linux evdev code), and CSI = 2 h to stop the normal typed
 * bytes.  Doors built on termgfx (SyncDoom, SyncDuke, ...) turn it on when our CSI < c reply lists capability 8,
 * which cterm adds once key_event_mode_change is set.  Without it they only see key presses and fake a hold with an
 * auto-release timer, which is what made movement stutter and multi-key input (run + strafe + fire) unreliable.
 *
 * cterm keeps the mode flags; g_key_modes mirrors them after every cterm_write so the UI thread can read them
 * without g_lock (held for the whole of a large frame).  The Avalonia side (TerminalCanvas) maps PhysicalKey to
 * evdev itself, feeds syncterm_key_event and drops its own typed bytes while bit 1 is set. */

static void keyev_clear_reported(void) {
    pthread_mutex_lock(&g_pk_lock);
    memset(g_pk_reported, 0, sizeof(g_pk_reported));
    pthread_mutex_unlock(&g_pk_lock);
}

/* CSI = 1 h / l, called from inside cterm_write.  Either way the remembered key state starts clean: upstream clears
 * it on disable, and the settle window in termgfx expects no stale presses on enable. */
static void keyev_mode_change(int enable, void *cbdata) {
    (void)enable;
    (void)cbdata;
    keyev_clear_reported();
}

static void keyev_attach(struct cterminal *cterm) {
    if (cterm && cterm->key_event_mode_change != keyev_mode_change) {
        cterm->key_event_mode_change = keyev_mode_change;
        cterm->key_event_mode_change_cbdata = NULL;
    }
}

static void keyev_sync_modes(struct cterminal *cterm) {
    int modes = 0;
    if (cterm) {
        if (cterm->pk_mode)
            modes |= 1;
        if (cterm->suppress_translated_keys)
            modes |= 2;
    }
    atomic_store(&g_key_modes, modes);
}

static void keyev_reset(void) {
    atomic_store(&g_key_modes, 0);
    keyev_clear_reported();
}

/* Sends CSI = Pk[;Pk...] K|k for the listed keys; caller holds g_pk_lock so reports go out in edge order */
static void keyev_emit_locked(const uint16_t *keys, size_t count, int pressed) {
    char buf[1024];
    size_t pos;

    if (count == 0 || !g_connected)
        return;
    pos = (size_t)snprintf(buf, sizeof(buf), "\x1b[=");
    for (size_t i = 0; i < count; i++) {
        int n = snprintf(buf + pos, sizeof(buf) - pos, "%s%u", i == 0 ? "" : ";", keys[i]);
        if (n < 0 || (size_t)n >= sizeof(buf) - pos)
            return;
        pos += (size_t)n;
    }
    if (pos + 1 >= sizeof(buf))
        return;
    buf[pos++] = pressed ? 'K' : 'k';
    if (conn_send(buf, pos, 1000) > 0)
        g_bytes_sent += pos;
}

/* Current key modes: bit 0 = physical key reports on, bit 1 = typed bytes suppressed.  0 when not connected. */
SYNCTERM_API int syncterm_get_key_modes(void) {
    return g_connected ? atomic_load(&g_key_modes) : 0;
}

/* One physical key edge, already mapped to its Linux evdev code by the app (pressed=1 down, 0 up).  Auto-repeat
 * downs and releases of keys the board never saw pressed are dropped.  Returns 1 when a report was sent. */
SYNCTERM_API int syncterm_key_event(int evdev, int pressed) {
    uint16_t code;
    int was;

    if (!g_connected || !(atomic_load(&g_key_modes) & 1))
        return 0;
    if (evdev <= 0 || evdev >= PK_MAX_EVDEV)
        return 0;
    code = (uint16_t)evdev;

    pthread_mutex_lock(&g_pk_lock);
    was = (g_pk_reported[code / 8] & (1 << (code % 8))) != 0;
    if (was == (pressed != 0)) {
        pthread_mutex_unlock(&g_pk_lock);
        return 0;
    }
    if (pressed)
        g_pk_reported[code / 8] |= (uint8_t)(1 << (code % 8));
    else
        g_pk_reported[code / 8] &= (uint8_t)~(1 << (code % 8));
    keyev_emit_locked(&code, 1, pressed);
    pthread_mutex_unlock(&g_pk_lock);
    return 1;
}

/* Focus left the terminal: report every key the board still thinks is held as released, in one sequence
 * (upstream's ciokey_focus_lost + cterm_pk_resync).  Otherwise an Alt-Tab mid-game leaves the player running. */
SYNCTERM_API void syncterm_key_release_all(void) {
    uint16_t batch[64];
    size_t count = 0;

    pthread_mutex_lock(&g_pk_lock);
    for (uint16_t evdev = 0; evdev < PK_MAX_EVDEV; evdev++) {
        if (!(g_pk_reported[evdev / 8] & (1 << (evdev % 8))))
            continue;
        g_pk_reported[evdev / 8] &= (uint8_t)~(1 << (evdev % 8));
        batch[count++] = evdev;
        if (count == sizeof(batch) / sizeof(batch[0])) {
            if (atomic_load(&g_key_modes) & 1)
                keyev_emit_locked(batch, count, 0);
            count = 0;
        }
    }
    if (count > 0 && (atomic_load(&g_key_modes) & 1))
        keyev_emit_locked(batch, count, 0);
    pthread_mutex_unlock(&g_pk_lock);
}

/* Send a single key code */
SYNCTERM_API int syncterm_send_key(int keyCode) {
    if (!g_connected) {
        return -1;
    }

    unsigned char c = (unsigned char)keyCode;
    int sent = conn_send(&c, 1, 1000);
    if (sent > 0) {
        g_bytes_sent += sent;
    }
    return sent;
}

/* Send a string */
SYNCTERM_API int syncterm_send_string(const char *str) {
    if (!g_connected) {
        return -1;
    }

    if (!str) {
        return -1;
    }

    /* Validate string length to prevent size_t to int overflow */
    size_t str_len = strlen(str);
    if (str_len > INT_MAX) {
        LOGE("String too long: %zu", str_len);
        return -1;
    }
    int len = (int)str_len;
    int sent = conn_send(str, (size_t)len, 1000);
    if (sent > 0) {
        g_bytes_sent += sent;
    }

    return sent;
}

/* Speed emulation vs. terminal detection. Boards detect ANSI by sending a query (CSI 6 n, CSI c ...) and waiting a
   second or two for the reply. On a real line, everything before the query had already reached the caller when the
   board sent it. Here the network delivers it all at once and the meter holds it back, so a query queued behind a
   screenful of text was answered seconds late and boards (Mystic, for one) fell back to no graphics. So when a
   complete query is waiting in the queue, everything up to and including it is delivered at once: the reply goes out
   on time, and after whatever came before it (a CPR still reports the right cursor position).

   Scans the queued raw bytes (for telnet these still carry IAC doubling, which never touches an escape sequence) and
   returns how many to deliver so every complete query in the look-ahead window is answered, or 0 for none.
   Caller holds g_lock. */
static size_t find_pending_query_end(void) {
    pthread_mutex_lock(&conn_inbuf.mutex);
    size_t waiting = conn_buf_bytes(&conn_inbuf);
    if (waiting == g_query_scan_waiting) {
        /* Nothing new since a scan that found no query */
        pthread_mutex_unlock(&conn_inbuf.mutex);
        return 0;
    }
    size_t n = conn_buf_peek(&conn_inbuf, g_query_peek, sizeof(g_query_peek));
    pthread_mutex_unlock(&conn_inbuf.mutex);

    size_t end = 0;
    for (size_t i = 0; i < n; i++) {
        unsigned char c = g_query_peek[i];
        if (c == 0x05) {
            end = i + 1;  /* ENQ: answerback */
            continue;
        }
        if (c != 0x1b || i + 1 >= n) {
            continue;
        }
        unsigned char kind = g_query_peek[i + 1];
        if (kind == '[') {
            /* CSI: parameters, intermediates, final byte */
            size_t j = i + 2;
            bool dollar = false;
            while (j < n && g_query_peek[j] >= 0x30 && g_query_peek[j] <= 0x3f) {
                j++;
            }
            while (j < n && g_query_peek[j] >= 0x20 && g_query_peek[j] <= 0x2f) {
                if (g_query_peek[j] == '$') {
                    dollar = true;
                }
                j++;
            }
            if (j >= n) {
                break;  /* the rest of it hasn't arrived yet */
            }
            unsigned char final = g_query_peek[j];
            /* n: DSR / CPR (incl. SyncTERM's CSI = n)   c: device attributes   t: window reports   $p: DECRQM */
            if (final == 'n' || final == 'c' || final == 't' || (final == 'p' && dollar)) {
                end = j + 1;
            }
            i = j;
        } else if (kind == '_' || kind == ']' || kind == 'P') {
            /* APC / OSC / DCS strings (SyncTERM and xterm queries live here), ended by ST or BEL */
            size_t j = i + 2;
            while (j < n && g_query_peek[j] != 0x07
                   && !(g_query_peek[j] == 0x1b && j + 1 < n && g_query_peek[j + 1] == '\\')) {
                j++;
            }
            if (j >= n) {
                break;
            }
            end = g_query_peek[j] == 0x07 ? j + 1 : j + 2;
            i = end - 1;
        }
    }

    if (end == 0) {
        g_query_scan_waiting = waiting;
    }
    return end;
}

/* What cterm answered while the received text went through it (terminal queries), and any speed the board set. */
struct terminal_feed {
    char   ret[1024];
    size_t ret_len;
    int    speed;
};

/* trace_stream_feed's text callback: a run of ordinary received bytes, through the terminal emulator. */
static void feed_terminal(const char *text, size_t len, void *ctx)
{
    struct terminal_feed *feed = (struct terminal_feed *)ctx;
    char retbuf[256];
    int speed = -1;
    size_t ret_len;

    for (size_t i = 0; i < len; i++) {
        if (text[i] == 0x07) {
            g_bell_detected = 1;   /* (a TRACE binary frame never gets here, so its bytes can't ring the bell) */
            break;
        }
    }
    memset(retbuf, 0, sizeof(retbuf));
    ret_len = cterm_write(g_cterm, text, len, retbuf, sizeof(retbuf), &speed);
    if (speed >= 0)
        feed->speed = speed;
    if (ret_len > 0) {
        if (feed->ret_len + ret_len > sizeof(feed->ret)) {
            /* A run of queries in one read: send what we have and carry on, rather than lose the rest */
            if (feed->ret_len > 0 && conn_send(feed->ret, feed->ret_len, 1000) > 0)
                g_bytes_sent += feed->ret_len;
            feed->ret_len = 0;
        }
        if (ret_len <= sizeof(feed->ret)) {
            memcpy(feed->ret + feed->ret_len, retbuf, ret_len);
            feed->ret_len += ret_len;
        }
    }
}

/* Process incoming data and update terminal */
/* Returns: bytes processed, or -100 if ZMODEM download detected, -101 if upload ready */
SYNCTERM_API int syncterm_process_data(void) {
    /* A screen copy waiting for the lock goes first (bounded, so a stuck one can't stop the connection) */
    if (atomic_load(&g_snapshot_waiting) > 0) {
        for (int i = 0; i < 200 && atomic_load(&g_snapshot_waiting) > 0; i++)
            usleep(100);
    }
    pthread_mutex_lock(&g_lock);

    if (!g_connected || !g_cterm) {
        pthread_mutex_unlock(&g_lock);
        return 0;
    }

    /* If ZMODEM already detected, don't consume more data */
    pthread_mutex_lock(&g_zmodem_lock);
    if (g_zmodem_detected) {
        pthread_mutex_unlock(&g_zmodem_lock);
        pthread_mutex_unlock(&g_lock);
        return -100;  /* Signal ZMODEM detected */
    }
    pthread_mutex_unlock(&g_zmodem_lock);

    /* Check for data waiting */
    size_t waiting = conn_data_waiting();
    if (waiting == 0) {
        pthread_mutex_unlock(&g_lock);
        return 0;
    }

    /* Read up to 4KB at a time */
    unsigned char buffer[4096];
    size_t max_read = sizeof(buffer);

    /* Modem speed emulation: deliver only the characters the emulated line has carried since the last call
       (10 bits per character). The rest stays queued in the connection buffer, the way a real line holds it back.
       The credit is capped at ~50 ms of data so a burst after a quiet spell doesn't blast out at once. -102 tells
       the poll loop that data is waiting on the meter, so it keeps polling at the active rate. */
    int bps = g_emulated_bps > 0 ? (int)g_emulated_bps : (int)g_host_bps;
    bool query_flush = false;
    if (bps > 0) {
        int64_t now = get_current_time_ms();
        double cps = bps / 10.0;
        if (g_bps_last_ms == 0 || now < g_bps_last_ms) {
            g_bps_last_ms = now;
        }
        g_bps_credit += (double)(now - g_bps_last_ms) * cps / 1000.0;
        g_bps_last_ms = now;
        double cap = cps * 0.05;
        if (cap < 1.0) {
            cap = 1.0;
        }
        if (g_bps_credit > cap) {
            g_bps_credit = cap;
        }

        size_t query_end = find_pending_query_end();
        if (query_end > 0) {
            /* A terminal query is waiting: deliver through it now, unmetered (see find_pending_query_end) */
            query_flush = true;
            extern struct conn_api conn_api;
            max_read = query_end;
            if (conn_api.rx_parse_cb != NULL) {
                max_read *= 2;  /* conn_recv_upto takes half the requested size in raw bytes when it has to expand */
            }
            if (max_read > sizeof(buffer) - 16) {
                max_read = sizeof(buffer) - 16;
            }
        } else {
            if (g_bps_credit < 1.0) {
                pthread_mutex_unlock(&g_lock);
                return -102;
            }
            max_read = (size_t)g_bps_credit;
            if (max_read > sizeof(buffer) - 16) {
                max_read = sizeof(buffer) - 16;  /* leaves room to finish a ZMODEM header, below */
            }
        }
    } else {
        g_bps_credit = 0;
        g_bps_last_ms = 0;
    }

    int len = conn_recv_upto(buffer, max_read, 0);

    if (len <= 0) {
        pthread_mutex_unlock(&g_lock);
        return len;
    }

    if (bps > 0) {
        if (!query_flush) {
            g_bps_credit -= len;
        }
        g_query_scan_waiting = (size_t)-1;  /* the queue moved: look again next time */

        /* ZMODEM detection needs ZDLE and the header type in the same buffer, with the ZPADs before them. A
           metered read can stop in the middle of that, and the transfer would never auto-start. When the chunk
           ends in a possible header start, pull the rest of the header in unmetered. */
        int tail_start = len > 3 ? len - 3 : 0;
        for (int i = tail_start; i < len; i++) {
            if ((buffer[i] == 0x2a || buffer[i] == 0x18) && conn_data_waiting() > 0) {
                int extra = conn_recv_upto(buffer + len, 16, 0);
                if (extra > 0) {
                    len += extra;
                }
                break;
            }
        }
    }

    /* Track bytes received */
    g_bytes_received += len;

    /* Buffer data for logging if enabled */
    if (g_logging_enabled) {
        pthread_mutex_lock(&g_log_lock);
        if (g_logging_enabled) {  /* Re-check under lock to avoid TOCTOU */
            int space_available = LOG_BUFFER_SIZE - g_log_buffer_len;
            int to_copy = (len < space_available) ? len : space_available;
            if (to_copy > 0) {
                memcpy(g_log_buffer + g_log_buffer_len, buffer, to_copy);
                g_log_buffer_len += to_copy;
            }
        }
        pthread_mutex_unlock(&g_log_lock);
    }

    /* Check for ZMODEM init sequence */
    zmodem_detect_result_t zmodem_result = detect_zmodem_ex(buffer, len);
    /* Use < len (not <=) to prevent off-by-one boundary access */
    if (zmodem_result.position >= 0 && zmodem_result.position < len) {
        int zmodem_pos = zmodem_result.position;
        LOGI("ZMODEM detected at position %d, frame type 0x%02x", zmodem_pos, zmodem_result.frame_type);

        /* Process any data before ZMODEM start through terminal (through the TRACE filter, like the normal path:
           a binary frame that ended just before a transfer started must not reach cterm as stray bytes) */
        if (zmodem_pos > 0) {
            struct terminal_feed feed;
            feed.ret_len = 0;
            feed.speed = -1;
            linux_apc_attach(g_cterm);
            keyev_attach(g_cterm);
            mouse_bridge_attach(g_cterm);
            trace_stream_feed((const char *)buffer, (size_t)zmodem_pos, feed_terminal, &feed);
            keyev_sync_modes(g_cterm);
            if (feed.speed >= 0) {
                g_host_bps = feed.speed;  /* the board set its own speed (CSI * r) */
            }
            if (feed.ret_len > 0 && conn_send(feed.ret, feed.ret_len, 1000) > 0) {
                g_bytes_sent += feed.ret_len;
            }
        }

        /* Save ZMODEM data for later */
        pthread_mutex_lock(&g_zmodem_lock);
        g_zmodem_buffer_len = len - zmodem_pos;
        /* Validate buffer length is positive and within bounds (prevents signed wrap) */
        if (g_zmodem_buffer_len <= 0) {
            g_zmodem_buffer_len = 0;
            pthread_mutex_unlock(&g_zmodem_lock);
            pthread_mutex_unlock(&g_lock);
            return 0;  /* Nothing to copy */
        }
        if (g_zmodem_buffer_len > (int)sizeof(g_zmodem_buffer)) {
            g_zmodem_buffer_len = sizeof(g_zmodem_buffer);
        }
        memcpy(g_zmodem_buffer, buffer + zmodem_pos, g_zmodem_buffer_len);

        /* Set appropriate flag based on frame type */
        if (zmodem_result.frame_type == ZMODEM_FRAME_ZRINIT) {
            /* BBS is ready to receive - this is for uploads */
            LOGI("ZRINIT detected - BBS ready to receive upload");
            g_zmodem_upload_ready = 1;
            g_zmodem_detected = 0;
            pthread_mutex_unlock(&g_zmodem_lock);
            pthread_mutex_unlock(&g_lock);
            return -101;  /* Signal ZMODEM upload ready */
        } else {
            /* ZRQINIT or other - this is for downloads */
            LOGI("ZRQINIT detected - BBS wants to send download");
            g_zmodem_detected = 1;
            g_zmodem_upload_ready = 0;
            pthread_mutex_unlock(&g_zmodem_lock);
            pthread_mutex_unlock(&g_lock);
            return -100;  /* Signal ZMODEM download detected */
        }
    }

    /* Scan for ESC]800; in buffer — TAP debug diagnostic (only while tracing) */
    for (int i = 0; tap_debug_enabled() && i + 5 < len; i++) {
        if (buffer[i] == 0x1B && buffer[i+1] == ']' &&
            buffer[i+2] == '8' && buffer[i+3] == '0' &&
            buffer[i+4] == '0' && buffer[i+5] == ';') {
            tap_debug_log("syncterm_process_data: ESC]800; FOUND at offset %d in %d-byte buffer", i, len);
            /* Log first 128 bytes of the OSC for context */
            int loglen = len - i;
            if (loglen > 128) loglen = 128;
            char hexbuf[260];
            for (int h = 0; h < loglen && h < 128; h++)
                sprintf(hexbuf + h*2, "%02x", buffer[i+h]);
            hexbuf[loglen*2] = '\0';
            tap_debug_log("syncterm_process_data: hex from ESC: %s", hexbuf);
            break;
        }
    }

    /* Normal processing through terminal emulator. TRACE binary frames come out first (apc/trace_bridge.c): cterm
       would drop them (it only takes printable text inside an APC), so the filter queues them for the app and hands
       the text around them to feed_terminal() in order. */
    struct terminal_feed feed;
    feed.ret_len = 0;
    feed.speed = -1;

    linux_apc_attach(g_cterm);   /* SyncTERM:... APCs (cache, JXL images, audio) -- apc/apc_bridge.c */
    keyev_attach(g_cterm);       /* CSI = 1 h physical key reports -- see above syncterm_send_key */
    mouse_bridge_attach(g_cterm);  /* CSI ? 1000 h mouse reports -- mouse_bridge.c */
    trace_stream_feed((const char *)buffer, (size_t)len, feed_terminal, &feed);
    keyev_sync_modes(g_cterm);
    if (feed.speed >= 0) {
        g_host_bps = feed.speed;  /* the board set its own speed (CSI * r) */
    }
    linux_apc_poll();

    char *retbuf = feed.ret;
    size_t ret_len = feed.ret_len;

    pthread_mutex_unlock(&g_lock);

    /* Send any return data (e.g., terminal queries) */
    if (ret_len > 0 && conn_send(retbuf, ret_len, 1000) > 0) {
        g_bytes_sent += ret_len;
    }

    return len;
}

/* Check how much data is waiting */
SYNCTERM_API int syncterm_data_waiting(void) {
    if (!g_connected) {
        return 0;
    }
    return (int)conn_data_waiting();
}

/* ------------------------------------------------------------------ */
/* Screen buffer functions                                             */
/* ------------------------------------------------------------------ */

/* Convert an RGB color (0x00RRGGBB) to the nearest palette index (0-15). */
/* Uses squared Euclidean distance in RGB space. */
static int rgb_to_nearest_palette(uint32_t rgb, uint32_t *palette) {
    int r = (rgb >> 16) & 0xFF;
    int g = (rgb >> 8) & 0xFF;
    int b = rgb & 0xFF;
    int best_idx = 0;
    int best_dist = INT_MAX;
    for (int i = 0; i < 16; i++) {
        int pr = (palette[i] >> 16) & 0xFF;
        int pg = (palette[i] >> 8) & 0xFF;
        int pb = palette[i] & 0xFF;
        int dr = r - pr;
        int dg = g - pg;
        int db = b - pb;
        int dist = dr*dr + dg*dg + db*db;
        if (dist < best_dist) {
            best_dist = dist;
            best_idx = i;
        }
    }
    return best_idx;
}

/* Resolve a vmem_cell colour to a 24-bit RGB value.                          */
/* This mirrors color_value() in bitmap_con.c, which is SyncTERM's own         */
/* renderer: the CIOLIB_COLOR_RGB flag means the cell carries a literal 24-bit */
/* colour (ESC[38;2;R;G;Bm and friends), anything else is an index into the    */
/* palette. cterm fills vmem_cell.fg/bg from cterm->fg_color, which for legacy */
/* content is exactly what attr2palette() produced from the DOS attribute      */
/* byte -- so for ordinary ANSI this returns the same colour the old           */
/* legacy_attr path did, while extended colours now survive instead of being   */
/* snapped to the nearest of 16.                                               */
static uint32_t cell_colour_rgb(uint32_t col, uint32_t *pal) {
    if (col & 0x80000000u)          /* CIOLIB_COLOR_RGB */
        return col & 0x00FFFFFFu;
    if (!pal)
        return 0;
    /* The ciolib palette is 65536 entries, so a 16-bit index can never overrun. */
    return pal[col & 0xFFFFu] & 0x00FFFFFFu;
}

/* Get screen buffer for rendering */
/* Writes packed int array: [char | (attr << 8) | (fg << 16) | (bg << 24)] */
/* When fgout/bgout are non-NULL they additionally receive the per-cell 24-bit */
/* RGB colours, which is what the true-colour renderer consumes; the packed    */
/* array keeps its 4-bit indices so existing callers are unaffected.           */
/* Returns number of cells written */
static int screen_buffer_impl(int *buffer, int *fgout, int *bgout, int buffer_len) {
    if (!buffer || buffer_len <= 0) {
        return 0;
    }

    atomic_fetch_add(&g_snapshot_waiting, 1);
    pthread_mutex_lock(&g_lock);
    atomic_fetch_sub(&g_snapshot_waiting, 1);

    linux_ciolib_lock();

    int width = linux_ciolib_get_screen_width();
    int height = linux_ciolib_get_screen_height();

    /* Validate dimensions and calculate size with overflow protection */
    int size;
    if (!validate_dimensions(width, height, &size)) {
        LOGE("Invalid screen dimensions: %dx%d", width, height);
        linux_ciolib_unlock();
        pthread_mutex_unlock(&g_lock);
        return 0;
    }

    struct vmem_cell *screen = linux_ciolib_get_screen_buffer();
    if (!screen) {
        linux_ciolib_unlock();
        pthread_mutex_unlock(&g_lock);
        return 0;
    }

    /* Get current palette for RGB-to-palette conversion */
    uint32_t *pal = linux_ciolib_get_palette();

    /* Clamp to caller's buffer size */
    if (size > buffer_len) {
        size = buffer_len;
    }

    /* Pack cell data for caller.                                          */
    /* Both FG and BG are derived from legacy_attr — the authoritative      */
    /* DOS attribute byte maintained by cterm via textattr().                */
    /* When CIOLIB_COLOR_RGB flag (0x80000000) is set on vmem_cell.fg/bg,   */
    /* fall back to RGB-to-palette conversion for that component.           */
    /* Bit 23 (bit 7 of fg byte) carries the underline flag.               */
    /* ⚠️ BRIGHT-BACKGROUND MODES: attribute bit 7 is NOT blink.
     *
     * `set_bright_bg` in cterm.c deliberately sets attr bit 7 in any CIOLIB_VIDEO_BGBRIGHT mode
     * to mean "background colour 8-15", and the Commodore modes are BGBRIGHT|NOBLINK. Treating
     * that bit as the ANSI blink attribute did two visible things on a PETSCII board:
     *   1. every reverse-video cell flickered in step with the text-blink timer, which reads as
     *      "the cursor is making the screen blink" — most PETSCII artwork is reverse-video blocks;
     *   2. the background got masked down to 0-7, so colours came out plain wrong.
     * So in a NOBLINK mode we keep the bright background AND strip the bit from the packed
     * attribute, which is the only thing the managed renderer reads it for. */
    const unsigned int blink_bit = linux_ciolib_mode_no_blink() ? 0u : 0x80u;

    for (int i = 0; i < size; i++) {
        unsigned int ch = screen[i].ch & 0xFFu;
        unsigned int attr = screen[i].legacy_attr & 0xFFu;

        unsigned int fg;
        if (screen[i].fg & 0x80000000u) {
            /* Direct RGB foreground — convert to nearest 16-color palette entry */
            fg = pal ? (unsigned int)rgb_to_nearest_palette(screen[i].fg & 0x00FFFFFFu, pal) : 0u;
        } else {
            /* Use legacy_attr lower nibble for foreground (0-15) */
            fg = attr & 0x0Fu;
        }
        if (screen[i].flags & VMEM_FLAG_UNDERLINE)
            fg |= 0x80u;

        unsigned int bg;
        if (screen[i].bg & 0x80000000u) {
            /* Direct RGB background — convert to nearest 16-color palette entry */
            bg = pal ? (unsigned int)rgb_to_nearest_palette(screen[i].bg & 0x00FFFFFFu, pal) : 0u;
        } else {
            /* Use legacy_attr upper nibble for background (0-7, +bright) */
            bg = (attr >> 4) & 0x0Fu;
        }

        buffer[i] = (int)(ch | ((attr & ~(0x80u ^ blink_bit)) << 8) | (fg << 16) | (bg << 24));

        /* True colour: hand the renderer the real 24-bit values. Where bit 7 really IS the DOS
         * blink attribute it steals the background's intensity bit, so a blinking cell's
         * background index is only 0-7 -- mask before the palette lookup or a blinking cell
         * renders on a bright background it never asked for. In a NOBLINK mode the bit means the
         * opposite and must be left alone. (RGB backgrounds are literal and are never masked.) */
        if (fgout)
            fgout[i] = (int)cell_colour_rgb(screen[i].fg, pal);
        if (bgout) {
            uint32_t bgcol = screen[i].bg;
            if (blink_bit && (attr & blink_bit) && !(bgcol & 0x80000000u))
                bgcol &= 0x07u;
            bgout[i] = (int)cell_colour_rgb(bgcol, pal);
        }
    }

    linux_ciolib_clear_dirty();
    linux_ciolib_unlock();

    pthread_mutex_unlock(&g_lock);
    return size;
}

SYNCTERM_API int syncterm_get_screen_buffer(int *buffer, int buffer_len) {
    return screen_buffer_impl(buffer, NULL, NULL, buffer_len);
}

/* True-colour variant: same packed cells, plus per-cell 24-bit fg/bg. All three
 * arrays must hold at least buffer_len ints. One call so the dirty flag is
 * cleared exactly once per frame. */
SYNCTERM_API int syncterm_get_screen_buffer_rgb(int *buffer, int *fg, int *bg, int buffer_len) {
    return screen_buffer_impl(buffer, fg, bg, buffer_len);
}

/* Get color palette */
/* Writes 16 ints, returns 1 on success */
SYNCTERM_API int syncterm_get_palette(int *palette) {
    if (!palette) {
        return 0;
    }

    linux_ciolib_lock();
    uint32_t *pal = linux_ciolib_get_palette();

    if (!pal) {
        linux_ciolib_unlock();
        return 0;
    }

    for (int i = 0; i < 16; i++) {
        palette[i] = (int)pal[i];
    }

    linux_ciolib_unlock();
    return 1;
}

/* Get screen width */
SYNCTERM_API int syncterm_get_screen_width(void) {
    return linux_ciolib_get_screen_width();
}

/* Get screen height */
SYNCTERM_API int syncterm_get_screen_height(void) {
    return linux_ciolib_get_screen_height();
}

/* Get cursor X position */
SYNCTERM_API int syncterm_get_cursor_x(void) {
    return linux_ciolib_get_cursor_x();
}

/* Get cursor Y position */
SYNCTERM_API int syncterm_get_cursor_y(void) {
    return linux_ciolib_get_cursor_y();
}

/* Check if cursor is visible */
SYNCTERM_API int syncterm_is_cursor_visible(void) {
    return linux_ciolib_is_cursor_visible() ? 1 : 0;
}

/* Get cursor type: 0=hidden, 1=solid block, 2=normal underline */
SYNCTERM_API int syncterm_get_cursor_type(void) {
    return linux_ciolib_get_cursor_type();
}

/* Get cursor scanline start (top of cursor within cell) */
SYNCTERM_API int syncterm_get_cursor_start(void) {
    return linux_ciolib_get_cursor_start();
}

/* Get cursor scanline end (bottom of cursor within cell) */
SYNCTERM_API int syncterm_get_cursor_end(void) {
    return linux_ciolib_get_cursor_end();
}

/* Check if cursor blinks (0 = steady/always visible) */
SYNCTERM_API int syncterm_get_cursor_blinks(void) {
    return linux_ciolib_get_cursor_blinks();
}

/* Get cursor foreground color as resolved 0x00RRGGBB (matching color_value(ciolib_fg)) */
SYNCTERM_API int syncterm_get_ciolib_fg(void) {
    return (int)linux_ciolib_get_fg_color();
}

/* Check if screen is dirty (needs redraw) */
SYNCTERM_API int syncterm_is_screen_dirty(void) {
    return linux_ciolib_is_dirty() ? 1 : 0;
}

/* Get dirty region bounds (for partial redraw optimization) */
/* Writes min_x, min_y, max_x, max_y; returns 1 if dirty */
SYNCTERM_API int syncterm_get_dirty_region(int *min_x, int *min_y, int *max_x, int *max_y) {
    if (!min_x || !min_y || !max_x || !max_y) {
        return 0;
    }

    linux_ciolib_lock();

    int has_dirty = linux_ciolib_get_dirty_region(min_x, min_y, max_x, max_y);

    linux_ciolib_unlock();
    return has_dirty ? 1 : 0;
}

/* ------------------------------------------------------------------ */
/* Terminal control functions                                           */
/* ------------------------------------------------------------------ */

/* Set terminal size */
SYNCTERM_API void syncterm_set_terminal_size(int width, int height) {
    LOGI("syncterm_set_terminal_size: %dx%d", width, height);

    /* Validate input dimensions */
    if (width <= 0 || height <= 0 || width > 1000 || height > 1000) {
        LOGE("Invalid terminal dimensions: %dx%d", width, height);
        return;
    }

    pthread_mutex_lock(&g_lock);

    /* Check if size is actually changing - skip if same to preserve screen content */
    if (g_term_width == width && g_term_height == height) {
        LOGI("Terminal size unchanged (%dx%d), skipping resize", width, height);
        pthread_mutex_unlock(&g_lock);
        return;
    }

    g_term_width = width;
    g_term_height = height;

    /* Resize the ciolib screen */
    linux_ciolib_resize(width, height);

    /* Reinitialize cterm with new size if already initialized */
    if (g_cterm && g_initialized) {
        struct cterminal *old_cterm = g_cterm;
        g_cterm = NULL;  /* Set to NULL first to prevent race conditions */
        cterm_end(old_cterm, 0);

        /* Reallocate scrollback if needed with overflow protection */
        int scrollback_cols = width > 132 ? width : 132;
        size_t scrollback_alloc_size;
        if (!validate_alloc_size(g_scrollback_lines, scrollback_cols,
                                 sizeof(struct vmem_cell), &scrollback_alloc_size) ||
            scrollback_alloc_size > MAX_SCROLLBACK_BYTES) {
            LOGE("Scrollback buffer size overflow during resize: %d x %d (max: %d bytes)",
                 g_scrollback_lines, scrollback_cols, MAX_SCROLLBACK_BYTES);
            /* Keep old scrollback and try to reinitialize with it */
            g_cterm = cterm_init(height, width, 1, 1,
                                 g_scrollback_lines, scrollback_cols,
                                 g_scrollback, g_emulation);
            if (g_cterm) {
                cterm_start(g_cterm);
            }
            pthread_mutex_unlock(&g_lock);
            return;
        }

        struct vmem_cell *old_scrollback = g_scrollback;
        g_scrollback = calloc(1, scrollback_alloc_size);

        if (g_scrollback) {
            free(old_scrollback);
            g_cterm = cterm_init(height, width, 1, 1,
                                 g_scrollback_lines, scrollback_cols,
                                 g_scrollback, g_emulation);
            if (g_cterm) {
                cterm_start(g_cterm);
            } else {
                LOGE("Failed to reinitialize cterm after resize");
            }
        } else {
            LOGE("Failed to allocate scrollback buffer during resize");
            g_scrollback = old_scrollback;  /* Restore old buffer */
        }
    }

    pthread_mutex_unlock(&g_lock);
}

/* Set font by name */
SYNCTERM_API int syncterm_set_font(const char *fontName) {
    LOGI("syncterm_set_font called");

    if (!g_initialized) {
        LOGE("Not initialized");
        return 0;
    }

    if (!fontName) {
        LOGE("Font name is NULL");
        return 0;
    }

    pthread_mutex_lock(&g_lock);

    /* Search for the font in conio_fontdata array */
    int font_id = -1;
    for (int i = 0; i < 256; i++) {
        if (conio_fontdata[i].desc != NULL &&
            strcmp(conio_fontdata[i].desc, fontName) == 0) {
            font_id = i;
            break;
        }
    }

    if (font_id >= 0) {
        /* Save to global for use in syncterm_connect */
        strncpy(g_font_name, fontName, sizeof(g_font_name) - 1);
        g_font_name[sizeof(g_font_name) - 1] = '\0';

        /* Also update current config (for active connections) */
        strncpy(g_bbs_config.font, fontName, sizeof(g_bbs_config.font) - 1);
        g_bbs_config.font[sizeof(g_bbs_config.font) - 1] = '\0';

        /* Set the font in ciolib (font_id, force=1, font_num=0 for default+primary) */
        int result = ciolib_setfont(font_id, 1, 0);
        LOGI("setfont: '%s' (id=%d) result=%d", fontName, font_id, result);

        /* cterm snapshots getfont(1) into altfont[] at init and re-applies altfont on
           every write, so a cterm created before this call (screen-mode change) would
           keep flipping back to the old font. Keep its primary slot in step. */
        if (result && g_cterm)
            g_cterm->altfont[0] = font_id;

        pthread_mutex_unlock(&g_lock);
        return result;
    }

    LOGE("Font not found: %s", fontName);
    pthread_mutex_unlock(&g_lock);
    return 0;
}

/* Set font by ID */
SYNCTERM_API int syncterm_set_font_by_id(int fontId) {
    if (fontId < 0 || fontId >= 256) {
        return 0;
    }

    pthread_mutex_lock(&g_lock);

    /* Set in ciolib — linux_setfont updates g_current_font_id for us */
    int result = ciolib_setfont(fontId, 1, 0);
    LOGI("setfont by id: %d result=%d", fontId, result);
    if (result && g_cterm)
        g_cterm->altfont[0] = fontId;

    pthread_mutex_unlock(&g_lock);
    return result;
}

/**
 * The font the TERMINAL is using, which is `cterm->altfont[0]` and NOT the live ciolib font.
 *
 * ⚠️ **THIS DISTINCTION IS THE WHOLE BUG BEHIND "PETSCII CASE MODE DOES NOTHING".**
 * `cterm_write` saves the host's fonts at entry, installs `cterm->altfont[]`, processes the data,
 * and RESTORES the saved fonts before returning. So a font change made *during* the call — which is
 * exactly what the PETSCII 0x0E / 0x8E case-switch does — has already been undone by the time
 * cterm_write returns. Upstream SyncTERM never notices because bitmap_con draws inside that window;
 * we poll from managed code afterwards, so we must read the value that PERSISTS.
 *
 * `cterm->altfont[0]` is that value: the PETSCII handler assigns it alongside its `setfont` call,
 * `cterm_init` seeds it from the host font, and `syncterm_set_font` keeps it in step.
 */
static int resolved_font_id(void) {
    if (g_cterm) {
        int f = g_cterm->altfont[0];
        if (f >= 0 && f < 256 && conio_fontdata[f].desc != NULL)
            return f;
    }
    return g_current_font_id;
}

/* Get current font ID (polled by C# to detect BBS-driven font changes) */
SYNCTERM_API int syncterm_get_current_font_id(void) {
    return resolved_font_id();
}

/* Clear the screen and reset terminal state for a fresh connection.
   Destroys and recreates cterm so all state (colors, cursor, modes) is clean. */
/* Destroy and recreate cterm with fresh state (caller holds g_lock). */
static void recreate_cterm_locked(void) {
    if (!(g_cterm && g_initialized))
        return;

    cterm_end(g_cterm, 0);
    g_cterm = NULL;

    /* Clear and reuse existing scrollback buffer */
    if (g_scrollback) {
        int scrollback_cols = g_term_width > 132 ? g_term_width : 132;
        size_t scrollback_alloc_size;
        if (validate_alloc_size(g_scrollback_lines, scrollback_cols,
                                 sizeof(struct vmem_cell), &scrollback_alloc_size) &&
            scrollback_alloc_size <= MAX_SCROLLBACK_BYTES) {
            memset(g_scrollback, 0, scrollback_alloc_size);
        }
    }

    /* Recreate cterm with fresh state */
    int scrollback_cols = g_term_width > 132 ? g_term_width : 132;
    g_cterm = cterm_init(g_term_height, g_term_width, 1, 1,
                         g_scrollback_lines, scrollback_cols,
                         g_scrollback, g_emulation);
    if (g_cterm) {
        cterm_start(g_cterm);
        LOGI("Terminal reset: %dx%d (fresh cterm, emulation %d)", g_term_width, g_term_height, g_emulation);
    } else {
        LOGE("Failed to recreate cterm during reset");
    }
}

SYNCTERM_API void syncterm_clear_screen(void) {
    pthread_mutex_lock(&g_lock);
    if (g_cterm && g_initialized) {
        recreate_cterm_locked();
    } else if (g_cterm) {
        cterm_clearscreen(g_cterm, 7);
    }
    pthread_mutex_unlock(&g_lock);
}

/* Push keyboard input */
SYNCTERM_API void syncterm_push_input(const uint8_t *data, int len) {
    if (!data || len <= 0) return;

    linux_ciolib_push_input_buffer((const unsigned char *)data, len);
}

/* SSH blank login (call before every connect): 1 = log in with no credentials and never prompt, for boards that
   log the caller in inside the BBS (Rainwire); a board that wants a password fails with a message saying so.
   0 = the username/password given to syncterm_connect, as always. See ssh_blank_login in ssh.c. */
SYNCTERM_API void syncterm_set_ssh_blank_login(int on) {
    pthread_mutex_lock(&g_lock);
    g_ssh_blank_login = on ? 1 : 0;
    LOGI("SSH blank login set to: %d", g_ssh_blank_login);
    pthread_mutex_unlock(&g_lock);
}

/* Set hide status line option (call before connect) */
SYNCTERM_API void syncterm_set_hide_status_line(int hide) {
    pthread_mutex_lock(&g_lock);
    g_hide_status_line = hide ? 1 : 0;
    LOGI("Hide status line set to: %d", g_hide_status_line);
    pthread_mutex_unlock(&g_lock);
}

/* Set screen mode option (call before connect) */
/* Mode indices: 0=80x25, 1=80x30, 2=80x40, 3=80x50, 4=132x25, 5=132x30, 6=132x40, 7=132x50,
   8=132x60, 9=C64 40x25, 10=C128 40x25, 11=C128 80x25 */
/* These must be mapped to native SyncTERM SCREEN_MODE_* enum values. */
static const int bridge_to_native_screen_mode[] = {
    SCREEN_MODE_80X25,   /* 0 -> 80x25 */
    SCREEN_MODE_80X30,   /* 1 -> 80x30 */
    SCREEN_MODE_80X43,   /* 2 -> 80x40 (closest: 80x43) */
    SCREEN_MODE_80X50,   /* 3 -> 80x50 */
    SCREEN_MODE_132X25,  /* 4 -> 132x25 */
    SCREEN_MODE_132X30,  /* 5 -> 132x30 */
    SCREEN_MODE_132X43,  /* 6 -> 132x40 (closest: 132x43) */
    SCREEN_MODE_132X50,  /* 7 -> 132x50 */
    SCREEN_MODE_132X60,  /* 8 -> 132x60 */
    SCREEN_MODE_C64,     /* 9 -> Commodore 64 40x25 (PETSCII) */
    SCREEN_MODE_C128_40, /* 10 -> Commodore 128 40x25 (PETSCII) */
    SCREEN_MODE_C128_80, /* 11 -> Commodore 128 80x25 (PETSCII) */
};
#define BRIDGE_SCREEN_MODE_COUNT (sizeof(bridge_to_native_screen_mode) / sizeof(bridge_to_native_screen_mode[0]))

SYNCTERM_API void syncterm_set_screen_mode(int mode) {
    pthread_mutex_lock(&g_lock);
    if (mode < 0 || mode >= (int)BRIDGE_SCREEN_MODE_COUNT) {
        mode = 0;
    }
    g_screen_mode = bridge_to_native_screen_mode[mode];
    LOGI("Screen mode set to: %d -> native %d", mode, g_screen_mode);

    /* Derive the cterm emulation and the explicit ciolib video mode from the screen
       mode, the same way bbslist.c's get_emulation() does upstream. */
    {
        int emulation = CTERM_EMULATION_ANSI_BBS;
        int vmode = -1;
        switch (g_screen_mode) {
            case SCREEN_MODE_C64:     emulation = CTERM_EMULATION_PETASCII; vmode = C64_40X25;  break;
            case SCREEN_MODE_C128_40: emulation = CTERM_EMULATION_PETASCII; vmode = C128_40X25; break;
            case SCREEN_MODE_C128_80: emulation = CTERM_EMULATION_PETASCII; vmode = C128_80X25; break;
            default: break;
        }
        /* ⚠️ UNCONDITIONAL — do NOT put this back behind an "only if the mode changed" test.
         *
         * Nothing resets the native side between connections: syncterm_init() early-returns once
         * g_initialized is set and no client calls the cleanup that WOULD reset g_emulation and
         * the vmode override. So on a SECOND connection to the same board both already match, the
         * change test came out false, and cterm was NOT recreated — it carried the previous
         * session's c64reversemode / attribute / cursor / margins straight into the new one, which
         * on a PETSCII board draws as corrupt artwork (seen on Android 2026-09-04).
         * set_screen_mode is only ever called from the connect path, so applying the mode's
         * starting state every time is exactly right. */
        g_emulation = emulation;
        linux_ciolib_set_video_mode(vmode);

        /* cterm caches emulation and the mode's default attribute at init time */
        recreate_cterm_locked();
    }
    pthread_mutex_unlock(&g_lock);
}

/* ------------------------------------------------------------------ */
/* Status / stats functions                                            */
/* ------------------------------------------------------------------ */

/* Get connection status info */
/* Writes status string to buffer, returns length written */
SYNCTERM_API int syncterm_get_status_info(char *buffer, int buffer_len) {
    if (!buffer || buffer_len <= 0) {
        return 0;
    }

    char status[256];

    if (!g_initialized) {
        snprintf(status, sizeof(status), "Not initialized");
    } else if (!g_connected) {
        snprintf(status, sizeof(status), "Disconnected");
    } else {
        snprintf(status, sizeof(status), "Connected to %s:%d (%dx%d)",
                 g_bbs_config.addr, g_bbs_config.port,
                 g_term_width, g_term_height);
    }

    int len = (int)strlen(status);
    if (len >= buffer_len) {
        len = buffer_len - 1;
    }
    memcpy(buffer, status, len);
    buffer[len] = '\0';

    return len;
}

/* Modem speed emulation: the user's setting in bps, 0 = full speed (a board's own CSI * r still applies) */
SYNCTERM_API void syncterm_set_emulated_bps(int bps) {
    g_emulated_bps = bps > 0 ? bps : 0;
}

/* The speed incoming data is actually metered at: the user's setting, else the board's, 0 = full speed */
SYNCTERM_API int syncterm_get_effective_bps(void) {
    int user = g_emulated_bps;
    return user > 0 ? user : (int)g_host_bps;
}

/* Get connection statistics */
/* Writes 4 int64s: [bytes_sent, bytes_received, connect_time_ms, current_time_ms] */
/* Returns 1 on success */
SYNCTERM_API int syncterm_get_connection_stats(int64_t *stats) {
    if (!stats) {
        return 0;
    }

    int64_t current_time = get_current_time_ms();
    stats[0] = (int64_t)g_bytes_sent;
    stats[1] = (int64_t)g_bytes_received;
    stats[2] = (int64_t)g_connect_time_ms;
    stats[3] = current_time;

    return 1;
}

/* Get font bitmap data for rendering */
/* Returns the 8xN bitmap data for all 256 characters */
/* Format: first 2 bytes are width (8) and height, rest is bitmap data */
/* Returns bytes written, or 0 on failure */
SYNCTERM_API int syncterm_get_font_bitmap(uint8_t *buffer, int buffer_len) {
    if (!buffer || buffer_len <= 2) {
        return 0;
    }

    pthread_mutex_lock(&g_lock);

    const int font_id = resolved_font_id();
    LOGI("syncterm_get_font_bitmap: font_id=%d (altfont-resolved)", font_id);

    /* Find font with 8x16 data */
    const char *bitmap_data = NULL;
    int font_height = 16;

    /* First try current font */
    if (font_id >= 0 && font_id < 256) {
        const char *desc = conio_fontdata[font_id].desc;
        LOGI("Font %d desc: %s", font_id, desc ? desc : "(null)");
        LOGI("Font %d 8x16: %p, 8x14: %p, 8x8: %p", font_id,
             (const void *)conio_fontdata[font_id].eight_by_sixteen,
             (const void *)conio_fontdata[font_id].eight_by_fourteen,
             (const void *)conio_fontdata[font_id].eight_by_eight);

        /* Commodore/Atari modes are natively 8x8 (320x200); use that slot first so the
           picture keeps the machine's real geometry instead of the row-doubled 8x16. */
        int mode_h = linux_ciolib_get_mode_char_height();
        if (mode_h == 8 && conio_fontdata[font_id].eight_by_eight != NULL) {
            bitmap_data = conio_fontdata[font_id].eight_by_eight;
            font_height = 8;
            LOGI("Using 8x8 font from font %d (mode native)", font_id);
        } else if (conio_fontdata[font_id].eight_by_sixteen != NULL) {
            bitmap_data = conio_fontdata[font_id].eight_by_sixteen;
            font_height = 16;
            LOGI("Using 8x16 font from font %d", font_id);
        } else if (conio_fontdata[font_id].eight_by_fourteen != NULL) {
            bitmap_data = conio_fontdata[font_id].eight_by_fourteen;
            font_height = 14;
            LOGI("Using 8x14 font from font %d", font_id);
        } else if (conio_fontdata[font_id].eight_by_eight != NULL) {
            bitmap_data = conio_fontdata[font_id].eight_by_eight;
            font_height = 8;
            LOGI("Using 8x8 font from font %d", font_id);
        }
    }

    /* Fallback to Codepage 437 English (font 0) */
    /* Font 0 is always valid since conio_fontdata array size is 257 (indices 0-256) */
    #define CONIO_FONTDATA_SIZE 257
    if (bitmap_data == NULL && 0 < CONIO_FONTDATA_SIZE) {
        LOGI("Trying fallback to font 0");
        LOGI("Font 0 8x16: %p", (const void *)conio_fontdata[0].eight_by_sixteen);
        if (conio_fontdata[0].eight_by_sixteen != NULL) {
            bitmap_data = conio_fontdata[0].eight_by_sixteen;
            font_height = 16;
            LOGI("Using fallback 8x16 font from font 0");
        }
    }
    #undef CONIO_FONTDATA_SIZE

    if (bitmap_data == NULL) {
        LOGE("No font bitmap data available!");
        pthread_mutex_unlock(&g_lock);
        return 0;
    }

    LOGI("Got font bitmap data, height=%d", font_height);

    /* Update char_pixel_height for sixel scroll calculations */
    linux_ciolib_set_char_pixel_height(font_height);

    /* Create buffer with font data */
    /* Format: first 2 bytes are width (8) and height, rest is bitmap data */
    /* font_height is 8, 14, or 16; max data_size = 256 * 16 = 4096 */
    int data_size = 256 * font_height;
    if (data_size <= 0 || data_size > 256 * 64) {
        pthread_mutex_unlock(&g_lock);
        return 0;
    }

    int total_size = data_size + 2;
    if (total_size > buffer_len) {
        LOGE("Font bitmap buffer too small: need %d, have %d", total_size, buffer_len);
        pthread_mutex_unlock(&g_lock);
        return 0;
    }

    buffer[0] = 8;
    buffer[1] = (uint8_t)font_height;
    memcpy(buffer + 2, bitmap_data, data_size);

    pthread_mutex_unlock(&g_lock);
    return total_size;
}

/* ------------------------------------------------------------------ */
/* ZMODEM functions                                                    */
/* ------------------------------------------------------------------ */

/* Check if ZMODEM auto-download was detected */
SYNCTERM_API int syncterm_is_zmodem_detected(void) {
    pthread_mutex_lock(&g_zmodem_lock);
    int result = g_zmodem_detected ? 1 : 0;
    pthread_mutex_unlock(&g_zmodem_lock);
    return result;
}

/* Get buffered ZMODEM data (call before starting ZMODEM receive) */
/* Returns bytes written, or 0 if no data */
SYNCTERM_API int syncterm_get_zmodem_buffer(uint8_t *buffer, int buffer_len) {
    if (!buffer || buffer_len <= 0) {
        return 0;
    }

    pthread_mutex_lock(&g_zmodem_lock);

    if (g_zmodem_buffer_len <= 0) {
        pthread_mutex_unlock(&g_zmodem_lock);
        return 0;
    }

    int to_copy = g_zmodem_buffer_len;
    if (to_copy > buffer_len) {
        to_copy = buffer_len;
    }

    memcpy(buffer, g_zmodem_buffer, to_copy);

    pthread_mutex_unlock(&g_zmodem_lock);
    return to_copy;
}

/* Clear ZMODEM detection state (call after transfer completes or is cancelled) */
SYNCTERM_API void syncterm_clear_zmodem_detected(void) {
    pthread_mutex_lock(&g_zmodem_lock);
    g_zmodem_detected = 0;
    g_zmodem_upload_ready = 0;
    g_zmodem_buffer_len = 0;
    pthread_mutex_unlock(&g_zmodem_lock);
    LOGI("ZMODEM detection cleared");
}

/* Clear buffered ZMODEM data, detection state, AND drain connection buffer */
/* The buffered data was ZRQINIT (BBS requesting to send) - we don't need to */
/* re-process it. zmodem_recv_init() will send ZRINIT and wait for ZFILE. */
/* We also drain any leftover data in the connection buffer that arrived */
/* between ZMODEM detection and starting the receive - this stale data */
/* would otherwise be read by zmodem_recv_header() causing failure. */
SYNCTERM_API int syncterm_push_zmodem_buffer(void) {
    pthread_mutex_lock(&g_zmodem_lock);

    int len = g_zmodem_buffer_len;

    if (len > 0) {
        LOGI("Clearing %d bytes of ZMODEM buffer (ZRQINIT signal - not pushing back)", len);
        LOGI("  First bytes were: %02x %02x %02x %02x",
             g_zmodem_buffer[0], g_zmodem_buffer[1],
             len > 2 ? g_zmodem_buffer[2] : 0, len > 3 ? g_zmodem_buffer[3] : 0);
    }

    /* Clear the buffer and detection state */
    g_zmodem_buffer_len = 0;
    g_zmodem_detected = 0;

    pthread_mutex_unlock(&g_zmodem_lock);

    /* CRITICAL: Drain any stale data from the connection buffer */
    /* Between ZMODEM detection and now, there may be leftover data from: */
    /* 1. Additional terminal output from the BBS (status messages, etc.) */
    /* 2. Data that arrived after the ZRQINIT but before we processed it */
    /* This stale data would confuse zmodem_recv_header() if not drained. */
    /* The BBS is waiting for our ZRINIT before sending ZFILE, so there */
    /* shouldn't be any important ZMODEM data to preserve here. */
    size_t waiting = conn_data_waiting();
    if (waiting > 0) {
        LOGI("Draining %zu bytes of stale data from connection buffer", waiting);
        unsigned char drain_buf[256];
        size_t total_drained = 0;
        while (conn_data_waiting() > 0 && total_drained < MAX_DRAIN_BYTES) {
            int n = conn_recv_upto(drain_buf, sizeof(drain_buf), 10);  /* 10ms timeout */
            if (n <= 0) break;
            total_drained += (size_t)n;  /* Explicit cast for type consistency */
            /* Log first few bytes for debugging */
            if (total_drained == (size_t)n) {
                LOGI("  First drained bytes: %02x %02x %02x %02x",
                     drain_buf[0], n > 1 ? drain_buf[1] : 0,
                     n > 2 ? drain_buf[2] : 0, n > 3 ? drain_buf[3] : 0);
            }
        }
        LOGI("Drained %zu total bytes", total_drained);
    } else {
        LOGI("Connection buffer is empty - good");
    }

    return 0;
}

/* Queue a file for upload (will be sent when BBS sends ZRINIT) */
SYNCTERM_API void syncterm_queue_upload(const char *filePath) {
    if (!filePath) {
        LOGE("Upload file path is NULL");
        return;
    }

    pthread_mutex_lock(&g_zmodem_lock);
    strncpy(g_upload_queued_file, filePath, sizeof(g_upload_queued_file) - 1);
    g_upload_queued_file[sizeof(g_upload_queued_file) - 1] = '\0';
    g_upload_file_queued = 1;
    pthread_mutex_unlock(&g_zmodem_lock);

    LOGI("File queued for upload: %s", g_upload_queued_file);
}

/* Check if a file is queued for upload */
SYNCTERM_API int syncterm_is_upload_queued(void) {
    pthread_mutex_lock(&g_zmodem_lock);
    int result = g_upload_file_queued ? 1 : 0;
    pthread_mutex_unlock(&g_zmodem_lock);
    return result;
}

/* Check if BBS is ready for upload (ZRINIT received) */
SYNCTERM_API int syncterm_is_upload_ready(void) {
    pthread_mutex_lock(&g_zmodem_lock);
    int result = g_zmodem_upload_ready ? 1 : 0;
    pthread_mutex_unlock(&g_zmodem_lock);
    return result;
}

/* Get the queued upload file path */
/* Writes to buffer, returns length written, or 0 if no file queued */
SYNCTERM_API int syncterm_get_queued_upload(char *buffer, int buffer_len) {
    if (!buffer || buffer_len <= 0) {
        return 0;
    }

    pthread_mutex_lock(&g_zmodem_lock);

    if (!g_upload_file_queued || g_upload_queued_file[0] == '\0') {
        pthread_mutex_unlock(&g_zmodem_lock);
        return 0;
    }

    int len = (int)strlen(g_upload_queued_file);
    if (len >= buffer_len) {
        len = buffer_len - 1;
    }
    memcpy(buffer, g_upload_queued_file, len);
    buffer[len] = '\0';

    pthread_mutex_unlock(&g_zmodem_lock);
    return len;
}

/* Clear the upload queue */
SYNCTERM_API void syncterm_clear_upload_queue(void) {
    pthread_mutex_lock(&g_zmodem_lock);
    g_upload_file_queued = 0;
    g_upload_queued_file[0] = '\0';
    g_zmodem_upload_ready = 0;
    pthread_mutex_unlock(&g_zmodem_lock);
    LOGI("Upload queue cleared");
}

/* ------------------------------------------------------------------ */
/* Bell / Audio functions                                              */
/* ------------------------------------------------------------------ */

/* Check if bell was detected and clear the flag (atomic read-and-clear) */
/* Returns: 1 if bell was detected, 0 otherwise */
SYNCTERM_API int syncterm_check_bell(void) {
    pthread_mutex_lock(&g_lock);
    int detected = g_bell_detected;
    g_bell_detected = 0;  /* Clear flag after reading */
    pthread_mutex_unlock(&g_lock);
    return detected ? 1 : 0;
}

/* Audio command functions (OSC 800 protocol) */

/* The TAP+ trace opens, writes and closes tap_native_debug.log several times per command, which stalls a board
 * streaming tile commands (and grew that file to 190 MB on Windows). Only when TERMINATOR_TAP_DEBUG is set (not
 * "0"). Windows 2026-09-15. */
static int tap_debug_enabled(void) {
    static int enabled = -1;
    if (enabled < 0) {
        const char *value = getenv("TERMINATOR_TAP_DEBUG");
        enabled = (value != NULL && value[0] != '\0' && value[0] != '0') ? 1 : 0;
    }
    return enabled;
}

/* Debug file logging for TAP audio pipeline diagnostics */
static void tap_debug_log(const char *fmt, ...) {
    if (!tap_debug_enabled())
        return;
    if (g_tap_logpath[0] == '\0') {
        const char *config_home = getenv("XDG_CONFIG_HOME");
        if (config_home && config_home[0]) {
            snprintf(g_tap_logpath, sizeof(g_tap_logpath), "%s/TERMinator/tap_native_debug.log", config_home);
        } else {
            const char *home = getenv("HOME");
            if (home && home[0]) {
                snprintf(g_tap_logpath, sizeof(g_tap_logpath), "%s/.config/TERMinator/tap_native_debug.log", home);
            } else {
                snprintf(g_tap_logpath, sizeof(g_tap_logpath), "/tmp/tap_native_debug.log");
            }
        }
    }
    FILE *fp = fopen(g_tap_logpath, "a");
    if (!fp) return;
    struct timespec ts;
    clock_gettime(CLOCK_REALTIME, &ts);
    struct tm tm;
    localtime_r(&ts.tv_sec, &tm);
    fprintf(fp, "%02d:%02d:%02d.%03ld ", tm.tm_hour, tm.tm_min, tm.tm_sec, ts.tv_nsec / 1000000);
    va_list args;
    va_start(args, fmt);
    vfprintf(fp, fmt, args);
    va_end(args);
    fprintf(fp, "\n");
    fclose(fp);
}

/* Called from cterm.c when OSC 800 sequence is received */
void linux_audio_command(const char *cmd, size_t len) {
    tap_debug_log("linux_audio_command: called with len=%zu cmd='%.128s'", len, cmd ? cmd : "(null)");

    if (cmd == NULL || len == 0 || len >= AUDIO_CMD_BUFFER_SIZE) {
        tap_debug_log("linux_audio_command: REJECTED (null=%d len=%zu max=%d)", cmd==NULL, len, AUDIO_CMD_BUFFER_SIZE);
        return;
    }

    pthread_mutex_lock(&g_audio_lock);
    if (g_audio_queue_count < AUDIO_CMD_QUEUE_SIZE) {
        memcpy(g_audio_cmd_queue[g_audio_queue_head], cmd, len);
        g_audio_cmd_queue[g_audio_queue_head][len] = '\0';
        g_audio_cmd_lengths[g_audio_queue_head] = (int)len;
        g_audio_queue_head = (g_audio_queue_head + 1) % AUDIO_CMD_QUEUE_SIZE;
        g_audio_queue_count++;
        tap_debug_log("linux_audio_command: QUEUED OK (len=%d count=%d)", (int)len, g_audio_queue_count);
    } else {
        tap_debug_log("linux_audio_command: DROPPED (queue full, count=%d): %.64s", g_audio_queue_count, cmd);
        LOGW("Audio command dropped (queue full): %.64s", cmd);
    }
    pthread_mutex_unlock(&g_audio_lock);
}

/* Check if an audio command is pending */
SYNCTERM_API int syncterm_check_audio_command(void) {
    int ready = g_audio_queue_count > 0 ? 1 : 0;
    if (ready)
        tap_debug_log("syncterm_check_audio_command: READY (count=%d)", g_audio_queue_count);
    return ready;
}

/* Dequeue the next pending audio command string */
/* Writes to buffer, returns length written, or 0 if no command */
SYNCTERM_API int syncterm_get_audio_command(char *buffer, int buffer_len) {
    if (!buffer || buffer_len <= 0) {
        return 0;
    }

    pthread_mutex_lock(&g_audio_lock);

    if (g_audio_queue_count <= 0) {
        pthread_mutex_unlock(&g_audio_lock);
        return 0;
    }

    int len = g_audio_cmd_lengths[g_audio_queue_tail];
    g_audio_cmd_queue[g_audio_queue_tail][len] = '\0';

    if (len >= buffer_len) {
        len = buffer_len - 1;
    }
    memcpy(buffer, g_audio_cmd_queue[g_audio_queue_tail], len);
    buffer[len] = '\0';

    g_audio_queue_tail = (g_audio_queue_tail + 1) % AUDIO_CMD_QUEUE_SIZE;
    g_audio_queue_count--;

    pthread_mutex_unlock(&g_audio_lock);
    tap_debug_log("syncterm_get_audio_command: returned len=%d count=%d cmd='%.128s'", len, g_audio_queue_count, buffer);
    return len;
}

/* ------------------------------------------------------------------ */
/* Logging functions                                                   */
/* ------------------------------------------------------------------ */

/* Enable or disable session logging */
SYNCTERM_API void syncterm_set_logging_enabled(int enabled) {
    pthread_mutex_lock(&g_log_lock);
    g_logging_enabled = enabled ? 1 : 0;
    if (!enabled) {
        /* Clear buffer when logging is disabled */
        g_log_buffer_len = 0;
    }
    pthread_mutex_unlock(&g_log_lock);
    LOGI("Session logging %s", enabled ? "enabled" : "disabled");
}

/* Get and clear logged data */
/* Returns bytes written, or 0 if no data */
SYNCTERM_API int syncterm_get_logged_data(uint8_t *buffer, int buffer_len) {
    if (!buffer || buffer_len <= 0) {
        return 0;
    }

    pthread_mutex_lock(&g_log_lock);

    if (g_log_buffer_len <= 0) {
        pthread_mutex_unlock(&g_log_lock);
        return 0;
    }

    int to_copy = g_log_buffer_len;
    if (to_copy > buffer_len) {
        to_copy = buffer_len;
    }

    memcpy(buffer, g_log_buffer, to_copy);

    /* Clear the buffer after reading */
    g_log_buffer_len = 0;

    pthread_mutex_unlock(&g_log_lock);
    return to_copy;
}

/* ------------------------------------------------------------------ */
/* Scrollback functions                                                */
/* ------------------------------------------------------------------ */

/* Get scrollback buffer info */
/* Writes 3 ints: [filled_lines, total_capacity, columns] */
/* Returns 1 on success */
SYNCTERM_API int syncterm_get_scrollback_info(int *info) {
    if (!info) {
        return 0;
    }

    pthread_mutex_lock(&g_lock);

    info[0] = 0;
    info[1] = 0;
    info[2] = 0;

    if (g_cterm && g_cterm->scrollback) {
        info[0] = g_cterm->backfilled;   /* Lines actually filled */
        info[1] = g_cterm->backlines;    /* Total capacity */
        info[2] = g_cterm->backwidth;    /* Columns per line */
    }

    pthread_mutex_unlock(&g_lock);
    return 1;
}

/* Get scrollback buffer content */
/* offset: lines back from most recent (0 = most recent scrollback line) */
/* count: number of lines to retrieve */
/* Returns cells written to buffer, or 0 if invalid */
static int scrollback_buffer_impl(int offset, int count, int *buffer,
                                  int *fgout, int *bgout, int buffer_len) {
    if (!buffer || buffer_len <= 0) {
        return 0;
    }

    /* Bright-background modes: attr bit 7 is background brightness, not blink.
     * See the long note in the screen-buffer packer — history must follow the same rule or
     * scrolling back through a PETSCII session recolours it. */
    const unsigned int blink_bit = linux_ciolib_mode_no_blink() ? 0u : 0x80u;

    pthread_mutex_lock(&g_lock);

    if (!g_cterm || !g_cterm->scrollback || g_cterm->backfilled <= 0) {
        pthread_mutex_unlock(&g_lock);
        return 0;
    }

    int filled = g_cterm->backfilled;
    int capacity = g_cterm->backlines;
    int cols = g_cterm->backwidth;
    int backpos = g_cterm->backpos;  /* Next write position (one past most recent) */

    /* Validate parameters */
    if (offset < 0 || count <= 0 || offset >= filled) {
        pthread_mutex_unlock(&g_lock);
        return 0;
    }

    /* Clamp count to available lines (use subtraction to avoid overflow) */
    /* Instead of: if (offset + count > filled) which can overflow */
    /* Use: if (count > filled - offset) which is safe since offset < filled */
    if (count > filled - offset) {
        count = filled - offset;
    }

    /* Calculate size with overflow check */
    long long size_check = (long long)count * (long long)cols;
    if (size_check > INT_MAX || size_check <= 0) {
        pthread_mutex_unlock(&g_lock);
        return 0;
    }
    int size = (int)size_check;

    /* Clamp to caller's buffer size */
    if (size > buffer_len) {
        size = buffer_len;
    }

    /* Pre-calculate maximum valid cell index to avoid repeated overflow checks */
    int max_valid_cell_idx;
    if (!safe_mult_int(capacity, cols, &max_valid_cell_idx)) {
        /* Overflow in capacity * cols - shouldn't happen with valid cterm */
        pthread_mutex_unlock(&g_lock);
        return 0;
    }

    /* The scrollback is a ring buffer */
    /* backpos points to the NEXT write position (one past the most recent line) */
    /* Most recent line is at (backpos - 1 + capacity) % capacity */
    /* offset=0 means the most recent line, offset=1 means the one before that, etc. */

    struct vmem_cell *scrollback = g_cterm->scrollback;
    int arr_idx = 0;

    /* Palette for resolving indexed colours. The array itself is allocated once in the ciolib
     * state and never moves, so taking the pointer here is safe; only its contents can change
     * under us, and a one-frame-stale palette is harmless. */
    uint32_t *pal = linux_ciolib_get_palette();

    for (int line = 0; line < count; line++) {
        /* Calculate ring buffer index for this line */
        /* We're reading 'offset + line' lines back from the most recent */
        int lines_back = offset + line;

        /* Validate lines_back doesn't exceed filled (prevents reading uninitialized data) */
        if (lines_back >= filled) {
            break;  /* Stop if we've exceeded available data */
        }

        /* Safe modulo calculation for ring index */
        /* Use long long to prevent overflow in intermediate calculation */
        long long ring_calc = (long long)backpos - 1 - lines_back + (long long)capacity * 2;
        int ring_idx = (int)(ring_calc % capacity);
        if (ring_idx < 0) ring_idx += capacity;  /* Handle negative modulo */

        /* Validate ring_idx is within bounds */
        if (ring_idx < 0 || ring_idx >= capacity) {
            continue;  /* Skip invalid index */
        }

        /* Copy this line's cells */
        for (int col = 0; col < cols; col++) {
            /* Safe multiplication for cell index */
            int line_offset;
            if (!safe_mult_int(ring_idx, cols, &line_offset)) {
                continue;  /* Skip on overflow */
            }
            int cell_idx;
            if (!safe_add_int(line_offset, col, &cell_idx)) {
                continue;  /* Skip on overflow */
            }

            /* Validate cell_idx is within scrollback buffer bounds */
            if (cell_idx < 0 || cell_idx >= max_valid_cell_idx) {
                continue;  /* Skip invalid index */
            }

            /* Validate arr_idx is within output array bounds */
            if (arr_idx >= size) {
                break;  /* Stop if output array is full */
            }

            struct vmem_cell *cell = &scrollback[cell_idx];

            unsigned int ch = cell->ch & 0xFFu;
            unsigned int attr = cell->legacy_attr & 0xFFu;
            /* Use legacy_attr for fg, vmem_cell.bg for bg (matching screen buffer approach) */
            unsigned int fg = attr & 0x0Fu;
            if (cell->flags & VMEM_FLAG_UNDERLINE)
                fg |= 0x80u;
            unsigned int bg = cell->bg & 0x0Fu;
            buffer[arr_idx] = (int)(ch | ((attr & ~(0x80u ^ blink_bit)) << 8) | (fg << 16) | (bg << 24));

            /* True colour, same rule as the live screen -- history keeps its real
             * colours because the scrollback is stored as whole vmem_cells. */
            if (fgout)
                fgout[arr_idx] = (int)cell_colour_rgb(cell->fg, pal);
            if (bgout) {
                uint32_t bgcol = cell->bg;
                if (blink_bit && (attr & blink_bit) && !(bgcol & 0x80000000u))
                    bgcol &= 0x07u;
                bgout[arr_idx] = (int)cell_colour_rgb(bgcol, pal);
            }
            arr_idx++;
        }
        /* Check if we filled the array (outer loop break) */
        if (arr_idx >= size) {
            break;
        }
    }

    pthread_mutex_unlock(&g_lock);
    return arr_idx;
}

SYNCTERM_API int syncterm_get_scrollback_buffer(int offset, int count, int *buffer, int buffer_len) {
    return scrollback_buffer_impl(offset, count, buffer, NULL, NULL, buffer_len);
}

/* True-colour variant -- see syncterm_get_screen_buffer_rgb. */
SYNCTERM_API int syncterm_get_scrollback_buffer_rgb(int offset, int count, int *buffer,
                                                    int *fg, int *bg, int buffer_len) {
    return scrollback_buffer_impl(offset, count, buffer, fg, bg, buffer_len);
}

/* ------------------------------------------------------------------ */
/* Debug/diagnostic functions                                          */
/* ------------------------------------------------------------------ */

/* Dump raw vmem_cell data for the first N rows to a file.             */
/* Writes ch, legacy_attr, fg (raw uint32), bg (raw uint32), flags.    */
SYNCTERM_API int syncterm_debug_dump_screen(const char *filepath, int num_rows) {
    if (!filepath || num_rows <= 0) return 0;

    FILE *f = fopen(filepath, "w");
    if (!f) return 0;

    linux_ciolib_lock();
    struct vmem_cell *screen = linux_ciolib_get_screen_buffer();
    int w = linux_ciolib_get_screen_width();
    int h = linux_ciolib_get_screen_height();
    uint32_t *pal = linux_ciolib_get_palette();

    fprintf(f, "Screen: %dx%d\n", w, h);
    fprintf(f, "Palette[0..15]:");
    if (pal) {
        for (int i = 0; i < 16; i++)
            fprintf(f, " %06X", pal[i] & 0xFFFFFF);
    }
    fprintf(f, "\n\n");

    if (screen) {
        int rows = num_rows < h ? num_rows : h;
        for (int row = 0; row < rows; row++) {
            fprintf(f, "Row %d:\n", row);
            for (int col = 0; col < w; col++) {
                int idx = row * w + col;
                struct vmem_cell *c = &screen[idx];
                fprintf(f, "  [%02d] ch=0x%02X attr=0x%02X fg=0x%08X bg=0x%08X flags=0x%02X\n",
                        col, c->ch, c->legacy_attr, c->fg, c->bg, c->flags);
            }
        }
    }
    linux_ciolib_unlock();

    fprintf(f, "\nCursor: type=%d visible=%d x=%d y=%d\n",
            linux_ciolib_get_cursor_type(),
            linux_ciolib_is_cursor_visible(),
            linux_ciolib_get_cursor_x(),
            linux_ciolib_get_cursor_y());

    fclose(f);
    return 1;
}

/* ------------------------------------------------------------------ */
/* SSH Debug Log functions                                             */
/* ------------------------------------------------------------------ */

/* Defined in linux_stubs.c */
extern int  ssh_debug_get_log(char *buf, int buflen);
extern void ssh_debug_clear_log(void);

/* Get accumulated SSH debug log — returns bytes written */
SYNCTERM_API int syncterm_get_ssh_debug_log(char *buffer, int bufferLen) {
    return ssh_debug_get_log(buffer, bufferLen);
}

/* Clear the SSH debug log */
SYNCTERM_API void syncterm_clear_ssh_debug_log(void) {
    ssh_debug_clear_log();
}

/* ------------------------------------------------------------------ */
/* Sixel graphics functions                                            */
/* ------------------------------------------------------------------ */

/* Check if any sixel pixel data has been written */
SYNCTERM_API int syncterm_has_sixel_data(void) {
    return linux_ciolib_has_sixel_data();
}

/* Check if the sixel pixel buffer has new data since last read */
SYNCTERM_API int syncterm_is_sixel_dirty(void) {
    return linux_ciolib_is_pixel_dirty();
}

/* Get sixel pixel buffer dimensions */
/* Writes width and height; returns 1 on success */
SYNCTERM_API int syncterm_get_sixel_dimensions(int *width, int *height) {
    if (!width || !height) return 0;
    *width = linux_ciolib_get_pixel_width();
    *height = linux_ciolib_get_pixel_height();
    return 1;
}

/* Get the sixel pixel buffer as ARGB int array and clear dirty flag */
/* Returns number of pixels written, or 0 if no data */
SYNCTERM_API int syncterm_get_sixel_buffer(int *buffer, int buffer_len) {
    if (!buffer || buffer_len <= 0) return 0;

    linux_ciolib_lock();

    if (!linux_ciolib_has_sixel_data()) {
        linux_ciolib_unlock();
        return 0;
    }

    uint32_t *pixel_buf = linux_ciolib_get_pixel_buffer();
    int pw = linux_ciolib_get_pixel_width();
    int ph = linux_ciolib_get_pixel_height();

    if (!pixel_buf || pw <= 0 || ph <= 0) {
        linux_ciolib_unlock();
        return 0;
    }

    int total_pixels = pw * ph;
    if (total_pixels > buffer_len) {
        total_pixels = buffer_len;
    }

    memcpy(buffer, pixel_buf, total_pixels * sizeof(int));
    linux_ciolib_clear_pixel_dirty();

    linux_ciolib_unlock();
    return total_pixels;
}

/* Copy only the pixel layer's changed rectangle straight into the app's bitmap (dest_w x dest_h, dest_stride pixels
 * per row); full = everything. Returns pixels copied, 0 for nothing, -1 when the layer isn't dest's size (*bw x *bh
 * is the size to re-create the bitmap at). Windows syncterm_copy_sixel_pixels, 2026-09-15. */
SYNCTERM_API int syncterm_copy_sixel_pixels(uint32_t *dest, int dest_w, int dest_h, int dest_stride, int full,
                                            int *x, int *y, int *w, int *h, int *bw, int *bh) {
    if (!x || !y || !w || !h || !bw || !bh) return 0;
    return linux_ciolib_copy_dirty_pixels(dest, dest_w, dest_h, dest_stride, full, x, y, w, h, bw, bh);
}
