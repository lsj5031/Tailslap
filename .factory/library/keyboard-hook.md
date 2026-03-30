# KeyboardHook

Low-level keyboard hook for transcriber hotkey detection.

## Location
- Implementation: `TailSlap/KeyboardHook.cs`
- Tests: `TailSlap.Tests/KeyboardHookTests.cs` (29 tests)

## Key Design Decisions
- Uses `SetWindowsHookEx(WH_KEYBOARD_LL)` — global low-level keyboard hook
- Hook callback delegates stored as field (`_hookCallback`) to prevent GC collection
- Internal methods (`ProcessKeyDown`, `ProcessKeyUp`, `ProcessModifierChange`) for testability via reflection
- Auto-repeat suppressed by tracking `_primaryKeyHeld` state
- Modifier release does NOT affect recording — only primary key release fires OnKeyUp
- Max duration safety net uses `System.Threading.Timer` — fires `ForceStop()` after configurable duration (default 60s)
- `Reconfigure()` uninstalls + reinstalls the hook atomically
- `IsInstalled` tracks hook state via `_hookId != IntPtr.Zero`
- `Dispose()` calls `Uninstall()` to ensure `UnhookWindowsHookEx` is called

## P/Invoke
- `SetWindowsHookEx` / `UnhookWindowsHookEx` / `CallNextHookEx` from user32.dll
- `GetModuleHandle` from kernel32.dll for hook module handle
- `GetAsyncKeyState` from user32.dll for modifier state detection

## Events
- `OnKeyDown`: Fired once per hotkey press (auto-repeat suppressed)
- `OnKeyUp`: Fired when primary key released OR ForceStop() called (max duration safety)

## Integration Notes (for future workers)
- KeyboardHook is NOT yet wired into MainForm — that's a separate feature's responsibility
- The hook replaces RegisterHotKey for TRANSCRIBER_HOTKEY_ID only
- Hook callbacks run on the thread that installed the hook (UI thread via message pump)
- Should be registered in Program.cs DI as a singleton
