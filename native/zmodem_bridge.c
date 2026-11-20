/**
 * ZMODEM Linux Bridge
 *
 * Provides exported C functions to connect C#/Avalonia with the native
 * ZMODEM file transfer protocol implementation.
 *
 * Ported from zmodem_jni.c (Android JNI bridge).
 */

#include <string.h>
#include <stdlib.h>
#include <stdio.h>
#include <stdint.h>
#include <errno.h>
#include <time.h>      /* clock_gettime / localtime_r */
#include <pthread.h>
#include <unistd.h>
#include <sys/stat.h>
#include <sys/types.h>

#include "safe_math.h"

/* xpdev headers - must come before zmodem.h for type definitions */
#include "gen_defs.h"   /* BOOL, BYTE, etc. */
#include "genwrap.h"
#include "filewrap.h"
#include "dirwrap.h"    /* MAX_PATH */

/* SBBS3 protocol headers */
#include "zmodem.h"
#include "telnet.h"     /* TELNET_DO, TELNET_BINARY_TX */
extern unsigned char telnet_local_option[0x100];   /* telnet_io.c: what the board agreed to */
#include "xmodem.h"
#include "sexyz.h"   /* SEND/RECV/XMODEM/YMODEM/CRC/GMODE + the get_block result codes */
#include "conn.h"
#include "bbslist.h"

/* Connection config from syncterm_bridge.c */
extern struct bbslist g_bbs_config;

/* DLL export macro */
#ifdef SYNCTERM_BRIDGE_EXPORTS
#define SYNCTERM_API __attribute__((visibility("default")))
#else
#define SYNCTERM_API
#endif

/* Debug logging.
 *
 * Goes to stderr AND, when transfer tracing is enabled, to a plain text file -
 * because stderr is gone once the app was launched from a desktop icon, which
 * is useless for chasing a hang the user hits in normal use. See zm_trace()
 * below. */
static void zm_trace(const char* line);

/* One emitter behind all four macros: stderr, the trace file, and the
 * live status pane in the transfer dialog all see the same message, and the
 * severity is carried as a single char so the UI can colour it. */
static void zm_emit(char level, const char* msg);

#define LOGI(fmt, ...) do { char _buf[512]; snprintf(_buf, sizeof(_buf), fmt, ##__VA_ARGS__); zm_emit('I', _buf); } while(0)
/* Debug-level: trace file and stderr only, never the status pane. This is
 * the per-block chatter -- one "Sending ZCRCG data subpacket" and one
 * "Progress:" per 1024 bytes, ~123,000 lines for a 62 MB upload -- which would
 * bury the handful of lines that actually say what went wrong. SyncTERM's own
 * status pane filters the same way. */
#define LOGD(fmt, ...) do { char _buf[512]; snprintf(_buf, sizeof(_buf), fmt, ##__VA_ARGS__); zm_emit('D', _buf); } while(0)
#define LOGE(fmt, ...) do { char _buf[512]; snprintf(_buf, sizeof(_buf), fmt, ##__VA_ARGS__); zm_emit('E', _buf); } while(0)
#define LOGW(fmt, ...) do { char _buf[512]; snprintf(_buf, sizeof(_buf), fmt, ##__VA_ARGS__); zm_emit('W', _buf); } while(0)

/* Monotonic milliseconds -- the POSIX stand-in for Windows' GetTickCount64(). */
static unsigned long long zm_tick_ms(void) {
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (unsigned long long)ts.tv_sec * 1000ULL
         + (unsigned long long)(ts.tv_nsec / 1000000);
}

/* Transfer states */
typedef enum {
    TRANSFER_IDLE = 0,
    TRANSFER_RECEIVING = 1,
    TRANSFER_SENDING = 2,
    TRANSFER_COMPLETE = 3,
    TRANSFER_ERROR = 4,
    TRANSFER_CANCELLED = 5
} transfer_state_t;

/* ------------------------------------------------------------------
 * Transfer trace log
 *
 * Appends every ZMODEM/XMODEM log line to $HOME/terminator-zmodem.log.
 * Opened and closed per line ON PURPOSE: the whole point is diagnosing a HANG,
 * and a buffered FILE* loses exactly the last few lines - the ones that say
 * where it stopped - when the user kills the app.
 *
 * Truncated at the start of each transfer (see zm_trace_begin) so the file is
 * about one run, not an ever-growing pile.
 * ------------------------------------------------------------------ */
static BOOL g_trace_enabled = FALSE;  /* OFF for shipping: a 62 MB upload writes ~11 MB
                                       * here. The transfer dialog's status pane covers
                                       * the everyday case; flip to TRUE to capture a
                                       * full protocol trace when chasing a bad board. */

static void zm_trace_path(char* out, size_t len) {
    const char* home = getenv("HOME");
    snprintf(out, len, "%s/terminator-zmodem.log", home ? home : ".");
}

static void zm_trace(const char* line) {
    if (!g_trace_enabled || line == NULL)
        return;

    char path[MAX_PATH + 1];
    zm_trace_path(path, sizeof(path));

    FILE* f = fopen(path, "a");
    if (f == NULL)
        return;

    struct timespec ts;
    struct tm tmv;
    clock_gettime(CLOCK_REALTIME, &ts);
    localtime_r(&ts.tv_sec, &tmv);
    fprintf(f, "%02u:%02u:%02u.%03u %s",
            (unsigned)tmv.tm_hour, (unsigned)tmv.tm_min, (unsigned)tmv.tm_sec,
            (unsigned)(ts.tv_nsec / 1000000), line);
    fclose(f);
}

/* Start a fresh trace file and stamp what we are about to do. */
static void zm_trace_begin(const char* what) {
    if (!g_trace_enabled)
        return;

    char path[MAX_PATH + 1];
    zm_trace_path(path, sizeof(path));

    FILE* f = fopen(path, "w");   /* truncate */
    if (f != NULL) {
        time_t now = time(NULL);
        struct tm tmv;
        localtime_r(&now, &tmv);
        fprintf(f, "=== %04u-%02u-%02u %02u:%02u:%02u  %s ===\n",
                (unsigned)tmv.tm_year + 1900, (unsigned)tmv.tm_mon + 1, (unsigned)tmv.tm_mday,
                (unsigned)tmv.tm_hour, (unsigned)tmv.tm_min, (unsigned)tmv.tm_sec, what);
        fclose(f);
    }
}

/* ------------------------------------------------------------------
 * Live status ring for the transfer dialog
 *
 * SyncTERM shows a running protocol log under its progress bar, which is the
 * single most useful thing on that screen when a transfer misbehaves -- it is
 * where "board went quiet after ZEOF" becomes visible without a trace file.
 * This is the same feed.
 *
 * A fixed ring, drained by the UI poll: the producer is the transfer thread
 * and the consumer is the Avalonia poll loop, so it is guarded by a pthread
 * mutex -- statically initialisable, so no init call is needed and a log line emitted
 * before syncterm_transfer_init() is still safe.
 *
 * Overrun drops the OLDEST lines and says so, rather than blocking the
 * transfer thread on a slow UI.
 * ------------------------------------------------------------------ */
#define ZM_UILOG_LINES     512
#define ZM_UILOG_LINE_MAX  208

static pthread_mutex_t g_uilog_lock = PTHREAD_MUTEX_INITIALIZER;
static char     g_uilog[ZM_UILOG_LINES][ZM_UILOG_LINE_MAX];
static unsigned g_uilog_written = 0;   /* total ever produced */
static unsigned g_uilog_taken   = 0;   /* total handed to the UI */

static void zm_uilog_push(char level, const char* msg) {
    if (level == 'D')   /* per-block chatter: trace file only */
        return;

    pthread_mutex_lock(&g_uilog_lock);

    char* slot = g_uilog[g_uilog_written % ZM_UILOG_LINES];
    slot[0] = level;
    /* Line is level char + text + NUL. */
    strncpy(slot + 1, msg, ZM_UILOG_LINE_MAX - 2);
    slot[ZM_UILOG_LINE_MAX - 1] = '\0';

    /* Newlines would break the one-line-per-entry framing the UI relies on. */
    for (char* q = slot + 1; *q; q++) {
        if (*q == '\r' || *q == '\n')
            *q = ' ';
    }

    g_uilog_written++;

    /* Producer outran the consumer: forget the oldest rather than stall. */
    if (g_uilog_written - g_uilog_taken > ZM_UILOG_LINES)
        g_uilog_taken = g_uilog_written - ZM_UILOG_LINES;

    pthread_mutex_unlock(&g_uilog_lock);
}

static void zm_emit(char level, const char* msg) {
    const char* tag = (level == 'E') ? "SyncTERM-ZMODEM ERROR: "
                    : (level == 'W') ? "SyncTERM-ZMODEM WARN: "
                                     : "SyncTERM-ZMODEM: ";
    char out[ZM_UILOG_LINE_MAX + 320];
    snprintf(out, sizeof(out), "%s%s\n", tag, msg);
    fputs(out, stderr);
    zm_trace(out);
    zm_uilog_push(level, msg);
}

/**
 * Drain new status lines into <paramref name="buffer"/>, one per line, each
 * prefixed with its severity char ('I', 'W', 'E'). Returns bytes written.
 * Only ever returns lines not yet taken, so the UI can poll as often as it
 * likes without repeating itself.
 */
SYNCTERM_API int syncterm_get_transfer_log(char* buffer, int bufferLen) {
    if (buffer == NULL || bufferLen <= 1)
        return 0;

    int used = 0;
    pthread_mutex_lock(&g_uilog_lock);

    while (g_uilog_taken < g_uilog_written) {
        const char* line = g_uilog[g_uilog_taken % ZM_UILOG_LINES];
        int len = (int)strlen(line);

        /* Leave room for the line, its newline and the terminator. */
        if (used + len + 2 > bufferLen)
            break;

        memcpy(buffer + used, line, (size_t)len);
        used += len;
        buffer[used++] = '\n';
        g_uilog_taken++;
    }

    pthread_mutex_unlock(&g_uilog_lock);
    buffer[used] = '\0';
    return used;
}

/** Drop anything pending — called when a new transfer dialog opens. */
SYNCTERM_API void syncterm_clear_transfer_log(void) {
    pthread_mutex_lock(&g_uilog_lock);
    g_uilog_taken = g_uilog_written;
    pthread_mutex_unlock(&g_uilog_lock);
}

/* Transfer context */
typedef struct {
    zmodem_t zm;
    xmodem_t xm;
    transfer_state_t state;
    int cancelled;
    int64_t current_pos;
    int64_t total_size;
    int current_file_index;   /* 1-based index of file being sent in a batch (0 = n/a) */
    int total_files;          /* total files in the current batch (0/1 = single) */
    char current_file[MAX_PATH + 1];
    char download_dir[MAX_PATH + 1];
    char error_message[256];
    int log_level;
    pthread_mutex_t lock;
} transfer_context_t;

/* Global transfer context */
static transfer_context_t g_transfer = {
    .state = TRANSFER_IDLE,
    .cancelled = 0,
    .current_pos = 0,
    .total_size = 0,
    .current_file = "",
    .download_dir = "",
    .error_message = "",
    .log_level = LOG_DEBUG   /* diagnostic build - see zm_trace() */
};

static int g_transfer_initialized = 0;

/* Batch upload queue.
 *
 * Populated by the UI thread (syncterm_upload_queue_add) before a batch send
 * is kicked off, then read by the transfer thread inside
 * syncterm_zmodem_send_batch(). Population and consumption never overlap
 * (the UI adds all files, THEN starts the send), so the array itself needs no
 * lock — only the per-file progress counters (in g_transfer) are shared with
 * the poll thread and those are guarded by g_transfer.lock. */
#define MAX_UPLOAD_FILES 256
typedef struct {
    char paths[MAX_UPLOAD_FILES][MAX_PATH + 1];
    int count;
} upload_batch_t;
static upload_batch_t g_upload_batch = { .count = 0 };

/* Create directory path recursively (like mkdir -p) */
static int mkpath_recursive(const char* path) {
    char tmp[MAX_PATH + 1];
    strncpy(tmp, path, MAX_PATH);
    tmp[MAX_PATH] = '\0';
    size_t len = strlen(tmp);

    /* Strip trailing slashes */
    while (len > 0 && (tmp[len - 1] == '/' || tmp[len - 1] == '\\'))
        tmp[--len] = '\0';

    if (len == 0) return 0;

    for (size_t i = 1; i <= len; i++) {
        if (tmp[i] == '/' || tmp[i] == '\0') {
            char saved = tmp[i];
            tmp[i] = '\0';

            int rc = mkdir(tmp, 0755);
            if (rc != 0 && errno != EEXIST) {
                return -1;
            }

            tmp[i] = saved;
            if (saved == '\0') break;
        }
    }
    return 0;
}

