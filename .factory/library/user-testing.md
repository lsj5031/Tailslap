# User Testing

Testing surface and validation strategy for the Typeless Mode mission.

**What belongs here:** Testing surface findings, required testing skills/tools, resource cost classification.

---

## Validation Surface

This is a WinForms desktop application that captures global keyboard input and types into other applications.

**Primary surface**: The running TailSlap.exe interacting with foreground applications (e.g., Notepad, browser text fields).

**Testing approach**: Manual validation by the user. The app captures global keyboard hooks and types into other applications — this cannot be automated via browser testing tools. The user tests by:
1. Running TailSlap.exe
2. Opening a target application (e.g., Notepad)
3. Pressing and holding the transcriber hotkey (Ctrl+Alt+T)
4. Speaking into the microphone
5. Releasing the hotkey
6. Observing text appearing in the target application

**Required tools**: Manual testing by the user. No automated UI testing tools are applicable.

**Limitations**:
- Global keyboard hooks cannot be tested in headless/CI environments
- SendKeys/SendInput require a real Windows desktop session with interactive window station
- Microphone input requires physical or virtual audio device
- Transcription server must be running for end-to-end validation

## Validation Concurrency

**Max concurrent validators**: 1 (manual testing by a single user on a single desktop session)

**Rationale**: The typeless mode captures global keyboard input, which is inherently exclusive — only one application can process a given hotkey at a time. The user must test sequentially.

## Resource Cost

- **App memory**: ~50-80 MB (WinForms + .NET runtime)
- **Audio recording**: Negligible additional memory (8 buffers × 6400 bytes)
- **No browser overhead**: This is not a web application
