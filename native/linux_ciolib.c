/**
 * Linux ciolib implementation
 *
 * Provides stub implementations of ciolib functions for Linux/Avalonia.
 * Instead of rendering directly, this stores terminal state in a buffer
 * that can be read via P/Invoke for rendering in Linux.
 */

#include <stdlib.h>
#include <string.h>
#include <stdbool.h>
#include <stdio.h>
#include <pthread.h>
#include <unistd.h>

#define LOGI(fmt, ...) do { char _buf[512]; snprintf(_buf, sizeof(_buf), "SyncTERM-CIO: " fmt "\n", ##__VA_ARGS__); fprintf(stderr, "%s", _buf); } while(0)
#define LOGE(fmt, ...) do { char _buf[512]; snprintf(_buf, sizeof(_buf), "SyncTERM-CIO ERROR: " fmt "\n", ##__VA_ARGS__); fprintf(stderr, "%s", _buf); } while(0)

#include "safe_math.h"
#include "linux_ciolib.h"
#include "ciolib.h"
#include "vidmodes.h"
#include "pic_bridge.h"

// Maximum terminal dimensions to prevent unreasonable allocations
#define MAX_TERMINAL_WIDTH  1000
#define MAX_TERMINAL_HEIGHT 1000

// Global ciolib variables that must be defined
struct text_info cio_textinfo;
cioapi_t cio_api;
int _wscroll = 1;
int directvideo = 0;
int hold_update = 0;
int puttext_can_move = 0;
int ciolib_reaper = 0;
const char *ciolib_appname = "SyncTERM";
double ciolib_initial_scaling = 1.0;
int ciolib_initial_mode = C80;
enum ciolib_scaling ciolib_initial_scaling_type = CIOLIB_SCALING_INTERNAL;
const void *ciolib_initial_icon = NULL;
size_t ciolib_initial_icon_width = 0;
const char *ciolib_initial_program_name = "SyncTERM";
const char *ciolib_initial_program_class = "SyncTERM";
bool ciolib_swap_mouse_butt45 = false;
uint32_t ciolib_fg = 7;  // Light gray
uint32_t ciolib_bg = 0;  // Black

// Font data from allfonts.c (extern - actual data is in allfonts.c)
extern struct conio_font_data_struct conio_fontdata[257];

// Current font ID tracked in syncterm_bridge.c (non-static for cross-TU access)
extern int g_current_font_id;

/* Font slot tracking – mirrors bitmap_con.c's default_font / current_font[] */
static int linux_default_font = 0;
static int linux_current_font[4] = {0, 0, 0, 0};

// Video flags (CIOLIB_VIDEO_BGBRIGHT etc.) stored by setvideoflags/getvideoflags
static volatile int linux_video_flags = 0;

// Linux-specific screen state
static struct linux_screen_state {
    struct vmem_cell *screen;
    int width;
    int height;
    int cursor_x;
    int cursor_y;
    int cursor_visible;
    int cursor_type;
    uint8_t current_attr;
    uint8_t current_flags;
    uint32_t fg_color;
    uint32_t bg_color;
    int dirty;
    // Dirty region tracking for partial redraws
    int dirty_min_x;
    int dirty_max_x;
    int dirty_min_y;
    int dirty_max_y;
    pthread_mutex_t mutex;
    uint32_t palette[65536];
    // Sixel pixel framebuffer
    uint32_t *pixel_buffer;    // ARGB framebuffer (alpha=0 transparent, 0xFF opaque)
    int pixel_width;           // Buffer width in pixels
    int pixel_height;          // Buffer height in pixels
    int pixel_dirty;           // New data since last read
    int pixel_has_data;        // ANY sixel data has been written
    // Changed-pixel rectangle since the app last copied the layer (inclusive; x1 < x0 = none recorded,
    // so a dirty layer with no rectangle copies whole)
    int pixel_dirty_x0;
    int pixel_dirty_y0;
    int pixel_dirty_x1;
    int pixel_dirty_y1;
    int char_pixel_height;     // Character cell height in pixels (e.g. 16 for 8x16 font)
    int vmode_override;        // Explicit ciolib video mode (C64_40X25 etc.), -1 = derive from geometry
    // Scanline-based cursor state (matching bitmap_con.c)
    int curs_start;            // Top scanline of cursor within cell
    int curs_end;              // Bottom scanline of cursor within cell
    int curs_blinks;           // Whether cursor blinks (1=yes, 0=steady)
    int default_curs_start;    // Default curs_start for _NORMALCURSOR
    int default_curs_end;      // Default curs_end for _NORMALCURSOR
} linux_state = {
    .screen = NULL,
    .width = 80,
    .height = 25,
    .cursor_x = 1,
    .cursor_y = 1,
    .cursor_visible = 1,
    .cursor_type = _NORMALCURSOR,
    .current_attr = 7,
    .fg_color = 7,
    .bg_color = 0,
    .dirty = 0,
    .dirty_min_x = 0,
    .dirty_max_x = 0,
    .dirty_min_y = 0,
    .dirty_max_y = 0,
    /* .mutex initialized in initciolib() */
    .palette = {
        0x000000, // Black
        0x0000AA, // Blue
        0x00AA00, // Green
        0x00AAAA, // Cyan
        0xAA0000, // Red
        0xAA00AA, // Magenta
        0xAA5500, // Brown
        0xAAAAAA, // Light Gray
        0x555555, // Dark Gray
        0x5555FF, // Light Blue
        0x55FF55, // Light Green
        0x55FFFF, // Light Cyan
        0xFF5555, // Light Red
        0xFF55FF, // Light Magenta
        0xFFFF55, // Yellow
        0xFFFFFF  // White
        /* remaining entries zero-initialized */
    },
    .pixel_buffer = NULL,
    .pixel_width = 0,
    .pixel_height = 0,
    .pixel_dirty = 0,
    .pixel_has_data = 0,
    .pixel_dirty_x1 = -1,
    .pixel_dirty_y1 = -1,
    .char_pixel_height = 0,
    .vmode_override = -1,
    .curs_start = 14,
    .curs_end = 15,
    .curs_blinks = 1,
    .default_curs_start = 14,
    .default_curs_end = 15
};

// Forward declarations
static void linux_clreol(void);
static int linux_puttext(int sx, int sy, int ex, int ey, void *buf);
static int linux_vmem_puttext(int sx, int sy, int ex, int ey, struct vmem_cell *buf);
static int linux_gettext(int sx, int sy, int ex, int ey, void *buf);
static int linux_vmem_gettext(int sx, int sy, int ex, int ey, struct vmem_cell *buf);
static void linux_textattr(int attr);
static int linux_kbhit(void);
static int linux_kbwait(int timeout);
static void linux_delay(long ms);
static int linux_wherex(void);
static int linux_wherey(void);
static int linux_putch(int c);
static void linux_gotoxy(int x, int y);
static void linux_clrscr(void);
static void linux_gettextinfo(struct text_info *info);
static void linux_setcursortype(int type);
static int linux_current_vmode(void);
static void linux_apply_mode_cursor_locked(void);
static int linux_getch(void);
static int linux_getche(void);
static void linux_beep(void);
static void linux_highvideo(void);
static void linux_lowvideo(void);
static void linux_normvideo(void);
static void linux_textmode(int mode);
static int linux_ungetch(int ch);
static int linux_movetext(int sx, int sy, int ex, int ey, int dx, int dy);
static void linux_wscroll(void);
static void linux_window(int sx, int sy, int ex, int ey);
static void linux_delline(void);
static void linux_insline(void);
static void linux_textbackground(int color);
static void linux_textcolor(int color);
static void linux_settitle(const char *title);
static void linux_setname(const char *name);
static int linux_setfont(int font, int force, int font_num);
static int linux_getfont(int font_num);
static void linux_setvideoflags(int flags);
static int linux_getvideoflags(void);
static int linux_setpalette(uint32_t entry, uint16_t r, uint16_t g, uint16_t b);
static int linux_attr2palette(uint8_t attr, uint32_t *fg, uint32_t *bg);

// Input buffer for keyboard input from managed code
#define INPUT_BUFFER_SIZE 256
static unsigned char input_buffer[INPUT_BUFFER_SIZE];
static int input_head = 0;
static int input_tail = 0;
static pthread_mutex_t input_mutex;
static int input_mutex_initialized = 0;

// Public functions for managed code access
int linux_ciolib_get_screen_width(void) {
    return linux_state.width;
}

int linux_ciolib_get_screen_height(void) {
    return linux_state.height;
}

int linux_ciolib_get_cursor_x(void) {
    return linux_state.cursor_x;
}

int linux_ciolib_get_cursor_y(void) {
    return linux_state.cursor_y;
}

int linux_ciolib_is_cursor_visible(void) {
    /* Match cursor_visible_locked() from bitmap_con.c:383-396 */
    if (!linux_state.cursor_visible)
        return 0;
    if (linux_state.curs_start > linux_state.curs_end)
        return 0;
    return 1;  /* Blink phase handled in C# timer */
}

int linux_ciolib_get_cursor_type(void) {
    return linux_state.cursor_type;
}

int linux_ciolib_get_cursor_start(void) {
    return linux_state.curs_start;
}

int linux_ciolib_get_cursor_end(void) {
    return linux_state.curs_end;
}

int linux_ciolib_get_cursor_blinks(void) {
    return linux_state.curs_blinks;
}

/* Resolve ciolib_fg to an RGB color, matching color_value() in bitmap_con.c. */
uint32_t linux_ciolib_get_fg_color(void) {
    uint32_t col = ciolib_fg;
    if (col & 0x80000000u)  /* CIOLIB_COLOR_RGB */
        return col & 0x00FFFFFFu;
    uint32_t idx = col & 0x00FFFFFFu;
    if (idx < sizeof(linux_state.palette) / sizeof(linux_state.palette[0]))
        return linux_state.palette[idx] & 0x00FFFFFFu;
    return linux_state.palette[7] & 0x00FFFFFFu;  /* fallback: light gray */
}

int linux_ciolib_get_char_pixel_height(void) {
    return linux_state.char_pixel_height;
}

void linux_ciolib_set_custom_cursor(int s, int e, int r, int b, int v) {
    double ratio;

    pthread_mutex_lock(&linux_state.mutex);
    if (r == 0)
        ratio = 1;
    else {
        int charh = linux_state.char_pixel_height > 0 ? linux_state.char_pixel_height : 16;
        ratio = charh / r;  /* integer division, matching bitmap_con.c exactly */
    }
    if (s >= 0)
        linux_state.curs_start = (int)(s * ratio);
    if (e >= 0)
        linux_state.curs_end = (int)(e * ratio);
    if (b >= 0)
        linux_state.curs_blinks = b;
    if (v >= 0)
        linux_state.cursor_visible = v;
    linux_state.dirty = 1;
    pthread_mutex_unlock(&linux_state.mutex);
}

int linux_ciolib_is_dirty(void) {
    return linux_state.dirty;
}

void linux_ciolib_clear_dirty(void) {
    linux_state.dirty = 0;
    // Reset dirty region to invalid state
    linux_state.dirty_min_x = linux_state.width;
    linux_state.dirty_max_x = 0;
    linux_state.dirty_min_y = linux_state.height;
    linux_state.dirty_max_y = 0;
}

