/**
 * Linux stubs for SyncTERM dependencies that aren't needed on Linux/Avalonia.
 *
 * This file provides stub implementations for UI-related functions
 * that are referenced by the connection code but not used when
 * hidepopups is set to true.
 *
 * Ported from android_stubs.c for the Linux/Avalonia build.
 */

#include <stdlib.h>
#include <string.h>
#include <stdint.h>
#include <stddef.h>
#include <stdio.h>
#include <stdarg.h>
#include <stdbool.h>
#include <pthread.h>
#include <unistd.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <errno.h>

// Include ciolib.h early to get struct definitions and correct function signatures
#include "ciolib.h"
#include "linux_ciolib.h"
#include "bbslist.h"
#include "cterm.h"

#define LOGI(fmt, ...) do { char _buf[512]; snprintf(_buf, sizeof(_buf), "SyncTERM-Stubs: " fmt "\n", ##__VA_ARGS__); fprintf(stderr, "%s", _buf); } while(0)
#define LOGW(fmt, ...) do { char _buf[512]; snprintf(_buf, sizeof(_buf), "SyncTERM-Stubs WARN: " fmt "\n", ##__VA_ARGS__); fprintf(stderr, "%s", _buf); } while(0)

// Forward declaration for the audio command handler in syncterm_bridge.c
extern void linux_audio_command(const char *cmd, size_t len);

// Bridge: cterm.c calls android_audio_command() for OSC 800 — forward to Linux handler
void android_audio_command(const char *cmd, size_t len) {
    LOGI("android_audio_command CALLED: len=%zu cmd='%.128s'", len, cmd ? cmd : "(null)");
    linux_audio_command(cmd, len);
}

// Linux app files directory path (set from C# managed code via P/Invoke)
char g_linux_files_dir[512] = {0};

// Set the Linux files directory (called from C# managed code)
void linux_set_files_dir(const char *path) {
    if (!path) {
        // Auto-detect using XDG_CONFIG_HOME or HOME
        const char *config_home = getenv("XDG_CONFIG_HOME");
        if (config_home && config_home[0]) {
            snprintf(g_linux_files_dir, sizeof(g_linux_files_dir), "%s/TERMinator", config_home);
        } else {
            const char *home = getenv("HOME");
            if (home && home[0]) {
                snprintf(g_linux_files_dir, sizeof(g_linux_files_dir), "%s/.config/TERMinator", home);
            }
        }
        if (g_linux_files_dir[0] != '\0') {
            mkdir(g_linux_files_dir, 0755);
            LOGI("Files directory auto-set to: %s", g_linux_files_dir);
        } else {
            LOGW("linux_set_files_dir: XDG_CONFIG_HOME and HOME not set");
        }
        return;
    }
    size_t path_len = strlen(path);
    if (path_len >= sizeof(g_linux_files_dir)) {
        LOGW("linux_set_files_dir: path too long (%zu >= %zu)",
             path_len, sizeof(g_linux_files_dir));
        return;
    }
    strncpy(g_linux_files_dir, path, sizeof(g_linux_files_dir) - 1);
    g_linux_files_dir[sizeof(g_linux_files_dir) - 1] = '\0';
    LOGI("Files directory set to: %s", g_linux_files_dir);
}

// =============================================================================
// SSH Debug Log - accumulates diagnostic messages during SSH/TelnetS connections
// =============================================================================

#define SSH_DEBUG_LOG_SIZE (16 * 1024)
static char     g_ssh_debug_log[SSH_DEBUG_LOG_SIZE];
static int      g_ssh_debug_log_len = 0;
static pthread_mutex_t g_ssh_debug_lock = PTHREAD_MUTEX_INITIALIZER;

