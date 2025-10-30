# TailSlap - Quick Start Guide

## What Was Built

A complete Windows WinForms tray application for text refinement via LLM services, built per your detailed plan.

## Project Structure

```
TailSlap/
├── TailSlap.csproj               # Project file (.NET 9, WinForms)
├── Program.cs                     # Entry point with single-instance mutex
├── MainForm.cs                    # Main form (hidden, tray-only)
├── ConfigService.cs               # JSON config management
├── Dpapi.cs                       # API key encryption (DPAPI)
├── ClipboardService.cs            # Clipboard operations
├── TextRefiner.cs                 # LLM HTTP client with retry logic
├── AutoStartService.cs            # Windows startup registry
├── Logger.cs                      # File logging
├── Properties/
│   ├── Resources.resx            # Embedded icons
│   └── Resources.Designer.cs     # Generated resource accessor
├── IconIdle.ico                  # Green circle (idle state)
├── IconWork1.ico                 # Yellow circle (working frame 1)
├── IconWork2.ico                 # Orange circle (working frame 2)
└── README.md                     # Full documentation

## Built Output

Location: `TailSlap\bin\Release\net9.0-windows\win-x64\publish\`

Files:
- `TailSlap.exe` - Main executable (156 KB)
- `TailSlap.dll` - Application assembly
- `TailSlap.runtimeconfig.json` - Runtime configuration
- `TailSlap.deps.json` - Dependency manifest

## How to Run

1. **Install .NET 9 Runtime** (if not already installed):
   Download from: https://dotnet.microsoft.com/download/dotnet/9.0

2. **Run the application**:
   ```
   TailSlap\bin\Release\net9.0-windows\win-x64\publish\TailSlap.exe
   ```

3. **First Run**:
   - App appears in system tray (green circle icon)
   - Config file created at: `%APPDATA%\TailSlap\config.json`
   - Default hotkey: `Ctrl+Alt+R`

## Quick Test

### Test with Local Ollama (No API Key)

1. **Install Ollama** (if not already): https://ollama.ai
2. **Start Ollama** and ensure it's running on port 11434
3. **Select some text** in Notepad or any app
4. **Press Ctrl+Alt+R**
5. Watch the tray icon animate (yellow/orange)
6. Refined text is pasted back

### Test with LLM Provider (e.g., OpenRouter)

1. **Get an API key** from https://openrouter.ai
2. **Edit config** at `%APPDATA%\TailSlap\config.json`:
   ```json
   {
     "Llm": {
       "BaseUrl": "https://openrouter.ai/api/v1",
       "Model": "anthropic/claude-3.5-sonnet",
       "ApiKeyEncrypted": null
     }
   }
   ```
3. **Add API key** (temporarily in plain text for testing):
   - Restart app
   - It will encrypt the key automatically on first use

**Note**: For production, use DPAPI to encrypt the key first.

## Configuration Examples

### OpenRouter (Claude 3.5 Sonnet)
```json
{
  "Llm": {
    "BaseUrl": "https://openrouter.ai/api/v1",
    "Model": "anthropic/claude-3.5-sonnet",
    "Temperature": 0.2
  }
}
```

### OpenAI (GPT-4)
```json
{
  "Llm": {
    "BaseUrl": "https://api.openai.com/v1",
    "Model": "gpt-4",
    "Temperature": 0.2
  }
}
```

### Local Ollama (Default)
```json
{
  "Llm": {
    "BaseUrl": "http://localhost:11434/v1",
    "Model": "llama3.1",
    "Temperature": 0.2
  }
}
```

## Tray Menu Options

Right-click the tray icon:
- **Refine Now**: Manually refine current clipboard/selection
- **Auto Paste**: Toggle auto-paste (checked = enabled)
- **Change Hotkey**: Opens dialog to set custom hotkey
- **Start with Windows**: Toggle auto-start on boot
- **Quit**: Exit application

## Features Implemented

✅ System tray icon with context menu  
✅ Global hotkey registration (Ctrl+Alt+R)  
✅ Single-instance mutex (only one copy runs)  
✅ Clipboard capture via Ctrl+C  
✅ OpenAI-compatible API client  
✅ Retry logic (2 attempts, 1s backoff) for 429/5xx errors  
✅ 30s timeout for slow LLM responses  
✅ DPAPI-encrypted API key storage  
✅ Auto-paste toggle  
✅ Custom hotkey configuration  
✅ Windows startup integration  
✅ File logging (no sensitive data)  
✅ Animated tray icon (idle → working → idle)  
✅ Balloon notifications for errors/success  

## Build Options

### Framework-Dependent (Current)
```bash
dotnet publish -c Release
```
- Small size (~156 KB EXE)
- Requires .NET 9 Runtime installed
- Fast startup

### Self-Contained (Optional)
```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```
- Large size (~80 MB single EXE)
- No .NET Runtime required
- Slower startup

**Note**: Self-contained build requires .NET 9 SDK with matching runtime versions.

## Next Steps

1. **Test the application** with your preferred LLM provider
2. **Customize the config** for your use case
3. **Set up API key** securely if using LLM provider
4. **Enable auto-start** if you want it to run on boot

## Troubleshooting

**App doesn't start:**
- Ensure .NET 9 Runtime is installed
- Check Windows Event Viewer for errors

**Hotkey doesn't work:**
- Another app might be using Ctrl+Alt+R
- Try changing to Ctrl+Alt+T or another combo

**LLM request fails:**
- For Ollama: Ensure it's running (`ollama serve`)
- For LLM providers: Check API key and BaseUrl
- Review logs at `%APPDATA%\TailSlap\app.log`

**Icon not visible:**
- Check system tray overflow area
- Windows might hide it by default

## Development Notes

- **Framework**: .NET 9 (targets net9.0-windows)
- **UI**: WinForms (System.Windows.Forms)
- **Icons**: Embedded as base64 in Resources.resx
- **Config**: JSON in %APPDATA%\TailSlap\
- **Encryption**: Windows DPAPI (user-scoped)
- **No external dependencies**: Uses only built-in .NET libraries

## Comparison to Original Plan

The implementation matches the plan with these changes:
- ✅ Used .NET 9 instead of .NET 8 (SDK availability)
- ✅ Removed Microsoft.VisualBasic dependency (custom dialog instead)
- ✅ Framework-dependent by default (self-contained optional)
- ✅ All other features implemented as specified

Enjoy your text refinement tool! 🎉