// Get dirty region bounds (returns 0 if no dirty region, 1 if valid)
int linux_ciolib_get_dirty_region(int *min_x, int *min_y, int *max_x, int *max_y) {
    if (!linux_state.dirty) {
        return 0;
    }
    *min_x = linux_state.dirty_min_x;
    *min_y = linux_state.dirty_min_y;
    *max_x = linux_state.dirty_max_x;
    *max_y = linux_state.dirty_max_y;
    return 1;
}

// Mark a cell as dirty and expand dirty region
static inline void mark_cell_dirty(int x, int y) {
    linux_state.dirty = 1;
    if (x < linux_state.dirty_min_x) linux_state.dirty_min_x = x;
    if (x > linux_state.dirty_max_x) linux_state.dirty_max_x = x;
    if (y < linux_state.dirty_min_y) linux_state.dirty_min_y = y;
    if (y > linux_state.dirty_max_y) linux_state.dirty_max_y = y;
}

// Mark a rectangular region as dirty
static inline void mark_region_dirty(int x1, int y1, int x2, int y2) {
    linux_state.dirty = 1;
    if (x1 < linux_state.dirty_min_x) linux_state.dirty_min_x = x1;
    if (x2 > linux_state.dirty_max_x) linux_state.dirty_max_x = x2;
    if (y1 < linux_state.dirty_min_y) linux_state.dirty_min_y = y1;
    if (y2 > linux_state.dirty_max_y) linux_state.dirty_max_y = y2;
}

// Mark entire screen as dirty
static inline void mark_screen_dirty(void) {
    linux_state.dirty = 1;
    linux_state.dirty_min_x = 0;
    linux_state.dirty_max_x = linux_state.width - 1;
    linux_state.dirty_min_y = 0;
    linux_state.dirty_max_y = linux_state.height - 1;
}

struct vmem_cell* linux_ciolib_get_screen_buffer(void) {
    return linux_state.screen;
}

void linux_ciolib_lock(void) {
    pthread_mutex_lock(&linux_state.mutex);
}

void linux_ciolib_unlock(void) {
    pthread_mutex_unlock(&linux_state.mutex);
}

uint32_t* linux_ciolib_get_palette(void) {
    return linux_state.palette;
}

// Push keyboard input from managed code
void linux_ciolib_push_input(unsigned char c) {
    pthread_mutex_lock(&input_mutex);
    int next = (input_head + 1) % INPUT_BUFFER_SIZE;
    if (next != input_tail) {
        input_buffer[input_head] = c;
        input_head = next;
    }
    pthread_mutex_unlock(&input_mutex);
}

void linux_ciolib_push_input_buffer(const unsigned char *buf, int len) {
    // Validate parameters - reject NULL buffer or negative/zero length
    if (buf == NULL || len <= 0) {
        return;
    }

    pthread_mutex_lock(&input_mutex);
    for (int i = 0; i < len; i++) {
        int next = (input_head + 1) % INPUT_BUFFER_SIZE;
        if (next == input_tail) break;
        input_buffer[input_head] = buf[i];
        input_head = next;
    }
    pthread_mutex_unlock(&input_mutex);
}

// =============================================================================
// Sixel pixel framebuffer support
// =============================================================================

// CIOLIB_COLOR_RGB flag - bit 31 indicates direct RGB color
#ifndef CIOLIB_COLOR_RGB
#define CIOLIB_COLOR_RGB 0x80000000
#endif

// Grow the changed-pixel rectangle (inclusive pixel coordinates) and flag the layer dirty.
// Caller holds the state mutex.
static void mark_pixels_dirty(int x0, int y0, int x1, int y1) {
    if (x0 < 0) x0 = 0;
    if (y0 < 0) y0 = 0;
    if (x1 < x0 || y1 < y0)
        return;
    if (linux_state.pixel_dirty_x1 < linux_state.pixel_dirty_x0) {
        linux_state.pixel_dirty_x0 = x0;
        linux_state.pixel_dirty_y0 = y0;
        linux_state.pixel_dirty_x1 = x1;
        linux_state.pixel_dirty_y1 = y1;
    } else {
        if (x0 < linux_state.pixel_dirty_x0) linux_state.pixel_dirty_x0 = x0;
        if (y0 < linux_state.pixel_dirty_y0) linux_state.pixel_dirty_y0 = y0;
        if (x1 > linux_state.pixel_dirty_x1) linux_state.pixel_dirty_x1 = x1;
        if (y1 > linux_state.pixel_dirty_y1) linux_state.pixel_dirty_y1 = y1;
    }
    linux_state.pixel_dirty = 1;
}

static void mark_all_pixels_dirty(void) {
    mark_pixels_dirty(0, 0, 0x3FFFFFFF, 0x3FFFFFFF);   /* clamped to the buffer when copied */
}

static void reset_pixels_dirty(void) {
    linux_state.pixel_dirty = 0;
    linux_state.pixel_dirty_x0 = 0;
    linux_state.pixel_dirty_y0 = 0;
    linux_state.pixel_dirty_x1 = -1;
    linux_state.pixel_dirty_y1 = -1;
}

// Ensure pixel buffer is large enough; lazy allocate/grow
static void ensure_pixel_buffer(int w, int h) {
    if (w <= 0 || h <= 0 || w > 8192 || h > 8192) return;

    if (linux_state.pixel_buffer != NULL &&
        linux_state.pixel_width >= w && linux_state.pixel_height >= h) {
        return;  // Already large enough
    }

    // Use the larger of existing and requested dimensions
    int new_w = (w > linux_state.pixel_width) ? w : linux_state.pixel_width;
    int new_h = (h > linux_state.pixel_height) ? h : linux_state.pixel_height;

    size_t new_size;
    if (!safe_mult_size((size_t)new_w, (size_t)new_h, &new_size) ||
        !safe_mult_size(new_size, sizeof(uint32_t), &new_size)) {
        LOGE("Pixel buffer size overflow: %dx%d", new_w, new_h);
        return;
    }

    uint32_t *new_buf = (uint32_t *)calloc(1, new_size);  // calloc zeros = transparent
    if (!new_buf) {
        LOGE("Failed to allocate pixel buffer %dx%d", new_w, new_h);
        return;
    }

    // Copy old content if resizing
    if (linux_state.pixel_buffer != NULL) {
        int copy_w = (linux_state.pixel_width < new_w) ? linux_state.pixel_width : new_w;
        int copy_h = (linux_state.pixel_height < new_h) ? linux_state.pixel_height : new_h;
        for (int y = 0; y < copy_h; y++) {
            memcpy(new_buf + y * new_w,
                   linux_state.pixel_buffer + y * linux_state.pixel_width,
                   copy_w * sizeof(uint32_t));
        }
        free(linux_state.pixel_buffer);
    }

    linux_state.pixel_buffer = new_buf;
    linux_state.pixel_width = new_w;
    linux_state.pixel_height = new_h;
    mark_all_pixels_dirty();   /* new size: the app re-creates its bitmap and takes everything */
}

// Resolve a ciolib color value to ARGB (fully opaque)
static uint32_t resolve_pixel_color(uint32_t col) {
    uint32_t rgb;
    if (col & CIOLIB_COLOR_RGB) {
        rgb = col & 0x00FFFFFF;
    } else {
        uint32_t idx = col & 0xFFFF;
        if (idx < 65536) {
            rgb = linux_state.palette[idx] & 0x00FFFFFF;
        } else {
            rgb = 0;
        }
    }
    return 0xFF000000 | rgb;  // Fully opaque
}

// Clear pixel buffer; the caller holds linux_state.mutex. That lock is a plain pthread mutex, NOT recursive like the
// Windows CRITICAL_SECTION this file was ported from, so code already holding it must call this, never
// linux_ciolib_clear_pixels(): linux_clrscr did, and ESC[2J after any sixel image (Fractals: exit the door after
// switching engines) deadlocked the poll thread on itself, then the UI thread on the same lock (2026-09-18).
static void clear_pixels_locked(void) {
    if (linux_state.pixel_buffer != NULL) {
        size_t buf_size;
        if (safe_mult_size((size_t)linux_state.pixel_width, (size_t)linux_state.pixel_height, &buf_size)) {
            memset(linux_state.pixel_buffer, 0, buf_size * sizeof(uint32_t));
        }
        mark_all_pixels_dirty();
        linux_state.pixel_has_data = 0;
    }
}

// Clear pixel buffer
void linux_ciolib_clear_pixels(void) {
    pthread_mutex_lock(&linux_state.mutex);
    clear_pixels_locked();
    pthread_mutex_unlock(&linux_state.mutex);
}

// Sixel setpixels implementation
int linux_ciolib_setpixels(uint32_t sx, uint32_t sy, uint32_t ex, uint32_t ey,
                          uint32_t x_off, uint32_t y_off, uint32_t mx_off, uint32_t my_off,
                          struct ciolib_pixels *pixels, struct ciolib_mask *mask) {
    if (!pixels || !pixels->pixels) return 0;

    pthread_mutex_lock(&linux_state.mutex);

    // Ensure pixel buffer is large enough
    ensure_pixel_buffer((int)(ex + 1), (int)(ey + 1));

    if (!linux_state.pixel_buffer) {
        pthread_mutex_unlock(&linux_state.mutex);
        return 0;
    }

    int buf_w = linux_state.pixel_width;
    int buf_h = linux_state.pixel_height;

    for (uint32_t dst_y = sy; dst_y <= ey; dst_y++) {
        if ((int)dst_y >= buf_h) break;
        uint32_t src_y = dst_y - sy + y_off;
        if (src_y >= pixels->height) break;

        for (uint32_t dst_x = sx; dst_x <= ex; dst_x++) {
            if ((int)dst_x >= buf_w) break;
            uint32_t src_x = dst_x - sx + x_off;
            if (src_x >= pixels->width) break;

            // Check mask
            if (mask && mask->bits) {
                uint32_t mask_x = dst_x - sx + mx_off;
                uint32_t mask_y = dst_y - sy + my_off;
                if (mask_x < mask->width && mask_y < mask->height) {
                    /* One continuous bit run across the whole mask, as cterm's sixel mask, the APC PBM decoder
                     * and upstream bitmap_con.c write it (rows don't start on a fresh byte). */
                    size_t mask_pos = (size_t)mask_y * mask->width + mask_x;
                    size_t mask_byte_idx = mask_pos / 8;
                    uint8_t mask_bit = 0x80 >> (mask_pos % 8);
                    if (!(mask->bits[mask_byte_idx] & mask_bit)) {
                        continue;  // Mask bit clear: skip this pixel
                    }
                }
            }

            uint32_t src_idx = src_y * pixels->width + src_x;
            uint32_t dst_idx = dst_y * (uint32_t)buf_w + dst_x;
            linux_state.pixel_buffer[dst_idx] = resolve_pixel_color(pixels->pixels[src_idx]);
        }
    }

    mark_pixels_dirty((int)sx, (int)sy,
                      (int)ex < buf_w ? (int)ex : buf_w - 1,
                      (int)ey < buf_h ? (int)ey : buf_h - 1);
    linux_state.pixel_has_data = 1;

    pthread_mutex_unlock(&linux_state.mutex);
    return 1;
}

// Public accessors for sixel pixel data
int linux_ciolib_has_sixel_data(void) {
    return linux_state.pixel_has_data;
}

