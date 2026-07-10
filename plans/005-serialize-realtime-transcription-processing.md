# Plan 005: Serialize realtime transcription processing and protect queue maps

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md` — unless a reviewer dispatched you and told you they
> maintain the index.
>
> **Drift check (run first)**:
> `git diff --stat 6d0b6ca..HEAD -- TailSlap/RealtimeTranscriptionController.cs TailSlap.Tests/RealtimeTranscriptionControllerTests.cs`
> If any in-scope file changed since this plan was written, compare the
> "Current state" excerpts against the live code before proceeding; on a
> mismatch, treat it as a STOP condition.

## Status

- **Priority**: P1
- **Effort**: M
- **Risk**: MED
- **Depends on**: plan 001 recommended (CI must run tests)
- **Category**: bug
- **Planned at**: commit `6d0b6ca`, 2026-07-09

## Why this matters

Realtime streaming mode injects text into the foreground app as interim and final transcripts arrive. Ordered updates are enqueued, then **fire-and-forget** `ProcessTranscriptionAsync` without awaiting. Finals are marked **completed and removed from the pending map before processing finishes**, so a later item can start typing while the previous final is still applying keystrokes. Pending maps are also mutated on the receive path **without** the same lock that `CleanupAsync` uses when clearing them — stop/cleanup can race with event handlers. Result: garbled/duplicated on-screen text, or collection corruption during stop.

`ProcessTranscriptionAsync` already serializes its body with `SemaphoreSlim _transcriptionLock`, but **queue bookkeeping is outside that lock** and completion is premature. Fix ordering + locking; keep typing behavior otherwise identical.

## Current state

### File: `TailSlap/RealtimeTranscriptionController.cs`

Key fields (~17–51):

- `_transcriptionLock` — `SemaphoreSlim(1,1)` around typing state in `ProcessTranscriptionAsync`
- `_pendingOrderedRealtimeUpdates`, `_orderedRealtimeSequences`, `_completedOrderedRealtimeItems`, `_nextOrderedRealtimeSequence`
- `_allowRealtimeTextUpdates` volatile flag
- `_lastTypedLength`, `_realtimeTranscriptionText`, `_typedText`

**Fire-and-forget + premature completion** (~429–469):

```csharp
private void TryProcessQueuedOrderedRealtimeUpdates()
{
    var textProcessingToken = _textProcessingCts?.Token ?? CancellationToken.None;

    while (true)
    {
        // ... select next ready PendingOrderedRealtimeUpdate into `next` ...

        if (next == null)
        {
            return;
        }

        _ = ProcessTranscriptionAsync(
            next.Update.Text,
            next.Update.IsFinal,
            next.Update.ItemId,
            textProcessingToken
        );

        if (!next.Update.IsFinal)
        {
            return;
        }

        _completedOrderedRealtimeItems.Add(next.Update.ItemId!);
        _pendingOrderedRealtimeUpdates.Remove(next.Update.ItemId!);
        _orderedRealtimeSequences.Remove(next.Update.ItemId!);
    }
}
```

**Maps mutated without queue lock** (~414–426) in the ordered-update handler path:

```csharp
_orderedRealtimeSequences[update.ItemId] = sequence;
_pendingOrderedRealtimeUpdates[update.ItemId] = new PendingOrderedRealtimeUpdate { ... };
TryProcessQueuedOrderedRealtimeUpdates();
```

**Cleanup clears maps under `_transcriptionLock` only** (~923–945), while handlers may still run until unsubscribe (~957–962) **after** the clear.

**Legacy path** also fire-and-forgets (~492):

```csharp
_ = ProcessTranscriptionAsync(text, isFinal, null, textProcessingToken);
```

**Typing mutation under lock** (~591–747): `ProcessTranscriptionAsync` waits on `_transcriptionLock`, updates `_lastTypedLength` / pastes text, releases in `finally`.

### Tests: `TailSlap.Tests/RealtimeTranscriptionControllerTests.cs`

- Construction, null args, disabled transcriber early return.
- Uses Moq for config, clipboard, factories.
- Limited coverage of ordered interim/final sequencing — expect to **extend** this file with reflection or `internal` test hooks if needed.

**Conventions**: sealed controller; `ConfigureAwait(false)` not always used on UI-bound paths — match surrounding async style in this file; log via `Logger.Log` in try/catch; do not change public `IRealtimeTranscriptionController` surface unless required.

## Commands you will need

| Purpose | Command | Expected on success |
|---------|---------|---------------------|
| Build | `dotnet build -c Release` | exit 0 |
| Controller tests | `dotnet test -c Release --filter FullyQualifiedName~RealtimeTranscriptionController` | all pass |
| Full suite | `dotnet test -c Release` | exit 0 |

## Scope

**In scope**:

- `TailSlap/RealtimeTranscriptionController.cs`
- `TailSlap.Tests/RealtimeTranscriptionControllerTests.cs`
- Optionally `InternalsVisibleTo` in `TailSlap.csproj` **only if** already used or you add a minimal `internal` test seam; prefer reflection like other tests if that is the local pattern
- `plans/README.md` status

**Out of scope**:

- `AudioRecorder` Dispose/StopAsync race (separate finding)
- `OpenAIRealtimeTranscriber` / `RealtimeTranscriber` wire protocol
- `TextTyper` baseline (plan 004)
- Refactoring dual realtime providers
- Changing VAD / silence thresholds

## Git workflow

- Branch: `advisor/005-serialize-realtime-transcription`
- Commit message example: `Serialize realtime transcript queue processing`
- Do NOT push/PR unless asked.

## Steps

### Step 1: Define the target concurrency model (implement exactly this)

**Goals**:

1. At most one `ProcessTranscriptionAsync` **logical apply** runs at a time (already true via semaphore) **and** ordered-queue **completion** only happens **after** that apply finishes for finals.
2. All reads/writes of `_pendingOrderedRealtimeUpdates`, `_orderedRealtimeSequences`, `_completedOrderedRealtimeItems`, and `_nextOrderedRealtimeSequence` happen under a **single dedicated lock object** (e.g. `readonly object _orderedRealtimeLock = new();`) **or** under `_transcriptionLock` consistently — pick one scheme and use it everywhere including `CleanupAsync`.
3. No fire-and-forget that allows a second final’s apply to start before the first final’s apply completed **because** the queue marked the first complete early.
4. Legacy path (`ProcessLegacyTranscriptionEvent`) must also avoid concurrent applies that reorder interims; serializing through the same single-consumer mechanism is required.

**Recommended design (preferred — implement this unless STOP)**:

Introduce a single-consumer loop:

- Field: `Task? _processingLoopTask` + `Channel<WorkItem>` **or** simpler: `SemaphoreSlim` gate + `async Task PumpOrderedQueueAsync()` started once when streaming starts and stopped on cleanup.
- Simpler approach that minimizes new infrastructure:

**Simpler approach (acceptable)**:

1. Change `TryProcessQueuedOrderedRealtimeUpdates` to be `async Task` and **await** `ProcessTranscriptionAsync` for each dequeued item.
2. Ensure only one pump runs: use `int _queuePumpRunning` with `Interlocked.CompareExchange` — if pump already running, return; else loop until queue has no runnable item.
3. For **interim** updates: while pumping, after awaiting process for an interim, **re-read** the latest pending text for that `itemId` (coalesce) so rapid interims collapse to latest, then continue or exit.
4. For **final**: await `ProcessTranscriptionAsync`, **then** under the ordered lock mark completed and remove pending, then continue the while-loop for the next ready item.
5. Call site from event handler: `_ = PumpOrderedQueueAsync();` is OK **only if** the pump is single-flight and awaits internals (event thread must not block on UI forever — fire-and-forget the **pump**, not each process with premature completion).
6. Legacy events: enqueue as work items into the same pump (e.g. null `itemId` path) or `await` via the same single-flight pump method.

**Coalescing interims (required for correctness under load)**:

When multiple interims for the same `itemId` arrive, pending map already keeps latest (`_pendingOrderedRealtimeUpdates[itemId] = ...`). Pump must:

- Take snapshot of next work under lock
- For interim, remove or leave pending until final — current code `return`s after starting interim process without removing pending; after fix, either leave latest interim in map until final, or remove after successful apply of that snapshot. Prefer: **after awaiting interim apply, do not add to `_completedOrderedRealtimeItems`**; leave/update pending until final.

### Step 2: Lock all ordered-map mutations

Under the chosen lock:

- Insert/update pending in handle path
- `CanProcessOrderedRealtimeUpdate` reads
- Completion set add/remove
- Sequence dictionary updates
- `CleanupAsync` clears

**Cleanup order** (important):

1. Set `_allowRealtimeTextUpdates = false` (already done)
2. Cancel `_textProcessingCts`
3. Unsubscribe `OnTranscription` **before** or **while holding** lock prior to clear if possible — today unsubscribe is **after** clear; move unsubscribe of transcription handler **before** clearing maps when `transcriber` is still non-null, **or** ignore events when `!_allowRealtimeTextUpdates` at the very top of the handler (verify this early-return exists; if not, add it).

Check handler entry for `_allowRealtimeTextUpdates` — if missing, add:

```csharp
if (!_allowRealtimeTextUpdates)
    return;
