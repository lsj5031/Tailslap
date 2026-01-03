# TailSlap

<div align="center">
  <img src="TailSlap/Icons/icon.png" alt="TailSlap Logo" width="128" height="128">
  
  **A Windows utility that enhances your clipboard and text refinement experience with AI-powered processing.**
  
  TailSlap runs in the system tray and allows you to quickly refine selected text using LLM services.
</div>

## Features

- **Text Refinement**: Process and enhance selected text with a hotkey (`Ctrl+Alt+R`)
- **Audio Transcription**: Record and transcribe audio from your microphone (`Ctrl+Alt+T`)
  - **Streaming Mode**: Type words as they are spoken (real-time feedback)
  - **Voice Activity Detection**: Auto-stop recording after silence
- **Clipboard Integration**: Automatically paste refined text back into your applications
- **Customizable Hotkeys**: Set your preferred keyboard shortcut via the Settings menu
- **Encrypted History**: View and manage your refinement and transcription history (secured with DPAPI)
- **System Tray Integration**: Runs quietly in the background
- **Auto-start Option**: Launch on Windows startup

## Installation

1. Download TailSlap.exe from the [releases page](https://github.com/tailslap/TailSlap/releases)
2. Run the executable directly (no installation needed)
3. The application will start automatically and appear in your system tray

### Requirements

- **Windows 10 or later**
- **Internet connection** for LLM processing (local Ollama doesn't require internet)
- **Real-time Backend**: [glm-asr-docker](https://github.com/lsj5031/glm-asr-docker) is required for real-time streaming and SSE transcription

## Usage

### Text Refinement
1. Select text in any application
2. Press the configured hotkey (default: `Ctrl+Alt+R`)
3. The text will be processed and automatically pasted back (if enabled)

### Audio Transcription
1. Press the transcription hotkey (default: `Ctrl+Alt+T`)
2. Record audio from your microphone
3. The audio will be transcribed and available in your history

**Advanced Settings:**
- Enable **Streaming Mode** in Settings to see text appear while you speak (Requires [glm-asr-docker](https://github.com/lsj5031/glm-asr-docker))
- Configure **Silence Detection** to automatically stop recording when you finish speaking

### System Tray Menu

Right-click the TailSlap icon in the system tray to access:
- **Refine Now**: Process the currently selected text immediately (via clipboard)
- **Transcribe Now**: Start audio recording for transcription
- **Settings...**: Configure LLM endpoint, model, temperature, transcription settings, and hotkeys
- **Open Logs...**: View application logs for debugging
- **Encrypted Refinement History...**: View and clear your refinement history
- **Encrypted Transcription History...**: View and clear your transcription history
- **Start with Windows**: Toggle automatic startup with Windows
- **Quit**: Exit the application

## Configuration

Configuration is stored in a JSON file located at:
`%APPDATA%\TailSlap\config.json`

You can edit this file directly or use the Settings dialog in the system tray menu.

## Privacy & Security
- **End-to-End Encryption**: All history (refinement and transcription) is stored on disk using Windows DPAPI (user-scoped encryption). Only you can read your history.
- **Sensitive Data**: API Keys are also encrypted with DPAPI.
- **Logs**: Application logs are sanitized and do not contain sensitive text content (SHA256 fingerprints are logged instead).

## Logs

Application logs are stored at:
`%APPDATA%\TailSlap\app.log`

## Animation

TailSlap uses a smooth 8-frame animated icon during text processing:

| Frame 1 | Frame 2 | Frame 3 | Frame 4 | Frame 5 | Frame 6 | Frame 7 | Frame 8 |
|---------|---------|---------|---------|---------|---------|---------|---------|
| ![Frame1](TailSlap/Icons/1.png) | ![Frame2](TailSlap/Icons/2.png) | ![Frame3](TailSlap/Icons/3.png) | ![Frame4](TailSlap/Icons/4.png) | ![Frame5](TailSlap/Icons/5.png) | ![Frame6](TailSlap/Icons/6.png) | ![Frame7](TailSlap/Icons/7.png) | ![Frame8](TailSlap/Icons/8.png) |

The animation cycles through all 8 frames with pulsing tray text ("TailSlap - Processing...") during LLM requests to give you smooth visual feedback.

## Building from Source
2. Install [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
3. Publish: `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true`

Output: `TailSlap\bin\Release\net9.0-windows\win-x64\publish\TailSlap.exe` (~80MB, no runtime required)

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

- **Issues**: [GitHub Issues](https://github.com/tailslap/TailSlap/issues)
- **Discussions**: [GitHub Discussions](https://github.com/tailslap/TailSlap/discussions)
- **Logs**: Check `%APPDATA%\TailSlap\app.log` for debugging

## Build Status

![Build](https://github.com/tailslap/TailSlap/actions/workflows/build.yml/badge.svg)

All commits and pull requests are automatically built and tested via GitHub Actions.

## Acknowledgments

Built with [.NET 9](https://dotnet.microsoft.com/) and [Windows Forms](https://docs.microsoft.com/windows-forms/)
