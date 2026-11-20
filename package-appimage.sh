#!/usr/bin/env bash
# Build a portable TERMinator-x86_64.AppImage that bundles .NET runtime + libvlc.
# Requires: dotnet sdk, vlc installed locally (to vendor libs+plugins from), curl.
# Output: dist/TERMinator-x86_64.AppImage
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJ="$ROOT/src/TERMinator.Linux/TERMinator.Linux.csproj"
DIST="$ROOT/dist"
APPDIR="$DIST/TERMinator.AppDir"
APPIMAGE="$DIST/${APPIMAGE_NAME:-TERMinator-x86_64.AppImage}"
ICON_SRC="$ROOT/src/TERMinator.Linux/Assets/header_logo.png"
CACHE="${XDG_CACHE_HOME:-$HOME/.cache}/terminator-appimage"
APPIMAGETOOL_URL="https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage"

# --- pick a working libsyncterm-native.so (same checks as package.sh) ---
find_working_so() {
    local so
    for so in \
        "$ROOT/native/build/Release/libsyncterm-native.so" \
        "$ROOT/native/build/libsyncterm-native.so" \
        "$ROOT/src/TERMinator.Linux/bin/x64/Debug/net8.0/libsyncterm-native.so" \
        "$ROOT/src/TERMinator.Linux/bin/x64/Release/net8.0/libsyncterm-native.so"
    do
        [[ -f "$so" ]] || continue
        # NB: capture first -- "nm | grep -q" trips pipefail via SIGPIPE on nm.
        local defined undefined
        defined="$(nm -D --defined-only "$so" 2>/dev/null || true)"
        undefined="$(nm -D --undefined-only "$so" 2>/dev/null || true)"
        grep -q audio_bridge_init <<<"$defined" || continue
        grep -qE 'telnets_(close|connect)' <<<"$undefined" && continue
        echo "$so"
        return 0
    done
    return 1
}

NATIVE_SO="${NATIVE_SO:-$(find_working_so || true)}"
[[ -n "$NATIVE_SO" ]] || { echo "ERROR: no working libsyncterm-native.so. Set NATIVE_SO=..." >&2; exit 1; }
echo "==> Native lib: $NATIVE_SO"

# --- locate libvlc + plugins on the host (or env-overridden) ---
VLC_LIB_DIR="${VLC_LIB_DIR:-}"
VLC_PLUGIN_DIR="${VLC_PLUGIN_DIR:-}"
if [[ -z "$VLC_LIB_DIR" ]]; then
    for d in /usr/lib/x86_64-linux-gnu /usr/lib64 /usr/lib; do
        if compgen -G "$d/libvlc.so*" >/dev/null; then VLC_LIB_DIR="$d"; break; fi
    done
fi
if [[ -z "$VLC_PLUGIN_DIR" ]]; then
    for d in /usr/lib/x86_64-linux-gnu/vlc/plugins /usr/lib64/vlc/plugins /usr/lib/vlc/plugins; do
        if [[ -d "$d" ]]; then VLC_PLUGIN_DIR="$d"; break; fi
    done
fi
if [[ -z "$VLC_LIB_DIR" || -z "$VLC_PLUGIN_DIR" ]]; then
    cat >&2 <<EOF
ERROR: libvlc not found.
  Install VLC so its libs + plugins can be vendored into the AppImage:
    sudo apt install vlc libvlc-dev
  Or set VLC_LIB_DIR and VLC_PLUGIN_DIR explicitly.
EOF
    exit 1
fi
echo "==> VLC libs:    $VLC_LIB_DIR"
echo "==> VLC plugins: $VLC_PLUGIN_DIR"

# --- acquire appimagetool ---
mkdir -p "$CACHE"
if command -v appimagetool >/dev/null 2>&1; then
    APPIMAGETOOL="$(command -v appimagetool)"
else
    APPIMAGETOOL="$CACHE/appimagetool-x86_64.AppImage"
    if [[ ! -x "$APPIMAGETOOL" ]]; then
        echo "==> Downloading appimagetool"
        curl -fsSL -o "$APPIMAGETOOL" "$APPIMAGETOOL_URL"
        chmod +x "$APPIMAGETOOL"
    fi
fi
echo "==> appimagetool: $APPIMAGETOOL"

# --- stage native .so where csproj expects it ---
mkdir -p "$ROOT/native/build/Release"
STAGED_SO="$ROOT/native/build/Release/libsyncterm-native.so"
[[ "$NATIVE_SO" -ef "$STAGED_SO" ]] || cp -f "$NATIVE_SO" "$STAGED_SO"