int linux_ciolib_is_pixel_dirty(void) {
    return linux_state.pixel_dirty;
}

void linux_ciolib_clear_pixel_dirty(void) {
    reset_pixels_dirty();
}

// Copy the layer's changed rectangle straight into dest (dest_w x dest_h ARGB, dest_stride pixels per row)
// and clear the dirty state; full copies everything. Returns the pixels copied, 0 when there is nothing to
// copy, or -1 when dest isn't the layer's size (nothing copied, still dirty). *bw x *bh is always the size.
int linux_ciolib_copy_dirty_pixels(uint32_t *dest, int dest_w, int dest_h, int dest_stride, int full,
                                 int *x, int *y, int *w, int *h, int *bw, int *bh) {
    int ret = 0;
    int pw;
    int ph;
    int x0;
    int y0;
    int x1;
    int y1;

    pthread_mutex_lock(&linux_state.mutex);
    pw = linux_state.pixel_width;
    ph = linux_state.pixel_height;
    *bw = pw;
    *bh = ph;
    *x = *y = *w = *h = 0;
    if (linux_state.pixel_buffer == NULL || !linux_state.pixel_has_data || pw <= 0 || ph <= 0) {
        /* Nothing to show (the app drops its overlay): clear the flag, or the poll loop asks for a redraw every cycle */
        reset_pixels_dirty();
        goto done;
    }
    if (dest == NULL)
        goto done;
    if (dest_w != pw || dest_h != ph || dest_stride < pw) {
        ret = -1;
        goto done;
    }
    if (!full && !linux_state.pixel_dirty)
        goto done;

    x0 = 0;
    y0 = 0;
    x1 = pw - 1;
    y1 = ph - 1;
    if (!full && linux_state.pixel_dirty_x1 >= linux_state.pixel_dirty_x0
        && linux_state.pixel_dirty_y1 >= linux_state.pixel_dirty_y0) {
        if (linux_state.pixel_dirty_x0 > x0) x0 = linux_state.pixel_dirty_x0;
        if (linux_state.pixel_dirty_y0 > y0) y0 = linux_state.pixel_dirty_y0;
        if (linux_state.pixel_dirty_x1 < x1) x1 = linux_state.pixel_dirty_x1;
        if (linux_state.pixel_dirty_y1 < y1) y1 = linux_state.pixel_dirty_y1;
    }
    if (x1 >= x0 && y1 >= y0) {
        int rw = x1 - x0 + 1;
        for (int row = y0; row <= y1; row++) {
            memcpy(dest + (size_t)row * (size_t)dest_stride + (size_t)x0,
                   linux_state.pixel_buffer + (size_t)row * (size_t)pw + (size_t)x0,
                   (size_t)rw * sizeof(uint32_t));
        }
        *x = x0;
        *y = y0;
        *w = rw;
        *h = y1 - y0 + 1;
        ret = rw * (y1 - y0 + 1);
    }
    reset_pixels_dirty();

done:
    pthread_mutex_unlock(&linux_state.mutex);
    return ret;
}

uint32_t* linux_ciolib_get_pixel_buffer(void) {
    return linux_state.pixel_buffer;
}

int linux_ciolib_get_pixel_width(void) {
    return linux_state.pixel_width;
}

int linux_ciolib_get_pixel_height(void) {
    return linux_state.pixel_height;
}

// Set the character cell pixel height (e.g. 16 for 8x16 font)
void linux_ciolib_set_char_pixel_height(int h) {
    linux_state.char_pixel_height = h;
    /* Cursor scanlines are relative to the cell — a new cell height retargets them. */
    linux_ciolib_apply_mode_cursor();
}

// Implementation functions
/*
 * ---- Sixel pixel-layer clearing for PARTIAL erases -------------------------
 *
 * Sixel graphics live in their own ARGB framebuffer (pixel_buffer) that is
 * composited over the text grid; the text cells in `screen` know nothing about
 * them. Until 2026-08-17 the ONLY thing that ever cleared that framebuffer was
 * clrscr(), i.e. ESC[2J. Every PARTIAL erase — ESC[J / ESC[0J (erase below),
 * ESC[1J (erase above) and ESC[K in all its forms — clears cterm's text cells
 * through clreol()/clear2bol() and never touched the pixels, so sixel images
 * stayed on screen as ghosts underneath the fresh text.
 *
 * That asymmetry is why this only reproduced on some boards: Synchronet clears
 * with ESC[2J and happened to hit the one path that worked, while Mystic uses
 * the erase-below form and left its sixels behind on every screen change.
 *
 * Cell geometry comes from vparams — the SAME table cterm derives its sixel
 * coordinates from — so the rectangle wiped here can never drift away from
 * where setpixels() actually wrote.
 */
static void linux_cell_pixel_size(int *cwp, int *chp) {
    int cols = linux_state.width;
    int rows = linux_state.height;
    int charh = linux_state.char_pixel_height;
    int cw = 0, ch = 0;

    if (linux_state.vmode_override >= 0) {
        for (size_t i = 0; i < NUMMODES; i++) {
            if (vparams[i].mode == linux_state.vmode_override) {
                cw = vparams[i].charwidth;
                ch = vparams[i].charheight;
                break;
            }
        }
    }
    for (size_t i = 0; cw == 0 && i < NUMMODES; i++) {
        if (vparams[i].cols != cols || vparams[i].rows != rows)
            continue;
        if (charh > 0 && vparams[i].charheight == charh) {
            cw = vparams[i].charwidth;      /* geometry AND cell height agree */
            ch = vparams[i].charheight;
            break;
        }
        if (cw == 0) {                      /* geometry alone — remember the first */
            cw = vparams[i].charwidth;
            ch = vparams[i].charheight;
        }
    }
    if (cw <= 0) cw = 8;
    if (ch <= 0) ch = (charh > 0) ? charh : 16;
    if (cwp) *cwp = cw;
    if (chp) *chp = ch;
}

/* Wipe the sixel pixels under a rectangle of TEXT CELLS (1-based, inclusive).
 * Caller must already hold the state mutex.
 *
 * NOTE: pixel_has_data is deliberately left SET — pixels may well survive
 * outside this rectangle, and only a full clear may claim the layer is empty. */
static void linux_clear_pixel_cells(int sx, int sy, int ex, int ey) {
    if (linux_state.pixel_buffer == NULL || !linux_state.pixel_has_data)
        return;

    int cw, ch;
    linux_cell_pixel_size(&cw, &ch);

    long px0 = (long)(sx - 1) * cw;
    long py0 = (long)(sy - 1) * ch;
    long px1 = (long)ex * cw - 1;
    long py1 = (long)ey * ch - 1;

    if (px0 < 0) px0 = 0;
    if (py0 < 0) py0 = 0;
    if (px1 > (long)linux_state.pixel_width - 1)  px1 = (long)linux_state.pixel_width - 1;
    if (py1 > (long)linux_state.pixel_height - 1) py1 = (long)linux_state.pixel_height - 1;
    if (px1 < px0 || py1 < py0)
        return;

    for (long yy = py0; yy <= py1; yy++) {
        memset(linux_state.pixel_buffer + (size_t)yy * (size_t)linux_state.pixel_width + (size_t)px0,
               0, (size_t)(px1 - px0 + 1) * sizeof(uint32_t));
    }
    mark_pixels_dirty((int)px0, (int)py0, (int)px1, (int)py1);
}

static void linux_clreol(void) {
    pthread_mutex_lock(&linux_state.mutex);
    int y = linux_state.cursor_y - 1;
    int x = linux_state.cursor_x - 1;
    if (y >= 0 && y < linux_state.height && x >= 0 && linux_state.screen) {
        for (int i = x; i < linux_state.width; i++) {
            int idx;
            if (!validate_index(i, y, linux_state.width, linux_state.height, &idx)) {
                break;  // Index overflow, stop processing
            }
            linux_state.screen[idx].ch = ' ';
            linux_state.screen[idx].legacy_attr = linux_state.current_attr;
            linux_state.screen[idx].fg = linux_state.fg_color;
            linux_state.screen[idx].bg = linux_state.bg_color;
            linux_state.screen[idx].font = 0;
            linux_state.screen[idx].flags = linux_state.current_flags & ~VMEM_FLAG_UNDERLINE;
        }
        mark_region_dirty(x, y, linux_state.width - 1, y);
        /* An erase drops the graphics under it too, not just the text. */
        linux_clear_pixel_cells(x + 1, y + 1, linux_state.width, y + 1);
    }
    pthread_mutex_unlock(&linux_state.mutex);
}

/* Set by linux_movetext around its text copy: moving text isn't writing it, and the pixel layer moves on its
 * own there, so the pixels under the destination must survive. */
static int g_moving_text = 0;

static int linux_puttext(int sx, int sy, int ex, int ey, void *buf) {
    // Convert legacy format to vmem and call vmem_puttext
    if (!buf) return 0;

    // Validate rectangle dimensions with overflow protection
    int width, height;
    if (!validate_rect_dims(sx, sy, ex, ey, &width, &height)) {
        return 0;  // Invalid rectangle
    }

    unsigned char *src = (unsigned char *)buf;

    pthread_mutex_lock(&linux_state.mutex);
    if (linux_state.screen) {
        for (int y = 0; y < height; y++) {
            for (int x = 0; x < width; x++) {
                int screen_x = sx - 1 + x;
                int screen_y = sy - 1 + y;

                // Validate screen index
                int idx;
                if (!validate_index(screen_x, screen_y, linux_state.width,
                                   linux_state.height, &idx)) {
                    continue;  // Skip out-of-bounds
                }

                // Validate source index
                int src_idx;
                if (!validate_index(x, y, width, height, &src_idx)) {
                    continue;
                }

                // Validate multiplication to prevent overflow
                int src_offset;
                if (!safe_mult_int(src_idx, 2, &src_offset)) {
                    continue;  // Skip if multiplication overflows
                }

                // Validate src_offset + 1 is within buffer bounds
                // Buffer size is width * height * 2 bytes
                // NOTE: Must compute width*height safely first to avoid overflow
                int area;
                if (!safe_mult_int(width, height, &area)) {
                    continue;
                }
                int buf_size;
                if (!safe_mult_int(area, 2, &buf_size)) {
                    continue;
                }
                if (src_offset < 0 || src_offset + 1 >= buf_size) {
                    continue;  // Skip if would read past buffer end
                }

                linux_state.screen[idx].ch = src[src_offset];
                linux_state.screen[idx].legacy_attr = src[src_offset + 1];
                linux_state.screen[idx].fg = src[src_offset + 1] & 0x0F;
                linux_state.screen[idx].bg = (src[src_offset + 1] >> 4) & 0x0F;
                linux_state.screen[idx].flags = 0;
            }
        }
        mark_region_dirty(sx - 1, sy - 1, ex - 1, ey - 1);
        /* Text written over graphics replaces them, as in SyncTERM's single framebuffer */
        if (!g_moving_text)
            linux_clear_pixel_cells(sx, sy, ex, ey);
    }
    pthread_mutex_unlock(&linux_state.mutex);
    return 1;
}

