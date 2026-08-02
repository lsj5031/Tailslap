# TailSlap

<div align="center">
  <img src="TailSlap/Icons/icon.png" alt="TailSlap Logo" width="128" height="128">
  
  **A Windows tray utility for clipboard refinement, dictation, and realtime transcription.**
  
  TailSlap runs in the system tray and allows you to quickly refine selected text using LLM services.
</div>

## Features

- **Text Refinement**: Process and enhance selected text with a hotkey (`Ctrl+Alt+R`)
- **Toggle Transcription**: Press a hotkey to start recording, press again to stop and transcribe with optional LLM auto-enhancement (`Ctrl+Alt+T`)
- **Push-to-Talk Transcription**: Hold `Ctrl+Win` to record audio, release to transcribe, and deliver the final result to your active application
- **Real-time Streaming**: Type words as they are spoken with WebSocket streaming (`Ctrl+Alt+Y`)
  - **Streaming Mode**: Real-time transcription via WebSocket connection
  - **Voice Activity Detection**: Auto-stop recording after silence (configurable threshold)
  - **Audio Format**: 16-bit mono, 16kHz WAV with optimized buffer management
- **Clipboard Integration**: Automatically paste refined text back into your applications
- **Safer Hotkey Setup**: The hotkey capture dialog turns green only for available shortcuts and red when the combination conflicts with another TailSlap mode or another app
- **Reliable Text Delivery**: TailSlap can paste via focused-control `WM_PASTE`, clipboard shortcuts, or Unicode `SendInput` depending on what the target app accepts
- **Customizable Hotkeys**: Configure four hotkeys via Settings menu:
  - Text Refinement: `Ctrl+Alt+R` (default)
  - Toggle Transcription: `Ctrl+Alt+T` (default)
  - Push-to-Talk: `Ctrl+Win` hold (default)
  - Real-time Streaming: `Ctrl+Alt+Y` (default)
- **Encrypted History**: View and manage your refinement and transcription history (secured with DPAPI)
- **Recording Overlay**: Floating capsule overlay with waveform bars while recording or streaming, and a status indicator while processing
- **System Tray Integration**: Runs quietly in the background
- **Auto-start Option**: Launch on Windows startup

## Installation

1. Download one of these assets from the [releases page](https://github.com/lsj5031/Tailslap/releases):
   - `TailSlap-self-contained-win-x64.zip`: Recommended for most users. No separate .NET install required.
   - `TailSlap-framework-dependent-win-x64.zip`: Smaller download, but requires the .NET 10 Desktop Runtime x64.
2. If you chose the framework-dependent zip, install the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) first.
3. Extract the zip, then run `TailSlap.exe`.
4. The application will start automatically and appear in your system tray

### Requirements

