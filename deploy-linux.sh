#!/usr/bin/env bash
# ==============================================================================
# CHATFISH LINUX DEPLOYMENT (Velopack AppImage)
# ==============================================================================
# Mirrors deploy.ps1 Part 1 (Windows Velopack installer), for Linux desktop.
#
# Usage:
#   ./deploy-linux.sh                  # publish, pack, upload
#   ./deploy-linux.sh --skip-upload    # publish + pack only (local test)
#   ./deploy-linux.sh --version 1.1.3
#   ./deploy-linux.sh --skip-download  # do not fetch previous releases (no deltas)
#
# Prerequisites:
#   - .NET 10 SDK
#   - vpk (dotnet tool install -g vpk)
#   - System libs for the app: GTK4, libadwaita, WebKitGTK 6 (runtime on user machines)
#   - scp/ssh access to the server (unless --skip-upload)
# ==============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

SERVER_IP="${SERVER_IP:-bg5.local}"
SSH_USER="${SSH_USER:-daniel}"
OUTPUT_DIR="${OUTPUT_DIR:-./linux_publish}"
RELEASES_DIR="${RELEASES_DIR:-./linux_releases}"
VERSION="${VERSION:-0.0.1}"
UPDATE_FEED="${UPDATE_FEED:-https://chatfish.me/releases/linux}"
REMOTE_RELEASES="${REMOTE_RELEASES:-/var/www/chatfish/releases/linux}"
# Velopack packId drives the AppImage / nupkg / Setup.exe filenames — keep it simple.
PACK_ID="Chatfish"
PACK_TITLE="Chatfish"
MAIN_EXE="Chatfish"
ICON_PATH="ChatfishApp.Maui/Resources/AppIcon/chatfish.png"
# Fallback icons if the primary is missing
ICON_FALLBACKS=(
  "wwwroot/images/icon512.png"
  "wwwroot/images/chatfish.png"
)

SKIP_UPLOAD=false
SKIP_DOWNLOAD=false

usage() {
  sed -n '2,18p' "$0" | sed 's/^# \?//'
  exit "${1:-0}"
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --skip-upload)   SKIP_UPLOAD=true; shift ;;
    --skip-download) SKIP_DOWNLOAD=true; shift ;;
    --version)       VERSION="${2:?}"; shift 2 ;;
    --version=*)     VERSION="${1#*=}"; shift ;;
    -h|--help)       usage 0 ;;
    *)
      echo "Unknown option: $1" >&2
      usage 1
      ;;
  esac
done

resolve_icon() {
  if [[ -f "$ICON_PATH" ]]; then
    echo "$ICON_PATH"
    return
  fi
  for candidate in "${ICON_FALLBACKS[@]}"; do
    if [[ -f "$candidate" ]]; then
      echo "$candidate"
      return
    fi
  done
  echo ""
}

require_cmd() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "ERROR: required command not found: $1" >&2
    exit 1
  fi
}

echo "================================================================"
echo " CHATFISH LINUX DEPLOY — Velopack AppImage"
echo " Version:  $VERSION"
echo " Feed:     $UPDATE_FEED"
echo "================================================================"

require_cmd dotnet
require_cmd vpk

ICON="$(resolve_icon)"
if [[ -z "$ICON" ]]; then
  echo "ERROR: no icon found (tried $ICON_PATH and fallbacks)" >&2
  exit 1
fi
echo "Icon:     $ICON"

# Clean previous builds
echo ""
echo "Cleaning $OUTPUT_DIR and $RELEASES_DIR ..."
rm -rf "$OUTPUT_DIR" "$RELEASES_DIR"
mkdir -p "$RELEASES_DIR"

# Download previous releases for delta updates (optional; first release has none)
if [[ "$SKIP_DOWNLOAD" == "true" ]]; then
  echo "Skipping previous-release download (--skip-download)."
else
  echo ""
  echo "Downloading previous releases from $UPDATE_FEED ..."
  if ! vpk download http --url "$UPDATE_FEED" --outputDir "$RELEASES_DIR" --channel linux; then
    echo "WARNING: could not download previous releases (empty feed or network)."
    echo "         Continuing without deltas."
  fi
fi

# Publish self-contained Linux build (plain net10.0 / LINUX_DESKTOP)
echo ""
echo "Publishing ChatfishApp.Maui (net10.0 / linux-x64 self-contained) ..."
dotnet publish "ChatfishApp.Maui/ChatfishApp.Maui.csproj" \
  -c Release \
  -f net10.0 \
  -r linux-x64 \
  --self-contained true \
  -o "$OUTPUT_DIR" \
  -p:ApplicationDisplayVersion="$VERSION" \
  -p:Version="$VERSION" \
  -p:DebugType=None \
  -p:DebugSymbols=false

if [[ ! -x "$OUTPUT_DIR/$MAIN_EXE" && ! -f "$OUTPUT_DIR/$MAIN_EXE" ]]; then
  echo "ERROR: expected main executable not found: $OUTPUT_DIR/$MAIN_EXE" >&2
  ls -la "$OUTPUT_DIR" | head -40 >&2
  exit 1
fi
chmod +x "$OUTPUT_DIR/$MAIN_EXE" || true

# Pack into AppImage + nupkg + releases.linux.json
echo ""
echo "Packing Velopack AppImage ..."
vpk pack \
  --packId "$PACK_ID" \
  --packVersion "$VERSION" \
  --packDir "$OUTPUT_DIR" \
  --packTitle "$PACK_TITLE" \
  --packAuthors "Chatfish" \
  --mainExe "$MAIN_EXE" \
  --icon "$ICON" \
  --outputDir "$RELEASES_DIR" \
  --channel linux \
  --runtime linux-x64 \
  --categories "Network;Office;Chat;"

echo ""
echo "Release artifacts:"
ls -lah "$RELEASES_DIR"

APPIMAGE="$(find "$RELEASES_DIR" -maxdepth 1 -type f -name '*.AppImage' | head -1 || true)"
if [[ -z "$APPIMAGE" ]]; then
  echo "ERROR: no .AppImage produced in $RELEASES_DIR" >&2
  exit 1
fi
echo ""
echo "AppImage: $APPIMAGE"

if [[ "$SKIP_UPLOAD" == "true" ]]; then
  echo ""
  echo "Skipping upload (--skip-upload)."
  echo "Done. Local packages are in: $RELEASES_DIR"
  echo "  Feed URL for UpdateManager: $UPDATE_FEED"
  echo "  Manual install: chmod +x \"$APPIMAGE\" && \"$APPIMAGE\""
  exit 0
fi

require_cmd scp
require_cmd ssh

echo ""
echo "Ensuring remote directory $REMOTE_RELEASES ..."
ssh "${SSH_USER}@${SERVER_IP}" "mkdir -p '${REMOTE_RELEASES}'"

echo "Uploading to ${SSH_USER}@${SERVER_IP}:${REMOTE_RELEASES}/ ..."
scp -r "${RELEASES_DIR}/"* "${SSH_USER}@${SERVER_IP}:${REMOTE_RELEASES}/"

echo ""
echo "Linux deployment complete."
echo "  Updates feed:  $UPDATE_FEED"
echo "  AppImage:      $UPDATE_FEED/$(basename "$APPIMAGE")"
echo "  Feed index:    $UPDATE_FEED/releases.linux.json"
echo "  Users: chmod +x Chatfish.AppImage && ./Chatfish.AppImage"
