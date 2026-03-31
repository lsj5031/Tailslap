# Changelog

All notable changes to TailSlap will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [3.0.6] - 2026-04-01

### Fixed
- **GitHub Actions Node 24 opt-in**: CI workflows now set `FORCE_JAVASCRIPT_ACTIONS_TO_NODE24=true`, which suppresses GitHub's temporary Node 20 deprecation warning for JavaScript-based actions while upstream actions finish their Node 24 migration.

## [3.0.5] - 2026-04-01

### Changed
- **Typeless auto-enhancement**: Push-to-talk transcription now runs the same post-transcription LLM auto-enhancement gate as standard transcription mode, and if the refined result passes safety checks it replaces the streamed draft in place.
- **GitHub Actions runtime updates**: CI workflows now use the Node 24-capable major versions of `actions/checkout`, `actions/setup-dotnet`, and `actions/upload-artifact` to avoid GitHub's Node 20 deprecation warnings.

## [3.0.4] - 2026-04-01

### Fixed
- **Release workflow self-contained publish**: GitHub Actions now restores the `win-x64` runtime packs before `--no-restore` self-contained publish steps, fixing the failed Windows release packaging run.

## [3.0.3] - 2026-04-01

### Changed
- **Realtime stop responsiveness**: Pressing the realtime hotkey a second time now uses a much shorter adaptive shutdown wait, so local OpenAI-protocol sessions stop feeling stuck in processing when there is no meaningful tail left to flush.
- **Realtime debugging notes**: Documented the current JSONL log location, safe `jq` filters, and the local OpenAI-protocol `glm-nano-2512` setup used for troubleshooting.

### Fixed
- **Custom realtime duplication**: Legacy/custom realtime transcript finalization now avoids retyping earlier text when the server sends cumulative follow-up updates.
- **Realtime capture stalls**: Streaming audio recovery now re-arms empty completed WinMM buffers and recovers much faster after sustained capture gaps, reducing dropped speech in realtime mode.

## [3.0.2] - 2026-04-01

### Changed
- **Hotkey setup validation**: Hotkey capture now validates conflicts live in Settings, showing green only for available shortcuts and red when the combination is already used by TailSlap or another application.
- **Paste delivery reliability**: Clipboard paste now marshals back onto the UI thread when needed and tries `WM_PASTE` against focused native edit controls before keyboard-driven fallbacks.
- **Direct typing reliability**: Incremental typing paths now prefer Unicode `SendInput` before falling back to `SendKeys`, improving punctuation and non-ASCII text delivery.

### Fixed
- **Hotkey capture side effects**: Pressing an existing TailSlap shortcut while the Settings dialog is open no longer triggers refine/transcribe actions instead of just capturing the key combination.
- **Hotkey conflict messaging**: Registration failures now report whether Windows says the shortcut is already owned by another application.
- **Streaming text entry compatibility**: Realtime and typeless text insertion now degrade more gracefully when clipboard paste is unavailable.

## [3.0.1] - 2026-03-31

### Changed
- **Recording overlay visuals**: The floating overlay now uses a capsule-shaped window region instead of a keyed rectangular background, preserving the animated waveform while removing the visible grey box artifact.
- **Waveform motion**: Recording and streaming overlays now use flowing staggered bar phases with RMS-driven updates so the bars stay lively during silence and respond more naturally during speech.

## [3.0.0] - 2026-03-31

### Added
- **Toggle Transcription Mode**: Restored press-to-start/press-to-stop transcription on `Ctrl+Alt+T` (via Win32 `RegisterHotKey`). Press once to start recording, press again to stop and transcribe. Includes optional LLM auto-enhancement for longer transcriptions. Uses `ClipboardHelper` for result delivery.
- **Typeless / Push-to-Talk Mode**: Moved push-to-talk recording to a new `Ctrl+Win` hold-to-record hotkey (via `KeyboardHook` WH_KEYBOARD_LL). Hold both modifiers to record, release either to stop and transcribe with SSE streaming via `TextTyper`.
- **Modifier-only hotkey support**: `KeyboardHook` now supports `HotkeyConfig.Key == 0`, meaning no primary key is required — the hook fires when the configured modifier combination is fully held and stops when any required modifier is released.
- **Push-to-Talk Hotkey setting**: Added `TypelessHotkey` configuration property and Settings UI capture field for customizing the push-to-talk hotkey independently from the toggle transcription hotkey.

