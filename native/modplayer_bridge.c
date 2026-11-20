/**
 * MOD/Tracker Playback Linux Bridge
 *
 * Uses libxmp to load and render MOD/S3M/XM/IT tracker modules
 * as PCM audio for playback via the C#/Avalonia audio system.
 *
 * Ported from modplayer_jni.c (Android JNI bridge).
 */

#include <stdio.h>
#include <string.h>
#include <stdint.h>
#include <pthread.h>

/* DLL export macro */
#ifdef SYNCTERM_BRIDGE_EXPORTS
#define SYNCTERM_API __attribute__((visibility("default")))
#else
#define SYNCTERM_API
#endif

/* Windows debug logging */
#define LOGI(fmt, ...) do { char _buf[512]; snprintf(_buf, sizeof(_buf), "SyncTERM-MOD: " fmt "\n", ##__VA_ARGS__); fprintf(stderr, "%s", _buf); } while(0)
#define LOGW(fmt, ...) do { char _buf[512]; snprintf(_buf, sizeof(_buf), "SyncTERM-MOD WARN: " fmt "\n", ##__VA_ARGS__); fprintf(stderr, "%s", _buf); } while(0)
#define LOGE(fmt, ...) do { char _buf[512]; snprintf(_buf, sizeof(_buf), "SyncTERM-MOD ERROR: " fmt "\n", ##__VA_ARGS__); fprintf(stderr, "%s", _buf); } while(0)

/* libxmp API forward declarations (linked from static library) */
typedef void* xmp_context;
#define XMP_PLAYER_VOLUME 2
extern xmp_context xmp_create_context(void);
extern void xmp_free_context(xmp_context);
extern int xmp_load_module(xmp_context, const char*);
extern void xmp_release_module(xmp_context);
extern int xmp_start_player(xmp_context, int, int);
extern void xmp_end_player(xmp_context);
extern int xmp_play_buffer(xmp_context, void*, int, int);
extern int xmp_set_player(xmp_context, int, int);
extern int xmp_seek_time(xmp_context, int);

#define MOD_SAMPLE_RATE 44100

static xmp_context g_mod_ctx = NULL;
static volatile int g_mod_playing = 0;
static volatile int g_mod_volume = 100;  /* 0-100 */
static pthread_mutex_t g_mod_lock;
static int g_mod_lock_initialized = 0;

/* Initialize the critical section if not already done */
static void ensure_lock_initialized(void) {
    if (!g_mod_lock_initialized) {
        pthread_mutex_init(&g_mod_lock, NULL);
        g_mod_lock_initialized = 1;
    }
}

/**
 * Load a module file
 * Returns: 1 on success, 0 on failure
 */
SYNCTERM_API int syncterm_mod_load(const char* filePath) {
    if (!filePath) {
        LOGE("NULL file path");
        return 0;
    }

    ensure_lock_initialized();
    pthread_mutex_lock(&g_mod_lock);

    /* Clean up any existing context */
    if (g_mod_ctx != NULL) {
        if (g_mod_playing) {
            xmp_end_player(g_mod_ctx);
            g_mod_playing = 0;
        }
        xmp_release_module(g_mod_ctx);
        xmp_free_context(g_mod_ctx);
        g_mod_ctx = NULL;
    }

    /* Create new context and load */
    g_mod_ctx = xmp_create_context();
    if (g_mod_ctx == NULL) {
        LOGE("Failed to create xmp context");
        pthread_mutex_unlock(&g_mod_lock);
        return 0;
    }

    int ret = xmp_load_module(g_mod_ctx, filePath);
    if (ret < 0) {
        LOGE("Failed to load module: error %d", ret);
        xmp_free_context(g_mod_ctx);
        g_mod_ctx = NULL;
        pthread_mutex_unlock(&g_mod_lock);
        return 0;
    }

    /* Start the player: 44100 Hz, 16-bit stereo (default flags = 0) */
    ret = xmp_start_player(g_mod_ctx, MOD_SAMPLE_RATE, 0);
    if (ret < 0) {
        LOGE("Failed to start player: error %d", ret);
        xmp_release_module(g_mod_ctx);
        xmp_free_context(g_mod_ctx);
        g_mod_ctx = NULL;
        pthread_mutex_unlock(&g_mod_lock);
        return 0;
    }

    /* Apply current volume */
    xmp_set_player(g_mod_ctx, XMP_PLAYER_VOLUME, g_mod_volume);

    g_mod_playing = 1;
    LOGI("Module loaded and player started: %s", filePath);

    pthread_mutex_unlock(&g_mod_lock);
    return 1;
}

