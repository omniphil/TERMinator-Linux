/*
 * pic_bridge.c -- TERMinator:Pic, a Rainwire BBS's pictures through the client's own GIF player
 * (Rainwire docs/PICTURES.md, protocol A).
 * Ported from TERMinator-Windows/native/pic_bridge.c (the master) 2026-10-04: same protocol and limits, pthread lock.
 *
 * The board stores the file first with SyncTERM's cache APC (C;S, apc_bridge.c), then places it with
 *   ESC _ TERMinator:Pic;Show;id=<0-7>;name=<cache name>;rect=<row1>,<col1>,<row2>,<col2> ESC \
 *   ESC _ TERMinator:Pic;Hide;id=<0-7> ESC \
 *   ESC _ TERMinator:Pic;HideAll ESC \
 * rect is 1-based screen cells, inclusive. A Show whose file isn't in the cache is ignored. The client says it has
 * this with pic=1 in its Rainwire:Caps;Info answer (osc_ext.h).
 *
 * Commands are checked here and queued for the app in stream order, with the name already resolved to a file:
 *   Show;id=<n>;rect=<r1>,<c1>,<r2>,<c2>;path=<full UTF-8 path, '/' separators>    (path is last: it runs to the end)
 *   Hide;id=<n>
 *   HideAll
 * A full screen clear (linux_clrscr) queues its own HideAll at that point in the stream, so a clear followed by a new
 * Show still ends with the picture up. The app does the drawing (TerminalView's Pic slots); TAP+ is separate.
 */

#include <pthread.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>

#include "apc_bridge.h"
#include "pic_bridge.h"

#define PIC_PREFIX      "TERMinator:Pic;"
#define PIC_PREFIX_LEN  (sizeof(PIC_PREFIX) - 1)
#define PIC_SLOTS       8
#define PIC_NAME_MAX    128
#define PIC_ARGS_MAX    512    /* a Show is well under this; longer is dropped */
#define PIC_PATH_MAX    1024   /* as apc_bridge.c's APC_PATH_MAX */
#define PIC_QUEUE_MAX   256

#ifdef SYNCTERM_BRIDGE_EXPORTS
#define PIC_API __attribute__((visibility("default")))
#else
#define PIC_API
#endif

static pthread_mutex_t  g_lock = PTHREAD_MUTEX_INITIALIZER;
static char            *g_queue[PIC_QUEUE_MAX];
static int              g_head, g_count;
static bool             g_shown;   /* a Show was queued since the last HideAll: a clear has something to hide */

static void ensure_init(void)
{
	/* statically initialised lock: nothing to do (Windows needs InitOnce here) */
}

/* Queues a command for the app, in arrival order. Call with g_lock held. */
static void enqueue_locked(const char *cmd)
{
	char *copy;

	if (g_count >= PIC_QUEUE_MAX)
		return;
	copy = strdup(cmd);
	if (copy == NULL)
		return;
	g_queue[(g_head + g_count) % PIC_QUEUE_MAX] = copy;
	g_count++;
}

static void enqueue_hide_all_locked(void)
{
	/* Back to back HideAlls (a board clearing every frame) collapse into one */
	if (g_count > 0 && strcmp(g_queue[(g_head + g_count - 1) % PIC_QUEUE_MAX], "HideAll") == 0)
		return;
	enqueue_locked("HideAll");
	g_shown = false;
}

/* The value of key=... in a ';'-separated argument list (NUL-terminated, modified in place), or NULL. */
static char *arg_value(char *args, const char *key)
{
	size_t klen = strlen(key);
	char  *p    = args;

	while (p != NULL && *p) {
		char *end = strchr(p, ';');
		if (strncmp(p, key, klen) == 0 && p[klen] == '=') {
			if (end != NULL)
				*end = '\0';
			return p + klen + 1;
		}
		p = end != NULL ? end + 1 : NULL;
	}
	return NULL;
}

static bool parse_slot(const char *s, int *id)
{
	if (s == NULL || s[0] < '0' || s[0] >= '0' + PIC_SLOTS || s[1] != '\0')
		return false;
	*id = s[0] - '0';
	return true;
}

/* ^[A-Za-z0-9._-]{1,128}$ and no "..": a flat name in the board's cache folder */
static bool valid_name(const char *s)
{
	size_t n = 0;

	if (s == NULL || strstr(s, "..") != NULL)
		return false;
	for (; s[n]; n++) {
		char c = s[n];
		if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.' || c == '_'
		      || c == '-'))
			return false;
	}
	return n >= 1 && n <= PIC_NAME_MAX;
}

