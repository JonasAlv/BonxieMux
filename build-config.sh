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

# Remote URLs & Cache Paths based strictly on property values
SKUA_PLAYER_CACHE="$SCRIPT_DIR/player/${SKUA_PLAYER_VERSION}-${SKUA_PLAYER_OS}"
SKUA_PLAYER_URL="https://github.com/retr0gr4d3/skua-mugetsu/releases/download/v${SKUA_PLAYER_VERSION}/linux.tar.gz"

SKUA_PLAYER_V2_CACHE="$SCRIPT_DIR/player/${SKUA_PLAYER_VERSION}-${SKUA_PLAYER_OS}-x86-64-v2"
SKUA_PLAYER_V2_URL="https://github.com/retr0gr4d3/skua-mugetsu/releases/download/v${SKUA_PLAYER_VERSION}/linux-x86-64-v2.tar.gz"

# Shared function to assemble application payload into $CURRENT_DIR
assemble_payload() {
    local local_player="${SKUA_PLAYER_LOCAL:-}"
    local local_player_v2="${SKUA_PLAYER_V2_LOCAL:-}"

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

    echo "--- Building .NET solution (Release) ---"
    rm -rf "$APP_BIN/player" "$APP_BIN/player-x86-64-v2"
    local dotnet_args=("$SCRIPT_DIR/Skua.sln" -c Release)
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
    dotnet build "${dotnet_args[@]}"

    echo "--- Publishing self-contained .NET runtime (linux-x64) ---"
    local pub_app="$BUILD_DIR/.publish_app"
    local pub_mgr="$BUILD_DIR/.publish_mgr"
    rm -rf "$pub_app" "$pub_mgr"
    mkdir -p "$pub_app" "$pub_mgr"

    dotnet publish "$SCRIPT_DIR/Skua.App.Avalonia/Skua.App.Avalonia.csproj" -c Release -r linux-x64 --self-contained -o "$pub_app"
    dotnet publish "$SCRIPT_DIR/Skua.Manager.Avalonia/Skua.Manager.Avalonia.csproj" -c Release -r linux-x64 --self-contained -o "$pub_mgr"

    echo "--- Assembling application payload in $CURRENT_DIR ---"
    rm -rf "$CURRENT_DIR"
    mkdir -p "$CURRENT_DIR"
    mkdir -p "$CURRENT_DIR/Assemblies/Skills"
    mkdir -p "$CURRENT_DIR/Assemblies/runtimes/linux-x64/native"
    mkdir -p "$CURRENT_DIR/plugins"
    mkdir -p "$CURRENT_DIR/FFDec"

    # Copy self-contained application and manager outputs
    cp -a "$pub_app/." "$CURRENT_DIR/"
    cp -a "$pub_mgr/." "$CURRENT_DIR/"
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

    # Copy all dependent DLLs to Assemblies/
    cp -f "$CURRENT_DIR"/*.dll "$CURRENT_DIR/Assemblies/"
    rm -f "$CURRENT_DIR/Assemblies/BonxieMux.dll" "$CURRENT_DIR/Assemblies/BonxieMux.Manager.dll"

    # Copy runtimes native libraries
    if [ -d "$CURRENT_DIR/runtimes" ]; then
        cp -rf "$CURRENT_DIR/runtimes" "$CURRENT_DIR/Assemblies/"
    fi

    # Copy plugins
    cp -f "$SCRIPT_DIR/Skua.Plugin.DailyTracker/bin/Release/net10.0"/Skua.Plugin.*.dll "$CURRENT_DIR/plugins/"
    cp -f "$SCRIPT_DIR/Skua.Plugin.ButlerV5/bin/Release/net10.0"/ButlerV5.dll "$CURRENT_DIR/plugins/"
    cp -f "$SCRIPT_DIR/Skua.Plugin.Cosmetics/bin/Release/net10.0"/Skua.Plugin.Cosmetics.dll "$CURRENT_DIR/plugins/"

    # Copy skua.swf
    cp -f "$SWF_SOURCE" "$CURRENT_DIR/skua.swf"

    # Copy FFDec
    if [[ ! -s "$SCRIPT_DIR/FFDec/ffdec.jar" || ! -x "$SCRIPT_DIR/FFDec/ffdec.sh" || ! -d "$SCRIPT_DIR/FFDec/lib" ]]; then
        echo "Error: Linux FFDec runtime is incomplete under $SCRIPT_DIR/FFDec" >&2
        exit 1
    fi
    cp -a "$SCRIPT_DIR/FFDec/." "$CURRENT_DIR/FFDec/"

    # Copy or download official Skua Mugetsu Flash runner
    mkdir -p "$CURRENT_DIR/player" "$CURRENT_DIR/player-x86-64-v2"
    if [[ -n "$local_player" && -x "$local_player/Mugetsu" ]]; then
        echo "Using local Mugetsu from $local_player..."
        cp -a "$local_player/." "$CURRENT_DIR/player/"
    elif [[ -s "$APP_BIN/player/Mugetsu" ]]; then
        echo "Using bundled skua-mugetsu from $APP_BIN/player..."
        cp -a "$APP_BIN/player/." "$CURRENT_DIR/player/"
    elif [[ -s "$SKUA_PLAYER_CACHE/Mugetsu/Mugetsu" ]]; then
        echo "Using cached skua-mugetsu from player/..."
        cp -a "$SKUA_PLAYER_CACHE/Mugetsu/." "$CURRENT_DIR/player/"
    else
        echo "Mugetsu binary not found locally. Fetching official skua-mugetsu v${SKUA_PLAYER_VERSION} from GitHub Releases..."
        local tmp_archive="$CURRENT_DIR/skua-mugetsu.tar.gz"
        curl -sSL --fail "$SKUA_PLAYER_URL" -o "$tmp_archive"
        tar -xzf "$tmp_archive" -C "$CURRENT_DIR/player/"
        if [[ -d "$CURRENT_DIR/player/Mugetsu" ]]; then
            cp -a "$CURRENT_DIR/player/Mugetsu/." "$CURRENT_DIR/player/"
            rm -rf "$CURRENT_DIR/player/Mugetsu"
        fi
        rm -f "$tmp_archive"
    fi

    if [[ ! -s "$CURRENT_DIR/player/Mugetsu" ]]; then
        echo "Error: Mugetsu executable not found in $CURRENT_DIR/player." >&2
        exit 1
    fi
    cp -f "$CURRENT_DIR/player/Mugetsu" "$CURRENT_DIR/Mugetsu"
    chmod +x "$CURRENT_DIR/player/Mugetsu" "$CURRENT_DIR/Mugetsu"

    # Bundle x86-64-v2 player when available
    if [[ -n "$local_player_v2" && -x "$local_player_v2/Mugetsu" ]]; then
        echo "Using local x86-64-v2 Mugetsu from $local_player_v2..."
        cp -a "$local_player_v2/." "$CURRENT_DIR/player-x86-64-v2/"
    elif [[ -s "$APP_BIN/player-x86-64-v2/Mugetsu" ]]; then
        cp -a "$APP_BIN/player-x86-64-v2/." "$CURRENT_DIR/player-x86-64-v2/"
    elif [[ -s "$SKUA_PLAYER_V2_CACHE/Mugetsu/Mugetsu" ]]; then
        cp -a "$SKUA_PLAYER_V2_CACHE/Mugetsu/." "$CURRENT_DIR/player-x86-64-v2/"
    elif curl -sSL --fail "$SKUA_PLAYER_V2_URL" -o "$CURRENT_DIR/skua-mugetsu-v2.tar.gz" 2>/dev/null; then
        tar -xzf "$CURRENT_DIR/skua-mugetsu-v2.tar.gz" -C "$CURRENT_DIR/player-x86-64-v2/"
        if [[ -d "$CURRENT_DIR/player-x86-64-v2/Mugetsu" ]]; then
            cp -a "$CURRENT_DIR/player-x86-64-v2/Mugetsu/." "$CURRENT_DIR/player-x86-64-v2/"
            rm -rf "$CURRENT_DIR/player-x86-64-v2/Mugetsu"
        fi
        rm -f "$CURRENT_DIR/skua-mugetsu-v2.tar.gz"
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
