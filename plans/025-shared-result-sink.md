# Plan 025: Extract shared transcription enhancement, delivery, and history persistence

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving on. If a
> STOP condition occurs, report it instead of improvising. When done, update
> this plan's status in `plans/README.md`, unless a reviewer owns the index.
>
> **Drift check (run first)**:
> `git diff --stat f3016ac..HEAD -- TailSlap/TypelessController.cs TailSlap/TranscriptionController.cs TailSlap/RealtimeTranscriptionController.cs TailSlap/TranscriptionAutoEnhancer.cs TailSlap/Program.cs TailSlap.Tests`
> Compare any changed in-scope region with the excerpts below. STOP if the
> three controllers no longer have the described result-processing behavior.

## Status

- **Priority**: P2
- **Effort**: M
- **Risk**: MED (all three transcription modes are touched)
- **Depends on**: plan 020 (characterization tests must land first); coordinate with plan 014, which changes delivery-result handling
- **Category**: architecture
- **Planned at**: commit `f3016ac`, 2026-07-30

## Why this matters

Toggle, typeless, and realtime transcription separately implement the same
post-transcription concerns: optional LLM enhancement, raw transcription
history, refinement-pair history, and final delivery. Their copies have already
drifted. Typeless swallows delivery exceptions and wraps logger calls,
toggle lets delivery exceptions escape and logs raw exception messages, while
realtime uses a deliberately different clipboard-only policy after text was
already typed. Future privacy, delivery-verification, or history changes must
currently be applied in three places and are easy to miss.

The state machines must remain in the controllers. Extract only the completed
result pipeline, with an explicit delivery policy preserving each mode's
existing behavior.

## Current state

`TailSlap/TypelessController.cs:469-477`:

```csharp
var finalText = await MaybeEnhanceTranscriptionAsync(transcriptionText, cfg)
    .ConfigureAwait(false);

if (!string.Equals(finalText, transcriptionText, StringComparison.Ordinal))
{
    await ApplyEnhancedTextAsync(finalText, cfg).ConfigureAwait(false);
}

PersistHistoryEntries(transcriptionText, finalText, cfg, durationMs);
```

Its `ApplyEnhancedTextAsync` uses `TextTyper.TypeAsync` only when AutoPaste is
enabled, otherwise it places text on the clipboard. Its
`PersistHistoryEntries` writes raw transcription history and, when changed,
the original/enhanced pair to refinement history.

`TailSlap/TranscriptionController.cs:224-239` performs the same stages. Its
`ApplyFinalTextAsync` distinguishes streamed results:

```csharp
if (!streamedResults)
{
    await _clipboardHelper
        .SetTextAndPasteAsync(finalText, cfg.Transcriber.AutoPaste)
        .ConfigureAwait(false);
    return;
}

if (!string.Equals(finalText, originalText, StringComparison.Ordinal))
{
    await _textTyper
        .TypeAsync(finalText, autoPaste: cfg.Transcriber.AutoPaste)
        .ConfigureAwait(false);
}
```

`TailSlap/RealtimeTranscriptionController.cs:1121-1189` enhances during cleanup.
If enhancement changes the transcript, it puts the improved draft on the
clipboard and tells the user to press Ctrl+V rather than rewriting text already
typed into the target:

```csharp
await _clip.SetTextAsync(sessionText).ConfigureAwait(false);
NotificationService.ShowInfo(
    "Enhanced transcript is on the clipboard (Ctrl+V to paste)."
);
_history.Append(rawSessionText, sessionText, cfg.Llm.Model);
...
_history.AppendTranscription(rawSessionText, durationMs);
```

`TranscriptionAutoEnhancer.MaybeEnhanceAsync` is already a shared static helper.
`Program.ConfigureServices` registers the controllers and their dependencies as
singletons.

## Chosen design

Add an injectable `ITranscriptionResultSink` /
`TranscriptionResultSink`. Do not create a generic callback bag. Use a small
request model with a named delivery policy:

```csharp
internal enum TranscriptionDeliveryPolicy
{
    DeliverFinalText,
    DeliverOnlyIfEnhanced,
    EnhancedToClipboardWithNotice,
}

internal sealed record TranscriptionResultRequest(
    string RawText,
    AppConfig Config,
    int DurationMs,
    TranscriptionDeliveryPolicy DeliveryPolicy,
    bool ResultsAlreadyStreamed = false
);

internal sealed record TranscriptionResult(string FinalText, bool WasEnhanced);

internal interface ITranscriptionResultSink
{
    Task<TranscriptionResult> ProcessAsync(
        TranscriptionResultRequest request,
        CancellationToken cancellationToken = default
    );
}
```

