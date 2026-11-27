# BonxieMux UI/UX & Replicability Guidelines

This document is the canonical design and architecture specification for **BonxieMux** (and the UI parity reference for **Vesper**). It mirrors Microsoft's official **Visual Studio Code UX Guidelines** (`code.visualstudio.com/api/ux-guidelines`) and **`microsoft/vscode` `main` branch** visual tokens and density metrics.

---

## 1. Core Rules (Non-Negotiable)

1. **Replicability**
   - Never hardcode user- or machine-specific paths (`/home/...`, `/tmp/...` fallbacks without `$XDG_RUNTIME_DIR`).
   - All Rust builds and tests must use `--locked` against committed `Cargo.lock` files.
   - `Skua.Backend` must remain **100% UI-free** (enforced at compile time by `ValidateAvaloniaArchitecture` in `Directory.Build.props`).
   - `Skua.Shared.Avalonia` must never reference `Skua.App.Avalonia` or `Skua.Manager.Avalonia`.
   - All plugins (`Skua.Plugin.*`) must remain **standalone** (self-contained inside their own project), follow every UI rule here, and separate internal backend services (`Services/`) from Avalonia `Window` code-behind so every UI is easy to replicate.
2. **VS Code UI/UX Fidelity**
   - Every visual change must adhere to the VS Code component guidelines and exact `microsoft/vscode` `main` values below.
   - Never introduce hardcoded hex colors, font sizes, corner radii, heights, spacings, paddings, or raw resource string literals in views or C# UI code. Always use `Tokens.axaml`, `Colors.axaml`, and `UiTokens.cs`.
   - `python3 scripts/lint-ui.py` must pass with **0 violations** before any commit.

---

## 2. VS Code Component UX Guidelines

Based on `code.visualstudio.com/api/ux-guidelines`:

### Status Bar (`TabbedHostWindow.StatusBar.cs`, `TabbedHostWindow.axaml`)
- **Placement:** Left = workspace/global items (Butler, Watchdog); Right = active-tab contextual items (Location/Map, Script, Auto, Auto Headless, Grid Layout, Viewport Size).
- **Monochrome by default:** Never use custom green, yellow, amber, or accent colors on normal/routine status bar items (e.g. Auto Headless ON, Script Running/Paused, Auto Combat, Tab Loading). Use `SkuaTextBrush` (active) and `SkuaMutedTextBrush` (idle), conveying state through **icon choice + concise text**.
- **Color is strictly for warnings/errors:** Reserve `SkuaDangerBrush` / `SkuaWarningBrush` exclusively for genuine urgent alerts (e.g. Watchdog `"Help!"` or active rescue warning).
- **Concise & actionable:** Keep labels short, always provide a descriptive `ToolTip.Tip`, and make items clickable to open the relevant view or toggle the setting.

### Activity Bar & Side Bar (`Skua.Manager.Avalonia`, tool shells)
- Keep Activity Bar items purposeful, icon-led, and mapped to distinct views; mark the active item with `UiActiveBarThickness` (`2,0,0,0`) in `SkuaAccentBrush`.
- Limit Side Bar sections so views stay scannable without clutter or duplicated controls.

### Tabs & Title Bar
- Custom frameless title bar (`ExtendClientAreaToDecorationsHint="True"`) using `WindowTitleBar` and `UiTitleBarHeight`.
- Active tab uses `SkuaPageBrush` with `SkuaTextBrush`; inactive tabs use `SkuaSurfaceBrush` with `SkuaMutedTextBrush` and `SkuaNavHoverBrush` on hover.

### Menus & Context Menus
- Group related actions cleanly with separators; hide or disable irrelevant actions rather than flooding top-level menus.
- Every icon-only `<Button>` must have a `ToolTip.Tip`.

### Quick Picks / Command Palette (`QuickPickWindow`, `CommandPaletteWindow`)
- Single-input filterable list with clear icon metaphors, short labels, subtle descriptions (`SkuaMutedTextBrush`), and full keyboard navigation (`Up`/`Down`/`Enter`/`Esc`).

### Notifications & Dialogs
- Prefer non-intrusive status bar updates for routine background activity.
- Reserve modal dialogs (`DialogService`) for confirmations, destructive actions, or required user input.