/* ------------------------------------------------------------------ *
 * Transmit / receive staging buffers.
 *
 * The ZMODEM engine drives I/O one byte at a time. Handing every single
 * byte to conn_send()/conn_recv_upto() is not just slow (a malloc and a
 * mutex acquisition per byte on Telnet links) -- it is incorrect, because
 * the Telnet parse callbacks in conn.c do not map 1 byte to 1 byte:
 *
 *   TX: st_telnet_expand() doubles IAC and (when binary mode is not
 *       negotiated) turns CR into CR LF, so conn_send() can return 2 for a
 *       single input byte.
 *   RX: telnet_interpret() swallows IAC sequences entirely and holds a CR
 *       back until it sees the following byte, so conn_recv_upto() can
 *       return 0 -- or 2, overrunning a 1-byte destination buffer.
 *
 * SyncTERM's own transfer code (term.c send_byte/flush_send/recv_byte)
 * stages both directions through buffers for exactly this reason; these
 * mirror that behaviour.
 * ------------------------------------------------------------------ */
#define ZM_TX_BUFSIZE 16384
static unsigned char g_tx_buf[ZM_TX_BUFSIZE];
static size_t        g_tx_buf_len = 0;

/* Bytes the Telnet RX parser produced beyond the one we were asked for. */
static unsigned char g_rx_pending[8];
static int           g_rx_pending_len = 0;
static int           g_rx_pending_pos = 0;

static void zmodem_reset_io_buffers(void) {
    g_tx_buf_len = 0;
    g_rx_pending_len = 0;
    g_rx_pending_pos = 0;
}

/* ------------------------------------------------------------------
 * End-of-file delivery guard
 *
 * Some boards (cnet is the one that caught this) never send the ZRINIT that
 * is supposed to follow our ZEOF. They consider the upload finished, print
 * their post-upload screen, and drop back to the command prompt.
 *
 * zmodem_send_file() reads that silence as a mid-stream data error and does
 * the protocol-correct thing for one: rewind to the last ACKed position and
 * re-send with a halved block size. But on a streaming (ZCRCG) transfer the
 * only ACK ever received is the one for the opening handshake block, so the
 * rewind target is ~1024 -- the engine restarts a whole multi-megabyte file
 * from the top, max_errors (10) times over, and every one of those bytes now
 * lands on the board's COMMAND PROMPT instead of in its receiver.
 *
 * So: once the last byte of the file has gone out, a timeout no longer means
 * "error", it means "the board stopped talking after taking delivery". Arm a
 * wall-clock deadline at that point and enforce it through the cancel
 * callback -- the only predicate the engine consults in its innermost receive
 * loops -- then report the transfer as the success it actually was.
 *
 * The wait is deliberately LONG. A board that is still writing, scanning or
 * dupe-checking a multi-megabyte upload can block its receiver well past the
 * normal recv_timeout, and the file is already delivered -- there is nothing
 * to gain by giving up early and everything to lose, because giving up is
 * what triggers the destructive re-send. So after EOF we raise the engine's
 * own recv_timeout too, otherwise it would hit 10s/30s and start rewinding
 * long before our deadline is reached.
 * ------------------------------------------------------------------ */
#define POST_EOF_WAIT_BASE_SECS    60   /* fixed overhead, any size */
#define POST_EOF_WAIT_SECS_PER_MB   5   /* measured cnet: ~2.4s/MB; 2x margin */
#define POST_EOF_WAIT_MAX_SECS     600  /* never wait more than 10 minutes */

/* How long to give the receiver to acknowledge, scaled by how much it has to
 * chew through. Measured against cnet: a 62 MB upload took 146 seconds before
 * it sent ZRINIT, i.e. ~2.4 s/MB, so a FIXED timeout that suits one file size
 * fails the next one up. */
static unsigned post_eof_wait_secs(int64_t file_size) {
    int64_t mb = file_size / (1024 * 1024);
    int64_t secs = POST_EOF_WAIT_BASE_SECS + mb * POST_EOF_WAIT_SECS_PER_MB;
    if (secs > POST_EOF_WAIT_MAX_SECS)
        secs = POST_EOF_WAIT_MAX_SECS;
    return (unsigned)secs;
}

static unsigned g_eof_wait_secs = 0;             /* window actually chosen */
static unsigned long long g_eof_deadline = 0;    /* 0 = disarmed */
static BOOL g_eof_delivered = FALSE;             /* whole file reached the wire */
static BOOL g_eof_completed_on_timeout = FALSE;  /* deadline fired; report success */
static int64_t g_progress_high_water = -1;       /* furthest position reached */

static void zmodem_eof_guard_reset(void) {
    g_eof_wait_secs = 0;
    g_eof_deadline = 0;
    g_eof_delivered = FALSE;
    g_eof_completed_on_timeout = FALSE;
    g_progress_high_water = -1;
}

/* Forward declarations for callbacks */
static void zmodem_watchdog_kick(void);
static void zmodem_watchdog_arm(void);
static void zmodem_watchdog_disarm(void);
static void zmodem_trace_begin_if_first(void);
static int zmodem_lputs_cb(void* cbdata, int level, const char* str);
static int zmodem_send_byte_cb(void* cbdata, BYTE ch, unsigned timeout);
static int zmodem_recv_byte_cb(void* cbdata, unsigned timeout);
static void zmodem_progress_cb(void* cbdata, int64_t current_pos);
static BOOL zmodem_is_connected_cb(void* cbdata);
static BOOL zmodem_is_cancelled_cb(void* cbdata);
static BOOL zmodem_data_waiting_cb(void* cbdata, unsigned timeout);
static void zmodem_flush_cb(void* cbdata);

/* External connection API - declared in conn.h, implemented in conn.c */
/* Note: conn_connected(), conn_send(), conn_recv_upto(), conn_data_waiting() */
/* are all declared in conn.h which is included above */

/* ------------------------------------------------------------------
 * Transfer diagnostics -- level 'G' lines in the transfer window.
 *
 * The engine's detail (which frame the board sent, where it asked us to
 * resend from, the bytes it threw away) is all LOG_DEBUG, which only ever
 * reaches the trace file. This picks out the parts that explain a failure
 * and turns them into a handful of plain lines, so a bad transfer can be
 * diagnosed from the window without a trace build. Nothing here is per
 * block except block-size CHANGES, and runs of discarded bytes collapse to
 * one line. The shared engine (src/sbbs3/zmodem.c) is untouched.
 * ------------------------------------------------------------------ */
static int g_zmodem_8k;     /* Transfer > ZMODEM 8K Blocks, defined further down */

#define LOGG(fmt, ...) do { char _buf[512]; snprintf(_buf, sizeof(_buf), fmt, ##__VA_ARGS__); zm_emit('G', _buf); } while(0)

static struct {
    BOOL active;
    unsigned long long start_tick;
    unsigned long long last_rx_tick;    /* last byte the board sent us */
    int64_t last_pos;                   /* previous progress position */
    uint64_t moved;                     /* forward movement, resends included */
    unsigned last_block, largest_block; /* sends: our block size */
    unsigned largest_rx_block;          /* receives: largest subpacket */
    unsigned zrpos, znak, other_frames, crc_errors, timeouts;
    unsigned purged, purged_total;
    char purged_sample[48];
} g_diag;

static void diag_bytes(char* out, size_t len, uint64_t n) {
    if (n < 1024)
        snprintf(out, len, "%llu B", (unsigned long long)n);
    else if (n < 1024ULL * 1024)
        snprintf(out, len, "%.1f KB", n / 1024.0);
    else
        snprintf(out, len, "%.1f MB", n / 1048576.0);
}

static void diag_block(char* out, size_t len, unsigned n) {
    if (n >= 1024 && n % 1024 == 0)
        snprintf(out, len, "%uK", n / 1024);
    else
        snprintf(out, len, "%u bytes", n);
}

static double diag_silence_secs(void) {
    if (g_diag.last_rx_tick == 0)
        return (zm_tick_ms() - g_diag.start_tick) / 1000.0;
    return (zm_tick_ms() - g_diag.last_rx_tick) / 1000.0;
}

static BOOL diag_sending(void) {
    pthread_mutex_lock(&g_transfer.lock);
    BOOL sending = (g_transfer.state == TRANSFER_SENDING);
    pthread_mutex_unlock(&g_transfer.lock);
    return sending;
}

static void diag_flush_purge(void) {
    if (g_diag.purged == 0)
        return;
    LOGG("Discarded %u stray byte%s from the board: %s", g_diag.purged,
         g_diag.purged == 1 ? "" : "s", g_diag.purged_sample);
    g_diag.purged = 0;
    g_diag.purged_sample[0] = '\0';
}

static void diag_begin(BOOL is_ssh) {
    memset(&g_diag, 0, sizeof(g_diag));
    g_diag.active = TRUE;
    g_diag.start_tick = zm_tick_ms();
    /* The board's answer, not our request: conn_api.binary_mode is set the moment we ASK (conn_binary_mode_on) */
    LOGG("Link: %s, binary mode %s, IAC escaping %s, CR escaping %s, timeouts %us, uploads up to %s blocks",
         is_ssh ? "SSH" : "Telnet",
         is_ssh ? "on" : telnet_local_option[TELNET_BINARY_TX] == TELNET_DO ? "on" : "not agreed yet",
         g_transfer.zm.escape_telnet_iac ? "on" : "off",
         g_transfer.zm.escape_cr ? "on" : "off",
         g_transfer.zm.recv_timeout,
         g_zmodem_8k ? "8K" : "1K");
}

/* Called with every engine log line, before it is routed by level */
static void diag_on_engine_line(int level, const char* str) {
    static const char purge_tag[] = "Purging data in receive buffer: ";
    static const char frame_tag[] = "zmodem_recv_header frame: ";
    const char* p;

    if (!g_diag.active || str == NULL)
        return;

    if ((p = strstr(str, purge_tag)) != NULL) {
        size_t have = strlen(g_diag.purged_sample);
        p += sizeof(purge_tag) - 1;
        g_diag.purged++;
        g_diag.purged_total++;
        if (have + strlen(p) + 2 < sizeof(g_diag.purged_sample)) {
            if (have)
                strcat(g_diag.purged_sample, " ");
            strcat(g_diag.purged_sample, p);
        } else if (have + 5 < sizeof(g_diag.purged_sample) &&
                   strstr(g_diag.purged_sample, " ...") == NULL) {
            strcat(g_diag.purged_sample, " ...");
        }
        return;
    }
    diag_flush_purge();

    /* Receives: remember the largest subpacket the board sent */
    if ((p = strstr(str, "Successful receipt of subpacket type: ")) != NULL) {
        const char* paren = strrchr(str, '(');
        unsigned n;
        if (paren != NULL && sscanf(paren, "(%u bytes)", &n) == 1 &&
            n > g_diag.largest_rx_block)
            g_diag.largest_rx_block = n;
        return;
    }

    if (strstr(str, "CRC ERROR") != NULL) {
        g_diag.crc_errors++;
        LOGG("CRC error: %s", str);
        return;
    }

    /* One timeout logs several lines; count the raw one: "... TIMEOUT (N seconds)" */
    if (level <= LOG_WARNING && strstr(str, " TIMEOUT (") != NULL) {
        g_diag.timeouts++;
        LOGG("Board silent for %.1fs (timeout)", diag_silence_secs());
        return;
    }

    /* Frames the board sends while we upload. The receive side sees a frame
     * per data block, so only sends are reported. */
    if ((p = strstr(str, frame_tag)) != NULL && diag_sending()) {
        char want[32], had[32];
        p += sizeof(frame_tag) - 1;
        if (strcmp(p, "ZACK") == 0 || strcmp(p, "ZRINIT") == 0 ||
            strcmp(p, "ZFIN") == 0)
            return;
        diag_bytes(had, sizeof(had), (uint64_t)g_transfer.zm.current_file_pos);
        if (strcmp(p, "ZRPOS") == 0) {
            if (g_progress_high_water <= 0)
                return;     /* the normal "start here" before any data */
            g_diag.zrpos++;
            diag_bytes(want, sizeof(want), (uint64_t)g_transfer.zm.rxd_header_pos);
            LOGG("Board asked for a resend from %s (we had sent %s)", want, had);
            return;
        }
        if (strcmp(p, "ZNAK") == 0) {
            g_diag.znak++;
            LOGG("Board rejected our last header (ZNAK) at %s", had);
            return;
        }
        g_diag.other_frames++;
        LOGG("Board sent %s at %s", p, had);
    }
}

/* Sends: report block-size changes (the ramp up, and drops after errors) */
static void diag_on_progress(int64_t current_pos, BOOL sending) {
    if (!g_diag.active)
        return;
    if (current_pos > g_diag.last_pos)
        g_diag.moved += (uint64_t)(current_pos - g_diag.last_pos);
    g_diag.last_pos = current_pos;

    if (!sending)
        return;
    unsigned bs = g_transfer.zm.block_size;
    if (bs != g_diag.last_block) {
        char blk[32];
        diag_block(blk, sizeof(blk), bs);
        if (g_diag.last_block != 0 && bs < g_diag.last_block)
            LOGG("Block size dropped to %s after an error", blk);
        else
            LOGG("Block size now %s", blk);
        g_diag.last_block = bs;
        if (bs > g_diag.largest_block)
            g_diag.largest_block = bs;
    }
}

