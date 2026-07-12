#!/usr/bin/env bash
# ==============================================================================
# CHATFISH LINUX DEPLOYMENT (Velopack AppImage + .deb + install.sh)
# ==============================================================================
# Mirrors deploy.ps1 Part 1 (Windows Velopack installer), for Linux desktop.
#
# Usage:
#   ./deploy-linux.sh                  # publish, pack, upload
#   ./deploy-linux.sh --skip-upload    # publish + pack only (local test)
#   ./deploy-linux.sh --version 1.1.3
#   ./deploy-linux.sh --skip-download  # do not fetch previous releases (no deltas)
#
# Artifacts (uploaded to https://chatfish.me/releases/linux/):
#   Chatfish.AppImage
#   chatfish_${VERSION}_amd64.deb
#   install.sh          → also served at https://chatfish.me/install.sh
#                         default: user AppImage (~/Applications) for Velopack self-update
#                         --system: .deb under /opt (upgrade via reinstall)
#   releases.linux.json (Velopack feed)
#
# Prerequisites:
#   - .NET 10 SDK, vpk, dpkg-deb
#   - scp/ssh (unless --skip-upload)
# ==============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

SERVER_IP="${SERVER_IP:-bg5.local}"
SSH_USER="${SSH_USER:-daniel}"
OUTPUT_DIR="${OUTPUT_DIR:-./linux_publish}"
RELEASES_DIR="${RELEASES_DIR:-./linux_releases}"
DEB_BUILD_DIR="${DEB_BUILD_DIR:-./linux_deb_build}"
VERSION="${VERSION:-0.0.5}"
UPDATE_FEED="${UPDATE_FEED:-https://chatfish.me/releases/linux}"
REMOTE_RELEASES="${REMOTE_RELEASES:-/var/www/chatfish/releases/linux}"
REMOTE_WWWROOT="${REMOTE_WWWROOT:-/var/www/chatfish}"
PACK_ID="Chatfish"
PACK_TITLE="Chatfish"
MAIN_EXE="Chatfish"
ICON_PATH="ChatfishApp.Maui/Resources/AppIcon/chatfish.png"
ICON_FALLBACKS=(
  "wwwroot/images/icon512.png"
  "wwwroot/images/chatfish.png"
)

SKIP_UPLOAD=false
SKIP_DOWNLOAD=false

