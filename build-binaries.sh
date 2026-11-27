#!/usr/bin/env bash
# ==============================================================================
# build-binaries.sh
# Builds BonxieMux Linux Portable Binaries + .zip + .tar.gz
# Usage: ./build-binaries.sh [--local-mugetsu /path/to/mugetsu] [--local-mugetsu-v2 /path] [--reuse-payload]
# ==============================================================================
set -Eeuo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
source "$SCRIPT_DIR/build-config.sh"
parse_common_args "$0" "$@"

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
export DOTNET_EnableDiagnostics="${DOTNET_EnableDiagnostics:-0}"
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
export DOTNET_EnableDiagnostics="${DOTNET_EnableDiagnostics:-0}"
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
compress_release_dir "BonxieMux-${APP_VERSION}-x86_64"

echo "=== Binary Packaging Complete! ==="
