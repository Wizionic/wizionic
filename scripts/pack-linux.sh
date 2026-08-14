#!/usr/bin/env bash
# ==============================================================================
# Pack Linux desktop + homeserver artifacts (no production upload).
# Used by .github/workflows/release.yml and for local testing.
#
# Usage (from repo root):
#   ./scripts/pack-linux.sh --version 0.2.0
#   ./scripts/pack-linux.sh --version 0.2.0 --skip-download
#   ./scripts/pack-linux.sh --version 0.2.0 --skip-homeserver
# ==============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$REPO_ROOT"

VERSION="${VERSION:-0.2.0}"
OUTPUT_DIR="${OUTPUT_DIR:-./linux_publish}"
RELEASES_DIR="${RELEASES_DIR:-./linux_releases}"
DEB_BUILD_DIR="${DEB_BUILD_DIR:-./linux_deb_build}"
HOMESERVER_OUTPUT="${HOMESERVER_OUTPUT:-./homeserver_publish_linux}"
HOMESERVER_RELEASES="${HOMESERVER_RELEASES:-./homeserver_releases_linux}"
UPDATE_FEED="${UPDATE_FEED:-https://wizionic.com/releases/linux}"
HOMESERVER_FEED="${HOMESERVER_FEED:-https://wizionic.com/releases/homeserver/linux}"
PACK_ID="Wizionic"
PACK_TITLE="Wizionic"
MAIN_EXE="Wizionic"
ICON_PATH="App.Maui/Resources/AppIcon/app.png"
ICON_FALLBACKS=(
  "wwwroot/images/icon512.png"
  "wwwroot/images/app.png"
)

SKIP_DOWNLOAD=false
SKIP_HOMESERVER=false

usage() {
  sed -n '2,10p' "$0" | sed 's/^# \?//'
  exit "${1:-0}"
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --skip-download)   SKIP_DOWNLOAD=true; shift ;;
    --skip-homeserver) SKIP_HOMESERVER=true; shift ;;
    --version)         VERSION="${2:?}"; shift 2 ;;
    --version=*)       VERSION="${1#*=}"; shift ;;
    -h|--help)         usage 0 ;;
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
echo " WIZIONIC LINUX PACK â€” AppImage + .deb + install.sh"
echo " Version:  $VERSION"
echo " Feed:     $UPDATE_FEED"
echo "================================================================"

require_cmd dotnet
require_cmd vpk
require_cmd dpkg-deb
require_cmd zip
require_cmd sha256sum

ICON="$(resolve_icon)"
if [[ -z "$ICON" ]]; then
  echo "ERROR: no icon found (tried $ICON_PATH and fallbacks)" >&2
  exit 1
fi
echo "Icon:     $ICON"

# Clean previous builds (keep downloaded prior releases out of DEB dir)
echo ""
echo "Cleaning $OUTPUT_DIR, $RELEASES_DIR, $DEB_BUILD_DIR, $HOMESERVER_OUTPUT, $HOMESERVER_RELEASES ..."
rm -rf "$OUTPUT_DIR" "$RELEASES_DIR" "$DEB_BUILD_DIR" "$HOMESERVER_OUTPUT" "$HOMESERVER_RELEASES"
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
echo "Publishing App.Maui (net10.0 / linux-x64 self-contained) ..."
dotnet publish "App.Maui/App.Maui.csproj" \
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
  --packAuthors "Wizionic" \
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
# Normalize to Wizionic.AppImage for stable install URLs
if [[ "$APPIMAGE_NAME" != "Wizionic.AppImage" ]]; then
  cp -f "$APPIMAGE" "$RELEASES_DIR/Wizionic.AppImage"
  chmod +x "$RELEASES_DIR/Wizionic.AppImage"
  APPIMAGE_NAME="Wizionic.AppImage"
  APPIMAGE="$RELEASES_DIR/Wizionic.AppImage"
fi
echo "AppImage: $APPIMAGE"

# ------------------------------------------------------------------------------
# .deb package (wraps AppImage under /opt/wizionic)
# ------------------------------------------------------------------------------
DEB_NAME="wizionic_${VERSION}_amd64.deb"
echo ""
echo "Building .deb installer ($DEB_NAME) ..."
rm -rf "$DEB_BUILD_DIR"
mkdir -p \
  "$DEB_BUILD_DIR/DEBIAN" \
  "$DEB_BUILD_DIR/opt/wizionic" \
  "$DEB_BUILD_DIR/usr/bin" \
  "$DEB_BUILD_DIR/usr/share/applications" \
  "$DEB_BUILD_DIR/usr/share/icons/hicolor/256x256/apps" \
  "$DEB_BUILD_DIR/usr/share/icons/hicolor/512x512/apps"

cp "$APPIMAGE" "$DEB_BUILD_DIR/opt/wizionic/Wizionic.AppImage"
chmod 755 "$DEB_BUILD_DIR/opt/wizionic/Wizionic.AppImage"

