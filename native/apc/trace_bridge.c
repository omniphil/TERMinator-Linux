/*
 * trace_bridge.c -- TRACE (TERMinator Render And Compute Engine) escape handling for the Linux bridge.
 * Ported from TERMinator-Windows/native/trace_bridge.c (the master) 2026-09-18: same protocol and limits, pthread lock.
 *
 * A door talks to TRACE with APC strings in the normal terminal stream (BBSGames/Doom/ENGINE.md):
 *   ESC _ TERMinator:TRACE;Query ESC \                                   -> answered here (detection):
 *       ESC _ TERMinator:TRACE;Info;v=1;wasm=1;audio=1;... ESC \     (what this client can do for a module)
 *   ESC _ TERMinator:TRACE;Open;module=fractal;v=1;rect=1,1,23,79 ESC \  -> queued for the app
 *   ESC _ TERMinator:TRACE;Data;module=fractal;<payload> ESC \           -> queued for the app
 *   ESC _ TERMinator:TRACE;Asset;module=doom;sha256=<hex>;size=<n> ESC \ -> queued for the app (a WAD or similar)
 *   ESC _ TERMinator:TRACE;Hide|Show|Close;module=fractal ESC \          -> queued for the app
 * Terminals without TRACE ignore APC strings they don't know, so a door can send these safely to anyone.
 *
 * Binary frames (Query reply "bin=1"): cterm only takes printable ASCII inside an APC string, so text commands carry
 * binary as base64 (a third bigger). A door that sees bin=1 can send
 *   ESC _ TERMinator:TRACE;Bin;<escaped bytes> ESC \  (ST)
 * instead, where the bytes are a command header line and raw payload ("Data;module=doom\n<payload>",
 * "Put;module=doom;offset=0\n<chunk>") with nine byte values escaped as '=' followed by the byte + 64 (mod 256):
 * NUL, LF, CR, XON, XOFF, CAN, ESC, '=' and 0xFF. About 3.5% bigger on random data, and nothing that telnet's CR/NUL
 * rules, IAC doubling or ZMODEM detection (CAN) could touch ever appears raw; with no raw ESC inside, ESC \ always
 * ends the frame. These frames never reach cterm: trace_stream_feed() lifts them out of the received bytes first,
 * in order with the text around them, and queues them for the app marked with a leading 0x01 byte.
 *
 * The app does all the work (starting modules, placing them over the screen) and tells us which modules are
 * installed for the Query reply. Separate from the SyncTERM APCs (apc_bridge.c) and from TAP+ (OSC 800).
 */

#include <pthread.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "apc_bridge.h"
#include "trace_bridge.h"
#include "wm_bridge.h"

#define TRACE_PREFIX      "TERMinator:TRACE;"
#define TRACE_PREFIX_LEN  (sizeof(TRACE_PREFIX) - 1)
#define TRACE_VERSION     1
/* Deep enough for a big upload arriving in one burst: a 4 MB WAD is about 1400 Put commands, and on a fast link
 * cterm can hand us all of them between two polls. Anything dropped here would fail the upload. */
#define QUEUE_MAX         4096
#define COMMAND_MAX       8192
/* A binary frame's decoded size: a header line plus up to 65535 bytes of payload (a Data message, a Put chunk) */
#define BIN_MAX           (65535 + 512)
/* Everything queued at once, so a board can't make us hold more than this between two polls */
#define QUEUE_BYTES_MAX   (32 * 1024 * 1024)
#define BIN_MARK          0x01   /* first byte of a queued binary frame (text commands never start with it) */

typedef struct {
	char   *data;
	size_t  len;
} queued_t;

#ifdef SYNCTERM_BRIDGE_EXPORTS
#define TRACE_API __attribute__((visibility("default")))
#else
#define TRACE_API
#endif

static pthread_mutex_t  g_lock = PTHREAD_MUTEX_INITIALIZER;
static queued_t         g_queue[QUEUE_MAX];
static int              g_head, g_count;
static size_t           g_queued_bytes;
static char             g_capabilities[512];  /* e.g. "wasm=1", from the app */

static void ensure_init(void)
{
	/* statically initialised lock: nothing to do (Windows needs InitOnce here) */
}

/* Queues a command for the app, in arrival order; a binary frame gets BIN_MARK in front. */
static void enqueue(const char *data, size_t len, bool binary)
{
	pthread_mutex_lock(&g_lock);
	if (g_count < QUEUE_MAX && g_queued_bytes + len + 2 <= QUEUE_BYTES_MAX) {
		char *copy = (char *)malloc(len + 2);
		if (copy != NULL) {
			size_t at = 0;
			if (binary)
				copy[at++] = BIN_MARK;
			memcpy(copy + at, data, len);
			copy[at + len] = 0;
			g_queue[(g_head + g_count) % QUEUE_MAX].data = copy;
			g_queue[(g_head + g_count) % QUEUE_MAX].len = at + len;
			g_queued_bytes += at + len;
			g_count++;
		}
	}
	pthread_mutex_unlock(&g_lock);
}

bool trace_apc_handler(const char *strbuf, size_t slen)
{
	const char *cmd;
	size_t len;

	if (strbuf == NULL || slen < TRACE_PREFIX_LEN || strncmp(strbuf, TRACE_PREFIX, TRACE_PREFIX_LEN) != 0)
		return false;
	ensure_init();
	cmd = strbuf + TRACE_PREFIX_LEN;
	len = slen - TRACE_PREFIX_LEN;

	if (len >= 5 && strncmp(cmd, "Query", 5) == 0 && (len == 5 || cmd[5] == ';')) {
		char reply[700];
		int n;
		pthread_mutex_lock(&g_lock);
		/* Switched off in Preferences (no capabilities): stay silent, like a terminal without TRACE */
		n = g_capabilities[0] ? snprintf(reply, sizeof(reply), "\x1b_TERMinator:TRACE;Info;v=%d;%s\x1b\\",
		                                 TRACE_VERSION, g_capabilities) : 0;
		pthread_mutex_unlock(&g_lock);
		if (n > 0 && n < (int)sizeof(reply))
			apc_send(reply, (size_t)n);
		return true;
	}

	if (len == 0 || len >= COMMAND_MAX)
		return true;  /* ours, but empty or oversized: dropped */

	enqueue(cmd, len, false);
	return true;
}

