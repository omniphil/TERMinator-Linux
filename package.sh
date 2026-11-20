#!/usr/bin/env bash
# Self-contained linux-x64 publish + tarball for end-user distribution.
# Output: dist/publish/ (raw) and dist/terminator-linux-x64.tar.gz (shippable).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJ="$ROOT/src/TERMinator.Linux/TERMinator.Linux.csproj"
PUBLISH_DIR="$ROOT/dist/publish"
TARBALL="$ROOT/dist/terminator-linux-x64.tar.gz"

# Pick a working libsyncterm-native.so: must export audio_bridge_init AND have no
# unresolved telnets_* symbols. native/build/libsyncterm-native.so is currently
# missing telnets linkage; only the Debug-output copy is known-good.
find_working_so() {
    local so
    for so in \
        "$ROOT/native/build/Release/libsyncterm-native.so" \
        "$ROOT/native/build/libsyncterm-native.so" \
        "$ROOT/src/TERMinator.Linux/bin/x64/Debug/net8.0/libsyncterm-native.so" \
        "$ROOT/src/TERMinator.Linux/bin/x64/Release/net8.0/libsyncterm-native.so"
    do
        [[ -f "$so" ]] || continue
        if ! nm -D --defined-only "$so" 2>/dev/null | grep -q audio_bridge_init; then
            continue
        fi
        if nm -D --undefined-only "$so" 2>/dev/null | grep -qE 'telnets_(close|connect)'; then
            continue
        fi
        echo "$so"
        return 0
    done
    return 1
}

NATIVE_SO="${NATIVE_SO:-$(find_working_so || true)}"
if [[ -z "$NATIVE_SO" ]]; then
    echo "ERROR: no working libsyncterm-native.so found." >&2
    echo "Set NATIVE_SO=/path/to/libsyncterm-native.so or rebuild native/." >&2
    exit 1
fi
echo "==> Using native lib: $NATIVE_SO"

# csproj globs native/build/Release/libsyncterm-native.so — stage it there.
mkdir -p "$ROOT/native/build/Release"
cp -f "$NATIVE_SO" "$ROOT/native/build/Release/libsyncterm-native.so"

echo "==> dotnet publish (self-contained, linux-x64)"
rm -rf "$PUBLISH_DIR"
dotnet publish "$PROJ" \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    -p:DebugType=embedded \
    -o "$PUBLISH_DIR"

if [[ ! -f "$PUBLISH_DIR/libsyncterm-native.so" ]]; then
    cp -f "$NATIVE_SO" "$PUBLISH_DIR/libsyncterm-native.so"
fi
nm -D --defined-only "$PUBLISH_DIR/libsyncterm-native.so" | grep -q audio_bridge_init \
    || { echo "ERROR: publish output .so missing audio_bridge_init" >&2; exit 1; }
chmod +x "$PUBLISH_DIR/TERMinator"

echo "==> Packing tarball"
mkdir -p "$(dirname "$TARBALL")"
tar -C "$ROOT/dist" --transform 's|^publish|terminator|' -czf "$TARBALL" publish

SIZE=$(du -h "$TARBALL" | cut -f1)
cat <<EOF

Done.
  Folder:  $PUBLISH_DIR
  Tarball: $TARBALL ($SIZE)

End-user instructions:
  tar -xzf $(basename "$TARBALL")
  cd terminator
  ./TERMinator

Runtime prerequisite (LibVLCSharp binding needs libvlc on host):
  Debian/Ubuntu:  sudo apt install vlc
  Fedora:         sudo dnf install vlc
EOF
