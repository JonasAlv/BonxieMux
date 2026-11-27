#!/usr/bin/env bash
# ==============================================================================
# build-appimages.sh
# Builds BonxieMux Standalone AppImages + .zip + .tar.gz
# Usage: ./build-appimages.sh [--local-mugetsu /path/to/mugetsu] [--local-mugetsu-v2 /path] [--reuse-payload]
# ==============================================================================
set -Eeuo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
source "$SCRIPT_DIR/build-config.sh"
parse_common_args "$0" "$@"

echo "=== Building Standalone AppImages ==="

# Build solution and assemble current/ payload
assemble_payload

appimagetool_cache="${XDG_CACHE_HOME:-$HOME/.cache}/bonxiemux/tools/appimagetool"
appimagetool_cmd="appimagetool"
if ! command -v appimagetool &>/dev/null; then
    if [[ -x "$appimagetool_cache" ]]; then
        appimagetool_cmd="$appimagetool_cache"
    else
        echo "appimagetool not found in PATH. Downloading standalone appimagetool..."
        mkdir -p "$(dirname "$appimagetool_cache")"
        curl_download "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage" -o "$appimagetool_cache"
        chmod +x "$appimagetool_cache"
        appimagetool_cmd="$appimagetool_cache"
    fi
fi

skua_appdir="$BUILD_DIR/BonxieMux.AppDir"
mgr_appdir="$BUILD_DIR/BonxieMux-Manager.AppDir"
rm -rf "$skua_appdir" "$mgr_appdir"
mkdir -p "$skua_appdir/usr/bin" "$skua_appdir/usr/share/applications" "$skua_appdir/current"
mkdir -p "$mgr_appdir/usr/bin" "$mgr_appdir/usr/share/applications" "$mgr_appdir/current"

cp -a "$CURRENT_DIR/." "$skua_appdir/current/"
cp -a "$CURRENT_DIR/." "$mgr_appdir/current/"
# Strip game-client-only assets from Manager AppImage (Manager launches BonxieMux separately)
rm -rf \
    "$mgr_appdir/current/player" \
    "$mgr_appdir/current/player-x86-64-v2" \
    "$mgr_appdir/current/FFDec" \
    "$mgr_appdir/current/plugins" \
    "$mgr_appdir/current/QuestData.json" \
    "$mgr_appdir/current/skua.swf" \
    "$mgr_appdir/current/libbonxie_wayland.so" \
    "$mgr_appdir/current/BonxieMux" \
    "$mgr_appdir/current/BonxieMux.dll" \
    "$mgr_appdir/current/BonxieMux.deps.json" \
    "$mgr_appdir/current/BonxieMux.runtimeconfig.json"

# Icon generation
skua_icon="$APP_ICON"
mgr_icon="$MANAGER_ICON"

if python3 -c "from PIL import Image" 2>/dev/null; then
    python3 - <<EOF
from PIL import Image
import os

def gen(src, appdir, name):
    img = Image.open(src)
    root_icon = os.path.join(appdir, f"{name}.png")
    img_256 = img.resize((256, 256), Image.Resampling.LANCZOS)
    img_256.save(root_icon)
    img_256.save(os.path.join(appdir, ".DirIcon"), format="PNG")
    for s in [16, 32, 48, 64, 128, 256, 512]:
        p = os.path.join(appdir, "usr", "share", "icons", "hicolor", f"{s}x{s}", "apps")
        os.makedirs(p, exist_ok=True)
        img.resize((s, s), Image.Resampling.LANCZOS).save(os.path.join(p, f"{name}.png"))

gen("$skua_icon", "$skua_appdir", "bonxiemux")
gen("$skua_icon", "$skua_appdir", "BonxieMux")
gen("$mgr_icon", "$mgr_appdir", "bonxiemux-manager")
gen("$mgr_icon", "$mgr_appdir", "BonxieMux-Manager")
EOF
else
    echo "Note: python3 PIL not found, copying raw icon assets..."
    for target in "$skua_appdir:bonxiemux:$skua_icon" "$skua_appdir:BonxieMux:$skua_icon" "$mgr_appdir:bonxiemux-manager:$mgr_icon" "$mgr_appdir:BonxieMux-Manager:$mgr_icon"; do
        IFS=':' read -r appdir name src_icon <<< "$target"
        cp -f "$src_icon" "$appdir/${name}.png"
        cp -f "$src_icon" "$appdir/.DirIcon"
        mkdir -p "$appdir/usr/share/icons/hicolor/256x256/apps"
        cp -f "$src_icon" "$appdir/usr/share/icons/hicolor/256x256/apps/${name}.png"
    done
fi

