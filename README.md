# TailSlap

A Windows utility that enhances your clipboard and text refinement experience with AI-powered processing. TailSlap runs in the system tray and allows you to quickly refine selected text using LLM services.

## Features

- **Text Refinement**: Process and enhance selected text with a hotkey
- **Clipboard Integration**: Automatically paste refined text back into your applications
- **Customizable Hotkeys**: Set your preferred keyboard shortcut for text refinement
- **History**: View and manage your refinement history
- **System Tray Integration**: Runs quietly in the background
- **Auto-start Option**: Launch on Windows startup

## Installation

1. Download the latest release from the [releases page](https://github.com/yourusername/TailSlap/releases)
2. Run the installer or extract the portable version
3. The application will start automatically and appear in your system tray

## Usage

1. Select text in any application
2. Press the configured hotkey (default: `Ctrl+Alt+R`)
3. The text will be processed and automatically pasted back (if enabled)

### System Tray Menu

Right-click the TailSlap icon in the system tray to access:
- **Refine Now**: Process the currently selected text
- **Auto Paste**: Toggle automatic pasting of refined text
- **Change Hotkey**: Set a custom keyboard shortcut
- **Settings**: Configure application preferences
- **History**: View and manage your refinement history
- **Start with Windows**: Toggle automatic startup with Windows

## Configuration

Configuration is stored in a JSON file located at:
`%APPDATA%\TailSlap\config.json`

You can edit this file directly or use the Settings dialog in the system tray menu.

## Logs

Application logs are stored at:
`%APPDATA%\TailSlap\app.log`

## Requirements

- Windows 10 or later
- .NET 6.0 or later
- Internet connection for LLM processing

## Building from Source

1. Clone the repository
2. Open the solution in Visual Studio 2022 or later
3. Restore NuGet packages
4. Build the solution

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

## Support

For support, please [open an issue](https://github.com/yourusername/TailSlap/issues) on GitHub.
