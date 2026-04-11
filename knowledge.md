# Project Knowledge: TailSlap

A Windows system tray utility that enhances clipboard and text refinement with AI-powered processing.

## Quickstart

- **Setup**: Install [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- **Dev**: `dotnet build -c Release` from repo root
- **Test**: `dotnet test` (runs xUnit tests in TailSlap.Tests)
- **Publish**: `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true`
- **Run output**: `TailSlap\bin\Release\net9.0-windows\win-x64\publish\TailSlap.exe`

## Architecture

- **Framework**: .NET 9 Windows Forms (net9.0-windows)
- **UI**: Tray-only hidden form with animated 8-frame icon
- **DI**: Microsoft.Extensions.DependencyInjection
- **HTTP**: HttpClientFactory with connection pooling
- **Encryption**: Windows DPAPI (user-scoped)

### Key Directories

- `TailSlap/` - Main application source
- `TailSlap.Tests/` - xUnit test project
- `scripts/` - PowerShell diagnostic scripts

### Four Operating Modes

1. **Refinement** (Ctrl+Alt+R): LLM text enhancement via clipboard
2. **Toggle Transcription** (Ctrl+Alt+T): Press to start/stop recording, then transcribe
3. **Push-to-Talk** (Ctrl+Win hold): Hold modifiers to record, release to transcribe with SSE streaming
4. **Realtime Streaming** (Ctrl+Alt+Y): WebSocket real-time transcription

### Key Services

- `TextRefiner` - OpenAI-compatible LLM client with retry logic
- `RemoteTranscriber` - HTTP transcription with SSE support
- `RealtimeTranscriber` - WebSocket streaming client
- `ClipboardService` - Win32 clipboard with Ctrl+C fallback
- `AudioRecorder` - WinMM API with WebRTC VAD
- `ConfigService` - JSON config with FileSystemWatcher hot reload
- `HistoryService` - DPAPI-encrypted JSONL history

## Conventions

- **Language**: C# 12 with nullable reference types
- **Naming**: PascalCase (public), `_camelCase` (private fields)
- **Classes**: Sealed by default
- **Dependencies**: Minimal NuGet (only Microsoft.Extensions.DependencyInjection from framework)
- **JSON**: System.Text.Json with camelCase
- **Logging**: SHA256 fingerprints (never log sensitive text)
- **Error handling**: Graceful degradation, user-friendly notifications

## Gotchas

- Modifier-only hotkeys only work for push-to-talk (Key=0)
- Logs: `%APPDATA%\TailSlap\logs\app.jsonl` (preferred over app.log)
- Config: `%APPDATA%\TailSlap\config.json`
- Requires [glm-asr-docker](https://github.com/lsj5031/glm-asr-docker) for transcription
- Realtime uses OpenAI-compatible protocol via `/v1/realtime?intent=transcription`