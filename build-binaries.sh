#!/usr/bin/env bash
# ==============================================================================
# build-binaries.sh
# Builds BonxieMux Linux Portable Binaries + .zip + .tar.gz
# Usage: ./build-binaries.sh [--local-mugetsu /path/to/mugetsu] [--local-mugetsu-v2 /path]
# ==============================================================================
set -Eeuo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
source "$SCRIPT_DIR/build-config.sh"

LOCAL_MUGETSU="${SKUA_PLAYER_LOCAL:-}"
LOCAL_MUGETSU_V2="${SKUA_PLAYER_V2_LOCAL:-}"

while [[ $# -gt 0 ]]; do
    case "$1" in
        --local-mugetsu)
            [[ $# -ge 2 ]] || { echo "Error: --local-mugetsu requires a path." >&2; exit 1; }
            LOCAL_MUGETSU="$2"
            shift 2
            ;;
        --local-mugetsu-v2)
            [[ $# -ge 2 ]] || { echo "Error: --local-mugetsu-v2 requires a path." >&2; exit 1; }
            LOCAL_MUGETSU_V2="$2"
            shift 2
            ;;
        --help|-h)
            echo "Usage: $0 [--local-mugetsu PATH] [--local-mugetsu-v2 PATH]"
            echo "  --local-mugetsu PATH     Use a local standard Mugetsu binary or its folder."
            echo "  --local-mugetsu-v2 PATH  Use a local x86-64-v2 Mugetsu binary or its folder."
            exit 0
            ;;
        *)
            echo "Unknown argument: $1" >&2
            echo "Use $0 --help for options." >&2
            exit 1
            ;;
    esac
done

if [[ -n "$LOCAL_MUGETSU" ]]; then export SKUA_PLAYER_LOCAL="$LOCAL_MUGETSU"; fi
if [[ -n "$LOCAL_MUGETSU_V2" ]]; then export SKUA_PLAYER_V2_LOCAL="$LOCAL_MUGETSU_V2"; fi

echo "=== Building Portable Binary Distribution ==="

# Build solution and assemble current/ payload
assemble_payload

rm -rf "$BINARY_DIR"
mkdir -p "$BINARY_DIR"
cp -a "$CURRENT_DIR/." "$BINARY_DIR/current/"
touch "$BINARY_DIR/.portable"

# Copy compiled Rust root launcher wrappers
LAUNCHER_BIN="$SCRIPT_DIR/Skua.WaylandHost/target/release/bonxie-launcher"
if [[ ! -x "$LAUNCHER_BIN" ]]; then
    echo "Error: Compiled Rust launcher not found or not executable: $LAUNCHER_BIN" >&2
    exit 1
fi
cp -f "$LAUNCHER_BIN" "$BINARY_DIR/BonxieMux"
cp -f "$LAUNCHER_BIN" "$BINARY_DIR/BonxieMux.Manager"
chmod +x "$BINARY_DIR/BonxieMux" "$BINARY_DIR/BonxieMux.Manager"

# Shell script launcher fallbacks
cat << 'EOF' > "$BINARY_DIR/BonxieMux.sh"
#!/usr/bin/env bash
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CURRENT_DIR="$SCRIPT_DIR/current"
cd "$CURRENT_DIR"
export LD_LIBRARY_PATH="$CURRENT_DIR:$CURRENT_DIR/runtimes/linux-x64/native:${LD_LIBRARY_PATH:-}"
export PATH="$CURRENT_DIR:$PATH"
export DOTNET_ROOT="$CURRENT_DIR"
exec "$CURRENT_DIR/BonxieMux" "$@"
EOF
chmod +x "$BINARY_DIR/BonxieMux.sh"

cat << 'EOF' > "$BINARY_DIR/BonxieMux.Manager.sh"
#!/usr/bin/env bash
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CURRENT_DIR="$SCRIPT_DIR/current"
cd "$CURRENT_DIR"
export LD_LIBRARY_PATH="$CURRENT_DIR:$CURRENT_DIR/runtimes/linux-x64/native:${LD_LIBRARY_PATH:-}"
export PATH="$CURRENT_DIR:$PATH"
export DOTNET_ROOT="$CURRENT_DIR"
exec "$CURRENT_DIR/BonxieMux.Manager" "$@"
EOF
chmod +x "$BINARY_DIR/BonxieMux.Manager.sh"

# Create root icons if available
if [[ -f "$APP_ICON" ]]; then
    cp -f "$APP_ICON" "$BINARY_DIR/bonxiemux.png"
fi
if [[ -f "$MANAGER_ICON" ]]; then
    cp -f "$MANAGER_ICON" "$BINARY_DIR/bonxiemux-manager.png"
fi

echo "Compressing binary release archives..."
(
    cd "$BUILD_DIR"
    rm -f "BonxieMux-${APP_VERSION}-x86_64.zip" "BonxieMux-${APP_VERSION}-x86_64.tar.gz"
    if command -v zip &>/dev/null; then
        zip -q -r "BonxieMux-${APP_VERSION}-x86_64.zip" "BonxieMux-${APP_VERSION}-x86_64"
    elif command -v bsdtar &>/dev/null; then
        bsdtar -a -cf "BonxieMux-${APP_VERSION}-x86_64.zip" "BonxieMux-${APP_VERSION}-x86_64"
    fi
    tar -czf "BonxieMux-${APP_VERSION}-x86_64.tar.gz" "BonxieMux-${APP_VERSION}-x86_64"
)

echo "=== Binary Packaging Complete! ==="
