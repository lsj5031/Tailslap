# Product

<!-- impeccable:product-schema 1 -->

## Platform

windows-native (WinForms desktop, Windows 10/11, x64)

## Stack

.NET 10 Windows Forms (net10.0-windows), C# 12 with nullable reference types, programmatic UI (no designer files), Microsoft.Extensions.DependencyInjection, System.Text.Json, Windows DPAPI for secrets/history, WinMM audio capture with WebRTC VAD, OpenAI-compatible HTTP + WebSocket clients. Published as a single-file self-contained x64 exe.

## Users

Primary user: the developer-operator of the tool (single-user desktop utility). Daily job: speak to type. The user routinely dictates messages into chat apps, editors, and web pages through TailSlap's push-to-talk and realtime modes — this very conversation was dictated through the tool. Secondary job: refine selected clipboard text with a local LLM and inspect transcription/refinement history.

## Product Purpose

TailSlap is a Windows system-tray utility that turns the user's voice into typed text anywhere and refines clipboard text with a self-hosted LLM. Success means the user can speak naturally and have text appear in the focused app as if they had typed it — reliably, privately, and without breaking flow.

## Positioning

Local-first AI typing: it works entirely against the user's own OpenAI-compatible servers (self-hosted ASR like glm-asr-docker, local LLM endpoints), stores history encrypted with Windows DPAPI, and lives in the tray with four global-hotkey modes. No cloud account, no vendor lock-in, no text leaving the machine except to the user's own endpoints.

## Operating Context

- Resides in the system tray with an animated beaver icon; tray-only (no main window).
- Four global-hotkey modes: Refinement (Ctrl+Alt+R), Toggle Transcription (Ctrl+Alt+T), Push-to-Talk (Ctrl+Win hold), Realtime Streaming (Ctrl+Alt+Y).
- A floating dark capsule overlay shows live waveform bars, status, and streaming text during any active mode.
- Config: `%APPDATA%\TailSlap\config.json` (hot-reloaded). Logs: `%APPDATA%\TailSlap\logs\app.jsonl` with SHA256 fingerprints (never plaintext secrets).
- History: DPAPI-encrypted JSONL for both refinement and transcription entries, searchable and exportable.
- User works in a bright environment on a light-themed Windows desktop; dialogs and forms are light-only (user-confirmed decision).

## Capabilities and Constraints

- Modes: refinement, toggle transcription, push-to-talk (modifier-only hold), realtime streaming (WebSocket).
- Streaming SSE/WS transcription with dedup of resent/snapshot chunks; auto-paste with verification and fallback typing; clipboard-history exclusion for delivered text.
- VAD silence detection with sensitivity presets; microphone selection; ASR language hint; realtime session prompt.
- Diagnostics panel (endpoint reachability), recent errors & warnings viewer, history search/export.
- Constraints: WinForms only (no CSS/HTML), single-exe publish, must remain lightweight and out of the user's way, DPI-aware (DpiHelper + AutoScaleMode.Dpi), dark overlay capsule over light dialogs.
- Undecided: none material at this time.

## Brand Commitments

- Name: TailSlap. Logo: cartoon beaver icon (loaded from `Icons` dir at runtime, cached bitmap shared by all dialogs).
- Light theme only (user-confirmed); native Windows feel as the base, with committed brand styling allowed.
- Cyan-blue accent (RGB 90, 210, 255) established by the recording overlay waveform; severity palette green/amber/red used by the issues/diagnostics dialogs.
- UI copy is developer-plain: short labels, no marketing voice.

## Evidence on Hand

- The user's own daily usage (this conversation dictated through the tool) — the tool's central job is proven by its own usage.
- Screenshots of TailSlap Diagnostics and Recent Issues dialogs supplied during this project's UI work.
- Logs at `%APPDATA%\TailSlap\logs\app.jsonl` showing real session behavior (duplicate-chunk resends, hotkey registration, paste verification).
- 337 unit tests in TailSlap.Tests covering controllers, sink, typer, services.

## Product Principles

1. The tool disappears into the task — voice-to-text must feel like native typing; UI must never obscure the state or the task.
2. Privacy by default — history encrypted with DPAPI, secrets never logged, only the user's own endpoints are touched.
3. Self-hosted freedom — any OpenAI-compatible ASR/LLM server works; no lock-in.
4. Keyboard-first — every mode is a global hotkey away; the tray menu is the secondary surface.
5. Fail visibly, recover quietly — every failure is logged with a fingerprint, surfaced in the issues/diagnostics viewers, and retryable.

## Accessibility & Inclusion

- Full DPI scaling via DpiHelper + AutoScaleMode.Dpi on all forms.
- Severity is always encoded in text/glyph + color (not color alone): ✓/⚠/✗ plus tinted rows.
- Keyboard navigable forms (AcceptButton/CancelButton, F5 refresh, hotkey capture).