/* ---- binary frames in the received stream ---- */

#define BIN_PREFIX        "\x1b_TERMinator:TRACE;Bin;"
#define BIN_PREFIX_LEN    (sizeof(BIN_PREFIX) - 1)

static size_t         g_match;          /* bytes of BIN_PREFIX matched so far (held back from cterm) */
static bool           g_in_frame;       /* inside a frame: collecting until ESC \ */
static bool           g_frame_esc;      /* the last frame byte was ESC */
static bool           g_frame_unescape; /* the last frame byte was '=' */
static bool           g_frame_overflow; /* too big: read to its end, then drop it */
static unsigned char *g_frame;
static size_t         g_frame_len;

static void frame_reset(void)
{
	g_in_frame = g_frame_esc = g_frame_unescape = g_frame_overflow = false;
	g_frame_len = 0;
}

static void frame_put(unsigned char c)
{
	if (g_frame_overflow)
		return;
	if (g_frame == NULL)
		g_frame = (unsigned char *)malloc(BIN_MAX);
	if (g_frame == NULL || g_frame_len >= BIN_MAX) {
		g_frame_overflow = true;
		return;
	}
	g_frame[g_frame_len++] = c;
}

void trace_stream_feed(const char *in, size_t len, trace_text_fn text, void *ctx)
{
	size_t run = 0;   /* start of the text not yet handed on */

	for (size_t i = 0; i < len; i++) {
		unsigned char c = (unsigned char)in[i];

		if (g_in_frame) {
			run = i + 1;
			if (g_frame_esc) {
				g_frame_esc = false;
				if (c == '\\') {
					/* ESC \ : the frame is complete. Text before it was handed on already, so it queues in order */
					if (!g_frame_overflow && !g_frame_unescape && g_frame_len > 0)
						enqueue((const char *)g_frame, g_frame_len, true);
					frame_reset();
					continue;
				}
				/* A raw ESC can't be inside a frame: the link garbled it. Drop the frame and carry on from here as
				 * text, so the terminal isn't left swallowing everything. */
				frame_reset();
				run = i;
				i--;
				continue;
			}
			if (c == 0x1b)
				g_frame_esc = true;
			else if (g_frame_unescape) {
				g_frame_unescape = false;
				frame_put((unsigned char)(c - 64));
			} else if (c == '=')
				g_frame_unescape = true;
			else
				frame_put(c);
			continue;
		}

		if (g_match > 0 || c == 0x1b) {
			if (c == (unsigned char)BIN_PREFIX[g_match]) {
				if (g_match == 0 && i > run)
					text(in + run, i - run, ctx);   /* the text up to this ESC */
				run = i + 1;
				if (++g_match == BIN_PREFIX_LEN) {
					g_match = 0;
					frame_reset();
					g_in_frame = true;
				}
				continue;
			}
			if (g_match > 0) {
				/* Not a frame after all: what was held back is ordinary text, then look at this byte again */
				text(BIN_PREFIX, g_match, ctx);
				g_match = 0;
				run = i;
				i--;
			}
		}
	}
	if (run < len && !g_in_frame && g_match == 0)
		text(in + run, len - run, ctx);
}

void trace_session_reset(void)
{
	ensure_init();
	pthread_mutex_lock(&g_lock);
	while (g_count > 0) {
		free(g_queue[g_head].data);
		g_head = (g_head + 1) % QUEUE_MAX;
		g_count--;
	}
	g_queued_bytes = 0;
	pthread_mutex_unlock(&g_lock);
	g_match = 0;   /* (the stream is only fed under the bridge's lock, as is this reset) */
	frame_reset();
	wm_session_reset();   /* Rainwire's window messages go with the session too (wm_bridge.c) */
}

/* What this TERMinator can run, for the Query reply after "v=<n>;", e.g. "wasm=1". */
TRACE_API void syncterm_trace_set_modules(const char *capabilities)
{
	ensure_init();
	pthread_mutex_lock(&g_lock);
	snprintf(g_capabilities, sizeof(g_capabilities), "%s", capabilities != NULL ? capabilities : "");
	pthread_mutex_unlock(&g_lock);
}

/* Takes the oldest queued TRACE command (the text after "TERMinator:TRACE;", or 0x01 + a decoded binary
 * frame) into buffer.
 * Returns its length, 0 when none is queued, or -1 when buffer is too small (the command stays queued). */
TRACE_API int syncterm_trace_poll(char *buffer, int buffer_len)
{
	int result = 0;

	ensure_init();
	pthread_mutex_lock(&g_lock);
	if (g_count > 0) {
		size_t len = g_queue[g_head].len;
		if (buffer == NULL || buffer_len <= 0 || len >= (size_t)buffer_len) {
			result = -1;
		} else {
			memcpy(buffer, g_queue[g_head].data, len + 1);
			free(g_queue[g_head].data);
			g_queued_bytes -= len;
			g_head = (g_head + 1) % QUEUE_MAX;
			g_count--;
			result = (int)len;
		}
	}
	pthread_mutex_unlock(&g_lock);
	return result;
}
