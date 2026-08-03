# TailSlap project directory

The canonical product and configuration documentation lives in the
[root README](../README.md). Contributor workflow and architecture details
live in [AGENTS.md](../AGENTS.md).

## Build from the repository root

```powershell
dotnet build -c Release
dotnet test -c Release
dotnet publish TailSlap/TailSlap.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

The publish output is
`TailSlap\bin\Release\net10.0-windows\win-x64\publish\`. Run
`TailSlap.exe` from that directory. A self-contained publish also needs the
native `WebRtcVad.dll`; branded icons are embedded in the executable.

## Source map

- `MainForm.cs`: tray host, hotkeys, diagnostics, and animation coordination
- `ConfigService.cs`: camelCase JSON configuration and DPAPI-protected secrets
- `RemoteTranscriber.cs`: OpenAI-compatible HTTP transcription
- `OpenAIRealtimeTranscriber.cs` / `RealtimeTranscriber.cs`: realtime WebSocket providers
- `TextTyper.cs` / `ClipboardService.cs`: verified text delivery and fallbacks
- `AudioRecorder.cs`: WinMM recording and VAD integration
- `HistoryService.cs`: DPAPI-protected JSONL history

The default realtime provider is `openai`; `custom` remains available for
legacy stream endpoints. Push-to-talk and toggle HTTP streaming merge response
chunks and deliver the final text once, while realtime mode can display live
partial text.
