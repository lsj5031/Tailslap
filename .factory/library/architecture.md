# Architecture

How the TailSlap typeless mode system works.

## What belongs here

High-level description of components, relationships, data flows, and invariants. Avoid implementation details.

---

## System Overview

TailSlap is a WinForms desktop tray app (.NET 9, Windows-only) with three primary features:
1. **Text refinement** (Ctrl+Alt+R) — capture selected text → send to LLM → paste refined result
2. **Typeless mode** (Ctrl+Alt+T) — hold to record speech → release → transcribe via SSE → type into focused app
3. **Streaming transcription** (Ctrl+Alt+Y) — real-time WebSocket bidirectional audio/transcription

## Typeless Mode Components

### KeyboardHook
Low-level Windows keyboard hook (`WH_KEYBOARD_LL`) that detects key-down and key-up for the configured transcriber hotkey. Replaces `RegisterHotKey` for this hotkey only. Handles auto-repeat suppression and modifier tracking (ignores modifier release before primary key release).

**Invariant**: Hook is installed on app start, removed on app exit. Only one hook instance exists. Hook callbacks are processed on the UI thread.

### TypelessController
State machine: `Idle → Recording → Processing → Idle`
- **Recording** (key-down): Creates AudioRecorder, records to temp WAV file, starts tray animation
- **Processing** (key-up): Stops recording, sends WAV to RemoteTranscriber.TranscribeStreamingAsync, types SSE chunks via TextTyper, saves to history
- **Error**: Any failure returns to Idle with notification

**Invariant**: Only one recording/transcription at a time. If already processing, new key-down is rejected.

### TextTyper
Types text into the foreground application's focused input. Uses hybrid approach:
- Clipboard paste (via ClipboardHelper) for chunks >5 characters
- SendKeys for chunks <=5 characters
- Backspace corrections via SendInput when server sends updated text (common-prefix algorithm)
- Foreground window monitoring — resets baseline if user switches apps

**Invariant**: Always checks foreground window before typing. Never types into wrong window.

### AudioRecorder (existing)
Records 16-bit PCM mono at 16kHz via WinMM API. Supports VAD (WebRTC or RMS). Records to temp WAV file. Disposable with RAII cleanup.

### RemoteTranscriber (existing)
HTTP client for OpenAI-compatible transcription API. Supports SSE streaming (`TranscribeStreamingAsync`) that yields text chunks as `IAsyncEnumerable<string>`. Falls back to full response if server doesn't stream.

## Data Flow

```
Key-down detected by KeyboardHook
  → TypelessController.OnKeyDown()
  → AudioRecorder.RecordAsync() (writes to temp WAV)
  → Tray animation: "Recording..."

Key-up detected by KeyboardHook
  → TypelessController.OnKeyUp()
  → AudioRecorder stops
  → Tray animation: "Transcribing..."
  → RemoteTranscriber.TranscribeStreamingAsync(tempWav)
  → For each SSE chunk:
      → TextTyper.TypeAsync(chunk, targetWindow)
      → Hybrid: clipboard paste or SendKeys
  → HistoryService.AppendTranscription(text, duration)
  → Delete temp WAV
  → Tray animation: idle
```

## Key Invariants

1. **State isolation**: Recording and processing are mutually exclusive. Only one active at a time.
2. **Resource cleanup**: Temp WAV files always deleted in finally block. AudioRecorder always disposed.
3. **Thread safety**: Keyboard hook callbacks marshaled to UI thread. SendKeys/SendInput require STA thread.
4. **No sensitive data in logs**: SHA256 fingerprints only.
5. **DPAPI encryption**: All history entries encrypted.
6. **Minimum duration**: Recordings <500ms are discarded without transcription.
7. **Maximum duration**: Safety net auto-stop at 60 seconds.
8. **Existing features untouched**: Refinement (Ctrl+Alt+R) and streaming transcription (Ctrl+Alt+Y) remain unchanged.