static void diag_end(void) {
    char moved[32], rate[32], blk[32];
    double secs;

    if (!g_diag.active)
        return;
    diag_flush_purge();
    secs = (zm_tick_ms() - g_diag.start_tick) / 1000.0;
    diag_bytes(moved, sizeof(moved), g_diag.moved);
    diag_bytes(rate, sizeof(rate), secs > 0 ? (uint64_t)(g_diag.moved / secs) : 0);
    LOGG("Summary: %u error%s (%u resend, %u NAK, %u CRC, %u timeout, %u other), "
         "%u stray byte%s discarded, %s moved in %.0fs (%s/s)",
         g_transfer.zm.errors, g_transfer.zm.errors == 1 ? "" : "s",
         g_diag.zrpos, g_diag.znak, g_diag.crc_errors, g_diag.timeouts,
         g_diag.other_frames, g_diag.purged_total,
         g_diag.purged_total == 1 ? "" : "s", moved, secs, rate);
    if (g_diag.largest_block) {
        diag_block(blk, sizeof(blk), g_diag.largest_block);
        LOGG("Receiver: %s-duplex, overlapped I/O %s, CRC-%u, buffer %s; largest block sent %s",
             g_transfer.zm.can_full_duplex ? "full" : "half",
             g_transfer.zm.can_overlap_io ? "yes" : "no",
             g_transfer.zm.can_fcs_32 ? 32 : 16,
             g_transfer.zm.recv_bufsize ? "limited" : "unlimited", blk);
    } else if (g_diag.largest_rx_block) {
        diag_block(blk, sizeof(blk), g_diag.largest_rx_block);
        LOGG("Largest block received %s", blk);
    }
    g_diag.active = FALSE;
}

/**
 * ZMODEM Callback: Log output
 */
static int zmodem_lputs_cb(void* cbdata, int level, const char* str) {
    (void)cbdata;

    diag_on_engine_line(level, str);

    switch (level) {
        case LOG_EMERG:
        case LOG_ALERT:
        case LOG_CRIT:
        case LOG_ERR:
            LOGE("%s", str);
            break;
        case LOG_WARNING:
            LOGW("%s", str);
            break;
        case LOG_NOTICE:
        case LOG_INFO:
            LOGI("%s", str);
            break;
        case LOG_DEBUG:
        default:
            LOGD("%s", str);
            break;
    }

    return 0;
}

/**
 * ZMODEM Callback: Send a byte with timeout
 */
static void zmodem_flush_tx(void) {
    if (g_tx_buf_len == 0) {
        return;
    }

    if (!conn_connected()) {
        g_tx_buf_len = 0;
        return;
    }

    /* conn_send() returns the count of bytes it queued *after* Telnet
     * expansion, not the number of ours it consumed: st_telnet_expand()
     * doubles every IAC (0xFF) on the way out, so handing it n bytes can
     * legitimately come back as anything from n to 2n. That matters far more
     * for X/YMODEM than for ZMODEM -- ZMODEM escapes IAC itself before its
     * bytes ever reach us, while X/YMODEM puts RAW FILE BYTES through here,
     * so a binary file full of 0xFF exercises the expansion on every block.
     *
     * ⚠️ Which is why there is deliberately no partial-write re-staging here.
     * A short return cannot be mapped back onto our input: we would have to
     * know where in OUR buffer the expanded cut landed, and st_telnet_expand()
     * is stateful (it carries CR/LF state across calls), so re-running it to
     * find out would itself corrupt the stream. Re-sending a mis-cut prefix is
     * strictly worse than failing.
     *
     * In practice a short return means the link is dead, not busy:
     * conn_buf_wait_free() waits for room for the WHOLE expanded payload and
     * only returns less after its timeout -- 120 seconds here, by which point
     * every protocol timeout (10-30s) has long since fired and the transfer is
     * over anyway. So treat it as fatal, say so, and drop the staged bytes. */
    size_t staged = g_tx_buf_len;
    int sent = conn_send(g_tx_buf, staged, 120 * 1000);

    /* ⚠️ ALWAYS drop the staged bytes, even on failure. Keeping them would leave
     * g_tx_buf_len at its maximum, and the very next zmodem_send_byte_cb() would
     * write at g_tx_buf[ZM_TX_BUFSIZE] -- past the end of the array -- and push
     * g_tx_buf_len beyond the "== sizeof" flush trigger, so it would never flush
     * again and every subsequent byte would walk further off the end. The caller
     * reports the failure through the usual error path instead. */
    g_tx_buf_len = 0;

    if (sent <= 0) {
        LOGE("flush_tx: conn_send accepted 0 of %zu bytes - dropping (link is dead)", staged);
        return;
    }

    /* sent >= staged is the best "it all went" test available -- the expanded
     * length is never smaller than what we handed over, so anything below it is
     * unambiguously a short write. (A cut that lands after an expanded IAC can
     * still read as >= staged; that needs a two-minute stall to happen at all,
     * and the transfer is already lost by then.) */
    if ((size_t)sent < staged) {
        LOGE("flush_tx: short write -- conn_send queued %d of >=%zu bytes; "
             "connection has stalled, abandoning the staged bytes", sent, staged);
    }
}

static int zmodem_send_byte_cb(void* cbdata, BYTE ch, unsigned timeout) {
    (void)cbdata;
    (void)timeout;

    if (!conn_connected()) {
        return -1;
    }

    g_tx_buf[g_tx_buf_len++] = ch;
    if (g_tx_buf_len == sizeof(g_tx_buf)) {
        zmodem_flush_tx();
    }

    /* Success unless the buffer is still completely full, which means the
     * flush above could not push a single byte out. */
    return (g_tx_buf_len < sizeof(g_tx_buf)) ? 0 : -1;
}

/**
 * ZMODEM Callback: Receive a byte with timeout
 */
static int zmodem_recv_byte_cb(void* cbdata, unsigned timeout) {
    (void)cbdata;
    static unsigned recv_call_count = 0;

    recv_call_count++;

    /* Serve anything the Telnet parser handed back beyond the byte we asked
     * for on a previous call (e.g. a held CR released by the byte after it). */
    if (g_rx_pending_pos < g_rx_pending_len) {
        return g_rx_pending[g_rx_pending_pos++];
    }
    g_rx_pending_len = g_rx_pending_pos = 0;

    /* Never block waiting for a reply to something still sitting in the
     * staging buffer. */
    zmodem_flush_tx();

    if (!conn_connected()) {
        LOGE("recv_byte: conn_connected() returned false! (call #%u)", recv_call_count);
        return -1;
    }

    /* Cap timeout to prevent overflow in multiplication (max ~4294 seconds) */
    if (timeout > 4000000) timeout = 4000000;
    unsigned long long deadline = zm_tick_ms() + (unsigned long long)timeout * 1000ULL;

    for (;;) {
        /* conn_recv_upto() only reserves expansion headroom when asked for
         * more than one byte, so always give it a buffer with slack. */
        BYTE buf[sizeof(g_rx_pending)];
        unsigned long long now = zm_tick_ms();
        unsigned wait_ms = (now >= deadline) ? 0 : (unsigned)(deadline - now);

        int received = conn_recv_upto(buf, 1, wait_ms);

        if (received > 0) {
            if (received > (int)sizeof(g_rx_pending))
                received = (int)sizeof(g_rx_pending);
            memcpy(g_rx_pending, buf, (size_t)received);
            g_rx_pending_len = received;
            g_rx_pending_pos = 1;
            g_diag.last_rx_tick = zm_tick_ms();
            return g_rx_pending[0];
        }

        /* received == 0 means bytes arrived but the Telnet parser consumed
         * them all (an IAC sequence, or a CR being held for CR/LF
         * disambiguation). That is NOT a receive failure -- reporting it as
         * one aborts the ZMODEM frame in progress. Keep waiting instead.
         *
         * This is what makes a raw 0xFF inside X/YMODEM file data survive: the
         * sender doubles it, so the wire carries IAC IAC. The first call reads
         * the first 0xFF, telnet_interpret() holds it as the start of a command
         * and yields nothing -- received == 0, and we loop rather than report a
         * timeout. The second call reads the second 0xFF, the parser resolves
         * IAC IAC to a single literal 0xFF, and that one byte is what the
         * protocol sees. Returning -1 on the first of the pair would lose every
         * 0xFF in the file. */
        if (received == 0 && zm_tick_ms() < deadline && conn_connected()) {
            continue;
        }

        return -1;  /* Timeout or error */
    }
}

/**
 * ZMODEM Callback: Progress update
 */
static void zmodem_progress_cb(void* cbdata, int64_t current_pos) {
    (void)cbdata;

    /* Only genuine forward movement counts as progress. A retry that rewinds
     * and re-sends ground we already covered must NOT refresh the watchdog --
     * that is exactly how a 30s retry loop kept a 60s deadline permanently out
     * of reach and turned a dead board into a five-minute hang. */
    if (current_pos > g_progress_high_water) {
        g_progress_high_water = current_pos;
        zmodem_watchdog_kick();
    }

    /* Last byte of the file is on the wire: from here on, silence means the
     * board took delivery and stopped talking, not that the data went bad.
     *
     * SENDS ONLY. This callback also fires on a receive, where "the last byte
     * arrived" says nothing about a silent board -- and a receive has no
     * per-file hook to reset the guard, so in a batch DOWNLOAD file 1's
     * deadline cut file 2 off mid-stream and reported it complete (19 B then
     * 1.5 MB at 15 KB/s stopped at 931,840 bytes). A receive never arms it. */
    pthread_mutex_lock(&g_transfer.lock);
    BOOL sending = (g_transfer.state == TRANSFER_SENDING);
    pthread_mutex_unlock(&g_transfer.lock);
    diag_on_progress(current_pos, sending);
    int64_t file_size = g_transfer.zm.current_file_size;
    if (sending && !g_eof_delivered && file_size > 0 && current_pos >= file_size) {
        g_eof_delivered = TRUE;
        g_eof_wait_secs = post_eof_wait_secs(file_size);
        g_eof_deadline = zm_tick_ms() +
                         ((unsigned long long)g_eof_wait_secs + 5) * 1000ULL;
        /* Stop the engine timing out (and rewinding) before our deadline. */
        g_transfer.zm.recv_timeout = g_eof_wait_secs;
        /* Hand the wait over to the EOF deadline. The idle watchdog measures
         * PROGRESS, and after the last byte there is none by definition -- left
         * armed it fires at 60s every time and pre-empts this guard entirely. */
        zmodem_watchdog_disarm();
        LOGI("Whole file delivered (%lld bytes); waiting up to %d seconds for "
             "the receiver to acknowledge", (long long)file_size,
             g_eof_wait_secs);
    }

    int64_t total_size;
    char current_file[MAX_PATH + 1];

    pthread_mutex_lock(&g_transfer.lock);
    g_transfer.current_pos = current_pos;

    /* Also update from zmodem structure */
    g_transfer.total_size = g_transfer.zm.current_file_size;
    if (g_transfer.zm.current_file_name[0] != '\0') {
        strncpy(g_transfer.current_file, g_transfer.zm.current_file_name, MAX_PATH);
        g_transfer.current_file[MAX_PATH] = '\0';
    }

    /* Copy for logging outside critical section */
    total_size = g_transfer.total_size;
    strncpy(current_file, g_transfer.current_file, MAX_PATH);
    current_file[MAX_PATH] = '\0';
    pthread_mutex_unlock(&g_transfer.lock);

    LOGD("Progress: %lld / %lld - %s",
         (long long)current_pos,
         (long long)total_size,
         current_file);
}

/**
 * ZMODEM Callback: Check if connected
 */
static BOOL zmodem_is_connected_cb(void* cbdata) {
    (void)cbdata;
    BOOL connected = conn_connected() ? TRUE : FALSE;
    if (!connected) {
        LOGE("is_connected_cb: returning FALSE!");
    }
    return connected;
}

/* Wall-clock deadline for the end-of-session ZFIN exchange; 0 = not armed.
 * See zmodem_finish_session() for why this has to exist. */
static unsigned long long g_zfin_deadline = 0;

/* ------------------------------------------------------------------
 * IDLE WATCHDOG - the backstop the ZFIN deadline alone does not provide.
 *
 * zmodem_get_zfin() is NOT the only place that can hang forever. EVERY call to
 * zmodem_recv_header() shares the same flaw: its scan loop in
 * zmodem_recv_header_raw() only gives up after recv_timeout seconds of TOTAL
 * SILENCE, so a board that keeps talking - printing its post-upload screen, or
 * echoing junk back at us - feeds that loop indefinitely. The wait for ZRINIT
 * right after ZEOF (zmodem.c:1717) is exactly such a call, and it sits BEFORE
 * the ZFIN handshake, so the ZFIN deadline never gets a chance to fire.
 *
 * Rather than bound each call site, bound PROGRESS: the progress callback fires
 * on every block read from the file, so during a healthy transfer this is
 * refreshed constantly and never trips. It only trips when we have sent
 * everything and are stuck waiting on a board that will not answer.
 *
 * 60s is 6x the 10s protocol recv_timeout - long enough that no healthy slow
 * board trips it, short enough that the user is not staring at a dead dialog.
 * ------------------------------------------------------------------ */
