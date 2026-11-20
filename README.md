# BonxieMux

**BonxieMux** is a native Linux focused AdventureQuest Worlds (AQW) bot and multi-account tabbed client built with .NET 10 and Avalonia. It embeds the [skua-mugetsu](https://github.com/retr0gr4d3/skua-mugetsu) Flash runner via a Rust X11/XWayland host (`Skua.X11Host`) (native Wayland host and implementation in `wayland` branch) and includes **BonxieMux Manager** for managing accounts, scripts, and updates.
It's based on [VibeSkua](https://github.com/NinjaXz/VibeSkua) and [Skua](https://github.com/auqw/skua)

---

## Linux Development Prerequisites

Install these tools using your distribution's package manager or official installers:

### Required
- **.NET 10 SDK** (`dotnet` 10.0)
- **Rust toolchain** (`cargo` and `rustc`) — builds `Skua.X11Host` (`libbonxie_x11.so`).
- **Archive & download tools**: `curl`, `tar`, and `zip` (or `bsdtar`)

### Optional
- **`gcc`** — only needed for `./build-binaries.sh` (compiles native launcher wrappers)
- **`python3` + `Pillow`** — only needed for multi-resolution icon generation in `./build-appimages.sh` (falls back to raw icon copy if missing)
- **`appimagetool`** — `./build-appimages.sh` downloads it automatically into `Build/tools/` if not in `PATH`
- **Apache Flex SDK (`mxmlc`)** — only needed if you modify `Skua.AS3/` (a prebuilt `skua.swf` is already included)
- **Java Runtime (JRE)** — only needed at runtime to use the bundled `FFDec` SWF tools

---

## Building from Source

1. **Build the native X11 host (`libbonxie_x11.so`):**
   ```bash
   cargo build --release --manifest-path Skua.X11Host/Cargo.toml
   ```

2. **Build the .NET application:**
   ```bash
   dotnet build Skua.App.Avalonia/Skua.App.Avalonia.csproj -c Release
   ```
   The first build automatically downloads the `Mugetsu` player into `player/`.  
   To use a local `Mugetsu` build instead:
   ```bash
   dotnet build Skua.App.Avalonia/Skua.App.Avalonia.csproj -c Release -p:SkuaPlayerLocal=/path/to/mugetsu-folder
   ```

---

## Packaging (AppImages & Portable Binaries)

- **Build standalone AppImages** (outputs to `Build/`):
  ```bash
  ./build-appimages.sh
  ```

- **Build portable Linux binaries** (outputs to `Build/`):
  ```bash
  ./build-binaries.sh
  ```

Both packaging scripts also accept `--local-mugetsu /path/to/mugetsu-folder` (and `--local-mugetsu-v2 /path/to/mugetsu-v2-folder`) if you want to bundle a locally built player.
You can also copy-paste your skua-mugetsu binary inside the player folder in `Skua.App.Avalonia/bin/Release/net10.0/player/` if you are testing(create the `player` folder if it does not exist).