static int linux_vmem_puttext(int sx, int sy, int ex, int ey, struct vmem_cell *buf) {
    if (!buf) return 0;

    // Validate rectangle dimensions with overflow protection
    int width, height;
    if (!validate_rect_dims(sx, sy, ex, ey, &width, &height)) {
        return 0;  // Invalid rectangle
    }

    pthread_mutex_lock(&linux_state.mutex);
    if (linux_state.screen) {
        for (int y = 0; y < height; y++) {
            for (int x = 0; x < width; x++) {
                int screen_x = sx - 1 + x;
                int screen_y = sy - 1 + y;

                // Validate screen index
                int idx;
                if (!validate_index(screen_x, screen_y, linux_state.width,
                                   linux_state.height, &idx)) {
                    continue;
                }

                // Validate source index
                int src_idx;
                if (!validate_index(x, y, width, height, &src_idx)) {
                    continue;
                }

                linux_state.screen[idx] = buf[src_idx];
            }
        }
        mark_region_dirty(sx - 1, sy - 1, ex - 1, ey - 1);
        /* Text written over graphics replaces them, as in SyncTERM's single framebuffer */
        if (!g_moving_text)
            linux_clear_pixel_cells(sx, sy, ex, ey);
    }
    pthread_mutex_unlock(&linux_state.mutex);
    return 1;
}

static int linux_gettext(int sx, int sy, int ex, int ey, void *buf) {
    if (!buf) return 0;

    // Validate rectangle dimensions with overflow protection
    int width, height;
    if (!validate_rect_dims(sx, sy, ex, ey, &width, &height)) {
        return 0;  // Invalid rectangle
    }

    unsigned char *dst = (unsigned char *)buf;

    pthread_mutex_lock(&linux_state.mutex);
    if (linux_state.screen) {
        for (int y = 0; y < height; y++) {
            for (int x = 0; x < width; x++) {
                int screen_x = sx - 1 + x;
                int screen_y = sy - 1 + y;

                // Validate screen index
                int idx;
                if (!validate_index(screen_x, screen_y, linux_state.width,
                                   linux_state.height, &idx)) {
                    continue;
                }

                // Validate destination index
                int dst_idx;
                if (!validate_index(x, y, width, height, &dst_idx)) {
                    continue;
                }

                // Validate multiplication to prevent overflow
                int dst_offset;
                if (!safe_mult_int(dst_idx, 2, &dst_offset)) {
                    continue;  // Skip if multiplication overflows
                }

                // Validate dst_offset + 1 is within buffer bounds
                // Buffer size is width * height * 2 bytes
                // NOTE: Must compute width*height safely first to avoid overflow
                int area;
                if (!safe_mult_int(width, height, &area)) {
                    continue;
                }
                int buf_size;
                if (!safe_mult_int(area, 2, &buf_size)) {
                    continue;
                }
                if (dst_offset < 0 || dst_offset + 1 >= buf_size) {
                    continue;  // Skip if would write past buffer end
                }

                dst[dst_offset] = linux_state.screen[idx].ch;
                dst[dst_offset + 1] = linux_state.screen[idx].legacy_attr;
            }
        }
    }
    pthread_mutex_unlock(&linux_state.mutex);
    return 1;
}

static int linux_vmem_gettext(int sx, int sy, int ex, int ey, struct vmem_cell *buf) {
    if (!buf) return 0;

    // Validate rectangle dimensions with overflow protection
    int width, height;
    if (!validate_rect_dims(sx, sy, ex, ey, &width, &height)) {
        return 0;  // Invalid rectangle
    }

    pthread_mutex_lock(&linux_state.mutex);
    if (linux_state.screen) {
        for (int y = 0; y < height; y++) {
            for (int x = 0; x < width; x++) {
                int screen_x = sx - 1 + x;
                int screen_y = sy - 1 + y;

                // Validate screen index
                int idx;
                if (!validate_index(screen_x, screen_y, linux_state.width,
                                   linux_state.height, &idx)) {
                    continue;
                }

                // Validate destination index
                int dst_idx;
                if (!validate_index(x, y, width, height, &dst_idx)) {
                    continue;
                }

                buf[dst_idx] = linux_state.screen[idx];
            }
        }
    }
    pthread_mutex_unlock(&linux_state.mutex);
    return 1;
}

static void linux_textattr(int attr) {
    pthread_mutex_lock(&linux_state.mutex);
    linux_state.current_attr = (uint8_t)attr;
    linux_state.fg_color = attr & 0x0F;
    linux_state.bg_color = (attr >> 4) & 0x0F;
    pthread_mutex_unlock(&linux_state.mutex);
}

static int linux_kbhit(void) {
    pthread_mutex_lock(&input_mutex);
    int has_data = (input_head != input_tail);
    pthread_mutex_unlock(&input_mutex);
    return has_data;
}

static int linux_kbwait(int timeout) {
    // Simple polling implementation
    long elapsed = 0;
    while (elapsed < timeout || timeout == 0) {
        if (linux_kbhit()) return 1;
        usleep(10 * 1000); // 10ms
        elapsed += 10;
        if (timeout == 0) break;
    }
    return linux_kbhit();
}

static void linux_delay(long ms) {
    // Validate ms to prevent negative values
    if (ms <= 0) {
        return;  // No delay for zero or negative
    }
    // Cap at a reasonable maximum (1 hour = 3600000ms) to prevent overflow
    if (ms > 3600000L) {
        ms = 3600000L;
    }
    usleep((unsigned int)(ms * 1000));
}

static int linux_wherex(void) {
    return linux_state.cursor_x;
}

static int linux_wherey(void) {
    return linux_state.cursor_y;
}

static int linux_putch(int c) {
    /* ⚠️ BEL is a beep, never a glyph. cterm's BEL handler calls putch(7) on __unix__ builds
     * (Windows calls MessageBeep instead), expecting ciolib to beep the way the real
     * ciolib_putch does. Drawing it wrote char 7 into the cell AND advanced the cursor, so
     * every BEL pushed the rest of the line one column right (aBSiNTHE's "Three new messages
     * posted!" landed a cell past its dark-grey leading T). The sound is already handled:
     * syncterm_process_data scans the stream for 0x07 and sets g_bell_detected. */
    if (c == 7)
        return c;

    pthread_mutex_lock(&linux_state.mutex);
    if (linux_state.screen) {
        int x = linux_state.cursor_x - 1;
        int y = linux_state.cursor_y - 1;

        // Validate index with overflow protection
        int idx;
        if (validate_index(x, y, linux_state.width, linux_state.height, &idx)) {
            linux_state.screen[idx].ch = (uint8_t)c;
            linux_state.screen[idx].legacy_attr = linux_state.current_attr;
            linux_state.screen[idx].fg = linux_state.fg_color;
            linux_state.screen[idx].bg = linux_state.bg_color;
            linux_state.screen[idx].font = 0;
            linux_state.screen[idx].flags = linux_state.current_flags;
            mark_cell_dirty(x, y);
            linux_clear_pixel_cells(x + 1, y + 1, x + 1, y + 1);  /* text replaces graphics under it */

            // Advance cursor
            linux_state.cursor_x++;
            if (linux_state.cursor_x > linux_state.width) {
                linux_state.cursor_x = 1;
                linux_state.cursor_y++;
                if (linux_state.cursor_y > linux_state.height) {
                    linux_state.cursor_y = linux_state.height;
                    // Would need to scroll here
                }
            }
        }
    }
    pthread_mutex_unlock(&linux_state.mutex);
    return c;
}

static void linux_gotoxy(int x, int y) {
    pthread_mutex_lock(&linux_state.mutex);
    if (x >= 1 && x <= linux_state.width) {
        linux_state.cursor_x = x;
    }
    if (y >= 1 && y <= linux_state.height) {
        linux_state.cursor_y = y;
    }
    pthread_mutex_unlock(&linux_state.mutex);
}

static void linux_clrscr(void) {
    pthread_mutex_lock(&linux_state.mutex);
    if (linux_state.screen) {
        // Calculate screen size with overflow protection
        int screen_size;
        if (validate_dimensions(linux_state.width, linux_state.height, &screen_size)) {
            for (int i = 0; i < screen_size; i++) {
                linux_state.screen[i].ch = ' ';
                linux_state.screen[i].legacy_attr = linux_state.current_attr;
                linux_state.screen[i].fg = linux_state.fg_color;
                linux_state.screen[i].bg = linux_state.bg_color;
                linux_state.screen[i].font = 0;
                linux_state.screen[i].flags = linux_state.current_flags & ~VMEM_FLAG_UNDERLINE;
            }
        }
        linux_state.cursor_x = 1;
        linux_state.cursor_y = 1;
        // Clear sixel pixel buffer on screen clear
        if (linux_state.pixel_has_data) {
            clear_pixels_locked();   // we hold the (non-recursive) lock already
        }
        // ...and Rainwire's TERMinator:Pic pictures, in stream order with the commands around it (apc/pic_bridge.c)
        pic_screen_cleared();
        mark_screen_dirty();
    }
    pthread_mutex_unlock(&linux_state.mutex);
}

static void linux_gettextinfo(struct text_info *info) {
    if (!info) return;
    pthread_mutex_lock(&linux_state.mutex);
    info->winleft = 1;
    info->wintop = 1;
    info->winright = linux_state.width;
    info->winbottom = linux_state.height;
    info->attribute = linux_state.current_attr;
    info->normattr = 7;
    /* The real mode, as Windows reports: cterm sizes and places sixel / APC images and picks the Commodore
       colour tables from currmode, so a fixed C80 misplaced graphics in 80x43/80x50/132-column modes */
    info->currmode = linux_current_vmode();
    info->screenheight = linux_state.height;
    info->screenwidth = linux_state.width;
    info->curx = linux_state.cursor_x;
    info->cury = linux_state.cursor_y;
    pthread_mutex_unlock(&linux_state.mutex);
}

/* Which vparams row are we actually running?
 *
 * Look the mode up by the dimensions in use; when the loaded font's height is known
 * prefer an entry that also matches it, which disambiguates modes sharing a geometry
 * (e.g. C80X50 vs C4350). vparams is the very table cterm reads, so this mapping
 * cannot drift away from it. Falls back to C80 if nothing matches.
 *
 * The caller must already hold linux_state.mutex; this reads state without taking it,
 * because the pthread mutex here is not recursive.
 */
static int linux_current_vmode(void) {
    int cols = linux_state.width;
    int rows = linux_state.height;
    int charh = linux_state.char_pixel_height;
    int best = -1;

    /* An explicit mode (Commodore/Atari emulation) wins over geometry inference:
       40x25 with an 8x8 font is ambiguous between C40 and C64_40X25, and cterm keys
       its PETSCII colour table and attr defaults off currmode. */
    if (linux_state.vmode_override >= 0)
        return linux_state.vmode_override;

    for (size_t i = 0; i < NUMMODES; i++) {
        if (vparams[i].cols != cols || vparams[i].rows != rows)
            continue;
        if (charh > 0 && vparams[i].charheight == charh)
            return vparams[i].mode;     /* geometry AND cell height agree */
        if (best == -1)
            best = vparams[i].mode;     /* geometry alone — remember the first */
    }
    return (best == -1) ? C80 : best;
}

/* Cell height (pixels) for the active mode. Prefers the height of the font actually
   loaded; falls back to what vparams declares, then to the 8x16 VGA default. */