#define TRANSFER_IDLE_LIMIT_MS 60000
static unsigned long long g_last_progress_ms = 0;   /* 0 = watchdog disarmed */
static BOOL g_trace_started = FALSE;

static void zmodem_trace_begin_if_first(void) {
    if (!g_trace_started) {
        zm_trace_begin("transfer session");
        g_trace_started = TRUE;
    }
}

static void zmodem_watchdog_arm(void)    { g_last_progress_ms = zm_tick_ms(); }
static void zmodem_watchdog_disarm(void) { g_last_progress_ms = 0; }
static void zmodem_watchdog_kick(void)   { if (g_last_progress_ms) g_last_progress_ms = zm_tick_ms(); }

/**
 * ZMODEM Callback: Check if cancelled
 */
static BOOL zmodem_is_cancelled_cb(void* cbdata) {
    (void)cbdata;

    /* The ZFIN watchdog rides on the cancel check because that is the ONLY
     * predicate the engine consults inside its innermost receive loops. */
    if (g_zfin_deadline != 0 && zm_tick_ms() > g_zfin_deadline) {
        return TRUE;
    }

    /* The file is already delivered and the board has gone quiet. Break out
     * before zmodem_send_file() can "recover" by re-sending it. */
    if (g_eof_deadline != 0 && zm_tick_ms() > g_eof_deadline) {
        if (!g_eof_completed_on_timeout) {
            g_eof_completed_on_timeout = TRUE;
            LOGW("No acknowledgement of ZEOF after %d seconds. The whole file "
                 "was sent, so this counts as delivered -- closing instead of "
                 "re-sending it.", g_eof_wait_secs);
        }
        return TRUE;
    }

    if (g_last_progress_ms != 0 &&
        zm_tick_ms() - g_last_progress_ms > TRANSFER_IDLE_LIMIT_MS) {
        LOGE("IDLE WATCHDOG: no transfer progress for %d seconds - giving up. "
             "The board stopped answering; if the file shows as received on the "
             "board, it arrived and only the handshake was lost.",
             TRANSFER_IDLE_LIMIT_MS / 1000);
        return TRUE;
    }

    pthread_mutex_lock(&g_transfer.lock);
    int cancelled = g_transfer.cancelled;
    pthread_mutex_unlock(&g_transfer.lock);

    return cancelled ? TRUE : FALSE;
}

/**
 * ZMODEM Callback: Check if data is waiting
 */
static BOOL zmodem_data_waiting_cb(void* cbdata, unsigned timeout) {
    (void)cbdata;

    /* Bytes already decoded by the Telnet parser count as waiting data. */
    if (g_rx_pending_pos < g_rx_pending_len) {
        return TRUE;
    }

    /* Same reasoning as in recv_byte: get staged output onto the wire before
     * waiting to hear back about it. */
    zmodem_flush_tx();

    if (!conn_connected()) {
        return FALSE;
    }

    /* Cap timeout to prevent overflow in multiplication (max ~4294 seconds) */
    if (timeout > 4000000) timeout = 4000000;
    /* Poll for data with timeout */
    unsigned elapsed = 0;
    while (elapsed < timeout * 1000) {
        size_t waiting = conn_data_waiting();
        if (waiting > 0) {
            return TRUE;
        }

        /* Check for cancellation */
        pthread_mutex_lock(&g_transfer.lock);
        int cancelled = g_transfer.cancelled;
        pthread_mutex_unlock(&g_transfer.lock);
        if (cancelled) {
            return FALSE;
        }

        usleep(10000);  /* 10ms */
        elapsed += 10;
    }

    return conn_data_waiting() > 0 ? TRUE : FALSE;
}

/**
 * ZMODEM Callback: Flush output
 */
static void zmodem_flush_cb(void* cbdata) {
    (void)cbdata;
    /* ZMODEM calls this at every frame/subpacket boundary -- i.e. exactly
     * where the staged bytes have to be on the wire before we wait for the
     * peer's response. */
    zmodem_flush_tx();
}

/**
 * Initialize the transfer system
 * Returns: 1 on success, 0 on failure
 */
SYNCTERM_API int syncterm_transfer_init(void) {
    LOGD("syncterm_transfer_init called");

    if (g_transfer_initialized) {
        LOGI("Transfer system already initialized");
        return 1;
    }

    /* Recursive, to keep the Windows CRITICAL_SECTION semantics this code was
     * written against (the Start* paths call zmodem_begin_session() while
     * holding the lock). */
    pthread_mutexattr_t attr;
    pthread_mutexattr_init(&attr);
    pthread_mutexattr_settype(&attr, PTHREAD_MUTEX_RECURSIVE);
    pthread_mutex_init(&g_transfer.lock, &attr);
    pthread_mutexattr_destroy(&attr);

    /* Initialize ZMODEM structure */
    zmodem_init(&g_transfer.zm,
                &g_transfer,           /* cbdata */
                zmodem_lputs_cb,       /* lputs */
                zmodem_progress_cb,    /* progress */
                zmodem_send_byte_cb,   /* send_byte */
                zmodem_recv_byte_cb,   /* recv_byte */
                zmodem_is_connected_cb,/* is_connected */
                zmodem_is_cancelled_cb,/* is_cancelled */
                zmodem_data_waiting_cb,/* data_waiting */
                zmodem_flush_cb);      /* flush */

    /* Configure ZMODEM (defaults — tuned per connection type before each transfer) */
    g_transfer.zm.log_level = &g_transfer.log_level;
    g_transfer.zm.max_errors = 10;
    g_transfer.zm.recv_timeout = 10;
    g_transfer.zm.send_timeout = 10;
    g_transfer.zm.escape_telnet_iac = TRUE;
    g_transfer.zm.recv_bufsize = 0;  /* no ZRINIT buffer limit, so senders stream (was 16384: a ZACK stop every 16 KB); conn_inbuf back-pressures, never drops */

    g_transfer.state = TRANSFER_IDLE;
    g_transfer.cancelled = 0;
    g_transfer_initialized = 1;

    LOGI("Transfer system initialized successfully");
    return 1;
}

/* TRUE while a transfer forced the Telnet layer into binary mode and must
 * put it back the way it found it when the session ends. */
static BOOL g_restore_binary_mode = FALSE;

/**
 * Start a ZMODEM session.
 *
 * zmodem_init() memsets the whole zmodem_t, so this must run before EVERY
 * transfer, not once at startup. The engine accumulates a great deal of
 * per-session state that is never otherwise cleared -- block_size and
 * max_block_size are halved on each error, frame_in_transit says whether a
 * ZDATA header still has to be emitted, cancelled latches on a received
 * ZCAN, and ack_file_pos / current_file_pos / current_file_size all describe
 * the *previous* file. Carrying any of that into the next transfer produces
 * exactly the kind of "finished, then re-sent part of the file" behaviour
 * ZMODEM uses its retry path for. SyncTERM's own term.c re-inits per
 * transfer for this reason.
 *
 * Also settles connection-type specific tuning and, on Telnet links, forces
 * binary mode so the Telnet layer stops rewriting CR in the byte stream.
 */
/* Transfer > ZMODEM 8K Blocks ("Super ZMODEM 8K"). Only changes what we SEND:
 * the engine starts every upload at 1K and doubles after each clean block up
 * to max_block_size, halving again on errors. Downloads need nothing -- the
 * sender picks the size and rx_data_subpacket already holds 8192. */
static int g_zmodem_8k = 0;

SYNCTERM_API void syncterm_zmodem_set_8k(int enable) {
    g_zmodem_8k = enable ? 1 : 0;
}

/* Largest subpacket received in this transfer: the board picks the download
 * block size, so the window title says 8K once an 8K block arrives. */
SYNCTERM_API unsigned syncterm_zmodem_largest_rx_block(void) {
    return g_diag.largest_rx_block;
}

static void zmodem_begin_session(void) {
    int conn_type = g_bbs_config.conn_type;
    int is_ssh = (conn_type == CONN_TYPE_SSH || conn_type == CONN_TYPE_SSHNA);

    zmodem_trace_begin_if_first();
    zmodem_reset_io_buffers();
    zmodem_eof_guard_reset();

    zmodem_init(&g_transfer.zm,
                &g_transfer,           /* cbdata */
                zmodem_lputs_cb,       /* lputs */
                zmodem_progress_cb,    /* progress */
                zmodem_send_byte_cb,   /* send_byte */
                zmodem_recv_byte_cb,   /* recv_byte */
                zmodem_is_connected_cb,/* is_connected */
                zmodem_is_cancelled_cb,/* is_cancelled */
                zmodem_data_waiting_cb,/* data_waiting */
                zmodem_flush_cb);      /* flush */

    g_transfer.zm.log_level = &g_transfer.log_level;
    g_transfer.zm.max_errors = 10;
    g_transfer.zm.recv_bufsize = 0;  /* no ZRINIT buffer limit, so senders stream (was 16384: a ZACK stop every 16 KB); conn_inbuf back-pressures, never drops */
    if (g_zmodem_8k) {
        g_transfer.zm.max_block_size = sizeof(g_transfer.zm.tx_data_subpacket);
        LOGI("ZMODEM 8K blocks enabled (uploads grow 1K -> %u)", g_transfer.zm.max_block_size);
    }

    if (is_ssh) {
        /* IAC (0xFF) and CR escaped on SSH too (2026-10-09): many boards' SSH servers hand the session to their
         * telnet code, which eats a raw 0xFF as a telnet command -- a board failed every upload block over SSH
         * while telnet (always escaped) worked. Each costs ~0.4%; every receiver decodes ZDLE escapes. */
        g_transfer.zm.escape_telnet_iac = TRUE;
        g_transfer.zm.escape_cr = TRUE;
        g_transfer.zm.recv_timeout = 30;    /* SSH rekeying and crypto overhead */
        g_transfer.zm.send_timeout = 30;
        LOGI("ZMODEM session started for SSH (IAC and CR escaped, 30s timeouts)");
    } else {
        /* Telnet/TelnetS — escape IAC (0xFF) to avoid protocol conflicts */
        g_transfer.zm.escape_telnet_iac = TRUE;
        g_transfer.zm.escape_cr = TRUE;     /* a raw CR goes out as CR LF until (unless) the board agrees to BINARY */
        g_transfer.zm.recv_timeout = 10;
        g_transfer.zm.send_timeout = 10;
        LOGI("ZMODEM session started for Telnet (IAC and CR escaped, 10s timeouts)");
    }

    /* A ZMODEM stream is binary: raw CR bytes appear both in file data and at
     * the end of every hex header. If the Telnet layer is not in binary mode
     * it expands CR to CR LF on the way out and holds CR back on the way in,
     * corrupting the stream in both directions. */
    g_restore_binary_mode = FALSE;
    if (!is_ssh && !conn_api.binary_mode) {
        LOGI("Forcing Telnet binary mode for the duration of the transfer");
        conn_binary_mode_on();
        g_restore_binary_mode = TRUE;
    }
    diag_begin(is_ssh);
}

/**
 * End a transfer session — ZMODEM or X/YMODEM: push out anything still staged
 * and undo the binary mode override if we were the ones who turned it on.
 */
static void transfer_end_session(void) {
    diag_end();
    zmodem_watchdog_disarm();
    zmodem_flush_tx();
    zmodem_reset_io_buffers();

    if (g_restore_binary_mode) {
        conn_binary_mode_off();
        g_restore_binary_mode = FALSE;
    }
}

/* How long the whole end-of-session ZFIN exchange gets, start to finish. */
#define ZFIN_TIMEOUT_MS 8000

/**
 * Close a ZMODEM session: send ZFIN, wait for the peer's ZFIN, answer with OO.
 *
 * ⚠️ NEVER call zmodem_get_zfin() bare -- it can hang forever.
 *
 * By the time we get here the FILE IS ALREADY DELIVERED: the receiver commits it
 * when it answers our ZEOF with ZRINIT, which is what made zmodem_send_file()
 * return TRUE. Everything below is just the goodbye handshake, and a board that
 * has already dropped back to its menu will never answer it.
 *
 * Left to itself, zmodem_get_zfin() then behaves very badly:
 *   - it retries max_errors (10) times, ALTERNATING ZFIN and ZABORT, so we spray
 *     ZABORT at a board that has already banked the file, and the hex headers
 *     land as junk on its command prompt;
 *   - each attempt calls zmodem_recv_header(), whose scan loop has NO wall-clock
 *     bound -- it only gives up after recv_timeout seconds of TOTAL SILENCE. A
 *     board that is printing its post-upload screen (or echoing the junk we just
 *     typed at it) keeps that loop fed indefinitely, so the transfer sits at 100%
 *     and never finishes.
 *
 * So: one ZFIN, no ZABORT, and a hard wall-clock deadline enforced through the
 * cancel callback -- the only predicate the engine consults in its innermost
 * receive loops. The transfer is still reported COMPLETE either way; whether the
 * board bothered to say goodbye has no bearing on whether the file arrived.
 */
