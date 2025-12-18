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
- **Core Services**:
   - `TextRefiner`: OpenAI-compatible LLM HTTP client with retry logic (2 attempts, 1s backoff)
   - `ClipboardService`: Clipboard operations via Win32 P/Invoke (text capture, paste, fallback to `Ctrl+C`)
   - `ConfigService`: JSON config in `%APPDATA%\TailSlap\config.json` with validation methods
   - `Dpapi`: Windows DPAPI encryption for API keys (user-scoped)
   - `AutoStartService`: Registry-based Windows startup via HKEY_CURRENT_USER\Run
   - `Logger`: File logging to `%APPDATA%\TailSlap\app.log` (no sensitive data logged - SHA256 fingerprints only)
   - `HistoryService`: **Encrypted** JSONL history file in `%APPDATA%\TailSlap\history.jsonl.encrypted` (max 50 entries) with Windows DPAPI protection
   - `NotificationService`: Balloon tips for user feedback (success/warning/error)
   - `HotkeyCaptureForm`: Interactive dialog for capturing new hotkey combinations
   - `SettingsForm`: UI for configuring LLM endpoint, model, temperature, max tokens
   - `HistoryForm`: UI for viewing encrypted refinement history with decryption status and diff view
   - `TranscriptionHistoryForm`: UI for viewing encrypted transcription history with decryption status
   - `RemoteTranscriber`: OpenAI-compatible transcription HTTP client (multipart form POST with WAV audio)
   - `AudioRecorder`: Windows Multimedia API (WinMM) via P/Invoke for microphone recording (16-bit mono, 16kHz WAV output)
- **Single-instance mutex** prevents multiple app instances
- **Global hotkey registration** (default Ctrl+Alt+R, user-customizable)
- **Animated tray icon** (4-frame animation with pulsing text) during refinement
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
- **No external NuGet dependencies**: Only built-in .NET libraries (System.*, Microsoft.*)