/**
 * Render PCM frames from the loaded module
 * buffer: pointer to int16_t array (stereo interleaved, 2 samples per frame)
 * frames: number of stereo frames to render
 * Returns: number of frames rendered, or -1 on error/end
 */
SYNCTERM_API int syncterm_mod_get_pcm(int16_t* buffer, int frames) {
    if (!buffer || frames <= 0) {
        return -1;
    }

    ensure_lock_initialized();
    pthread_mutex_lock(&g_mod_lock);

    if (g_mod_ctx == NULL || !g_mod_playing) {
        pthread_mutex_unlock(&g_mod_lock);
        return -1;
    }

    /* Calculate buffer size: stereo 16-bit = 4 bytes per frame */
    int buf_size = frames * 4;

    /* xmp_play_buffer returns 0 on success, -XMP_END on end, negative on error
     * loop parameter: 0 = loop indefinitely, 1 = play once */
    int ret = xmp_play_buffer(g_mod_ctx, buffer, buf_size, 1);

    if (ret < 0) {
        /* -XMP_END means module finished */
        pthread_mutex_unlock(&g_mod_lock);
        return -1;
    }

    pthread_mutex_unlock(&g_mod_lock);
    return frames;
}

/**
 * Stop and release the current module
 */
SYNCTERM_API void syncterm_mod_stop(void) {
    ensure_lock_initialized();
    pthread_mutex_lock(&g_mod_lock);

    if (g_mod_ctx != NULL) {
        if (g_mod_playing) {
            xmp_end_player(g_mod_ctx);
            g_mod_playing = 0;
        }
        xmp_release_module(g_mod_ctx);
        xmp_free_context(g_mod_ctx);
        g_mod_ctx = NULL;
        LOGI("Module stopped and released");
    }

    pthread_mutex_unlock(&g_mod_lock);
}

/**
 * Set playback volume
 * volume: 0.0 to 1.0 (mapped to 0-100 for libxmp)
 */
SYNCTERM_API void syncterm_mod_set_volume(float volume) {
    int vol = (int)(volume * 100.0f);
    if (vol < 0) vol = 0;
    if (vol > 100) vol = 100;

    ensure_lock_initialized();
    pthread_mutex_lock(&g_mod_lock);
    g_mod_volume = vol;
    if (g_mod_ctx != NULL && g_mod_playing) {
        xmp_set_player(g_mod_ctx, XMP_PLAYER_VOLUME, vol);
    }
    pthread_mutex_unlock(&g_mod_lock);
}

/**
 * Restart module from the beginning
 */
SYNCTERM_API void syncterm_mod_restart(void) {
    ensure_lock_initialized();
    pthread_mutex_lock(&g_mod_lock);
    if (g_mod_ctx && g_mod_playing) {
        xmp_restart_module(g_mod_ctx);
    }
    pthread_mutex_unlock(&g_mod_lock);
}

/**
 * Check if a module is currently playing
 * Returns: 1 if playing, 0 if not
 */
SYNCTERM_API int syncterm_mod_is_playing(void) {
    return g_mod_playing ? 1 : 0;
}

/**
 * Seek to a position in milliseconds
 */
SYNCTERM_API void syncterm_mod_seek(int positionMs) {
    ensure_lock_initialized();
    pthread_mutex_lock(&g_mod_lock);
    if (g_mod_ctx && g_mod_playing) {
        xmp_seek_time(g_mod_ctx, positionMs);
        LOGI("Seek to %dms", positionMs);
    }
    pthread_mutex_unlock(&g_mod_lock);
}