void ssh_debug_log(const char *fmt, ...) {
    if (!fmt) return;
    char buf[1024];
    va_list ap;
    va_start(ap, fmt);
    int n = vsnprintf(buf, sizeof(buf), fmt, ap);
    va_end(ap);
    if (n <= 0) return;
    if (n >= (int)sizeof(buf)) n = (int)sizeof(buf) - 1;

    pthread_mutex_lock(&g_ssh_debug_lock);
    int avail = SSH_DEBUG_LOG_SIZE - 1 - g_ssh_debug_log_len;
    if (n > avail) n = avail;
    if (n > 0) {
        memcpy(g_ssh_debug_log + g_ssh_debug_log_len, buf, n);
        g_ssh_debug_log_len += n;
        g_ssh_debug_log[g_ssh_debug_log_len] = '\0';
    }
    pthread_mutex_unlock(&g_ssh_debug_lock);
}

int ssh_debug_get_log(char *buf, int buflen) {
    if (!buf || buflen <= 0) return 0;
    pthread_mutex_lock(&g_ssh_debug_lock);
    int copy = g_ssh_debug_log_len;
    if (copy >= buflen) copy = buflen - 1;
    if (copy > 0) memcpy(buf, g_ssh_debug_log, copy);
    buf[copy] = '\0';
    pthread_mutex_unlock(&g_ssh_debug_lock);
    return copy;
}

void ssh_debug_clear_log(void) {
    pthread_mutex_lock(&g_ssh_debug_lock);
    g_ssh_debug_log_len = 0;
    g_ssh_debug_log[0] = '\0';
    pthread_mutex_unlock(&g_ssh_debug_lock);
}

void ssh_debug_reset_retry_count(void) {
    // Reset handled in uifcinput
}

// uifc stub structure and functions
typedef struct {
    int (*pop)(const char *);
    int scrn_len;
    int scrn_width;
} uifc_api_t;

static int stub_pop(const char *str) {
    if (str) {
        LOGI("uifc.pop: %s", str);
    }
    return 0;
}

uifc_api_t uifc = {
    .pop = stub_pop,
    .scrn_len = 25,
    .scrn_width = 80
};

// uifc message functions
void uifcmsg(const char *str, const char *help) {
    if (str) {
        LOGW("uifcmsg: %s", str);
        ssh_debug_log("[uifcmsg] %s\n", str);
    }
    if (help) {
        ssh_debug_log("[uifcmsg detail] %s\n", help);
    }
}

// g_bbs_config is defined in syncterm_bridge.c
extern struct bbslist g_bbs_config;

int uifcinput(const char *mode, int maxlen, char *str, int opts, const char *help) {
    // Handle SSH credential prompts by providing values from g_bbs_config
    // (the native SSH code calls uifcinput when it needs credentials,
    //  but since we're in bridge mode there's no UI to show)
    if (mode && str && maxlen > 0) {
        if (strcmp(mode, "UserID") == 0 && g_bbs_config.user[0] != '\0') {
            strncpy(str, g_bbs_config.user, maxlen);
            str[maxlen] = '\0';
            LOGI("uifcinput: provided UserID from config: %s", str);
            ssh_debug_log("[auth] Username provided: %s\n", str);
            return (int)strlen(str);
        }
        if (strcmp(mode, "Password") == 0 && g_bbs_config.password[0] != '\0') {
            strncpy(str, g_bbs_config.password, maxlen);
            str[maxlen] = '\0';
            LOGI("uifcinput: provided Password from config (len=%zu)", strlen(str));
            ssh_debug_log("[auth] Password provided (length=%zu)\n", strlen(str));
            return (int)strlen(str);
        }
    }
    LOGW("uifcinput: no value for mode='%s' — returning cancel", mode ? mode : "(null)");
    ssh_debug_log("[auth] uifcinput cancelled for mode='%s'\n", mode ? mode : "(null)");
    return -1;  // Cancel for anything we can't handle
}

// uifc initialization
void init_uifc(int force_remote, int force_login) {
    LOGI("init_uifc called (stubbed)");
}

void uifc_bail(void) {
    LOGI("uifc_bail called (stubbed)");
}

// Window/popup functions
int popup_menu(int items, char **item_list, char *title, int top, int left) {
    return -1;  // Cancel
}