### Icons & Typography (Mandatory: VS Code Codicons)
- **Icon pack:** Use **exclusively** official Microsoft VS Code **Codicons** (`Skua.Shared.Avalonia.Controls.Icons.Codicon` / `<icons:Codicon Kind="..." />`, backed by `Skua.Shared.Avalonia/Assets/codicon.ttf` and `CodiconKind.cs`).
- **Sizing:** Always bind `Width` and `Height` to `UiIconXs` (`12`), `UiIconSm` (`14`), `UiIconMd` (`16`), `UiIconLg` (`18`), or `UiActivityIconSize` (`18` Compact / `24` Comfortable). `Codicon` automatically syncs `FontSize`/`LineHeight` to `Width`/`Height` for live density switching.
- **Canonical shell icons:**
  - Window controls (`WindowTitleBar`): `ChromeMinimize`, `ChromeMaximize`, `ChromeRestore`, `ChromeClose`, `OpenInWindow`.
  - Activity Bar (`TabbedHostWindow`): Army (`Globe`), Grid View (`Layout`), Auto Combat (`Robot`), Fast Jump (`Rocket`), Bot Windows (`Extensions`), Script Repo (`SourceControl`), Settings/Themes (`SettingsGear`).
  - Status Bar (`TabbedHostWindow` / `Manager`): Butler (`Organization` / `ArrowRight` / `StarFull`), Watchdog (`Shield` / `Warning` / `CircleSlash`), Location (`Location`), Script (`FileCode` / `PlayCircle` / `DebugPause`), Auto (`Flame`), Headless (`EyeClosed`), Grid (`Layout`), Viewport (`ScreenFull`), Queue (`ListOrdered`), Updates (`Sync`).
