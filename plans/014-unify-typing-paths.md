# Plan 014: Surface delivery failures to the user and unify typing helpers across controllers

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md` — unless a reviewer dispatched you and told you they
> maintain the index.
>
> **Drift check (run first)**:
> `git diff --stat f3016ac..HEAD -- TailSlap/TextTyper.cs TailSlap/RealtimeTranscriptionController.cs TailSlap/TypelessController.cs TailSlap/TranscriptionController.cs TailSlap.Tests/TextTyperTests.cs TailSlap.Tests/RealtimeTranscriptionControllerTests.cs`
> If any in-scope file changed since this plan was written, compare the
> "Current state" excerpts against the live code before proceeding; on a
> mismatch, treat it as a STOP condition. Note: plan 013 intentionally
> changes `ClipboardService.cs`/`NativeInputSimulator.cs` — those diffs are
> expected and fine.

## Status

- **Priority**: P1
- **Effort**: S–M
- **Risk**: MED
- **Depends on**: plans/013-paste-delivery-verification.md
- **Category**: bug
- **Planned at**: commit `f3016ac`, 2026-07-30

## Why this matters

When `TextTyper.TypeAsync` detects that the foreground window changed, it parks the text on the clipboard and returns `DeliverySuccess = false, WindowChanged = true` — **silently**. Every caller discards the returned `TypeResult`, so the user sees nothing typed and no notification (they must guess the text is on the clipboard). Separately, `RealtimeTranscriptionController` types short chunks via raw Unicode `SendInput` without waiting for hotkey modifiers to be released, so the first realtime chunk can be eaten as accidental shortcuts while the user still holds Ctrl+Alt+Y. This plan closes both gaps and removes a duplicated P/Invoke.

## Current state

### Silent window-change branch — `TailSlap/TextTyper.cs` (~lines 96-135)

```csharp
if (windowChanged)
{
    return new TypeResult
    {
        WindowChanged = true,
        DeliverySuccess = false,
        Text = text,
        TextOnClipboard = await _clip.SetTextAsync(text).ConfigureAwait(false),
    };
}
```

No notification here. Contrast: the all-methods-failed branches inside `TypeAsync` DO call `NotificationService.ShowInfo("Text delivery failed. The text is on your clipboard — paste manually with Ctrl+V.")`.

### Callers discard the result

- `TailSlap/TypelessController.cs:421` — `await _typer.TypeAsync(fullText, ...)` (streamed SSE chunks; return value unused).
- `TailSlap/TypelessController.cs:~504` — enhanced final text via `TypeAsync`, result unused (`ApplyEnhancedTextAsync`, ~498-522).
- `TailSlap/TranscriptionController.cs:387` — streamed chunk `TypeAsync`, result unused.
- `TailSlap/TranscriptionController.cs:~470` — enhanced delta `TypeAsync`, result unused (`ApplyFinalTextAsync`, ~449-472).

(Verify exact line numbers with `grep -n "TypeAsync" TailSlap/TypelessController.cs TailSlap/TranscriptionController.cs` — they may have shifted a few lines.)

### Realtime short-chunk typing without modifier wait — `TailSlap/RealtimeTranscriptionController.cs` (~815-835 and ~898-906)

```csharp
if (newText.Length > 5)
{
    bool pasteSuccess = await _clip.SetTextAndPasteAsync(newText);
    if (!pasteSuccess)
    {
        TypeTextDirectly(newText);
    }
}
else
{
    TypeTextDirectly(newText);          // <-- raw SendInput, no modifier wait
}
```

```csharp
private static void TypeTextDirectly(string text)
{
    try { NativeInputSimulator.TypeTextDirectly(text); }
    catch (Exception ex) { Logger.Log($"TypeTextDirectly failed: {ex.Message}"); }
}
```

### Duplicated P/Invoke — `TailSlap/RealtimeTranscriptionController.cs` (~line 77) and `TailSlap/TextTyper.cs` (~line 25)

Both declare a private `[DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();` even though `NativeMethods.cs` already exposes `internal static extern IntPtr GetForegroundWindow()`.

### After plan 013 (prerequisite)

`NativeInputSimulator.WaitForModifierRelease(int timeoutMs = 1000, int pollMs = 15)` exists (public static, with an internal test overload taking a `Func<ushort,bool>` key-state probe).

### Conventions

- Logging always wrapped in `try { Logger.Log(...) } catch { }`.
- User-facing messages via `NotificationService` static methods.
- Tests: xUnit + Moq; `TailSlap.Tests/TextTyperTests.cs` uses a `TestableTextTyper` subclass overriding `SendBackspace`/`TypeTextDirectly`; `RealtimeTranscriptionControllerTests.cs` (22 tests) is the pattern for realtime controller tests.

## Commands you will need

| Purpose | Command | Expected on success |
|---------|---------|---------------------|
| Build | `dotnet build -c Release` | exit 0 |
| Focused tests | `dotnet test -c Release --filter "FullyQualifiedName~TextTyper|FullyQualifiedName~RealtimeTranscriptionController|FullyQualifiedName~TypelessController"` | all pass |
| Full suite | `dotnet test -c Release` | all pass |

## Scope

**In scope**:

- `TailSlap/TextTyper.cs`
- `TailSlap/RealtimeTranscriptionController.cs`
- `TailSlap/TypelessController.cs` (result consumption only)
- `TailSlap/TranscriptionController.cs` (result consumption only)
- `TailSlap.Tests/TextTyperTests.cs`
- `TailSlap.Tests/RealtimeTranscriptionControllerTests.cs`
- `plans/README.md` (status row)

**Out of scope**:

- `ClipboardService.cs` / `NativeInputSimulator.cs` internals — plan 013 owns those.
- Refactoring the realtime controller to inject a full `TextTyper` — its baseline model (`_lastTypedLength`/`_realtimeTranscriptionText`) is intentionally different; do not merge the state machines.
- History persistence / enhance logic in the controllers — plan 025.

## Git workflow

- Branch: `advisor/014-unify-typing-paths`
- Commit message example: `Fix: notify on window-change delivery failure and wait for modifiers before realtime typing`
- Do NOT push or open a PR unless the operator instructed it.

## Steps

### Step 1: Notify on the window-change branch in TextTyper

In `TypeAsync`'s `windowChanged` early-return branch, after the `SetTextAsync` call resolves, add (matching the existing failure-branch wording):

```csharp
NotificationService.ShowInfo(
    "Window changed before text could be typed. The text is on your clipboard — paste manually with Ctrl+V."
);
```

Only show it when `autoPaste` is true (when `autoPaste` is false, clipboard-only is the intended behavior — check the parameter before notifying).

**Verify**: `dotnet build -c Release` → exit 0; `dotnet test -c Release --filter FullyQualifiedName~TextTyper` → all pass (existing window-change tests may need the notification asserted or ignored; NotificationService is static — do NOT try to mock it, just keep assertions on `TypeResult`).

### Step 2: Consume TypeResult in the four controller call sites

At each call site listed in Current state, capture the result and log delivery failures so sessions are diagnosable:

```csharp
var typeResult = await _typer.TypeAsync(...).ConfigureAwait(false);
if (!typeResult.DeliverySuccess)
{
    try
    {
        Logger.Log(
            $"<ClassName>: delivery failed (windowChanged={typeResult.WindowChanged}, onClipboard={typeResult.TextOnClipboard})"
        );
    }
    catch { }
}
```

Do NOT add extra notifications in the controllers — `TextTyper` (Step 1 + existing branches) already notifies exactly once. For the streamed-chunk sites (TypelessController:421, TranscriptionController:387), a failed chunk should not abort the stream — log and continue (the baseline logic already retries undelivered text on the next chunk because the baseline only advances on success).

**Verify**: `dotnet build -c Release` → exit 0; `dotnet test -c Release --filter "FullyQualifiedName~TypelessController|FullyQualifiedName~TranscriptionController"` → all pass.

### Step 3: Wait for modifier release before realtime direct typing

In `RealtimeTranscriptionController`, change the private static `TypeTextDirectly` wrapper to wait for physical modifier release first:

```csharp
private static void TypeTextDirectly(string text)
{
    try
    {
        NativeInputSimulator.WaitForModifierRelease(timeoutMs: 500);
        NativeInputSimulator.TypeTextDirectly(text);
    }
    catch (Exception ex)
    {
        Logger.Log($"TypeTextDirectly failed: {ex.Message}");
    }
}
```

Use 500ms (not 1000ms) — realtime chunks arrive continuously and a long stall would back up the pump; after the first chunk the modifiers are already up so the wait is free.

Also apply the same wait inside `TextTyper.TypeTextDirectly`'s caller path: in `TextTyper.TypeAsync`, before the short-ASCII `TypeTextDirectly(newText)` call (the `else` branch of `useClipboard`), insert `NativeInputSimulator.WaitForModifierRelease(timeoutMs: 500);`. Do NOT add it inside `NativeInputSimulator.TypeTextDirectly` itself — that static is also used on paths that already waited (plan 013's paste chain), and double-waiting is wasteful.

**Verify**: `dotnet build -c Release` → exit 0.

### Step 4: Deduplicate GetForegroundWindow P/Invoke

- In `TailSlap/RealtimeTranscriptionController.cs`: delete the private `GetForegroundWindow` DllImport (~line 77) and replace all uses with `NativeMethods.GetForegroundWindow()`.
- In `TailSlap/TextTyper.cs`: delete the private DllImport (~line 25) and replace uses with `NativeMethods.GetForegroundWindow()`.

`NativeMethods` is `internal static` in the app assembly; both classes live in the same assembly, so this compiles directly.

**Verify**: `dotnet build -c Release` → exit 0; `grep -rn "DllImport" TailSlap/TextTyper.cs TailSlap/RealtimeTranscriptionController.cs` → no `GetForegroundWindow` declarations remain (RealtimeTranscriptionController may legitimately keep other DllImports only if they exist and are unrelated — check before deleting anything else).

### Step 5: Tests

- `TextTyperTests.cs`: add `TypeAsync_WindowChanged_TextPlacedOnClipboard` if not already covered — assert `WindowChanged == true`, `DeliverySuccess == false`, `TextOnClipboard == true` (mock `SetTextAsync` → true). Model after the existing window-change tests in the file.
- `RealtimeTranscriptionControllerTests.cs`: existing 22 tests must stay green — the `WaitForModifierRelease` call runs against the real keyboard state in tests; on a CI agent no keys are held so it returns immediately. If any test hangs, that is a STOP condition (see below).

**Verify**: `dotnet test -c Release` → all pass.

## Test plan

- New: `TypeAsync_WindowChanged_TextPlacedOnClipboard` (TextTyperTests) — window-change branch contract.
- Regression: full suite green; specifically the TypelessController streamed-chunk tests must still pass with the result-consumption logging added.
- Manual smoke: start realtime transcription (Ctrl+Alt+Y) and keep holding the hotkey for ~2s while speaking — the first chunk must appear as text, not vanish; alt-tab mid-typeless-session — a balloon must tell you the text is on the clipboard.

## Done criteria

- [ ] `dotnet build -c Release` exits 0
- [ ] `dotnet test -c Release` exits 0
- [ ] `grep -n "GetForegroundWindow" TailSlap/TextTyper.cs TailSlap/RealtimeTranscriptionController.cs` shows only `NativeMethods.GetForegroundWindow()` calls, no local DllImports
- [ ] All four controller `TypeAsync` call sites capture and log the `TypeResult`
- [ ] The windowChanged branch in `TextTyper.TypeAsync` notifies (when autoPaste is true)
- [ ] No files outside the in-scope list are modified (`git status`)
- [ ] `plans/README.md` status row for 014 updated

## STOP conditions

- Plan 013 has not landed (`NativeInputSimulator.WaitForModifierRelease` doesn't exist) — STOP; this plan depends on it.
- Any existing test hangs after adding `WaitForModifierRelease` calls (indicates a held-key state in the test environment or an interaction with `TestableTextTyper` overrides) — STOP and report which test.
- The controller call sites don't match the excerpts (drift from another plan landing first) — re-verify with grep; if the calls moved into a shared service (plan 025 landed early), STOP and report.

## Maintenance notes

- Plan 025 will extract shared persist/enhance/deliver logic from these controllers; the result-consumption logging added here should move into that shared sink when it lands.
- Reviewers: check that no path can show two notifications for one failed delivery (TextTyper notifies; controllers only log).
- Deferred: injecting TextTyper into RealtimeTranscriptionController to fully unify baselines — intentionally not done (different correction model, MED risk for little gain).
