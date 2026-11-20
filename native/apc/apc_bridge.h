/*
 * apc_bridge.h -- SyncTERM's APC handler (ESC _ SyncTERM:... ST) for the Linux bridge. See apc_bridge.c.
 * Ported from the Windows client (TERMinator-Windows/native/apc_bridge.h), where the hooks are wpf_apc_*.
 */
#ifndef APC_BRIDGE_H
#define APC_BRIDGE_H

#include <stdbool.h>
#include <stddef.h>

struct cterminal;

/* Joins `rel` onto the board's cache directory (UTF-8, '/' separators). False when no cache directory
 * is set, `rel` is absolute or has ".." segments. */
bool apc_cache_path(const char *rel, char *out, size_t outsz);

/* dlopen()s `soname` from the AppImage's private usr/lib/apc/ (see apc_bridge.c); NULL outside the AppImage or
 * when it isn't bundled. Only for when the system has no copy. */
void *apc_dlopen_bundled(const char *soname);

/* Sends a reply to the BBS. */
void apc_send(const char *buf, size_t len);

/* Installs the handler on `cterm` if it isn't already (cheap; called before every cterm_write). */
void linux_apc_attach(struct cterminal *cterm);
/* Once per processed chunk: audio Update notifications. */
void linux_apc_poll(void);
/* Disconnect: frees image buffers, audio slots and channels. */
void linux_apc_session_reset(void);

#endif /* APC_BRIDGE_H */