# --- publish into AppDir/usr/bin ---
echo "==> dotnet publish (self-contained, linux-x64)"
# On the Dropbox/drvfs mount a Windows-side handle can transiently block rmdir.
rm_retry() {
    local i
    for i in 1 2 3 4 5 6 7 8; do
        rm -rf "$1" 2>/dev/null || true
        [[ -e "$1" ]] || return 0
        if [[ $i -eq 1 ]]; then echo "    (removal blocked, retrying)" >&2; fi
        sleep 3
    done
    echo "ERROR: could not remove $1 (file held open on the Windows side?)" >&2
    return 1
}
rm_retry "$APPDIR"
mkdir -p "$APPDIR/usr/bin" "$APPDIR/usr/lib/vlc/plugins" \
         "$APPDIR/usr/share/applications" "$APPDIR/usr/share/icons/hicolor/256x256/apps"
dotnet publish "$PROJ" \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    -p:DebugType=embedded \
    -o "$APPDIR/usr/bin"
chmod +x "$APPDIR/usr/bin/TERMinator"

# --- TRACE sandbox host (gamesandbox + libwasmtime.so, same folder) ---
# The csproj copies them from TERMinator-Windows/trace/bin-linux-x86_64/ (built by trace/build_gamesandbox.sh) into
# usr/bin/trace/, where Services/TraceModules.HostPath looks. Copied again here so TRACE_HOST_DIR can point at another
# build, and so the host keeps its exec bit. Missing = a warning: the AppImage then answers doors' TRACE queries
# with wasm=0 and they fall back to their own graphics.
TRACE_HOST_DIR="${TRACE_HOST_DIR:-$ROOT/../TERMinator-Windows/trace/bin-linux-x86_64}"
if [[ -f "$TRACE_HOST_DIR/gamesandbox" && -f "$TRACE_HOST_DIR/libwasmtime.so" ]]; then
    echo "==> TRACE host: $TRACE_HOST_DIR"
    mkdir -p "$APPDIR/usr/bin/trace"
    cp -f "$TRACE_HOST_DIR/gamesandbox" "$TRACE_HOST_DIR/libwasmtime.so" "$APPDIR/usr/bin/trace/"
    chmod +x "$APPDIR/usr/bin/trace/gamesandbox"
    # The GPU library (WebGPU for modules) when this checkout has it: gamesandbox dlopen()s it by name, and the app
    # answers doors gpu=1 only when it is beside the host, so leaving it out just means modules use the processor
    if [[ -f "$TRACE_HOST_DIR/libwgpu_native.so" ]]; then
        cp -f "$TRACE_HOST_DIR/libwgpu_native.so" "$APPDIR/usr/bin/trace/"
    else
        echo "note: no libwgpu_native.so beside the TRACE host -- this AppImage has no GPU for modules"
    fi
else
    echo "WARNING: TRACE host not found in $TRACE_HOST_DIR (set TRACE_HOST_DIR); TRACE graphics will be off." >&2
fi

# --- vendor libvlc + plugins ---
echo "==> Vendoring libvlc"
cp -aP "$VLC_LIB_DIR"/libvlc.so*     "$APPDIR/usr/lib/"
cp -aP "$VLC_LIB_DIR"/libvlccore.so* "$APPDIR/usr/lib/"
cp -a  "$VLC_PLUGIN_DIR"/.           "$APPDIR/usr/lib/vlc/plugins/"

# --- vendor OpenAL Soft (+ its sndio dependency) ---
# libsyncterm-native.so links libopenal.so.1, and not every distro installs it: without it the native library
# fails to load and the app stops at "Missing Native Library". AppRun puts usr/lib on LD_LIBRARY_PATH. OpenAL
# still dlopens the system's PulseAudio / PipeWire at runtime, so it isn't bundled.
echo "==> Vendoring OpenAL"
OPENAL_LIB_DIR="${OPENAL_LIB_DIR:-}"
if [[ -z "$OPENAL_LIB_DIR" ]]; then
    for d in /usr/lib/x86_64-linux-gnu /usr/lib64 /usr/lib; do
        if compgen -G "$d/libopenal.so.1*" >/dev/null; then OPENAL_LIB_DIR="$d"; break; fi
    done
fi
if [[ -n "$OPENAL_LIB_DIR" ]]; then
    cp -aP "$OPENAL_LIB_DIR"/libopenal.so.1* "$APPDIR/usr/lib/"
    cp -aP "$OPENAL_LIB_DIR"/libsndio.so.7* "$APPDIR/usr/lib/" 2>/dev/null || true