static int linux_active_char_height(void) {
    if (linux_state.char_pixel_height > 0)
        return linux_state.char_pixel_height;

    int mode = linux_current_vmode();
    for (size_t i = 0; i < NUMMODES; i++) {
        if (vparams[i].mode == mode && vparams[i].charheight > 0)
            return vparams[i].charheight;
    }
    return 16;
}

/*
 * Adopt the cursor scanlines the ACTIVE VIDEO MODE declares.
 *
 * ⚠️ curs_start/curs_end are scanlines WITHIN the character cell, and they are not
 * portable between cell heights. vparams gives 14..15 for a 16-pixel cell (the VGA
 * underline) and 7..7 for an 8-pixel one (80x50, 132x60...). This state used to
 * hard-code the 16-pixel 14/15 for every mode and never revisit it, so on any
 * 8-pixel cell the cursor was painted at scanlines 14..15 of an 8-high cell: a thin
 * bar the best part of a ROW BELOW the character it belongs to. Typing landed in the
 * right place, which is what made it look like a pure rendering bug.
 *
 * Key this off vparams, the same table cterm and the sixel geometry read, so the
 * three can never drift apart. Call it whenever the mode, the geometry or the loaded
 * font height changes.
 *
 * Caller must hold linux_state.mutex.
 */
static void linux_apply_mode_cursor_locked(void) {
    int mode = linux_current_vmode();
    int s = -1, e = -1;

    for (size_t i = 0; i < NUMMODES; i++) {
        if (vparams[i].mode == mode) {
            s = vparams[i].curs_start;
            e = vparams[i].curs_end;
            break;
        }
    }
    if (s < 0 || e < 0 || s > e) {
        /* Unknown mode: bottom two scanlines of whatever cell we have. */
        int charh = linux_active_char_height();
        s = charh - 2;
        e = charh - 1;
        if (s < 0) s = 0;
    }

    if (linux_state.default_curs_start == s && linux_state.default_curs_end == e)
        return;

    linux_state.default_curs_start = s;
    linux_state.default_curs_end = e;

    /* _NOCURSOR and _SOLIDCURSOR carry scanlines of their own (and setcustomcursor
       overrides both); only the normal cursor follows the mode default. */
    if (linux_state.cursor_type == _NORMALCURSOR) {
        linux_state.curs_start = s;
        linux_state.curs_end = e;
    }
    linux_state.dirty = 1;
}

/* Public entry point — locks, then applies. */
void linux_ciolib_apply_mode_cursor(void) {
    pthread_mutex_lock(&linux_state.mutex);
    linux_apply_mode_cursor_locked();
    pthread_mutex_unlock(&linux_state.mutex);
}

static void linux_setcursortype(int type) {
    pthread_mutex_lock(&linux_state.mutex);
    int prev_type = linux_state.cursor_type;
    linux_state.cursor_type = type;
    linux_state.cursor_visible = (type != _NOCURSOR);

    /* Match bitmap_setcursortype() scanline logic from bitmap_con.c:1525-1547 */
    switch (type) {
        case _NOCURSOR:
            linux_state.curs_start = 0xFF;
            linux_state.curs_end = 0;
            break;
        case _SOLIDCURSOR:
            linux_state.curs_start = 0;
            linux_state.curs_end = linux_active_char_height() - 1;
            break;
        default: /* _NORMALCURSOR */
            linux_state.curs_start = linux_state.default_curs_start;
            linux_state.curs_end = linux_state.default_curs_end;
            break;
    }

    /* Mark dirty so the C# poll loop reads the new cursor type */
    linux_state.dirty = 1;
    pthread_mutex_unlock(&linux_state.mutex);

    /* Log cursor type changes for diagnostics */
    if (type != prev_type) {
        char msg[128];
        const char *names[] = {"NOCURSOR", "SOLIDCURSOR", "NORMALCURSOR"};
        const char *prev_name = (prev_type >= 0 && prev_type <= 2) ? names[prev_type] : "?";
        const char *new_name = (type >= 0 && type <= 2) ? names[type] : "?";
        snprintf(msg, sizeof(msg), "linux_setcursortype: %s(%d) -> %s(%d) curs_start=%d curs_end=%d\n",
                 prev_name, prev_type, new_name, type,
                 linux_state.curs_start, linux_state.curs_end);
        fprintf(stderr, "%s", msg);
    }
}

static int linux_getch(void) {
    // Wait for input
    while (!linux_kbhit()) {
        usleep(10 * 1000); // 10ms
    }

    pthread_mutex_lock(&input_mutex);
    int c = -1;
    if (input_head != input_tail) {
        c = input_buffer[input_tail];
        input_tail = (input_tail + 1) % INPUT_BUFFER_SIZE;
    }
    pthread_mutex_unlock(&input_mutex);
    return c;
}

static int linux_getche(void) {
    int c = linux_getch();
    if (c >= 0) {
        linux_putch(c);
    }
    return c;
}

static void linux_beep(void) {
    // Could trigger a sound callback to managed code
    LOGI("Beep!");
}

static void linux_highvideo(void) {
    pthread_mutex_lock(&linux_state.mutex);
    linux_state.current_attr |= 0x08;
    linux_state.fg_color = linux_state.current_attr & 0x0F;
    pthread_mutex_unlock(&linux_state.mutex);
}

static void linux_lowvideo(void) {
    pthread_mutex_lock(&linux_state.mutex);
    linux_state.current_attr &= ~0x08;
    linux_state.fg_color = linux_state.current_attr & 0x0F;
    pthread_mutex_unlock(&linux_state.mutex);
}

static void linux_normvideo(void) {
    linux_textattr(7);
}

static void linux_textmode(int mode) {
    int new_width = 80;
    int new_height = 25;

    // Map mode to dimensions
    switch (mode) {
        case C40:
        case BW40:
        case C64_40X25:
        case C128_40X25:
            new_width = 40;
            new_height = 25;
            break;
        case C128_80X25:
            new_width = 80;
            new_height = 25;
            break;
        case C80:
        case BW80:
        case MONO:
        default:
            new_width = 80;
            new_height = 25;
            break;
        case C80X28:
            new_width = 80;
            new_height = 28;
            break;
        case C80X30:
            new_width = 80;
            new_height = 30;
            break;
        case C80X43:
            new_width = 80;
            new_height = 43;
            break;
        case C80X50:
            new_width = 80;
            new_height = 50;
            break;
        case C80X60:
            new_width = 80;
            new_height = 60;
            break;
    }

    pthread_mutex_lock(&linux_state.mutex);

    // Reallocate screen buffer if size changed
    if (new_width != linux_state.width || new_height != linux_state.height) {
        // Validate allocation size with overflow protection
        size_t alloc_size;
        if (!validate_alloc_size(new_width, new_height, sizeof(struct vmem_cell), &alloc_size)) {
            LOGE("Screen buffer size overflow for %dx%d", new_width, new_height);
            pthread_mutex_unlock(&linux_state.mutex);
            return;
        }

        struct vmem_cell *new_screen = calloc(1, alloc_size);
        if (new_screen) {
            // Free old screen only after successful allocation
            free(linux_state.screen);
            linux_state.screen = new_screen;
            linux_state.width = new_width;
            linux_state.height = new_height;

            // Initialize screen with validated size
            int screen_size;
            if (validate_dimensions(new_width, new_height, &screen_size)) {
                for (int i = 0; i < screen_size; i++) {
                    linux_state.screen[i].ch = ' ';
                    linux_state.screen[i].legacy_attr = 7;
                    linux_state.screen[i].fg = 7;
                    linux_state.screen[i].bg = 0;
                    linux_state.screen[i].font = 0;
                    linux_state.screen[i].flags = 0;
                }
            }
        } else {
            // Allocation failed, keep old screen
            LOGE("Failed to allocate screen buffer for %dx%d", new_width, new_height);
        }
    }

    linux_state.cursor_x = 1;
    linux_state.cursor_y = 1;
    linux_apply_mode_cursor_locked();
    mark_screen_dirty();

    pthread_mutex_unlock(&linux_state.mutex);

    // Update text_info
    cio_textinfo.screenwidth = new_width;
    cio_textinfo.screenheight = new_height;
}

static int linux_ungetch(int ch) {
    pthread_mutex_lock(&input_mutex);
    // Push to front of buffer (actually just add to queue for simplicity)
    int next = (input_head + 1) % INPUT_BUFFER_SIZE;
    if (next != input_tail) {
        input_buffer[input_head] = (unsigned char)ch;
        input_head = next;
        pthread_mutex_unlock(&input_mutex);
        return ch;
    }
    pthread_mutex_unlock(&input_mutex);
    return -1;
}

static int linux_movetext(int sx, int sy, int ex, int ey, int dx, int dy) {
    // Validate rectangle dimensions with overflow protection
    int width, height;
    if (!validate_rect_dims(sx, sy, ex, ey, &width, &height)) {
        return 0;  // Invalid rectangle
    }

    // Validate allocation size
    size_t alloc_size;
    if (!validate_alloc_size(width, height, sizeof(struct vmem_cell), &alloc_size)) {
        return 0;  // Size overflow
    }

    struct vmem_cell *temp = malloc(alloc_size);
    if (!temp) return 0;

    // Get source
    linux_vmem_gettext(sx, sy, ex, ey, temp);
    // Put at destination
    g_moving_text = 1;
    linux_vmem_puttext(dx, dy, dx + width - 1, dy + height - 1, temp);
    g_moving_text = 0;

    free(temp);

    // Move the sixel pixel buffer in lock-step with the text.
    // ⚠️ This used to run only for UPWARD moves (dy < sy), which covered scrolling
    // and ESC[M delete-line but NOT ESC[L insert-line — cterm implements that as a
    // DOWNWARD movetext, so the text slid down while the graphics stayed put. Both
    // directions are handled now; the arithmetic below is already expressed in terms
    // of sy/dy, so it works either way once the vacated rows are computed on the
    // correct side of the block.
    // Still restricted to FULL-WIDTH moves: a partial-width block move would need a
    // per-row rectangular memmove, and cterm generates none on these paths.
    if (dy != sy && dx == sx &&
        sx == 1 && ex >= linux_state.width &&
        linux_state.pixel_buffer != NULL && linux_state.pixel_has_data) {
        pthread_mutex_lock(&linux_state.mutex);
        int ch = linux_state.char_pixel_height;
        if (ch <= 0) ch = 16;
        int pw = linux_state.pixel_width;
        int ph = linux_state.pixel_height;

        int src_pixel_y = (sy - 1) * ch;   // first pixel row of source region
        int dst_pixel_y = (dy - 1) * ch;   // destination pixel row
        int move_height = (ey - sy + 1) * ch; // pixel rows being moved

        // Clamp to pixel buffer bounds — either end can run past it depending
        // on which way the block is going.
        if (src_pixel_y + move_height > ph)
            move_height = ph - src_pixel_y;
        if (dst_pixel_y + move_height > ph)
            move_height = ph - dst_pixel_y;

        if (move_height > 0 && src_pixel_y >= 0 && dst_pixel_y >= 0 &&
            src_pixel_y < ph && dst_pixel_y < ph) {
            // Shift the pixel rows to match the text
            memmove(linux_state.pixel_buffer + dst_pixel_y * pw,
                    linux_state.pixel_buffer + src_pixel_y * pw,
                    (size_t)move_height * (size_t)pw * sizeof(uint32_t));

            // Clear the part of the SOURCE region the block no longer covers:
            // the rows below it after moving up, above it after moving down.
            int clear_start, clear_end;
            if (dst_pixel_y < src_pixel_y) {          /* moved up */
                clear_start = dst_pixel_y + move_height;
                if (clear_start < src_pixel_y)
                    clear_start = src_pixel_y;
                clear_end = src_pixel_y + move_height;
            } else {                                  /* moved down */
                clear_start = src_pixel_y;
                clear_end = dst_pixel_y;
                if (clear_end > src_pixel_y + move_height)
                    clear_end = src_pixel_y + move_height;
            }
            if (clear_end > ph) clear_end = ph;
            if (clear_start < clear_end) {
                memset(linux_state.pixel_buffer + clear_start * pw,
                       0,
                       (size_t)(clear_end - clear_start) * (size_t)pw * sizeof(uint32_t));
            }
            mark_all_pixels_dirty();
        }
        pthread_mutex_unlock(&linux_state.mutex);
    }

    return 1;
}