cat << 'EOF' > "$skua_appdir/bonxiemux.desktop"
[Desktop Entry]
Type=Application
Name=BonxieMux
GenericName=AQW Bot & Game Client
Comment=Linux AQW client with advanced scripting and multi-account tabs
Exec=AppRun %U
Icon=bonxiemux
Terminal=false
Categories=Game;RolePlaying;
StartupWMClass=BonxieMux
EOF
cp "$skua_appdir/bonxiemux.desktop" "$skua_appdir/usr/share/applications/bonxiemux.desktop"
cp "$skua_appdir/bonxiemux.desktop" "$skua_appdir/BonxieMux.desktop"
cp "$skua_appdir/bonxiemux.desktop" "$skua_appdir/usr/share/applications/BonxieMux.desktop"

cat << 'EOF' > "$mgr_appdir/bonxiemux-manager.desktop"
[Desktop Entry]
Type=Application
Name=BonxieMux Manager
GenericName=BonxieMux Account & Script Manager
Comment=Account manager, script updater, and launcher for BonxieMux
Exec=AppRun %U
Icon=bonxiemux-manager
Terminal=false
Categories=Game;
StartupWMClass=BonxieMux.Manager
EOF
cp "$mgr_appdir/bonxiemux-manager.desktop" "$mgr_appdir/usr/share/applications/bonxiemux-manager.desktop"
cp "$mgr_appdir/bonxiemux-manager.desktop" "$mgr_appdir/BonxieMux-Manager.desktop"
cp "$mgr_appdir/bonxiemux-manager.desktop" "$mgr_appdir/usr/share/applications/BonxieMux-Manager.desktop"

cat << 'EOF' > "$skua_appdir/AppRun"
#!/usr/bin/env bash
set -e
APPDIR="$(dirname "$(readlink -f "${0}")")"
export PATH="$APPDIR/usr/bin:$APPDIR/current:$PATH"
export LD_LIBRARY_PATH="$APPDIR/usr/lib:$APPDIR/current:$APPDIR/current/runtimes/linux-x64/native:${LD_LIBRARY_PATH:-}"
export DOTNET_ROOT="$APPDIR/current"
export DOTNET_EnableDiagnostics="${DOTNET_EnableDiagnostics:-0}"
exec "$APPDIR/current/BonxieMux" "$@"
EOF
chmod +x "$skua_appdir/AppRun"
ln -sf "../current/BonxieMux" "$skua_appdir/usr/bin/BonxieMux"

cat << 'EOF' > "$mgr_appdir/AppRun"
#!/usr/bin/env bash
set -e
APPDIR="$(dirname "$(readlink -f "${0}")")"
export PATH="$APPDIR/usr/bin:$APPDIR/current:$PATH"
export LD_LIBRARY_PATH="$APPDIR/usr/lib:$APPDIR/current:$APPDIR/current/runtimes/linux-x64/native:${LD_LIBRARY_PATH:-}"
export DOTNET_ROOT="$APPDIR/current"
export DOTNET_EnableDiagnostics="${DOTNET_EnableDiagnostics:-0}"
exec "$APPDIR/current/BonxieMux.Manager" "$@"
EOF
chmod +x "$mgr_appdir/AppRun"
ln -sf "../current/BonxieMux.Manager" "$mgr_appdir/usr/bin/BonxieMux-Manager"

# Files copied from dotnet output can retain owner-only execute bits (0744).
chmod -R a+rX "$skua_appdir" "$mgr_appdir"

export APPIMAGE_EXTRACT_AND_RUN=1
ARCH=x86_64 "$appimagetool_cmd" --comp zstd -n "$skua_appdir" "$BUILD_DIR/BonxieMux-${APP_VERSION}-x86_64.AppImage" &
pid_app=$!
ARCH=x86_64 "$appimagetool_cmd" --comp zstd -n "$mgr_appdir" "$BUILD_DIR/BonxieMux-Manager-${APP_VERSION}-x86_64.AppImage" &
pid_mgr=$!
wait "$pid_app"
wait "$pid_mgr"
chmod +x "$BUILD_DIR/BonxieMux-${APP_VERSION}-x86_64.AppImage" "$BUILD_DIR/BonxieMux-Manager-${APP_VERSION}-x86_64.AppImage"

rm -rf "$skua_appdir" "$mgr_appdir"

rm -rf "$APPIMAGE_BUNDLE_DIR"
mkdir -p "$APPIMAGE_BUNDLE_DIR"
cp -f "$BUILD_DIR/BonxieMux-${APP_VERSION}-x86_64.AppImage" "$APPIMAGE_BUNDLE_DIR/"
cp -f "$BUILD_DIR/BonxieMux-Manager-${APP_VERSION}-x86_64.AppImage" "$APPIMAGE_BUNDLE_DIR/"

echo "Compressing AppImage release archives..."
compress_release_dir "BonxieMux-${APP_VERSION}-x86_64"

echo "=== AppImage Packaging Complete! ==="
