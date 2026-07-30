# Plan 015: Close the ForceStop gaps — latch standard hotkeys and make stop transitions atomic

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md` — unless a reviewer dispatched you and told you they
> maintain the index.
>
> **Drift check (run first)**:
> `git diff --stat f3016ac..HEAD -- TailSlap/KeyboardHook.cs TailSlap/TypelessController.cs TailSlap.Tests/KeyboardHookTests.cs TailSlap.Tests/TypelessControllerTests.cs`
> If any in-scope file changed since this plan was written, compare the
> "Current state" excerpts against the live code before proceeding; on a
> mismatch, treat it as a STOP condition.

## Status

- **Priority**: P1
- **Effort**: S–M
- **Risk**: LOW
- **Depends on**: none
- **Category**: bug
- **Planned at**: commit `f3016ac`, 2026-07-30

## Why this matters

The push-to-talk safety net (`ForceStop` after 60s max recording) was recently hardened, but two gaps remain. (1) The `_forceStopped` latch is only checked for modifier-only hotkeys; for a hotkey with a primary key (Key != 0), the next OS auto-repeat KEYDOWN after ForceStop instantly restarts recording — an infinite record/stop cycle while the key is held, defeating the safety net. (2) `ForceStop` runs on a `System.Threading.Timer` thread-pool thread while the hook callback runs on the UI thread; both mutate `_isRecordingActive`/`_primaryKeyHeld` unsynchronized and both can invoke `OnKeyUp`. At the 60s boundary this can fire `TypelessController.HandleKeyUpAsync` twice concurrently — and its state guard is check-then-act (the `Recording → Processing` transition happens long after the guard), so both invocations proceed: the same WAV is transcribed twice, typed twice, and saved to history twice.

## Current state

### `TailSlap/KeyboardHook.cs` (576 lines)

Fields (~lines 67-77): plain (non-volatile) `bool _isRecordingActive`, `bool _primaryKeyHeld`, `bool _rightAltHeld`, `bool _forceStopped`, `System.Threading.Timer? _maxDurationTimer`. No lock object exists.

`ForceStop()` (~210-226), called from the timer callback (`StartMaxDurationTimer`, ~537-556) on a thread-pool thread:

```csharp
public void ForceStop()
{
    if (!_isRecordingActive)
        return;
    // ... log ...
    _isRecordingActive = false;
    _primaryKeyHeld = false;
    _forceStopped = true;
    StopMaxDurationTimer();
    OnKeyUp?.Invoke();
}
```

`ProcessKeyDown` (standard-hotkey path, ~250-283) — NO `_forceStopped` check:

```csharp
// Check if this key matches our configured hotkey
if (!MatchesConfig(currentModifiers, vk))
    return;

// Auto-repeat suppression: ignore repeated key-down while key is held
if (_primaryKeyHeld)
    return;

_primaryKeyHeld = true;
_isRecordingActive = true;
...
OnKeyDown?.Invoke();
```

Contrast `ProcessModifierOnlyKeyDown` (~290-330), which HAS the guard:

```csharp
// Prevent re-trigger after ForceStop until all required modifiers are released
if (_forceStopped)
    return;
```

`ProcessKeyUp` (~338-368) — returns early before any latch clearing:

```csharp
if (vk != _config.Key)
    return;
if (!_primaryKeyHeld)      // after ForceStop this is false → early return,
    return;                //  _forceStopped is never cleared on this path