void load_font_files(void) {
    LOGI("load_font_files called (stubbed)");
}

// Settings — must be large enough to match the real syncterm_settings struct
// (ssh.c accesses settings.list_path which is at a deep offset)
// Using a padded buffer to avoid memory corruption without pulling in syncterm.h
struct syncterm_settings {
    void *webgets;           /* named_string_t **webgets */
    int   confirm_close;
    int   startup_mode;
    int   output_mode;
    int   backlines;
    int   prompt_save;
    char  _modem_pad[1088];  /* struct modem_settings mdm (approx) */
    char  TERM[512];         /* char TERM[INI_MAX_VALUE_LEN+1] */
    char  list_path[260];    /* char list_path[MAX_PATH+1] */
    char  stored_list_path[260];
    double scaling_factor;
    int   xfer_failure_keypress_timeout;
    int   xfer_success_keypress_timeout;
    int   custom_cols;
    int   custom_rows;
    int   custom_fontheight;
    int   custom_aw;
    int   custom_ah;
    int   window_width;
    int   window_height;
    int   left_just;
    int   blocky;
    int   extern_scale;
    unsigned int audio_output_modes;
    int   invert_wheel;
    int   webgetUserList;
    int   keyDerivationIterations;
    int   defaultCursor;
};

struct syncterm_settings settings = {0};

// Terminal dimensions (defined in syncterm_bridge.c)
extern int g_term_width;
extern int g_term_height;

// Other stubs that might be needed
void get_cterm_size(int *cols, int *rows, int force_full) {
    if (cols) *cols = g_term_width;
    if (rows) *rows = g_term_height;
}

void term_title(const char *title) {
    LOGI("term_title: %s", title ? title : "(null)");
}

// Global variables that might be referenced
int quession = 0;
int *startup_mode = NULL;

// ANSI music stub
void play_music(const char *music_str) {
    LOGI("play_music called (not implemented on Windows)");
}

// Font change from BBS (e.g., SAUCE record or escape sequence)
int setfont_from_bbs(int font_num, int force) {
    LOGI("setfont_from_bbs: font_num=%d, force=%d", font_num, force);
    return ciolib_setfont(font_num, force, 0);
}

// Status line stubs
void setup_status_line(void) {
    // No-op on Linux/Avalonia
}

void update_status(int conn_status, int speed) {
    // No-op on Linux/Avalonia
}

// Log stubs
void conn_log(int level, const char *fmt, ...) {
    // Could be implemented to use fprintf(stderr) if needed
}

// =============================================================================
// ciolib pixel/graphics stubs
// =============================================================================

// Pixel manipulation - forwards to linux_ciolib for sixel graphics rendering
// Signatures must match ciolib.h exactly
int ciolib_setpixels(uint32_t sx, uint32_t sy, uint32_t ex, uint32_t ey,
                     uint32_t x_off, uint32_t y_off, uint32_t mx_off, uint32_t my_off,
                     struct ciolib_pixels *pixels, struct ciolib_mask *mask) {
    return linux_ciolib_setpixels(sx, sy, ex, ey, x_off, y_off, mx_off, my_off, pixels, mask);
}

struct ciolib_pixels *ciolib_getpixels(uint32_t sx, uint32_t sy, uint32_t ex, uint32_t ey, int force) {
    (void)sx; (void)sy; (void)ex; (void)ey; (void)force;
    return NULL;  // Not supported
}

void ciolib_freepixels(struct ciolib_pixels *pixels) {
    (void)pixels;
    // No-op
}

// Custom cursor — matches bitmap_getcustomcursor/bitmap_setcustomcursor from bitmap_con.c
void ciolib_getcustomcursor(int *startline, int *endline, int *range, int *blink, int *visible) {
    if (startline) *startline = linux_ciolib_get_cursor_start();
    if (endline) *endline = linux_ciolib_get_cursor_end();
    if (range) {
        int h = linux_ciolib_get_char_pixel_height();
        *range = h > 0 ? h : 16;
    }
    if (blink) *blink = linux_ciolib_get_cursor_blinks();
    if (visible) *visible = linux_ciolib_is_cursor_visible();
}