static void zmodem_finish_session(void) {
    unsigned saved_max_errors  = g_transfer.zm.max_errors;
    unsigned saved_recv_timeout = g_transfer.zm.recv_timeout;

    g_transfer.zm.max_errors = 1;               /* one ZFIN, never a ZABORT */
    if (g_transfer.zm.recv_timeout > 5)
        g_transfer.zm.recv_timeout = 5;
    g_zfin_deadline = zm_tick_ms() + ZFIN_TIMEOUT_MS;

    int type = zmodem_get_zfin(&g_transfer.zm);

    g_zfin_deadline = 0;
    g_transfer.zm.max_errors   = saved_max_errors;
    g_transfer.zm.recv_timeout = saved_recv_timeout;
    /* is_cancelled() ASSIGNS zm->cancelled from the callback, so the watchdog
     * firing latches it. Clear it -- the session is over and the flag would
     * otherwise leak into the next transfer's error handling. */
    g_transfer.zm.cancelled = FALSE;

    if (type != ZFIN)
        LOGW("No ZFIN from the receiver; the file was already delivered, closing anyway");
}

/**
 * Set download directory
 */
SYNCTERM_API void syncterm_set_download_dir(const char* dir) {
    if (!g_transfer_initialized) return;
    if (!dir) {
        LOGE("Download directory was not set");
        return;
    }

    pthread_mutex_lock(&g_transfer.lock);
    strncpy(g_transfer.download_dir, dir, MAX_PATH - 1);
    g_transfer.download_dir[MAX_PATH - 1] = '\0';
    pthread_mutex_unlock(&g_transfer.lock);

    LOGI("Download directory set to: %s", g_transfer.download_dir);
}

/**
 * Receive files via ZMODEM
 * Returns: number of files received, or negative on error
 */
SYNCTERM_API int syncterm_zmodem_receive(void) {
    LOGD("syncterm_zmodem_receive called");

    if (!g_transfer_initialized) {
        LOGE("Transfer system not initialized");
        return -1;
    }

    if (!conn_connected()) {
        LOGE("Not connected");
        return -2;
    }

    pthread_mutex_lock(&g_transfer.lock);
    if (g_transfer.state != TRANSFER_IDLE) {
        pthread_mutex_unlock(&g_transfer.lock);
        LOGE("Transfer already in progress");
        return -3;
    }

    g_transfer.state = TRANSFER_RECEIVING;
    g_transfer.cancelled = 0;
    g_transfer.current_pos = 0;
    g_transfer.total_size = 0;
    g_transfer.current_file[0] = '\0';
    g_transfer.error_message[0] = '\0';

    /* Fresh ZMODEM state + connection tuning for this transfer */
    zmodem_begin_session();

    char download_dir[MAX_PATH + 1];
    strncpy(download_dir, g_transfer.download_dir, MAX_PATH);
    download_dir[MAX_PATH] = '\0';
    pthread_mutex_unlock(&g_transfer.lock);

    if (download_dir[0] == '\0') {
        LOGE("Download directory not set");
        pthread_mutex_lock(&g_transfer.lock);
        g_transfer.state = TRANSFER_ERROR;
        strncpy(g_transfer.error_message, "Download directory not set", sizeof(g_transfer.error_message) - 1);
        pthread_mutex_unlock(&g_transfer.lock);
        transfer_end_session();
        return -4;
    }

    /* Create download directory recursively if it doesn't exist */
    if (mkpath_recursive(download_dir) != 0) {
        int saved_errno = errno;
        LOGE("Failed to create download directory: %s", strerror(saved_errno));
        pthread_mutex_lock(&g_transfer.lock);
        g_transfer.state = TRANSFER_ERROR;
        snprintf(g_transfer.error_message, sizeof(g_transfer.error_message),
                 "Failed to create directory: %s", strerror(saved_errno));
        pthread_mutex_unlock(&g_transfer.lock);
        transfer_end_session();
        return -5;
    }

    LOGI("Starting ZMODEM receive to: %s", download_dir);

    /* Perform ZMODEM receive */
    uint64_t bytes_received = 0;
    LOGI("Calling zmodem_recv_files...");
    int result = zmodem_recv_files(&g_transfer.zm, download_dir, &bytes_received);
    LOGI("zmodem_recv_files returned: %d, bytes_received: %llu", result, (unsigned long long)bytes_received);

    transfer_end_session();

    pthread_mutex_lock(&g_transfer.lock);
    if (g_transfer.cancelled) {
        g_transfer.state = TRANSFER_CANCELLED;
        LOGI("ZMODEM receive cancelled");
    } else if (result < 0) {
        g_transfer.state = TRANSFER_ERROR;
        snprintf(g_transfer.error_message, sizeof(g_transfer.error_message),
                 "ZMODEM receive failed (code %d)", result);
        LOGE("ZMODEM receive failed: %d, file: %s, pos: %lld/%lld",
             result, g_transfer.current_file,
             (long long)g_transfer.current_pos, (long long)g_transfer.total_size);
    } else if (result == 0 && bytes_received == 0) {
        g_transfer.state = TRANSFER_ERROR;
        snprintf(g_transfer.error_message, sizeof(g_transfer.error_message),
                 "No files received - check BBS is sending");
        LOGE("ZMODEM receive: no files received");
    } else {
        g_transfer.state = TRANSFER_COMPLETE;
        LOGI("ZMODEM receive complete: %d files, %llu bytes", result, (unsigned long long)bytes_received);
    }
    pthread_mutex_unlock(&g_transfer.lock);

    return result;
}

/**
 * Send a file via ZMODEM
 * Returns: 0 on success, negative on error
 */
SYNCTERM_API int syncterm_zmodem_send(const char* filePath) {
    LOGD("syncterm_zmodem_send called");

    if (!g_transfer_initialized) {
        LOGE("Transfer system not initialized");
        return -1;
    }

    if (!conn_connected()) {
        LOGE("Not connected");
        return -2;
    }

    if (!filePath) {
        LOGE("NULL file path");
        return -3;
    }

    pthread_mutex_lock(&g_transfer.lock);
    if (g_transfer.state != TRANSFER_IDLE) {
        pthread_mutex_unlock(&g_transfer.lock);
        LOGE("Transfer already in progress");
        return -4;
    }

    g_transfer.state = TRANSFER_SENDING;
    g_transfer.cancelled = 0;
    g_transfer.current_pos = 0;
    g_transfer.total_size = 0;
    strncpy(g_transfer.current_file, filePath, MAX_PATH);
    g_transfer.current_file[MAX_PATH] = '\0';
    g_transfer.error_message[0] = '\0';

    /* Fresh ZMODEM state + connection tuning for this transfer */
    zmodem_begin_session();
    pthread_mutex_unlock(&g_transfer.lock);

    LOGI("Starting ZMODEM send: %s", filePath);

    g_transfer.zm.current_file_num = g_transfer.zm.total_files = 1;

    /* Open the file */
    FILE *fp = fopen(filePath, "rb");
    if (!fp) {
        int saved_errno = errno;  /* Save errno immediately for thread safety */
        LOGE("Failed to open file: %s - %s", filePath, strerror(saved_errno));

        pthread_mutex_lock(&g_transfer.lock);
        g_transfer.state = TRANSFER_ERROR;
        snprintf(g_transfer.error_message, sizeof(g_transfer.error_message),
                 "Failed to open file: %s", strerror(saved_errno));
        pthread_mutex_unlock(&g_transfer.lock);
        transfer_end_session();
        return -5;
    }

    /* Get file name from path - check both slash types on Windows */
    const char *filename = strrchr(filePath, '/');
    const char *filename_bs = strrchr(filePath, '\\');
    if (filename_bs && (!filename || filename_bs > filename)) {
        filename = filename_bs;
    }
    if (filename) {
        filename++;  /* Skip the slash */
    } else {
        filename = filePath;
    }

    /* Make a mutable copy of filename for zmodem_send_file */
    char name_buf[MAX_PATH + 1];
    strncpy(name_buf, filename, MAX_PATH);
    name_buf[MAX_PATH] = '\0';

    /* Perform ZMODEM send */
    time_t start_time = 0;
    uint64_t bytes_sent = 0;
    zmodem_watchdog_arm();
    BOOL success = zmodem_send_file(&g_transfer.zm, name_buf, fp, TRUE, &start_time, &bytes_sent);

    fclose(fp);

    /* On success, end the ZMODEM session BEFORE setting TRANSFER_COMPLETE.
     * zmodem_get_zfin() sends ZFIN, waits for response, and sends OO.
     * We must finish all I/O before the C# poll loop sees "complete" and
     * resumes connection data processing. Skip on failure/cancel to avoid
     * a needless timeout waiting for a ZFIN response that won't come. */
    if (g_eof_completed_on_timeout && !g_transfer.cancelled) {
        /* Delivered, but the board stopped answering. Skip the ZFIN handshake:
         * it has already left ZMODEM, so the bytes would land on its command
         * prompt -- the very thing this guard exists to stop. */
        success = TRUE;
        g_transfer.zm.cancelled = FALSE;
        LOGI("Treating transfer as complete: the whole file was delivered");
    } else if (success && !g_transfer.cancelled) {
        zmodem_finish_session();
    }

    /* Flush anything still staged and restore the connection's Telnet mode
     * before the poll loop is allowed to resume reading the connection. */
    transfer_end_session();

    pthread_mutex_lock(&g_transfer.lock);
    if (g_transfer.cancelled) {
        g_transfer.state = TRANSFER_CANCELLED;
        LOGI("ZMODEM send cancelled");
    } else if (!success) {
        g_transfer.state = TRANSFER_ERROR;
        strncpy(g_transfer.error_message, "ZMODEM send failed", sizeof(g_transfer.error_message) - 1);
        LOGE("ZMODEM send failed");
    } else {
        g_transfer.state = TRANSFER_COMPLETE;
        LOGI("ZMODEM send complete: %llu bytes", (unsigned long long)bytes_sent);
    }
    pthread_mutex_unlock(&g_transfer.lock);

    return success ? 0 : -6;
}

/**
 * Clear the batch upload queue.
 * Call before adding files for a new batch.
 */
SYNCTERM_API void syncterm_upload_queue_clear(void) {
    g_upload_batch.count = 0;
    LOGI("Upload queue cleared");
}

/**
 * Add a file path to the batch upload queue.
 * Returns: new queue count on success, -1 on error (NULL path or queue full).
 */
SYNCTERM_API int syncterm_upload_queue_add(const char* filePath) {
    if (!filePath) {
        LOGE("upload_queue_add: NULL path");
        return -1;
    }
    if (g_upload_batch.count >= MAX_UPLOAD_FILES) {
        LOGE("upload_queue_add: queue full (max %d)", MAX_UPLOAD_FILES);
        return -1;
    }
    strncpy(g_upload_batch.paths[g_upload_batch.count], filePath, MAX_PATH);
    g_upload_batch.paths[g_upload_batch.count][MAX_PATH] = '\0';
    g_upload_batch.count++;
    LOGI("Upload queued (%d): %s", g_upload_batch.count, filePath);
    return g_upload_batch.count;
}

/**
 * Get the number of files currently in the batch upload queue.
 */
SYNCTERM_API int syncterm_upload_queue_count(void) {
    return g_upload_batch.count;
}

/**
 * Send all queued files via ZMODEM in a single batch session.
 *
 * Unlike syncterm_zmodem_send (one file, one session), this sends every file
 * in the queue over ONE ZMODEM session: the ZRQINIT handshake is issued only
 * for the first file, subsequent files skip straight to their ZFILE frame,
 * and a single ZFIN closes the session after the last file. This is exactly
 * how the SBBS ZMODEM engine expects batch transfers to be driven.
 *
 * Returns: number of files sent on success, negative on error.
 */