static void linux_wscroll(void) {
    pthread_mutex_lock(&linux_state.mutex);
    if (linux_state.screen && linux_state.height > 1) {
        // Calculate sizes with overflow protection
        int last_line_offset;
        if (!safe_mult_int(linux_state.height - 1, linux_state.width, &last_line_offset)) {
            pthread_mutex_unlock(&linux_state.mutex);
            return;  // Overflow
        }

        size_t move_size;
        if (!safe_mult_size((size_t)last_line_offset, sizeof(struct vmem_cell), &move_size)) {
            pthread_mutex_unlock(&linux_state.mutex);
            return;  // Overflow
        }

        // Move all lines up by one
        memmove(linux_state.screen,
                linux_state.screen + linux_state.width,
                move_size);

        // Clear bottom line with validated index
        for (int i = 0; i < linux_state.width; i++) {
            int idx;
            if (!safe_add_int(last_line_offset, i, &idx)) {
                break;  // Overflow
            }
            linux_state.screen[idx].ch = ' ';
            linux_state.screen[idx].legacy_attr = linux_state.current_attr;
            linux_state.screen[idx].fg = linux_state.fg_color;
            linux_state.screen[idx].bg = linux_state.bg_color;
            linux_state.screen[idx].font = 0;
            linux_state.screen[idx].flags = linux_state.current_flags & ~VMEM_FLAG_UNDERLINE;
        }
        // Scroll sixel pixel buffer up by one character cell height
        if (linux_state.pixel_buffer != NULL && linux_state.pixel_has_data) {
            int ch = linux_state.char_pixel_height;
            if (ch <= 0) ch = 16;  // fallback to standard VGA height
            int pw = linux_state.pixel_width;
            int ph = linux_state.pixel_height;
            if (ch < ph) {
                // Shift pixel rows up by char_pixel_height
                int rows_to_keep = ph - ch;
                memmove(linux_state.pixel_buffer,
                        linux_state.pixel_buffer + (ch * pw),
                        (size_t)rows_to_keep * (size_t)pw * sizeof(uint32_t));
                // Clear the newly exposed bottom rows
                memset(linux_state.pixel_buffer + (rows_to_keep * pw),
                       0,
                       (size_t)ch * (size_t)pw * sizeof(uint32_t));
            } else {
                // Scroll distance >= buffer height, just clear everything
                memset(linux_state.pixel_buffer, 0,
                       (size_t)pw * (size_t)ph * sizeof(uint32_t));
                linux_state.pixel_has_data = 0;
            }
            mark_all_pixels_dirty();
        }
        mark_screen_dirty();  // Scroll affects entire screen
    }
    pthread_mutex_unlock(&linux_state.mutex);
}

static void linux_window(int sx, int sy, int ex, int ey) {
    // For now, just update text_info
    cio_textinfo.winleft = sx;
    cio_textinfo.wintop = sy;
    cio_textinfo.winright = ex;
    cio_textinfo.winbottom = ey;
}

static void linux_delline(void) {
    pthread_mutex_lock(&linux_state.mutex);
    if (linux_state.screen) {
        int y = linux_state.cursor_y - 1;
        if (y >= 0 && y < linux_state.height - 1) {
            // Calculate offsets with overflow protection
            int src_offset, dst_offset, lines_to_move, cells_to_move;

            // Validate y + 1 won't overflow before multiplication
            int y_plus_1;
            if (!safe_add_int(y, 1, &y_plus_1)) {
                pthread_mutex_unlock(&linux_state.mutex);
                return;
            }

            if (!safe_mult_int(y, linux_state.width, &dst_offset) ||
                !safe_mult_int(y_plus_1, linux_state.width, &src_offset)) {
                pthread_mutex_unlock(&linux_state.mutex);
                return;
            }

            lines_to_move = linux_state.height - y - 1;

            // Calculate cells_to_move safely before passing to safe_mult_size
            if (!safe_mult_int(lines_to_move, linux_state.width, &cells_to_move)) {
                pthread_mutex_unlock(&linux_state.mutex);
                return;
            }

            size_t move_size;
            if (!safe_mult_size((size_t)cells_to_move, sizeof(struct vmem_cell), &move_size)) {
                pthread_mutex_unlock(&linux_state.mutex);
                return;
            }

            // Move lines up
            memmove(linux_state.screen + dst_offset,
                    linux_state.screen + src_offset,
                    move_size);
        }

        // Clear last line with validated index
        int last_line;
        if (safe_mult_int(linux_state.height - 1, linux_state.width, &last_line)) {
            for (int i = 0; i < linux_state.width; i++) {
                int idx;
                if (!safe_add_int(last_line, i, &idx)) break;
                linux_state.screen[idx].ch = ' ';
                linux_state.screen[idx].legacy_attr = linux_state.current_attr;
                linux_state.screen[idx].fg = linux_state.fg_color;
                linux_state.screen[idx].bg = linux_state.bg_color;
                linux_state.screen[idx].font = 0;
                linux_state.screen[idx].flags = linux_state.current_flags & ~VMEM_FLAG_UNDERLINE;
            }
        }
        // Mark from deleted line to bottom as dirty
        if (y >= 0 && y < linux_state.height) {
            mark_region_dirty(0, y, linux_state.width - 1, linux_state.height - 1);
        }
    }
    pthread_mutex_unlock(&linux_state.mutex);
}

static void linux_insline(void) {
    pthread_mutex_lock(&linux_state.mutex);
    if (linux_state.screen) {
        int y = linux_state.cursor_y - 1;
        if (y >= 0 && y < linux_state.height - 1) {
            // Calculate offsets with overflow protection
            int src_offset, dst_offset, lines_to_move, cells_to_move;

            // Validate y + 1 won't overflow before multiplication
            int y_plus_1;
            if (!safe_add_int(y, 1, &y_plus_1)) {
                pthread_mutex_unlock(&linux_state.mutex);
                return;
            }

            if (!safe_mult_int(y, linux_state.width, &src_offset) ||
                !safe_mult_int(y_plus_1, linux_state.width, &dst_offset)) {
                pthread_mutex_unlock(&linux_state.mutex);
                return;
            }

            lines_to_move = linux_state.height - y - 1;

            // Calculate cells_to_move safely before passing to safe_mult_size
            if (!safe_mult_int(lines_to_move, linux_state.width, &cells_to_move)) {
                pthread_mutex_unlock(&linux_state.mutex);
                return;
            }

            size_t move_size;
            if (!safe_mult_size((size_t)cells_to_move, sizeof(struct vmem_cell), &move_size)) {
                pthread_mutex_unlock(&linux_state.mutex);
                return;
            }

            // Move lines down
            memmove(linux_state.screen + dst_offset,
                    linux_state.screen + src_offset,
                    move_size);
        }

        // Clear current line with validated index
        if (y >= 0) {
            int line_offset;
            if (safe_mult_int(y, linux_state.width, &line_offset)) {
                for (int i = 0; i < linux_state.width; i++) {
                    int idx;
                    if (!safe_add_int(line_offset, i, &idx)) break;
                    linux_state.screen[idx].ch = ' ';
                    linux_state.screen[idx].legacy_attr = linux_state.current_attr;
                    linux_state.screen[idx].fg = linux_state.fg_color;
                    linux_state.screen[idx].bg = linux_state.bg_color;
                    linux_state.screen[idx].font = 0;
                    linux_state.screen[idx].flags = linux_state.current_flags & ~VMEM_FLAG_UNDERLINE;
                }
            }
        }
        // Mark from inserted line to bottom as dirty
        if (y >= 0 && y < linux_state.height) {
            mark_region_dirty(0, y, linux_state.width - 1, linux_state.height - 1);
        }
    }
    pthread_mutex_unlock(&linux_state.mutex);
}

static void linux_textbackground(int color) {
    pthread_mutex_lock(&linux_state.mutex);
    linux_state.bg_color = color & 0x0F;
    linux_state.current_attr = (linux_state.current_attr & 0x0F) | ((color & 0x0F) << 4);
    pthread_mutex_unlock(&linux_state.mutex);
}

static void linux_textcolor(int color) {
    pthread_mutex_lock(&linux_state.mutex);
    linux_state.fg_color = color & 0x0F;
    linux_state.current_attr = (linux_state.current_attr & 0xF0) | (color & 0x0F);
    pthread_mutex_unlock(&linux_state.mutex);
}

static void linux_settitle(const char *title) {
    LOGI("Set title: %s", title ? title : "(null)");
    // Could callback to managed code to set window title
}

static void linux_setname(const char *name) {
    LOGI("Set name: %s", name ? name : "(null)");
}

static int linux_setfont(int font, int force, int font_num) {
    LOGI("linux_setfont: font=%d, force=%d, font_num=%d", font, force, font_num);

    if (font < 0 || font > 255)
        return 0;

    /* Validate that the requested font has bitmap data for the current height.
     * If not and force is set, we allow it (cterm handles mode changes).
     * If not and force is not set, reject. */
    if (conio_fontdata[font].eight_by_sixteen == NULL &&
        conio_fontdata[font].eight_by_fourteen == NULL &&
        conio_fontdata[font].eight_by_eight == NULL) {
        LOGI("linux_setfont: font %d has no bitmap data, rejecting", font);
        return 0;
    }

    /* Mirror SyncTERM bitmap_con.c font slot logic:
     *   font_num 0 = set default font AND current_font[0] (primary)
     *   font_num 1 = set current_font[0] (primary)
     *   font_num 2-4 = set current_font[1-3] (alt attribute fonts)
     */
    switch (font_num) {
        case 0:
            linux_default_font = font;
            linux_current_font[0] = font;
            break;
        case 1:
            linux_current_font[0] = font;
            break;
        case 2:
        case 3:
        case 4:
            linux_current_font[font_num - 1] = font;
            break;
        default:
            return 0;
    }

    /* Update g_current_font_id whenever the primary font (slot 0) changes,
     * so the C# poll loop detects BBS-driven font switches. */
    if (font_num <= 1) {
        g_current_font_id = font;
        LOGI("linux_setfont: primary font now %d (%s)", font,
             conio_fontdata[font].desc ? conio_fontdata[font].desc : "(null)");
    }

    return 1;
}

static int linux_getfont(int font_num) {
    if (font_num == 0)
        return linux_default_font;
    if (font_num >= 1 && font_num <= 4)
        return linux_current_font[font_num - 1];
    return -1;
}

