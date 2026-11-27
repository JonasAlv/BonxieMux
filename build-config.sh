#!/usr/bin/env bash
# ==============================================================================
# build-config.sh
# Shared configuration and version properties reader for BonxieMux
# Strictly requires versions to be defined in Directory.Build.props (no fallbacks)
# ==============================================================================
if [[ -z "${SCRIPT_DIR:-}" ]]; then
    SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
fi

BUILD_DIR="$SCRIPT_DIR/Build"
CURRENT_DIR="$BUILD_DIR/payload/current"

APP_BIN="$SCRIPT_DIR/Skua.App.Avalonia/bin/Release/net10.0"
MGR_BIN="$SCRIPT_DIR/Skua.Manager.Avalonia/bin/Release/net10.0"
SWF_SOURCE="$SCRIPT_DIR/Skua.AS3/skua/bin/skua.swf"
APP_ICON="$SCRIPT_DIR/Skua.Shared.Avalonia/Assets/AppIcon.png"
MANAGER_ICON="$SCRIPT_DIR/Skua.Shared.Avalonia/Assets/ManagerIcon.png"
SKUA_ICON="$APP_ICON"

PROPS_FILE="$SCRIPT_DIR/Directory.Build.props"
if [[ ! -s "$PROPS_FILE" ]]; then
    echo "Error: missing properties file: $PROPS_FILE" >&2
    exit 1
fi

read_prop() {
    local name="$1"
    sed -n "s:.*<${name}[^>]*>\\([^<]*\\)</${name}>.*:\\1:p" "$PROPS_FILE" | head -n 1
}

APP_VERSION="${Version:-$(read_prop Version)}"
SKUA_PLAYER_VERSION="$(read_prop SkuaPlayerVersion)"
SKUA_PLAYER_OS="$(read_prop SkuaPlayerOs)"

BINARY_DIR="$BUILD_DIR/BonxieMux-${APP_VERSION}-x86_64"
APPIMAGE_BUNDLE_DIR="$BUILD_DIR/BonxieMux-${APP_VERSION}-x86_64"

if [[ -z "$APP_VERSION" || -z "$SKUA_PLAYER_VERSION" || -z "$SKUA_PLAYER_OS" ]]; then
    echo "Error: Directory.Build.props is missing required version properties (Version, SkuaPlayerVersion, or SkuaPlayerOs)." >&2
    exit 1
fi

# Remote URLs based strictly on property values (always fetched fresh unless --local-mugetsu is passed)
SKUA_PLAYER_URL="https://github.com/JonasAlv/skua-mugetsu/releases/download/v${SKUA_PLAYER_VERSION}/linux-x86-64-v3.tar.gz"
SKUA_PLAYER_V2_URL="https://github.com/JonasAlv/skua-mugetsu/releases/download/v${SKUA_PLAYER_VERSION}/linux-x86-64-v2.tar.gz"

# Resilient curl download helper
curl_download() {
    curl -fsSL --retry 3 --retry-delay 2 --retry-all-errors --connect-timeout 15 "$@"
}