# PATH helper
cat > "$DEB_BUILD_DIR/usr/bin/wizionic" << 'WRAPPER'
#!/bin/sh
exec /opt/wizionic/Wizionic.AppImage "$@"
WRAPPER
chmod 755 "$DEB_BUILD_DIR/usr/bin/wizionic"

INSTALLED_SIZE_KB="$(du -sk "$DEB_BUILD_DIR/opt" | awk '{print $1}')"

cat > "$DEB_BUILD_DIR/DEBIAN/control" << EOF
Package: wizionic
Version: $VERSION
Section: net
Priority: optional
Architecture: amd64
Installed-Size: $INSTALLED_SIZE_KB
Maintainer: Daniel Goodwin <daniellgoodwin@protonmail.com>
Homepage: https://wizionic.com
Description: Privacy-first local AI chat application
 Wizionic is a privacy-first AI chat hub with local-first storage,
 Ollama support, and optional multi-device sync.
EOF

cat > "$DEB_BUILD_DIR/DEBIAN/postinst" << 'EOF'
#!/bin/sh
set -e
chmod 755 /opt/wizionic/Wizionic.AppImage /usr/bin/wizionic 2>/dev/null || true
if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database -q /usr/share/applications 2>/dev/null || true
fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then
  gtk-update-icon-cache -f -t /usr/share/icons/hicolor 2>/dev/null || true
fi
exit 0
EOF
chmod 755 "$DEB_BUILD_DIR/DEBIAN/postinst"

cat > "$DEB_BUILD_DIR/usr/share/applications/wizionic.desktop" << EOF
[Desktop Entry]
Version=1.0
Type=Application
Name=Wizionic
Comment=Privacy-first AI chat hub
Exec=/opt/wizionic/Wizionic.AppImage
Icon=wizionic
Terminal=false
Categories=Network;Office;Chat;Utility;
StartupWMClass=com.wizionic.app
StartupNotify=true
EOF

# Icon (512 source â†’ 256 and 512 hicolor slots; desktops scale as needed)
cp "$ICON" "$DEB_BUILD_DIR/usr/share/icons/hicolor/512x512/apps/app.png"
cp "$ICON" "$DEB_BUILD_DIR/usr/share/icons/hicolor/256x256/apps/app.png"

dpkg-deb --root-owner-group --build "$DEB_BUILD_DIR" "$RELEASES_DIR/$DEB_NAME"
echo "Deb: $RELEASES_DIR/$DEB_NAME"

# ------------------------------------------------------------------------------
# install.sh (curl | bash)
#
# Default: user-local AppImage under ~/Applications (Velopack can self-update).
# Optional: --system installs the .deb under /opt (root-owned; use apt/dpkg or
# re-run install.sh --system to upgrade â€” in-app Velopack replace usually fails).
# ------------------------------------------------------------------------------
echo ""
echo "Writing install.sh ..."
cat > "$RELEASES_DIR/install.sh" << EOF
#!/usr/bin/env bash
# Wizionic Linux installer
#   curl -fsSL https://wizionic.com/install.sh | bash              # AppImage (recommended)
#   curl -fsSL https://wizionic.com/install.sh | bash -s -- --system  # .deb under /opt
set -euo pipefail

VERSION="${VERSION}"
BASE_URL="${UPDATE_FEED}"
INSTALL_DIR="\${WIZIONIC_INSTALL_DIR:-\${HOME}/Applications}"
APPIMAGE="Wizionic.AppImage"
DEB="wizionic_\${VERSION}_amd64.deb"
MODE="appimage"

usage() {
  cat <<USAGE
Wizionic Linux installer

Usage:
  curl -fsSL https://wizionic.com/install.sh | bash
  curl -fsSL https://wizionic.com/install.sh | bash -s -- [options]

Options:
  --appimage, --user   Install AppImage to ~/Applications (default; supports in-app updates)
  --system, --deb      Install system-wide .deb to /opt/wizionic (updates via reinstall)
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

echo "Installing Wizionic \${VERSION} (mode: \$MODE)..."

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
  echo "Wizionic installed system-wide under /opt/wizionic."
  echo "  Launch from your app menu, or run: wizionic"
  echo ""
  echo "NOTE: System installs are root-owned. In-app updates usually cannot replace"
  echo "      /opt/wizionic/Wizionic.AppImage. To upgrade later either:"
  echo "        curl -fsSL https://wizionic.com/install.sh | bash -s -- --system"
  echo "      or use the user AppImage install (recommended for auto-update):"
  echo "        curl -fsSL https://wizionic.com/install.sh | bash"
}