static void linux_setvideoflags(int flags) {
    linux_video_flags = flags;
}

static int linux_getvideoflags(void) {
    return linux_video_flags;
}

static int linux_setpalette(uint32_t entry, uint16_t r, uint16_t g, uint16_t b) {
    if (entry >= 65536) return 0;
    pthread_mutex_lock(&linux_state.mutex);
    uint32_t rgb = ((r >> 8) << 16) | ((g >> 8) << 8) | (b >> 8);
    if (linux_state.palette[entry] != rgb) {
        linux_state.palette[entry] = rgb;
        mark_screen_dirty();   /* cells drawn in this entry change colour with no cell write (OSC 4 effects) */
    }
    pthread_mutex_unlock(&linux_state.mutex);
    return 1;
}

static int linux_attr2palette(uint8_t attr, uint32_t *fg, uint32_t *bg) {
    if (fg) *fg = attr & 0x0F;
    if (bg) *bg = (attr >> 4) & 0x0F;
    return 1;
}

// Initialize ciolib for Linux
int initciolib(int mode) {
    LOGI("initciolib called with mode %d", mode);

    pthread_mutex_init(&linux_state.mutex, NULL);
    pthread_mutex_init(&input_mutex, NULL);
    input_mutex_initialized = 1;

    // Allocate initial screen buffer with safe math validation
    const int init_width = 80;
    const int init_height = 25;
    size_t alloc_size;
    int screen_size;

    if (!validate_alloc_size(init_width, init_height, sizeof(struct vmem_cell), &alloc_size) ||
        !validate_dimensions(init_width, init_height, &screen_size)) {
        LOGE("Screen buffer size overflow for %dx%d", init_width, init_height);
        return -1;
    }

    linux_state.width = init_width;
    linux_state.height = init_height;
    linux_state.screen = calloc(1, alloc_size);

    if (!linux_state.screen) {
        LOGE("Failed to allocate screen buffer");
        return -1;
    }

    // Initialize screen with spaces
    for (int i = 0; i < screen_size; i++) {
        linux_state.screen[i].ch = ' ';
        linux_state.screen[i].legacy_attr = 7;
        linux_state.screen[i].fg = 7;
        linux_state.screen[i].bg = 0;
        linux_state.screen[i].font = 0;
        linux_state.screen[i].flags = 0;
    }

    // Initialize text_info
    cio_textinfo.winleft = 1;
    cio_textinfo.wintop = 1;
    cio_textinfo.winright = 80;
    cio_textinfo.winbottom = 25;
    cio_textinfo.attribute = 7;
    cio_textinfo.normattr = 7;
    cio_textinfo.currmode = C80;
    cio_textinfo.screenheight = 25;
    cio_textinfo.screenwidth = 80;
    cio_textinfo.curx = 1;
    cio_textinfo.cury = 1;

    // Set up cio_api function pointers
    memset(&cio_api, 0, sizeof(cio_api));
    cio_api.mode = mode;
    cio_api.mouse = 0;
    cio_api.options = CONIO_OPT_BRIGHT_BACKGROUND
                    | CONIO_OPT_PALETTE_SETTING
                    | CONIO_OPT_LOADABLE_FONTS
                    | CONIO_OPT_CUSTOM_CURSOR
                    | CONIO_OPT_SET_PIXEL
                    | CONIO_OPT_EXTENDED_PALETTE;
    cio_api.clreol = linux_clreol;
    cio_api.puttext = linux_puttext;
    cio_api.vmem_puttext = linux_vmem_puttext;
    cio_api.gettext = linux_gettext;
    cio_api.vmem_gettext = linux_vmem_gettext;
    cio_api.textattr = linux_textattr;
    cio_api.kbhit = linux_kbhit;
    cio_api.kbwait = linux_kbwait;
    cio_api.delay = linux_delay;
    cio_api.wherex = linux_wherex;
    cio_api.wherey = linux_wherey;
    cio_api.putch = linux_putch;
    cio_api.gotoxy = linux_gotoxy;
    cio_api.clrscr = linux_clrscr;
    cio_api.gettextinfo = linux_gettextinfo;
    cio_api.setcursortype = linux_setcursortype;
    cio_api.getch = linux_getch;
    cio_api.getche = linux_getche;
    cio_api.beep = linux_beep;
    cio_api.highvideo = linux_highvideo;
    cio_api.lowvideo = linux_lowvideo;
    cio_api.normvideo = linux_normvideo;
    cio_api.textmode = linux_textmode;
    cio_api.ungetch = linux_ungetch;
    cio_api.movetext = linux_movetext;
    cio_api.wscroll = linux_wscroll;
    cio_api.window = linux_window;
    cio_api.delline = linux_delline;
    cio_api.insline = linux_insline;
    cio_api.textbackground = linux_textbackground;
    cio_api.textcolor = linux_textcolor;
    cio_api.settitle = linux_settitle;
    cio_api.setname = linux_setname;
    cio_api.setfont = linux_setfont;
    cio_api.getfont = linux_getfont;
    cio_api.setvideoflags = linux_setvideoflags;
    cio_api.getvideoflags = linux_getvideoflags;
    cio_api.setpalette = linux_setpalette;
    cio_api.attr2palette = linux_attr2palette;

    LOGI("ciolib initialized successfully");
    return 0;
}

void suspendciolib(void) {
    LOGI("suspendciolib called");
}

// Resize terminal for Linux
int linux_ciolib_resize(int width, int height) {
    LOGI("Resizing terminal to %dx%d", width, height);

    // Validate dimensions against defined maximums
    if (width <= 0 || height <= 0 ||
        width > MAX_TERMINAL_WIDTH || height > MAX_TERMINAL_HEIGHT) {
        LOGE("Invalid resize dimensions: %dx%d (max: %dx%d)",
             width, height, MAX_TERMINAL_WIDTH, MAX_TERMINAL_HEIGHT);
        return -1;
    }

    // Skip resize if dimensions haven't changed - preserves screen content
    pthread_mutex_lock(&linux_state.mutex);
    if (linux_state.screen != NULL &&
        linux_state.width == width && linux_state.height == height) {
        LOGI("Screen size unchanged (%dx%d), skipping resize", width, height);
        pthread_mutex_unlock(&linux_state.mutex);
        return 0;
    }
    pthread_mutex_unlock(&linux_state.mutex);

    // Validate allocation size with overflow protection
    size_t alloc_size;
    if (!validate_alloc_size(width, height, sizeof(struct vmem_cell), &alloc_size)) {
        LOGE("Screen buffer size overflow for resize: %dx%d", width, height);
        return -1;
    }

    pthread_mutex_lock(&linux_state.mutex);

    struct vmem_cell *new_screen = calloc(1, alloc_size);
    if (!new_screen) {
        pthread_mutex_unlock(&linux_state.mutex);
        return -1;
    }

    // Initialize new screen with validated size
    int screen_size;
    if (validate_dimensions(width, height, &screen_size)) {
        for (int i = 0; i < screen_size; i++) {
            new_screen[i].ch = ' ';
            new_screen[i].legacy_attr = linux_state.current_attr;
            new_screen[i].fg = linux_state.fg_color;
            new_screen[i].bg = linux_state.bg_color;
            new_screen[i].font = 0;
            new_screen[i].flags = linux_state.current_flags & ~VMEM_FLAG_UNDERLINE;
        }
    }

    // Copy old content with validated indices
    if (linux_state.screen) {
        int copy_width = (width < linux_state.width) ? width : linux_state.width;
        int copy_height = (height < linux_state.height) ? height : linux_state.height;

        for (int y = 0; y < copy_height; y++) {
            for (int x = 0; x < copy_width; x++) {
                int new_idx, old_idx;
                if (validate_index(x, y, width, height, &new_idx) &&
                    validate_index(x, y, linux_state.width, linux_state.height, &old_idx)) {
                    new_screen[new_idx] = linux_state.screen[old_idx];
                }
            }
        }
        free(linux_state.screen);
    }

    linux_state.screen = new_screen;
    linux_state.width = width;
    linux_state.height = height;

    // Free sixel pixel buffer on resize
    if (linux_state.pixel_buffer) {
        free(linux_state.pixel_buffer);
        linux_state.pixel_buffer = NULL;
        linux_state.pixel_width = 0;
        linux_state.pixel_height = 0;
        reset_pixels_dirty();
        linux_state.pixel_has_data = 0;
    }

    // Adjust cursor if needed
    if (linux_state.cursor_x > width) linux_state.cursor_x = width;
    if (linux_state.cursor_y > height) linux_state.cursor_y = height;
    linux_apply_mode_cursor_locked();

    mark_screen_dirty();

    pthread_mutex_unlock(&linux_state.mutex);

    // Update text_info
    cio_textinfo.screenwidth = width;
    cio_textinfo.screenheight = height;
    cio_textinfo.winright = width;
    cio_textinfo.winbottom = height;

    return 0;
}

// Cleanup
void linux_ciolib_cleanup(void) {
    pthread_mutex_lock(&linux_state.mutex);
    if (linux_state.screen) {
        free(linux_state.screen);
        linux_state.screen = NULL;
    }
    // Free sixel pixel buffer
    if (linux_state.pixel_buffer) {
        free(linux_state.pixel_buffer);
        linux_state.pixel_buffer = NULL;
        linux_state.pixel_width = 0;
        linux_state.pixel_height = 0;
        reset_pixels_dirty();
        linux_state.pixel_has_data = 0;
    }

    // Reset all state fields to defaults so next session starts clean
    linux_state.width = 80;
    linux_state.height = 25;
    linux_state.cursor_x = 1;
    linux_state.cursor_y = 1;
    linux_state.cursor_visible = 1;
    linux_state.cursor_type = _NORMALCURSOR;
    linux_state.current_attr = 7;
    linux_state.current_flags = 0;
    linux_state.fg_color = 7;
    linux_state.bg_color = 0;
    linux_state.dirty = 0;
    linux_state.dirty_min_x = 0;
    linux_state.dirty_max_x = 0;
    linux_state.dirty_min_y = 0;
    linux_state.dirty_max_y = 0;
    linux_state.char_pixel_height = 0;
    linux_state.vmode_override = -1;
    linux_state.curs_start = 14;
    linux_state.curs_end = 15;
    linux_state.curs_blinks = 1;
    linux_state.default_curs_start = 14;
    linux_state.default_curs_end = 15;

    // Reset palette: zero entire array, then restore default CGA 16 colors
    memset(linux_state.palette, 0, sizeof(linux_state.palette));
    {
        static const uint32_t default_cga[16] = {
            0x000000, 0x0000AA, 0x00AA00, 0x00AAAA,
            0xAA0000, 0xAA00AA, 0xAA5500, 0xAAAAAA,
            0x555555, 0x5555FF, 0x55FF55, 0x55FFFF,
            0xFF5555, 0xFF55FF, 0xFFFF55, 0xFFFFFF
        };
        memcpy(linux_state.palette, default_cga, sizeof(default_cga));
    }

    // Reset video flags and global ciolib colors
    linux_video_flags = 0;
    ciolib_fg = 7;
    ciolib_bg = 0;

    // Reset font slot state
    linux_default_font = 0;
    linux_current_font[0] = 0;
    linux_current_font[1] = 0;
    linux_current_font[2] = 0;
    linux_current_font[3] = 0;

    pthread_mutex_unlock(&linux_state.mutex);
    pthread_mutex_destroy(&linux_state.mutex);
    if (input_mutex_initialized) {
        pthread_mutex_destroy(&input_mutex);
        input_mutex_initialized = 0;
    }
}