```

at the start of `HandleRealtimeTranscriptionEvent`.

**Verify by inspection**: no path mutates the three collections without the lock.

### Step 3: Fix premature final completion

Must **not** execute:

```csharp
_completedOrderedRealtimeItems.Add(...);
_pendingOrderedRealtimeUpdates.Remove(...);
```

until **after** `await ProcessTranscriptionAsync(...)` returns for that final.

**Verify**: code review of pump loop order: await process → then mark complete.

### Step 4: Tests

Extend `RealtimeTranscriptionControllerTests.cs`.

Minimum cases:

1. **Ordered finals process sequentially**  
   If hard to drive without full streaming session, use reflection to invoke private handlers / pump with faked updates and a slow mock clipboard:

   - Mock `IClipboardService.SetTextAndPasteAsync` with a `TaskCompletionSource` delay on first call.
   - Inject two final updates with dependency order (`PreviousItemId` chain) if the public API allows, or invoke private `HandleRealtimeTranscriptionEvent` / ordered helpers via reflection (see existing reflection usage in `TextTyperTests` / controller tests).

2. **Cleanup during pending updates does not throw**  
   Start pump / enqueue then call stop/cleanup path if accessible; assert no exception and state Idle.

If full integration is too heavy, add an `internal` test helper on the controller:

```csharp
internal Task TestHook_ProcessOrderedUpdateForTests(RealtimeTranscriptionUpdate update);
```

Only if reflection is brittle — keep the surface minimal and `#if` not required.

