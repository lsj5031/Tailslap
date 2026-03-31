# TailSlap

<div align="center">
  <img src="TailSlap/Icons/icon.png" alt="TailSlap Logo" width="128" height="128">
  
  **A Windows utility that enhances your clipboard and text refinement experience with AI-powered processing.**
  
  TailSlap runs in the system tray and allows you to quickly refine selected text using LLM services.
</div>

## Features

- **Text Refinement**: Process and enhance selected text with a hotkey (`Ctrl+Alt+R`)
- **Toggle Transcription**: Press a hotkey to start recording, press again to stop and transcribe with optional LLM auto-enhancement (`Ctrl+Alt+T`)
- **Push-to-Talk Transcription**: Hold `Ctrl+Win` to record audio, release to transcribe and type the result into your active application incrementally
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
- **Recording Overlay**: Floating capsule overlay with real-time audio waveform bars during push-to-talk recording
- **System Tray Integration**: Runs quietly in the background
- **Auto-start Option**: Launch on Windows startup

## Installation

1. Download one of these assets from the [releases page](https://github.com/lsj5031/Tailslap/releases):
   - `TailSlap-self-contained-win-x64.exe`: Recommended for most users. No separate .NET install required.
   - `TailSlap-framework-dependent-win-x64.zip`: Smaller download, but requires the .NET 9 Desktop Runtime x64.
2. If you chose the framework-dependent zip, install the [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0) first.
3. Run `TailSlap.exe`.
4. The application will start automatically and appear in your system tray

### Requirements

- **Windows 10 or later**
- **Internet connection** for LLM processing (local Ollama doesn't require internet)
### Real-time Backend Requirements
- **WebSocket Streaming**: Requires a WebSocket-compatible transcription service
- **Custom Provider**: Uses `ws://localhost:18000/v1/audio/transcriptions/stream` and TailSlap's richer custom streaming protocol
- **OpenAI Provider**: Uses `ws://localhost:18000/v1/realtime?intent=transcription` for OpenAI-compatible realtime transcription
- **Recommended**: [glm-asr-docker](https://github.com/lsj5031/glm-asr-docker) for both streaming modes
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
4. Transcribed text is typed incrementally into your active application as SSE chunks arrive (tray icon animates slowly during transcription)
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
- **Streaming Mode**: Enable WebSocket streaming for real-time feedback (requires WebSocket endpoint)
- **Realtime Provider**: Choose `custom` for TailSlap's richer streaming protocol or `openai` for OpenAI-compatible `/v1/realtime?intent=transcription`
- **WebSocket Endpoint**: Built automatically from the base API endpoint for the selected realtime provider
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

#### LLM Configuration
- `BaseUrl`: OpenAI-compatible endpoint (default: `http://localhost:11434/v1`)
- `Model`: Model name (default: `llama3.1`)
- `Temperature`: Sampling temperature (default: `0.2`)
- `MaxTokens`: Maximum response tokens (optional)
- `ApiKey`: Encrypted API key for cloud services
- `HttpReferer`, `XTitle`: Optional HTTP headers

#### Transcription Configuration
- `BaseUrl`: OpenAI-style API root (default: `http://localhost:18000/v1`; app appends `/audio/transcriptions`)
- `Model`: Transcription model (default: `glm-nano-2512`)
- `ApiKey`: Encrypted API key (optional)
- `TimeoutSeconds`: Request timeout (default: `30`)
- `AutoPaste`: Automatically paste transcription results (default: `true`)
- `EnableVAD`: Voice Activity Detection (default: `true`)
- `SilenceThresholdMs`: Silence detection threshold in milliseconds (default: `2000`)
- `PreferredMicrophoneIndex`: Microphone device selection (default: `-1` for system default)
- `StreamResults`: Enable WebSocket streaming (default: `false`)
- `RealtimeProvider`: `custom` for TailSlap's native protocol or `openai` for OpenAI-compatible realtime transcription (default: `custom`)
- `WebSocketUrl`: Auto-constructed WebSocket endpoint for the selected streaming provider

#### Hotkey Configuration
- `Hotkey`: Text refinement hotkey (default: `Ctrl+Alt+R`)
- `TranscriberHotkey`: Toggle transcription hotkey (default: `Ctrl+Alt+T`)
- `TypelessHotkey`: Push-to-talk hotkey (default: `Ctrl+Win` hold, `Key = 0` means modifier-only)
- `StreamingTranscriberHotkey`: Real-time streaming hotkey (default: `Ctrl+Alt+Y`)

#### General Settings
- `AutoPaste`: Auto-paste refined text (default: `true`)
- `UseClipboardFallback`: Use Ctrl+C fallback when clipboard capture fails (default: `true`)

## Privacy & Security
- **End-to-End Encryption**: All history (refinement and transcription) is stored on disk using Windows DPAPI with `DataProtectionScope.CurrentUser`. Only the current Windows user can decrypt data.
- **API Key Protection**: All API keys encrypted with DPAPI using user-scoped protection.
- **Secure Logging**: Application logs use SHA256 fingerprints instead of sensitive text content. No plaintext user data is logged.
- **Graceful Degradation**: Encryption failures fall back safely without crashing the application.

## Logs

Application logs are stored at:
`%APPDATA%\TailSlap\logs\app.jsonl`

## Troubleshooting

### Hotkey stays red in Settings
- Pick a different combination if the dialog says the shortcut is already used by TailSlap or another application.
- Modifier-only hold hotkeys are supported only for push-to-talk; the other modes require at least one non-modifier key.

### Text was not pasted or typed into the target app
- TailSlap will automatically try focused-control paste, standard paste shortcuts, and direct Unicode typing depending on the app.
- If all delivery methods fail, the text is still left on the clipboard so you can paste it manually.

## Animation

TailSlap uses a smooth 8-frame animated icon during text processing:

| Frame 1 | Frame 2 | Frame 3 | Frame 4 | Frame 5 | Frame 6 | Frame 7 | Frame 8 |
|---------|---------|---------|---------|---------|---------|---------|---------|
| ![Frame1](TailSlap/Icons/1.png) | ![Frame2](TailSlap/Icons/2.png) | ![Frame3](TailSlap/Icons/3.png) | ![Frame4](TailSlap/Icons/4.png) | ![Frame5](TailSlap/Icons/5.png) | ![Frame6](TailSlap/Icons/6.png) | ![Frame7](TailSlap/Icons/7.png) | ![Frame8](TailSlap/Icons/8.png) |

The animation speed changes based on the active state:
- **Recording** (push-to-talk): Fast at 50ms intervals with "TailSlap - Recording..." tooltip
- **Transcribing**: Slow at 200ms intervals with "TailSlap - Transcribing..." tooltip
- **Refining**: Medium at 75ms intervals with "TailSlap - Refining..." tooltip
- **Streaming**: Medium at 75ms intervals with "TailSlap - Streaming..." tooltip

Tooltip text pulses every 300ms with up to 3 dots for visual feedback.

## Building from Source

### Prerequisites
1. Install [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)

### Build Commands
```bash
# Build release version
dotnet build -c Release

# Publish self-contained single file
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

**Output**: `TailSlap\bin\Release\net9.0-windows\win-x64\publish\TailSlap.exe`

**Technology Stack**: 
- .NET 9 with Windows Forms
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

Built with [.NET 9](https://dotnet.microsoft.com/), [Windows Forms](https://docs.microsoft.com/windows-forms/), and [WebRTC VAD](https://github.com/np-quang/WebRtcVadSharp)