static bool file_exists(const char *utf8)
{
	struct stat st;

	return stat(utf8, &st) == 0 && S_ISREG(st.st_mode);
}

static void show(char *args)
{
	char  buf[PIC_ARGS_MAX];
	char  path[PIC_PATH_MAX];
	char  cmd[PIC_PATH_MAX + 96];
	char *id_s, *name, *rect;
	int   id, r1, c1, r2, c2, used = 0;

	/* arg_value cuts the list it searches, so each key looks in its own copy */
	strcpy(buf, args);
	id_s = arg_value(buf, "id");
	if (!parse_slot(id_s, &id))
		return;
	strcpy(buf, args);
	name = arg_value(buf, "name");
	if (!valid_name(name) || !apc_cache_path(name, path, sizeof(path)) || !file_exists(path))
		return;   /* not in the cache: the client can't fetch it, so nothing happens */
	strcpy(buf, args);
	rect = arg_value(buf, "rect");
	if (rect == NULL || sscanf(rect, "%d,%d,%d,%d%n", &r1, &c1, &r2, &c2, &used) != 4 || rect[used] != '\0')
		return;
	if (r1 < 1 || c1 < 1 || r2 < r1 || c2 < c1 || r2 > 1000 || c2 > 1000)
		return;

	snprintf(cmd, sizeof(cmd), "Show;id=%d;rect=%d,%d,%d,%d;path=%s", id, r1, c1, r2, c2, path);
	pthread_mutex_lock(&g_lock);
	enqueue_locked(cmd);
	g_shown = true;
	pthread_mutex_unlock(&g_lock);
}

bool pic_apc_handler(const char *strbuf, size_t slen)
{
	char args[PIC_ARGS_MAX];
	size_t len;

	if (strbuf == NULL || slen < PIC_PREFIX_LEN || strncmp(strbuf, PIC_PREFIX, PIC_PREFIX_LEN) != 0)
		return false;
	ensure_init();
	len = slen - PIC_PREFIX_LEN;
	if (len == 0 || len >= sizeof(args))
		return true;   /* ours, but empty or oversized: dropped */
	memcpy(args, strbuf + PIC_PREFIX_LEN, len);
	args[len] = '\0';

	if (strncmp(args, "Show;", 5) == 0) {
		show(args + 5);
	} else if (strncmp(args, "Hide;", 5) == 0) {
		char cmd[16];
		int  id;
		if (parse_slot(arg_value(args + 5, "id"), &id)) {
			snprintf(cmd, sizeof(cmd), "Hide;id=%d", id);
			pthread_mutex_lock(&g_lock);
			enqueue_locked(cmd);
			pthread_mutex_unlock(&g_lock);
		}
	} else if (strcmp(args, "HideAll") == 0) {
		pthread_mutex_lock(&g_lock);
		enqueue_hide_all_locked();
		pthread_mutex_unlock(&g_lock);
	}
	return true;
}

void pic_screen_cleared(void)
{
	ensure_init();
	pthread_mutex_lock(&g_lock);
	if (g_shown)
		enqueue_hide_all_locked();
	pthread_mutex_unlock(&g_lock);
}

void pic_session_reset(void)
{
	ensure_init();
	pthread_mutex_lock(&g_lock);
	while (g_count > 0) {
		free(g_queue[g_head]);
		g_head = (g_head + 1) % PIC_QUEUE_MAX;
		g_count--;
	}
	g_shown = false;
	pthread_mutex_unlock(&g_lock);
}

/* Takes the oldest queued Pic command (see the top of this file, NUL-terminated) into buffer. Returns its length,
 * 0 when none is queued, or -1 when buffer is too small (the command stays queued). */
PIC_API int syncterm_pic_poll(char *buffer, int buffer_len)
{
	int result = 0;

	ensure_init();
	pthread_mutex_lock(&g_lock);
	if (g_count > 0) {
		size_t len = strlen(g_queue[g_head]);
		if (buffer == NULL || buffer_len <= 0 || len >= (size_t)buffer_len) {
			result = -1;
		} else {
			memcpy(buffer, g_queue[g_head], len + 1);
			free(g_queue[g_head]);
			g_head = (g_head + 1) % PIC_QUEUE_MAX;
			g_count--;
			result = (int)len;
		}
	}
	pthread_mutex_unlock(&g_lock);
	return result;
}