```

`_forceStopped` is currently cleared only in `ProcessModifierChange` (~382-394) when required modifiers are released — which never happens for a standard hotkey whose modifiers were already released, or clears too early while the primary key is still held.

### `TailSlap/TypelessController.cs` (611 lines)

`private enum ControllerState` (line 31) — values Idle / Recording / Processing; `_state` guarded by `_stateLock`. `HandleKeyUpAsync` (~245-300):

```csharp
public async Task HandleKeyUpAsync()
{
    lock (_stateLock)
    {
        if (_state != ControllerState.Recording)
            return;
    }                                  // <-- guard released; state still Recording

    _recordingCts?.Cancel();
    if (_recordingTask != null) { try { await _recordingTask...; } catch { } }

    var stats = _recordingStats;
    if (stats == null || stats.DurationMs < 500) { ...; ReturnToIdle(); return; }

    lock (_stateLock)
    {
        _state = ControllerState.Processing;   // line ~297 — far too late
    }
    OnProcessingStarted?.Invoke();
    ...
}
```

Two concurrent calls both pass the guard because the state only leaves `Recording` at line ~297.

### Wiring — `TailSlap/MainForm.cs` (~182-189)

```csharp
_keyboardHook.OnKeyDown += () => SafeFireAndForget(_typelessController.HandleKeyDownAsync());
_keyboardHook.OnKeyUp += () => SafeFireAndForget(_typelessController.HandleKeyUpAsync());
```

### Test conventions

`TailSlap.Tests/KeyboardHookTests.cs` (40 tests) drives the internal methods `ProcessKeyDown` / `ProcessKeyUp` / `ProcessModifierChange` / `ForceStop` directly (they are `internal`, reachable via `InternalsVisibleTo`) and asserts `OnKeyDown`/`OnKeyUp` event counts. `TypelessControllerTests.cs` (36 tests) mocks collaborators via factory interfaces. Match those styles.

## Commands you will need

| Purpose | Command | Expected on success |
|---------|---------|---------------------|
| Build | `dotnet build -c Release` | exit 0 |
| Focused tests | `dotnet test -c Release --filter "FullyQualifiedName~KeyboardHook|FullyQualifiedName~TypelessController"` | all pass |
| Full suite | `dotnet test -c Release` | all pass |

## Scope

**In scope**:

- `TailSlap/KeyboardHook.cs`
- `TailSlap/TypelessController.cs`
- `TailSlap.Tests/KeyboardHookTests.cs`
- `TailSlap.Tests/TypelessControllerTests.cs`
- `plans/README.md` (status row)

**Out of scope**:

- `MainForm.cs` wiring — unchanged.
- `TranscriptionController` / `RealtimeTranscriptionController` — different trigger mechanisms (RegisterHotKey, not the hook).
- Moving config I/O off the hook thread — plan 018.

## Git workflow

- Branch: `advisor/015-forcestop-latch-and-race`
- Commit message example: `Fix: latch ForceStop for standard hotkeys and serialize hook stop transitions`
- Do NOT push or open a PR unless the operator instructed it.

## Steps

### Step 1: Regression test — standard hotkey re-triggers after ForceStop (should fail before fix)

In `KeyboardHookTests.cs`, add (using the file's existing construction helpers for a hotkey with a primary key, e.g. Ctrl+Win+T style config):

```csharp
[Fact]
public void ProcessKeyDown_AfterForceStop_DoesNotRetriggerWhileKeyHeld()
{
    // configure standard hotkey (Key != 0), simulate key-down, then ForceStop,
    // then simulate the auto-repeat key-down with identical modifiers+vk
    // Assert: OnKeyDown fired exactly once.
}

[Fact]
public void ProcessKeyUp_AfterForceStop_ClearsLatch_AllowingNextPress()
{
    // key-down → ForceStop → key-up (primary key) → key-down again
    // Assert: second key-down fires OnKeyDown (total 2).
}
```

**Verify**: `dotnet test -c Release --filter FullyQualifiedName~ProcessKeyDown_AfterForceStop` → the first test FAILS (this proves the bug), the second may fail too.

### Step 2: Add the latch to the standard-hotkey path and clear it on primary key-up

In `ProcessKeyDown` (standard path), after the `MatchesConfig` check and BEFORE the `_primaryKeyHeld` check, add:

```csharp
// Prevent re-trigger after ForceStop until the primary key is released
if (_forceStopped)
    return;
```

In `ProcessKeyUp`, clear the latch when the configured primary key goes up — BEFORE the `!_primaryKeyHeld` early return:

```csharp
if (vk != _config.Key)
    return;

// Physical release of the primary key always clears the ForceStop latch
if (_forceStopped)
    _forceStopped = false;

if (!_primaryKeyHeld)
    return;