void ciolib_setcustomcursor(int startline, int endline, int range, int blink, int visible) {
    linux_ciolib_set_custom_cursor(startline, endline, range, blink, visible);
}

// RGB color mapping — must match bitmap_map_rgb() in bitmap_con.c.
// Parameters are uint16_t with color data in the UPPER byte (values passed as R<<8).
// Sets CIOLIB_COLOR_RGB flag (0x80000000) so downstream code knows it's RGB.
uint32_t ciolib_map_rgb(uint16_t r, uint16_t g, uint16_t b) {
    return 0x80000000u | ((r & 0xff00) << 8) | (g & 0xff00) | (b >> 8);
}

// =============================================================================
// Audio stubs - beep and tone generation
// =============================================================================

// xptone - returns 0 on success, -1 on failure
int xptone(double freq, unsigned long duration, unsigned volume) {
    LOGI("xptone: freq=%.2f dur=%lu vol=%u (not implemented)", freq, duration, volume);
    return 0;  // Pretend success
}

int xptone_complete(void) {
    return 1;  // Tone completed
}

int xptone_open(void) {
    return 0;  // Success
}

int xptone_close(void) {
    return 0;  // Success
}

void xpbeep(void) {
    LOGI("xpbeep (not implemented)");
}

// =============================================================================
// Emulation mode
// =============================================================================

/* Mirrors bbslist.c's get_emulation(): the screen mode decides the emulation. */
cterm_emulation_t get_emulation(struct bbslist *bbs) {
    if (!bbs)
        return CTERM_EMULATION_ANSI_BBS;
    switch (bbs->screen_mode) {
        case SCREEN_MODE_C64:
        case SCREEN_MODE_C128_40:
        case SCREEN_MODE_C128_80:
            return CTERM_EMULATION_PETASCII;
        case SCREEN_MODE_ATARI:
        case SCREEN_MODE_ATARI_XEP80:
            return CTERM_EMULATION_ATASCII;
        default:
            return CTERM_EMULATION_ANSI_BBS;
    }
}

const char* get_emulation_str(struct bbslist *bbs) {
    // Check if custom term name is set
    if (bbs && bbs->term_name[0])
        return bbs->term_name;

    // Return string based on emulation type
    switch (get_emulation(bbs)) {
        case CTERM_EMULATION_ANSI_BBS:
            return "syncterm";
        case CTERM_EMULATION_PETASCII:
            return "PETSCII";
        case CTERM_EMULATION_ATASCII:
            return "ATASCII";
        case CTERM_EMULATION_PRESTEL:
            return "Prestel";
        case CTERM_EMULATION_BEEB:
            return "Beeb7";
        case CTERM_EMULATION_ATARIST_VT52:
            return "AtariST+VT52";
        default:
            return "syncterm";
    }
}

// =============================================================================
// Serial/Modem stubs - not needed for Telnet-only
// =============================================================================

// Serial port functions
typedef void* COM_HANDLE;
#define COM_HANDLE_INVALID NULL

int modem_connect(void *bbs) {
    LOGW("modem_connect called - not supported on Linux/Avalonia");
    return -1;  // Failure
}

void modem_close(void) {
    // No-op
}

void serial_close(COM_HANDLE h) {
    // No-op
}

COM_HANDLE serial_open(const char *port) {
    return COM_HANDLE_INVALID;
}

int serial_read(COM_HANDLE h, void *buf, size_t len) {
    return -1;
}

int serial_write(COM_HANDLE h, const void *buf, size_t len) {
    return -1;
}

// =============================================================================
// SSH/SFTP stubs - disabled via WITHOUT_CRYPTLIB
// =============================================================================

#ifdef WITHOUT_CRYPTLIB
int ssh_connect(void *bbs) {
    return -1;
}