SYNCTERM_API int syncterm_zmodem_send_batch(void) {
    LOGD("syncterm_zmodem_send_batch called (%d files)", g_upload_batch.count);

    if (!g_transfer_initialized) {
        LOGE("Transfer system not initialized");
        return -1;
    }

    if (!conn_connected()) {
        LOGE("Not connected");
        return -2;
    }

    if (g_upload_batch.count <= 0) {
        LOGE("Upload queue is empty");
        return -3;
    }

    pthread_mutex_lock(&g_transfer.lock);
    if (g_transfer.state != TRANSFER_IDLE) {
        pthread_mutex_unlock(&g_transfer.lock);
        LOGE("Transfer already in progress");
        return -4;
    }

    g_transfer.state = TRANSFER_SENDING;
    g_transfer.cancelled = 0;
    g_transfer.current_pos = 0;
    g_transfer.total_size = 0;
    g_transfer.current_file_index = 0;
    g_transfer.total_files = g_upload_batch.count;
    g_transfer.current_file[0] = '\0';
    g_transfer.error_message[0] = '\0';

    /* Fresh ZMODEM state + connection tuning for this transfer */
    zmodem_begin_session();
    pthread_mutex_unlock(&g_transfer.lock);

    g_transfer.zm.total_files = g_upload_batch.count;

    /* Whether the ZMODEM session handshake (ZRQINIT) has been sent yet.
     * Tracked as a flag rather than "i == 0" so that if the first file(s)
     * fail to open, the first file that DOES open still carries the init. */
    BOOL initialized = FALSE;
    BOOL overall = TRUE;
    int files_sent = 0;
    uint64_t total_bytes_sent = 0;

    for (int i = 0; i < g_upload_batch.count; i++) {
        /* Honour a cancel requested between files */
        pthread_mutex_lock(&g_transfer.lock);
        int cancelled = g_transfer.cancelled;
        pthread_mutex_unlock(&g_transfer.lock);
        if (cancelled) {
            overall = FALSE;
            break;
        }

        const char* filePath = g_upload_batch.paths[i];
        LOGI("Batch send file %d/%d: %s", i + 1, g_upload_batch.count, filePath);

        FILE *fp = fopen(filePath, "rb");
        if (!fp) {
            int saved_errno = errno;  /* Save errno immediately for thread safety */
            LOGE("Failed to open file: %s - %s", filePath, strerror(saved_errno));
            pthread_mutex_lock(&g_transfer.lock);
            snprintf(g_transfer.error_message, sizeof(g_transfer.error_message),
                     "Failed to open file: %s", strerror(saved_errno));
            pthread_mutex_unlock(&g_transfer.lock);
            overall = FALSE;
            break;  /* v1 policy: abort the whole batch on a bad file */
        }

        /* Get file name from path - check both slash types on Windows */
        const char *filename = strrchr(filePath, '/');
        const char *filename_bs = strrchr(filePath, '\\');
        if (filename_bs && (!filename || filename_bs > filename)) {
            filename = filename_bs;
        }
        if (filename) {
            filename++;  /* Skip the slash */
        } else {
            filename = filePath;
        }

        /* Make a mutable copy of filename for zmodem_send_file */
        char name_buf[MAX_PATH + 1];
        strncpy(name_buf, filename, MAX_PATH);
        name_buf[MAX_PATH] = '\0';

        /* Publish per-file progress state for the poll thread */
        pthread_mutex_lock(&g_transfer.lock);
        g_transfer.current_file_index = i + 1;
        g_transfer.current_pos = 0;
        g_transfer.total_size = 0;
        strncpy(g_transfer.current_file, name_buf, MAX_PATH);
        g_transfer.current_file[MAX_PATH] = '\0';
        pthread_mutex_unlock(&g_transfer.lock);

        g_transfer.zm.current_file_num = i + 1;

        /* request_init (ZRQINIT) only for the first file actually sent */
        time_t start_time = 0;
        uint64_t bytes_sent = 0;
        zmodem_watchdog_arm();

        /* Each file gets its own end-of-file window. zmodem_begin_session()
         * runs ONCE for the whole batch, so without this the guard state from
         * file 1 survives into file 2: g_eof_delivered stays TRUE so the guard
         * never re-arms, and the stale deadline is already in the past, which
         * would abort the next file the moment it starts. */
        zmodem_eof_guard_reset();

        BOOL success = zmodem_send_file(&g_transfer.zm, name_buf, fp,
                                        initialized ? FALSE : TRUE,
                                        &start_time, &bytes_sent);
        fclose(fp);

        if (g_eof_completed_on_timeout) {
            /* Delivered, but the board stopped answering. Same reasoning as the
             * single-file send: the file arrived, so carry on with the batch. */
            success = TRUE;
            g_transfer.zm.cancelled = FALSE;
            LOGI("Treating '%s' as complete: the whole file was delivered", name_buf);
        }

        if (!success) {
            overall = FALSE;
            break;
        }

        initialized = TRUE;
        files_sent++;
        total_bytes_sent += bytes_sent;
    }

    /* Close the ZMODEM session ONCE, after the last file. Only if we actually
     * opened a session (sent >=1 file) and weren't cancelled/failed — skipping
     * ZFIN on failure avoids a needless timeout waiting for a response that
     * won't come. Mirrors the single-file send's handling of zmodem_get_zfin.
     *
     * Also skipped when the last file ended on the delivery guard: the board
     * has already left ZMODEM, so a ZFIN would land on its command prompt. */
    if (initialized && overall && !g_eof_completed_on_timeout) {
        pthread_mutex_lock(&g_transfer.lock);
        int cancelled = g_transfer.cancelled;
        pthread_mutex_unlock(&g_transfer.lock);
        if (!cancelled) {
            zmodem_finish_session();
        }
    }

    transfer_end_session();

    pthread_mutex_lock(&g_transfer.lock);
    if (g_transfer.cancelled) {
        g_transfer.state = TRANSFER_CANCELLED;
        LOGI("ZMODEM batch send cancelled after %d file(s)", files_sent);
    } else if (!overall) {
        g_transfer.state = TRANSFER_ERROR;
        if (g_transfer.error_message[0] == '\0') {
            strncpy(g_transfer.error_message, "ZMODEM send failed",
                    sizeof(g_transfer.error_message) - 1);
        }
        LOGE("ZMODEM batch send failed after %d file(s)", files_sent);
    } else {
        g_transfer.state = TRANSFER_COMPLETE;
        LOGI("ZMODEM batch send complete: %d file(s), %llu bytes",
             files_sent, (unsigned long long)total_bytes_sent);
    }
    pthread_mutex_unlock(&g_transfer.lock);

    return overall ? files_sent : -6;
}

/* ==================================================================== *
 * X / YMODEM
 *
 * Why this exists next to ZMODEM: plenty of boards never offer ZMODEM at
 * all — Commodore ones especially — and XMODEM is the lowest common
 * denominator that every one of them speaks.
 *
 * ⚠️ TELNET IS FINE. RAW IS **NOT** REQUIRED. The obvious worry — "XMODEM is
 * 8-bit binary, surely it needs a raw socket" — is already answered by the
 * staging buffers at the top of this file: the TX side doubles IAC on the way
 * out, the RX side copes with the Telnet parser swallowing IAC sequences and
 * holding CR back, and a session forces Telnet binary mode so CR is not
 * rewritten to CR LF. That is exactly the same machinery ZMODEM rides on, and
 * ZMODEM's stream is every bit as binary. SSH is natively 8-bit clean.
 *
 *
 * ⚠️ AMENDED 2026-09-03 — the claim above holds ONLY FOR A BOARD THAT REALLY
 * SPEAKS TELNET. Verified against a fake telnet BBS on loopback, both ends
 * telnet, byte-exact with a hostile payload. But not every board on port 23 is
 * actually speaking telnet, and against one that is not, OUR telnet layer eats
 * any 0xFF in the stream as an IAC command. ZMODEM survives that because it
 * escapes IAC itself before the bytes ever reach the wire; X/YMODEM blocks ARE
 * raw file bytes, so the download CRCs, retries and dies while ZMODEM on the
 * same board is fine. That is why there is now a RAW connection type
 * (CONN_TYPE_RAW) in the phonebook — no negotiation, no IAC handling. Do NOT
 * silently switch a transfer to raw; it is the USER's choice of link type.
 * ⚠️ sbbs3/xmodem.c IS ONLY HALF A PROTOCOL. It gives us xmodem_send_file()
 * for the send side, but there is deliberately no xmodem_recv_file(): upstream
 * drives the receive loop from the CALLER, in SyncTERM's term.c:xmodem_download().
 * xmodem_recv_files() below is that loop, ported with the uifc/curses parts
 * taken out. If the shared xmodem.c ever changes, diff it against term.c again.
 * ==================================================================== */

#ifndef XMODEM_128B
#define XMODEM_128B (1 << 10)  /* mirrors syncterm/term.h */
#endif

/* xmodem_t keeps a POINTER to the mode word and rewrites it in place as the
 * transfer degrades — CRC-16 back to checksum, YMODEM back to XMODEM, -g back
 * to plain. So the storage has to outlive the call, and reading it afterwards
 * tells you what actually happened on the wire. Only one transfer runs at a
 * time (guarded by g_transfer.state), so one static is enough. */
static long g_xm_mode = 0;

/**
 * X/YMODEM Callback: Progress update.
 * Different shape from ZMODEM's — block/offset/size/start rather than a bare
 * position — so it cannot share zmodem_progress_cb.
 */
static void xmodem_progress_cb(void* cbdata, unsigned block_num, int64_t offset,
                               int64_t fsize, time_t start) {
    (void)cbdata; (void)block_num; (void)start;

    pthread_mutex_lock(&g_transfer.lock);
    g_transfer.current_pos = offset;
    /* Plain XMODEM carries no file length, and the receive loop stands in
     * 0x7fffffff for "unknown". Report 0 so the UI falls back to a plain byte
     * counter instead of drawing a progress bar against a bogus 2 GB. */
    g_transfer.total_size = (fsize >= 0x7fffffff) ? 0 : fsize;
    pthread_mutex_unlock(&g_transfer.lock);
}

/**
 * Start an X/YMODEM session. Mirrors zmodem_begin_session(): fresh engine
 * state, connection-type tuning, and Telnet binary mode for the duration.
 */
static void xmodem_begin_session(long mode) {
    int conn_type = g_bbs_config.conn_type;
    int is_ssh = (conn_type == CONN_TYPE_SSH || conn_type == CONN_TYPE_SSHNA);

    zmodem_reset_io_buffers();
    g_xm_mode = mode;

    xmodem_init(&g_transfer.xm,
                &g_transfer.xm,        /* cbdata (our callbacks ignore it) */
                &g_xm_mode,
                zmodem_lputs_cb,       /* lputs      — protocol-agnostic */
                xmodem_progress_cb,    /* progress   — X/YMODEM shape */
                zmodem_send_byte_cb,   /* send_byte  — protocol-agnostic */
                zmodem_recv_byte_cb,   /* recv_byte  — protocol-agnostic */
                zmodem_is_connected_cb,
                zmodem_is_cancelled_cb,
                zmodem_flush_cb);

    g_transfer.xm.log_level = &g_transfer.log_level;
    g_transfer.xm.max_errors = 10;

    if (is_ssh) {
        g_transfer.xm.recv_timeout = 30;
        g_transfer.xm.send_timeout = 30;
        g_transfer.xm.ack_timeout  = 30;
    } else {
        g_transfer.xm.recv_timeout = 10;
        g_transfer.xm.send_timeout = 10;
        g_transfer.xm.ack_timeout  = 10;
    }

    /* 128-byte blocks. XMODEM_128B is not just a preference on old boards: a
     * C64 BBS that only knows 128-byte SOH blocks will reject the 1K STX
     * blocks xmodem_init() defaults to.
     *
     * ⚠️ block_size is what we SEND; max_block_size is the largest we will
     * ACCEPT. Clamping the latter too would make us reject a sender's 1K
     * blocks, so it stays wide open on the receive side — a receiver has no
     * reason to be fussy, it just reads whatever SOH/STX arrives. */
    if (mode & XMODEM_128B) {
        g_transfer.xm.block_size = XMODEM_MIN_BLOCK_SIZE;
        if (mode & SEND)
            g_transfer.xm.max_block_size = XMODEM_MIN_BLOCK_SIZE;
    }

    g_restore_binary_mode = FALSE;
    if (!is_ssh && !conn_api.binary_mode) {
        LOGI("Forcing Telnet binary mode for the duration of the transfer");
        conn_binary_mode_on();
        g_restore_binary_mode = TRUE;
    }

    LOGI("X/YMODEM session started: mode=0x%lx block_size=%u %s",
         mode, g_transfer.xm.block_size, is_ssh ? "(SSH)" : "(Telnet)");
}

/* Publish the name being transferred to the progress poller. */
static void xmodem_set_current_file(const char* path) {
    pthread_mutex_lock(&g_transfer.lock);
    strncpy(g_transfer.current_file, getfname(path), MAX_PATH);
    g_transfer.current_file[MAX_PATH] = '\0';
    pthread_mutex_unlock(&g_transfer.lock);
}

/**
 * Pick a path that does not collide with an existing file.
 *
 * term.c stops and asks the user (overwrite / rename / abort) via uifc. We are
 * on a background transfer thread with no way to ask, and silently overwriting
 * somebody's file is the one outcome that loses data, so append " (n)" before
 * the extension instead — the same thing a browser download does.
 */
static void xmodem_unique_path(char* path, size_t pathsize) {
    char base[MAX_PATH + 1];
    char ext[MAX_PATH + 1];
    char* dot;

    if (access(path, F_OK) != 0)
        return;   /* nothing there — use it as-is */

    strncpy(base, path, MAX_PATH);
    base[MAX_PATH] = '\0';
    ext[0] = '\0';

    /* Split at the LAST dot, but only if it is in the file name rather than a
     * directory component. */
    dot = strrchr(base, '.');
    if (dot != NULL && strpbrk(dot, "/") == NULL) {
        strncpy(ext, dot, MAX_PATH);
        ext[MAX_PATH] = '\0';
        *dot = '\0';
    }

    for (int n = 1; n < 1000; n++) {
        snprintf(path, pathsize, "%s (%d)%s", base, n, ext);
        if (access(path, F_OK) != 0)
            return;
    }
    /* 1000 collisions: fall through with the last candidate and let fopen fail. */
}

