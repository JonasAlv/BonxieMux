#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="${SCRIPT_DIR}/skua"
SOURCE_PATH="${PROJECT_ROOT}/src"
MAIN_SOURCE="${SOURCE_PATH}/skua/Main.as"
OUTPUT_DIR="${PROJECT_ROOT}/bin"
OUTPUT_PATH="${OUTPUT_DIR}/skua.swf"

if [ ! -f "${MAIN_SOURCE}" ]; then
    echo "AS3 entry point was not found: ${MAIN_SOURCE}" >&2
    exit 1
fi

# Auto-detect FLEX_HOME if not set
if [ -z "${FLEX_HOME:-}" ]; then
    if [ -d "/opt/apache-flex-sdk" ]; then
        export FLEX_HOME="/opt/apache-flex-sdk"
    fi
fi

MXMLC_CMD="${MXMLC:-mxmlc}"
if ! command -v "${MXMLC_CMD}" &>/dev/null; then
    if [ -n "${FLEX_HOME:-}" ] && [ -x "${FLEX_HOME}/bin/mxmlc" ]; then
        MXMLC_CMD="${FLEX_HOME}/bin/mxmlc"
    else
        echo "Apache Flex mxmlc was not found. Install Apache Flex SDK (e.g. paru -S apache-flex-sdk) or set FLEX_HOME / MXMLC." >&2
        exit 1
    fi
fi

# Auto-detect compatible Java (Java 8 or 11 preferred for Flex SDK)
if [ -z "${JAVA_HOME:-}" ]; then
    for jvm in /usr/lib/jvm/java-8-openjdk /usr/lib/jvm/java-11-openjdk /usr/lib/jvm/java-17-openjdk; do
        if [ -d "$jvm" ]; then
            export JAVA_HOME="$jvm"
            export PATH="$jvm/bin:$PATH"
            break
        fi
    done
fi

# Prefer the checked-in PlayerGlobal SWC unless the caller chose another SDK.
if [ -z "${PLAYERGLOBAL_HOME:-}" ] && [ -s "${SCRIPT_DIR}/playerglobal/28.0/playerglobal.swc" ]; then
    export PLAYERGLOBAL_HOME="${SCRIPT_DIR}/playerglobal"
fi
if [ -z "${PLAYERGLOBAL_HOME:-}" ] || [ ! -s "${PLAYERGLOBAL_HOME}/28.0/playerglobal.swc" ]; then
    echo "PlayerGlobal 28.0 was not found. Set PLAYERGLOBAL_HOME or restore ${SCRIPT_DIR}/playerglobal/28.0/playerglobal.swc." >&2
    exit 1
fi

mkdir -p "${OUTPUT_DIR}"

"${MXMLC_CMD}" \
    -source-path "${SOURCE_PATH}" \
    -default-size 958 550 \
    -output "${OUTPUT_PATH}" \
    "${MAIN_SOURCE}" \
    -target-player 28.0 \
    -optimize

echo "AS3 game content built: ${OUTPUT_PATH}"
