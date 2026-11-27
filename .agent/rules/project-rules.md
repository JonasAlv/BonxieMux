# BonxieMux Agent & Contributor Rules

All contributors and AI agents (Antigravity / Gemini, Claude Code, Codex, Cursor) working in this repository must follow these rules.

## 1. Three Primary Engineering Rules

1. **Replicability**
   - Keep builds reproducible on any standard Linux distro: zero hardcoded user/machine paths, `--locked` Cargo builds (`Skua.WaylandHost`), and `Directory.Build.props` as the single source of version truth.
   - Maintain strict architectural boundaries (enforced at compile time in `Directory.Build.props`):
     - `Skua.Backend` is **100% UI-free** (no Avalonia packages or `*.Avalonia` project references).
     - `Skua.Shared.Avalonia` must never reference `Skua.App.Avalonia` or `Skua.Manager.Avalonia`.
     - `Skua.App.Avalonia` and `Skua.Manager.Avalonia` must never reference each other.
   - Keep all plugins (`Skua.Plugin.*`) **standalone** (self-contained inside their own project), while cleanly separating internal backend services (`Services/`) from Avalonia `Window` code-behind.
2. **VS Code UI/UX Guidelines & `microsoft/vscode` `main` Values**
   - Follow [ui-ux.md](ui-ux.md) for all UI work: exact Dark Modern / Light Modern hexes, Compact vs. Comfortable density metrics, official Microsoft VS Code **Codicons** (`<icons:Codicon Kind="..." />`, backed by `Skua.Shared.Avalonia/Assets/codicon.ttf` — never `Material.Icons`), monochrome status bar items (color strictly reserved for genuine warning/error alerts like Watchdog `"Help!"`), and zero hardcoded UI tokens.
   - All new or modified UI tokens must stay in 1:1 parity across `Tokens.axaml`, `Colors.axaml`, and `UiTokens.cs`.
3. **No Fallbacks**
   - BonxieMux is in active pre-release development — never add or keep compatibility shims, legacy lookup paths, dual-format loaders, or fallback branches for prototype/superseded code.
   - When replacing an implementation, layout, transport, or build flag, delete the old path completely and fail fast with a clear error if a required dependency or asset is missing.
   - Only keep explicit runtime branching when required by actively supported targets (e.g., `x86-64-v2` vs. `x86-64-v3` CPU builds).

## 2. Batched Change & Verification Workflow

For any multi-project UI or architecture change, work **bottom-up by dependency order** in focused batches—verifying each batch with `lint-ui.py` and a targeted project build before moving to the next:

1. **Batch 1 — `Skua.Shared.Avalonia`** *(Foundation first: tokens, styles, `Codicon`, shared shell/primitives — prevents temporary shims in downstream projects)*
2. **Batch 2 — `Skua.App.Avalonia`** *(Main client shell, Activity Bar, Status Bar, views)*
3. **Batch 3 — `Skua.Manager.Avalonia`** *(Account manager, Army Queue, Scheduler, Launcher)*
4. **Batch 4 — Standalone Plugins (`Skua.Plugin.*`)** *(Each plugin individually or as a final pass: `ButlerV5`, `Cosmetics`, `DailyTracker`, `Omniscope`, `PerfRecorder`), followed by a full `Skua.sln` build*

*(For backend/core refactors, follow the same bottom-up order: `Skua.Core.*` → `Skua.Backend` → `Skua.App.Avalonia` / `Skua.Manager.Avalonia` / `Skua.Plugin.*`.)*

## 3. Runtime & Performance Invariants

- **Flash bridge:** Pure `AF_UNIX` + Linux `SO_PEERCRED` verification (`Skua.Backend/Flash/FlashUtil.cs`) — do not reintroduce TCP/procfs fallbacks.
- **Hidden & Headless tabs:** Must never significantly slow down or stall scripts or game actions. Mugetsu suspends GPU rendering via `SkuaVisible(false)` (`self.occluded = true`), `Skua.WaylandHost` releases `wl_buffer`s immediately on hidden tabs, and `ScriptOption.cs` keeps native 24 FPS AVM2 ticks (`Math.Min(normalFps, 24)`).
- **`skua-mugetsu` patches:** Permanent changes belong in `skua-mugetsu/source/patches/skua.patch`, never only in temporary `source/build/` folders.

## 4. Required Verification Before Finishing Changes

```bash
python3 scripts/lint-ui.py
cargo test --locked --manifest-path Skua.WaylandHost/Cargo.toml
dotnet build Skua.sln -c Release --no-restore -m:1
```

All checks must pass with **0 violations, 0 warnings, and 0 errors**.