install_appimage() {
  mkdir -p "\$INSTALL_DIR"
  echo "Downloading AppImage from \$BASE_URL/\$APPIMAGE ..."
  # Do not use HEAD â€” release endpoints historically returned 405 for HEAD.
  if ! curl -fL --progress-bar -o "\$INSTALL_DIR/\$APPIMAGE" "\$BASE_URL/\$APPIMAGE"; then
    echo "ERROR: could not download \$BASE_URL/\$APPIMAGE" >&2
    exit 1
  fi
  chmod +x "\$INSTALL_DIR/\$APPIMAGE"

  mkdir -p "\${HOME}/.local/share/applications"
  cat > "\${HOME}/.local/share/applications/wizionic.desktop" << DESKTOP
[Desktop Entry]
Version=1.0
Type=Application
Name=Wizionic
Comment=Privacy-first AI chat hub
Exec=\$INSTALL_DIR/\$APPIMAGE
Icon=wizionic
Terminal=false
Categories=Network;Office;Chat;Utility;
StartupNotify=true
StartupWMClass=com.wizionic.app
DESKTOP

  if command -v curl >/dev/null 2>&1; then
    ICON_DIR="\${HOME}/.local/share/icons/hicolor/256x256/apps"
    mkdir -p "\$ICON_DIR"
    curl -fsSL -o "\$ICON_DIR/app.png" "https://wizionic.com/images/icon512.png" 2>/dev/null \\
      || curl -fsSL -o "\$ICON_DIR/app.png" "https://wizionic.com/images/app.png" 2>/dev/null \\
      || true
  fi

  if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database "\${HOME}/.local/share/applications" 2>/dev/null || true
  fi

  # Prefer user desktop entry over a leftover system .deb entry in menus.
  if [[ -f /usr/share/applications/wizionic.desktop ]]; then
    echo ""
    echo "NOTE: A system install was also detected (/usr/share/applications/wizionic.desktop)."
    echo "      Prefer launching: \$INSTALL_DIR/\$APPIMAGE"
    echo "      or remove the .deb if you only want the user AppImage:"
    echo "        sudo apt remove wizionic   # or: sudo dpkg -r wizionic"
  fi

  echo ""
  echo "Wizionic AppImage installed (user-local â€” supports in-app updates)."
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

# ------------------------------------------------------------------------------
# Part 1b â€” Linux Home Server package (self-contained host + WASM)
# Optional install from MAUI setup wizard. Does not change production Docker.
# ------------------------------------------------------------------------------
HOMESERVER_ZIP_NAME=""
if [[ "$SKIP_HOMESERVER" == "true" ]]; then
  echo ""
  echo "Skipping Home Server package (--skip-homeserver)."
else
  echo ""
  echo "================================================================"
  echo " Building Linux Home Server package (self-contained host + WASM)"
  echo "================================================================"
  mkdir -p "$HOMESERVER_RELEASES"

  echo "Publishing App (linux-x64 self-contained) ..."
  dotnet publish "App.csproj" \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    -o "$HOMESERVER_OUTPUT" \
    -p:DebugType=None \
    -p:DebugSymbols=false \
    -p:BlazorEnableCompression=true \
    -p:SelectBlazorWebAssemblyRazorConfiguration=Release \
    -p:BuildProjectReferences=true \
    -p:Version="$VERSION" \
    -p:ApplicationDisplayVersion="$VERSION"

  if [[ ! -f "$HOMESERVER_OUTPUT/App" && ! -f "$HOMESERVER_OUTPUT/App.dll" ]]; then
    echo "ERROR: homeserver publish missing App entrypoint in $HOMESERVER_OUTPUT" >&2
    ls -la "$HOMESERVER_OUTPUT" | head -40 >&2
    exit 1
  fi
  # Ensure native entrypoint is executable when present
  if [[ -f "$HOMESERVER_OUTPUT/App" ]]; then
    chmod +x "$HOMESERVER_OUTPUT/App" || true
  fi

  HOMESERVER_ZIP_NAME="homeserver-linux-x64-${VERSION}.zip"
  HOMESERVER_ZIP_PATH="$HOMESERVER_RELEASES/$HOMESERVER_ZIP_NAME"
  echo "Zipping $HOMESERVER_ZIP_PATH ..."
  # zip contents of publish dir (no extra top-level folder)
  (
    cd "$HOMESERVER_OUTPUT"
    zip -qr "$REPO_ROOT/$HOMESERVER_ZIP_PATH" .
  )

  HOMESERVER_SHA256="$(sha256sum "$HOMESERVER_ZIP_PATH" | awk '{print $1}')"
  cat > "$HOMESERVER_RELEASES/latest.json" << MANIFEST
{
  "version": "${VERSION}",
  "fileName": "${HOMESERVER_ZIP_NAME}",
  "sha256": "${HOMESERVER_SHA256}",
  "url": "${HOMESERVER_FEED}/${HOMESERVER_ZIP_NAME}"
}
MANIFEST
  echo "Home Server package: $HOMESERVER_ZIP_PATH (sha256=$HOMESERVER_SHA256)"
fi

echo ""
echo "Release artifacts (MAUI):"
ls -lah "$RELEASES_DIR"
if [[ -d "$HOMESERVER_RELEASES" ]]; then
  echo "Release artifacts (Home Server):"
  ls -lah "$HOMESERVER_RELEASES"
fi

echo ""
echo "Done. Local packages are in: $RELEASES_DIR"
if [[ -n "$HOMESERVER_ZIP_NAME" ]]; then
  echo "  Homeserver: $HOMESERVER_RELEASES/$HOMESERVER_ZIP_NAME"
fi
exit 0
