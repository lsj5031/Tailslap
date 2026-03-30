# TextTyper

Text typing subsystem for typeless mode. Types text into the foreground application's focused input.

## Location
- Implementation: `TailSlap/TextTyper.cs`
- Tests: `TailSlap.Tests/TextTyperTests.cs` (59 tests)

## Key Design Decisions
- Hybrid clipboard/SendKeys approach: clipboard paste for text > 5 chars, SendKeys for <= 5 chars
- Text containing Unicode characters (> 127) always uses clipboard paste (SendKeys can't handle Unicode)
- Text containing newlines always uses clipboard paste (preserves line breaks)
- Backspace corrections via SendInput with common-prefix algorithm to minimize keystrokes
- Foreground window monitoring: if target window changes during typing, baseline is reset and text is placed on clipboard
- SendKeys special characters (+, ^, %, ~, (, ), [, ], {, }) are properly escaped in `{char}` format
- `\n` converted to `{ENTER}`, `\r` stripped
- If all delivery methods fail, text remains on clipboard and user is notified
- SendInput fallback to SendKeys `{BS N}` for backspaces if SendInput event count mismatch
- Thread-safe state management via `_stateLock` for baseline text and target window

## Delivery Fallback Chain
1. **Long text (>5) or Unicode or multiline**: `ClipboardService.SetTextAndPasteAsync` → SendKeys fallback (ASCII only) → `ClipboardService.SetText` as last resort
2. **Short text (<=5)**: `SendKeys.SendWait` → `ClipboardService.SetTextAndPasteAsync` fallback → `ClipboardService.SetText` as last resort

## Interface
- `TypeAsync(string text, bool autoPaste, IntPtr? foregroundWindow, CancellationToken)` → `TypeResult`
- `ResetBaseline()` — clears text baseline and target window
- `SetBaseline(string text, IntPtr targetWindow)` — sets previously typed text
- Static: `EscapeForSendKeys(string)`, `ContainsUnicode(string)`, `ContainsNewline(string)`, `GetCommonPrefixLength(string, string)`

## TypeResult
- `DeliverySuccess` — whether text was delivered to the target app
- `TextOnClipboard` — whether text is available on clipboard
- `Text` — the full text that was attempted
- `NewText` — the portion that needed to be typed (after correction)
- `BackspaceCount` — number of backspace keystrokes sent
- `WindowChanged` — whether the foreground window changed

## Integration Notes (for future workers)
- TextTyper is NOT yet registered in Program.cs DI — that's the typeless-controller or mainform-integration feature's responsibility
- Depends on `IClipboardService` (already registered)
- TextTyper should be registered as a singleton since it tracks baseline state across a transcription session
- The `TypeAsync` method should be called from the UI thread (SendKeys/SendInput require STA thread)
- P/Invoke: `GetForegroundWindow`, `SendInput`, `MapVirtualKey` from user32.dll
- Internal methods (`CalculateBackspaceCount`, `IsForegroundWindowChanged`, `CheckWindowChanged`) for testability via reflection

## Validation Contract Coverage
- VAL-TYPE-001: Text typed into focused application ✅
- VAL-TYPE-002: Clipboard paste for long text (>5 chars) ✅
- VAL-TYPE-003: SendKeys for short text (<=5 chars) ✅
- VAL-TYPE-004: Backspace corrections with common-prefix algorithm ✅
- VAL-TYPE-005: Foreground window monitoring, baseline reset on change ✅
- VAL-TYPE-006: Special characters escaped for SendKeys ✅
- VAL-TYPE-007: Unicode text via clipboard paste ✅
- VAL-TYPE-008: Multi-line text with preserved line breaks ✅
- VAL-TYPE-009: Delivery failure preserves text on clipboard ✅
- VAL-TYPE-010: AutoPaste disabled puts text on clipboard only ✅
- VAL-THREAD-002: SendKeys/SendInput run on STA thread (architectural constraint) ✅