- **Forbidden:** Never reintroduce `Material.Icons` / `Material.Icons.Avalonia`, never use raw `<PathIcon>` in AXAML, and never use decorative Unicode glyphs (`●`, `▸`, `★`, emojis) as UI icons.
- **Typography:** Leave `Window.FontFamily` at the system default (`fontconfig` sans-serif, matching VS Code's Linux `system-ui` behavior) and use `UiFontMono` for code, packets, logs, and IDs.

---

## 3. Exact `microsoft/vscode` `main` Theme Palette

Mapped 1:1 in `Skua.Shared.Avalonia/Theming/Colors.axaml` and `Skua.Shared.Avalonia/Services/VsCodeThemeLoader.cs`:

| Token (`UiTokens`) | VS Code `main` Key | Dark Modern (`dark_modern.json`) | Light Modern (`light_modern.json`) |
|---|---|---|---|
| `SkuaPageBrush` | `editor.background` | `#1F1F1F` | `#FFFFFF` |
| `SkuaSurfaceBrush` | `sideBar.background` / `titleBar.activeBackground` / `statusBar.background` | `#181818` | `#F8F8F8` |
| `SkuaSurfaceAltBrush` | `sideBarSectionHeader.background` / elevated surface | `#252526` | `#F3F3F3` |
| `SkuaSurfaceSubtleBrush` | `sideBar.border` / `titleBar.border` / `statusBar.border` / `tab.border` | `#2B2B2B` | `#EBEBEB` |
| `SkuaNavHoverBrush` | `list.hoverBackground` | `#2A2D2E` | `#E8E8E8` |
| `SkuaPressedBrush` | `list.inactiveSelectionBackground` | `#37373D` | `#D6D6D6` |
| `SkuaBorderBrush` | `input.border` / `dropdown.border` | `#3C3C3C` | `#CECECE` |
| `SkuaBorderStrongBrush` / `SkuaWindowBorderBrush` | `menu.border` / `widget.border` | `#454545` | `#B4B4B4` |
| `SkuaTextBrush` | `foreground` | `#CCCCCC` | `#1F1F1F` |
| `SkuaMutedTextBrush` | `descriptionForeground` | `#9D9D9D` | `#616161` |
| `SkuaAccentColor` / `SkuaAccentBrush` | `button.background` / `focusBorder` | `#0078D4` | `#005FB8` |
| `SkuaAccentHoverBrush` | `button.hoverBackground` | `#026EC1` | `#0258A8` |
| `SkuaAccentPressedBrush` | pressed primary button | `#005FB8` | `#004C94` |
| `SkuaDangerBrush` | `errorForeground` | `#F85149` | `#E51400` |
| `SkuaWarningBrush` | `editorWarning.foreground` | `#FBBF24` | `#B45309` |
| `SkuaSuccessBrush` | semantic success (non-status-bar) | `#34D399` | `#059669` |
| `SkuaScrollThumbBrush` | `scrollbarSlider.background` | `#424242` | `#C1C1C1` |

When `UseThemeAccentColor` is enabled, `ThemeResourceApplicator.ApplyAccentBrushes` must preserve `ActivePalette.AccentHover` (`#026EC1` / `#0258A8`) and `ActivePalette.AccentPressed` rather than computing a synthetic lighten/darken override.

---

## 4. Exact Compact vs. Comfortable Density Metrics

Defined in `Skua.Shared.Avalonia/Theming/Tokens.axaml` (Compact default) and switched dynamically in `ThemeResourceApplicator.ApplyUiDensity(app, comfortable)` to mirror VS Code's `window.density.layout` & `window.density.editorTabHeight`:

| Token (`UiTokens`) | Compact (Default) | Comfortable (VS Code Default) | VS Code `main` Reference |
|---|---|---|---|
| `UiTitleBarHeight` | `30` | `35` | `titlebarPart.ts` (`35px` default, `30px` compact) |
| `UiTabBarHeight` | `25` | `35` | `tabsTitleControl.ts` (`35px` default, `22–25px` compact) |
| `UiStatusBarHeight` | `22` | `22` | `statusbarPart.ts` (`22px` standard) |
| `UiActivityBarWidth` | `36` | `48` | `activitybarPart.ts` (`48px` default, `36px` compact) |
| `UiActivityItemSize` | `30` | `40` | Activity bar item hit target / active pill |
| `UiActivityIconSize` | `18` | `24` | `activitybarPart.ts` (`24px` default, `18px` compact) |
| `UiSidebarWidth` | `220` | `220` | Standard compact sidebar width |
| `UiControlHeight` | `28` | `32` | Input / button / combo row height |
| `UiSmallControlHeight` | `24` | `28` | Toolbar / tab-strip action buttons |
| `UiChipHeight` | `20` | `24` | Tag / filter chips |
| `UiMenuItemHeight` | `26` | `28` | Context & top-bar menu rows |
| `UiListItemMinHeight` | `26` | `28` | List / tree rows |
| `UiScrollbarSize` | `6` | `8` | Minimal workbench scrollbars |
| `UiFontMicro` | `10` | `10` | Badges / micro labels |
| `UiFontSmall` | `11` | `12` | Secondary / status bar text |
| `UiFontBody` | `12` | `13` | VS Code workbench UI font (`13px` default, `12px` compact) |
| `UiFontHeading` | `14` | `14` | Section headers |
| `UiFontDisplay` | `18` | `18` | Page / dialog titles |
| `UiGap2xs` / `Xs` / `Sm` / `Md` / `Base` / `Lg` / `Xl` | `2 / 4 / 6 / 8 / 10 / 12 / 16` | `4 / 6 / 8 / 10 / 12 / 16 / 20` | Consistent 2px/4px spacing scale |
| `UiPaddingXs` / `Sm` / `Card` / `CardCompact` / `Lg` | `4 / 6 / 10 / 12,10 / 12` | `6 / 8 / 14 / 14,12 / 16` | Container padding scale |
| `UiControlPadding` / `UiButtonPadding` | `10,0` / `8,0` | `12,0` / `12,0` | Horizontal control padding |
| `UiRadiusNone` / `UiRadiusControl` / `UiRadiusCard` | `0` / `4` / `6` | `0` / `4` / `6` | VS Code `border-radius: 4px` on controls |
| `UiBorderThin` / `UiFocusBorderThickness` / `UiActiveBarThickness` | `1` / `2` / `2,0,0,0` | `1` / `2` / `2,0,0,0` | 1px hairlines, 2px focus/active bar |

---

## 5. Batched UI Rollout & Automated Enforcement (`scripts/lint-ui.py`)

Apply cross-project UI changes in **bottom-up dependency batches**, verifying each batch before moving to the next:
1. `Skua.Shared.Avalonia` (tokens, styles, shared controls, `Codicon`)
2. `Skua.App.Avalonia`
3. `Skua.Manager.Avalonia`
4. `Skua.Plugin.*` (`ButlerV5`, `Cosmetics`, `DailyTracker`, `Omniscope`, `PerfRecorder`), followed by a full `Skua.sln` build

Run after every batch:

```bash
python3 scripts/lint-ui.py
```

All 18 checks must pass with **0 violations** (broken/unused tokens, unused CSS classes, no `<PathIcon>`, no hardcoded colors/fonts/radii/heights/spacing/padding/Codicon sizes in AXAML or C# UI, tooltips on icon-only buttons, no code-only plugin Windows, no raw resource string literals, 1:1 `UiTokens.cs` parity).
