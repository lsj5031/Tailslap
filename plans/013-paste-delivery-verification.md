# Plan 013: Make paste delivery honest — verify where possible, eliminate known silent-failure classes

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md` — unless a reviewer dispatched you and told you they
> maintain the index.
>
> **Drift check (run first)**:
> `git diff --stat f3016ac..HEAD -- TailSlap/ClipboardService.cs TailSlap/NativeInputSimulator.cs TailSlap.Tests/ClipboardServiceTests.cs TailSlap.Tests/NativeInputSimulatorTests.cs`
> If any in-scope file changed since this plan was written, compare the
> "Current state" excerpts against the live code before proceeding; on a
> mismatch, treat it as a STOP condition.

## Status

- **Priority**: P1
- **Effort**: M
- **Risk**: MED
- **Depends on**: none
- **Category**: bug
- **Planned at**: commit `f3016ac`, 2026-07-30

## Why this matters

Users report the app's auto-paste of transcribed/refined text "sometimes does nothing" — the text is on the clipboard (manual Ctrl+V works) but never appears in the target app. Root cause: every paste method in `ClipboardService` returns `true` unconditionally unless it throws, so silently dropped keystrokes (still-held hotkey modifiers corrupting the Ctrl+V chord, elevated target windows filtering synthetic input via UIPI, read-only edit controls ignoring WM_PASTE) are reported as success — no fallback method runs and no notification fires. This plan makes failure detectable where it can be detected and removes the known silent-failure preconditions before sending the paste.

## Current state

### Files

- `TailSlap/ClipboardService.cs` (1973 lines) — clipboard + paste implementation. Paste chain: `PasteAsyncCore` (~line 1075) → `PasteWithMultipleMethodsAsync` (~1130) → `TryPasteWindowMessageAsync` / `TryPasteCtrlVAsync` / `TryPasteShiftInsertAsync` / `TryPasteSendInputAsync` (~1180-1275). `NormalizeInputState()` (~1836) sends synthetic modifier KEYUPs. `SupportsWindowMessagePaste` (~1277) gates WM_PASTE by window class name.
- `TailSlap/NativeInputSimulator.cs` (245 lines) — shared static SendInput helpers (`SendBackspace`, `TypeTextDirectly`, `BuildUnicodeInputs`, `EscapeForSendKeys`).
- `TailSlap/NativeMethods.cs` — shared internal P/Invoke (`GetForegroundWindow`, `GetWindowThreadProcessId`, `GetGUIThreadInfo`).
- `TailSlap.Tests/ClipboardServiceTests.cs` — currently only trivial smoke tests (instance creation).

### The unconditional-success bug (ClipboardService.cs ~1220-1236)

```csharp
private async System.Threading.Tasks.Task<bool> TryPasteCtrlVAsync()
{
    try
    {
        NormalizeInputState();
        SendKeys.SendWait("^v");
        await Task.Delay(75).ConfigureAwait(true);
        return true;                       // <-- returns true even if nothing pasted
    }
    catch
    {
        return false;
    }
}
```

`TryPasteShiftInsertAsync`, `TryPasteSendInputAsync`, and `TryPasteWindowMessageAsync` have the same shape (WM_PASTE version returns true right after `SendMessage(targetWindow, WM_PASTE, ...)`). `PasteWithMultipleMethodsAsync` stops at the first `true`, so the later fallbacks are effectively dead code — `SendKeys.SendWait` almost never throws.

### The modifier race (ClipboardService.cs ~1836-1878)

`NormalizeInputState()` checks `GetAsyncKeyState` for Ctrl/Alt/Shift/Win, sends one batch of synthetic `KEYEVENTF_KEYUP` events, then `Thread.Sleep(20)`. There is **no wait for physical release**: if the user still physically holds Ctrl+Alt (they just pressed the stop hotkey), hardware auto-repeat re-asserts the modifiers within ~30ms and the synthesized `^v` becomes Ctrl+Alt+V — a no-op in most apps, still reported as success.

### No elevation check anywhere

Neither `ClipboardService` nor `NativeInputSimulator` checks whether the foreground process is elevated. When the target runs as admin and TailSlap does not, UIPI silently discards all `SendInput`/`SendKeys` events (the `SendInput` return count is still nonzero). Pasting into an admin terminal/regedit can never work, and the user gets no notification.

### Failure path that DOES work (keep it)

`PasteAsyncCore` (~1075-1115) already shows `NotificationService.ShowError("Auto-paste failed. Please paste manually (Ctrl+V).")` when `PasteWithMultipleMethodsAsync` returns false — the problem is only that it never returns false.

### Conventions

- Private fields `_camelCase`, sealed classes, all logging wrapped in `try { Logger.Log(...) } catch { }`.
- P/Invoke: `ClipboardService` declares its own (`GetAsyncKeyState`, `SendInput`, `SendMessage`, `GetClassName` are already present in the file); shared ones live in `NativeMethods.cs`.
- Tests: xUnit + Moq, see `TailSlap.Tests/TextTyperTests.cs` for the style (testable seams via `internal` members + `InternalsVisibleTo` already configured — verify with `grep -rn "InternalsVisibleTo" TailSlap/`).

## Commands you will need

| Purpose | Command | Expected on success |
|---------|---------|---------------------|
| Build | `dotnet build -c Release` | exit 0 |
| Focused tests | `dotnet test -c Release --filter "FullyQualifiedName~ClipboardService|FullyQualifiedName~NativeInputSimulator"` | all pass |
| Full suite | `dotnet test -c Release` | all pass |

## Scope

**In scope** (the only files you should modify):

- `TailSlap/ClipboardService.cs`
- `TailSlap/NativeInputSimulator.cs`
- `TailSlap/NativeMethods.cs` (add P/Invoke only)
- `TailSlap.Tests/ClipboardServiceTests.cs`
- `TailSlap.Tests/NativeInputSimulatorTests.cs` (create)
- `plans/README.md` (status row)

**Out of scope** (do NOT touch, even though they look related):

- `TailSlap/TextTyper.cs` and controller call sites — plan 014 handles consuming delivery results; changing them here creates merge conflicts.
- The clipboard **capture** side (`CaptureSelectionOrClipboardAsync`, UIA probe, `RunInSta`) — different pipeline.
- `IClipboardService` public interface shape — callers depend on it.
- Clipboard history exclusion formats — plan 022.

## Git workflow

- Branch: `advisor/013-paste-delivery-verification`
- Commit message style (from `git log`): imperative summary, e.g. `Fix: verify paste delivery and wait for modifier release`
- Do NOT push or open a PR unless the operator instructed it.

## Steps

### Step 1: Add a testable wait-for-physical-modifier-release helper

In `TailSlap/NativeInputSimulator.cs`, add:

```csharp
/// <summary>
/// Waits until no modifier key (Ctrl, Alt, Shift, Win) is physically held,
/// polling every pollMs, up to timeoutMs. Returns true if all released.
/// </summary>
public static bool WaitForModifierRelease(int timeoutMs = 1000, int pollMs = 15)
    => WaitForModifierRelease(timeoutMs, pollMs, IsKeyDown);

