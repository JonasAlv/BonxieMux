#!/usr/bin/env bash
# ==============================================================================
# compile-as3.sh
# Compiles the ActionScript 3 client (skua.swf) using Apache Flex SDK (mxmlc)
# and synchronizes it across active development and build target directories.
# ==============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
PROJECT_ROOT="${SCRIPT_DIR}/skua"
SOURCE_PATH="${PROJECT_ROOT}/src"
MAIN_SOURCE="${SOURCE_PATH}/skua/Main.as"
OUTPUT_DIR="${PROJECT_ROOT}/bin"
OUTPUT_PATH="${OUTPUT_DIR}/skua.swf"

# Terminal styling (disabled if not a tty)
if [[ -t 1 ]]; then
    C_RESET="\033[0m"
    C_BOLD="\033[1m"
    C_GREEN="\033[32m"
    C_YELLOW="\033[33m"
    C_BLUE="\033[34m"
    C_RED="\033[31m"
else
    C_RESET=""
    C_BOLD=""
    C_GREEN=""
    C_YELLOW=""
    C_BLUE=""
    C_RED=""
fi

log_info()    { echo -e "${C_BLUE}${C_BOLD}[*]${C_RESET} $*"; }
log_success() { echo -e "${C_GREEN}${C_BOLD}[+]${C_RESET} $*"; }
log_warn()    { echo -e "${C_YELLOW}${C_BOLD}[!]${C_RESET} $*"; }
log_error()   { echo -e "${C_RED}${C_BOLD}[-]${C_RESET} $*" >&2; }

if [[ ! -f "${MAIN_SOURCE}" ]]; then
    log_error "AS3 entry point not found: ${MAIN_SOURCE}"
    exit 1
fi

# 1. Auto-detect Apache Flex SDK
if [[ -z "${FLEX_HOME:-}" ]]; then
    for candidate in \
        "/opt/apache-flex-sdk" \
        "/opt/flex-sdk" \
        "/opt/flex" \
        "/usr/lib/apache-flex-sdk" \
        "/usr/share/apache-flex-sdk" \
        "${HOME}/.local/share/apache-flex-sdk"; do
        if [[ -d "${candidate}" ]]; then
            export FLEX_HOME="${candidate}"
            break
        fi
    done
fi

MXMLC_CMD="${MXMLC:-mxmlc}"
if ! command -v "${MXMLC_CMD}" &>/dev/null; then
    if [[ -n "${FLEX_HOME:-}" && -x "${FLEX_HOME}/bin/mxmlc" ]]; then
        MXMLC_CMD="${FLEX_HOME}/bin/mxmlc"
    else
        log_error "Apache Flex mxmlc compiler not found."
        log_error "Install Apache Flex SDK (e.g. 'paru -S apache-flex-sdk') or set FLEX_HOME / MXMLC."
        exit 1
    fi
fi

# 2. Force Java 8 (the only fully compatible and stable runtime for Apache Flex SDK)
JAVA_8_HOME=""

is_java_8() {
    local jvm_bin="$1/bin/java"
    [[ -x "$jvm_bin" ]] && "$jvm_bin" -version 2>&1 | grep -qE 'version "(1\.8\.|8(\.|$))'
}

# Check if caller explicitly passed a Java 8 JAVA_HOME
if [[ -n "${JAVA_HOME:-}" ]] && is_java_8 "${JAVA_HOME}"; then
    JAVA_8_HOME="${JAVA_HOME}"
fi

# Search standard Linux JVM locations for Java 8
if [[ -z "${JAVA_8_HOME}" ]]; then
    for candidate in \
        "/usr/lib/jvm/java-8-openjdk" \
        "/usr/lib/jvm/java-8-adoptopenjdk" \
        "/usr/lib/jvm/java-1.8.0-openjdk" \
        "/usr/lib/jvm/java-8-openjdk-amd64" \
        "/usr/lib/jvm/zulu-8" \
        "/usr/lib/jvm/java-8-oracle" \
        "/opt/jdk8" \
        "/opt/java-8"; do
        if is_java_8 "${candidate}"; then
            JAVA_8_HOME="${candidate}"
            break
        fi
    done
fi

if [[ -z "${JAVA_8_HOME}" ]]; then
    log_error "Java 8 (OpenJDK 1.8) is required to run the Apache Flex compiler but was not found."
    log_error "Please install Java 8:"
    log_error "  - Arch/CachyOS: sudo pacman -S jre8-openjdk"
    log_error "  - Ubuntu/Debian: sudo apt install openjdk-8-jre-headless"
    log_error "  - Fedora: sudo dnf install java-1.8.0-openjdk"
    exit 1
fi

export JAVA_HOME="${JAVA_8_HOME}"
export PATH="${JAVA_HOME}/bin:${PATH}"
log_info "Using forced Java 8 runtime: ${JAVA_HOME}"

# 3. Resolve PlayerGlobal 28.0 SWC dependency
# Prefer checked-in repository PlayerGlobal if the configured system one doesn't have version 28.0
if [[ -z "${PLAYERGLOBAL_HOME:-}" ]] || [[ ! -s "${PLAYERGLOBAL_HOME}/28.0/playerglobal.swc" ]]; then
    if [[ -s "${SCRIPT_DIR}/playerglobal/28.0/playerglobal.swc" ]]; then
        export PLAYERGLOBAL_HOME="${SCRIPT_DIR}/playerglobal"
    fi
fi

if [[ -z "${PLAYERGLOBAL_HOME:-}" ]] || [[ ! -s "${PLAYERGLOBAL_HOME}/28.0/playerglobal.swc" ]]; then
    log_error "PlayerGlobal 28.0 was not found."
    log_error "Set PLAYERGLOBAL_HOME or restore ${SCRIPT_DIR}/playerglobal/28.0/playerglobal.swc."
    exit 1
fi

mkdir -p "${OUTPUT_DIR}"

log_info "Compiling ActionScript 3 client with target Player 28.0..."
"${MXMLC_CMD}" \
    -source-path "${SOURCE_PATH}" \
    -default-size 958 550 \
    -output "${OUTPUT_PATH}" \
    "${MAIN_SOURCE}" \
    -target-player 28.0 \
    -optimize

SWF_SIZE="$(stat -c%s "${OUTPUT_PATH}" 2>/dev/null || stat -f%z "${OUTPUT_PATH}" 2>/dev/null || echo "0")"
log_success "Built AS3 game client: ${OUTPUT_PATH} (${SWF_SIZE} bytes)"

# 4. Synchronize built SWF to active development and runtime paths
SYNC_TARGETS=(
    "${SCRIPT_DIR}/../Skua.App.Avalonia/skua.swf"
    "${SCRIPT_DIR}/../Skua.App.Avalonia/bin/Debug/net10.0/skua.swf"
    "${SCRIPT_DIR}/../Skua.App.Avalonia/bin/Release/net10.0/skua.swf"
    "${SCRIPT_DIR}/../Build/payload/current/skua.swf"
)

for target in "${SYNC_TARGETS[@]}"; do
    target_dir="$(dirname "${target}")"
    if [[ -d "${target_dir}" ]]; then
        cp -f "${OUTPUT_PATH}" "${target}"
        log_info "Synced to $(realpath -m "${target}")"
    fi
done

log_success "AS3 compilation and sync complete."