// Undefine ciolib macros to avoid conflicts with our wrapper functions
#undef gotoxy
#undef wherex
#undef wherey
#undef clrscr
#undef gettextinfo
#undef textattr
#undef vmem_puttext
#undef vmem_gettext
#undef movetext
#undef window
#undef putch
#undef setcursortype
#undef clreol
#undef delline
#undef insline
#undef textbackground
#undef textcolor
#undef highvideo
#undef lowvideo
#undef normvideo
#undef setfont
#undef getfont
#undef settitle
#undef setname
#undef beep
#undef kbhit
#undef getch
#undef getche
#undef ungetch
#undef wscroll
#undef delay
#undef puttext
#undef gettext
#undef textmode
#undef setpalette
#undef attr2palette
#undef setvideoflags
#undef getvideoflags
#undef setunderline

// ciolib_* wrapper functions that cterm.c expects
// Call linux_* functions directly to avoid macro issues

void ciolib_gotoxy(int x, int y) {
    linux_gotoxy(x, y);
}

int ciolib_wherex(void) {
    return linux_wherex();
}

int ciolib_wherey(void) {
    return linux_wherey();
}

void ciolib_clrscr(void) {
    linux_clrscr();
}

void ciolib_gettextinfo(struct text_info *info) {
    linux_gettextinfo(info);
}

void ciolib_textattr(int attr) {
    linux_textattr(attr);
}

int ciolib_vmem_puttext(int sx, int sy, int ex, int ey, struct vmem_cell *buf) {
    return linux_vmem_puttext(sx, sy, ex, ey, buf);
}

int ciolib_vmem_gettext(int sx, int sy, int ex, int ey, struct vmem_cell *buf) {
    return linux_vmem_gettext(sx, sy, ex, ey, buf);
}

int ciolib_movetext(int sx, int sy, int ex, int ey, int dx, int dy) {
    return linux_movetext(sx, sy, ex, ey, dx, dy);
}

void ciolib_window(int sx, int sy, int ex, int ey) {
    linux_window(sx, sy, ex, ey);
}

int ciolib_putch(int ch) {
    return linux_putch(ch);
}

void ciolib_setcursortype(int type) {
    linux_setcursortype(type);
}

void ciolib_clreol(void) {
    linux_clreol();
}
/* Called by cterm's clear2bol() (ESC[1J / ESC[1K) — see linux_clear_pixel_cells. */
void ciolib_clear_pixel_rect(int sx, int sy, int ex, int ey) {
    pthread_mutex_lock(&linux_state.mutex);
    linux_clear_pixel_cells(sx, sy, ex, ey);
    pthread_mutex_unlock(&linux_state.mutex);
}


void ciolib_delline(void) {
    linux_delline();
}

void ciolib_insline(void) {
    linux_insline();
}

void ciolib_textbackground(int color) {
    linux_textbackground(color);
}

void ciolib_textcolor(int color) {
    linux_textcolor(color);
}

void ciolib_highvideo(void) {
    linux_highvideo();
}

void ciolib_lowvideo(void) {
    linux_lowvideo();
}

void ciolib_normvideo(void) {
    linux_normvideo();
}

int ciolib_setfont(int font, int force, int font_num) {
    return linux_setfont(font, force, font_num);
}

int ciolib_getfont(int font_num) {
    return linux_getfont(font_num);
}

void ciolib_settitle(const char *title) {
    linux_settitle(title);
}

void ciolib_setname(const char *name) {
    linux_setname(name);
}

void ciolib_beep(void) {
    linux_beep();
}

int ciolib_kbhit(void) {
    return linux_kbhit();
}

int ciolib_getch(void) {
    return linux_getch();
}

int ciolib_getche(void) {
    return linux_getche();
}

int ciolib_ungetch(int ch) {
    return linux_ungetch(ch);
}

void ciolib_wscroll(void) {
    linux_wscroll();
}

void ciolib_delay(long ms) {
    linux_delay(ms);
}

int ciolib_puttext(int sx, int sy, int ex, int ey, void *buf) {
    return linux_puttext(sx, sy, ex, ey, buf);
}

int ciolib_gettext(int sx, int sy, int ex, int ey, void *buf) {
    return linux_gettext(sx, sy, ex, ey, buf);
}

void ciolib_textmode(int mode) {
    linux_textmode(mode);
}

int ciolib_setpalette(uint32_t entry, uint16_t r, uint16_t g, uint16_t b) {
    return linux_setpalette(entry, r, g, b);
}

int ciolib_attr2palette(uint8_t attr, uint32_t *fg, uint32_t *bg) {
    return linux_attr2palette(attr, fg, bg);
}

void ciolib_setvideoflags(int flags) {
    linux_setvideoflags(flags);
}

int ciolib_getvideoflags(void) {
    return linux_getvideoflags();
}

// Additional ciolib functions expected by cterm.c
void ciolib_setcolour(uint32_t fg, uint32_t bg) {
    pthread_mutex_lock(&linux_state.mutex);
    linux_state.fg_color = fg;
    linux_state.bg_color = bg;
    // Update global ciolib_fg/bg — used by cursor rendering (color_value(ciolib_fg))
    ciolib_fg = fg;
    ciolib_bg = bg;
    // Do NOT rebuild current_attr here.  cterm manages legacy_attr via
    // textattr() which is the authoritative source for the DOS attribute byte.
    // Rebuilding it from fg/bg corrupts legacy_attr when extended or RGB
    // colors are in use (matching Android/iOS behaviour).
    pthread_mutex_unlock(&linux_state.mutex);
}

void ciolib_setunderline(bool underline) {
    pthread_mutex_lock(&linux_state.mutex);
    if (underline)
        linux_state.current_flags |= VMEM_FLAG_UNDERLINE;
    else
        linux_state.current_flags &= ~VMEM_FLAG_UNDERLINE;
    pthread_mutex_unlock(&linux_state.mutex);
}

int ciolib_attrfont(uint8_t attr) {
    // Return font number for attribute - just return 0 for default
    return 0;
}

// ============================================================================
// Explicit video mode (Commodore / Atari emulation)
// ============================================================================

/* Load the 16-entry attribute palette that vparams declares for the active mode.
   Only non-CGA palettes (C64, Atari, ...) are applied; ordinary text modes keep the
   CGA colours the renderer has always used, so ANSI sessions are unaffected. */
void linux_ciolib_apply_mode_palette(void) {
    static const uint32_t cga_palette[16] = {
        0x000000, 0x0000AA, 0x00AA00, 0x00AAAA,
        0xAA0000, 0xAA00AA, 0xAA5500, 0xAAAAAA,
        0x555555, 0x5555FF, 0x55FF55, 0x55FFFF,
        0xFF5555, 0xFF55FF, 0xFFFF55, 0xFFFFFF
    };
    int pal = -1;

    if (linux_state.vmode_override >= 0) {
        for (size_t i = 0; i < NUMMODES; i++) {
            if (vparams[i].mode == linux_state.vmode_override) {
                pal = vparams[i].palette;
                break;
            }
        }
    }

    pthread_mutex_lock(&linux_state.mutex);
    /* ⚠️ Uses the palette the SHARED source declares — currently the "pepto" C64 set.
     *
     * An override to the older CTerm-derived set (still in vidmodes.c behind `#if 0`)
     * was tried on Windows 2026-09-03 and REVERTED the same day: it reads BRIGHTER, not
     * dimmer, on a real board. Mean luminance says otherwise (126 pepto vs 120 CTerm),
     * which is exactly why that metric is the wrong one here — the CTerm set is far more
     * SATURATED (purple B41AE2 vs 8E3C97, cyan 30E6C6 vs 75CEC8, green 1FD21E vs 56AC4D)
     * and chroma is what reads as glare. Do not "fix the brightness" by swapping palettes
     * again without looking at a board first. */
    if (pal == C64_PALETTE || pal == ATARI_PALETTE) {
        for (int i = 0; i < 16; i++) {
            uint32_t dac = palettes[pal][i];
            if (dac < TOTAL_DAC_SIZE) {
                linux_state.palette[i] = ((uint32_t)dac_default[dac].red << 16)
                                       | ((uint32_t)dac_default[dac].green << 8)
                                       |  (uint32_t)dac_default[dac].blue;
            }
        }
    } else {
        memcpy(linux_state.palette, cga_palette, sizeof(cga_palette));
    }
    linux_state.dirty = 1;
    pthread_mutex_unlock(&linux_state.mutex);
}

void linux_ciolib_set_video_mode(int mode) {
    linux_state.vmode_override = mode;
    linux_ciolib_apply_mode_palette();
    linux_ciolib_apply_mode_cursor();
    LOGI("Video mode override set to %d", mode);
}

int linux_ciolib_get_video_mode(void) {
    return linux_state.vmode_override;
}

/**
 * Does the active video mode treat attribute bit 7 as BACKGROUND BRIGHTNESS rather than BLINK?
 *
 * ⚠️ This is what stops the Commodore modes flickering. `set_bright_bg` in cterm.c deliberately
 * sets attr bit 7 in any CIOLIB_VIDEO_BGBRIGHT mode to mean "background colour 8-15", and the C64
 * and C128 modes are both BGBRIGHT|NOBLINK. A renderer that still reads bit 7 as the ANSI blink
 * attribute therefore blinks every reverse-video cell on a PETSCII screen — which on a real board
 * is most of the artwork.
 */
int linux_ciolib_mode_no_blink(void) {
    /* iCE colours: ESC[?33h sets CIOLIB_VIDEO_BGBRIGHT through setvideoflags in any mode, and then
     * bit 7 is the bright background too, so those cells must not blink. */
    if (linux_video_flags & (CIOLIB_VIDEO_BGBRIGHT | CIOLIB_VIDEO_NOBLINK))
        return 1;
    int mode = linux_current_vmode();
    for (size_t i = 0; i < NUMMODES; i++) {
        if (vparams[i].mode == mode)
            return (vparams[i].flags & CIOLIB_VIDEO_NOBLINK) ? 1 : 0;
    }
    return 0;
}

/* Character cell height (pixels) the active explicit mode expects, or 0 when none. */
int linux_ciolib_get_mode_char_height(void) {
    if (linux_state.vmode_override < 0)
        return 0;
    for (size_t i = 0; i < NUMMODES; i++) {
        if (vparams[i].mode == linux_state.vmode_override)
            return vparams[i].charheight;
    }
    return 0;
}

// Get/set modepalette functions
int ciolib_get_modepalette(uint32_t *palette) {
    if (palette) {
        pthread_mutex_lock(&linux_state.mutex);
        memcpy(palette, linux_state.palette, 16 * sizeof(uint32_t));
        pthread_mutex_unlock(&linux_state.mutex);
    }
    return 0;
}

int ciolib_set_modepalette(uint32_t *palette) {
    if (palette) {
        pthread_mutex_lock(&linux_state.mutex);
        memcpy(linux_state.palette, palette, 16 * sizeof(uint32_t));
        pthread_mutex_unlock(&linux_state.mutex);
    }
    return 0;
}
