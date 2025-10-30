# TailSlap - Text Refinement Tool

A Windows system tray application that refines text using AI-powered LLM services.

## Features

- **System Tray Application**: Runs minimized in the system tray
- **Global Hotkey**: Press `Ctrl+Alt+R` to refine selected text
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
    "ApiKeyEncrypted": null,
    "HttpReferer": null,
    "XTitle": null
  }
}
```

### Configuration Options

- **AutoPaste**: Automatically paste refined text after processing
- **Hotkey.Modifiers**: Modifier keys (3 = Ctrl+Alt, 5 = Ctrl+Shift, 6 = Alt+Shift)
- **Hotkey.Key**: Key code (82 = R, 84 = T, etc.)
- **Llm.BaseUrl**: LLM API endpoint (OpenAI-compatible)
- **Llm.Model**: Model name
- **Llm.Temperature**: Creativity (0.0-1.0)
- **Llm.ApiKey**: API key (set via code, will be encrypted)

### Supported LLM Providers

Any OpenAI-compatible API:
- **Ollama** (default): `http://localhost:11434/v1`
- **OpenRouter**: `https://openrouter.ai/api/v1`
- **OpenAI**: `https://api.openai.com/v1`
- **Azure OpenAI**, Anthropic, etc.

### Setting API Key

To set an API key, edit the config manually and add:
```json
"ApiKeyEncrypted": "base64-encrypted-value"
```

Or use code to encrypt:
```csharp
var key = Dpapi.Protect("your-api-key-here");
```

## Usage

1. **Select text** in any application
2. **Press Ctrl+Alt+R** (or your configured hotkey)
3. The tray icon will animate (yellow/orange circles) while processing
4. Refined text will be copied to clipboard
5. If Auto-Paste is enabled, it will be pasted automatically

## Tray Menu

Right-click the tray icon for options:
- **Refine Now**: Manually trigger refinement
- **Auto Paste**: Toggle automatic pasting
- **Change Hotkey**: Configure custom hotkey
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