usage() {
  sed -n '2,22p' "$0" | sed 's/^# \?//'
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
echo " CHATFISH LINUX DEPLOY — AppImage + .deb + install.sh"
echo " Version:  $VERSION"
echo " Feed:     $UPDATE_FEED"
echo "================================================================"

require_cmd dotnet
require_cmd vpk
require_cmd dpkg-deb

ICON="$(resolve_icon)"
if [[ -z "$ICON" ]]; then
  echo "ERROR: no icon found (tried $ICON_PATH and fallbacks)" >&2
  exit 1
fi
echo "Icon:     $ICON"

# Clean previous builds (keep downloaded prior releases out of DEB dir)
echo ""
echo "Cleaning $OUTPUT_DIR, $RELEASES_DIR, $DEB_BUILD_DIR ..."
rm -rf "$OUTPUT_DIR" "$RELEASES_DIR" "$DEB_BUILD_DIR"
mkdir -p "$RELEASES_DIR"

# Download previous releases for delta updates
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

# Publish self-contained Linux build
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

# Pack Velopack AppImage + nupkg + releases.linux.json
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

APPIMAGE="$(find "$RELEASES_DIR" -maxdepth 1 -type f -name '*.AppImage' | head -1 || true)"
if [[ -z "$APPIMAGE" ]]; then
  echo "ERROR: no .AppImage produced in $RELEASES_DIR" >&2
  exit 1
fi
APPIMAGE_NAME="$(basename "$APPIMAGE")"
# Normalize to Chatfish.AppImage for stable install URLs
if [[ "$APPIMAGE_NAME" != "Chatfish.AppImage" ]]; then
  cp -f "$APPIMAGE" "$RELEASES_DIR/Chatfish.AppImage"
  chmod +x "$RELEASES_DIR/Chatfish.AppImage"
  APPIMAGE_NAME="Chatfish.AppImage"
  APPIMAGE="$RELEASES_DIR/Chatfish.AppImage"
fi
echo "AppImage: $APPIMAGE"

# ------------------------------------------------------------------------------
# .deb package (wraps AppImage under /opt/chatfish)
# ------------------------------------------------------------------------------
DEB_NAME="chatfish_${VERSION}_amd64.deb"
echo ""
echo "Building .deb installer ($DEB_NAME) ..."
rm -rf "$DEB_BUILD_DIR"
mkdir -p \
  "$DEB_BUILD_DIR/DEBIAN" \
  "$DEB_BUILD_DIR/opt/chatfish" \
  "$DEB_BUILD_DIR/usr/bin" \
  "$DEB_BUILD_DIR/usr/share/applications" \
  "$DEB_BUILD_DIR/usr/share/icons/hicolor/256x256/apps" \
  "$DEB_BUILD_DIR/usr/share/icons/hicolor/512x512/apps"

cp "$APPIMAGE" "$DEB_BUILD_DIR/opt/chatfish/Chatfish.AppImage"
chmod 755 "$DEB_BUILD_DIR/opt/chatfish/Chatfish.AppImage"

# PATH helper
cat > "$DEB_BUILD_DIR/usr/bin/chatfish" << 'WRAPPER'
#!/bin/sh
exec /opt/chatfish/Chatfish.AppImage "$@"
WRAPPER
chmod 755 "$DEB_BUILD_DIR/usr/bin/chatfish"

INSTALLED_SIZE_KB="$(du -sk "$DEB_BUILD_DIR/opt" | awk '{print $1}')"

cat > "$DEB_BUILD_DIR/DEBIAN/control" << EOF
Package: chatfish
Version: $VERSION
Section: net
Priority: optional
Architecture: amd64
Installed-Size: $INSTALLED_SIZE_KB
Maintainer: Daniel Goodwin <daniellgoodwin@protonmail.com>
Homepage: https://chatfish.me
Description: Privacy-first local AI chat application
 Chatfish is a privacy-first AI chat hub with local-first storage,
 Ollama support, and optional multi-device sync.
EOF

cat > "$DEB_BUILD_DIR/DEBIAN/postinst" << 'EOF'
#!/bin/sh
set -e
chmod 755 /opt/chatfish/Chatfish.AppImage /usr/bin/chatfish 2>/dev/null || true
if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database -q /usr/share/applications 2>/dev/null || true
fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then
  gtk-update-icon-cache -f -t /usr/share/icons/hicolor 2>/dev/null || true
fi
exit 0
EOF
chmod 755 "$DEB_BUILD_DIR/DEBIAN/postinst"

cat > "$DEB_BUILD_DIR/usr/share/applications/chatfish.desktop" << EOF
[Desktop Entry]
Version=1.0
Type=Application
Name=Chatfish
Comment=Privacy-first AI chat hub
Exec=/opt/chatfish/Chatfish.AppImage
Icon=chatfish
Terminal=false
Categories=Network;Office;Chat;Utility;
StartupWMClass=com.chatfish.app
StartupNotify=true
EOF

# Icon (512 source → 256 and 512 hicolor slots; desktops scale as needed)
cp "$ICON" "$DEB_BUILD_DIR/usr/share/icons/hicolor/512x512/apps/chatfish.png"
cp "$ICON" "$DEB_BUILD_DIR/usr/share/icons/hicolor/256x256/apps/chatfish.png"

dpkg-deb --root-owner-group --build "$DEB_BUILD_DIR" "$RELEASES_DIR/$DEB_NAME"
echo "Deb: $RELEASES_DIR/$DEB_NAME"

# ------------------------------------------------------------------------------
# install.sh (curl | bash)
#
# Default: user-local AppImage under ~/Applications (Velopack can self-update).
# Optional: --system installs the .deb under /opt (root-owned; use apt/dpkg or
# re-run install.sh --system to upgrade — in-app Velopack replace usually fails).
# ------------------------------------------------------------------------------
echo ""
echo "Writing install.sh ..."
cat > "$RELEASES_DIR/install.sh" << EOF
#!/usr/bin/env bash
# Chatfish Linux installer
#   curl -fsSL https://chatfish.me/install.sh | bash              # AppImage (recommended)
#   curl -fsSL https://chatfish.me/install.sh | bash -s -- --system  # .deb under /opt
set -euo pipefail

VERSION="${VERSION}"
BASE_URL="${UPDATE_FEED}"
INSTALL_DIR="\${CHATFISH_INSTALL_DIR:-\${HOME}/Applications}"
APPIMAGE="Chatfish.AppImage"
DEB="chatfish_\${VERSION}_amd64.deb"
MODE="appimage"

usage() {
  cat <<USAGE
Chatfish Linux installer

Usage:
  curl -fsSL https://chatfish.me/install.sh | bash
  curl -fsSL https://chatfish.me/install.sh | bash -s -- [options]

Options:
  --appimage, --user   Install AppImage to ~/Applications (default; supports in-app updates)
  --system, --deb      Install system-wide .deb to /opt/chatfish (updates via reinstall)
  -h, --help           Show this help
USAGE
}

while [[ \$# -gt 0 ]]; do
  case "\$1" in
    --appimage|--user) MODE="appimage"; shift ;;
    --system|--deb)    MODE="system"; shift ;;
    -h|--help)         usage; exit 0 ;;
    *)
      echo "Unknown option: \$1" >&2
      usage >&2
      exit 1
      ;;
  esac
