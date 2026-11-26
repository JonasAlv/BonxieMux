#!/usr/bin/env python3
"""BonxieMux UI/UX Consistency Linter.

Enforces VS Code Compact Mode design-system invariants across all 14 Skua.* projects.
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SHARED = ROOT / "Skua.Shared.Avalonia"
TOKENS_FILE = SHARED / "Theming" / "Tokens.axaml"
COLORS_FILE = SHARED / "Theming" / "Colors.axaml"
APPLICATOR_FILE = SHARED / "Services" / "ThemeResourceApplicator.cs"
STYLES_DIR = SHARED / "Theming" / "Styles"
UITOKENS_FILE = SHARED / "Theming" / "UiTokens.cs"

SKIP_DIRS = {"bin", "obj", ".git", "Build", "packages", "TestResults"}
COMMENT_RE = re.compile(r"<!--.*?-->", re.DOTALL)


def iter_project_files(ext: str) -> list[Path]:
    files: list[Path] = []
    for proj in sorted(ROOT.glob("Skua.*")):
        if not proj.is_dir():
            continue
        for p in proj.rglob(f"*{ext}"):
            if any(part in SKIP_DIRS for part in p.parts):
                continue
            files.append(p)
    return sorted(files)


def strip_xml_comments(text: str) -> str:
    return COMMENT_RE.sub("", text)


def rel(p: Path) -> str:
    return str(p.relative_to(ROOT))


def collect_defined_keys() -> tuple[set[str], set[str], set[str]]:
    tokens_text = strip_xml_comments(TOKENS_FILE.read_text(encoding="utf-8"))
    colors_text = strip_xml_comments(COLORS_FILE.read_text(encoding="utf-8"))
    app_text = APPLICATOR_FILE.read_text(encoding="utf-8")

    token_keys = set(re.findall(r'x:Key="(Ui[A-Za-z0-9]+)"', tokens_text))
    color_keys = set(re.findall(r'x:Key="(Skua[A-Za-z0-9]+)"', colors_text))

    dynamic_keys: set[str] = set()
    for m in re.finditer(
        r'(?:SetOrUpdateBrush|SetOrUpdateColor|SetOrUpdateThemeBrush|SetColorResource)\([^;]*?"((?:Skua|Ui)[A-Za-z0-9]+)"',
        app_text,
    ):
        key = m.group(1)
        dynamic_keys.add(key)
        if key.endswith("Color"):
            dynamic_keys.add(key[: -len("Color")] + "Brush")
    # ThemeResourceApplicator also assigns app.Resources["Ui..."] directly
    for m in re.finditer(r'res\["(Ui[A-Za-z0-9]+)"\]', app_text):
        dynamic_keys.add(m.group(1))

    return token_keys, color_keys, token_keys | color_keys | dynamic_keys


def main() -> int:
    axaml_files = iter_project_files(".axaml")
    cs_files = iter_project_files(".cs")

    axaml_cache = {p: strip_xml_comments(p.read_text(encoding="utf-8")) for p in axaml_files}
    cs_cache = {p: p.read_text(encoding="utf-8") for p in cs_files}

    token_keys, color_keys, all_defined_keys = collect_defined_keys()

    results: list[tuple[str, list[str]]] = []

    # 1. No broken Ui*/Skua* DynamicResource keys
    broken_res: list[str] = []
    for p, text in axaml_cache.items():
        for i, line in enumerate(text.splitlines(), 1):
            for key in re.findall(r"\{DynamicResource\s+((?:Ui|Skua)[A-Za-z0-9]+)\}", line):
                if key not in all_defined_keys:
                    broken_res.append(f"{rel(p)}:{i}: unknown resource '{key}'")
    results.append(("No broken Ui*/Skua* DynamicResource keys", broken_res))

    # 2. No unused Tokens.axaml or Skua* Colors.axaml keys
    search_texts = [
        text
        for p, text in {**axaml_cache, **cs_cache}.items()
        if p not in {TOKENS_FILE, COLORS_FILE, UITOKENS_FILE}
    ]
    joined_search = "\n".join(search_texts)
    unused_tokens: list[str] = []
    for key in sorted(token_keys | color_keys):
        if not re.search(rf"\b{re.escape(key)}\b", joined_search):
            src = "Tokens.axaml" if key in token_keys else "Colors.axaml"
            unused_tokens.append(f"{src}: unused key '{key}'")
    results.append(("No unused Tokens.axaml / Skua* Colors.axaml keys", unused_tokens))

    # 3. No unused CSS classes in Theming/Styles/*.axaml
    style_files = sorted(STYLES_DIR.glob("*.axaml"))
    defined_classes: dict[str, Path] = {}
    for sf in style_files:
        text = axaml_cache[sf]
        for sel in re.findall(r'<Style\s+Selector="([^"]+)"', text):
            for part in sel.split(","):
                for cls in re.findall(r"(?<![A-Za-z0-9_-])\.([a-z][a-z0-9_-]*)", part):
                    # Ignore pseudo-classes or template part names if any
                    defined_classes.setdefault(cls, sf)

    non_style_texts = [
        text
        for p, text in {**axaml_cache, **cs_cache}.items()
        if STYLES_DIR not in p.parents
    ]
    joined_non_style = "\n".join(non_style_texts)
    unused_classes: list[str] = []
    for cls, sf in sorted(defined_classes.items()):
        # Referenced in AXAML Classes="..." or C# .Classes.Add("...") / .Set("...") / "cls"
        if not re.search(rf'(?:["\'\s]){re.escape(cls)}(?:["\'\s])', joined_non_style):
            unused_classes.append(f"{rel(sf)}: unused class '.{cls}'")
    results.append(("No unused CSS classes in Theming/Styles", unused_classes))

    # 4. No empty style files in Theming/Styles/*.axaml
    empty_styles: list[str] = []
    for sf in style_files:
        if "<Style " not in axaml_cache[sf] and "<Style>" not in axaml_cache[sf]:
            empty_styles.append(f"{rel(sf)}: contains 0 <Style> rules")
    results.append(("No empty style files in Theming/Styles", empty_styles))

    # 5. No raw <PathIcon in .axaml
    path_icons: list[str] = []
    for p, text in axaml_cache.items():
        for i, line in enumerate(text.splitlines(), 1):
            if "<PathIcon" in line:
                path_icons.append(f"{rel(p)}:{i}: raw <PathIcon> found")
    results.append(("No raw <PathIcon> in AXAML", path_icons))

    # 6. No obsolete Watermark= in .axaml
    watermarks: list[str] = []
    for p, text in axaml_cache.items():
        for i, line in enumerate(text.splitlines(), 1):
            if "Watermark=" in line:
                watermarks.append(f"{rel(p)}:{i}: obsolete Watermark= found")
    results.append(("No obsolete Watermark= in AXAML", watermarks))

    # 7. No hardcoded hex colors in view/control/plugin .axaml
    hex_colors: list[str] = []
    for p, text in axaml_cache.items():
        if p == COLORS_FILE or STYLES_DIR in p.parents:
            continue
        for i, line in enumerate(text.splitlines(), 1):
            for m in re.finditer(r'"(#[0-9A-Fa-f]{6,8})"', line):
                hex_colors.append(f"{rel(p)}:{i}: hardcoded hex color {m.group(1)}")
    results.append(("No hardcoded hex colors in view AXAML", hex_colors))

    # 8. No hardcoded monospace FontFamily, BorderThickness="1", FontSize="<num>", CornerRadius="4|6|8" in view AXAML
    hardcoded_tokens: list[str] = []
    for p, text in axaml_cache.items():
        if p in {TOKENS_FILE, COLORS_FILE} or STYLES_DIR in p.parents:
            continue
        for i, line in enumerate(text.splitlines(), 1):
            if re.search(r'FontFamily="[^"]*(?:Consolas|JetBrains Mono|Courier)[^"]*"', line):
                hardcoded_tokens.append(f"{rel(p)}:{i}: hardcoded monospace FontFamily (use UiFontMono)")
            if re.search(r'BorderThickness="1"', line):
                hardcoded_tokens.append(f"{rel(p)}:{i}: hardcoded BorderThickness=\"1\" (use UiBorderThin)")
            if re.search(r'FontSize="\d+(?:\.\d+)?"', line):
                hardcoded_tokens.append(f"{rel(p)}:{i}: hardcoded FontSize (use UiFont* token)")
            if re.search(r'CornerRadius="(?:4|6|8)"', line):
                hardcoded_tokens.append(f"{rel(p)}:{i}: hardcoded CornerRadius (use UiRadiusControl/UiRadiusCard)")
    results.append(("No hardcoded FontFamily/BorderThickness/FontSize/CornerRadius in view AXAML", hardcoded_tokens))

    # 9. No icon-only <Button> missing ToolTip.Tip
    icon_btn_no_tip: list[str] = []
    btn_re = re.compile(r"<Button\b([^>]*)>(.*?)</Button>", re.DOTALL)
    for p, text in axaml_cache.items():
        if STYLES_DIR in p.parents:
            continue
        for m in btn_re.finditer(text):
            attrs, body = m.group(1), m.group(2)
            if "MaterialIcon" in body and "TextBlock" not in body and 'Content="' not in attrs:
                if "ToolTip.Tip=" not in attrs and "<ToolTip.Tip>" not in body:
                    line_no = text[: m.start()].count("\n") + 1
                    icon_btn_no_tip.append(f"{rel(p)}:{line_no}: icon-only <Button> missing ToolTip.Tip")
    results.append(("No icon-only <Button> missing ToolTip.Tip", icon_btn_no_tip))

    # 10. No code-only standalone Window subclasses in Skua.Plugin.*
    code_only_windows: list[str] = []
    for p, text in cs_cache.items():
        if not any(part.startswith("Skua.Plugin.") for part in p.parts):
            continue
        if p.name.endswith(".axaml.cs"):
            continue
        # Allow partial code-behind splits (e.g. FooWindow.Part.cs) when FooWindow.axaml exists
        base_stem = p.name.split(".", 1)[0]
        if (p.parent / f"{base_stem}.axaml").exists():
            continue
        for i, line in enumerate(text.splitlines(), 1):
            if re.search(r"\bclass\s+\w+\s*:\s*(?:global::Avalonia\.Controls\.)?Window\b", line):
                code_only_windows.append(f"{rel(p)}:{i}: code-only Window subclass")
    results.append(("No code-only Window subclasses in plugins", code_only_windows))

    # 11. No hardcoded FontSize = <num> in C# UI files
    cs_font_sizes: list[str] = []
    ui_prefixes = ("Skua.App.Avalonia", "Skua.Manager.Avalonia", "Skua.Shared.Avalonia", "Skua.Plugin.")
    for p, text in cs_cache.items():
        r = rel(p)
        if not r.startswith(ui_prefixes):
            continue
        for i, line in enumerate(text.splitlines(), 1):
            if re.search(r"\bFontSize\s*=\s*\d+", line):
                cs_font_sizes.append(f"{r}:{i}: hardcoded C# FontSize (use UiTokens.Bind)")
    results.append(("No hardcoded FontSize = N in C# UI", cs_font_sizes))

    # 12. No non-fallback hardcoded Color.Parse / Brushes.* in C# UI files
    cs_brushes: list[str] = []
    for p, text in cs_cache.items():
        r = rel(p)
        if not r.startswith(ui_prefixes):
            continue
        if any(x in r for x in ("Theme", "Color", "Cosmetics", "FollowGroup", "WindowResize")):
            continue
        for i, line in enumerate(text.splitlines(), 1):
            if "UiTokens.GetBrush" in line:
                continue
            if re.search(r'Color\.Parse\("|Brushes\.(?!Transparent)[A-Z]', line):
                cs_brushes.append(f"{r}:{i}: hardcoded C# Color/Brush (use UiTokens)")
    results.append(("No non-fallback hardcoded C# Color/Brush", cs_brushes))

    # 13. No decorative Unicode glyphs in view AXAML
    unicode_glyphs: list[str] = []
    for p, text in axaml_cache.items():
        for i, line in enumerate(text.splitlines(), 1):
            if re.search(r"[✦◉◈♥⌖⌁★↑↓→✓✔✕✖●○◆◇]", line):
                unicode_glyphs.append(f"{rel(p)}:{i}: decorative Unicode glyph in AXAML")
    results.append(("No decorative Unicode glyphs in AXAML", unicode_glyphs))

    # 14. No hardcoded non-zero CornerRadius, interactive Height (24-38), or standard Spacing in view AXAML
    view_metrics: list[str] = []
    for p, text in axaml_cache.items():
        if p in {TOKENS_FILE, COLORS_FILE} or STYLES_DIR in p.parents:
            continue
        r = rel(p)
        for i, line in enumerate(text.splitlines(), 1):
            if re.search(r'CornerRadius="\d', line) and not re.search(r'CornerRadius="0"', line):
                view_metrics.append(f"{r}:{i}: hardcoded non-zero CornerRadius")
            if re.search(r'<(?:Button|TextBox|ComboBox|NumericUpDown|ToggleButton)\b[^>]*Height="(?:24|26|28|30|32|34|36|38)"', line):
                view_metrics.append(f"{r}:{i}: hardcoded interactive control Height (use UiControlHeight)")
            if re.search(r'\b(?:Spacing|RowSpacing|ColumnSpacing)="(?:[1-9]\d*)"', line):
                view_metrics.append(f"{r}:{i}: hardcoded non-zero Spacing (use UiGap* token)")
            if re.search(r'\bPadding="(?:4|6|10|12|12,10|10,0|8,0)"', line):
                view_metrics.append(f"{r}:{i}: hardcoded token-matching Padding (use Ui*Padding token)")
    results.append(("No hardcoded CornerRadius/Height/Spacing/Padding in view AXAML", view_metrics))

    # 15. No hardcoded MaterialIcon Width/Height in view AXAML
    icon_sizes: list[str] = []
    for p, text in axaml_cache.items():
        if p in {TOKENS_FILE, COLORS_FILE} or STYLES_DIR in p.parents:
            continue
        r = rel(p)
        for i, line in enumerate(text.splitlines(), 1):
            if "MaterialIcon" in line and re.search(r'(?:Width|Height)="(?:10|12|14|16|18|20|22|24)"', line):
                icon_sizes.append(f"{r}:{i}: hardcoded MaterialIcon size (use UiIcon* token)")
    results.append(("No hardcoded MaterialIcon Width/Height in view AXAML", icon_sizes))

    # 16. No non-fallback new CornerRadius(4|6|8) in C# UI files
    cs_cr: list[str] = []
    for p, text in cs_cache.items():
        r = rel(p)
        if not r.startswith(ui_prefixes) or "Theming/" in r:
            continue
        for i, line in enumerate(text.splitlines(), 1):
            if "UiTokens.GetCornerRadius" in line:
                continue
            if re.search(r"new\s+CornerRadius\((?:4|6|8)\)", line):
                cs_cr.append(f"{r}:{i}: hardcoded new CornerRadius (use UiTokens.Bind)")
    results.append(("No non-fallback new CornerRadius(4|6|8) in C# UI", cs_cr))

    # 17. No raw DynamicResourceExtension("Ui...|Skua...") string literals in C# UI files
    raw_dyn_res: list[str] = []
    for p, text in cs_cache.items():
        r = rel(p)
        if not r.startswith(ui_prefixes) or p in {UITOKENS_FILE, APPLICATOR_FILE}:
            continue
        for i, line in enumerate(text.splitlines(), 1):
            if re.search(r'(?:DynamicResourceExtension|TryFindResource|TryGetResource|FindResource)\s*\(\s*"(?:Ui|Skua)[A-Za-z0-9]+"', line):
                raw_dyn_res.append(f"{r}:{i}: raw resource string literal (use UiTokens.*)")
    results.append(("No raw resource string literals in C# UI", raw_dyn_res))

    # 18. UiTokens.cs 1:1 parity with Tokens.axaml + Colors.axaml
    uitokens_text = UITOKENS_FILE.read_text(encoding="utf-8")
    cs_tokens = set(re.findall(r"public const string ((?:Ui|Skua)[A-Za-z0-9]+)\s*=", uitokens_text))
    axaml_tokens = token_keys | color_keys
    parity_issues: list[str] = []
    for k in sorted(axaml_tokens - cs_tokens):
        parity_issues.append(f"UiTokens.cs missing key '{k}'")
    for k in sorted(cs_tokens - axaml_tokens):
        parity_issues.append(f"UiTokens.cs has extra key '{k}' not in Tokens.axaml/Colors.axaml")
    results.append(("UiTokens.cs 1:1 parity with Tokens.axaml + Colors.axaml", parity_issues))

    total_violations = 0
    for name, issues in results:
        status = "PASS" if not issues else f"FAIL ({len(issues)})"
        print(f"[{status}] {name}")
        for issue in issues[:25]:
            print(f"    - {issue}")
        if len(issues) > 25:
            print(f"    ... and {len(issues) - 25} more")
        total_violations += len(issues)

    print()
    if total_violations == 0:
        print("ALL CHECKS PASSED (0 violations)")
        return 0
    print(f"FAILED: {total_violations} total violation(s)")
    return 1


if __name__ == "__main__":
    sys.exit(main())
