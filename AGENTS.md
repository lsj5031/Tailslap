# Development Guide

This document contains internal development information for TailSlap contributors.

## Build & Run Commands

- **Build Release**: `dotnet build -c Release` (from TailSlap directory)
- **Publish**: `dotnet publish -c Release` → output in `TailSlap\bin\Release\net9.0-windows\win-x64\publish\`
- **Run**: `TailSlap\bin\Release\net9.0-windows\win-x64\publish\TailSlap.exe`
- **Self-contained build** (single file, ~80MB): `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true`

## Architecture

- **Single WinForms desktop app** (.NET 9, net9.0-windows)
- **Tray-only UI**: Hidden main form, runs as system tray icon with context menu
- **Dependency Injection**: Microsoft.Extensions.DependencyInjection for service management and composition
- **HttpClientFactory**: Centralized HTTP client with connection pooling, automatic decompression, configurable timeouts

## Operating Modes

TailSlap has four hotkey-activated modes, each with a distinct workflow and hotkey registration mechanism:

### 1. Refinement Mode (Ctrl+Alt+R default, user-customizable)
- **Trigger**: Single-press via Win32 `RegisterHotKey` → `WM_HOTKEY` → `TriggerRefine()`
- **Flow**: Reads selected text from the focused app via `ClipboardHelper` → sends to LLM via `TextRefiner` → types refined result back via `TextTyper`
- **Controller**: `IRefinementController` / `RefinementController`
- **Config**: `config.json` → `hotkey` + `llm` sections

### 2. Toggle Transcription (Ctrl+Alt+T default, user-customizable)
- **Trigger**: Single-press via Win32 `RegisterHotKey` → `WM_HOTKEY` → `TriggerTranscribe()` (toggle: press to start, press again to stop)
- **Flow**: `TranscriptionController.TriggerTranscribeAsync()` → records WAV via `AudioRecorder` → on second press, stops recording → transcribes via `RemoteTranscriber` → optional LLM auto-enhancement → pastes result via `ClipboardHelper`
- **Controller**: `ITranscriptionController` / `TranscriptionController`
- **Config**: `config.json` → `transcriberHotkey` + `transcriber` sections
- **Auto-enhancement**: If enabled and transcription exceeds character threshold, enhances with LLM refinement

### 3. Typeless / Push-to-Talk Transcription (Ctrl+Win hold default, user-customizable)
- **Trigger**: Hold-to-record via low-level keyboard hook (`KeyboardHook`, WH_KEYBOARD_LL); hold modifiers to start recording, release to stop and transcribe
- **Flow**: `KeyboardHook.OnKeyDown` → `TypelessController.HandleKeyDownAsync()` → records WAV via `AudioRecorder` → on modifier release, streams audio to transcription endpoint via `RemoteTranscriber` → SSE chunks typed incrementally via `TextTyper` → saved to transcription history
- **State machine**: `Idle → Recording → Processing → Idle` (events: `OnStarted`, `OnProcessingStarted`, `OnCompleted`)
- **Minimum recording**: 500ms; shorter recordings are discarded with a warning
- **Safety**: Max recording duration of 60s enforced by `KeyboardHook.ForceStop()`; auto-repeat suppression prevents duplicate recordings
- **Controller**: `ITypelessController` / `TypelessController`
- **Config**: `config.json` → `typelessHotkey` + `transcriber` sections
- **Tray animation**: Fast (50ms) during recording, slow (200ms) during transcription
- **Modifier-only hotkey**: Supports `Key == 0` meaning no primary key required — fires on modifier combination only

### 4. Realtime Streaming Transcription (Ctrl+Alt+Y default, user-customizable)
- **Trigger**: Single-press via Win32 `RegisterHotKey` → `WM_HOTKEY` → `TriggerStreamingTranscribe()`
- **Flow**: Opens WebSocket to realtime transcription endpoint → streams audio bidirectionally → receives partial/final transcripts in real-time
- **Controller**: `IRealtimeTranscriptionController` / `RealtimeTranscriptionController`
- **Config**: `config.json` → `streamingTranscriberHotkey` + `transcriber` sections

## Core Services

All interface-driven, registered via DI:

   - `ITextRefiner` / `TextRefiner`: OpenAI-compatible LLM HTTP client with retry logic (2 attempts, 1s backoff)
   - `ITextRefinerFactory`: Factory for creating TextRefiner instances
   - `IRemoteTranscriber` / `RemoteTranscriber`: OpenAI-compatible transcription HTTP client (multipart form POST with WAV audio); supports SSE streaming (Requires [glm-asr-docker](https://github.com/lsj5031/glm-asr-docker))
   - `IRemoteTranscriberFactory`: Factory for creating RemoteTranscriber instances
   - `RealtimeTranscriber`: WebSocket-based client for real-time bi-directional audio streaming and transcription (Requires [glm-asr-docker](https://github.com/lsj5031/glm-asr-docker))
   - `IClipboardService` / `ClipboardService`: Clipboard operations via Win32 P/Invoke (text capture, paste, fallback to `Ctrl+C`)
   - `IConfigService` / `ConfigService`: JSON config in `%APPDATA%\TailSlap\config.json` with validation methods; FileSystemWatcher for hot reload
   - `IHistoryService` / `HistoryService`: **Encrypted** JSONL history (stream-based I/O for large files, max 50 entries) with Windows DPAPI protection
   - `Dpapi`: Windows DPAPI encryption for API keys (user-scoped)
   - `AutoStartService`: Registry-based Windows startup via HKEY_CURRENT_USER\Run
   - `Logger`: File logging to `%APPDATA%\TailSlap\app.log` (no sensitive data logged - SHA256 fingerprints only); Span<T> optimized
   - `NotificationService`: Balloon tips for user feedback (success/warning/error)
   - `DiagnosticsEventSource`: EventSource for ETW-based diagnostics and performance monitoring (14 events across 7 categories)
   - `ITypelessController` / `TypelessController`: Push-to-talk transcription state machine (Idle → Recording → Processing → Idle); events: `OnStarted`, `OnProcessingStarted`, `OnCompleted`
   - `ITranscriptionController` / `TranscriptionController`: Toggle-based transcription (press to start recording, press again to stop and transcribe); includes auto-enhancement via LLM
   - `IRefinementController`: Text refinement workflow controller
   - `IRealtimeTranscriptionController` / `RealtimeTranscriptionController`: WebSocket-based real-time streaming transcription controller
   - `KeyboardHook`: Low-level keyboard hook (WH_KEYBOARD_LL) for push-to-talk hotkey detection; auto-repeat suppression, max recording duration safety net
   - `TextTyper`: Hybrid text delivery via clipboard paste and SendKeys fallback
   - `ClipboardHelper`: Clipboard read/capture with multiple fallback methods (WM_COPY, Ctrl+C, Ctrl+Insert)
   - `IAudioRecorderFactory`: Factory for creating AudioRecorder instances

- **UI Forms**:
   - `MainForm`: Main application form (hidden), wired via DI
   - `HotkeyCaptureForm`: Interactive dialog for capturing new hotkey combinations
   - `SettingsForm`: UI for configuring LLM endpoint, model, temperature, max tokens
   - `HistoryForm`: UI for viewing encrypted refinement history with decryption status and diff view
   - `TranscriptionHistoryForm`: UI for viewing encrypted transcription history with decryption status
- **Resource Management**:
   - `SafeWaveInHandle`: RAII wrapper for WinMM wave input handle safety
   - `AudioRecorder`: Handles WinMM audio recording with Voice Activity Detection (VAD) and real-time streaming support
   - `WebRtcVadService`: ML-based voice activity detection using Google's WebRTC VAD (GMM-based) via WebRtcVadSharp
- **Serialization**: `TailSlapJsonContext` (System.Text.Json source-generated context for AOT-friendly, reflection-free serialization)
- **Single-instance mutex** prevents multiple app instances
- **Global hotkey registration** (default Ctrl+Alt+R, user-customizable) via Win32 `RegisterHotKey`
- **Low-level keyboard hook** (WH_KEYBOARD_LL) for push-to-talk hotkey via `KeyboardHook`
- **Animated tray icon** (8-frame PNG animation with pulsing text) with state-aware speeds: fast (50ms) during recording, medium (75ms) during refinement/streaming, slow (200ms) during transcription
- **DPI-aware icon loading** (scales icons based on display DPI)

## Security & Encryption

- **API Keys**: Encrypted using Windows DPAPI `DataProtectionScope.CurrentUser` (Dpapi service)
- **History Files**: All refinement and transcription history encrypted with Windows DPAPI
  - Refinement: `%APPDATA%\TailSlap\history.jsonl.encrypted`
  - Transcription: `%APPDATA%\TailSlap\transcription-history.jsonl.encrypted`
  - Encryption is transparent to users; history forms show decryption status
  - Plaintext history (if present from older versions) remains unencrypted in separate files
  - Only the current Windows user can decrypt (not even administrators)
- **Log Files**: Never log sensitive text directly; use SHA256 fingerprints for debugging
- **System Integration**: Leverages Windows DPAPI, no custom encryption keys or passwords
- **Error Recovery**: Graceful degradation if encryption/decryption fails

## Code Style & Conventions

- **Language**: C# 12 (.NET 9) with nullable reference types enabled (`<Nullable>enable</Nullable>`)
- **Implicit usings** enabled (`<ImplicitUsings>enable</ImplicitUsings>`)
- **Naming**: PascalCase for public members, `_camelCase` for private fields
- **Classes**: Sealed where appropriate (ConfigService, TextRefiner, ClipboardService, etc.)
- **JSON**: System.Text.Json with `PropertyNamingPolicy.CamelCase` and pretty-printing for config
- **Error handling**: Explicit try-catch blocks with graceful fallbacks; show user-friendly notifications
- **Async**: Prefer `async/await` with `ConfigureAwait(false)` for UI deadlock safety
- **P/Invoke**: Declared in MainForm (hotkey registration), ClipboardService (clipboard access), and AudioRecorder (WinMM audio recording) with `DllImport` attributes
- **Validation**: Static helper methods in ConfigService (IsValidUrl, IsValidTemperature, IsValidMaxTokens, IsValidModelName)
- **Logging**: Wrap all logging in try-catch to prevent crashes if log write fails; log fingerprints (SHA256) of text, not the text itself
- **Notifications**: Use NotificationService for all user-facing messages (balloon tips)
- **UI Forms**: Always use `using` statements for form disposal; dialog-based for modality
- **Dependencies**: Minimal external NuGet—only Microsoft.Extensions.DependencyInjection (included via Microsoft.AspNetCore.App framework reference)

## Local Debug Notes

- Runtime logs for current builds are in `%APPDATA%\TailSlap\logs\app.jsonl`. Prefer that over the legacy `%APPDATA%\TailSlap\app.log` when debugging realtime issues.
- Safe `jq` filter style for this JSONL schema: use `.msg? | strings` so non-string or missing message fields do not explode the query.
- Useful realtime log command:
  `jq -r 'select((.msg? | strings) | test("OpenAIRealtimeTranscriber|RealtimeTranscriber|HandleRealtime|ProcessTranscriptionAsync|AudioRecorder.StartStreamingAsync|VAD\\[|StreamingRecovery")) | [.ts, .source, .msg] | @tsv' "%APPDATA%\\TailSlap\\logs\\app.jsonl"`
- Current local realtime setup to remember:
  `transcriber.realtimeProvider = "openai"`
  `transcriber.baseUrl = "http://localhost:18000/v1/audio/transcriptions"`
  `transcriber.model = "glm-nano-2512"`
- In this repo, "OpenAI mode" may therefore mean a local OpenAI-protocol backend rather than the hosted OpenAI API. Always confirm the actual `baseUrl` before debugging.