/**
 * Receive one or more files via X/YMODEM.
 *
 * Ported from SyncTERM term.c:xmodem_download(). Returns the number of files
 * received, or negative on failure.
 *
 * save_path is the full target path and applies to XMODEM only, which has no
 * filename in the protocol. YMODEM takes the name from the header block and
 * writes into the download directory; pass NULL/"" for it.
 */
static int xmodem_recv_files(const char* save_path) {
    xmodem_t* xm = &g_transfer.xm;
    uchar     block[XMODEM_MAX_BLOCK_SIZE];
    char      path[MAX_PATH + 1];
    char      tmp_path[MAX_PATH + 1];
    char      fname[MAX_PATH + 1];
    char      dldir[MAX_PATH + 1];
    int       i = 0;
    unsigned  errors;
    unsigned  block_num;
    unsigned  total_files = 0;
    unsigned  wr;
    int       files_received = 0;
    BOOL      success = FALSE;
    BOOL      extra_pass = FALSE;
    long      fmode = 0;
    long      serial_num = -1;
    ulong     tmpftime = 0;
    int64_t   file_bytes = 0, file_bytes_left = 0, total_bytes = 0;
    FILE*     fp = NULL;
    time_t    startfile, ftime = 0;

    pthread_mutex_lock(&g_transfer.lock);
    strncpy(dldir, g_transfer.download_dir, MAX_PATH);
    dldir[MAX_PATH] = '\0';
    pthread_mutex_unlock(&g_transfer.lock);

    fname[0] = '\0';

    while (conn_connected()) {
        if (g_xm_mode & XMODEM) {
            /* XMODEM: no name, no size. The caller told us where to put it. */
            if (save_path != NULL && save_path[0] != '\0')
                snprintf(path, sizeof(path), "%s", save_path);
            else
                snprintf(path, sizeof(path), "%s/XMODEM.DAT", dldir);
            strncpy(fname, getfname(path), MAX_PATH);
            fname[MAX_PATH] = '\0';
            file_bytes = file_bytes_left = 0x7fffffff;
        }
        else {
            /* YMODEM: block 0 carries name, size, mtime and the batch counts. */
            LOGI("Fetching YMODEM header block");
            for (errors = 0; errors <= xm->max_errors && !xm->cancelled; errors++) {
                xmodem_put_nak(xm, /* expected_block: */ 0);
                i = xmodem_get_block(xm, block, /* expected_block: */ 0);
                if (i == SUCCESS) {
                    if (!(g_xm_mode & GMODE)) {
                        zmodem_send_byte_cb(xm, ACK, 10);
                        zmodem_flush_tx();
                    }
                    break;
                }
                if (extra_pass) {
                    /* Upstream's hack for sz 0.12.21rc: no header after a
                     * completed file means the batch is simply over. */
                    LOGI("No YMODEM header block after transfer, assuming end of batch");
                    goto done;
                }
                if (i == NOINP && (g_xm_mode & GMODE)) {
                    g_xm_mode &= ~GMODE;
                    LOGW("YMODEM-g timed out, falling back to %s",
                         (g_xm_mode & CRC) ? "CRC-16" : "Checksum");
                }
                if (i == NOT_YMODEM) {
                    /* The sender is really doing XMODEM. term.c prompts for a
                     * filename here; we cannot, so use the caller's path if it
                     * gave one and otherwise land it in the download dir under
                     * a fixed name rather than dropping the transfer. */
                    LOGW("Sender is XMODEM, not YMODEM — falling back");
                    g_xm_mode &= ~YMODEM;
                    g_xm_mode |= XMODEM | CRC;
                    if (save_path != NULL && save_path[0] != '\0')
                        snprintf(path, sizeof(path), "%s", save_path);
                    else
                        snprintf(path, sizeof(path), "%s/XMODEM.DAT", dldir);
                    strncpy(fname, getfname(path), MAX_PATH);
                    fname[MAX_PATH] = '\0';
                    file_bytes = file_bytes_left = 0x7fffffff;
                    break;
                }
                if ((errors + 1 > xm->max_errors / 3) && (g_xm_mode & CRC)
                    && !(g_xm_mode & GMODE)) {
                    LOGI("Falling back to 8-bit Checksum mode");
                    g_xm_mode &= ~CRC;
                }
            }
            if (errors > xm->max_errors || xm->cancelled) {
                xmodem_cancel(xm);
                goto done;
            }
            if (i != NOT_YMODEM) {
                if (!block[0]) {
                    LOGI("Received YMODEM termination block");
                    goto done;
                }
                extra_pass = FALSE;
                file_bytes = total_bytes = 0;
                total_files = 0;
                sscanf(((char*)block) + strlen((char*)block) + 1,
                       "%" PRId64 " %lo %lo %lo %u %" PRId64,
                       &file_bytes,   /* file size (decimal) */
                       &tmpftime,     /* file time (octal unix format) */
                       &fmode,        /* file mode (not used) */
                       &serial_num,   /* program serial number */
                       &total_files,  /* remaining files to be sent */
                       &total_bytes); /* remaining bytes to be sent */
                ftime = (time_t)tmpftime;

                strncpy(fname, (char*)block, MAX_PATH);
                fname[MAX_PATH] = '\0';

                if (!file_bytes)
                    file_bytes = 0x7fffffff;
                file_bytes_left = file_bytes;
                if (!total_files)
                    total_files = 1;
                if (total_bytes < file_bytes)
                    total_bytes = file_bytes;

                snprintf(path, sizeof(path), "%s/%s", dldir, getfname(fname));
                LOGI("Incoming: %s (%" PRId64 " bytes, %u file(s) to come)",
                     getfname(fname), file_bytes, total_files);
            }
        }

        if (isdir(path)) {
            LOGE("%s is an existing directory", path);
            xmodem_cancel(xm);
            goto done;
        }
        if (!(g_xm_mode & OVERWRITE))
            xmodem_unique_path(path, sizeof(path));

        if (mkpath_recursive(dldir) != 0)
            LOGW("Could not create download directory %s", dldir);

        /* ⚠️ Receive into a SIDECAR and only put it in place once the file is
         * complete.
         *
         * The obvious thing — fopen(path, "wb") — truncates the destination
         * BEFORE the handshake has even succeeded, so picking an existing file
         * in the Save-As and then having the transfer fail (wrong protocol on
         * the board, carrier dropped, anything) DESTROYS that file. Upstream
         * term.c does exactly that, and it only escaped notice there because
         * nothing ever set the OVERWRITE bit, so xmodem_unique_path() always
         * steered around existing files. The moment overwrite works as the user
         * asked, the data-loss window opens.
         *
         * XMODEM has no resume, so a partial download is worthless — on failure
         * the sidecar is simply deleted and the user's file is untouched. */
        snprintf(tmp_path, sizeof(tmp_path), "%s.part", path);
        xmodem_unique_path(tmp_path, sizeof(tmp_path));  /* skip a stale sidecar */

        if ((fp = fopen(tmp_path, "wb")) == NULL) {
            LOGE("Error %d creating %s", errno, tmp_path);
            pthread_mutex_lock(&g_transfer.lock);
            snprintf(g_transfer.error_message, sizeof(g_transfer.error_message),
                     "Cannot create %s", getfname(tmp_path));
            pthread_mutex_unlock(&g_transfer.lock);
            xmodem_cancel(xm);
            goto done;
        }

        xmodem_set_current_file(path);
        pthread_mutex_lock(&g_transfer.lock);
        g_transfer.current_file_index = files_received + 1;
        g_transfer.total_files = (int)total_files;
        g_transfer.current_pos = 0;
        g_transfer.total_size = (file_bytes >= 0x7fffffff) ? 0 : file_bytes;
        pthread_mutex_unlock(&g_transfer.lock);

        LOGI("Receiving %s via %s %s", path,
             (g_xm_mode & XMODEM) ? ((g_xm_mode & GMODE) ? "XMODEM-g" : "XMODEM")
                                  : ((g_xm_mode & GMODE) ? "YMODEM-g" : "YMODEM"),
             (g_xm_mode & CRC) ? "CRC-16" : "Checksum");

        startfile = time(NULL);
        success = FALSE;
        errors = 0;
        block_num = 1;

        /* On the NOT_YMODEM path block 1 is already sitting in block[] — it is
         * what told us this was XMODEM — so do not NAK for it again. */
        if (i != NOT_YMODEM)
            xmodem_put_nak(xm, block_num);

        while (conn_connected()) {
            xmodem_progress_cb(xm, block_num, ftello(fp), file_bytes, startfile);

            if (xm->is_cancelled(xm->cbdata)) {
                LOGW("Cancelled locally");
                xmodem_cancel(xm);
                goto done;
            }

            if (i == NOT_YMODEM)
                i = SUCCESS;             /* consume the block already in hand */
            else
                i = xmodem_get_block(xm, block, block_num);

            if (i != SUCCESS) {
                if (i == EOT) {          /* end of this file */
                    success = TRUE;
                    xmodem_put_ack(xm);
                    break;
                }
                if (i == CAN) {          /* remote cancelled */
                    xm->cancelled = TRUE;
                    break;
                }
                if (g_xm_mode & GMODE) {
                    /* -g has no retransmission: one bad block ends it. */
                    LOGE("Too many errors (%u) in streaming mode", ++errors);
                    goto done;
                }
                if (++errors > xm->max_errors) {
                    LOGE("Too many errors (%u)", errors);
                    xmodem_cancel(xm);
                    break;
                }
                if (i != NOT_XMODEM && block_num == 1
                    && errors > (xm->max_errors / 3)
                    && (g_xm_mode & CRC) && !(g_xm_mode & GMODE)) {
                    LOGI("Falling back to 8-bit Checksum mode (error=%d)", i);
                    g_xm_mode &= ~CRC;
                }
                xmodem_put_nak(xm, block_num);
                continue;
            }

            if (!(g_xm_mode & GMODE)) {
                zmodem_send_byte_cb(xm, ACK, 10);
                zmodem_flush_tx();
            }
            if (file_bytes_left <= 0) {
                LOGW("Sender sent more bytes than its header specified");
                break;
            }
            wr = xm->block_size;
            if (wr > (unsigned)file_bytes_left)
                wr = (unsigned)file_bytes_left;
            if (fwrite(block, 1, wr, fp) != wr) {
                LOGE("Error writing %u bytes at offset %" PRId64, wr, (int64_t)ftello(fp));
                xmodem_cancel(xm);
                goto done;
            }
            file_bytes_left -= wr;
            block_num++;
        }

        fflush(fp);

        /* XMODEM pads the last block to the block size with ^Z, and its
         * "length" was our 0x7fffffff placeholder, so for XMODEM the file on
         * disk IS the length — only YMODEM can truncate to a real size. */
        if (file_bytes < (int64_t)filelength(fileno(fp))) {
            LOGI("Truncating file to %" PRId64 " bytes", file_bytes);
            if (chsize(fileno(fp), file_bytes) != 0)
                LOGE("Error %d truncating file", errno);
        } else {
            file_bytes = filelength(fileno(fp));
        }
        fclose(fp);
        fp = NULL;

        if (success) {
            /* rename() replaces atomically on POSIX and — the point of the whole
             * exercise — leaves the destination alone if the rename itself fails.
             * Do NOT "simplify" this to remove()+rename(); that reopens the very
             * window this closes. (Windows uses MoveFileEx(MOVEFILE_REPLACE_EXISTING).) */
            if (rename(tmp_path, path) != 0) {
                LOGE("Could not put %s in place as %s (error %d)", tmp_path, path, errno);
                pthread_mutex_lock(&g_transfer.lock);
                snprintf(g_transfer.error_message, sizeof(g_transfer.error_message),
                         "Received the file but could not save it as %s", getfname(path));
                pthread_mutex_unlock(&g_transfer.lock);
                success = FALSE;
            }
        }

        if (success) {
            time_t t = time(NULL) - startfile;
            if (!t) t = 1;
            files_received++;
            if (!(g_xm_mode & XMODEM) && ftime)
                setfdate(path, ftime);
            LOGI("Received %s: %" PRId64 " bytes in %lus (%lu CPS)",
                 getfname(path), file_bytes, (ulong)t, (ulong)(file_bytes / t));
        } else {
            LOGE("Transfer %s", xm->cancelled ? "cancelled" : "failed");
            /* Bin the sidecar. The destination was never opened, so whatever
             * the user already had at `path` is still sitting there intact. */
            remove(tmp_path);
        }

        if (g_xm_mode & XMODEM)
            break;                       /* XMODEM is one file, full stop */

        if (total_files == 0 || --total_files == 0)
            extra_pass = TRUE;
        if (file_bytes < total_bytes)
            total_bytes -= file_bytes;
        else
            total_bytes = 0;
    }

done:
    if (fp)
        fclose(fp);
    return files_received;
}

