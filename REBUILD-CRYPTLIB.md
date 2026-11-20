# Rebuild cryptlib for Linux

## Why

The SSH identifier validation in cryptlib rejected `SSH-2.0-Go`, which is sent by Go-based SSH proxies used by some BBSes. The fix lowers the minimum software version string length from 3 to 2 characters in `session/ssh2_id.c`.

## What's already done

- `native/cryptlib/include/cryptlib.h` includes `CRYPTLIB_PATCHES "TERMinator-ssh-go-proxy-fix"`.
- The Linux project links `native/cryptlib/linux-x86_64/libcl.a` from `native/CMakeLists.txt`.
- The shared patch source is in the sibling `TERMinator` repo at `3rdp/src/cl/session/ssh2_id.c`, and the rebuild patch is `3rdp/build/cl-allow-ssh-2.0-go.patch`.

## What needs to be rebuilt

If `native/cryptlib/linux-x86_64/libcl.a` is refreshed, rebuild the Linux native library so the app consumes it:

- `native/build/libsyncterm-native.so`
- `src/TERMinator.Linux/bin/x64/Debug/net8.0/libsyncterm-native.so`
- `src/TERMinator.Linux/bin/x64/Release/net8.0/libsyncterm-native.so`

## Verification

After rebuilding the Linux native library, connect to a BBS that uses a Go SSH proxy. The `SSH-2.0-Go` identifier should no longer be rejected.
