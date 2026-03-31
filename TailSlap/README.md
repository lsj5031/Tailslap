# TailSlap - Text Refinement Tool

A Windows system tray application that refines text using AI-powered LLM services.

## Features

- **System Tray Application**: Runs minimized in the system tray
- **Global Hotkeys**:
  - `Ctrl+Alt+R`: Refine selected text
  - `Ctrl+Alt+T`: Toggle transcription (press to start recording, press again to stop)
  - `Ctrl+Win` hold: Push-to-talk transcription (hold to record, release to transcribe)
- **Audio Transcription**: Record microphone input and transcribe to text with optional LLM auto-enhancement
- **Encrypted History**: Securely stores refinement and transcription history
- **Auto-Paste**: Automatically pastes refined text back (toggleable)
- **Animation**: Visual feedback during LLM processing
- **Encrypted API Keys**: API keys stored securely using Windows DPAPI
- **Auto-Start**: Optional Windows startup integration
- **Retry Logic**: Automatic retry on transient failures (429/5xx errors)

## Requirements

- Windows 10/11
- .NET 9 Runtime (Download from https://dotnet.microsoft.com/download/dotnet/9.0)

## Installation

1. Download or build the application
2. Run `TailSlap.exe`
3. The app will appear in your system tray (look for the green circle icon)

## Configuration

Configuration is stored in: `%APPDATA%\TailSlap\config.json`

### Default Configuration

```json
{
  "AutoPaste": true,
  "Hotkey": {
    "Modifiers": 3,
    "Key": 82
  },
  "Llm": {
    "Enabled": true,
    "BaseUrl": "http://localhost:11434/v1",
    "Model": "llama3.1",
    "Temperature": 0.2,
    "MaxTokens": null,
    "RefinementPrompt": "You are an expert writing assistant that turns rough dictated text into polished professional writing.\n\nPreserve the original meaning, intent, and factual content.\nRemove filler words, false starts, repetitions, self-corrections, and obvious speech-to-text artifacts.\nFix grammar, punctuation, capitalization, and obvious transcription mistakes.\nMake the result concise, well-structured, elegant, and easy to read.\nKeep the tone natural and professional.\nPreserve useful formatting and line breaks. If the input is one long spoken block, you may introduce clear paragraph breaks or lists when that improves readability.\nDo not add new facts, requests, promises, greetings, sign-offs, or commentary that were not implied by the input.\nDo not over-edit for style; stay close to the original wording unless a change improves clarity.\nReturn only the final polished text.",
    "ApiKeyEncrypted": null,
    "HttpReferer": null,
    "XTitle": null
  },
  "Transcriber": {
    "Enabled": true,
    "BaseUrl": "http://localhost:18000/v1",
    "Model": "glm-nano-2512",
    "TimeoutSeconds": 30,
    "AutoPaste": true,
    "EnableVAD": false,
    "SilenceThresholdMs": 1000,
    "PreferredMicrophoneIndex": -1,
    "UseWebRtcVad": true,
    "WebRtcVadSensitivity": 2
  }
}
```

### Configuration Options

- **AutoPaste**: Automatically paste refined text after processing
- **Hotkey.Modifiers**: Modifier keys (3 = Ctrl+Alt, 5 = Ctrl+Shift, 10 = Ctrl+Win)
- **Hotkey.Key**: Key code (82 = R, 84 = T, 0 = modifier-only/hold)
- **TypelessHotkey.Modifiers**: Push-to-talk modifier keys (default: 10 = Ctrl+Win)
- **TypelessHotkey.Key**: Push-to-talk key (default: 0 = modifier-only/hold)
- **Llm.BaseUrl**: LLM API endpoint (OpenAI-compatible)
- **Llm.Model**: Model name
- **Llm.Temperature**: Creativity (0.0-1.0)
- **Llm.RefinementPrompt**: The system prompt used to polish dictated text
- **Llm.ApiKey**: API key (set via code, will be encrypted)
- **Transcriber.UseWebRtcVad**: Use ML-based WebRTC VAD for smarter speech detection (default: true)
- **Transcriber.WebRtcVadSensitivity**: VAD sensitivity (0=Low, 1=Medium, 2=High, 3=VeryHigh)

### Supported LLM Providers

Any OpenAI-compatible API:
- **Ollama** (default): `http://localhost:11434/v1`
- **OpenRouter**: `https://openrouter.ai/api/v1`
- **OpenAI**: `https://api.openai.com/v1`
- **Azure OpenAI**, Anthropic, etc.

### Setting API Key

For security, **do not edit the config file manually** to set API keys.
Please use the **Settings...** menu in the system tray, which will automatically encrypt your key using Windows DPAPI.

## Usage

1. **Select text** in any application
2. **Press Ctrl+Alt+R** (or your configured hotkey)
3. The tray icon will animate (yellow/orange circles) while processing
4. Refined text will be copied to clipboard
5. If Auto-Paste is enabled, it will be pasted automatically

## Tray Menu

Right-click the tray icon for options:
- **Refine Now**: Manually trigger refinement
- **Transcribe Now**: Start toggle-based audio transcription
- **Settings...**: Configure hotkeys, models, and API keys
- **Encrypted Refinement History...**: View refinement history
- **Encrypted Transcription History...**: View transcription history
- **Start with Windows**: Toggle auto-start
- **Quit**: Exit application

## Logs

Application logs are stored in: `%APPDATA%\TailSlap\app.log`

## Building from Source

```bash
cd TailSlap
dotnet restore
dotnet build -c Release
dotnet publish -c Release
```

Output: `bin\Release\net9.0-windows\win-x64\publish\TailSlap.exe`

## Troubleshooting

### Hotkey not working
- Check if another application is using the same hotkey
- Try changing the hotkey via the tray menu

### LLM request failed
- Verify the LLM service is running (for local Ollama)
- Check the BaseUrl in config.json
- Check API key is set (for LLM providers)
- Review app.log for detailed errors

### Icon not appearing
- Check system tray overflow area (hidden icons)
- Ensure .NET 9 Runtime is installed

## Security Notes

- API keys are encrypted using Windows DPAPI (user-scoped)
- Use HTTPS for LLM endpoints
- HTTP is acceptable for localhost only
- Logs do NOT contain sensitive text content

## License

MIT License (or as specified by your organization)