/**
 * Receive via X/YMODEM.
 *
 * savePath is the full destination path and is REQUIRED for XMODEM, which has
 * no filename in the protocol. For YMODEM pass NULL — the name comes from the
 * header block and the file lands in the download directory.
 *
 * mode is the sexyz.h bit set: XMODEM or YMODEM, optionally | CRC | GMODE, and
 * | XMODEM_128B to pin 128-byte blocks.
 *
 * Returns the number of files received, or negative on error.
 */
SYNCTERM_API int syncterm_xmodem_receive(const char* savePath, int mode) {
    LOGD("syncterm_xmodem_receive called: mode=0x%x path=%s", mode, savePath ? savePath : "(none)");

    if (!g_transfer_initialized) {
        LOGE("Transfer system not initialized");
        return -1;
    }
    if (!conn_connected()) {
        LOGE("Not connected");
        return -2;
    }
    if ((mode & (XMODEM | YMODEM)) == 0) {
        LOGE("Neither XMODEM nor YMODEM requested (mode=0x%x)", mode);
        return -6;
    }
    if ((mode & XMODEM) && (savePath == NULL || savePath[0] == '\0')) {
        LOGE("XMODEM receive needs a destination path — the protocol has no filename");
        return -7;
    }

    pthread_mutex_lock(&g_transfer.lock);
    if (g_transfer.state != TRANSFER_IDLE) {
        pthread_mutex_unlock(&g_transfer.lock);
        LOGE("Transfer already in progress");
        return -3;
    }
    g_transfer.state = TRANSFER_RECEIVING;
    g_transfer.cancelled = 0;
    g_transfer.current_pos = 0;
    g_transfer.total_size = 0;
    g_transfer.current_file_index = 0;
    g_transfer.total_files = 0;
    g_transfer.current_file[0] = '\0';
    g_transfer.error_message[0] = '\0';

    char download_dir[MAX_PATH + 1];
    strncpy(download_dir, g_transfer.download_dir, MAX_PATH);
    download_dir[MAX_PATH] = '\0';
    pthread_mutex_unlock(&g_transfer.lock);

    if (download_dir[0] == '\0' && !(mode & XMODEM)) {
        LOGE("Download directory not set");
        pthread_mutex_lock(&g_transfer.lock);
        g_transfer.state = TRANSFER_ERROR;
        strncpy(g_transfer.error_message, "Download directory not set",
                sizeof(g_transfer.error_message) - 1);
        pthread_mutex_unlock(&g_transfer.lock);
        return -4;
    }
    if (download_dir[0] != '\0' && mkpath_recursive(download_dir) != 0) {
        int saved_errno = errno;
        LOGE("Failed to create download directory: %s", strerror(saved_errno));
        pthread_mutex_lock(&g_transfer.lock);
        g_transfer.state = TRANSFER_ERROR;
        snprintf(g_transfer.error_message, sizeof(g_transfer.error_message),
                 "Failed to create directory: %s", strerror(saved_errno));
        pthread_mutex_unlock(&g_transfer.lock);
        return -5;
    }

    xmodem_begin_session((long)(mode | RECV));

    int files = xmodem_recv_files(savePath);

    transfer_end_session();

    pthread_mutex_lock(&g_transfer.lock);
    if (g_transfer.cancelled) {
        g_transfer.state = TRANSFER_CANCELLED;
        LOGI("X/YMODEM receive cancelled");
    } else if (files <= 0) {
        g_transfer.state = TRANSFER_ERROR;
        if (g_transfer.error_message[0] == '\0') {
            snprintf(g_transfer.error_message, sizeof(g_transfer.error_message),
                     "No files received — is the BBS sending %s?",
                     (mode & YMODEM) ? "YMODEM" : "XMODEM");
        }
        LOGE("X/YMODEM receive failed");
    } else {
        g_transfer.state = TRANSFER_COMPLETE;
        LOGI("X/YMODEM receive complete: %d file(s)", files);
    }
    pthread_mutex_unlock(&g_transfer.lock);

    return files > 0 ? files : -8;
}

/**
 * Send a file via X/YMODEM.
 *
 * mode is the sexyz.h bit set, as for the receive above. Returns 0 on success,
 * negative on error.
 *
 * Single file only: YMODEM batch would need the remaining-files/bytes counters
 * fed per file, and the boards this exists for do not offer batch anyway.
 */
SYNCTERM_API int syncterm_xmodem_send(const char* filePath, int mode) {
    LOGD("syncterm_xmodem_send called: mode=0x%x path=%s", mode, filePath ? filePath : "(null)");

    if (!g_transfer_initialized) {
        LOGE("Transfer system not initialized");
        return -1;
    }
    if (!conn_connected()) {
        LOGE("Not connected");
        return -2;
    }
    if (!filePath || !filePath[0]) {
        LOGE("NULL file path");
        return -3;
    }
    if ((mode & (XMODEM | YMODEM)) == 0) {
        LOGE("Neither XMODEM nor YMODEM requested (mode=0x%x)", mode);
        return -6;
    }

    pthread_mutex_lock(&g_transfer.lock);
    if (g_transfer.state != TRANSFER_IDLE) {
        pthread_mutex_unlock(&g_transfer.lock);
        LOGE("Transfer already in progress");
        return -4;
    }
    g_transfer.state = TRANSFER_SENDING;
    g_transfer.cancelled = 0;
    g_transfer.current_pos = 0;
    g_transfer.total_size = 0;
    g_transfer.current_file_index = 0;
    g_transfer.total_files = 0;
    g_transfer.error_message[0] = '\0';
    pthread_mutex_unlock(&g_transfer.lock);

    FILE* fp = fopen(filePath, "rb");
    if (fp == NULL) {
        LOGE("Failed to open %s (errno %d)", filePath, errno);
        pthread_mutex_lock(&g_transfer.lock);
        g_transfer.state = TRANSFER_ERROR;
        snprintf(g_transfer.error_message, sizeof(g_transfer.error_message),
                 "Cannot open %s", getfname(filePath));
        pthread_mutex_unlock(&g_transfer.lock);
        return -5;
    }

    xmodem_set_current_file(filePath);

    xmodem_begin_session((long)(mode | SEND));

    g_transfer.xm.total_files = 1;
    g_transfer.xm.total_bytes = filelength(fileno(fp));

    pthread_mutex_lock(&g_transfer.lock);
    g_transfer.total_size = g_transfer.xm.total_bytes;
    pthread_mutex_unlock(&g_transfer.lock);

    BOOL success = xmodem_send_file(&g_transfer.xm, filePath, fp, NULL, NULL);

    /* YMODEM signs a batch off with an empty header block; without it the
     * receiver sits waiting for the next file. */
    if (success && (g_xm_mode & YMODEM)) {
        if (xmodem_get_mode(&g_transfer.xm)) {
            uchar term_block[XMODEM_MIN_BLOCK_SIZE];
            LOGI("Sending YMODEM termination block");
            memset(term_block, 0, sizeof(term_block));
            xmodem_put_block(&g_transfer.xm, term_block, sizeof(term_block), 0);
            if (xmodem_get_ack(&g_transfer.xm, /* tries: */ 6, /* block_num: */ 0) != ACK)
                LOGW("No ACK after the YMODEM terminating block");
        }
    }

    fclose(fp);
    transfer_end_session();

    pthread_mutex_lock(&g_transfer.lock);
    if (g_transfer.cancelled) {
        g_transfer.state = TRANSFER_CANCELLED;
        LOGI("X/YMODEM send cancelled");
    } else if (!success) {
        g_transfer.state = TRANSFER_ERROR;
        if (g_transfer.error_message[0] == '\0') {
            snprintf(g_transfer.error_message, sizeof(g_transfer.error_message),
                     "%s send failed", (mode & YMODEM) ? "YMODEM" : "XMODEM");
        }
        LOGE("X/YMODEM send failed");
    } else {
        g_transfer.state = TRANSFER_COMPLETE;
        LOGI("X/YMODEM send complete");
    }
    pthread_mutex_unlock(&g_transfer.lock);

    return success ? 0 : -7;
}

/**
 * Cancel current transfer
 */
SYNCTERM_API void syncterm_transfer_cancel(void) {
    LOGD("syncterm_transfer_cancel called");

    pthread_mutex_lock(&g_transfer.lock);
    g_transfer.cancelled = 1;
    pthread_mutex_unlock(&g_transfer.lock);

    /* Send the canonical ZMODEM cancel sequence (8x CAN, 10x BS) straight
     * down the connection.
     *
     * This deliberately does NOT go through zmodem_send_zabort(): that would
     * push bytes into the shared TX staging buffer from the UI thread while
     * the transfer thread is writing to it. conn_send() has its own lock, so
     * writing here is safe, and CAN bytes are what a ZMODEM peer actually
     * watches for. The transfer thread notices the cancelled flag on its next
     * is_cancelled() check and unwinds the session itself. */
    if (g_transfer_initialized && conn_connected()) {
        static const unsigned char cancel_seq[] = {
            0x18, 0x18, 0x18, 0x18, 0x18, 0x18, 0x18, 0x18,   /* CAN x8 */
            0x08, 0x08, 0x08, 0x08, 0x08,
            0x08, 0x08, 0x08, 0x08, 0x08                      /* BS  x10 */
        };
        conn_send((void *)cancel_seq, sizeof(cancel_seq), 1000);
    }
}

/**
 * Get current transfer state
 * Returns: transfer_state_t enum value
 */
SYNCTERM_API int syncterm_get_transfer_state(void) {
    pthread_mutex_lock(&g_transfer.lock);
    int state = (int)g_transfer.state;
    pthread_mutex_unlock(&g_transfer.lock);

    return state;
}

/**
 * Get transfer progress
 * Writes current_pos and total_size into the progress array (2 elements).
 * Returns: 1 on success
 */
SYNCTERM_API int syncterm_get_transfer_progress(int64_t* progress) {
    if (!progress) {
        return 0;
    }

    pthread_mutex_lock(&g_transfer.lock);
    progress[0] = g_transfer.current_pos;
    progress[1] = g_transfer.total_size;
    pthread_mutex_unlock(&g_transfer.lock);

    return 1;
}

/**
 * Get batch file index for the current transfer.
 * Writes the 1-based index of the file being sent and the total file count.
 * For single-file transfers both stay 0. Returns 1 on success, 0 on bad args.
 */
SYNCTERM_API int syncterm_get_transfer_file_index(int* current, int* total) {
    if (!current || !total) {
        return 0;
    }

    pthread_mutex_lock(&g_transfer.lock);
    *current = g_transfer.current_file_index;
    *total = g_transfer.total_files;
    pthread_mutex_unlock(&g_transfer.lock);

    return 1;
}

/**
 * Get current file name being transferred
 * Copies the filename into the provided buffer.
 * Returns: length of the filename string, or 0 if empty
 */
SYNCTERM_API int syncterm_get_transfer_filename(char* buffer, int buffer_len) {
    if (!buffer || buffer_len <= 0) {
        return 0;
    }

    pthread_mutex_lock(&g_transfer.lock);
    strncpy(buffer, g_transfer.current_file, buffer_len - 1);
    buffer[buffer_len - 1] = '\0';
    pthread_mutex_unlock(&g_transfer.lock);

    return (int)strlen(buffer);
}

/**
 * Get last error message
 * Copies the error message into the provided buffer.
 * Returns: length of the error string, or 0 if empty
 */
SYNCTERM_API int syncterm_get_transfer_error(char* buffer, int buffer_len) {
    if (!buffer || buffer_len <= 0) {
        return 0;
    }

    pthread_mutex_lock(&g_transfer.lock);
    strncpy(buffer, g_transfer.error_message, buffer_len - 1);
    buffer[buffer_len - 1] = '\0';
    pthread_mutex_unlock(&g_transfer.lock);

    return (int)strlen(buffer);
}

/**
 * Reset transfer state to idle
 */
SYNCTERM_API void syncterm_transfer_reset(void) {
    LOGD("syncterm_transfer_reset called");

    pthread_mutex_lock(&g_transfer.lock);
    g_transfer.state = TRANSFER_IDLE;
    g_transfer.cancelled = 0;
    g_transfer.current_pos = 0;
    g_transfer.total_size = 0;
    g_transfer.current_file_index = 0;
    g_transfer.total_files = 0;
    g_transfer.current_file[0] = '\0';
    g_transfer.error_message[0] = '\0';
    pthread_mutex_unlock(&g_transfer.lock);
    g_diag.largest_rx_block = 0;   /* no stale "8K" title from the last download */
}

/**
 * Cleanup transfer system
 */
SYNCTERM_API void syncterm_transfer_cleanup(void) {
    LOGD("syncterm_transfer_cleanup called");

    if (!g_transfer_initialized) {
        return;
    }

    pthread_mutex_destroy(&g_transfer.lock);
    g_transfer_initialized = 0;
}