```

**Verify**: both Step 1 tests now PASS; `dotnet test -c Release --filter FullyQualifiedName~KeyboardHook` → all pass (existing modifier-only latch tests unaffected).

### Step 3: Serialize KeyboardHook state mutations

Add `private readonly object _syncLock = new();` to `KeyboardHook`. Wrap the read-modify-write sections of `ForceStop`, `ProcessKeyDown`, `ProcessModifierOnlyKeyDown`, `ProcessKeyUp`, and `ProcessModifierChange` in `lock (_syncLock)`, with the pattern: decide-and-mutate inside the lock, set a local `bool fireKeyUp/fireKeyDown`, invoke `OnKeyUp?.Invoke()` / `OnKeyDown?.Invoke()` AFTER releasing the lock (never invoke events under the lock — handlers schedule async work and must not risk lock-ordering issues).

Example shape for `ForceStop`:

```csharp
public void ForceStop()
{
    bool fire = false;
    lock (_syncLock)
    {
        if (_isRecordingActive)
        {
            _isRecordingActive = false;
            _primaryKeyHeld = false;
            _forceStopped = true;
            fire = true;
        }
    }
    if (!fire)
        return;
    try { Logger.Log("KeyboardHook force stop triggered (max duration or external)"); } catch { }
    StopMaxDurationTimer();
    OnKeyUp?.Invoke();
}
```

Apply the same decide-inside/fire-outside shape to `ProcessKeyUp` and `ProcessModifierChange` (their `OnKeyUp` firing is now mutually exclusive with `ForceStop`'s: whichever takes the lock first flips `_isRecordingActive`/`_primaryKeyHeld`, the other sees the changed state and does not fire).

**Verify**: `dotnet build -c Release` → exit 0; `dotnet test -c Release --filter FullyQualifiedName~KeyboardHook` → all 40+ pass.

### Step 4: Make TypelessController's Recording→stop transition atomic

In `HandleKeyUpAsync`, transition out of `Recording` INSIDE the initial guard lock so a second concurrent call cannot pass:

```csharp
lock (_stateLock)
{
    if (_state != ControllerState.Recording)
        return;
    _state = ControllerState.Processing;   // claim the stop atomically
}
```

Then delete the now-redundant later block (`lock (_stateLock) { _state = ControllerState.Processing; }` at ~295-298) — keep the `OnProcessingStarted?.Invoke()` and log exactly where they are. The short-recording path (`stats == null || DurationMs < 500`) already calls `ReturnToIdle()`, which resets `_state = Idle` — that still works because state is `Processing` at that point, not `Recording`.

Behavioral note: `IsProcessing` now becomes true during the drain/await window (previously still `IsRecording`). Check `TypelessControllerTests.cs` for assertions on `IsRecording`/`IsProcessing` mid-stop and update them to the new (correct) semantics. Also `HandleKeyDownAsync`'s rejected-while-Processing branch (~183-199) will now show "Transcription in progress. Please wait." for a key-down during drain — that is acceptable and correct.

**Verify**: `dotnet test -c Release --filter FullyQualifiedName~TypelessController` → all pass after updating any transition-timing assertions.

### Step 5: Concurrency regression test for double key-up

In `TypelessControllerTests.cs`, add a test that fires `HandleKeyUpAsync()` twice concurrently (`Task.WhenAll(controller.HandleKeyUpAsync(), controller.HandleKeyUpAsync())`) after a started recording, with the mocked transcriber counting invocations. Assert the transcriber ran exactly once and `OnCompleted` fired exactly once. Use `TaskCompletionSource` in the mock to control timing (the file already uses `Task.Delay(Timeout.Infinite, ct)` blockers — follow that pattern, but prefer TCS signaling over real delays).

**Verify**: `dotnet test -c Release` → full suite passes, including the new test.

## Test plan

- `ProcessKeyDown_AfterForceStop_DoesNotRetriggerWhileKeyHeld` (new, KeyboardHookTests) — the safety-net bypass.
- `ProcessKeyUp_AfterForceStop_ClearsLatch_AllowingNextPress` (new) — latch lifecycle.
- Double-key-up concurrency test (new, TypelessControllerTests) — single transcription guaranteed.
- All 40 existing KeyboardHook + 36 TypelessController tests stay green (some transition-timing assertions may need updating per Step 4 — update only assertions about WHEN Processing begins, nothing else).

## Done criteria

- [ ] `dotnet build -c Release` exits 0
- [ ] `dotnet test -c Release` exits 0, including 3 new tests
- [ ] `ProcessKeyDown` (standard path) contains the `_forceStopped` guard
- [ ] `ForceStop`/`ProcessKeyUp`/`ProcessModifierChange` mutate state under `_syncLock` and fire events outside it
- [ ] `HandleKeyUpAsync` leaves `Recording` state inside its first lock
- [ ] No files outside the in-scope list are modified (`git status`)
- [ ] `plans/README.md` status row for 015 updated

## STOP conditions

- The excerpted code doesn't match (drift — e.g., someone already added a lock).
- Adding the lock deadlocks a test (would indicate an event handler synchronously re-enters the hook — report which path).
- More than ~5 existing tests need assertion changes for Step 4 — that suggests the timing semantics matter more than assessed; STOP and report the failing list.

## Maintenance notes

- Reviewers: verify no `OnKeyUp?.Invoke()` remains inside a `lock` block, and that `StopMaxDurationTimer` is not called under the lock (Timer.Dispose can block).
- If a future change adds a `Stopping` state to `ControllerState`, the Step 4 claim-transition is the place to use it.
- The `_rightAltHeld` field is written by the hook thread only — left unsynchronized deliberately; do not "fix" it.