The sink owns enhancement, delivery after enhancement, and both history writes.
Controllers own recording/streaming state, empty-result detection, session
assembly, lifecycle notifications, and temp-file cleanup.

Map policies exactly:

- toggle: `DeliverFinalText`, pass `ResultsAlreadyStreamed = streamedResults`;
- typeless: `DeliverOnlyIfEnhanced`;
- realtime: `EnhancedToClipboardWithNotice`.

If plan 014 changed `TextTyper.TypeAsync` result handling, the sink must consume
and surface that result exactly as plan 014 specifies rather than reverting it.

## Scope

**In scope**:

- New `TailSlap/ITranscriptionResultSink.cs`
- New `TailSlap/TranscriptionResultSink.cs`
- `TailSlap/Program.cs`
- The three transcription controllers listed above
- New `TailSlap.Tests/TranscriptionResultSinkTests.cs`
- Constructor updates in existing controller tests
- `plans/README.md`

**Out of scope**:

- Recording, SSE/WebSocket parsing, controller state machines, hotkeys
- `TranscriptionAutoEnhancer` heuristics
- Refinement mode
- Changing realtime's clipboard-only enhanced-draft UX
- History storage format or paste reliability internals

## Git workflow

- Branch: `advisor/025-shared-result-sink`
- Commit example: `Refactor transcription result processing into shared sink`
- Do not push or open a PR unless instructed.

## Steps

### Step 1: Establish the characterization baseline

Confirm plan 020's controller and HTTP tests exist and pass:

```powershell
dotnet test -c Release --filter "FullyQualifiedName~TranscriptionController"
```

Also run the current full suite and record its pass count. If plan 020 has not
landed, STOP. Do not refactor three modes without characterization coverage.

### Step 2: Add the request, result, policy, and interface

Create `ITranscriptionResultSink.cs` in namespace `TailSlap`, matching the
repo's interface-per-file convention. Keep these types `internal` unless DI or
tests require public visibility under the existing
`InternalsVisibleTo("TailSlap.Tests")` setup.

Validate request arguments in the sink, not in each controller:

- reject null request/config;
- reject null/empty raw text (controllers should already filter it);
- clamp negative duration to zero or throw consistently with current callers;
- do not clone or reload config, use the validated snapshot supplied by the
  controller so one operation cannot observe multiple config versions.

**Verify**: `dotnet build -c Release`.

### Step 3: Implement `TranscriptionResultSink`

Inject:

- `IHistoryService`
- `ITextRefinerFactory`
- `ClipboardHelper`
- `IClipboardService`
- `TextTyper`

Processing order:

1. Call `TranscriptionAutoEnhancer.MaybeEnhanceAsync(raw, cfg, factory, ct)`.
2. Determine `wasEnhanced` with ordinal comparison.
3. Apply the selected policy:
   - `DeliverFinalText`: if results were not streamed, call
     `ClipboardHelper.SetTextAndPasteAsync(final, AutoPaste)`; if streamed,
     deliver only when enhanced using `TextTyper.TypeAsync`.
   - `DeliverOnlyIfEnhanced`: do nothing if unchanged; if changed, use
     `TextTyper.TypeAsync(final, true)` when AutoPaste is true, otherwise
     `ClipboardHelper.SetTextAndPasteAsync(final, false)`.
   - `EnhancedToClipboardWithNotice`: do nothing if unchanged; if changed,
     call `IClipboardService.SetTextAsync(final)` and show the existing
     `"Enhanced transcript is on the clipboard (Ctrl+V to paste)."` notice.
4. Append raw transcription history regardless of whether enhancement changed
   text.
5. If enhanced, append the raw/final/model refinement pair.
6. Return `TranscriptionResult`.

Preserve resilience:

- A failed enhancement falls back to raw text, as
  `TranscriptionAutoEnhancer` does today.
- History failures must be logged and must not turn a successful transcription
  into a controller failure.
- Delivery failure behavior must match plan 014. Do not claim success when its
  `TypeResult`/paste result reports failure.