# Shared CLI argument parser for build-binaries.sh and build-appimages.sh
parse_common_args() {
    local prog="$1"
    shift
    local local_mugetsu="${SKUA_PLAYER_LOCAL:-}"
    local local_mugetsu_v2="${SKUA_PLAYER_V2_LOCAL:-}"
    REUSE_PAYLOAD="${REUSE_PAYLOAD:-0}"

    while [[ $# -gt 0 ]]; do
        case "$1" in
            --local-mugetsu)
                [[ $# -ge 2 ]] || { echo "Error: --local-mugetsu requires a path." >&2; exit 1; }
                local_mugetsu="$2"
                shift 2
                ;;
            --local-mugetsu-v2)
                [[ $# -ge 2 ]] || { echo "Error: --local-mugetsu-v2 requires a path." >&2; exit 1; }
                local_mugetsu_v2="$2"
                shift 2
                ;;
            --reuse-payload|--skip-build)
                REUSE_PAYLOAD=1
                shift
                ;;
            --help|-h)
                echo "Usage: $prog [--local-mugetsu PATH] [--local-mugetsu-v2 PATH] [--reuse-payload]"
                echo "  --local-mugetsu PATH     Use a local standard Mugetsu binary or its folder."
                echo "  --local-mugetsu-v2 PATH  Use a local x86-64-v2 Mugetsu binary or its folder."
                echo "  --reuse-payload          Reuse existing Build/payload/current instead of rebuilding."
                exit 0
                ;;
            *)
                echo "Unknown argument: $1" >&2
                echo "Use $prog --help for options." >&2
                exit 1
                ;;
        esac
    done

    if [[ -n "$local_mugetsu" ]]; then export SKUA_PLAYER_LOCAL="$local_mugetsu"; fi
    if [[ -n "$local_mugetsu_v2" ]]; then export SKUA_PLAYER_V2_LOCAL="$local_mugetsu_v2"; fi
    export REUSE_PAYLOAD
}

# Download and extract a skua-mugetsu tarball directly into <dest_dir>
fetch_mugetsu_to_dir() {
    local url="$1"
    local dest_dir="$2"
    local tmp_dir="$BUILD_DIR/.mugetsu_extract.$$"
    local tmp_archive="$tmp_dir/archive.tar.gz"

    rm -rf "$tmp_dir"
    mkdir -p "$tmp_dir" "$dest_dir"
    if ! curl_download "$url" -o "$tmp_archive"; then
        rm -rf "$tmp_dir"
        return 1
    fi

    tar -xzf "$tmp_archive" -C "$tmp_dir"
    rm -f "$tmp_archive"

    if [[ -d "$tmp_dir/Mugetsu" ]]; then
        cp -a "$tmp_dir/Mugetsu/." "$dest_dir/"
    else
        cp -a "$tmp_dir/." "$dest_dir/"
    fi
    rm -rf "$tmp_dir"
    return 0
}

# Compress a release directory inside $BUILD_DIR into .zip and .tar.gz in parallel
compress_release_dir() {
    local dir_name="$1"
    (
        cd "$BUILD_DIR"
        rm -f "${dir_name}.zip" "${dir_name}.tar.gz"

        local pids=()
        if command -v zip &>/dev/null; then
            zip -q -r "${dir_name}.zip" "$dir_name" &
            pids+=($!)
        elif command -v bsdtar &>/dev/null; then
            bsdtar -a -cf "${dir_name}.zip" "$dir_name" &
            pids+=($!)
        fi

        if command -v pigz &>/dev/null; then
            tar -I pigz -cf "${dir_name}.tar.gz" "$dir_name" &
            pids+=($!)
        else
            tar -czf "${dir_name}.tar.gz" "$dir_name" &
            pids+=($!)
        fi

        local failed=0
        for pid in "${pids[@]}"; do
            if ! wait "$pid"; then
                failed=1
            fi
        done
        return "$failed"
    )
}

# Shared function to assemble application payload into $CURRENT_DIR
assemble_payload() {
    local local_player="${SKUA_PLAYER_LOCAL:-}"
    local local_player_v2="${SKUA_PLAYER_V2_LOCAL:-}"

    if [[ "${REUSE_PAYLOAD:-0}" == "1" && -x "$CURRENT_DIR/BonxieMux" && -x "$CURRENT_DIR/BonxieMux.Manager" && -s "$CURRENT_DIR/libcoreclr.so" ]]; then
        echo "--- Reusing existing payload in $CURRENT_DIR ---"
        return 0
    fi

    echo "--- Preparing Build directory ($BUILD_DIR) ---"
    mkdir -p "$BUILD_DIR"
    rm -rf "$CURRENT_DIR" "$BUILD_DIR/.publish_app" "$BUILD_DIR/.publish_mgr"

    # Compile AS3 game client if Apache Flex SDK is available and sources changed
    if command -v mxmlc &>/dev/null || [[ -n "${FLEX_HOME:-}" ]] || [[ -d "/opt/apache-flex-sdk" ]]; then
        if [[ ! -s "$SWF_SOURCE" ]] || [[ -n "$(find "$SCRIPT_DIR/Skua.AS3/skua/src" -type f -newer "$SWF_SOURCE" -print -quit 2>/dev/null)" ]]; then
            echo "--- Building AS3 game client (skua.swf) from source ---"
            "$SCRIPT_DIR/Skua.AS3/compile-as3.sh"
        else
            echo "--- AS3 game client (skua.swf) is up-to-date ---"
        fi
    elif [[ -s "$SWF_SOURCE" ]]; then
        echo "--- Using existing skua.swf (install apache-flex-sdk to compile from source) ---"
    else
        echo "Error: Required game client was not found: $SWF_SOURCE" >&2
        echo "Run Skua.AS3/compile-as3.sh after installing Apache Flex mxmlc, then retry." >&2
        exit 1
    fi

    # Build embedded Wayland DMA-BUF host (libbonxie_wayland.so) and Rust launcher (bonxie-launcher)
    local wayland_host_so="$SCRIPT_DIR/Skua.WaylandHost/target/release/libbonxie_wayland.so"
    local wayland_launcher="$SCRIPT_DIR/Skua.WaylandHost/target/release/bonxie-launcher"
    if command -v cargo &>/dev/null; then
        if [[ ! -s "$wayland_host_so" || ! -x "$wayland_launcher" ]] || [[ -n "$(find "$SCRIPT_DIR/Skua.WaylandHost/src" "$SCRIPT_DIR/Skua.WaylandHost/Cargo.toml" "$SCRIPT_DIR/Skua.WaylandHost/Cargo.lock" -type f \( -newer "$wayland_host_so" -o -newer "$wayland_launcher" \) -print -quit 2>/dev/null)" ]]; then
            echo "--- Building embedded Wayland DMA-BUF host & Rust launcher ---"
            cargo build --release --locked --manifest-path "$SCRIPT_DIR/Skua.WaylandHost/Cargo.toml"
        else
            echo "--- Embedded Wayland DMA-BUF host & Rust launcher are up-to-date ---"
        fi
    elif [[ -s "$wayland_host_so" && -x "$wayland_launcher" ]]; then
        echo "--- Using existing libbonxie_wayland.so & bonxie-launcher (install cargo to build from source) ---"
    else
        echo "Error: libbonxie_wayland.so or bonxie-launcher not found and cargo is missing." >&2
        exit 1
    fi

    echo "--- Restoring & building .NET solution (Release) ---"
    rm -rf "$APP_BIN/player" "$APP_BIN/player-x86-64-v2"
    local dotnet_args=("$SCRIPT_DIR/Skua.sln" -c Release --no-restore)
    local publish_args=()
    if [[ -n "$local_player" ]]; then
        if [[ -f "$local_player" ]]; then
            local_player="$(cd -- "$(dirname "$local_player")" && pwd -P)"
        fi
        if [[ ! -d "$local_player" || ! -x "$local_player/Mugetsu" ]]; then
            echo "Error: SKUA_PLAYER_LOCAL must point to an executable Mugetsu binary or directory containing it." >&2
            exit 1
        fi
        local_player="$(cd -- "$local_player" && pwd -P)"
        dotnet_args+=("-p:SkuaPlayerLocal=$local_player")
        echo "Using local Mugetsu from $local_player"
    fi
    if [[ -n "$local_player_v2" ]]; then
        if [[ -f "$local_player_v2" ]]; then
            local_player_v2="$(cd -- "$(dirname "$local_player_v2")" && pwd -P)"
        fi
        if [[ ! -d "$local_player_v2" || ! -x "$local_player_v2/Mugetsu" ]]; then
            echo "Error: SKUA_PLAYER_V2_LOCAL must point to an executable Mugetsu binary or directory containing it." >&2
            exit 1
        fi
        local_player_v2="$(cd -- "$local_player_v2" && pwd -P)"
        dotnet_args+=("-p:SkuaPlayerV2Local=$local_player_v2")
        dotnet_args+=("-p:SkuaPlayerAvxDir=$local_player_v2")
        echo "Using local x86-64-v2 Mugetsu from $local_player_v2"
    fi
    dotnet restore "$SCRIPT_DIR/Skua.sln" -r linux-x64 -p:PublishReadyToRun=true
    dotnet build "${dotnet_args[@]}"

    echo "--- Publishing self-contained .NET runtime (linux-x64) ---"
    local pub_app="$BUILD_DIR/.publish_app"
    local pub_mgr="$BUILD_DIR/.publish_mgr"
    rm -rf "$pub_app" "$pub_mgr"
    mkdir -p "$pub_app" "$pub_mgr"

    local common_publish_flags=(
        -c Release
        -r linux-x64
        --self-contained
        --no-restore
        -p:PublishReadyToRun=true
        -p:PublishReadyToRunComposite=false
        -p:CopyOutputSymbolsToPublishDirectory=false
    )
    dotnet publish "$SCRIPT_DIR/Skua.App.Avalonia/Skua.App.Avalonia.csproj" "${common_publish_flags[@]}" -o "$pub_app" "${publish_args[@]}"
    dotnet publish "$SCRIPT_DIR/Skua.Manager.Avalonia/Skua.Manager.Avalonia.csproj" "${common_publish_flags[@]}" -o "$pub_mgr" "${publish_args[@]}"

    echo "--- Assembling application payload in $CURRENT_DIR ---"
    rm -rf "$CURRENT_DIR"
    mkdir -p "$CURRENT_DIR"
    mkdir -p "$CURRENT_DIR/plugins"
    mkdir -p "$CURRENT_DIR/FFDec"

    # Copy self-contained application and manager outputs
    cp -a "$pub_app/." "$CURRENT_DIR/"
    cp -a "$pub_mgr/." "$CURRENT_DIR/"
    rm -f "$CURRENT_DIR"/*.pdb
    rm -rf "$pub_app" "$pub_mgr"

    local required_outputs=(
        "$CURRENT_DIR/BonxieMux"
        "$CURRENT_DIR/BonxieMux.dll"
        "$CURRENT_DIR/BonxieMux.runtimeconfig.json"
        "$CURRENT_DIR/BonxieMux.Manager"
        "$CURRENT_DIR/BonxieMux.Manager.dll"
        "$CURRENT_DIR/BonxieMux.Manager.runtimeconfig.json"
        "$CURRENT_DIR/libcoreclr.so"
        "$SCRIPT_DIR/Skua.Plugin.DailyTracker/bin/Release/net10.0/Skua.Plugin.DailyTracker.dll"
        "$SCRIPT_DIR/Skua.Plugin.ButlerV5/bin/Release/net10.0/ButlerV5.dll"
        "$SCRIPT_DIR/Skua.Plugin.Cosmetics/bin/Release/net10.0/Skua.Plugin.Cosmetics.dll"
        "$SCRIPT_DIR/Skua.Plugin.PerfRecorder/bin/Release/net10.0/Skua.Plugin.PerfRecorder.dll"
        "$SCRIPT_DIR/Skua.Plugin.Omniscope/bin/Release/net10.0/Skua.Plugin.Omniscope.dll"
    )
    for req in "${required_outputs[@]}"; do
        if [[ ! -s "$req" ]]; then
            echo "Error: Required payload output is missing or empty: $req" >&2
            exit 1
        fi
    done

    # Copy JSON data assets
    [ -f "$APP_BIN/QuestData.json" ] && cp -f "$APP_BIN/QuestData.json" "$CURRENT_DIR/"
    [ -f "$APP_BIN/AdvancedSkills.json" ] && cp -f "$APP_BIN/AdvancedSkills.json" "$CURRENT_DIR/"
    [ -f "$SCRIPT_DIR/Skua.Core/ScriptDates.json" ] && cp -f "$SCRIPT_DIR/Skua.Core/ScriptDates.json" "$CURRENT_DIR/"

    # Copy plugins
    cp -f "$SCRIPT_DIR/Skua.Plugin.DailyTracker/bin/Release/net10.0"/Skua.Plugin.*.dll "$CURRENT_DIR/plugins/"
    cp -f "$SCRIPT_DIR/Skua.Plugin.ButlerV5/bin/Release/net10.0"/ButlerV5.dll "$CURRENT_DIR/plugins/"
    cp -f "$SCRIPT_DIR/Skua.Plugin.Cosmetics/bin/Release/net10.0"/Skua.Plugin.Cosmetics.dll "$CURRENT_DIR/plugins/"
    cp -f "$SCRIPT_DIR/Skua.Plugin.PerfRecorder/bin/Release/net10.0"/Skua.Plugin.PerfRecorder.dll "$CURRENT_DIR/plugins/"
    cp -f "$SCRIPT_DIR/Skua.Plugin.Omniscope/bin/Release/net10.0"/Skua.Plugin.Omniscope.dll "$CURRENT_DIR/plugins/"

    # Copy skua.swf
    cp -f "$SWF_SOURCE" "$CURRENT_DIR/skua.swf"

    # Copy embedded Wayland DMA-BUF host library
    cp -f "$wayland_host_so" "$CURRENT_DIR/libbonxie_wayland.so"

    # Copy FFDec
    if [[ ! -s "$SCRIPT_DIR/FFDec/ffdec.jar" || ! -x "$SCRIPT_DIR/FFDec/ffdec.sh" || ! -d "$SCRIPT_DIR/FFDec/lib" ]]; then
        echo "Error: Linux FFDec runtime is incomplete under $SCRIPT_DIR/FFDec" >&2
        exit 1
    fi
    cp -a "$SCRIPT_DIR/FFDec/." "$CURRENT_DIR/FFDec/"

    # Copy local or fetch fresh official Skua Mugetsu Flash runner
    mkdir -p "$CURRENT_DIR/player" "$CURRENT_DIR/player-x86-64-v2"
    if [[ -n "$local_player" && -x "$local_player/Mugetsu" ]]; then
        echo "Using local Mugetsu from $local_player..."
        cp -a "$local_player/." "$CURRENT_DIR/player/"
    else
        echo "Fetching official skua-mugetsu v${SKUA_PLAYER_VERSION} from GitHub Releases..."
        fetch_mugetsu_to_dir "$SKUA_PLAYER_URL" "$CURRENT_DIR/player"
    fi

    if [[ ! -s "$CURRENT_DIR/player/Mugetsu" ]]; then
        echo "Error: Mugetsu executable not found in $CURRENT_DIR/player." >&2
        exit 1
    fi
    chmod +x "$CURRENT_DIR/player/Mugetsu"

    # Bundle x86-64-v2 player when available
    if [[ -n "$local_player_v2" && -x "$local_player_v2/Mugetsu" ]]; then
        echo "Using local x86-64-v2 Mugetsu from $local_player_v2..."
        cp -a "$local_player_v2/." "$CURRENT_DIR/player-x86-64-v2/"
    elif fetch_mugetsu_to_dir "$SKUA_PLAYER_V2_URL" "$CURRENT_DIR/player-x86-64-v2" 2>/dev/null; then
        :
    fi

    if [[ -s "$CURRENT_DIR/player-x86-64-v2/Mugetsu" ]]; then
        chmod +x "$CURRENT_DIR/player-x86-64-v2/Mugetsu"
        echo "Bundled x86-64-v2 Mugetsu player."
    else
        rmdir "$CURRENT_DIR/player-x86-64-v2" 2>/dev/null || true
        echo "No x86-64-v2 Mugetsu asset is available; packaging standard player only."
    fi

    chmod +x "$CURRENT_DIR/BonxieMux" "$CURRENT_DIR/BonxieMux.Manager"
}
