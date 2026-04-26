#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# Pobiera binarki ffmpeg + mediamtx dla wszystkich obsługiwanych platform
# i umieszcza je w runtime/binaries/{rid}/.
#
# SafeView używa tych binarek zamiast wymagać instalacji w systemie.
# Platform identifier (rid) zgodny z .NET Runtime Identifier:
#   - osx-arm64    Apple Silicon (M1/M2/M3/M4)
#   - linux-x64    Linux amd64
#   - win-x64      Windows 64-bit
#
# Binarki NIE są commitowane do gita — uruchom ten skrypt po klonie repo.
# Aby pobrać tylko dla jednej platformy: ./download-binaries.sh osx-arm64
# ─────────────────────────────────────────────────────────────────────────────

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BIN_ROOT="$REPO_ROOT/runtime/binaries"
WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT

# Latest MediaMTX release — query GitHub API
MTX_VERSION="$(curl -s https://api.github.com/repos/bluenviron/mediamtx/releases/latest | grep '"tag_name"' | head -1 | cut -d'"' -f4)"
echo "→ MediaMTX version: $MTX_VERSION"

PLATFORMS=("${@:-osx-arm64 linux-x64 win-x64}")
if [ "$#" -eq 0 ]; then
    PLATFORMS=(osx-arm64 linux-x64 win-x64)
fi

for rid in "${PLATFORMS[@]}"; do
    echo ""
    echo "════════════════════════════════════════════"
    echo "  Platform: $rid"
    echo "════════════════════════════════════════════"
    mkdir -p "$BIN_ROOT/$rid"

    case "$rid" in
        osx-arm64)
            MTX_FILE="mediamtx_${MTX_VERSION}_darwin_arm64.tar.gz"
            MTX_URL="https://github.com/bluenviron/mediamtx/releases/download/$MTX_VERSION/$MTX_FILE"
            FFMPEG_URL="https://evermeet.cx/ffmpeg/get/zip"
            FFMPEG_EXT="zip"
            FFMPEG_BIN="ffmpeg"
            ;;
        linux-x64)
            MTX_FILE="mediamtx_${MTX_VERSION}_linux_amd64.tar.gz"
            MTX_URL="https://github.com/bluenviron/mediamtx/releases/download/$MTX_VERSION/$MTX_FILE"
            FFMPEG_URL="https://johnvansickle.com/ffmpeg/releases/ffmpeg-release-amd64-static.tar.xz"
            FFMPEG_EXT="tar.xz"
            FFMPEG_BIN="ffmpeg"
            ;;
        win-x64)
            MTX_FILE="mediamtx_${MTX_VERSION}_windows_amd64.zip"
            MTX_URL="https://github.com/bluenviron/mediamtx/releases/download/$MTX_VERSION/$MTX_FILE"
            FFMPEG_URL="https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip"
            FFMPEG_EXT="zip"
            FFMPEG_BIN="ffmpeg.exe"
            ;;
        *)
            echo "  ✗ Nieznana platforma: $rid (pomijam)"
            continue
            ;;
    esac

    # ── MediaMTX ───────────────────────────────────────────
    echo "  ↓ MediaMTX ($MTX_URL)"
    curl -fL --progress-bar "$MTX_URL" -o "$WORK_DIR/mtx.$rid.archive"
    if [[ "$MTX_FILE" == *.zip ]]; then
        unzip -qo "$WORK_DIR/mtx.$rid.archive" -d "$WORK_DIR/mtx-$rid"
    else
        mkdir -p "$WORK_DIR/mtx-$rid"
        tar -xzf "$WORK_DIR/mtx.$rid.archive" -C "$WORK_DIR/mtx-$rid"
    fi
    if [[ "$rid" == "win-x64" ]]; then
        cp "$WORK_DIR/mtx-$rid/mediamtx.exe" "$BIN_ROOT/$rid/mediamtx.exe"
    else
        cp "$WORK_DIR/mtx-$rid/mediamtx" "$BIN_ROOT/$rid/mediamtx"
        chmod +x "$BIN_ROOT/$rid/mediamtx"
    fi
    echo "  ✓ MediaMTX → $BIN_ROOT/$rid/"

    # ── FFmpeg ─────────────────────────────────────────────
    echo "  ↓ FFmpeg ($FFMPEG_URL)"
    curl -fL --progress-bar "$FFMPEG_URL" -o "$WORK_DIR/ff.$rid.archive"
    case "$FFMPEG_EXT" in
        zip)
            unzip -qo "$WORK_DIR/ff.$rid.archive" -d "$WORK_DIR/ff-$rid"
            # evermeet.cx zip has ffmpeg in root; gyan.dev zip has it in bin/ subdir
            FF_FOUND="$(find "$WORK_DIR/ff-$rid" -name "$FFMPEG_BIN" -type f | head -1)"
            ;;
        tar.xz)
            mkdir -p "$WORK_DIR/ff-$rid"
            tar -xJf "$WORK_DIR/ff.$rid.archive" -C "$WORK_DIR/ff-$rid"
            FF_FOUND="$(find "$WORK_DIR/ff-$rid" -name "$FFMPEG_BIN" -type f | head -1)"
            ;;
    esac
    if [ -z "${FF_FOUND:-}" ] || [ ! -f "$FF_FOUND" ]; then
        echo "  ✗ Nie znaleziono binarki ffmpeg w archiwum"
        continue
    fi
    cp "$FF_FOUND" "$BIN_ROOT/$rid/$FFMPEG_BIN"
    if [[ "$rid" != "win-x64" ]]; then
        chmod +x "$BIN_ROOT/$rid/$FFMPEG_BIN"
    fi
    echo "  ✓ FFmpeg → $BIN_ROOT/$rid/"
done

echo ""
echo "════════════════════════════════════════════"
echo "  Gotowe. Zawartość $BIN_ROOT:"
echo "════════════════════════════════════════════"
ls -la "$BIN_ROOT"/*/
