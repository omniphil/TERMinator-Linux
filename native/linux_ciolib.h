/**
 * Linux ciolib header
 *
 * Declares functions for managed code access to the Linux ciolib implementation.
 * Ported from wpf_ciolib.h for Linux/Avalonia.
 */

#ifndef _LINUX_CIOLIB_H_
#define _LINUX_CIOLIB_H_

#include <stdbool.h>
#include <stdint.h>

// Forward declaration
struct vmem_cell;

#ifdef __cplusplus
extern "C" {
#endif

// Screen state accessors
int linux_ciolib_get_screen_width(void);
int linux_ciolib_get_screen_height(void);
int linux_ciolib_get_cursor_x(void);
int linux_ciolib_get_cursor_y(void);
int linux_ciolib_is_cursor_visible(void);
int linux_ciolib_get_cursor_type(void);
int linux_ciolib_get_cursor_start(void);
int linux_ciolib_get_cursor_end(void);
int linux_ciolib_get_cursor_blinks(void);
uint32_t linux_ciolib_get_fg_color(void);
int linux_ciolib_get_char_pixel_height(void);
void linux_ciolib_set_custom_cursor(int s, int e, int r, int b, int v);
int linux_ciolib_is_dirty(void);
void linux_ciolib_clear_dirty(void);
int linux_ciolib_get_dirty_region(int *min_x, int *min_y, int *max_x, int *max_y);
struct vmem_cell* linux_ciolib_get_screen_buffer(void);
uint32_t* linux_ciolib_get_palette(void);

// Thread safety
void linux_ciolib_lock(void);
void linux_ciolib_unlock(void);

// Input handling
void linux_ciolib_push_input(unsigned char c);
void linux_ciolib_push_input_buffer(const unsigned char *buf, int len);

// Terminal management
int linux_ciolib_resize(int width, int height);
void linux_ciolib_cleanup(void);

// Explicit video mode (Commodore / Atari emulation); -1 = derive from geometry
void linux_ciolib_set_video_mode(int mode);
int linux_ciolib_get_video_mode(void);
int linux_ciolib_get_mode_char_height(void);
int linux_ciolib_mode_no_blink(void);
void linux_ciolib_apply_mode_palette(void);

// Sixel pixel framebuffer
int linux_ciolib_has_sixel_data(void);
int linux_ciolib_is_pixel_dirty(void);
void linux_ciolib_clear_pixel_dirty(void);
int linux_ciolib_copy_dirty_pixels(uint32_t *dest, int dest_w, int dest_h, int dest_stride, int full,
                                   int *x, int *y, int *w, int *h, int *bw, int *bh);
uint32_t* linux_ciolib_get_pixel_buffer(void);
int linux_ciolib_get_pixel_width(void);
int linux_ciolib_get_pixel_height(void);
void linux_ciolib_clear_pixels(void);
void linux_ciolib_set_char_pixel_height(int h);
// Re-derive the cursor scanlines (curs_start/curs_end) from the active mode's
// vparams entry. Must be called whenever the mode, the geometry or the font
// height changes — the scanlines are relative to the CELL, so a value carried
// over from a taller cell draws the cursor a row low.
void linux_ciolib_apply_mode_cursor(void);

struct ciolib_pixels;
struct ciolib_mask;
int linux_ciolib_setpixels(uint32_t sx, uint32_t sy, uint32_t ex, uint32_t ey,
                          uint32_t x_off, uint32_t y_off, uint32_t mx_off, uint32_t my_off,
                          struct ciolib_pixels *pixels, struct ciolib_mask *mask);

#ifdef __cplusplus
}
#endif

#endif // _LINUX_CIOLIB_H_
