# Contributing to TailSlap

Thanks for your interest in contributing to TailSlap! Here are the guidelines to help you get started.

## Code of Conduct

Be respectful, inclusive, and constructive in all interactions.

## How to Contribute

### Reporting Issues

1. Check existing [issues](https://github.com/lsj5031/Tailslap/issues) to avoid duplicates
2. Include a clear description and steps to reproduce
3. Share your environment: Windows version, .NET runtime version, LLM provider used
4. Attach logs from `%APPDATA%\TailSlap\app.log` if relevant

### Submitting Pull Requests

1. **Fork and branch**: Create a feature branch from `main`
   ```bash
   git checkout -b feature/your-feature-name
   ```

2. **Code style**: Follow the conventions in [AGENTS.md](AGENTS.md)
   - C# 12 with nullable reference types enabled
   - PascalCase for public members, `_camelCase` for private fields
   - Sealed classes by default
   - No external NuGet dependencies beyond built-in .NET libraries

3. **Test your changes**:
   ```bash
   # Build release version
   dotnet build -c Release
   
   # Publish self-contained single file
   dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
   ```
   Then run the published exe and verify the feature works

4. **Commit messages**: Clear, concise, present tense
   - `Add clipboard history feature`
   - `Fix hotkey registration on Windows 11`
   - `Improve error messages for invalid LLM config`

5. **Before pushing**:
   - Ensure code compiles without warnings
   - No hardcoded secrets, API keys, or sensitive information
   - Format code consistently (VS Code format recommended)
   - Automated tests will run via GitHub Actions (build.yml)

6. **Submit PR**:
   - Reference related issues: "Fixes #123"
   - Describe what changed and why
   - Include any configuration examples if adding new features
   - GitHub Actions will automatically build and test your PR

## Development Setup

1. Install [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
2. Clone the repo and open in Visual Studio 2022 or VS Code
3. Build: `dotnet build`
4. Run: `TailSlap\bin\Debug\net9.0-windows\win-x64\TailSlap.exe`

## Areas for Contribution

- **Features**: New LLM provider integrations, UI improvements, history management, real-time streaming enhancements
- **Bug fixes**: Issues in clipboard handling, hotkey registration, error handling, WebSocket connectivity
- **Documentation**: Improving README, config examples, troubleshooting guides
- **Testing**: Feedback on different Windows versions and LLM providers
- **Performance**: Audio buffer optimization, HTTP client tuning, animation smoothness
- **Security**: Encryption improvements, logging sanitization, API key management

## Architecture Overview

See [AGENTS.md](AGENTS.md) for detailed architecture, build commands, and code conventions.

Key components:
- `MainForm.cs` - Tray UI, hotkey handling, and real-time streaming coordination
- `TextRefiner.cs` - OpenAI-compatible LLM HTTP client with retry logic
- `RemoteTranscriber.cs` - HTTP-based transcription client
- `RealtimeTranscriber.cs` - WebSocket-based real-time streaming client
- `ConfigService.cs` - JSON configuration management with validation
- `ClipboardService.cs` - Windows clipboard integration with fallback mechanisms
- `AudioRecorder.cs` - WinMM-based audio recording with VAD
- `HistoryService.cs` - Encrypted history management with DPAPI
- `AutoStartService.cs` - Windows startup registry handling
- `NotificationService.cs` - Balloon tip notification system
- `Dpapi.cs` - Windows DPAPI encryption wrapper
- `Logger.cs` - File logging with SHA256 fingerprinting
- `DiagnosticsEventSource.cs` - ETW-based diagnostics and performance monitoring

## Questions?

Open an issue for clarifications or reach out in discussions.