- **Windows 10 or later**
- **A configured LLM/transcription endpoint**. Endpoints may be local, on a LAN, or hosted; internet access is only required for remote services.
### Real-time Backend Requirements
- **WebSocket Streaming**: Requires a WebSocket-compatible transcription service when the realtime hotkey is used
- **Recommended provider**: `openai` — OpenAI-compatible `ws://…/v1/realtime?intent=transcription` (default; works with [glm-asr-docker](https://github.com/lsj5031/glm-asr-docker))
- **Legacy custom provider**: `custom` — `ws://…/v1/audio/transcriptions/stream` (still supported)
- **Fallback**: Standard HTTP transcription also supported

## Usage

### Text Refinement
1. Select text in any application
2. Press the configured hotkey (default: `Ctrl+Alt+R`)
3. The text will be processed and automatically pasted back (if enabled)

### Push-to-Talk Transcription
1. Press and hold the push-to-talk hotkey (default: `Ctrl+Win`)
2. Speak into your microphone -- a floating capsule overlay appears at the bottom of the screen with real-time waveform bars driven by your audio level
3. Release the hotkey to stop recording and start transcription
4. The streamed response is merged and deduplicated, then the final transcription is delivered once to your active application (tray icon animates slowly during transcription)
5. Results are saved to encrypted transcription history

### Toggle Transcription
1. Press the transcription hotkey (default: `Ctrl+Alt+T`) to start recording
2. Speak into your microphone
3. Press the hotkey again to stop recording and transcribe
4. If auto-enhancement is enabled and the transcription is long enough, it will be refined with LLM
5. Result is pasted into your active application
6. Results are saved to encrypted transcription history

### Real-time Streaming Transcription
1. Press the streaming hotkey (default: `Ctrl+Alt+Y`)
2. Speak naturally - text appears in real-time via WebSocket connection
3. Automatic silence detection can stop recording when you pause speaking, or you can stop manually

**Advanced Settings:**
- **Stream Results**: For **toggle** transcription only — stream post-recording HTTP chunks into the app as they arrive. Live speech-to-text while talking uses the **Realtime** hotkey, not this checkbox.
- **Realtime Provider**: Prefer `openai` (default). `custom` remains available for older stream endpoints.
- **ASR Language / session prompt**: Optional language hint for HTTP and OpenAI-protocol realtime transcription, plus an optional realtime vocabulary prompt (blank language = provider auto-detect)
- **WebSocket Endpoint**: A derived runtime endpoint built automatically from the base API endpoint and selected realtime provider; it is not a persisted JSON setting
- **Silence Detection**: Configure threshold (default: 2000ms) to auto-stop recording
- **Microphone Selection**: Choose preferred microphone device in Settings

### System Tray Menu

Right-click the TailSlap icon in the system tray to access:
- **Refine Now**: Process the currently selected text immediately (via clipboard)
- **Transcribe Now**: Start toggle-based audio transcription
- **Enable LLM Refinement**: Toggle LLM post-processing on/off
- **Enable Transcription**: Toggle the transcription hotkeys on/off
- **Run Diagnostics...**: Run audio device and connectivity diagnostics
- **Settings...**: Configure LLM endpoint, model, temperature, transcription settings, and hotkeys
- **Open Logs...**: View application logs for debugging
- **Encrypted Refinement History...**: View and clear your refinement history
- **Encrypted Transcription History...**: View and clear your transcription history
- **Start with Windows**: Toggle automatic startup with Windows
- **Quit**: Exit the application

### Hotkey Capture Feedback

When you change a hotkey in Settings:
- TailSlap temporarily suspends its own active hotkeys so pressing an existing shortcut does not accidentally trigger refine, transcription, or streaming.
- The capture box turns **green** when the shortcut is available.
- The capture box turns **red** when the shortcut conflicts with another TailSlap hotkey or a global hotkey already registered by another application.

## Configuration

Configuration is stored in a JSON file located at:
`%APPDATA%\TailSlap\config.json`

You can edit this file directly or use the Settings dialog in the system tray menu.

### Configuration Options

The JSON serializer writes camelCase property names. API keys are entered through Settings and stored as DPAPI-protected `apiKeyEncrypted` values; do not hand-edit them.

#### LLM Configuration
- `baseUrl`: OpenAI-compatible endpoint (default: `http://localhost:11434/v1`)
- `model`: Model name (default: `llama3.1`)
- `temperature`: Sampling temperature (default: `0.2`)
- `maxTokens`: Maximum response tokens (optional)
- `apiKeyEncrypted`: DPAPI-protected API key written by Settings
- `httpReferer`, `xTitle`: Optional HTTP headers

#### Transcription Configuration
- `enabled`: Enable transcription hotkeys (default: `true`)
- `baseUrl`: OpenAI-compatible API root (default: `http://localhost:18000/v1`; app derives `/audio/transcriptions`)
- `model`: Transcription model (default: `glm-nano-2512`)
- `apiKeyEncrypted`: DPAPI-protected API key written by Settings
- `timeoutSeconds`: Request timeout (default: `30`)
- `autoPaste`: Automatically paste transcription results (default: `true`)
- `enableVAD`: Voice Activity Detection (default: `true`)
- `silenceThresholdMs`: Silence detection threshold in milliseconds (default: `2000`)
- `preferredMicrophoneIndex`: Microphone device selection (default: `-1` for system default)
- `streamResults`: Request HTTP streaming for toggle transcription (default: `false`). Chunks are merged and delivered as one final result; live speech-to-text uses the realtime hotkey.
- `realtimeProvider`: `openai` (default, recommended) or `custom` (legacy stream URL)
- `language`: Optional BCP-47 hint for HTTP and OpenAI-protocol realtime (default empty = auto)
- `realtimeSessionPrompt`: Optional session vocabulary hint for OpenAI-protocol realtime
- `webSocketUrl`: Derived runtime value only; it is ignored when reading/writing JSON

#### Hotkey Configuration
- `hotkey`: Text refinement hotkey (default: `Ctrl+Alt+R`)
- `transcriberHotkey`: Toggle transcription hotkey (default: `Ctrl+Alt+T`)
- `typelessHotkey`: Push-to-talk hotkey (default: `Ctrl+Win` hold, `key = 0` means modifier-only)
- `streamingTranscriberHotkey`: Real-time streaming hotkey (default: `Ctrl+Alt+Y`)

#### General Settings
- `autoPaste`: Auto-paste refined text (default: `true`)
- `excludeFromClipboardHistory`: Exclude delivered text from Windows clipboard history (default: `true`)
- `useClipboardFallback`: Use Ctrl+C fallback when clipboard capture fails (default: `true`)

## Privacy & Security
- **End-to-End Encryption**: All history (refinement and transcription) is stored on disk using Windows DPAPI with `DataProtectionScope.CurrentUser`. Only the current Windows user can decrypt data.
- **API Key Protection**: All API keys encrypted with DPAPI using user-scoped protection.
- **Secure Logging**: Application logs use SHA256 fingerprints instead of sensitive text content. No plaintext user data is logged.
- **Graceful Degradation**: Encryption failures fall back safely without crashing the application.

## Logs

Application logs are stored at:
`%APPDATA%\TailSlap\logs\app.jsonl`

A legacy `%APPDATA%\TailSlap\app.log` may still exist from older builds, but
current diagnostics should use the JSONL log. Before sharing logs, inspect and
redact API keys, credential-bearing URLs or headers, transcripts, prompts, and
other personal or sensitive data.

## Troubleshooting

### Hotkey stays red in Settings
- Pick a different combination if the dialog says the shortcut is already used by TailSlap or another application.
- Modifier-only hold hotkeys are supported only for push-to-talk; the other modes require at least one non-modifier key.

### Text was not pasted or typed into the target app
- TailSlap will automatically try focused-control paste, standard paste shortcuts, and direct Unicode typing depending on the app.
- If all delivery methods fail, the text is still left on the clipboard so you can paste it manually.

### Diagnostics shows warnings even though a mode works
- The LLM probe calls `GET /models` with the configured bearer key. A `401` or `403` means the endpoint rejected or requires authentication; it does not mean the microphone or transcription path failed.
- The transcription probe calls the POST-only `/audio/transcriptions` endpoint with `GET`. A `405 Method Not Allowed` is expected and is reported as reachable.
- Realtime WebSocket connectivity is optional. If the HTTP transcription modes work but realtime is unavailable, the diagnostic reports a warning for that mode only. If transcription is disabled, realtime is not tested.
- Verify the configured `baseUrl` and `realtimeProvider` before changing a working setup. The app derives the HTTP and WebSocket paths from those values.

## Animation

TailSlap uses a smooth 8-frame animated icon during text processing:

| Frame 1 | Frame 2 | Frame 3 | Frame 4 | Frame 5 | Frame 6 | Frame 7 | Frame 8 |
|---------|---------|---------|---------|---------|---------|---------|---------|
| ![Frame1](TailSlap/Icons/1.png) | ![Frame2](TailSlap/Icons/2.png) | ![Frame3](TailSlap/Icons/3.png) | ![Frame4](TailSlap/Icons/4.png) | ![Frame5](TailSlap/Icons/5.png) | ![Frame6](TailSlap/Icons/6.png) | ![Frame7](TailSlap/Icons/7.png) | ![Frame8](TailSlap/Icons/8.png) |

The animation speed changes based on the active state:
- **Recording** (push-to-talk): Fast at 50ms intervals with "TailSlap - Recording..." tooltip
- **Transcribing**: Slow at 200ms intervals with "TailSlap - Transcribing..." tooltip
- **Refinement / processing**: Medium at 75ms intervals; the tray tooltip reflects the current processing state
- **Streaming**: Medium at 75ms intervals with "TailSlap - Streaming..." tooltip

Tooltip text pulses every 300ms with up to 3 dots for visual feedback.

## Building from Source

### Prerequisites
1. Install [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

### Build Commands
```bash
# Build release version
dotnet build -c Release

# Publish self-contained single file
dotnet publish TailSlap/TailSlap.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

**Output**: `TailSlap\bin\Release\net10.0-windows\win-x64\publish\TailSlap.exe`. Self-contained publishes also include `WebRtcVad.dll` and the `Icons` directory beside the executable; those files are required at runtime.

**Technology Stack**: 
- .NET 10 with Windows Forms
- Dependency Injection with Microsoft.Extensions.DependencyInjection
- HTTP Client Factory with connection pooling and compression
- Windows DPAPI for encryption
- WinMM API for audio recording
- WebRTC VAD for voice activity detection
- WebSocket client for real-time streaming

See [AGENTS.md](AGENTS.md) for detailed architecture and development guidelines.

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## Contributing

Contributions are welcome! Please see [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines on:
- How to report issues
- How to submit pull requests
- Code style and conventions
- Development setup

## Support

- **Issues**: [GitHub Issues](https://github.com/lsj5031/Tailslap/issues)
- **Discussions**: [GitHub Discussions](https://github.com/lsj5031/Tailslap/discussions)
- **Logs**: Check `%APPDATA%\TailSlap\logs\app.jsonl` for debugging

## Build Status

![Build](https://github.com/lsj5031/Tailslap/actions/workflows/build.yml/badge.svg)

All commits and pull requests are automatically built and tested via GitHub Actions.

## Acknowledgments

Built with [.NET 10](https://dotnet.microsoft.com/), [Windows Forms](https://docs.microsoft.com/windows-forms/), and [WebRTC VAD](https://github.com/np-quang/WebRtcVadSharp)
