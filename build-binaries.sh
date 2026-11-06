#!/usr/bin/env bash
# ==============================================================================
# build-binaries.sh
# Builds BonxieMux Linux Portable Binaries + .zip + .tar.gz
# Usage: ./build-binaries.sh [--local-mugetsu /path/to/mugetsu] [--local-mugetsu-v2 /path] [--local-watchdog /path]
# ==============================================================================
set -Eeuo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
source "$SCRIPT_DIR/build-config.sh"

LOCAL_MUGETSU="${SKUA_PLAYER_LOCAL:-}"
LOCAL_MUGETSU_V2="${SKUA_PLAYER_V2_LOCAL:-}"
LOCAL_WATCHDOG="${WATCHDOG_LOCAL:-}"

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
        --local-watchdog)
            [[ $# -ge 2 ]] || { echo "Error: --local-watchdog requires a path." >&2; exit 1; }
            LOCAL_WATCHDOG="$2"
            shift 2
            ;;
        --help|-h)
            echo "Usage: $0 [--local-mugetsu PATH] [--local-mugetsu-v2 PATH] [--local-watchdog PATH]"
            echo "  --local-mugetsu PATH     Use a local standard Mugetsu binary or its folder."
            echo "  --local-mugetsu-v2 PATH  Use a local x86-64-v2 Mugetsu binary or its folder."
            echo "  --local-watchdog PATH    Use a local WatchDog.dll or its folder."
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
if [[ -n "$LOCAL_WATCHDOG" ]]; then export WATCHDOG_LOCAL="$LOCAL_WATCHDOG"; fi

echo "=== Building Portable Binary Distribution ==="

# Build solution and assemble current/ payload
assemble_payload

rm -rf "$BINARY_DIR"
mkdir -p "$BINARY_DIR"
cp -a "$CURRENT_DIR/." "$BINARY_DIR/current/"
touch "$BINARY_DIR/.portable"

# Compile native root C launchers
cat << 'EOF' > "$BUILD_DIR/launcher.c"
#define _GNU_SOURCE
#include <unistd.h>
#include <libgen.h>
#include <limits.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

int main(int argc, char *argv[]) {
    char exe_path[PATH_MAX];
    ssize_t len = readlink("/proc/self/exe", exe_path, sizeof(exe_path) - 1);
    if (len == -1) {
        perror("readlink");
        return 1;
    }
    exe_path[len] = '\0';

    char *dir = dirname(exe_path);
    char current_dir[PATH_MAX];
    snprintf(current_dir, sizeof(current_dir), "%s/current", dir);

    char target[PATH_MAX];
    snprintf(target, sizeof(target), "%s/%s", current_dir, TARGET_BIN);

    if (chdir(current_dir) != 0) {
        perror("chdir");
        return 1;
    }

    char *orig_ld = getenv("LD_LIBRARY_PATH");
    char new_ld[PATH_MAX * 4];
    if (orig_ld && strlen(orig_ld) > 0) {
        snprintf(new_ld, sizeof(new_ld), "%s:%s/runtimes/linux-x64/native:%s", current_dir, current_dir, orig_ld);
    } else {
        snprintf(new_ld, sizeof(new_ld), "%s:%s/runtimes/linux-x64/native", current_dir, current_dir);
    }
    setenv("LD_LIBRARY_PATH", new_ld, 1);

    char *orig_path = getenv("PATH");
    char new_path[PATH_MAX * 2];
    if (orig_path && strlen(orig_path) > 0) {
        snprintf(new_path, sizeof(new_path), "%s:%s", current_dir, orig_path);
    } else {
        snprintf(new_path, sizeof(new_path), "%s", current_dir);
    }
    setenv("PATH", new_path, 1);
    setenv("DOTNET_ROOT", current_dir, 1);

    argv[0] = target;
    execv(target, argv);

    perror("execv failed");
    return 1;
}
EOF

gcc -O2 -DTARGET_BIN=\"BonxieMux\" "$BUILD_DIR/launcher.c" -o "$BINARY_DIR/BonxieMux"
gcc -O2 -DTARGET_BIN=\"BonxieMux.Manager\" "$BUILD_DIR/launcher.c" -o "$BINARY_DIR/BonxieMux.Manager"
rm -f "$BUILD_DIR/launcher.c"

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
if [[ -f "$SKUA_ICON" ]]; then
    cp -f "$SKUA_ICON" "$BINARY_DIR/bonxiemux.png"
    cp -f "$SKUA_ICON" "$BINARY_DIR/bonxiemux-manager.png"
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