internal static bool WaitForModifierRelease(
    int timeoutMs, int pollMs, Func<ushort, bool> isKeyDown)
{
    ushort[] mods = { 0x11 /*CTRL*/, 0x12 /*ALT*/, 0x10 /*SHIFT*/, 0x5B /*LWIN*/, 0x5C /*RWIN*/ };
    var deadline = Environment.TickCount64 + timeoutMs;
    while (true)
    {
        bool anyDown = false;
        foreach (var m in mods)
        {
            if (isKeyDown(m)) { anyDown = true; break; }
        }
        if (!anyDown) return true;
        if (Environment.TickCount64 >= deadline) return false;
        Thread.Sleep(pollMs);
    }
}

private static bool IsKeyDown(ushort vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
```

Add the `GetAsyncKeyState` DllImport to `NativeInputSimulator` (copy the signature from `ClipboardService.cs` — `[DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);`, adjust parameter type to match your call). Add `using System.Threading;` if missing.

**Verify**: `dotnet build -c Release` → exit 0.

### Step 2: Add elevation detection to NativeMethods + a decision helper

In `TailSlap/NativeMethods.cs` add:

```csharp
[DllImport("kernel32.dll", SetLastError = true)]
internal static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

[DllImport("advapi32.dll", SetLastError = true)]
internal static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

[DllImport("advapi32.dll", SetLastError = true)]
internal static extern bool GetTokenInformation(
    IntPtr TokenHandle, int TokenInformationClass,
    out int TokenInformation, int TokenInformationLength, out int ReturnLength);

[DllImport("kernel32.dll")]
internal static extern bool CloseHandle(IntPtr hObject);

internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
internal const uint TOKEN_QUERY = 0x0008;
internal const int TokenElevation = 20; // TOKEN_INFORMATION_CLASS.TokenElevation
```

In `ClipboardService.cs`, add a private helper:

```csharp
/// <summary>
/// True when the foreground window's process is elevated and this process is not,
/// meaning UIPI will silently discard our synthetic input.
/// </summary>
private static bool IsTargetElevatedAboveSelf(IntPtr foregroundWindow)
```

Implementation: get the target PID via `NativeMethods.GetWindowThreadProcessId`, open it with `PROCESS_QUERY_LIMITED_INFORMATION`, read `TokenElevation` for both the target process token and the current process token (`System.Diagnostics.Process.GetCurrentProcess()` handle or `OpenProcess` on own PID), return `targetElevated && !selfElevated`. On ANY failure (OpenProcess denied, etc.) return `false` — never block pasting because the check failed; wrap everything in try/catch and close handles in `finally`. Note: `OpenProcess` on an elevated process from a non-elevated one typically succeeds with `PROCESS_QUERY_LIMITED_INFORMATION`; if it fails with access denied, that itself is a strong elevation signal — treat OpenProcess failure with Win32 error 5 (ERROR_ACCESS_DENIED) as `targetElevated = true`.

**Verify**: `dotnet build -c Release` → exit 0.

### Step 3: Gate the paste chain on the two detectable failure classes

In `PasteAsyncCore` (ClipboardService.cs ~1075), after the existing `LogPasteDiagnostic("PasteAsync")` and foreground-window read, insert BEFORE the `Task.Delay(250)`:

1. **Elevation gate**: if `foregroundWindow != IntPtr.Zero && IsTargetElevatedAboveSelf(foregroundWindow)` → log `"PasteAsync: target window is elevated, synthetic input would be discarded"`, call `NotificationService.ShowError("Cannot paste into an elevated (admin) window. Text is on your clipboard — press Ctrl+V.")`, and `return false;` (do NOT fall through to the generic "Auto-paste failed" notification — return before it and make sure only one notification shows; restructure so the generic notification stays only on the method-chain-exhausted path).
2. **Modifier wait**: call `NativeInputSimulator.WaitForModifierRelease(timeoutMs: 1000)`. If it returns false, log `"PasteAsync: modifiers still held after 1000ms, proceeding with synthetic release"` and continue (NormalizeInputState still runs per-method as today). Do not fail the paste on timeout.

Keep the existing `Task.Delay(250)` after these gates.

**Verify**: `dotnet build -c Release` → exit 0. Manual smoke (optional, if you can run the app): transcribe into an elevated PowerShell — a balloon must appear instead of silence.

### Step 4: Verify WM_PASTE effect and check read-only style

In `TryPasteWindowMessageAsync` (~1180):

1. Before sending WM_PASTE, check the target for `ES_READONLY`: add `[DllImport("user32.dll")] static extern long GetWindowLongPtrW(IntPtr hWnd, int nIndex);` (or reuse if present; use `GetWindowLong` on x86 — this repo is win-x64 only so `GetWindowLongPtrW` is fine), `GWL_STYLE = -16`, `ES_READONLY = 0x0800`. If the style has `ES_READONLY`, log and `return false` (fall through to keyboard methods).
2. After `SendMessage(targetWindow, WM_PASTE, ...)` and the 75ms delay, verify the paste had an effect: send `WM_GETTEXTLENGTH (0x000E)` via `SendMessage` before and after the paste. Return `true` only if the length increased (`after > before`). If the length is unchanged, log `"TryPasteWindowMessageAsync: WM_PASTE had no effect (len unchanged)"` and return `false`.

Note: `WM_GETTEXTLENGTH` on `Edit`/`RichEdit`/`Scintilla` classes (the only classes `SupportsWindowMessagePaste` admits) is safe and cheap. If the pasted text could replace a selection of exactly equal length the check false-negatives — acceptable: the next method (Ctrl+V) pastes the same clipboard content over the same selection, which is idempotent for a replace-selection paste.

**Verify**: `dotnet build -c Release` → exit 0.

### Step 5: Demote keystroke methods from "always true" to "true unless known-failed"

For `TryPasteCtrlVAsync`, `TryPasteShiftInsertAsync`, `TryPasteSendInputAsync`: full verification of a keystroke paste into an arbitrary app is not possible — do NOT attempt to read foreground text generally. Instead:

1. After sending the keystroke and the 75ms delay, if the resolved focus target (reuse `ResolveFocusHwnd` + `SupportsWindowMessagePaste`) is an edit-class window, apply the same `WM_GETTEXTLENGTH` before/after check as Step 4 and return its result. For non-edit-class targets, keep returning `true` (trust, now that Steps 3's gates removed the known silent-failure classes).
2. In `PasteWithMultipleMethodsAsync`, when a method returns false after actually sending input (i.e., a verified no-effect on an edit control), log which method failed verification before trying the next — the existing loop structure already does this via `Logger.Log($"Attempting paste with {method}")`.

Factor the before/after length probe into one private helper, e.g. `private static bool VerifyEditPasteEffect(IntPtr targetWindow, Func<Task> sendPaste)` or a simpler imperative shape — your choice, but the length-read logic must exist exactly once.

**Verify**: `dotnet build -c Release` → exit 0; `dotnet test -c Release` → all existing tests still pass.

### Step 6: Unit tests

Create `TailSlap.Tests/NativeInputSimulatorTests.cs`:

- `WaitForModifierRelease_AllUp_ReturnsImmediately` — `isKeyDown` always false → returns true fast (assert elapsed < 200ms).
- `WaitForModifierRelease_HeldThenReleased_ReturnsTrue` — `isKeyDown` returns true for the first N calls then false → returns true.
- `WaitForModifierRelease_HeldForever_TimesOutFalse` — `isKeyDown` always true, `timeoutMs: 100` → returns false, elapsed ≥ 100ms.

In `TailSlap.Tests/ClipboardServiceTests.cs`, add tests for any pure/injectable logic you created (e.g., if you made the elevation decision or verification decision testable via an `internal` seam, cover: access-denied ⇒ treated as elevated; check-failure ⇒ returns false/never blocks). Do not write tests that require a real clipboard or real foreground window.

**Verify**: `dotnet test -c Release --filter "FullyQualifiedName~NativeInputSimulator|FullyQualifiedName~ClipboardService"` → all pass, including ≥3 new tests.

## Test plan

- New: `NativeInputSimulatorTests` (3 cases above) — model structure after `TailSlap.Tests/TextTyperTests.cs` (plain xUnit `[Fact]`s, no UI).
- New: decision-logic tests in `ClipboardServiceTests.cs` for elevation/verification seams.
- Regression: full suite must stay green — `dotnet test -c Release`.
- Manual smoke checklist (record results in the PR/commit message): paste into Notepad (edit-class, verifiable), into VS Code (non-edit-class, trust path), into an elevated terminal (must balloon, not silently fail), and trigger a transcription while still holding Ctrl+Alt through the stop (must wait, then paste correctly).

## Done criteria

- [ ] `dotnet build -c Release` exits 0
- [ ] `dotnet test -c Release` exits 0; new NativeInputSimulator + ClipboardService tests exist and pass
- [ ] `TryPasteCtrlVAsync` no longer contains an unconditional `return true` directly after `SendKeys.SendWait("^v")` for edit-class targets (grep: the length-verification helper is referenced from all three keystroke methods)
- [ ] `PasteAsyncCore` contains the elevation gate and the `WaitForModifierRelease` call
- [ ] No files outside the in-scope list are modified (`git status`)
- [ ] `plans/README.md` status row for 013 updated

## STOP conditions

- The paste-chain code at ~1129-1275 doesn't match the excerpts (drift).
- `InternalsVisibleTo` is not configured for the test project and adding internal seams therefore fails to compile in tests — STOP and report (don't switch to reflection).
- The `WM_GETTEXTLENGTH` verification makes an existing integration behavior fail twice after a fix attempt (e.g., RichEdit reports lengths inconsistently) — STOP and report which control class misbehaves rather than loosening the check globally.
- You find you need to change `IClipboardService` or `TextTyper` — that's plan 014's territory; STOP and report the coupling.

## Maintenance notes

- Plan 014 builds on this: once paste returns honest results, controllers must consume them. Land 013 first.
- Reviewers: scrutinize the elevation check's failure handling (must never block pasting when the check itself errors) and confirm only ONE notification fires per failed paste (not the elevation balloon AND the generic one).
- Deferred: restoring the user's prior clipboard content after paste, and a UIAccess manifest to paste into elevated windows — both are product decisions, out of scope.