**Also run** existing controller tests unchanged in intent.

```powershell
dotnet test -c Release --filter FullyQualifiedName~RealtimeTranscriptionController
```

**Verify**: exit 0; at least one new test covering “final completion after process” or “no concurrent paste reordering” if mock timing allows.

### Step 5: Full suite

```powershell
dotnet test -c Release
```

**Verify**: exit 0.

## Test plan

| Case | Notes |
|------|-------|
| Existing construction / disabled / basic trigger tests | Still pass |
| New: sequential finals / coalesced interims | Prefer mock delay on paste |
| New: handler no-ops when updates disallowed | Easy unit test via flag + reflection |

Pattern: Moq + reflection like current `RealtimeTranscriptionControllerTests` / `TextTyperTests`.

## Done criteria

- [ ] Finals are marked completed only after `ProcessTranscriptionAsync` completes
- [ ] Ordered maps are never mutated without the shared lock; cleanup uses the same lock
- [ ] Event handler ignores updates when realtime text updates are disallowed (or unsubscribes before clear)
- [ ] Single-flight pump (no parallel premature queue completion)
- [ ] `dotnet test -c Release --filter FullyQualifiedName~RealtimeTranscriptionController` exits 0
- [ ] Full `dotnet test -c Release` exits 0
- [ ] No out-of-scope product files modified
- [ ] `plans/README.md` status for 005 set to `DONE`

## STOP conditions

- Fix appears to require rewriting `OpenAIRealtimeTranscriber` event shapes — out of scope; STOP.
- You cannot test without a live microphone/WebSocket — use mocks; if even mocks cannot reach private queue without unsafe changes, implement the production fix carefully and add the best test possible; if production behavior is unclear due to drift, STOP.
- Deadlock risk: never call `WaitAsync` on `_transcriptionLock` while holding the ordered lock and then try to take ordered lock inside `ProcessTranscriptionAsync` in opposite order — document lock order: **ordered lock → release → transcription lock** (do not nest), or take only one lock for both if you unify.
- Second verification failure after a reasonable fix attempt — STOP and report.

## Maintenance notes

- Reviewers: focus on lock ordering, pump single-flight, and that interim coalescing still updates on-screen text to the latest interim.
- Follow-ups deferred: AudioRecorder dispose race; TextTyper vs controller dual typing paths unification.
- Any new realtime event source must enqueue through the same pump, not call `ProcessTranscriptionAsync` directly with `_ =`.
