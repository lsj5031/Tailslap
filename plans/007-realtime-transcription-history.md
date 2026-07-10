# Plan 007: Persist realtime sessions to encrypted transcription history

> **Executor instructions**: Follow step by step. Run every verification command.
> Touch only in-scope files. On STOP conditions, stop and report. Update
> `plans/README.md` status when done (unless reviewer maintains index).
>
> **Drift check (run first)**:
> `git diff --stat 6d0b6ca..HEAD -- TailSlap/RealtimeTranscriptionController.cs TailSlap/IRealtimeTranscriptionController.cs TailSlap/Program.cs TailSlap/HistoryService.cs TailSlap/TypelessController.cs TailSlap.Tests/RealtimeTranscriptionControllerTests.cs`
> On excerpt mismatch, re-read live code before coding.

## Status

- **Priority**: P1
- **Effort**: M
- **Risk**: LOW–MED
- **Depends on**: none (composes with 005 pump work already in tree)
- **Category**: direction
- **Planned at**: commit `6d0b6ca`, 2026-07-09

## Why this matters

Toggle and push-to-talk transcription save encrypted history; realtime streaming does not. Users lose the ability to re-copy or review long live-dictation sessions. `CleanupAsync` already materializes `finalTranscriptionText` / `finalTypedText` for a success toast — the natural place to persist once per session.

## Current state

- `IHistoryService.AppendTranscription(string text, int recordingDurationMs)` — existing API.
- `TypelessController.PersistHistoryEntries` — exemplar for append + optional refinement pair when enhanced.
- `RealtimeTranscriptionController` — **no** `IHistoryService` field; ctor takes config, clip, transcriber factory, audio factory only.
- `CleanupAsync` (~1047–1118) already computes:

```csharp
string finalTranscriptionText = _realtimeTranscriptionText;
string finalTypedText = _typedText;
// ... reset state ...
if (!string.IsNullOrEmpty(finalTranscriptionText) || !string.IsNullOrEmpty(finalTypedText))
{
    NotificationService.ShowSuccess("Real-time transcription complete.");
}
```

- `_streamingStartTime` exists for session timing (`NO_SPEECH_TIMEOUT` uses it).
- History cap: `HistoryService` `MaxEntries = 50`, trim every 10 appends.

**Convention**: inject `IHistoryService` via DI like `TypelessController`; log failures without crashing; never log full speech text (fingerprint/len only).

## Commands you will need

| Purpose | Command | Expected |
|---------|---------|----------|
| Build | `dotnet build -c Release` | exit 0 |
| Realtime tests | `dotnet test -c Release --filter FullyQualifiedName~RealtimeTranscriptionController` | pass |
| Full suite | `dotnet test -c Release` | pass |

## Scope

**In scope**:

- `TailSlap/RealtimeTranscriptionController.cs`
- `TailSlap/Program.cs` (DI registration if ctor gains `IHistoryService`)
- `TailSlap.Tests/RealtimeTranscriptionControllerTests.cs` (all construction sites)
- Optional one-line README note that realtime saves to transcription history
- `plans/README.md`

**Out of scope**:

- Auto-enhance on realtime stop (plan 010)
- History search/export UI (plan 008)
- Changing MaxEntries / encryption format
- Persisting every interim update (session rollup only)

## Git workflow

- Branch: `advisor/007-realtime-history`
- Commit example: `Save realtime sessions to transcription history`
- Do not push/PR unless asked.

## Product rules (do not improvise)

1. **Once per successful session** with non-empty text at cleanup — not per interim.
2. Text to store: prefer `finalTypedText + finalTranscriptionText` composition that matches what the user effectively produced:
   - If ordered items committed into `_typedText` and residual is in `_realtimeTranscriptionText`, concatenate the same way any existing “full session text” would read for the user.
   - Minimum acceptable: `string.IsNullOrEmpty(finalTypedText) ? finalTranscriptionText : finalTypedText + finalTranscriptionText` **or** mirror whatever the controller already treats as complete output in the success path — verify against how `_typedText` is updated on finals in `ProcessTranscriptionAsync`.
3. Duration: `(int)(DateTime.UtcNow - _streamingStartTime).TotalMilliseconds` if start was set; else `0`.
4. Empty/cancelled sessions with no text: **do not** append.
5. Failures to write history: log + continue (same as Typeless).

## Steps

### Step 1: Wire `IHistoryService` into the controller

- Add ctor parameter `IHistoryService history` (null-check).
- Update `Program.cs` DI (MainForm/`RealtimeTranscriptionController` registration — follow how Typeless gets history).
- Fix **all** test `new RealtimeTranscriptionController(...)` sites with `Mock<IHistoryService>()`.

**Verify**: `dotnet build -c Release` exit 0.

### Step 2: Persist on cleanup

In `CleanupAsync`, after computing final strings and **before or after** success toast (either is fine), if combined session text is non-empty:

```csharp
try
{
    var durationMs = _streamingStartTime == DateTime.MinValue
        ? 0
        : (int)Math.Max(0, (DateTime.UtcNow - _streamingStartTime).TotalMilliseconds);
    _history.AppendTranscription(sessionText, durationMs);
    Logger.Log($"RealtimeTranscriptionController: History saved, len={sessionText.Length}, duration={durationMs}ms");
}
catch (Exception ex)
{
    Logger.Log($"RealtimeTranscriptionController: History save failed: {ex.GetType().Name}");
}
```

Capture `_streamingStartTime` into a local **before** it is reset to `MinValue` later in cleanup (today reset is ~1110).

**Verify**: code review order — duration uses pre-reset start time.

### Step 3: Tests

Add tests modeled on existing controller tests (Moq + reflection if needed):

1. Cleanup/stop path with non-empty typed/residual text → `AppendTranscription` once with expected text/duration ≥ 0.
2. Cleanup with empty text → `AppendTranscription` never.
3. `AppendTranscription` throwing → cleanup still reaches Idle / does not throw.

**Verify**:

```powershell
dotnet test -c Release --filter FullyQualifiedName~RealtimeTranscriptionController
```

### Step 4: Full suite

```powershell
dotnet test -c Release
```

## Test plan

| Case | Expected |
|------|----------|
| Session with text | one `AppendTranscription` |
| Empty session | zero calls |
| History throws | cleanup completes |

Pattern: `TypelessControllerTests` history verifies; `RealtimeTranscriptionControllerTests` construction helpers.

## Done criteria

- [ ] Realtime controller depends on `IHistoryService`
- [ ] Non-empty session persists exactly once on cleanup
- [ ] Empty session does not persist
- [ ] History failures do not break cleanup
- [ ] All controller tests updated; full `dotnet test -c Release` passes
- [ ] No interim-per-event history spam
- [ ] `plans/README.md` status updated

## STOP conditions

- Unclear how to compose `finalTypedText` vs residual without double-counting — STOP and report both fields’ semantics from `ProcessTranscriptionAsync` rather than guessing wrong concatenation.
- DI registration path is non-obvious (multiple ctors) — STOP with findings.
- Requires HistoryService schema change — out of scope.

## Maintenance notes

- Reviewer: confirm MaxEntries trim still OK under heavy realtime use (one entry per session is fine).
- Plan 010 may also append refinement history when enhance changes text — keep that separate.
