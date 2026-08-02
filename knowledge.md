# Project Knowledge: TailSlap

A Windows system tray utility that enhances clipboard and text refinement with AI-powered processing.

## Quickstart

- **Setup**: Install [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- **Dev**: `dotnet build -c Release` from repo root
- **Test**: `dotnet test` (runs xUnit tests in TailSlap.Tests)
- **Publish**: `dotnet publish TailSlap\TailSlap.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true`
- **Run output**: `TailSlap\bin\Release\net10.0-windows\win-x64\publish\TailSlap.exe`

## Architecture

- **Framework**: .NET 10 Windows Forms (net10.0-windows)
- **UI**: Tray-only hidden form with animated 8-frame icon
- **DI**: Microsoft.Extensions.DependencyInjection
- **HTTP**: HttpClientFactory with connection pooling
- **Encryption**: Windows DPAPI (user-scoped)
- **Endpoints**: Local, LAN, or hosted OpenAI-compatible HTTP and WebSocket services; `openai` realtime is the default and `custom` remains supported

### Key Directories

- `TailSlap/` - Main application source
- `TailSlap.Tests/` - xUnit test project
- `scripts/` - PowerShell diagnostic scripts

### Four Operating Modes

1. **Refinement** (Ctrl+Alt+R): LLM text enhancement via clipboard
2. **Toggle Transcription** (Ctrl+Alt+T): Press to start/stop recording, then transcribe
3. **Push-to-Talk** (Ctrl+Win hold): Hold modifiers to record, release to consume SSE/NDJSON transcription chunks, merge them, and deliver the final text once
4. **Realtime Streaming** (Ctrl+Alt+Y): WebSocket real-time transcription

### Key Services

- `TextRefiner` - OpenAI-compatible LLM client with retry logic
- `RemoteTranscriber` - OpenAI-compatible HTTP transcription with full-response and optional SSE/NDJSON support
- `RealtimeTranscriber` / `OpenAIRealtimeTranscriber` - configurable custom and OpenAI-protocol WebSocket clients
- `ClipboardService` - Win32 clipboard with Ctrl+C fallback
- `AudioRecorder` - WinMM API with WebRTC VAD
- `ConfigService` - JSON config with FileSystemWatcher hot reload
- `HistoryService` - DPAPI-encrypted JSONL history

## Conventions

- **Language**: C# 12 with nullable reference types
- **Naming**: PascalCase (public), `_camelCase` (private fields)
- **Classes**: Sealed by default
- **Dependencies**: Minimal NuGet (`Microsoft.Extensions.DependencyInjection`, `Microsoft.Extensions.Http`, and `WebRtcVadSharp`)
- **JSON**: System.Text.Json with camelCase
- **Logging**: SHA256 fingerprints (never log sensitive text)
- **Error handling**: Graceful degradation, user-friendly notifications

## Gotchas

- Modifier-only hotkeys only work for push-to-talk (Key=0)
- Logs: `%APPDATA%\TailSlap\logs\app.jsonl` (preferred over app.log)
- Config: `%APPDATA%\TailSlap\config.json`
- `glm-asr-docker` is a supported local backend, not a hard dependency; any configured OpenAI-compatible transcription endpoint can be used
- Realtime derives `/v1/realtime?intent=transcription` for the default `openai` provider; `custom` remains available for legacy stream endpoints