### Changed
- **Four operating modes**: TailSlap now has four distinct modes: Refinement (Ctrl+Alt+R), Toggle Transcription (Ctrl+Alt+T), Typeless Push-to-Talk (Ctrl+Win hold), and Realtime Streaming (Ctrl+Alt+Y).
- **KeyboardHook uses TypelessHotkey**: The keyboard hook is now configured from `TypelessHotkey` instead of `TranscriberHotkey`, separating the toggle and push-to-talk hotkey mechanisms.

## [2.1.0] - 2026-03-31

### Added
- **Typeless (push-to-talk) transcription mode**: Hold-to-record hotkey (default Ctrl+Shift+') that captures audio via a low-level keyboard hook, transcribes with SSE streaming, and types the result into the focused application incrementally. Includes state-aware tray animation (fast during recording, slow during transcription), auto-repeat suppression, 500ms minimum recording guard, and 60s max recording safety net.

### Changed
- **State-aware tray animation**: Tray icon animation now uses distinct speeds for different states: fast (50ms) during push-to-talk recording, medium (75ms) during refinement/streaming, and slow (200ms) during transcription. The tooltip text also updates to reflect the current state ("Recording...", "Transcribing...", "Processing...").

### Fixed
- **KeyboardHook not installing on startup**: Fixed `KeyboardHook.Reconfigure()` not calling `Install()` when the hook hadn't been previously installed, causing the push-to-talk hotkey to be completely unresponsive after app launch or restart.

## [2.0.9] - 2026-03-29

### Changed
- **Standard Transcription Mode**: Standard push-to-talk transcription now always uses the non-streaming `/audio/transcriptions` request path, even if `StreamResults` is enabled. Live incremental streaming remains available through the dedicated realtime hotkey.
- **Transcription History Semantics**: Standard transcription history now keeps the raw transcription result, while enhanced output is mirrored into refinement history as a raw-to-refined pair when auto-enhancement changes the text.

### Fixed
- **Manual Stop Auto-Enhancement**: Stopping recording with the transcription hotkey no longer cancels the later LLM enhancement step, so long recordings can still be auto-refined after you stop recording.
- **Standard Transcription Truncation**: Avoided fragmentary standard transcription results caused by SSE chunk aggregation on non-realtime endpoints by routing standard transcription through the full-response path.

## [2.0.8] - 2026-03-29

### Added
- **OpenAI-Compatible Realtime Provider**: Added an `openai` realtime transcription mode that connects to `/v1/realtime?intent=transcription`, supports session update events, and handles OpenAI-style transcription event names alongside the existing custom streaming provider.
- **Realtime Compatibility Probe Script**: Added `scripts/Test-OpenAIRealtimeTranscription.ps1` to validate OpenAI-compatible realtime transcription endpoints end-to-end.

### Changed
- **Realtime Provider Selection**: Exposed realtime provider selection in settings so TailSlap can switch between the richer custom streaming protocol and the OpenAI-compatible transcription WebSocket flow.
- **Repository Automation Links**: Updated docs and GitHub workflows to track the repository's current `master` default branch and GitHub home.

### Fixed
- **Realtime Item Ordering**: Preserved `item_id` / `previous_item_id` ordering so independent utterances do not overwrite each other during OpenAI-style realtime transcription.
- **Realtime Finalization During Stop**: Final transcript events now still finalize local state even when the final text matches the last preview, preventing stale item bookkeeping and lost completion handling.
- **Silence Stop Compatibility**: Local stop-on-silence now cooperates better with server-driven turn detection by allowing late realtime updates to settle during shutdown.

## [2.0.7] - 2026-03-29

### Changed
- **UI Automation Isolation**: Moved selection-reading UI Automation work into an isolated helper-process mode so buggy accessibility providers cannot crash the main tray app during refinement.

### Fixed
- **Refine Crash Containment**: If UI Automation hangs, faults, or returns invalid output for the active app, TailSlap now logs the helper failure and falls back to Win32/clipboard capture instead of terminating.

## [2.0.6] - 2026-03-29

### Added
- **Custom Refinement Prompt**: Added a configurable `Llm.RefinementPrompt` setting and corresponding Settings UI field so refinement behavior can be tuned without rebuilding.
- **Realtime WebSocket Timeout Controls**: Added configurable connection, receive, send, heartbeat interval, and heartbeat timeout settings for real-time transcription.

### Changed
- **Refinement Prompting**: Reworked the default refinement instructions to better clean up dictated text while preserving meaning, structure, and professional tone.
- **Realtime Shutdown Flow**: Allowed final transcription updates to continue during shutdown so delayed server-finalized text can still be applied before cleanup completes.

### Fixed
- **OpenAI-Compatible `max_tokens` Interop**: Stopped sending `max_tokens: null`, which fixed one-token truncation from certain local OpenAI-compatible proxy endpoints.
- **Broken Refinement Output Guardrails**: Added recovery logic and validation for suspiciously short refinement results so obviously incomplete model output is not pasted over user text.
- **Refine Stability**: Hardened cancellation, null checks, and clipboard/UIA error handling to reduce intermittent refine crashes.
- **Realtime Connection Recovery**: Added heartbeat-based connection monitoring, timeout handling, and connection-loss recovery for real-time transcription sessions.
- **Realtime Final Transcript Loss**: Fixed a shutdown bug where final server text could arrive after stop had begun and be ignored by the client.

## [2.0.5] - 2026-03-28

### Added
- **WebRTC Voice Activity Detection**: Added ML-based WebRTC VAD with configurable sensitivity for both push-to-talk and real-time transcription modes.
- **Transcription Auto-Enhancement Controls**: Added settings to auto-enhance longer transcriptions with the configured LLM after a configurable character threshold.
- **Clipboard Helper Abstraction**: Added a shared clipboard/paste helper to reduce duplication between refinement and transcription workflows.

### Changed
- **Real-Time Streaming Performance**: Replaced byte-by-byte buffering with `MemoryStream` aggregation in the real-time transcription path.
- **Diagnostics Pipeline**: Moved config change debouncing to `System.Threading.Timer` and switched diagnostics HTTP calls to `HttpClientFactory`.
- **Secure Logging**: Expanded SHA256 fingerprint logging for transcription and streaming output so troubleshooting stays useful without logging raw text.

### Fixed
- **WebRTC VAD Deployment**: Ensured `WebRtcVad.dll` is copied into publish output so release builds can actually enable WebRTC VAD.
- **Paste Reliability**: Normalized modifier key state before paste, prioritized `Ctrl+V`, and added foreground-window diagnostics for paste troubleshooting.
- **Realtime Stop Behavior**: Prevented real-time transcription from continuing to backspace/type after stop is requested.
- **Unsafe Auto-Enhancement Output**: Rejected obviously bad LLM rewrites, such as aggressive shrinkage or low-overlap replacements, and fall back to the original transcription.
- **Audio Recording Shutdown**: Hardened WinMM cleanup to avoid `waveInUnprepareHeader` still-playing errors and switched standard recording shutdown to a reset-backed final drain path.
- **Clipboard Integration Regressions**: Fixed helper wiring so enhanced transcription output is what gets placed on the clipboard and pasted.

## [2.0.2] - 2026-01-06

### Fixed
- **Clipboard Thread Safety**: Added UI thread marshaling for clipboard operations to prevent cross-thread exceptions
- **Clipboard Service**: Enhanced SetText method with proper synchronization context handling and 5-second timeout
- **Transcription History**: Added new TranscriptionHistoryForm.cs for viewing encrypted transcription history with decryption status

### Added
- **UI Thread Management**: ClipboardService.Initialize() method for proper UI synchronization context setup
- **Enhanced Logging**: Added detailed thread context logging for clipboard operations debugging
- **Transcription History UI**: Complete form implementation with real-time refresh, corruption detection, and clear functionality

## [2.0.1] - 2026-01-06

### Fixed
- **Build System**: Added missing UIAutomation framework reference for Windows Desktop App compatibility
- **Namespace Resolution**: Added missing `using TailSlap;` directives to MainForm.cs, Program.cs, and test files
- **Test Configuration**: Fixed invalid assignment to read-only `WebSocketUrl` property in transcription tests
- **Code Formatting**: Applied csharpiER formatting to all 56 files for consistent code style

### Changed
- **Development Tools**: Switched from `dotnet format` to csharpiER (v1.2.5) for proper code formatting
- **Pre-commit Hooks**: Ensured all code passes csharpiER validation before commits

## [2.0.0] - 2026-01-05

### Added
- **Comprehensive AI-Assisted Clipboard Refinement**: Full OpenAI-compatible LLM integration with retry logic (2 attempts, 1s backoff)
- **Remote Transcription System**: HTTP-based transcription with OpenAI-compatible API endpoints and multipart form data
- **Real-time Streaming Transcription**: WebSocket-based bi-directional audio streaming with 500ms buffer aggregation
- **Professional Audio Recording**: WinMM API integration with SafeHandle RAII for 16-bit mono, 16kHz WAV recording
- **Voice Activity Detection (VAD)**: Configurable three-level thresholds (activation/sustain/silence) with 2s silence detection
- **Triple Hotkey System**: Global hotkeys for refinement (Ctrl+Alt+R), transcription (Ctrl+Alt+T), and streaming (Ctrl+Alt+Y)
- **System Tray Integration**: Hidden main form with animated 8-frame icon (75ms intervals) and pulsing tooltip
- **Encrypted Configuration**: JSON config with Windows DPAPI encryption for API keys and FileSystemWatcher hot reloading
- **Transcription History**: Encrypted JSONL storage with decryption status and clear functionality
- **Settings UI**: Comprehensive settings form with LLM and Transcriber configuration tabs and validation
- **Dependency Injection**: Microsoft.Extensions.DependencyInjection container with service registration
- **HTTP Client Factory**: Centralized HTTP client with connection pooling, compression, and configurable timeouts
- **ETW Diagnostics**: EventSource with 14 events across 7 diagnostic categories for performance monitoring
- **Single-instance Architecture**: Mutex-based prevention of multiple app instances
- **Windows Integration**: Registry-based startup integration and high DPI awareness (PerMonitorV2)
- **Security Features**: SHA256 fingerprinting for secure logging and Windows DPAPI encryption
- **Unit Testing**: xUnit framework with Moq for dependency mocking across core services

### Fixed
- **Audio Recording Stability**: Proper WinMM error handling and device cleanup
- **Clipboard Fallback**: Enhanced Ctrl+C fallback mechanism for clipboard capture failures
- **Memory Management**: Optimized real-time streaming with proper buffer management
- **Error Recovery**: Graceful degradation for encryption/decryption failures

### Changed
- **Architecture**: Modernized to .NET 9 with C# 12 nullable reference types
- **Configuration**: Enhanced validation with static helper methods for URL, temperature, and model validation
- **UI/UX**: Improved user feedback with balloon tip notifications and better error messages
- **Code Style**: Applied consistent C# 12 formatting and naming conventions throughout

## [1.6.2] - 2025-12-25

### Fixed
- Build configuration and deployment issues
- Updated project dependencies and build scripts

## [1.6.1] - 2025-12-20

### Changed
- Improved hotkey defaults for better user experience
- Enhanced configuration handling and validation
- Updated user interface elements for better usability

## [1.6.0] - 2025-12-19

### Changed
- Updated AGENTS.md with modernized architecture details
- Improved documentation structure and clarity

## [1.5.0] - 2025-12-19

### Added
- Enhanced system tray functionality
- Improved configuration management
- Better error handling and user feedback

## [1.4.1] - 2025-12-19

### Fixed
- Animation table corrected to match 8 frames with 8 columns
- Icon loading improvements for better visual consistency
- Documentation updates for animation system

### Changed
- Improved code formatting for better readability
- Enhanced TryLoadIco method calls with proper line breaks

## [1.3.4] - 2025-12-18

### Changed
- Major code style and documentation cleanup across all services
- Improved code organization and consistency

## [1.3.3] - 2025-12-18

### Added
- Transcription history support with encrypted storage
- Enhanced history management features

### Changed
- Improved transcription workflow and user experience

## [1.3.2] - 2025-12-18

### Fixed
- Resolved AudioRecorder syntax errors
- Implemented proper audio recording loop
- Enhanced audio recording stability and error handling

## [1.3.1] - 2025-12-17

### Added
- Audio transcription support with configurable hotkey (Ctrl+Alt+T)
- Real-time streaming transcription with WebSocket support (Ctrl+Alt+Y)
- Remote transcription service integration (OpenAI-compatible endpoints)
- Audio recording functionality using Windows Multimedia API (WinMM)
- Voice Activity Detection (VAD) with configurable silence threshold
- Transcription history form for viewing and managing transcribed audio
- Microphone recording with 16-bit mono, 16kHz WAV output
- Configurable transcription endpoint and API key settings
- Transcription history management with clear functionality
- WebSocket streaming with 500ms buffer aggregation for optimal performance
- Microphone device selection support

### Fixed
- AudioRecorder.cs syntax errors and proper recording loop implementation
- Enhanced audio recording stability and error handling

## [1.3.0] - 2025-12-16

### Added
- Real-time WebSocket streaming transcription client
- Enhanced audio recording with Voice Activity Detection
- Configurable silence detection threshold (default: 2000ms)
- Microphone device selection and management
- 500ms audio buffer aggregation for streaming optimization
- Separate hotkey for streaming transcription (Ctrl+Alt+Y)
- WebSocket URL auto-construction from HTTP endpoints
- Enhanced error handling for audio recording failures

## [1.1.0] - 2025-12-16

### Added
- Single-file EXE distribution with embedded icons
- Continuous tray icon animation during text processing (8 frames, 75ms intervals)
- Enhanced clipboard handling with Ctrl+C fallback mechanism
- UI Automation stability improvements with timeout and cleanup
- Dependency injection container with service registration
- HTTP Client Factory with connection pooling and compression
- SHA256 fingerprinting for secure logging
- ETW diagnostics integration for performance monitoring

## [1.0.2] - 2025-12-18

### Added
- Transcription history support (same as v1.3.3)

## [1.0.1] - 2025-12-17

### Added
- Audio transcription features (same as v1.3.1)

## [1.0.0] - 2025-11-24

### Added
- System tray integration with animated context menu
- Global hotkey registration for three functions (Refinement, Transcription, Streaming)
- Text refinement via OpenAI-compatible LLM endpoints with retry logic
- Support for local (Ollama) and cloud (OpenAI, OpenRouter, etc.) LLM providers
- Automatic clipboard capture with intelligent fallback mechanisms
- Customizable hotkey configuration with validation
- Windows startup integration via registry (auto-start on boot)
- JSON configuration file support with hot reloading (`%APPDATA%\TailSlap\config.json`)
- Windows DPAPI encryption for API keys and history data
- Retry logic with exponential backoff (2 attempts, 1s backoff)
- Configurable timeout for LLM requests (30 seconds default)
- Smooth animated tray icon during processing (8 frames, 75ms intervals)
- Secure file logging with SHA256 fingerprinting (`%APPDATA%\TailSlap\app.log`)
- Balloon tip notifications for success/error/warning feedback
- Single-instance mutex to prevent multiple app instances
- No external NuGet dependencies (built-in .NET libraries only)
- High DPI awareness with PerMonitorV2 support
- Global exception handling for UI and non-UI exceptions

### Technical Details
- Built with .NET 9, Windows Forms, and WPF support
- Targets Windows 10 and later with high DPI awareness
- Framework-dependent distribution (~156 KB executable)
- Self-contained build available (~80 MB single file)
- Dependency Injection with Microsoft.Extensions.DependencyInjection
- HTTP Client Factory with connection pooling (5 min lifetime) and automatic compression
- Windows DPAPI encryption with DataProtectionScope.CurrentUser
- WinMM API integration for professional audio recording
- WebSocket client for real-time bi-directional streaming
- ETW EventSource with 14 events across 7 diagnostic categories
- 8-frame tray icon animation with 75ms intervals and 300ms tooltip pulses

## Future Considerations

### Potential Additions
- Refinement history with search and export capabilities
- Multiple hotkey profiles for different workflows
- Custom system prompts and template management
- Batch refinement for multiple text selections
- Cross-platform support (Linux/macOS via .NET MAUI)
- Plugin architecture for custom LLM providers
- Voice commands for hands-free operation
- Advanced audio processing (noise reduction, echo cancellation)
- Real-time collaboration features
- Cloud synchronization for settings and history