done

echo "Installing Chatfish \${VERSION} (mode: \$MODE)..."

TMP="\$(mktemp -d)"
cleanup() { rm -rf "\$TMP"; }
trap cleanup EXIT
cd "\$TMP"

install_deb_file() {
  echo "Downloading .deb from \$BASE_URL/\$DEB ..."
  if ! curl -fL --progress-bar -o "\$DEB" "\$BASE_URL/\$DEB"; then
    echo "ERROR: could not download \$BASE_URL/\$DEB" >&2
    exit 1
  fi
  echo "Installing with dpkg (may prompt for sudo)..."
  if command -v sudo >/dev/null 2>&1; then
    sudo dpkg -i "./\$DEB" || sudo apt-get install -f -y
  else
    dpkg -i "./\$DEB" || apt-get install -f -y
  fi
  echo ""
  echo "Chatfish installed system-wide under /opt/chatfish."
  echo "  Launch from your app menu, or run: chatfish"
  echo ""
  echo "NOTE: System installs are root-owned. In-app updates usually cannot replace"
  echo "      /opt/chatfish/Chatfish.AppImage. To upgrade later either:"
  echo "        curl -fsSL https://chatfish.me/install.sh | bash -s -- --system"
  echo "      or use the user AppImage install (recommended for auto-update):"
  echo "        curl -fsSL https://chatfish.me/install.sh | bash"
}

install_appimage() {
  mkdir -p "\$INSTALL_DIR"
  echo "Downloading AppImage from \$BASE_URL/\$APPIMAGE ..."
  # Do not use HEAD — release endpoints historically returned 405 for HEAD.
  if ! curl -fL --progress-bar -o "\$INSTALL_DIR/\$APPIMAGE" "\$BASE_URL/\$APPIMAGE"; then
    echo "ERROR: could not download \$BASE_URL/\$APPIMAGE" >&2
    exit 1
  fi
  chmod +x "\$INSTALL_DIR/\$APPIMAGE"

  mkdir -p "\${HOME}/.local/share/applications"
  cat > "\${HOME}/.local/share/applications/chatfish.desktop" << DESKTOP
[Desktop Entry]
Version=1.0
Type=Application
Name=Chatfish
Comment=Privacy-first AI chat hub
Exec=\$INSTALL_DIR/\$APPIMAGE
Icon=chatfish
Terminal=false
Categories=Network;Office;Chat;Utility;
StartupNotify=true
StartupWMClass=com.chatfish.app
DESKTOP

  if command -v curl >/dev/null 2>&1; then
    ICON_DIR="\${HOME}/.local/share/icons/hicolor/256x256/apps"
    mkdir -p "\$ICON_DIR"
    curl -fsSL -o "\$ICON_DIR/chatfish.png" "https://chatfish.me/images/icon512.png" 2>/dev/null \\
      || curl -fsSL -o "\$ICON_DIR/chatfish.png" "https://chatfish.me/images/chatfish.png" 2>/dev/null \\
      || true
  fi

  if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database "\${HOME}/.local/share/applications" 2>/dev/null || true
  fi

  # Prefer user desktop entry over a leftover system .deb entry in menus.
  if [[ -f /usr/share/applications/chatfish.desktop ]]; then
    echo ""
    echo "NOTE: A system install was also detected (/usr/share/applications/chatfish.desktop)."
    echo "      Prefer launching: \$INSTALL_DIR/\$APPIMAGE"
    echo "      or remove the .deb if you only want the user AppImage:"
    echo "        sudo apt remove chatfish   # or: sudo dpkg -r chatfish"
  fi

  echo ""
  echo "Chatfish AppImage installed (user-local — supports in-app updates)."
  echo "  Path:   \$INSTALL_DIR/\$APPIMAGE"
  echo "  Launch: from your app menu, or: \$INSTALL_DIR/\$APPIMAGE"
}