- Log only lengths, models, durations, exception type names, and fingerprints
  where needed. Never log transcript text or free-form server content.

Avoid a broad catch around the entire method because that could suppress a
delivery failure before history persistence. Use narrow stage-specific catches.

**Verify**: `dotnet build -c Release`.

### Step 4: Register and inject the sink

In `Program.ConfigureServices`, before controller registrations:

```csharp
services.AddSingleton<ITranscriptionResultSink, TranscriptionResultSink>();
```

Replace `_history`, `_textRefinerFactory`, `_textTyper`, and clipboard fields in
each controller only where they were used exclusively for post-processing.
Inject `ITranscriptionResultSink` instead. Keep any dependency still used by
recording or streaming code.

Typeless has an internal test constructor. Update both constructors without
removing its custom `recordFunc` seam.

**Verify**: `dotnet build -c Release`.

### Step 5: Replace controller copies

- Toggle calls `ProcessAsync` after empty-transcription handling and supplies
  the streamed-results flag. Preserve the returned final text where subsequent
  logging depends on it.
- Typeless calls with `DeliverOnlyIfEnhanced`.
- Realtime cleanup calls with `EnhancedToClipboardWithNotice` and retains its
  final success notification and `OnStopped` ordering.

Delete the now-unused private `MaybeEnhanceTranscriptionAsync`,
`ApplyEnhancedTextAsync` / `ApplyFinalTextAsync`, and
`PersistHistoryEntries` methods. Do not move state-transition code into the
sink.

**Verify**:

```powershell
rg -n "PersistHistoryEntries|MaybeEnhanceTranscriptionAsync|ApplyEnhancedTextAsync|ApplyFinalTextAsync" TailSlap/*Controller.cs
```

Expected: no matches.

### Step 6: Add focused sink tests

Create `TranscriptionResultSinkTests.cs` following existing xUnit/Moq style.
Use mocks/fakes, never the real clipboard or `%APPDATA%` history. Cover:

1. unchanged toggle result, non-streamed: delivers raw text and writes only
   transcription history;
2. enhanced toggle result, non-streamed: delivers final text and writes both
   histories;
3. streamed unchanged toggle: no duplicate delivery;
4. streamed enhanced toggle: replacement delivery through `TextTyper`;
5. typeless unchanged: no final delivery, raw history written;
6. typeless enhanced with AutoPaste true and false: correct delivery path;
7. realtime enhanced: clipboard only, both histories;
8. realtime unchanged: no clipboard rewrite, raw history only;
9. history failure does not throw or suppress the other history attempt;
10. cancellation is passed to enhancement and does not corrupt history
    expectations.

If static notifications prevent direct assertion, assert the clipboard call and
leave notice text covered by a controller smoke test. Do not add a static
mocking package.

Update constructor calls in existing tests. Run focused and full suites.

## Verification

```powershell
dotnet build -c Release
dotnet test -c Release --filter "FullyQualifiedName~TranscriptionResultSink|FullyQualifiedName~TranscriptionController|FullyQualifiedName~TypelessController|FullyQualifiedName~RealtimeTranscriptionController"
dotnet test -c Release
```

All commands must exit 0.

## Done criteria

- [ ] One sink owns enhancement, result delivery policy, and both history writes
- [ ] All three controllers use it and retain their original state machines
- [ ] Realtime enhanced text remains clipboard-only with the existing notice
- [ ] No duplicate private result-processing helpers remain
- [ ] At least the ten sink cases above pass
- [ ] Full suite passes with no lower test count than the baseline
- [ ] Only in-scope files changed
- [ ] Plan 025 status updated in `plans/README.md`

## STOP conditions

- Plan 020 characterization tests are absent or failing.
- Plan 014's final delivery contract cannot be represented without changing the
  policy model above. Report the live contract and revise this plan first.
- Any controller dependency thought to be result-only is also used by its state
  machine. Keep that dependency; do not expand this refactor.
- Preserving realtime behavior would require moving cleanup synchronization
  (`_cleanupInProgress`, `finally`, `OnStopped`) into the sink.

## Maintenance notes

Review future post-processing changes in the sink first. New transcription
modes should select or deliberately add a named delivery policy rather than
copying controller methods. Keep policy names behavior-oriented, not
mode-oriented, so their semantics remain reviewable.
