# BonxieMux

**BonxieMux** is a Wayland-native Linux AdventureQuest Worlds (AQW) bot and multi-account tabbed client built with .NET 10 and Avalonia. It embeds the Wayland [skua-mugetsu](https://github.com/JonasAlv/skua-mugetsu) Flash runner (forked from [retr0gr4d3/skua-mugetsu](https://github.com/retr0gr4d3/skua-mugetsu)) via a zero-copy Rust Wayland DMA-BUF/EGL host (`Skua.WaylandHost`) and includes **BonxieMux Manager** for managing accounts, army queues, scheduled tasks, scripts, themes, and updates.

> For legacy X11/XWayland support, see the `x11` branch.

Based on [VibeSkua](https://github.com/NinjaXz/VibeSkua) and [Skua](https://github.com/auqw/skua).

---

## Runtime Requirements

- **Linux (`x86_64`)** running a **Wayland** session/compositor
- **EGL / DMA-BUF GPU support** (`libEGL.so.1`)

---

## Linux Development Prerequisites

Install these tools using your distribution's package manager or official installers:

### Required
- **.NET 10 SDK** (`dotnet` 10.0)
- **Rust toolchain & C linker** (`cargo`, `rustc`, `gcc`) — builds `Skua.WaylandHost` (`libbonxie_wayland.so` and `bonxie-launcher`)
- **Archive & download tools**: `curl`, `tar`, `gzip`, and `zip` (or `bsdtar`)

### Optional
- **`python3` + `Pillow`** — only needed for multi-resolution icon generation in `./build-appimages.sh` (falls back to raw icon copy if missing)
- **`appimagetool`** — `./build-appimages.sh` downloads it automatically into `Build/tools/` if not in `PATH`
- **Apache Flex SDK (`mxmlc`)** — only needed if you modify `Skua.AS3/` (a prebuilt `skua.swf` is already included)
- **Java Runtime (JRE)** — only needed at runtime to use the bundled `FFDec` SWF tools

---

## Building from Source

1. **Build the native Wayland DMA-BUF host & launcher (`libbonxie_wayland.so` & `bonxie-launcher`):**
   ```bash
   cargo build --release --locked --manifest-path Skua.WaylandHost/Cargo.toml
   ```

2. **Build the .NET solution:**
   ```bash
   dotnet build Skua.sln -c Release
   ```
   The first build automatically downloads the Wayland `Mugetsu` player (`x86-64-v3` plus `x86-64-v2` fallback for CPUs without AVX2) into `player/`.  
   To use a local `Mugetsu` build instead:
   ```bash
   dotnet build Skua.sln -c Release -p:SkuaPlayerLocal=/path/to/mugetsu-folder
   ```

---

## Packaging (AppImages & Portable Binaries)

- **Build standalone AppImages** (outputs `BonxieMux` and `BonxieMux-Manager` AppImages + archives to `Build/`):
  ```bash
  ./build-appimages.sh
  ```

- **Build portable Linux binaries** (outputs portable folder + `.zip` / `.tar.gz` to `Build/`):
  ```bash
  ./build-binaries.sh
  ```

Both packaging scripts automatically build `Skua.WaylandHost` and the .NET solution if needed, and accept `--local-mugetsu /path/to/mugetsu-folder` (and `--local-mugetsu-v2 /path/to/mugetsu-v2-folder`) to bundle a locally built player.

For quick local testing, you can also copy your `Mugetsu` binary into `Skua.App.Avalonia/bin/Release/net10.0/player/` (create the `player` folder if it does not exist).

---

## Contributing & AI Assistants

The markdown files under [`.agent/rules/`](.agent/rules/) ([`project-rules.md`](.agent/rules/project-rules.md) and [`ui-ux.md`](.agent/rules/ui-ux.md)) define the project's replicability, architecture, and VS Code UI/UX guidelines for contributors and AI coding assistants to follow.

