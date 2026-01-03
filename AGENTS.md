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
- **Core Services** (all interface-driven):
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
- **UI Forms**:
   - `MainForm`: Main application form (hidden), wired via DI
   - `HotkeyCaptureForm`: Interactive dialog for capturing new hotkey combinations
   - `SettingsForm`: UI for configuring LLM endpoint, model, temperature, max tokens
   - `HistoryForm`: UI for viewing encrypted refinement history with decryption status and diff view
   - `TranscriptionHistoryForm`: UI for viewing encrypted transcription history with decryption status
- **Resource Management**:
   - `SafeWaveInHandle`: RAII wrapper for WinMM wave input handle safety
   - `AudioRecorder`: Handles WinMM audio recording with Voice Activity Detection (VAD) and real-time streaming support
- **Serialization**: `TailSlapJsonContext` (System.Text.Json source-generated context for AOT-friendly, reflection-free serialization)
- **Single-instance mutex** prevents multiple app instances
- **Global hotkey registration** (default Ctrl+Alt+R, user-customizable)
- **Animated tray icon** (8-frame PNG animation with pulsing text) during refinement
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