void ssh_close(void) {
}
#endif

// =============================================================================
// RIP graphics stubs (if referenced)
// =============================================================================

void rip_reset(void) {
    // No-op
}

int rip_parse(unsigned char *buf, int len) {
    return 0;  // Not supported
}

// =============================================================================
// PTY stubs - not needed on Linux/Avalonia
// =============================================================================

int pty_connect(void *bbs) {
    LOGW("pty_connect called - not supported on Linux/Avalonia");
    return -1;
}

void pty_close(void) {
    // No-op
}

// =============================================================================
// ciolib cputs and font replacement
// =============================================================================

int ciolib_cputs(const char *str) {
    // Simple implementation - write string character by character
    extern int ciolib_putch(int);
    if (!str) return 0;
    int count = 0;
    while (*str) {
        ciolib_putch(*str++);
        count++;
    }
    return count;
}

void ciolib_replace_font(uint8_t id, char *name, void *data, size_t size) {
    (void)id; (void)name; (void)data; (void)size;
    LOGI("ciolib_replace_font called (not implemented)");
    // Not supported
}

// =============================================================================
// Logging variables
// =============================================================================

FILE *log_fp = NULL;
char *log_levels[] = {
    "EMERG",
    "ALERT",
    "CRIT",
    "ERR",
    "WARNING",
    "NOTICE",
    "INFO",
    "DEBUG"
};

// =============================================================================
// SSH-related stubs
// =============================================================================

// SYNCTERM_PATH types from syncterm.h
#define SYNCTERM_PATH_INI 0
#define SYNCTERM_PATH_LIST 1
#define SYNCTERM_PATH_CACHE 3
#define SYNCTERM_PATH_KEYS 4
#define SYNCTERM_PATH_SYSTEM_CACHE 5

// Get syncterm configuration file path
char *get_syncterm_filename(char *fn, int fnlen, int type, bool shared) {
    (void)shared;

    if (!fn || fnlen <= 0) {
        return fn;
    }

    fn[0] = '\0';

    // If files directory isn't set, try auto-detecting
    if (g_linux_files_dir[0] == '\0') {
        linux_set_files_dir(NULL);
    }

    // If still not set, return empty path
    if (g_linux_files_dir[0] == '\0') {
        LOGW("get_syncterm_filename: files directory not set");
        return fn;
    }

    const char *filename;
    switch (type) {
        case SYNCTERM_PATH_KEYS:
            filename = "ssh_keys.p15";
            break;
        case SYNCTERM_PATH_INI:
            filename = "syncterm.ini";
            break;
        case SYNCTERM_PATH_LIST:
            filename = "syncterm.lst";
            break;
        case SYNCTERM_PATH_CACHE:
        case SYNCTERM_PATH_SYSTEM_CACHE:
            filename = "cache";
            break;
        default:
            filename = "syncterm.dat";
            break;
    }

    snprintf(fn, fnlen, "%s/%s", g_linux_files_dir, filename);
    LOGI("get_syncterm_filename: type=%d -> %s", type, fn);
    return fn;
}

// Alias for uifc_bail (some code uses uifcbail)
void uifcbail(void) {
    uifc_bail();
}

// Get terminal window size
void get_term_win_size(int *width, int *height, int *pixelw, int *pixelh, int *nostatus) {
    if (width) *width = g_term_width;
    if (height) *height = g_term_height;
    if (pixelw) *pixelw = 0;
    if (pixelh) *pixelh = 0;
    if (nostatus) *nostatus = 1;
}

// BBS list INI reading - not needed for Linux/Avalonia connection flow
str_list_t iniReadBBSList(FILE *fp, bool userList) {
    (void)fp; (void)userList;
    return NULL;
}

// INI file style - global variable
ini_style_t ini_style = {0};

// Font lookup - ConPTY uses this but we don't need font ID resolution
int find_font_id(char *name) {
    (void)name;
    return 0; // Default font
}
