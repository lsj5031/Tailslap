# Design — TailSlap "Stockroom Tags"

> Design system recorded from the built world (impeccable `document` flow, ground truth over intention). Chosen on the decision page by the user; seed key `f64d3539`. Light-only, user-confirmed.

## Identity

TailSlap is a stockroom, not a dashboard: white cotton, black nylon, and safety-orange zip-tie tags. The thing that is ON is tagged orange; the thing that is OFF is a plain white plate; a 45° hazard diagonal means STOP. Every control reads like a label plate you can trust. The product's own cyan waveform accent is reserved for data; green/amber/red are the state lamps.

## Tokens (`TailSlap/UiTheme.cs` — the single source of truth)

### Palette

| Role | Value | Use |
|---|---|---|
| Ground | `#FAF9F6` | Form background |
| Panel | `#FFFFFF` | Lists, viewers, cards, plates |
| Ink | `#1F1F1F` | Primary text |
| Muted | `#6E6E6E` | Secondary text |
| Faint | `#9E9EA2` | Placeholders / disabled text |
| Rule | `#E2E0DA` | Hairlines, plate borders |
| Orange | `#FF6A00` | Decorative accent: tag squares, lamps |
| PrimaryFill | `#CC4A00` | Primary button plate (≥4.5:1 with white bold caps) |
| PrimaryHover / Pressed | `#B04100` / `#9E3900` | Primary plate states |
| OrangeSoft | `#FFF0E3` | Active-row tint |
| Cyan | `#0E7C9E` | Data accent (text-safe); `#5AD2FF` waveform bar |
| Success | back `#EDF7ED` / text `#2E8232` | OK rows, lamps |
| Warn | back `#FFF9E0` / text `#966800` | Warning rows, lamps |
| Error | back `#FFF1F1` / text `#BE2D2D` | Error rows, lamps |
### Type

- **Body / headers**: Segoe UI. Bold caps for quoted tags and plate buttons (`UiTheme.Caps` wraps text in straight quotes).
- **Data**: Consolas 9 (mono) for timestamps, hotkeys, URLs, values; Consolas 9 bold for values that pop.
- All fonts are **cached static, app-lifetime, never disposed** — forms must not dispose `UiTheme.*` fonts; only locally created `new Font(...)` objects (e.g. per-row bold derived fonts) get disposed on close.

## Components

- **TagStrip** — full-width section header: 8px orange square + quoted bold caps over a hairline rule. Top of every settings tab, dialog, and history form.
- **Buttons** (`UiTheme.StyleButton`) — Flat plates with hover/pressed states and an `EnabledChanged` handler for the disabled grey-out (applied at style time too):
  - Primary: orange fill, white bold caps, no border — exactly one per surface (OK / Yes / Close).
  - Secondary: white fill, hairline border, ink text.
  - Danger: white fill, red border/text, red hover tint (Clear History / Reset).
- **Lamp** — 10×10 square filled with the severity color (the label-plate grammar): status rows, validation, message-box severity.
- **HazardStrip** — 6px 45° black/white diagonal band (`HazardStrip` control): warning/error message boxes, settings validation failure.
- **Hotkey plates** — read-only TextBox, Consolas bold, single hairline border; valid state tints the plate `SuccessBack`, invalid `ErrorBack`.
- **History lists** — `ListView` Details with mono timestamps and severity-tinted rows: OK (white), FAIL (red), CORRUPT (amber), state badge bold-colored in its own column.

## Layout & motion

- User-facing dialogs use `DpiHelper.Scale` + `AutoScaleMode.Dpi`; the hidden tray host and floating overlay use their own fixed native layout rules.
- Docked composition: tag strip docked first (added last to the Controls collection so it takes the top edge), content fills below; bottom bars are `TableLayoutPanel`s stacking hazard → validation → button row.
- Motion exists only in the floating overlay (waveform bars, spring entrance, width tween); dialogs and forms are static (Operate mode: motion conveys state, nothing else).

## Surfaces

| Surface | Treatment |
|---|---|
| Tray menu / notifications | Native (untouched; balloon tips and context menu remain system-styled) |
| Settings | 4 tabs, quoted tag strips ("GENERAL", "LLM REFINEMENT", "RECORDING", "ADVANCED") with in-grid group headers (Core/Prompt/HTTP Headers/Hotkey & Test, Endpoint/Silence Detection/Source/Auto-Enhance/Hotkeys/Test, WebSocket/Realtime), mono hotkey plates, hazard validation strip, orange OK plate |
| Refinement History / Transcription History | Logo header + tag-subtitle counts, search, severity-tinted ListView, lamp status, themed plate buttons |
| Recent Errors & Warnings / Diagnostics | Logo header, severity rows, mono data, primary/secondary plate buttons |
| Hotkey capture | Mono display plate, semantic validation tints, quoted hint, themed plates |
| BrandedMessageBox | Hazard strip on warning/error, quoted-caps caption with orange tag + severity lamp, themed Yes/OK (primary) / No/Cancel (secondary) |
| Recording overlay | Dark capsule (product commitment) with cyan waveform and recording status while active |

## Guardrails

- The tool must disappear into the task: no decorative motion in dialogs, no display fonts, one primary action per surface, native affordances preserved.
- Never dispose shared `UiTheme` fonts; never `LoadMainIcon().ToBitmap()` ad hoc (use `BrandedMessageBox.GetLogoBitmap()`).
- Severity is never color alone: glyph/badge + tint always travel together.