else
    echo "WARNING: libopenal.so.1 not found (set OPENAL_LIB_DIR); the AppImage will need it installed on the target." >&2
fi

# --- vendor libsndfile + libjxl (SyncTERM door audio and JPEG XL frames) ---
# native/apc/ dlopen()s them (no link-time dependency): the system's copies first, else these. They go in a PRIVATE
# usr/lib/apc/, not on AppRun's LD_LIBRARY_PATH, so bundled codecs never stand in for the system libraries other
# parts of the process load (OpenAL's PulseAudio backend pulls libsndfile, freetype brotli); apc_dlopen_bundled()
# opens that folder itself. Bundled with every non-glibc / non-libstdc++ library they pull in; missing on the build
# host = a warning and the features rely on the target having them.
echo "==> Vendoring libsndfile + libjxl"
APC_DEST="$APPDIR/usr/lib/apc"
mkdir -p "$APC_DEST"
vendor_with_deps() {
    local lib="$1" dep
    [[ -e "$APC_DEST/$(basename "$lib")" ]] && return
    cp -aP "$lib" "$APC_DEST/"
    [[ -L "$lib" ]] && cp -aP "$(readlink -f "$lib")" "$APC_DEST/"
    while read -r dep; do
        case "$(basename "$dep")" in
            libc.so*|libm.so*|libdl.so*|libpthread.so*|librt.so*|ld-linux*|libstdc++.so*|libgcc_s.so*) continue ;;
            libmvec.so*|libresolv.so*|libutil.so*|libanl.so*|libnsl.so*|libcrypt.so*) continue ;;
        esac
        vendor_with_deps "$dep"
    done < <(ldd "$lib" | awk '$3 ~ /^\// {print $3}')
}
APC_LIB_DIR="${APC_LIB_DIR:-}"
if [[ -z "$APC_LIB_DIR" ]]; then
    for d in /usr/lib/x86_64-linux-gnu /usr/lib64 /usr/lib; do
        if compgen -G "$d/libsndfile.so.1*" >/dev/null; then APC_LIB_DIR="$d"; break; fi
    done
fi
if [[ -n "$APC_LIB_DIR" ]]; then
    vendor_with_deps "$APC_LIB_DIR/libsndfile.so.1"
    for jxl in "$APC_LIB_DIR"/libjxl.so.0.* "$APC_LIB_DIR"/libjxl_threads.so.0.*; do
        [[ -e "$jxl" ]] && vendor_with_deps "$jxl"
    done
    compgen -G "$APC_DEST/libjxl.so.0.*" >/dev/null \
        || echo "WARNING: libjxl not found (apt install libjxl0.7 or newer); JPEG XL frames will be off unless the target has it." >&2
else
    echo "WARNING: libsndfile.so.1 not found (set APC_LIB_DIR); door audio and JPEG XL rely on the target having them." >&2
fi

# --- AppRun: launcher that points the binary at the bundled libs ---
cat > "$APPDIR/AppRun" <<'APPRUN'
#!/bin/bash
HERE="$(dirname "$(readlink -f "$0")")"
export LD_LIBRARY_PATH="$HERE/usr/lib:${LD_LIBRARY_PATH:-}"
export VLC_PLUGIN_PATH="$HERE/usr/lib/vlc/plugins"
cd "$HERE/usr/bin"
exec ./TERMinator "$@"
APPRUN
chmod +x "$APPDIR/AppRun"

# --- desktop entry + icon (top-level + xdg mirrors; appimagetool checks both) ---
cat > "$APPDIR/terminator.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=TERMinator
Comment=BBS terminal emulator
Exec=TERMinator
Icon=terminator
Categories=Network;TerminalEmulator;
Terminal=false
EOF
cp "$APPDIR/terminator.desktop" "$APPDIR/usr/share/applications/terminator.desktop"
cp "$ICON_SRC" "$APPDIR/terminator.png"
cp "$ICON_SRC" "$APPDIR/usr/share/icons/hicolor/256x256/apps/terminator.png"

# --- build the AppImage (FUSE-less mode for WSL/containers) ---
echo "==> Building AppImage"
rm -f "$APPIMAGE"
APPIMAGE_EXTRACT_AND_RUN=1 ARCH=x86_64 "$APPIMAGETOOL" "$APPDIR" "$APPIMAGE"

SIZE=$(du -h "$APPIMAGE" | cut -f1)
cat <<EOF

Done.
  AppDir:   $APPDIR
  AppImage: $APPIMAGE ($SIZE)

End-user instructions (any modern x86_64 Linux):
  chmod +x $(basename "$APPIMAGE")
  ./$(basename "$APPIMAGE")
EOF