case "\$MODE" in
  system)
    if ! command -v dpkg >/dev/null 2>&1; then
      echo "ERROR: --system requires dpkg (Debian/Ubuntu). Use default AppImage install instead." >&2
      exit 1
    fi
    install_deb_file
    ;;
  *)
    install_appimage
    ;;
esac

echo "Done."
EOF
chmod +x "$RELEASES_DIR/install.sh"

# Also copy into repo wwwroot for non-volume static deploys (optional convenience)
mkdir -p wwwroot/releases/linux
cp -f "$RELEASES_DIR/install.sh" wwwroot/releases/linux/install.sh 2>/dev/null || true
# Root install.sh for static hosts that map wwwroot
cp -f "$RELEASES_DIR/install.sh" wwwroot/install.sh 2>/dev/null || true

echo ""
echo "Release artifacts:"
ls -lah "$RELEASES_DIR"

if [[ "$SKIP_UPLOAD" == "true" ]]; then
  echo ""
  echo "Skipping upload (--skip-upload)."
  echo "Done. Local packages are in: $RELEASES_DIR"
  echo "  curl -fsSL file://$SCRIPT_DIR/$RELEASES_DIR/install.sh | bash   # not for remote"
  echo "  Install command (after upload): curl -fsSL https://chatfish.me/install.sh | bash"
  exit 0
fi

require_cmd scp
require_cmd ssh

echo ""
echo "Ensuring remote directory $REMOTE_RELEASES ..."
ssh "${SSH_USER}@${SERVER_IP}" "mkdir -p '${REMOTE_RELEASES}'"

echo "Uploading release artifacts to ${SSH_USER}@${SERVER_IP}:${REMOTE_RELEASES}/ ..."
scp -r "${RELEASES_DIR}/"* "${SSH_USER}@${SERVER_IP}:${REMOTE_RELEASES}/"

# Convenience: install.sh at site root path used by curl | bash (volume or static tree)
echo "Publishing install.sh to site root (${REMOTE_WWWROOT}/install.sh) ..."
scp "$RELEASES_DIR/install.sh" "${SSH_USER}@${SERVER_IP}:${REMOTE_WWWROOT}/install.sh" \
  || scp "$RELEASES_DIR/install.sh" "${SSH_USER}@${SERVER_IP}:${REMOTE_WWWROOT}/wwwroot/install.sh" \
  || echo "WARNING: could not copy install.sh to site root — use $UPDATE_FEED/install.sh"

echo ""
echo "Linux deployment complete."
echo "  Install (AppImage, in-app updates): curl -fsSL https://chatfish.me/install.sh | bash"
echo "  Install (system .deb):              curl -fsSL https://chatfish.me/install.sh | bash -s -- --system"
echo "  (alt)       curl -fsSL $UPDATE_FEED/install.sh | bash"
echo "  AppImage:   $UPDATE_FEED/$APPIMAGE_NAME"
echo "  Deb:        $UPDATE_FEED/$DEB_NAME"
echo "  Feed:       $UPDATE_FEED/releases.linux.json"
