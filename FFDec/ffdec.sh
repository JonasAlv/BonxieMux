#!/usr/bin/env bash
set -Eeuo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"

if ! command -v java >/dev/null 2>&1; then
    echo "FFDec requires Java. Install a supported Java runtime and retry." >&2
    exit 127
fi
if [[ ! -s "$SCRIPT_DIR/ffdec.jar" ]]; then
    echo "FFDec jar not found: $SCRIPT_DIR/ffdec.jar" >&2
    exit 1
fi

java_args=(
    "-Xmx${FFDEC_MEMORY:-1024m}"
    -Djava.net.preferIPv4Stack=true
    -Djna.nosys=true
    -Djava.util.Arrays.useLegacyMergeSort=true
    --enable-native-access=ALL-UNNAMED
    --add-opens=java.base/java.lang=ALL-UNNAMED
    --add-opens=java.base/java.io=ALL-UNNAMED
    --add-opens=java.base/java.util=ALL-UNNAMED
    --add-opens=java.base/sun.nio.fs=ALL-UNNAMED
    --add-opens=java.desktop/sun.awt=ALL-UNNAMED
    --add-opens=java.desktop/sun.java2d=ALL-UNNAMED
)

if [[ -n "${FFDEC_STACK_SIZE:-}" ]]; then
    java_args+=("-Xss${FFDEC_STACK_SIZE}")
fi

exec java "${java_args[@]}" -jar "$SCRIPT_DIR/ffdec.jar" "$@"
